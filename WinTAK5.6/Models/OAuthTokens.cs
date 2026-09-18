using System;
using System.Collections.Generic;
using FeatureLink.Services;
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

    /// <summary>
    /// Every ArcGIS account persisted in <c>tokens.bin</c>, plus which one is currently active.
    ///
    /// This is the in-memory shape only — it is deliberately NOT what gets serialized. The blob on
    /// disk is a superset that also carries a flat <c>portalUrl</c>/<c>username</c>/<c>refreshToken</c>
    /// view of the active account, so a downgraded build still finds a session where it expects
    /// one; see <c>TokenBlobFormat</c>, which owns both directions of that translation.
    /// </summary>
    public sealed class StoredAccountSet
    {
        public List<StoredTokenState> Accounts { get; set; } = new List<StoredTokenState>();

        /// <summary>Key (portal|username) of the active account. May be null or name an account
        /// that is not in <see cref="Accounts"/> — <see cref="Active"/> resolves both cases.</summary>
        public string ActiveAccountKey { get; set; }

        /// <summary>The active account, or the first one when <see cref="ActiveAccountKey"/> is
        /// missing or stale. Never throws: a set with no accounts is "signed out", not an error.</summary>
        public StoredTokenState Active
        {
            get
            {
                if (Accounts == null || Accounts.Count == 0) return null;
                if (!string.IsNullOrEmpty(ActiveAccountKey))
                {
                    foreach (var account in Accounts)
                    {
                        if (account != null && string.Equals(
                                ArcGisAccountKey.Make(account.PortalUrl, account.Username),
                                ActiveAccountKey, StringComparison.Ordinal))
                            return account;
                    }
                }
                return Accounts[0];
            }
        }
    }
}
