using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeatureLink.Models;

namespace FeatureLink.Services
{
    /// <summary>
    /// ArcGIS Online / Portal OAuth2 PKCE sign-in, ported from the ATAK plugin's
    /// ArcGISAuthManager + OAuthHelper.
    ///
    /// Redirect mechanism — DESIGN NOTE:
    /// The ATAK plugin captures the OAuth redirect via ATAK's own WebView because a mobile app can
    /// register a custom URL scheme and ATAK hosts an embeddable WebView. WinTAK has no
    /// equivalent, so this uses the standard desktop OAuth pattern: open the system browser to the
    /// /sharing/rest/oauth2/authorize URL and capture the redirect with a short-lived loopback
    /// HTTP listener. The ArcGIS OAuth application MUST have a loopback redirect URI registered.
    ///
    /// Audit remediation applied here:
    ///  • <b>C-09</b> — a cryptographically random <c>state</c> nonce is generated per sign-in,
    ///    sent on the authorize URL and <b>required to match</b> on the callback. Without it, any
    ///    local process (or a browser following a crafted link) could hit
    ///    <c>http://localhost:5100x/callback/?code=…</c> and inject an attacker-controlled
    ///    authorization code, binding the plugin to the attacker's ArcGIS identity — textbook
    ///    authorization-code injection. The nonce lives in memory only, is cleared on completion,
    ///    and is never persisted or logged.
    ///  • The listener loops until a request actually carrying <c>code</c>/<c>error</c> arrives,
    ///    so a favicon or probe request no longer consumes the single-shot capture.
    ///  • <see cref="GetTokenAsync"/> serialises refreshes behind a semaphore and applies a
    ///    60-second expiry skew. ArcGIS rotates refresh tokens, so N concurrent refreshes (this is
    ///    called from six places, several on timers) invalidated each other and produced a
    ///    spontaneous sign-out under normal load.
    ///  • The authorize URL is asserted to be absolute https before <c>Process.Start</c>, since
    ///    the portal URL is restored from disk and <c>UseShellExecute=true</c> on a non-http
    ///    scheme invokes an arbitrary registered protocol handler.
    ///  • <see cref="Logout"/> revokes the refresh token at the portal instead of only forgetting
    ///    it locally.
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

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ArcGisHttp.EnsureTlsConfigured();
            // The token endpoint had no timeout at all (the 100 s default), so a refresh could
            // stall any calling path — including the PLI timer — for a minute and a half.
            return new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTime _accessTokenExpiryUtc;
        private string _username;
        private string _refreshToken;
        private string _portalUrl = ArcGisFeatureService.DefaultPortalUrl;

        /// <summary>The pending OAuth <c>state</c> nonce. In memory only, one at a time, cleared
        /// as soon as a callback is adjudicated.</summary>
        private string _pendingState;

        /// <summary>"Authenticated" now means "holds a credential that can produce a token", not
        /// merely "a username was once restored from disk" — the UI used to show a green
        /// "Signed in as X" for a permanently revoked session while every operation failed.</summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_refreshToken);
        public string Username => _username;
        public string PortalUrl => _portalUrl;

        public ArcGisAuthService()
        {
            var stored = SettingsStore.LoadTokenState();
            if (stored != null && !string.IsNullOrEmpty(stored.RefreshToken))
            {
                _username = stored.Username;
                _refreshToken = stored.RefreshToken;
                _portalUrl = string.IsNullOrEmpty(stored.PortalUrl) ? _portalUrl : stored.PortalUrl;
            }
        }

        /// <summary>
        /// Runs the full PKCE sign-in flow. Throws on failure/timeout/cancellation — callers catch
        /// and surface <c>ex.Message</c>.
        /// </summary>
        public async Task SignInAsync(string portalUrl, CancellationToken cancellationToken = default(CancellationToken))
        {
            _portalUrl = NormalizePortal(portalUrl);

            var pkce = GeneratePkce();
            string codeVerifier = pkce.verifier;
            string state = GenerateStateNonce();
            _pendingState = state;

            int port = FindAvailableLoopbackPort();
            string redirectUri = $"http://localhost:{port}/callback/";
            string authUrl = BuildAuthUrl(_portalUrl, ClientId, pkce.challenge, redirectUri, state);
            AssertBrowsableHttpsUrl(authUrl);

            try
            {
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add(redirectUri);
                    listener.Start();

                    Log.Info("Opening the system browser for ArcGIS sign-in.");
                    Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

                    var callback = await AwaitCallbackAsync(listener, cancellationToken).ConfigureAwait(false);

                    // C-09: reject anything whose state does not match the pending request. A
                    // missing state is also a rejection — an injected callback simply omits it.
                    if (!FixedTimeEquals(callback.State, state))
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

                    var tokens = await ExchangeCodeAsync(_portalUrl, ClientId, callback.Code, codeVerifier,
                        redirectUri, cancellationToken).ConfigureAwait(false);
                    ApplyTokens(tokens);
                    Log.Info("ArcGIS sign-in completed.");
                }
            }
            finally
            {
                _pendingState = null;
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

                var tokens = await RefreshAccessTokenAsync(_portalUrl, ClientId, _refreshToken, cancellationToken)
                    .ConfigureAwait(false);
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
                    SettingsStore.ClearTokenState();
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

            _accessToken = null;
            _accessTokenExpiryUtc = DateTime.MinValue;
            _username = null;
            _refreshToken = null;
            SettingsStore.ClearTokenState();

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
                using (var response = await Http.PostAsync(
                    NormalizePortal(portalUrl) + "/sharing/rest/oauth2/revokeToken", content).ConfigureAwait(false))
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

            SettingsStore.SaveTokenState(new StoredTokenState
            {
                PortalUrl = _portalUrl,
                Username = _username,
                RefreshToken = _refreshToken,
            });
        }

