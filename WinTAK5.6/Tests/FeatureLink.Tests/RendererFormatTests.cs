using System;
using System.Collections.Generic;
using System.Linq;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers both ArcGIS unique-value renderer layouts.
    ///
    /// <para><b>The field failure these exist to prevent.</b> Only the classic
    /// <c>uniqueValueInfos</c> array was read. Modern ArcGIS Online publishes
    /// <c>uniqueValueGroups[].classes[]</c> instead, so a layer that rendered twelve distinct
    /// symbols in ArcGIS produced <i>one</i> extracted symbol here — the <c>defaultSymbol</c> —
    /// which was then stamped on all twenty-nine features. No exception, no warning, no log line:
    /// the extractors simply saw a renderer with no categories and behaved perfectly correctly
    /// for that input. The only visible evidence was that every generated iconset zip contained
    /// exactly one file, <c>Other.png</c>.</para>
    ///
    /// <para>Both layouts are therefore asserted to produce identical results from identical
    /// content, so neither can regress without the other noticing.</para>
    /// </summary>
    public class RendererFormatTests
    {
        private const string Field = "kind";
        private static readonly string Png =
            Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });

        private static JObject Pms() => new JObject { ["type"] = "esriPMS", ["imageData"] = Png };

        private static JObject Sms(string hex)
        {
            var c = DisplayStyleResolver.ParseHexColor(hex, System.Drawing.Color.Black);
            return new JObject
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["size"] = 8,
                ["color"] = new JArray { c.R, c.G, c.B, 255 },
            };
        }

        /// <summary>Classic layout: a flat uniqueValueInfos array.</summary>
        private static JObject ClassicRenderer(params (string Value, JObject Symbol)[] cats)
        {
            var infos = new JArray();
            foreach (var c in cats)
                infos.Add(new JObject { ["value"] = c.Value, ["label"] = c.Value, ["symbol"] = c.Symbol });
            return new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = Field,
                ["uniqueValueInfos"] = infos,
            };
        }

        /// <summary>Modern layout: uniqueValueGroups -> classes -> values[][].</summary>
        private static JObject ModernRenderer(params (string Value, JObject Symbol)[] cats)
        {
            var classes = new JArray();
            foreach (var c in cats)
                classes.Add(new JObject
                {
                    ["label"] = c.Value,
                    ["symbol"] = c.Symbol,
                    ["values"] = new JArray { new JArray { c.Value } },
                });
            return new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = Field,
                ["uniqueValueGroups"] = new JArray { new JObject { ["heading"] = Field, ["classes"] = classes } },
            };
        }

        // ── the regression ──────────────────────────────────────────────────────────

        [Fact]
        public void The_modern_layout_yields_its_categories_rather_than_nothing()
        {
            var renderer = ModernRenderer(("ACP", Pms()), ("EOC", Pms()));

            var icons = AutoIconset.ExtractPictureSymbols(renderer);

            Assert.Equal(new[] { "ACP.png", "EOC.png" }, icons.Symbols.Select(s => s.FileName));
        }

        [Fact]
        public void The_modern_layout_yields_its_colours_rather_than_nothing()
        {
            var renderer = ModernRenderer(("Staging", Sms("00ff00")), ("Shelter", Sms("0000ff")));

            var result = AutoSymbology.Extract(renderer);

            Assert.False(result.IsEmpty);
            Assert.Equal(2, result.MarkerByValue.Count);
            Assert.True(result.MarkerByValue.ContainsKey("Staging"));
            Assert.True(result.MarkerByValue.ContainsKey("Shelter"));
        }

        /// <summary>The exact field shape: a couple of badges among several coloured categories,
        /// in the modern layout. Previously produced one icon and no colours.</summary>
        [Fact]
        public void A_modern_mixed_renderer_yields_both_icons_and_colours()
        {
            var renderer = ModernRenderer(
                ("ACP", Pms()), ("EOC", Pms()),
                ("Staging", Sms("00ff00")), ("Shelter", Sms("ffff00")), ("Other", Sms("00ffff")));

            var icons = AutoIconset.ExtractPictureSymbols(renderer);
            var colors = AutoSymbology.Extract(renderer);

            Assert.Equal(new[] { "ACP.png", "EOC.png" }, icons.Symbols.Select(s => s.FileName));
            Assert.Equal(3, colors.MarkerByValue.Count);
        }

        // ── the two layouts must agree ──────────────────────────────────────────────

        [Fact]
        public void Both_layouts_extract_the_same_icons_from_the_same_content()
        {
            var classic = AutoIconset.ExtractPictureSymbols(ClassicRenderer(("ACP", Pms()), ("EOC", Pms())));
            var modern = AutoIconset.ExtractPictureSymbols(ModernRenderer(("ACP", Pms()), ("EOC", Pms())));

            Assert.Equal(classic.Symbols.Select(s => s.FileName), modern.Symbols.Select(s => s.FileName));
            Assert.Equal(classic.Symbols.Select(s => s.Value), modern.Symbols.Select(s => s.Value));
            Assert.Equal(classic.Field, modern.Field);
        }

        [Fact]
        public void Both_layouts_extract_the_same_colours_from_the_same_content()
        {
            var classic = AutoSymbology.Extract(ClassicRenderer(("A", Sms("ff0000")), ("B", Sms("00ff00"))));
            var modern = AutoSymbology.Extract(ModernRenderer(("A", Sms("ff0000")), ("B", Sms("00ff00"))));

            Assert.Equal(classic.MarkerByValue.Keys.OrderBy(k => k), modern.MarkerByValue.Keys.OrderBy(k => k));
            Assert.Equal(classic.MarkerByValue["A"].Color, modern.MarkerByValue["A"].Color);
            Assert.Equal(classic.MarkerByValue["B"].Color, modern.MarkerByValue["B"].Color);
        }

        // ── shapes specific to the modern layout ────────────────────────────────────

        /// <summary>One class can cover several values sharing a symbol; each must resolve.</summary>
        [Fact]
        public void A_class_covering_several_values_yields_an_entry_for_each()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = Field,
                ["uniqueValueGroups"] = new JArray
                {
                    new JObject
                    {
                        ["classes"] = new JArray
                        {
                            new JObject
                            {
                                ["label"] = "Command",
                                ["symbol"] = Pms(),
                                ["values"] = new JArray
                                {
                                    new JArray { "ACP" }, new JArray { "EOC" }, new JArray { "ICP" },
                                },
                            },
                        },
                    },
                },
            };

            var icons = AutoIconset.ExtractPictureSymbols(renderer);

            Assert.Equal(new[] { "ACP", "EOC", "ICP" }, icons.Symbols.Select(s => s.Value));
            // Same label for all three, so §5.3 collision suffixes apply in renderer order.
            Assert.Equal(new[] { "Command.png", "Command_2.png", "Command_3.png" },
                icons.Symbols.Select(s => s.FileName));
        }

        /// <summary>A multi-field renderer composes its key with fieldDelimiter.</summary>
        [Fact]
        public void Multi_field_values_are_joined_with_the_renderer_delimiter()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = "a",
                ["field2"] = "b",
                ["fieldDelimiter"] = ", ",
                ["uniqueValueGroups"] = new JArray
                {
                    new JObject
                    {
                        ["classes"] = new JArray
                        {
                            new JObject
                            {
                                ["label"] = "Pair",
                                ["symbol"] = Pms(),
                                ["values"] = new JArray { new JArray { "X", "Y" } },
                            },
                        },
                    },
                },
            };

            Assert.Equal("X, Y", AutoSymbology.EnumerateValueEntries(renderer).Single().Value);
        }

        [Fact]
        public void A_renderer_carrying_both_layouts_reads_both_without_losing_either()
        {
            var renderer = ClassicRenderer(("Classic", Pms()));
            renderer["uniqueValueGroups"] = ModernRenderer(("Modern", Pms()))["uniqueValueGroups"];

            var values = AutoSymbology.EnumerateValueEntries(renderer).Select(e => e.Value).ToList();

            Assert.Contains("Classic", values);
            Assert.Contains("Modern", values);
        }

        [Fact]
        public void A_class_with_no_symbol_is_skipped_rather_than_throwing()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = Field,
                ["uniqueValueGroups"] = new JArray
                {
                    new JObject
                    {
                        ["classes"] = new JArray
                        {
                            new JObject { ["label"] = "NoSymbol", ["values"] = new JArray { new JArray { "X" } } },
                        },
                    },
                },
            };

            Assert.Empty(AutoSymbology.EnumerateValueEntries(renderer));
            Assert.True(AutoIconset.ExtractPictureSymbols(renderer).IsEmpty);
        }

        [Fact]
        public void An_empty_or_absent_renderer_yields_no_entries()
        {
            Assert.Empty(AutoSymbology.EnumerateValueEntries(null));
            Assert.Empty(AutoSymbology.EnumerateValueEntries(new JObject()));
        }

        /// <summary>The default symbol still applies when the modern layout classifies nothing —
        /// the pre-existing behaviour must survive.</summary>
        [Fact]
        public void A_default_symbol_is_still_used_when_there_are_no_classes()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = Field,
                ["uniqueValueGroups"] = new JArray(),
                ["defaultSymbol"] = Pms(),
            };

            var icons = AutoIconset.ExtractPictureSymbols(renderer);

            Assert.Equal(AutoIconset.DefaultIconFileName, icons.Symbols.Single().FileName);
        }
    }
}
