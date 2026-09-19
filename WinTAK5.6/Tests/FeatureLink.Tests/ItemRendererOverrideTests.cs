using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FeatureLink.Models;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers the item-level renderer override.
    ///
    /// <para><b>The failure this closes.</b> Styling a hosted feature layer on its portal item's
    /// <b>Visualization</b> tab does not modify the feature service. ArcGIS saves that renderer to
    /// the ITEM, at <c>/sharing/rest/content/items/{id}/data</c>, so the service endpoint keeps
    /// reporting whatever it was published with — commonly a single-symbol <c>simple</c> renderer.
    /// Reading only the service therefore produced one repeated marker on a layer the operator
    /// could plainly see was styled a dozen different ways, with nothing logged, because a simple
    /// renderer really does have exactly one symbol.</para>
    /// </summary>
    public class ItemRendererOverrideTests
    {
        private const string Portal = "https://www.arcgis.com";
        private const string ItemId = "c7a11600aeee437382ad66254383c893";

        private static ArcGisFeatureService WithResponse(StubHandler handler) =>
            new ArcGisFeatureService(handler);

        private static string ItemData(int layerId, string rendererType) => new JObject
        {
            ["layers"] = new JArray
            {
                new JObject
                {
                    ["id"] = layerId,
                    ["layerDefinition"] = new JObject
                    {
                        ["drawingInfo"] = new JObject
                        {
                            ["renderer"] = new JObject { ["type"] = rendererType, ["field1"] = "kind" },
                        },
                    },
                },
            },
        }.ToString();

        // ── the override is found and returned ──────────────────────────────────────

        [Fact]
        public async Task The_item_override_is_returned_when_one_exists()
        {
            var handler = new StubHandler().Enqueue(ItemData(0, "uniqueValue"));

            var renderer = await WithResponse(handler)
                .FetchItemRendererAsync(Portal, ItemId, 0, null);

            Assert.NotNull(renderer);
            Assert.Equal("uniqueValue", (string)renderer["type"]);
        }

        [Fact]
        public async Task It_reads_the_documented_item_data_endpoint()
        {
            var handler = new StubHandler().Enqueue(ItemData(0, "uniqueValue"));

            await WithResponse(handler).FetchItemRendererAsync(Portal, ItemId, 0, null);

            string requested = handler.RequestUris.Single();
            Assert.Contains("/sharing/rest/content/items/" + ItemId + "/data", requested, StringComparison.Ordinal);
            Assert.Contains("f=json", requested, StringComparison.Ordinal);
        }

        /// <summary>A multi-layer item carries an override per sublayer; the wrong one would style
        /// the layer with another layer's symbology.</summary>
        [Fact]
        public async Task The_override_matching_the_requested_sublayer_is_chosen()
        {
            string data = new JObject
            {
                ["layers"] = new JArray
                {
                    JObject.Parse(ItemData(0, "simple"))["layers"][0],
                    JObject.Parse(ItemData(3, "uniqueValue"))["layers"][0],
                },
            }.ToString();

            var renderer = await WithResponse(new StubHandler().Enqueue(data))
                .FetchItemRendererAsync(Portal, ItemId, 3, null);

            Assert.Equal("uniqueValue", (string)renderer["type"]);
        }

        [Fact]
        public async Task A_layer_id_of_minus_one_accepts_the_first_override()
        {
            var renderer = await WithResponse(new StubHandler().Enqueue(ItemData(7, "classBreaks")))
                .FetchItemRendererAsync(Portal, ItemId, -1, null);

            Assert.Equal("classBreaks", (string)renderer["type"]);
        }

        // ── absence is normal, never a failure ──────────────────────────────────────

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"layers\":[]}")]
        [InlineData("{\"layers\":[{\"id\":0}]}")]
        [InlineData("{\"layers\":[{\"id\":0,\"layerDefinition\":{}}]}")]
        public async Task An_item_with_no_override_yields_null_rather_than_throwing(string body)
        {
            var renderer = await WithResponse(new StubHandler().Enqueue(body))
                .FetchItemRendererAsync(Portal, ItemId, 0, null);

            Assert.Null(renderer);
        }

        /// <summary>No read access to the item's data is routine — the caller simply uses the
        /// service renderer. It must not surface as a download failure.</summary>
        [Fact]
        public async Task An_unreadable_item_yields_null_rather_than_throwing()
        {
            var handler = new StubHandler().Enqueue(
                "{\"error\":{\"code\":403,\"message\":\"You do not have permissions\"}}");

            var renderer = await WithResponse(handler).FetchItemRendererAsync(Portal, ItemId, 0, null);

            Assert.Null(renderer);
        }

        [Fact]
        public async Task No_item_id_means_no_request_is_made_at_all()
        {
            var handler = new StubHandler();

            Assert.Null(await WithResponse(handler).FetchItemRendererAsync(Portal, null, 0, null));
            Assert.Null(await WithResponse(handler).FetchItemRendererAsync(Portal, "", 0, null));
            Assert.Empty(handler.RequestUris);
        }

        // ── the sublayer index the override is matched on ───────────────────────────

        [Theory]
        [InlineData("https://h/a/rest/services/X/FeatureServer/0", 0)]
        [InlineData("https://h/a/rest/services/X/FeatureServer/3", 3)]
        [InlineData("https://h/a/rest/services/X/FeatureServer/12", 12)]
        [InlineData("https://h/a/rest/services/X/FeatureServer/3/", 3)]
        [InlineData("https://h/a/rest/services/X/FeatureServer", -1)]
        [InlineData("https://h/a/rest/services/X/MapServer", -1)]
        [InlineData("", -1)]
        [InlineData(null, -1)]
        public void LayerIndexOf_reads_the_trailing_sublayer_index(string url, int expected)
        {
            Assert.Equal(expected, ArcGisFeatureService.LayerIndexOf(url));
        }

        // ── the model carries the id across a restart ───────────────────────────────

        [Fact]
        public void ItemId_survives_the_settings_round_trip()
        {
            var layer = new ArcGisLayer("Test", "https://h/a/rest/services/X/FeatureServer/0", "private")
            {
                ItemId = ItemId,
            };

            var restored = ArcGisLayer.FromXElement(layer.ToXElement());

            Assert.Equal(ItemId, restored.ItemId);
        }

        /// <summary>A layer added by URL has no portal item, and an older settings.xml has no
        /// element at all. Both must read back as null, not "".</summary>
        [Fact]
        public void A_layer_with_no_item_round_trips_as_null()
        {
            var layer = new ArcGisLayer("Test", "https://h/a/rest/services/X/FeatureServer/0", "public");

            Assert.Null(ArcGisLayer.FromXElement(layer.ToXElement()).ItemId);
        }
    }
}
