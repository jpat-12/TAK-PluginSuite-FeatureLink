using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeatureLink.Models;

namespace FeatureLink.Services
{
    /// <summary>
    /// The stateful half of ArcGIS sign-in: the loopback redirect listener, the tokens for the
    /// signed-in account, and the refresh gate that keeps concurrent callers from invalidating
    /// each other's rotated refresh token.
    ///
    /// The protocol itself — PKCE, the state nonce, the authorize URL and the token-endpoint
    /// calls — lives in <see cref="ArcGisOAuth"/>, which also carries the design note explaining
    /// why the loopback redirect exists at all on WinTAK. Read that first.
    /// </summary>
    public sealed class ArcGisAuthService
    {
        // Reused from the ATAK plugin's registered ArcGIS OAuth application. Overridable via the
        // FEATURELINK_ARCGIS_CLIENT_ID environment variable so a deployment can point at its own
        // app registration without a code release — see OWNER DECISIONS in the remediation report.
        private const string DefaultClientId = "RXtGmClVuYd1Sp7d";

        private static readonly string ClientId =
            Environment.GetEnvironmentVariable("FEATURELINK_ARCGIS_CLIENT_ID") ?? DefaultClientId;

        private const int MinLoopbackPort = 51000;
        private const int MaxLoopbackPort = 51050;
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Refresh this far before the token actually expires. Without a margin a token
        /// with 200 ms of life left passed the freshness check and then 401'd mid-request.</summary>
        private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTime _accessTokenExpiryUtc;
        private string _username;
        private string _refreshToken;
        private string _portalUrl = ArcGisFeatureService.DefaultPortalUrl;

        /// <summary>"Authenticated" now means "holds a credential that can produce a token", not
        /// merely "a username was once restored from disk" — the UI used to show a green
        /// "Signed in as X" for a permanently revoked session while every operation failed.</summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_refreshToken);
        public string Username => _username;
        public string PortalUrl => _portalUrl;

        public ArcGisAuthService()
        {
            // Only the ACTIVE account is restored into this session's fields. The rest stay on
            // disk untouched until something explicitly switches accounts — this type still models
            // exactly one signed-in identity.
            var stored = SettingsStore.LoadAccountSet();
            var active = stored == null ? null : stored.Active;
            if (active != null && !string.IsNullOrEmpty(active.RefreshToken))
            {
                _username = active.Username;
                _refreshToken = active.RefreshToken;
                _portalUrl = string.IsNullOrEmpty(active.PortalUrl) ? _portalUrl : active.PortalUrl;
            }
        }

        /// <summary>
        /// Runs the full PKCE sign-in flow. Throws on failure/timeout/cancellation — callers catch
        /// and surface <c>ex.Message</c>.
        /// </summary>
        public async Task SignInAsync(string portalUrl, CancellationToken cancellationToken = default(CancellationToken))
        {
            _portalUrl = ArcGisOAuth.NormalizePortal(portalUrl);

            var pkce = ArcGisOAuth.GeneratePkce();
            string codeVerifier = pkce.verifier;
            // Deliberately a local, not a field: two sign-in flows started at once would otherwise
            // adjudicate each other's callback, and the second one to start would hand the first
            // one's listener a state it is bound to reject.
            string state = ArcGisOAuth.GenerateStateNonce();

            int port = FindAvailableLoopbackPort();
            string redirectUri = $"http://localhost:{port}/callback/";
            string authUrl = ArcGisOAuth.BuildAuthUrl(_portalUrl, ClientId, pkce.challenge, redirectUri, state);
            ArcGisOAuth.AssertBrowsableHttpsUrl(authUrl);

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(redirectUri);
                listener.Start();

                Log.Info("Opening the system browser for ArcGIS sign-in.");
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

                var callback = await AwaitCallbackAsync(listener, cancellationToken).ConfigureAwait(false);

                // C-09: reject anything whose state does not match the pending request. A
                // missing state is also a rejection — an injected callback simply omits it.
                if (!ArcGisOAuth.FixedTimeEquals(callback.State, state))
                {
                    Log.Error("Rejected an OAuth callback whose state did not match the pending request.");
                    throw new InvalidOperationException(
                        "The sign-in response did not match this sign-in request and was rejected. "
                        + "Start sign-in again, and do not follow FeatureLink sign-in links from other applications.");
                }

                if (callback.Error != null)
                    throw new InvalidOperationException(
                        $"OAuth error: {callback.ErrorDescription ?? callback.Error}");
                if (string.IsNullOrEmpty(callback.Code))
                    throw new InvalidOperationException("No authorization code received.");

                var tokens = await ArcGisOAuth.ExchangeCodeAsync(_portalUrl, ClientId, callback.Code,
                    codeVerifier, redirectUri, cancellationToken).ConfigureAwait(false);
                ApplyTokens(tokens);
                Log.Info("ArcGIS sign-in completed.");
            }
        }

