using System.Collections.Generic;
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

        // ── helper ──────────────────────────────────────────────────────────────

        /// <summary>Calls the real derivation with a fresh collision set. Latitude and longitude
        /// are passed explicitly because production reads them once, before the uid is derived.</summary>
        private static string Uid(string salt, JObject attrs, JObject geom, int index,
            double lat = double.NaN, double lon = double.NaN, ISet<string> taken = null)
        {
            ArcGisFeatureService.UidSource source;
            return ArcGisFeatureService.DeriveStableUid(
                salt, attrs, geom, index, lat, lon, taken ?? new HashSet<string>(), out source);
        }

        private static ArcGisFeatureService.UidSource SourceOf(string salt, JObject attrs,
            JObject geom, int index, double lat = double.NaN, double lon = double.NaN)
        {
            ArcGisFeatureService.UidSource source;
            ArcGisFeatureService.DeriveStableUid(
                salt, attrs, geom, index, lat, lon, new HashSet<string>(), out source);
            return source;
        }

        // ── object id ───────────────────────────────────────────────────────────

        [Fact]
        public void DeriveStableUid_PrefersTheObjectId_AndIsIdenticalAcrossTwoDownloads()
        {
            var attrs = JObject.Parse("{\"OBJECTID\":42,\"NAME\":\"Alpha\"}");
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            string first = Uid("abc123", attrs, geom, 0, 34.0, -117.0);
            string second = Uid("abc123", attrs, geom, 7, 34.0, -117.0);

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
            Assert.Equal("FL-salt-9", Uid("salt", attrs, null, 0, 34.0, -117.0));
        }

        /// <summary>An object id survives every edit, which is why it outranks position.</summary>
        [Fact]
        public void DeriveStableUid_WithAnObjectId_SurvivesBothEditsAndMoves()
        {
            var before = JObject.Parse("{\"OBJECTID\":42,\"STATUS\":\"green\"}");
            var after = JObject.Parse("{\"OBJECTID\":42,\"STATUS\":\"red\"}");

            Assert.Equal(Uid("salt", before, null, 0, 34.0, -117.0),
                         Uid("salt", after, null, 0, 35.5, -118.5));
        }

        // ── position, the no-object-id case ─────────────────────────────────────

        /// <summary>
        /// The behaviour this tier exists for.
        ///
        /// <para>The fallback used to hash the attributes and the geometry together, and an
        /// earlier version of this suite asserted exactly that — including that editing an
        /// attribute produced a DIFFERENT uid. That was the defect, not the contract: an operator
        /// editing the symbology field to change how a marker looks got a new uid, so the marker
        /// was disposed and recreated and every data package naming the old uid went stale. The
        /// assertion is deliberately inverted here.</para>
        /// </summary>
        [Fact]
        public void DeriveStableUid_WithNoObjectId_SurvivesAnAttributeEdit()
        {
            var before = JObject.Parse("{\"NAME\":\"Alpha\",\"STATUS\":\"green\"}");
            var after = JObject.Parse("{\"NAME\":\"Alpha\",\"STATUS\":\"red\"}");
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            string first = Uid("salt", before, geom, 0, 34.0, -117.0);
            string second = Uid("salt", after, geom, 0, 34.0, -117.0);

            Assert.Equal(first, second);
            Assert.StartsWith("FL-salt-p", first);
            Assert.Equal(ArcGisFeatureService.UidSource.Position,
                         SourceOf("salt", before, geom, 0, 34.0, -117.0));
        }

        [Fact]
        public void DeriveStableUid_WithNoObjectId_IgnoresThePositionInTheResultSet()
        {
            var attrs = JObject.Parse("{\"NAME\":\"Alpha\"}");
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            Assert.Equal(Uid("salt", attrs, geom, 0, 34.0, -117.0),
                         Uid("salt", attrs, geom, 3, 34.0, -117.0));
        }

        /// <summary>Honest about the remaining limit: position IS the identity here, so moving a
        /// feature necessarily renames it. Documented rather than hidden.</summary>
        [Fact]
        public void DeriveStableUid_WithNoObjectId_ChangesWhenTheFeatureMoves()
        {
            var attrs = JObject.Parse("{\"NAME\":\"Alpha\"}");
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            Assert.NotEqual(Uid("salt", attrs, geom, 0, 34.0, -117.0),
                            Uid("salt", attrs, geom, 0, 34.5, -117.0));
        }

        /// <summary>Two features stacked at one position must not share a uid — the second would
        /// silently replace the first on the map and in any package.</summary>
        [Fact]
        public void DeriveStableUid_TwoFeaturesAtTheSamePosition_AreSeparated()
        {
            var taken = new HashSet<string>();
            var geom = JObject.Parse("{\"x\":-117.0,\"y\":34.0}");

            string a = Uid("salt", JObject.Parse("{\"N\":\"A\"}"), geom, 0, 34.0, -117.0, taken);
            string b = Uid("salt", JObject.Parse("{\"N\":\"B\"}"), geom, 1, 34.0, -117.0, taken);
            string c = Uid("salt", JObject.Parse("{\"N\":\"C\"}"), geom, 2, 34.0, -117.0, taken);

            Assert.Equal(3, new HashSet<string>(new[] { a, b, c }).Count);
            Assert.EndsWith("_2", b);
            Assert.EndsWith("_3", c);
        }

        /// <summary>Sub-millimetre noise in the service's own output must not invent a new
        /// identity for a feature that has not moved.</summary>
        [Fact]
        public void DeriveStableUid_IsNotDisturbedByFloatingPointNoise()
        {
            var attrs = JObject.Parse("{\"N\":\"A\"}");

            Assert.Equal(Uid("salt", attrs, null, 0, 34.000000001, -117.000000002),
                         Uid("salt", attrs, null, 0, 34.0, -117.0));
        }

        // ── remaining fallbacks ─────────────────────────────────────────────────

        [Fact]
        public void DeriveStableUid_WithNoUsablePosition_FallsBackToTheContentHash()
        {
            var attrs = JObject.Parse("{\"NAME\":\"Alpha\"}");

            string uid = Uid("salt", attrs, null, 0);   // lat/lon NaN

            Assert.StartsWith("FL-salt-h", uid);
            Assert.Equal(ArcGisFeatureService.UidSource.Content, SourceOf("salt", attrs, null, 0));
        }

        [Fact]
        public void DeriveStableUid_CompletelyEmptyFeature_FallsBackToTheIndex()
        {
            Assert.Equal("FL-salt-i4", Uid("salt", null, null, 4));
            Assert.Equal(ArcGisFeatureService.UidSource.Index, SourceOf("salt", null, null, 4));
        }

        [Fact]
        public void DeriveStableUid_DifferentLayers_DoNotCollide()
        {
            var attrs = JObject.Parse("{\"OBJECTID\":1}");
            string a = Uid(ArcGisFeatureService.LayerUidSalt("https://a/FeatureServer/0"), attrs, null, 0);
            string b = Uid(ArcGisFeatureService.LayerUidSalt("https://b/FeatureServer/0"), attrs, null, 0);
            Assert.NotEqual(a, b);
        }

        /// <summary>Two layers with no object ids, whose features sit at the same coordinates,
        /// must still not collide — the layer salt is what separates them.</summary>
        [Fact]
        public void DeriveStableUid_SamePositionInDifferentLayers_DoNotCollide()
        {
            var attrs = JObject.Parse("{\"N\":\"A\"}");
            string a = Uid(ArcGisFeatureService.LayerUidSalt("https://a/FeatureServer/0"),
                           attrs, null, 0, 34.0, -117.0);
            string b = Uid(ArcGisFeatureService.LayerUidSalt("https://b/FeatureServer/0"),
                           attrs, null, 0, 34.0, -117.0);
            Assert.NotEqual(a, b);
        }

        /// <summary>Position uids are written with the invariant culture: a de-DE machine's comma
        /// decimal separator would otherwise derive a different uid for the same feature.</summary>
        [Theory]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        [InlineData("tr-TR")]
        public void DeriveStableUid_PositionIsCultureInvariant(string culture)
        {
            var attrs = JObject.Parse("{\"N\":\"A\"}");
            string expected = Uid("salt", attrs, null, 0, 34.25, -117.5);

            string actual = null;
            CultureScope.With(culture, () => actual = Uid("salt", attrs, null, 0, 34.25, -117.5));

            Assert.Equal(expected, actual);
        }
    }
}
