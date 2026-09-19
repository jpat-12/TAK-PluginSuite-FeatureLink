using System;
using System.Collections.Generic;
using FeatureLink.Models;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers how a marker gets its name.
    ///
    /// <para>Markers were labelled "Feature-0", "Feature-1", … — a position in the result set,
    /// which is meaningless to an operator and unstable: the same real-world feature gets a
    /// different label whenever the service returns rows in a different order. Every ArcGIS
    /// feature layer declares a <c>displayField</c>, which is the attribute ArcGIS itself labels
    /// features with and titles pop-ups on, so it is the name the operator already recognises.</para>
    /// </summary>
    public class CallsignNamingTests
    {
        private static Dictionary<string, string> Attrs(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Fact]
        public void The_layers_display_field_names_the_marker()
        {
            string name = ArcGisFeatureService.DeriveCallsign(
                Attrs("OBJECTID", "7", "SITE_NAME", "Shelby EOC"), "SITE_NAME", 3);

            Assert.Equal("Shelby EOC", name);
        }

        [Fact]
        public void The_display_field_is_matched_case_insensitively()
        {
            string name = ArcGisFeatureService.DeriveCallsign(
                Attrs("site_name", "Shelby EOC"), "SITE_NAME", 3);

            Assert.Equal("Shelby EOC", name);
        }

        /// <summary>displayField very often defaults to the object id. Naming markers "1", "2",
        /// "3" is no more useful than "Feature-1" and loses the signal that the name is a
        /// placeholder, so the object-id case is skipped in favour of the rest of the ladder.</summary>
        [Theory]
        [InlineData("OBJECTID")]
        [InlineData("objectid")]
        [InlineData("FID")]
        [InlineData("OID")]
        public void A_display_field_pointing_at_the_object_id_is_ignored(string displayField)
        {
            string name = ArcGisFeatureService.DeriveCallsign(
                Attrs(displayField, "7"), displayField, 3);

            Assert.Equal("Feature-3", name);
        }

        [Fact]
        public void An_object_id_display_field_still_yields_to_a_conventional_name_column()
        {
            string name = ArcGisFeatureService.DeriveCallsign(
                Attrs("OBJECTID", "7", "NAME", "Staging Alpha"), "OBJECTID", 3);

            Assert.Equal("Staging Alpha", name);
        }

        [Theory]
        [InlineData("name")]
        [InlineData("Name")]
        [InlineData("NAME")]
        [InlineData("title")]
        [InlineData("label")]
        [InlineData("callsign")]
        [InlineData("description")]
        public void Conventional_name_columns_are_used_when_there_is_no_display_field(string field)
        {
            string name = ArcGisFeatureService.DeriveCallsign(Attrs(field, "Bravo"), null, 3);

            Assert.Equal("Bravo", name);
        }

        [Fact]
        public void The_display_field_beats_a_conventional_column()
        {
            string name = ArcGisFeatureService.DeriveCallsign(
                Attrs("NAME", "Generic", "SITE_NAME", "Shelby EOC"), "SITE_NAME", 3);

            Assert.Equal("Shelby EOC", name);
        }

        /// <summary>A blank or null-ish value must fall through, not name a marker "" or
        /// "&lt;Null&gt;" — which is worse than the positional fallback because it looks
        /// deliberate.</summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("<Null>")]
        [InlineData("null")]
        public void A_blank_or_null_value_falls_through_to_the_next_candidate(string value)
        {
            string name = ArcGisFeatureService.DeriveCallsign(
                Attrs("SITE_NAME", value), "SITE_NAME", 3);

            Assert.Equal("Feature-3", name);
        }

        [Fact]
        public void With_nothing_usable_the_positional_fallback_remains()
        {
            Assert.Equal("Feature-11",
                ArcGisFeatureService.DeriveCallsign(Attrs("SHAPE_Length", "42"), null, 11));
            Assert.Equal("Feature-0", ArcGisFeatureService.DeriveCallsign(null, null, 0));
        }

        // ── the feature-count display ───────────────────────────────────────────────

        /// <summary>"0 features" was shown both for a layer that genuinely holds nothing and for
        /// one nobody had counted yet. The second is the common case — a browse-list row that has
        /// never been downloaded — and reporting it as zero states something false about the
        /// source data.</summary>
        [Fact]
        public void An_uncounted_layer_reads_as_unknown_not_as_zero()
        {
            var layer = new ArcGisLayer("L", "https://h/a/rest/services/X/FeatureServer/0", "private");

            Assert.False(layer.FeatureCountKnown);
            Assert.Equal("— features", layer.FeatureCountText);
        }

        [Fact]
        public void A_counted_layer_reports_its_count_and_a_real_zero_is_still_zero()
        {
            var layer = new ArcGisLayer("L", "https://h/a/rest/services/X/FeatureServer/0", "private");

            layer.FeatureCount = 29;
            Assert.True(layer.FeatureCountKnown);
            Assert.Equal("29 features", layer.FeatureCountText);

            layer.FeatureCount = 0;
            Assert.Equal("0 features", layer.FeatureCountText);
        }

        [Fact]
        public void The_known_flag_survives_the_settings_round_trip()
        {
            var layer = new ArcGisLayer("L", "https://h/a/rest/services/X/FeatureServer/0", "private");
            layer.FeatureCount = 0;

            var restored = ArcGisLayer.FromXElement(layer.ToXElement());

            Assert.True(restored.FeatureCountKnown);
            Assert.Equal("0 features", restored.FeatureCountText);
        }

        [Fact]
        public void An_older_settings_file_without_the_flag_reads_as_unknown()
        {
            var layer = new ArcGisLayer("L", "https://h/a/rest/services/X/FeatureServer/0", "private");
            var xml = layer.ToXElement();
            xml.Element("FeatureCountKnown")?.Remove();

            Assert.False(ArcGisLayer.FromXElement(xml).FeatureCountKnown);
        }
    }
}
