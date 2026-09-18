using System.Collections.Generic;
using FeatureLink.Models;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// The token blob is the one piece of FeatureLink state a user cannot recreate by re-entering
    /// it — they have to go back through a browser sign-in, per account, and on a portal that may
    /// not be reachable from where they are standing. Every failure mode here therefore has to
    /// degrade to "signed out", never to a throw out of a constructor, and an upgrade or a
    /// downgrade must not lose a refresh token.
    ///
    /// DPAPI and file IO are deliberately not in scope: <c>TokenBlobFormat</c> is string-in /
    /// string-out precisely so this suite can exist at all in the source-linked test project.
    /// </summary>
    public class TokenBlobFormatTests
    {
        private static StoredTokenState Account(string portal, string user, string refresh) =>
            new StoredTokenState { PortalUrl = portal, Username = user, RefreshToken = refresh };

        private const string Agol = "https://www.arcgis.com";
        private const string Portal = "https://portal.example.mil/arcgis";

        // -----------------------------------------------------------------------------
        // v1 -> v2 migration
        // -----------------------------------------------------------------------------

        [Fact]
        public void V1Blob_MigratesToASingleActiveAccount_WithTheRefreshTokenIntact()
        {
            // Exactly what a pre-multi-account build wrote: a flat triple, no "accounts", no "v".
            string v1 = "{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\",\"refreshToken\":\"RT-1\"}";

            var set = TokenBlobFormat.Deserialize(v1);

            Assert.NotNull(set);
            Assert.Single(set.Accounts);
            Assert.Equal("RT-1", set.Active.RefreshToken);
            Assert.Equal("jdoe", set.Active.Username);
            Assert.Equal(Agol, set.Active.PortalUrl);
            Assert.Equal(ArcGisAccountKey.Make(Agol, "jdoe"), set.ActiveAccountKey);
        }

        [Fact]
        public void V1BlobWithNoRefreshToken_IsSignedOut_NotAnAccountThatCanNeverGetAToken()
        {
            Assert.Null(TokenBlobFormat.Deserialize(
                "{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\"}"));
        }

        [Fact]
        public void AnEmptyAccountsArrayIsNotMistakenForAV1Blob()
        {
            // The reason Deserialize branches on the PRESENCE of "accounts": this document has a
            // legacy refreshToken too, but it is v2 and its account list is authoritative.
            string json = "{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\","
                + "\"refreshToken\":\"RT-1\",\"v\":2,\"accounts\":[]}";

            Assert.Null(TokenBlobFormat.Deserialize(json));
        }

        // -----------------------------------------------------------------------------
        // v2 round-trip
        // -----------------------------------------------------------------------------

        [Fact]
        public void V2RoundTrip_PreservesEveryAccount_ItsOrder_AndTheActiveKey()
        {
            var accounts = new List<StoredTokenState>
            {
                Account(Agol, "jdoe", "RT-A"),
                Account(Portal, "jdoe", "RT-B"),
                Account(Agol, "second.user", "RT-C"),
            };
            string activeKey = ArcGisAccountKey.Make(Portal, "jdoe");

            var set = TokenBlobFormat.Deserialize(TokenBlobFormat.Serialize(accounts, activeKey));

            Assert.Equal(3, set.Accounts.Count);
            Assert.Equal(new[] { "RT-A", "RT-B", "RT-C" },
                set.Accounts.ConvertAll(a => a.RefreshToken).ToArray());
            Assert.Equal(activeKey, set.ActiveAccountKey);
            Assert.Equal("RT-B", set.Active.RefreshToken);
        }

        [Fact]
        public void AccountsWithoutARefreshTokenAreDroppedOnWrite()
        {
            // Such an entry would show in the account switcher as signed in and fail every call.
            var accounts = new List<StoredTokenState>
            {
                Account(Agol, "jdoe", "RT-A"),
                Account(Agol, "ghost", null),
                null,
            };

            var set = TokenBlobFormat.Deserialize(TokenBlobFormat.Serialize(accounts, null));

            Assert.Single(set.Accounts);
            Assert.Equal("jdoe", set.Accounts[0].Username);
        }

        [Fact]
        public void SerializingNoAccountsProducesADocumentThatReadsBackAsSignedOut()
        {
            Assert.Null(TokenBlobFormat.Deserialize(
                TokenBlobFormat.Serialize(new List<StoredTokenState>(), null)));
            Assert.Null(TokenBlobFormat.Deserialize(TokenBlobFormat.Serialize(null, null)));
        }

        // -----------------------------------------------------------------------------
        // THE DOWNGRADE GUARANTEE
        // -----------------------------------------------------------------------------

        [Fact]
        public void V2Document_StillCarriesTheFlatLegacyTripleForTheActiveAccount()
        {
            // The whole reason the document is a superset. A user who rolls back to an older
            // FeatureLink .wpk — a normal field recovery step — must still be signed in, so the
            // three keys StoredTokenState looks for have to be present at the ROOT and describe
            // the ACTIVE account, not merely the first one. Asserted on raw JSON on purpose:
            // round-tripping through our own reader would not catch losing them.
            var accounts = new List<StoredTokenState>
            {
                Account(Agol, "jdoe", "RT-A"),
                Account(Portal, "other.user", "RT-B"),
            };
            string activeKey = ArcGisAccountKey.Make(Portal, "other.user");

            var raw = JObject.Parse(TokenBlobFormat.Serialize(accounts, activeKey));

            Assert.Equal(Portal, (string)raw["portalUrl"]);
            Assert.Equal("other.user", (string)raw["username"]);
            Assert.Equal("RT-B", (string)raw["refreshToken"]);
            Assert.Equal(2, (int)raw["v"]);
            Assert.Equal(activeKey, (string)raw["activeAccountKey"]);
            Assert.Equal(2, ((JArray)raw["accounts"]).Count);
        }

        [Fact]
        public void TheLegacyTripleIsExactlyWhatAnOlderBuildWouldDeserialize()
        {
            // StoredTokenState is the type the old build binds against; binding it to the v2
            // document is the closest this suite can get to running the old build.
            var accounts = new List<StoredTokenState> { Account(Portal, "jdoe", "RT-B") };

            var legacy = SafeJson.Deserialize<StoredTokenState>(
                TokenBlobFormat.Serialize(accounts, ArcGisAccountKey.Make(Portal, "jdoe")));

            Assert.Equal(Portal, legacy.PortalUrl);
            Assert.Equal("jdoe", legacy.Username);
            Assert.Equal("RT-B", legacy.RefreshToken);
        }

        // -----------------------------------------------------------------------------
        // activeAccountKey resolution
        // -----------------------------------------------------------------------------

        [Fact]
        public void AnActiveKeyNamingAnAccountThatIsNotInTheListFallsBackToTheFirst()
        {
            // Happens after an account is removed by another window, or by a hand-edited blob.
            // Leaving it dangling would present as "signed out" with the credentials still there.
            string json = "{\"v\":2,\"activeAccountKey\":\"" + ArcGisAccountKey.Make(Agol, "deleted")
                + "\",\"accounts\":["
                + "{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\",\"refreshToken\":\"RT-A\"},"
                + "{\"portalUrl\":\"" + Portal + "\",\"username\":\"other\",\"refreshToken\":\"RT-B\"}]}";

            var set = TokenBlobFormat.Deserialize(json);

            Assert.Equal("RT-A", set.Active.RefreshToken);
            Assert.Equal(ArcGisAccountKey.Make(Agol, "jdoe"), set.ActiveAccountKey);
        }

        [Fact]
        public void AMissingActiveKeyFallsBackToTheFirstAccount()
        {
            string json = "{\"v\":2,\"accounts\":["
                + "{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\",\"refreshToken\":\"RT-A\"}]}";

            var set = TokenBlobFormat.Deserialize(json);

            Assert.Equal("RT-A", set.Active.RefreshToken);
            Assert.Equal(ArcGisAccountKey.Make(Agol, "jdoe"), set.ActiveAccountKey);
        }

        [Fact]
        public void AnUnknownActiveKeyOnWriteIsRewrittenToTheFirstAccount_NotPersistedDangling()
        {
            var accounts = new List<StoredTokenState> { Account(Agol, "jdoe", "RT-A") };

            var raw = JObject.Parse(TokenBlobFormat.Serialize(accounts, "https://nope|nobody"));

            Assert.Equal(ArcGisAccountKey.Make(Agol, "jdoe"), (string)raw["activeAccountKey"]);
            Assert.Equal("RT-A", (string)raw["refreshToken"]);
        }

        [Fact]
        public void TheActiveKeyMatchesRegardlessOfStoredUsernameCasingOrTrailingSlash()
        {
            // The stored entry and the key can have been written by different code paths (sign-in
            // vs a share import), so resolution must go through the same normalisation as Make.
            string json = "{\"v\":2,\"activeAccountKey\":\"" + ArcGisAccountKey.Make(Agol, "JDoe")
                + "\",\"accounts\":["
                + "{\"portalUrl\":\"" + Portal + "\",\"username\":\"other\",\"refreshToken\":\"RT-B\"},"
                + "{\"portalUrl\":\"" + Agol + "/\",\"username\":\"JDOE\",\"refreshToken\":\"RT-A\"}]}";

            var set = TokenBlobFormat.Deserialize(json);

            Assert.Equal("RT-A", set.Active.RefreshToken);
        }

        // -----------------------------------------------------------------------------
        // Damaged input degrades, never throws
        // -----------------------------------------------------------------------------

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("{}")]
        [InlineData("{\"v\":2}")]
        [InlineData("{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\",\"refreshToken\":\"")]  // truncated
        [InlineData("[{\"refreshToken\":\"RT\"}]")]                                                 // array root
        [InlineData("\"just a string\"")]
        [InlineData("null")]
        [InlineData("not json at all")]
        [InlineData("{\"v\":2,\"accounts\":\"not-an-array\"}")]
        [InlineData("{\"v\":2,\"accounts\":[\"not-an-object\",7]}")]
        public void DamagedOrEmptyInputReadsAsSignedOutInsteadOfThrowing(string json)
        {
            // This runs inside ArcGisAuthService's constructor, on the UI thread, during plugin
            // load — a throw here takes the dock pane down instead of showing a sign-in button.
            Assert.Null(TokenBlobFormat.Deserialize(json));
        }

        [Fact]
        public void AnAccountEntryMissingItsRefreshTokenIsSkipped_NotTheWholeDocument()
        {
            string json = "{\"v\":2,\"accounts\":["
                + "{\"portalUrl\":\"" + Agol + "\",\"username\":\"ghost\"},"
                + "{\"portalUrl\":\"" + Portal + "\",\"username\":\"jdoe\",\"refreshToken\":\"RT-B\"}]}";

            var set = TokenBlobFormat.Deserialize(json);

            Assert.Single(set.Accounts);
            Assert.Equal("RT-B", set.Active.RefreshToken);
        }

        [Fact]
        public void UnknownFieldsAreIgnored_SoANewerBuildsBlobDoesNotSignAnOlderOneOut()
        {
            string json = "{\"v\":3,\"somethingNew\":{\"a\":1},\"accounts\":["
                + "{\"portalUrl\":\"" + Agol + "\",\"username\":\"jdoe\",\"refreshToken\":\"RT-A\","
                + "\"futureField\":true}]}";

            var set = TokenBlobFormat.Deserialize(json);

            Assert.Equal("RT-A", set.Active.RefreshToken);
        }
    }
}
