using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FeatureLink.Models;

namespace FeatureLink.Services
{
    /// <summary>
    /// The stateless half of ArcGIS Online / Portal OAuth2 PKCE sign-in — PKCE derivation, the
    /// state nonce, authorize-URL construction and the two token-endpoint calls. Ported from the
    /// ATAK plugin's ArcGISAuthManager + OAuthHelper.
    ///
    /// Nothing here holds session state. <see cref="ArcGisAuthService"/> owns the tokens, the
    /// refresh gate and the loopback listener; splitting the protocol out keeps the parts that can
    /// be reasoned about (and unit-tested) free of the parts that need a browser and a socket.
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
    ///  • <see cref="ArcGisAuthService.GetTokenAsync"/> serialises refreshes behind a semaphore and
    ///    applies a 60-second expiry skew. ArcGIS rotates refresh tokens, so N concurrent refreshes
    ///    (this is called from six places, several on timers) invalidated each other and produced a
    ///    spontaneous sign-out under normal load.
    ///  • The authorize URL is asserted to be absolute https before <c>Process.Start</c>, since
    ///    the portal URL is restored from disk and <c>UseShellExecute=true</c> on a non-http
    ///    scheme invokes an arbitrary registered protocol handler.
    ///  • <see cref="ArcGisAuthService.Logout"/> revokes the refresh token at the portal instead of
    ///    only forgetting it locally.
    /// </summary>
    internal static class ArcGisOAuth
    {
        /// <summary>One client for every OAuth call, including <c>revokeToken</c> in
        /// <see cref="ArcGisAuthService"/> — a new <c>HttpClient</c> per call exhausts sockets.</summary>
        internal static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ArcGisHttp.EnsureTlsConfigured();
            // The token endpoint had no timeout at all (the 100 s default), so a refresh could
            // stall any calling path — including the PLI timer — for a minute and a half.
            return new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

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

        internal static async Task<OAuthTokens> ExchangeCodeAsync(string portalUrl, string clientId,
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

        internal static async Task<OAuthTokens> RefreshAccessTokenAsync(string portalUrl, string clientId,
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

        internal static async Task<OAuthTokens> PostForTokensAsync(string endpoint,
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

        internal static string NormalizePortal(string url) =>
            string.IsNullOrWhiteSpace(url) ? ArcGisFeatureService.DefaultPortalUrl : url.Trim().TrimEnd('/');
    }
}
