using System;
using System.Collections.Generic;
using System.Linq;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers merging picture-marker icons with simple-marker colours into one config.
    ///
    /// <para><b>The defect these exist to prevent.</b> Icons and colours were first treated as
    /// alternatives — any picture symbol at all meant the colour config was never built. On a real
    /// layer whose renderer carried a couple of badge icons among a dozen coloured circles, that
    /// produced a single icon stamped on every feature and no colours whatsoever, while ArcGIS
    /// rendered twelve distinct symbols. Nothing failed, nothing logged; the map was just wrong.
    /// Precedence has to be per VALUE, not per layer, and these tests pin that.</para>
    ///
    /// <para>Each assertion runs the merged config back through the REAL resolvers the download
    /// path uses, rather than inspecting JSON shape — a config that looks right but resolves wrong
    /// is the failure mode that actually reaches the operator.</para>
    /// </summary>
    public class IconColorMergeTests
    {
        private const string Uid = "9aa866980d8078ab2a4cbb84b5ad4de9b4c6af6d3192957e15b8ce459dfd2ad9";
        private const string Group = "Damage Icons";
        private const string Field = "kind";

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

        private static AutoIconset.Extraction Icons(params string[] values)
        {
            var e = new AutoIconset.Extraction { Field = Field };
            foreach (string v in values)
                e.Symbols.Add(new AutoIconset.PictureSymbol
                {
                    Value = v,
                    FileName = AutoIconset.FileName(v),
                    Png = Png,
                });
            return e;
        }

        private static JObject ColorConfig(params string[] valueAndHex)
        {
            var uv = new JArray();
            for (int i = 0; i < valueAndHex.Length; i += 2)
                uv.Add(new JObject { ["v"] = valueAndHex[i], ["c"] = valueAndHex[i + 1] });
            return new JObject
            {
                ["t"] = "uv",
                ["c"] = "808080",
                ["f"] = Field,
                ["op"] = 1.0,
                ["uv"] = uv,
            };
        }

        private static Dictionary<string, string> Attrs(string value) =>
            new Dictionary<string, string> { [Field] = value };

        /// <summary>Compares a resolved colour to an expected hex, tolerating the resolver's
        /// "#RRGGBB" formatting against the "rrggbb" form the config carries.</summary>
        private static void AssertColor(string expectedHex, JObject sym, string value)
        {
            var resolved = DisplayStyleResolver.ResolveColor(sym, Attrs(value));
            Assert.True(resolved.HasValue, $"no colour resolved for value '{value}'");
            string actual = DisplayStyleResolver.ToHex6(resolved.Value).TrimStart('#');
            Assert.Equal(expectedHex.TrimStart('#'), actual, ignoreCase: true);
        }

        // ── the regression that motivated the file ──────────────────────────────────

        /// <summary>A renderer with ONE picture symbol and several coloured categories must keep
        /// every colour. Previously the single icon replaced all of them.</summary>
        [Fact]
        public void One_icon_among_many_colours_does_not_erase_the_colours()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP"),
                ColorConfig("ACP", "ff0000", "Staging", "00ff00", "Shelter", "0000ff"));

            AssertColor("00ff00", merged, "Staging");
            AssertColor("0000ff", merged, "Shelter");
        }

        /// <summary>...and the value that DOES have a picture symbol still resolves its icon.</summary>
        [Fact]
        public void The_value_with_a_picture_symbol_still_resolves_its_icon()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP"),
                ColorConfig("ACP", "ff0000", "Staging", "00ff00"));

            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "ACP.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("ACP")));
        }

        /// <summary>A value with only a colour must NOT pick up another value's icon — the bug
        /// that made every feature look identical.</summary>
        [Fact]
        public void A_colour_only_value_resolves_no_icon()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP"),
                ColorConfig("ACP", "ff0000", "Staging", "00ff00"));

            Assert.Null(DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("Staging")));
        }

        [Fact]
        public void A_value_can_carry_both_an_icon_and_a_colour()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP"),
                ColorConfig("ACP", "ff0000"));

            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "ACP.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("ACP")));
            AssertColor("ff0000", merged, "ACP");
        }

        // ── the shapes either side can arrive in ────────────────────────────────────

        [Fact]
        public void Icons_for_values_the_colour_config_never_mentioned_are_still_emitted()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP", "EOC"),
                ColorConfig("Staging", "00ff00"));

            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "ACP.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("ACP")));
            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "EOC.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("EOC")));
            AssertColor("00ff00", merged, "Staging");
        }

        [Fact]
        public void With_no_icons_the_colour_config_passes_through_untouched()
        {
            var colors = ColorConfig("Staging", "00ff00");

            Assert.Same(colors, AutoIconset.MergeIconAndColorConfig(Uid, Group, null, colors));
            Assert.Same(colors, AutoIconset.MergeIconAndColorConfig(Uid, Group,
                new AutoIconset.Extraction(), colors));
        }

        [Fact]
        public void With_no_colours_the_icons_still_resolve_per_value()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP", "EOC"), null);

            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "ACP.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("ACP")));
            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "EOC.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("EOC")));
        }

        /// <summary>A single unclassified picture symbol — the one case where stamping every
        /// feature with the same icon IS correct, because the renderer says so.</summary>
        [Fact]
        public void A_lone_default_symbol_applies_to_every_feature()
        {
            var icons = new AutoIconset.Extraction();
            icons.Symbols.Add(new AutoIconset.PictureSymbol
            {
                Value = null,
                IsDefault = true,
                FileName = AutoIconset.DefaultIconFileName,
                Png = Png,
            });

            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, icons, null);

            Assert.Equal("ic", (string)merged["t"]);
            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, AutoIconset.DefaultIconFileName),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("anything")));
        }

        /// <summary>A default symbol alongside classified values is a FALLBACK, not a stamp: a
        /// value with its own entry must keep it.</summary>
        [Fact]
        public void A_default_symbol_does_not_override_a_classified_value()
        {
            var icons = Icons("ACP");
            icons.Symbols.Add(new AutoIconset.PictureSymbol
            {
                Value = null,
                IsDefault = true,
                FileName = AutoIconset.DefaultIconFileName,
                Png = Png,
            });

            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, icons,
                ColorConfig("Staging", "00ff00"));

            Assert.Equal("adv", (string)merged["t"]);
            Assert.Equal(AutoIconset.IconsetPath(Uid, Group, "ACP.png"),
                DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("ACP")));
            AssertColor("00ff00", merged, "Staging");
        }

        [Fact]
        public void The_driving_field_comes_from_the_colour_config_when_it_has_one()
        {
            var icons = Icons("ACP");
            icons.Field = "wrong_field";

            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, icons,
                ColorConfig("ACP", "ff0000"));

            Assert.Equal(Field, (string)merged["f"]);
        }

        [Fact]
        public void A_value_is_never_emitted_twice()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP"),
                ColorConfig("ACP", "ff0000"));

            var values = ((JArray)merged["vs"]).Select(e => (string)e["v"]).ToList();
            Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>The realistic shape: a handful of badges among many coloured categories,
        /// which is what the source layer in the field report actually published.</summary>
        [Fact]
        public void A_mixed_renderer_resolves_every_category_distinctly()
        {
            var merged = AutoIconset.MergeIconAndColorConfig(Uid, Group, Icons("ACP", "EOC"),
                ColorConfig("ACP", "ff0000", "EOC", "0000ff", "Staging", "00ff00",
                            "Shelter", "ffff00", "Other", "00ffff"));

            // Badges resolve to icons.
            Assert.NotNull(DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("ACP")));
            Assert.NotNull(DisplayStyleResolver.ResolveIconsetPath(merged, Attrs("EOC")));

            // The rest resolve to their own distinct colours, and to no icon.
            AssertColor("00ff00", merged, "Staging");
            AssertColor("ffff00", merged, "Shelter");
            AssertColor("00ffff", merged, "Other");
            Assert.All(new[] { "Staging", "Shelter", "Other" },
                v => Assert.Null(DisplayStyleResolver.ResolveIconsetPath(merged, Attrs(v))));
        }
    }
}
