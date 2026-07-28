using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeatureLink.Models;
using Newtonsoft.Json;

namespace FeatureLink.Services
{
    /// <summary>
    /// ArcGIS Online / Portal OAuth2 PKCE sign-in, ported from the ATAK plugin's
    /// ArcGISAuthManager + OAuthHelper (com.atakmap.android.featurelink.arcgis).
    ///
    /// Redirect mechanism — DESIGN NOTE:
    /// The ATAK plugin captures the OAuth redirect via ATAK's own WebView
    /// (shouldOverrideUrlLoading intercepting the custom "featurelink://auth" scheme) because a
    /// mobile app can register a custom URL scheme / app link and ATAK hosts an embeddable
    /// WebView directly inside its own UI. WinTAK has no equivalent — there is no documented
    /// deep-link/custom-URI-scheme activation path for a WinTAK plugin anywhere in the SDK
    /// samples (ImageFolderSync, OpenAtlas, VideoStream), and WPF has no first-party embedded
    /// browser control in this project's .NET Framework 4.8 / no-extra-NuGet constraints.
    /// Instead this uses the standard desktop OAuth pattern: open the user's default system
    /// browser to the /sharing/rest/oauth2/authorize URL, and capture the redirect with a
    /// short-lived local loopback HTTP listener (http://localhost:{port}/callback/). This is the
    /// same approach Esri's own ArcGIS Pro / ArcGIS REST desktop samples use, and it means the
    /// ArcGIS OAuth application ("RXtGmClVuYd1Sp7d" client ID reused from the ATAK build, or a
    /// new client ID — see README) MUST have a "http://localhost/callback/" (or similar, with a
    /// wildcard/any-port allowance if the portal's app config supports it) redirect URI
    /// registered, in addition to (or instead of) "featurelink://auth".
    /// </summary>
    public sealed class ArcGisAuthService
    {
        // Reused from the ATAK plugin's registered ArcGIS OAuth application. This will only work
        // for sign-in if that application's redirect URI allowlist is updated to also include a
        // loopback URI (see class remarks above) — TODO: confirm with the ArcGIS org admin
        // whether to extend the existing app or mint a fresh "FeatureLink for WinTAK" OAuth app.
        private const string ClientId = "RXtGmClVuYd1Sp7d";

        private const int MinLoopbackPort = 51000;
        private const int MaxLoopbackPort = 51050;
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new HttpClient();

        private string _accessToken;
        private DateTime _accessTokenExpiryUtc;
        private string _username;
        private string _refreshToken;
        private string _portalUrl = "https://www.arcgis.com";

        public bool IsAuthenticated => !string.IsNullOrEmpty(_username);
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
        /// Runs the full PKCE sign-in flow: generates verifier/challenge, opens the system
        /// browser to the portal's authorize endpoint, waits for the loopback redirect carrying
        /// the auth code, then exchanges it for tokens. Throws on failure/timeout/cancellation —
        /// callers should catch and surface <c>ex.Message</c> to the status text, same as
        /// ArcGISAuthManager.FailureCallback did.
        /// </summary>
        public async Task SignInAsync(string portalUrl, CancellationToken cancellationToken = default(CancellationToken))
        {
            _portalUrl = NormalizePortal(portalUrl);

            var (codeVerifier, codeChallenge) = GeneratePkce();
            int port = FindAvailableLoopbackPort();
            string redirectUri = $"http://localhost:{port}/callback/";

            string authUrl = BuildAuthUrl(_portalUrl, ClientId, codeChallenge, redirectUri);

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(redirectUri);
                listener.Start();

                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

                var contextTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(contextTask, Task.Delay(SignInTimeout, cancellationToken))
                    .ConfigureAwait(false);
                if (completed != contextTask)
                    throw new TimeoutException("Sign-in timed out waiting for the browser redirect.");

                var context = await contextTask.ConfigureAwait(false);
                var query = context.Request.QueryString;
                string code = query["code"];
                string error = query["error"];
                string errorDescription = query["error_description"];

                WriteCallbackResponse(context.Response, error == null);
                listener.Stop();

                if (error != null)
                    throw new InvalidOperationException($"OAuth error: {errorDescription ?? error}");
                if (string.IsNullOrEmpty(code))
                    throw new InvalidOperationException("No authorization code received.");

                var tokens = await ExchangeCodeAsync(_portalUrl, ClientId, code, codeVerifier, redirectUri)
                    .ConfigureAwait(false);
                ApplyTokens(tokens);
            }
        }

