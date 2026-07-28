using Newtonsoft.Json;

namespace FeatureLink.Models
{
    /// <summary>
    /// Deserialized response body of a successful /sharing/rest/oauth2/token call.
    /// Mirrors OAuthHelper.OAuthTokens from the ATAK plugin.
    /// </summary>
    public sealed class OAuthTokens
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("expires_in")]
        public long ExpiresInSeconds { get; set; } = 3600;

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("error_description")]
        public string ErrorDescription { get; set; }
    }

    /// <summary>
    /// What actually gets persisted (DPAPI-encrypted) between sessions — deliberately smaller
    /// than <see cref="OAuthTokens"/> (no access token: it's short-lived and re-derived from the
    /// refresh token on next launch, same as ArcGISAuthManager.getToken()'s silent-refresh path).
    /// </summary>
    public sealed class StoredTokenState
    {
        [JsonProperty("portalUrl")]
        public string PortalUrl { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("refreshToken")]
        public string RefreshToken { get; set; }
    }
}
