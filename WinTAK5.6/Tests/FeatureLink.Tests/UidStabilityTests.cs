using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// C-39 (cross-language case folding) and the uid-stability half of §3.7.
    ///
    /// The old synthesis was <c>$"FL-{i}-{UtcNow}"</c>, which produced a different uid on every
    /// re-sync: on 5.6 the diff sweep then disposed and recreated every marker (a full flash each
    /// refresh interval) and on 5.7, where the sweep is missing entirely, they accumulated without
    /// bound.
    ///
    /// The golden hex values below are not self-generated. They were produced independently by
    /// Python 3.13 (<c>hashlib.sha256(url.strip().lower().encode('utf-8')).hexdigest()[:12]</c>)
    /// and Node 24 (<c>createHash('sha256').update(url.trim().toLowerCase(),'utf8')</c>) on this
    /// machine, and both agreed. This asserts that C#'s <c>ToLowerInvariant()</c> + UTF-8 SHA-256
    /// lands on the same bytes — including the dotted/dotless-I case, CJK and an astral-plane
    /// emoji — which is the guarantee AUTO-ICONSET-SPEC.md makes and which no auditor had checked
    /// in more than one language.
    /// </summary>
    public class UidStabilityTests
    {
        [Theory]
        [InlineData("https://services.example.com/arcgis/rest/services/Demo/FeatureServer/0", "5c908ed3ba44")]
        [InlineData("HTTPS://SERVICES.EXAMPLE.COM/ArcGIS/rest/services/Demo/FeatureServer/0", "5c908ed3ba44")]
        [InlineData("  https://services.example.com/arcgis/rest/services/Demo/FeatureServer/0  ", "5c908ed3ba44")]
        [InlineData("https://example.com/ISTANBUL/FeatureServer/0", "1f226e32ae2e")]
        [InlineData("https://example.com/中文レイヤー/FeatureServer/0", "6afe4571be66")]
        [InlineData("https://example.com/🚀rocket/FeatureServer/0", "479ef4f8ce7c")]
        [InlineData("", "e3b0c44298fc")]
        public void LayerUidSalt_MatchesThePythonAndJavaScriptGoldenVectors(string url, string expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.LayerUidSalt(url));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("tr-TR")]   // the dotted/dotless-I locale: ToLower() would give "ıstanbul"
        [InlineData("az-Latn-AZ")]
        [InlineData("ar-SA")]
        public void LayerUidSalt_IsCultureInvariant(string culture)
        {
            CultureScope.With(culture, () =>
                Assert.Equal("1f226e32ae2e",
                    ArcGisFeatureService.LayerUidSalt("https://example.com/ISTANBUL/FeatureServer/0")));
        }

        [Fact]
        public void DeriveStableUid_PrefersTheObjectId_AndIsIdenticalAcrossTwoDownloads()
        {
            var attrs = JObject.Parse("{\"OBJECTID\":42,\"NAME\":\"Alpha\"}");
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            string first = ArcGisFeatureService.DeriveStableUid("abc123", attrs, geom, 0);
            string second = ArcGisFeatureService.DeriveStableUid("abc123", attrs, geom, 7);

            Assert.Equal("FL-abc123-42", first);
            // The index must not participate when an object id exists — otherwise a feature that
            // moved position in the result set would get a new uid.
            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData("OBJECTID")]
        [InlineData("objectid")]
        [InlineData("ObjectID")]
        [InlineData("FID")]
        [InlineData("OID")]
        [InlineData("fid")]
        public void DeriveStableUid_RecognisesTheCommonObjectIdSpellings(string field)
        {
            var attrs = JObject.Parse("{\"" + field + "\":9}");
            Assert.Equal("FL-salt-9", ArcGisFeatureService.DeriveStableUid("salt", attrs, null, 0));
        }

        [Fact]
        public void DeriveStableUid_WithNoObjectId_HashesTheFeatureContent_AndIsStable()
        {
            var attrs = JObject.Parse("{\"NAME\":\"Alpha\"}");
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            string a = ArcGisFeatureService.DeriveStableUid("salt", attrs, geom, 0);
            string b = ArcGisFeatureService.DeriveStableUid("salt", attrs, geom, 3);
            Assert.Equal(a, b);
            Assert.StartsWith("FL-salt-h", a);

            var different = JObject.Parse("{\"NAME\":\"Bravo\"}");
            Assert.NotEqual(a, ArcGisFeatureService.DeriveStableUid("salt", different, geom, 0));
        }

        [Fact]
        public void DeriveStableUid_DifferentLayers_DoNotCollide()
        {
            var attrs = JObject.Parse("{\"OBJECTID\":1}");
            string a = ArcGisFeatureService.DeriveStableUid(
                ArcGisFeatureService.LayerUidSalt("https://a/FeatureServer/0"), attrs, null, 0);
            string b = ArcGisFeatureService.DeriveStableUid(
                ArcGisFeatureService.LayerUidSalt("https://b/FeatureServer/0"), attrs, null, 0);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DeriveStableUid_CompletelyEmptyFeature_FallsBackToTheIndex()
        {
            Assert.Equal("FL-salt-i4", ArcGisFeatureService.DeriveStableUid("salt", null, null, 4));
        }
    }
}