        // -------------------------------------------------------------------------
        // PKCE + state + REST
        // -------------------------------------------------------------------------

        internal static (string verifier, string challenge) GeneratePkce()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            // RFC 7636 §4.1: the verifier is an ASCII string drawn from the unreserved set;
            // base64url of 32 random bytes satisfies that, which is why ASCII encoding below is
            // correct rather than incidental. Changing the verifier alphabet would break it.
            string verifier = Base64UrlEncode(bytes);

            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
                return (verifier, Base64UrlEncode(digest));
            }
        }

        /// <summary>Computes the S256 code challenge for a given verifier. Exposed so the test
        /// suite can assert against RFC 7636 Appendix B's published vector.</summary>
        internal static string ComputeCodeChallenge(string verifier)
        {
            using (var sha256 = SHA256.Create())
                return Base64UrlEncode(sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        /// <summary>256 bits of CSPRNG entropy, base64url-encoded (C-09).</summary>
        internal static string GenerateStateNonce()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        /// <summary>Length-and-content comparison that does not short-circuit on the first
        /// differing character. Overkill for a nonce compared once, but free.</summary>
        internal static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        internal static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static string BuildAuthUrl(string portalUrl, string clientId, string codeChallenge,
            string redirectUri, string state)
        {
            return NormalizePortal(portalUrl)
                + "/sharing/rest/oauth2/authorize"
                + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state);
        }

        /// <summary>The portal URL is restored from <c>settings.xml</c>/<c>tokens.bin</c>, so a
        /// tampered file could otherwise turn <c>Process.Start(UseShellExecute=true)</c> into an
        /// arbitrary protocol-handler invocation (<c>ms-settings:</c>, a UNC path, <c>file:</c>).</summary>
        internal static void AssertBrowsableHttpsUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Refusing to open a non-https sign-in URL. Check the configured ArcGIS portal URL.");
        }

        private static async Task<OAuthTokens> ExchangeCodeAsync(string portalUrl, string clientId,
            string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
        {
            var body = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            };
            return await PostForTokensAsync(NormalizePortal(portalUrl) + "/sharing/rest/oauth2/token",
                body, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<OAuthTokens> RefreshAccessTokenAsync(string portalUrl, string clientId,
            string refreshToken, CancellationToken cancellationToken)
        {
            var body = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
            };
            return await PostForTokensAsync(NormalizePortal(portalUrl) + "/sharing/rest/oauth2/token",
                body, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<OAuthTokens> PostForTokensAsync(string endpoint,
            Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (var response = await Http.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false))
            {
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // A 500 with an HTML body used to make the deserializer return null or throw a
                    // JsonReaderException, surfacing as an opaque message with no status code.
                    throw new InvalidOperationException(
                        $"The ArcGIS token endpoint returned HTTP {(int)response.StatusCode} "
                        + $"({response.ReasonPhrase}).");
                }

                OAuthTokens tokens;
                try
                {
                    tokens = SafeJson.Deserialize<OAuthTokens>(json);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "The ArcGIS token endpoint returned a response that was not JSON.", ex);
                }

                if (tokens == null) throw new InvalidOperationException("Token exchange returned no data");
                if (!string.IsNullOrEmpty(tokens.Error))
                    throw new InvalidOperationException(
                        "OAuth error: " + (tokens.ErrorDescription ?? tokens.Error));
                return tokens;
            }
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

        internal static string NormalizePortal(string url) =>
            string.IsNullOrWhiteSpace(url) ? ArcGisFeatureService.DefaultPortalUrl : url.Trim().TrimEnd('/');
    }
}
