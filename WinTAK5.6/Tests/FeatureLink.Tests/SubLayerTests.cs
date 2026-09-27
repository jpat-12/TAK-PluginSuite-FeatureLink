using System.Linq;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// C-08 — enumerating a FeatureServer's sublayers.
    ///
    /// <para>The defect this closes was silent data loss in the plugin's core function:
    /// <c>EnsureLayerIndex</c> appended <c>/0</c> to a bare service root and nothing enumerated the
    /// rest, so adding a five-layer service gave the operator one layer with no error, no warning
    /// and nothing in the log. It is the likeliest explanation for two field reports — "most of the
    /// layers weren't being downloaded" and a line layer reporting zero features.</para>
    ///
    /// <para>The parsing is tested rather than the fetching: which layers are offered, and which
    /// are correctly refused, is the part that decides what an operator actually gets.</para>
    /// </summary>
    public class SubLayerTests
    {
        private const string Root = "https://services9.arcgis.com/ABC/arcgis/rest/services/TEST/FeatureServer";

        private static JObject Service(string layersJson)
        {
            return JObject.Parse("{\"currentVersion\":11.1,\"layers\":" + layersJson + "}");
        }

        // ── enumeration ─────────────────────────────────────────────────────────

        [Fact]
        public void Every_queryable_sublayer_becomes_its_own_url()
        {
            var root = Service(@"[
                {""id"":0,""name"":""Checkpoints"",""geometryType"":""esriGeometryPoint""},
                {""id"":1,""name"":""Routes"",""geometryType"":""esriGeometryPolyline""},
                {""id"":2,""name"":""Sectors"",""geometryType"":""esriGeometryPolygon""}]");

            var found = ArcGisFeatureService.ParseSubLayers(root, Root);

            Assert.Equal(3, found.Count);
            Assert.Equal(new[] { Root + "/0", Root + "/1", Root + "/2" },
                         found.Select(l => l.Url).ToArray());
            Assert.Equal(new[] { "Checkpoints", "Routes", "Sectors" },
                         found.Select(l => l.Name).ToArray());
        }

        /// <summary>Ids are taken from the service, not from the array position — a service may
        /// number its layers with gaps.</summary>
        [Fact]
        public void Ids_come_from_the_service_not_the_array_order()
        {
            var root = Service(@"[{""id"":3,""name"":""Third""},{""id"":7,""name"":""Seventh""}]");

            var found = ArcGisFeatureService.ParseSubLayers(root, Root);

            Assert.Equal(new[] { Root + "/3", Root + "/7" }, found.Select(l => l.Url).ToArray());
            Assert.Equal(new[] { 3, 7 }, found.Select(l => l.Id).ToArray());
        }

        /// <summary>A GROUP layer has no geometry of its own and cannot be queried. Offering it
        /// would give the operator a row that can only ever fail to download.</summary>
        [Fact]
        public void Group_layers_are_skipped()
        {
            var root = Service(@"[
                {""id"":0,""name"":""All Operations"",""subLayerIds"":[1,2]},
                {""id"":1,""name"":""Checkpoints"",""geometryType"":""esriGeometryPoint""},
                {""id"":2,""name"":""Routes"",""geometryType"":""esriGeometryPolyline""}]");

            var found = ArcGisFeatureService.ParseSubLayers(root, Root);

            Assert.Equal(2, found.Count);
            Assert.DoesNotContain(found, l => l.Name == "All Operations");
        }

        /// <summary>A null <c>subLayerIds</c> is how a normal layer reports "I am not a group";
        /// treating the key's mere presence as grouping would hide every layer.</summary>
        [Fact]
        public void A_null_subLayerIds_does_not_make_it_a_group()
        {
            var root = Service(@"[{""id"":0,""name"":""Checkpoints"",""subLayerIds"":null}]");

            Assert.Single(ArcGisFeatureService.ParseSubLayers(root, Root));
        }

        [Fact]
        public void A_layer_with_no_usable_id_is_skipped()
        {
            var root = Service(@"[
                {""name"":""No id at all""},
                {""id"":-1,""name"":""Negative""},
                {""id"":0,""name"":""Fine""}]");

            var found = ArcGisFeatureService.ParseSubLayers(root, Root);

            Assert.Single(found);
            Assert.Equal("Fine", found[0].Name);
        }

        /// <summary>ArcGIS has been seen to send ids as JSON strings.</summary>
        [Fact]
        public void An_id_sent_as_a_string_is_still_read()
        {
            var root = Service(@"[{""id"":""4"",""name"":""Stringy""}]");

            var found = ArcGisFeatureService.ParseSubLayers(root, Root);

            Assert.Single(found);
            Assert.Equal(4, found[0].Id);
            Assert.Equal(Root + "/4", found[0].Url);
        }

        /// <summary>No layers array means the caller must fall back to the legacy <c>/0</c> —
        /// returning nothing is how that is signalled, and it must never throw.</summary>
        [Fact]
        public void A_service_with_no_layers_array_yields_nothing()
        {
            Assert.Empty(ArcGisFeatureService.ParseSubLayers(JObject.Parse("{\"currentVersion\":11.1}"), Root));
            Assert.Empty(ArcGisFeatureService.ParseSubLayers(Service("[]"), Root));
            Assert.Empty(ArcGisFeatureService.ParseSubLayers(null, Root));
            Assert.Empty(ArcGisFeatureService.ParseSubLayers(Service("[]"), null));
        }

        [Fact]
        public void A_trailing_slash_on_the_service_root_does_not_double_up()
        {
            var found = ArcGisFeatureService.ParseSubLayers(
                Service(@"[{""id"":0,""name"":""X""}]"), Root + "/");

            Assert.Equal(Root + "/0", found[0].Url);
        }

        // ── what the picker shows ───────────────────────────────────────────────

        [Theory]
        [InlineData("esriGeometryPoint", "Checkpoints  (points)")]
        [InlineData("esriGeometryPolyline", "Checkpoints  (lines)")]
        [InlineData("esriGeometryPolygon", "Checkpoints  (areas)")]
        [InlineData("esriGeometryEnvelope", "Checkpoints")]
        [InlineData("", "Checkpoints")]
        [InlineData(null, "Checkpoints")]
        public void The_picker_names_a_layer_and_hints_at_its_geometry(string geometry, string expected)
        {
            var layer = new ArcGisFeatureService.SubLayerRef
            {
                Id = 0,
                Name = "Checkpoints",
                GeometryType = geometry,
            };

            Assert.Equal(expected, layer.Display);
        }

        /// <summary>A nameless layer still needs something clickable.</summary>
        [Fact]
        public void A_layer_with_no_name_falls_back_to_its_id()
        {
            var layer = new ArcGisFeatureService.SubLayerRef { Id = 5, Name = null };

            Assert.Equal("Layer 5", layer.Display);
        }

        // ── the URL shapes around it ────────────────────────────────────────────

        [Theory]
        [InlineData("https://h/a/rest/services/X/FeatureServer", true)]
        [InlineData("https://h/a/rest/services/X/MapServer", true)]
        [InlineData("https://h/a/rest/services/X/featureserver", true)]
        [InlineData("https://h/a/rest/services/X/FeatureServer/", true)]
        [InlineData("https://h/a/rest/services/X/FeatureServer/0", false)]
        [InlineData("https://h/a/rest/services/X", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void A_service_root_is_told_apart_from_an_addressed_layer(string url, bool expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.IsServiceRoot(url));
        }

        [Theory]
        [InlineData("https://h/a/FeatureServer/0?token=secret", "https://h/a/FeatureServer/0")]
        [InlineData("  https://h/a/FeatureServer/  ", "https://h/a/FeatureServer")]
        [InlineData("https://h/a/FeatureServer?f=json", "https://h/a/FeatureServer")]
        public void A_url_is_canonicalised_before_anything_reads_it(string raw, string expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.CanonicalServiceUrl(raw));
        }

        /// <summary>The legacy fallback still works — it is now only reached when enumeration
        /// found nothing, and dropping the layer instead would be worse.</summary>
        [Fact]
        public void The_zero_fallback_is_unchanged_for_a_service_that_advertises_nothing()
        {
            Assert.Equal("https://h/a/FeatureServer/0",
                ArcGisFeatureService.EnsureLayerIndex("https://h/a/FeatureServer"));
            Assert.Equal("https://h/a/FeatureServer/3",
                ArcGisFeatureService.EnsureLayerIndex("https://h/a/FeatureServer/3"));
        }

        /// <summary>Each sublayer is keyed by its own URL, which is what lets every existing
        /// URL-keyed structure — display configs, iconset uids, extents, the marker map — keep
        /// working with no change.</summary>
        [Fact]
        public void Each_sublayer_url_round_trips_through_the_index_reader()
        {
            var found = ArcGisFeatureService.ParseSubLayers(
                Service(@"[{""id"":0,""name"":""A""},{""id"":11,""name"":""B""}]"), Root);

            Assert.Equal(0, ArcGisFeatureService.LayerIndexOf(found[0].Url));
            Assert.Equal(11, ArcGisFeatureService.LayerIndexOf(found[1].Url));
        }

        /// <summary>Two sublayers of one service must not collide in any URL-keyed structure.</summary>
        [Fact]
        public void Sublayers_of_one_service_have_distinct_uid_salts()
        {
            var found = ArcGisFeatureService.ParseSubLayers(
                Service(@"[{""id"":0,""name"":""A""},{""id"":1,""name"":""B""}]"), Root);

            Assert.NotEqual(ArcGisFeatureService.LayerUidSalt(found[0].Url),
                            ArcGisFeatureService.LayerUidSalt(found[1].Url));
        }
    }
}
