using System;
using System.Collections.Generic;
using FeatureLink.Models;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// The on-disk shape of <c>tokens.bin</c>'s plaintext, and nothing else: no crypto, no file IO,
    /// no <see cref="SettingsStore"/>. That separation is what makes the format testable at all —
    /// DPAPI cannot run in the source-linked test project, so anything entangled with it is
    /// untestable by construction, and this format is the piece that must not be got wrong.
    ///
    /// THE DOWNGRADE GUARANTEE — why the document is a superset rather than a clean v2:
    /// <code>
    /// {"portalUrl":…,"username":…,"refreshToken":…,   &lt;- legacy flat view of the ACTIVE account
    ///  "v":2,"activeAccountKey":…,"accounts":[{portalUrl,username,refreshToken},…]}
    /// </code>
    /// A user who signs in on a multi-account build and then rolls back to an older FeatureLink
    /// (a .wpk downgrade is a normal recovery step in the field) would otherwise have their
    /// <c>StoredTokenState</c> deserialize to three nulls — a silent sign-out, with the operator
    /// told nothing and no way to tell it apart from a revoked session. The three legacy keys cost
    /// a few dozen bytes and make the old build keep working on the active account.
    ///
    /// The reverse direction is equally load-bearing: <see cref="Deserialize"/> branches on the
    /// PRESENCE of the "accounts" array, not on a typed deserialize. A v1 blob run through
    /// <c>SafeJson.Deserialize&lt;StoredAccountSet&gt;</c> yields a non-null object with a null
    /// list — indistinguishable from a corrupt v2 blob, which is exactly the case that must NOT be
    /// treated as "signed out".
    /// </summary>
    internal static class TokenBlobFormat
    {
        /// <summary>Format version of the multi-account document. Bumped only if the ACCOUNTS
        /// shape changes; the legacy flat keys are frozen forever, since their whole purpose is
        /// being readable by builds that predate this field.</summary>
        public const int CurrentVersion = 2;

        public static string Serialize(IReadOnlyList<StoredTokenState> accounts, string activeKey)
        {
            var array = new JArray();
            StoredTokenState active = null;

            if (accounts != null)
            {
                foreach (var account in accounts)
                {
                    if (account == null || string.IsNullOrEmpty(account.RefreshToken)) continue;
                    array.Add(ToJson(account));

                    if (active == null && !string.IsNullOrEmpty(activeKey)
                        && string.Equals(ArcGisAccountKey.Make(account.PortalUrl, account.Username),
                            activeKey, StringComparison.Ordinal))
                        active = account;
                }
            }

            // An unknown or absent activeKey resolves to the first account rather than writing a
            // pointer to nothing — a blob whose active account does not exist is a blob that reads
            // back as signed out.
            if (active == null && array.Count > 0)
                active = accounts != null ? FirstUsable(accounts) : null;

            var root = new JObject();
            if (active != null)
            {
                root["portalUrl"] = active.PortalUrl;
                root["username"] = active.Username;
                root["refreshToken"] = active.RefreshToken;
            }
            root["v"] = CurrentVersion;
            root["activeAccountKey"] =
                active == null ? null : ArcGisAccountKey.Make(active.PortalUrl, active.Username);
            root["accounts"] = array;

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Reads either format. Returns null for "no session on disk" — including for
        /// unparseable, truncated and non-object input, because the only recovery from a damaged
        /// token blob is signing in again, and throwing here would do that from inside a
        /// constructor on the UI thread.</summary>
        public static StoredAccountSet Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            // SafeJson, not JObject.Parse: TypeNameHandling.None and the depth cap are pinned
            // there, and this string comes off disk where another process may have edited it.
            JObject root = SafeJson.ParseObjectOrNull(json);
            if (root == null) return null;

            var accountsToken = root["accounts"] as JArray;
            if (accountsToken != null)
                return ReadV2(root, accountsToken);

            string refreshToken = (string)root["refreshToken"];
            if (!string.IsNullOrEmpty(refreshToken))
                return MigrateV1(root, refreshToken);

            // Neither shape: an empty document, or one written by a failed save. Signed out.
            return null;
        }

        private static StoredAccountSet ReadV2(JObject root, JArray accountsToken)
        {
            var set = new StoredAccountSet();
            foreach (var entry in accountsToken)
            {
                var obj = entry as JObject;
                if (obj == null) continue;

                string refreshToken = (string)obj["refreshToken"];
                // An account without a refresh token cannot produce a token and would show in the
                // switcher as a permanently broken "signed in" entry.
                if (string.IsNullOrEmpty(refreshToken)) continue;

                set.Accounts.Add(new StoredTokenState
                {
                    PortalUrl = (string)obj["portalUrl"],
                    Username = (string)obj["username"],
                    RefreshToken = refreshToken,
                });
            }

            if (set.Accounts.Count == 0) return null;

            string activeKey = (string)root["activeAccountKey"];
            // StoredAccountSet.Active already falls back to the first entry, but the key is
            // normalised away here so callers never persist a pointer to a missing account.
            set.ActiveAccountKey = KnownKeyOrFirst(set, activeKey);
            return set;
        }

        private static StoredAccountSet MigrateV1(JObject root, string refreshToken)
        {
            var account = new StoredTokenState
            {
                PortalUrl = (string)root["portalUrl"],
                Username = (string)root["username"],
                RefreshToken = refreshToken,
            };

            Log.Info("Migrated a single-account ArcGIS session to the multi-account store.");

            var set = new StoredAccountSet();
            set.Accounts.Add(account);
            set.ActiveAccountKey = ArcGisAccountKey.Make(account.PortalUrl, account.Username);
            return set;
        }

        private static string KnownKeyOrFirst(StoredAccountSet set, string activeKey)
        {
            if (!string.IsNullOrEmpty(activeKey))
            {
                foreach (var account in set.Accounts)
                {
                    if (string.Equals(ArcGisAccountKey.Make(account.PortalUrl, account.Username),
                            activeKey, StringComparison.Ordinal))
                        return activeKey;
                }
            }
            var first = set.Accounts[0];
            return ArcGisAccountKey.Make(first.PortalUrl, first.Username);
        }

        private static StoredTokenState FirstUsable(IReadOnlyList<StoredTokenState> accounts)
        {
            foreach (var account in accounts)
                if (account != null && !string.IsNullOrEmpty(account.RefreshToken)) return account;
            return null;
        }

        private static JObject ToJson(StoredTokenState account)
        {
            var obj = new JObject();
            obj["portalUrl"] = account.PortalUrl;
            obj["username"] = account.Username;
            obj["refreshToken"] = account.RefreshToken;
            return obj;
        }
    }
}
