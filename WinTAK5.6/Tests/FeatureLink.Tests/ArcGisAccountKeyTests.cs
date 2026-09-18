using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// The account key decides whether a second sign-in REPLACES the stored account or ADDS a
    /// duplicate. Get the normalisation wrong and the user ends up with two entries for one
    /// identity that overwrite each other's refresh token on every timer-driven refresh — which
    /// presents as a random sign-out, not as a naming bug.
    /// </summary>
    public class ArcGisAccountKeyTests
    {
        [Fact]
        public void Make_AndTryParse_RoundTrip()
        {
            string key = ArcGisAccountKey.Make("https://www.arcgis.com", "jdoe");

            string portal, username;
            Assert.True(ArcGisAccountKey.TryParse(key, out portal, out username));
            Assert.Equal("https://www.arcgis.com", portal);
            Assert.Equal("jdoe", username);
        }

        [Theory]
        [InlineData("https://www.arcgis.com")]
        [InlineData("https://www.arcgis.com/")]
        [InlineData("  https://www.arcgis.com/  ")]
        [InlineData("https://www.arcgis.com///")]
        public void PortalsDifferingOnlyInTrailingSlashOrPaddingAreOneAccount(string portal)
        {
            Assert.Equal(ArcGisAccountKey.Make("https://www.arcgis.com", "jdoe"),
                ArcGisAccountKey.Make(portal, "jdoe"));
        }

        [Theory]
        [InlineData("jdoe")]
        [InlineData("JDoe")]
        [InlineData("JDOE")]
        [InlineData("  jdoe ")]
        public void UsernameIsCaseInsensitive_BecauseArcGisTreatsItThatWay(string username)
        {
            Assert.Equal(ArcGisAccountKey.Make("https://www.arcgis.com", "jdoe"),
                ArcGisAccountKey.Make("https://www.arcgis.com", username));
        }

        [Fact]
        public void SameUsernameOnDifferentPortalsAreDifferentAccounts()
        {
            Assert.NotEqual(ArcGisAccountKey.Make("https://www.arcgis.com", "jdoe"),
                ArcGisAccountKey.Make("https://portal.example.mil/arcgis", "jdoe"));
        }

        [Fact]
        public void AnEmptyPortalKeysTheDefaultPortal_SoASavedAccountIsFoundAgainAfterARestart()
        {
            // The portal field can come back empty from an older blob; it must not key a third,
            // unreachable "" account.
            Assert.Equal(ArcGisAccountKey.Make("https://www.arcgis.com", "jdoe"),
                ArcGisAccountKey.Make("", "jdoe"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("no-separator-here")]
        [InlineData("|jdoe")]                       // empty portal half
        [InlineData("https://www.arcgis.com|")]     // empty username half
        public void TryParse_RejectsAnythingMakeDidNotProduce(string key)
        {
            string portal, username;
            Assert.False(ArcGisAccountKey.TryParse(key, out portal, out username));
            Assert.Null(portal);
            Assert.Null(username);
        }

        [Fact]
        public void TryParse_SplitsOnTheFirstSeparator_SoAPortalPathCannotEatTheUsername()
        {
            string key = ArcGisAccountKey.Make("https://portal.example.mil/arcgis", "a|b");

            string portal, username;
            Assert.True(ArcGisAccountKey.TryParse(key, out portal, out username));
            Assert.Equal("https://portal.example.mil/arcgis", portal);
            Assert.Equal("a|b", username);
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("tr-TR")]        // dotted/dotless I: ToLower() here yields "ıstanbuluser"
        [InlineData("az-Latn-AZ")]
        public void Make_IsCultureInvariant(string culture)
        {
            // The key is written into tokens.bin. If the operator's locale changed how it folds
            // case, a Turkish-locale user would key their own stored account differently from the
            // one already on disk and appear signed out with the credential sitting right there.
            string expected = null;
            CultureScope.With("en-US", () =>
                expected = ArcGisAccountKey.Make("https://WWW.ArcGIS.com", "ISTANBULuser"));

            CultureScope.With(culture, () =>
                Assert.Equal(expected, ArcGisAccountKey.Make("https://WWW.ArcGIS.com", "ISTANBULuser")));
        }

        [Fact]
        public void DisplayName_IsJustTheUsernameWhenOnlyOneAccountHasIt()
        {
            Assert.Equal("jdoe", ArcGisAccountKey.DisplayName("jdoe", "www.arcgis.com", false));
        }

        [Fact]
        public void DisplayName_AddsThePortalHostOnlyWhenTheUsernameIsAmbiguous()
        {
            Assert.Equal("jdoe (portal.example.mil)",
                ArcGisAccountKey.DisplayName("jdoe", "portal.example.mil", true));
        }

        [Fact]
        public void DisplayName_OmitsAnEmptyHostRatherThanRenderingEmptyParentheses()
        {
            Assert.Equal("jdoe", ArcGisAccountKey.DisplayName("jdoe", "", true));
        }

        [Theory]
        [InlineData("https://www.arcgis.com", "www.arcgis.com")]
        [InlineData("https://portal.example.mil/arcgis", "portal.example.mil")]
        [InlineData("not a url", "not a url")]   // shown raw rather than blank
        [InlineData("", "")]
        public void PortalHost_ExtractsTheHostAndFallsBackToTheRawString(string portal, string expected)
        {
            Assert.Equal(expected, ArcGisAccountKey.PortalHost(portal));
        }
    }
}