        /// <summary>Returns a valid access token, silently refreshing via the stored refresh
        /// token if the cached one has expired. Returns null if refresh also fails (caller
        /// should prompt for a fresh sign-in) — mirrors ArcGISAuthManager.getToken().</summary>
        public async Task<string> GetTokenAsync()
        {
            if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiryUtc)
                return _accessToken;

            if (string.IsNullOrEmpty(_refreshToken))
                return null;

            try
            {
                var tokens = await RefreshAccessTokenAsync(_portalUrl, ClientId, _refreshToken)
                    .ConfigureAwait(false);
                if (tokens == null) return null;
                ApplyTokens(tokens);
                return _accessToken;
            }
            catch
            {
                return null;
            }
        }

        public void Logout()
        {
            _accessToken = null;
            _accessTokenExpiryUtc = DateTime.MinValue;
            _username = null;
            _refreshToken = null;
            SettingsStore.ClearTokenState();
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
        // PKCE + REST — ported from OAuthHelper.java
        // -------------------------------------------------------------------------

        private static (string verifier, string challenge) GeneratePkce()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            string verifier = Base64UrlEncode(bytes);

            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
                string challenge = Base64UrlEncode(digest);
                return (verifier, challenge);
            }
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string BuildAuthUrl(string portalUrl, string clientId, string codeChallenge, string redirectUri)
        {
            return NormalizePortal(portalUrl)
                + "/sharing/rest/oauth2/authorize"
                + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
                + "&code_challenge_method=S256";
        }

        private static async Task<OAuthTokens> ExchangeCodeAsync(string portalUrl, string clientId,
            string code, string codeVerifier, string redirectUri)
        {
            string endpoint = NormalizePortal(portalUrl) + "/sharing/rest/oauth2/token";
            var body = new System.Collections.Generic.Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            };
            return await PostForTokensAsync(endpoint, body).ConfigureAwait(false);
        }

        private static async Task<OAuthTokens> RefreshAccessTokenAsync(string portalUrl, string clientId,
            string refreshToken)
        {
            string endpoint = NormalizePortal(portalUrl) + "/sharing/rest/oauth2/token";
            var body = new System.Collections.Generic.Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
            };
            return await PostForTokensAsync(endpoint, body).ConfigureAwait(false);
        }

        private static async Task<OAuthTokens> PostForTokensAsync(string endpoint,
            System.Collections.Generic.Dictionary<string, string> form)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (var response = await Http.PostAsync(endpoint, content).ConfigureAwait(false))
            {
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var tokens = JsonConvert.DeserializeObject<OAuthTokens>(json);
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
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static int FindAvailableLoopbackPort()
        {
            for (int port = MinLoopbackPort; port <= MaxLoopbackPort; port++)
            {
                try
                {
                    var probe = new HttpListener();
                    probe.Prefixes.Add($"http://localhost:{port}/callback/");
                    probe.Start();
                    probe.Stop();
                    return port;
                }
                catch (HttpListenerException)
                {
                    // Port in use — try the next one.
                }
            }
            throw new InvalidOperationException("No available loopback port for OAuth redirect capture.");
        }

        private static string NormalizePortal(string url) =>
            string.IsNullOrWhiteSpace(url) ? "https://www.arcgis.com" : url.TrimEnd('/');
    }
}