        private sealed class CallbackResult
        {
            public string Code;
            public string State;
            public string Error;
            public string ErrorDescription;
        }

        /// <summary>Loops until a request carrying <c>code</c> or <c>error</c> arrives. The old
        /// code took the <b>first</b> request unconditionally, so a browser's favicon or preflight
        /// probe consumed the single-shot listener and the real callback then failed.</summary>
        private static async Task<CallbackResult> AwaitCallbackAsync(HttpListener listener,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.Add(SignInTimeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Sign-in timed out waiting for the browser redirect.");

                var contextTask = listener.GetContextAsync();
                using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var delayTask = Task.Delay(remaining, delayCts.Token);
                    var completed = await Task.WhenAny(contextTask, delayTask).ConfigureAwait(false);
                    if (completed != contextTask)
                    {
                        // Observe the abandoned listener task so disposing the listener does not
                        // surface an unobserved task exception, and distinguish cancel from timeout.
                        ObserveAndDiscard(contextTask);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("Sign-in timed out waiting for the browser redirect.");
                    }
                    delayCts.Cancel();
                    ObserveAndDiscard(delayTask);
                }

                var context = await contextTask.ConfigureAwait(false);
                var query = context.Request.QueryString;
                string code = query["code"];
                string error = query["error"];

                if (code == null && error == null)
                {
                    // A probe/favicon request — answer it and keep waiting.
                    WriteCallbackResponse(context.Response, false);
                    continue;
                }

                WriteCallbackResponse(context.Response, error == null);
                return new CallbackResult
                {
                    Code = code,
                    State = query["state"],
                    Error = error,
                    ErrorDescription = query["error_description"],
                };
            }
        }

        private static void ObserveAndDiscard(Task task)
        {
            task?.ContinueWith(t => { var ignored = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        /// <summary>Returns a valid access token, refreshing via the stored refresh token if the
        /// cached one is expired or about to be. Returns null if refresh fails.</summary>
        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_accessToken != null && DateTime.UtcNow.Add(ExpirySkew) < _accessTokenExpiryUtc)
                return _accessToken;
            if (string.IsNullOrEmpty(_refreshToken))
                return null;

            // Serialise refreshes: ArcGIS rotates refresh tokens, so concurrent refresh POSTs
            // invalidate each other and whichever finishes last wins — a spontaneous sign-out.
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-checked: another caller may have refreshed while we waited.
                if (_accessToken != null && DateTime.UtcNow.Add(ExpirySkew) < _accessTokenExpiryUtc)
                    return _accessToken;
                if (string.IsNullOrEmpty(_refreshToken)) return null;

                var tokens = await ArcGisOAuth.RefreshAccessTokenAsync(_portalUrl, ClientId, _refreshToken,
                    cancellationToken).ConfigureAwait(false);
                if (tokens == null) return null;
                ApplyTokens(tokens);
                return _accessToken;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                // Transient network failure — the stored refresh token is probably still good, so
                // do not treat this as a revocation. These two cases used to be indistinguishable
                // behind a single `catch { return null; }`.
                Log.Warn("Token refresh failed (transient network error): " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Token refresh failed — the ArcGIS session may have been revoked.", ex);
                if (IsInvalidGrant(ex))
                {
                    _refreshToken = null;
                    _accessToken = null;
                    ForgetActiveAccount();
                }
                return null;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private static bool IsInvalidGrant(Exception ex) =>
            ex?.Message != null && ex.Message.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0;

        public void Logout()
        {
            string refreshToken = _refreshToken;
            string portal = _portalUrl;

            // Forget the stored account BEFORE the fields are cleared — the key is derived from
            // them, and a cleared username keys a different (non-existent) account.
            ForgetActiveAccount();

            _accessToken = null;
            _accessTokenExpiryUtc = DateTime.MinValue;
            _username = null;
            _refreshToken = null;

            // Revoke at the portal too: a long-lived credential used to stay valid after the user
            // believed they had signed out. Best-effort and off the UI path.
            if (!string.IsNullOrEmpty(refreshToken))
                Task.Run(() => RevokeAsync(portal, refreshToken));
        }

        private static async Task RevokeAsync(string portalUrl, string refreshToken)
        {
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["auth_token"] = refreshToken,
                    ["token_type_hint"] = "refresh_token",
                    ["f"] = "json",
                };
                using (var content = new FormUrlEncodedContent(form))
                using (var response = await ArcGisOAuth.Http.PostAsync(
                    ArcGisOAuth.NormalizePortal(portalUrl) + "/sharing/rest/oauth2/revokeToken",
                    content).ConfigureAwait(false))
                {
                    Log.Info("Refresh-token revocation returned HTTP " + (int)response.StatusCode + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not revoke the refresh token at the portal: " + ex.Message);
            }
        }

        private void ApplyTokens(OAuthTokens tokens)
        {
            _accessToken = tokens.AccessToken;
            _accessTokenExpiryUtc = DateTime.UtcNow.AddSeconds(tokens.ExpiresInSeconds);
            if (!string.IsNullOrEmpty(tokens.Username)) _username = tokens.Username;
            if (!string.IsNullOrEmpty(tokens.RefreshToken)) _refreshToken = tokens.RefreshToken;

            PersistActiveAccount();
        }

        /// <summary>Writes this session's account into the stored set as the active one, leaving
        /// every other account's refresh token alone. A plain overwrite here would delete the
        /// other accounts on every token refresh — and a refresh happens on a timer.</summary>
        private void PersistActiveAccount()
        {
            string key = ArcGisAccountKey.Make(_portalUrl, _username);
            var accounts = LoadAccountsExcept(key);
            accounts.Insert(0, new StoredTokenState
            {
                PortalUrl = _portalUrl,
                Username = _username,
                RefreshToken = _refreshToken,
            });
            SettingsStore.SaveAccountSet(accounts, key);
        }

        /// <summary>Drops this session's account from the stored set. The blob is deleted only
        /// when it was the last one — otherwise the remaining accounts must survive, and the first
        /// of them becomes active.</summary>
        private void ForgetActiveAccount()
        {
            var remaining = LoadAccountsExcept(ArcGisAccountKey.Make(_portalUrl, _username));
            if (remaining.Count == 0)
            {
                SettingsStore.ClearTokenState();
                return;
            }
            var first = remaining[0];
            SettingsStore.SaveAccountSet(remaining,
                ArcGisAccountKey.Make(first.PortalUrl, first.Username));
        }

        private static List<StoredTokenState> LoadAccountsExcept(string key)
        {
            var result = new List<StoredTokenState>();
            var stored = SettingsStore.LoadAccountSet();
            if (stored == null || stored.Accounts == null) return result;

            foreach (var account in stored.Accounts)
            {
                if (account == null) continue;
                if (string.Equals(ArcGisAccountKey.Make(account.PortalUrl, account.Username), key,
                        StringComparison.Ordinal))
                    continue;
                result.Add(account);
            }
            return result;
        }

        private static void WriteCallbackResponse(HttpListenerResponse response, bool success)
        {
            string html = success
                ? "<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'>"
                  + "<h2>Signed in to FeatureLink</h2><p>You can close this browser tab and return to WinTAK.</p></body></html>"
                : "<html><body style='font-family:sans-serif;text-align:center;margin-top:80px'>"
                  + "<h2>Sign-in failed</h2><p>You can close this browser tab and return to WinTAK.</p></body></html>";
            try
            {
                var buffer = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html";
                response.ContentLength64 = buffer.Length;
                using (var stream = response.OutputStream)
                    stream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                // A browser that disconnected first throws HttpListenerException; that must not
                // escape SignInAsync after the code has already been received.
                Log.Warn("Could not write the OAuth callback page: " + ex.Message);
            }
        }

        private static int FindAvailableLoopbackPort()
        {
            for (int port = MinLoopbackPort; port <= MaxLoopbackPort; port++)
            {
                HttpListener probe = null;
                try
                {
                    probe = new HttpListener();
                    probe.Prefixes.Add($"http://localhost:{port}/callback/");
                    probe.Start();
                    probe.Stop();
                    return port;
                }
                catch (HttpListenerException)
                {
                    // Port in use, or no URL ACL for this user — try the next one.
                }
                catch (ArgumentException)
                {
                    // Malformed prefix — should be impossible, but must not escape the loop.
                }
                finally
                {
                    // Close() releases the native HTTP.SYS registration; Stop() alone leaked one
                    // per probe, up to 51 per sign-in attempt.
                    try { probe?.Close(); } catch { }
                }
            }
            throw new InvalidOperationException(
                "No available loopback port for OAuth redirect capture. On a locked-down Windows "
                + "image this usually means an HTTP URL reservation is required — see the README.");
        }
    }
}
