using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using FeatureLink.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Conformance suite for <see cref="AutoIconset"/>.
    ///
    /// <para>The bulk of this file is driven off
    /// <c>TAKPortal/test/fixtures/auto-iconset-golden-vectors.json</c> — the SAME fixture the
    /// TAK Portal and ATAK implementations conform to. That matters more than the usual
    /// unit-test rationale: these functions are not merely required to be correct, they are
    /// required to be <b>identical across four independent implementations</b>. A locally
    /// reasonable-looking change that diverges from the fixture would silently stop this
    /// device's icons resolving against everyone else's, and nothing on this machine would look
    /// wrong. Asserting against the shared vectors is the only way to catch that here.</para>
    /// </summary>
    public class AutoIconsetTests
    {
        // ── the shared fixture ──────────────────────────────────────────────────────

        private static readonly Lazy<JObject> Vectors = new Lazy<JObject>(() =>
        {
            string path = Path.Combine(RepoLocator.Root, "TAKPortal", "test", "fixtures",
                "auto-iconset-golden-vectors.json");
            return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        });

        private static IEnumerable<JObject> Cases(string section, string key = "cases") =>
            ((JArray)Vectors.Value[section][key]).OfType<JObject>();

        public static TheoryData<string, string> CanonicalizeCases => Load("canonicalizeUrl", "in", "out");
        public static TheoryData<string, string> GroupCases => Load("groupFor", "in", "out");
        public static TheoryData<string, string> FileNameCases => Load("fileNameFor", "in", "out");
        /// <summary>
        /// The fixture tags two vectors <c>dotNetMayDiffer</c>: U+0130 (capital I with dot above)
        /// lowercases to the two code points <c>i</c> + U+0307 in Java/JS/Python, but .NET's
        /// <c>ToLowerInvariant</c> maps it to a single <c>i</c> on some runtimes. The fixture's own
        /// comment records this as a KNOWN, deliberate divergence that cannot affect a real UID,
        /// because the only string canonicalization folds is the host, and DNS/IDNA host labels
        /// are ASCII by the time a URL is parsed. Those cases are skipped here rather than
        /// asserted against a value this platform is documented not to produce — and
        /// <see cref="Case_folding_is_never_applied_to_the_path_or_the_layer_name"/> guards the
        /// thing that would actually break if someone widened the fold.
        /// </summary>
        public static TheoryData<string, string> CaseFoldingCases
        {
            get
            {
                var data = new TheoryData<string, string>();
                foreach (var c in Cases("caseFolding"))
                {
                    if ((bool?)c["dotNetMayDiffer"] == true) continue;
                    data.Add((string)c["in"], (string)c["out"]);
                }
                return data;
            }
        }

        private static TheoryData<string, string> Load(string section, string inKey, string outKey)
        {
            var data = new TheoryData<string, string>();
            foreach (var c in Cases(section)) data.Add((string)c[inKey], (string)c[outKey]);
            return data;
        }

        // ── §2 canonicalization ─────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(CanonicalizeCases))]
        public void Canonicalize_matches_the_shared_golden_vectors(string input, string expected)
        {
            Assert.Equal(expected, AutoIconset.Canonicalize(input));
        }

        /// <summary>§2.3 — a Web Map link must be REJECTED, never hashed raw. Hashing it would
        /// mint a UID no other platform can reproduce, which is worse than failing.</summary>
        [Fact]
        public void Canonicalize_rejects_everything_the_fixture_says_must_throw()
        {
            foreach (var c in Cases("canonicalizeUrl", "mustThrow"))
            {
                string input = (string)c["in"];
                Assert.ThrowsAny<ArgumentException>(() => AutoIconset.Canonicalize(input));
            }
        }

        [Theory]
        [MemberData(nameof(CaseFoldingCases))]
        public void Host_case_folding_matches_the_fixture(string input, string expected)
        {
            string url = "https://" + input + "/a/arcgis/rest/services/X/FeatureServer/0";
            Assert.StartsWith("https://" + expected + "/", AutoIconset.Canonicalize(url), StringComparison.Ordinal);
        }

        /// <summary>
        /// The real risk behind the U+0130 divergence is not the host — it is someone later
        /// "improving" the code by folding the PATH or the LAYER NAME too. Both feed the UID and
        /// the group, and folding either would desynchronise this platform from every other one.
        ///
        /// <para>Asserted with an ASCII path, because that is the part the contract actually
        /// defines. A non-ASCII path is NOT covered by the golden vectors, and .NET percent-encodes
        /// it via <c>Uri.AbsolutePath</c> — see the note on <see cref="AutoIconset.Canonicalize"/>.
        /// Asserting a value the fixture does not specify would be inventing a contract.</para>
        /// </summary>
        [Fact]
        public void Case_folding_is_never_applied_to_the_path_or_the_layer_name()
        {
            string canonical = AutoIconset.Canonicalize(
                "https://EXAMPLE.com/MixedCase/arcgis/rest/services/CamelName/FeatureServer/0");

            // Host folded, path untouched.
            Assert.StartsWith("https://example.com/", canonical, StringComparison.Ordinal);
            Assert.Contains("/MixedCase/", canonical, StringComparison.Ordinal);
            Assert.Contains("/CamelName/", canonical, StringComparison.Ordinal);

            // The layer name is SANITIZED (§5.1: anything outside [A-Za-z0-9 _-] becomes '_'),
            // but never CASE-FOLDED — ASCII case survives verbatim, which is the invariant that
            // matters, because folding it would change the group string on other platforms.
            Assert.Equal("CamelCase Layer Icons", AutoIconset.GroupFor("CamelCase Layer"));
            Assert.Equal("UPPER lower Icons", AutoIconset.GroupFor("UPPER lower"));

            // Non-ASCII is replaced, not folded and not preserved — one underscore per char.
            Assert.Equal("_STANBUL Incidents Icons", AutoIconset.GroupFor("İSTANBUL Incidents"));
            Assert.Equal("M_NCHEN Icons", AutoIconset.GroupFor("MÜNCHEN"));
        }

        // ── §4 UID ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Uid_matches_every_fixture_vector()
        {
            foreach (var c in Cases("uidFor"))
            {
                string url = (string)c["canonicalUrl"];
                string field = (string)c["field"] ?? string.Empty;
                string expected = (string)c["out"];
                Assert.Equal(expected, AutoIconset.Uid(url, field));
            }
        }

        /// <summary>The spec's own published test vector (§4), which every implementation is
        /// required to carry standalone.</summary>
        [Fact]
        public void Uid_matches_the_published_spec_test_vector()
        {
            string uid = AutoIconset.Uid(
                "https://services1.arcgis.com/abc/arcgis/rest/services/Damage/FeatureServer/0",
                "damage_level");

            Assert.Equal("9aa866980d8078ab2a4cbb84b5ad4de9b4c6af6d3192957e15b8ce459dfd2ad9", uid);
        }

        /// <summary>Field-name case is significant: these are different ArcGIS fields.</summary>
        [Fact]
        public void Uid_does_not_fold_field_name_case()
        {
            const string url = "https://services1.arcgis.com/abc/arcgis/rest/services/Damage/FeatureServer/0";
            Assert.NotEqual(AutoIconset.Uid(url, "DAMAGE_LEVEL"), AutoIconset.Uid(url, "damage_level"));
        }

        /// <summary>
        /// The Turkish-locale trap. Under tr-TR, "I".ToLower() is the dotless "ı", so a
        /// culture-sensitive fold would change the hashed URL and desynchronise this device's
        /// icons from every other platform's — visible only on Turkish machines, and only as
        /// "the icons don't match".
        /// </summary>
        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("tr-TR")]
        [InlineData("az-Latn-AZ")]
        [InlineData("ar-SA")]
        public void Uid_and_canonicalization_are_culture_invariant(string culture)
        {
            string canonical = null, uid = null, group = null;

            CultureScope.With(culture, () =>
            {
                canonical = AutoIconset.Canonicalize(
                    "https://SERVICES1.ArcGIS.com/ISTANBUL/arcgis/rest/services/Damage/FeatureServer");
                uid = AutoIconset.Uid(canonical, "DAMAGE_LEVEL");
                group = AutoIconset.GroupFor("ISTANBUL Incidents");
            });

            // Path case is preserved; only scheme and host fold.
            Assert.Equal(
                "https://services1.arcgis.com/ISTANBUL/arcgis/rest/services/Damage/FeatureServer/0",
                canonical);
            Assert.Equal(AutoIconset.Uid(canonical, "DAMAGE_LEVEL"), uid);
            Assert.Equal("ISTANBUL Incidents Icons", group);
        }

        // ── §5 naming ───────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(GroupCases))]
        public void GroupFor_matches_the_shared_golden_vectors(string input, string expected)
        {
            Assert.Equal(expected, AutoIconset.GroupFor(input));
        }

        [Theory]
        [MemberData(nameof(FileNameCases))]
        public void FileName_matches_the_shared_golden_vectors(string input, string expected)
        {
            Assert.Equal(expected, AutoIconset.FileName(input));
        }

        [Fact]
        public void Dedupe_matches_every_fixture_sequence()
        {
            foreach (var sequence in Cases("dedupe", "sequences"))
            {
                var labels = ((JArray)sequence["labels"]).Select(t => (string)t).ToList();
                var expected = ((JArray)sequence["out"]).Select(t => (string)t).ToList();

                var taken = new HashSet<string>(StringComparer.Ordinal);
                var actual = labels.Select(l => AutoIconset.Dedupe(AutoIconset.FileName(l), taken)).ToList();

                Assert.Equal(expected, actual);
            }
        }

        /// <summary>§5.1 — the 60-char cap is applied ONCE, to the base, before the suffix. The
        /// fixture carries a dedicated section for this because re-truncating downstream was a
        /// real cross-implementation defect.</summary>
        [Fact]
        public void Group_base_is_capped_once_and_the_suffix_is_never_truncated()
        {
            var spec = (JObject)Vectors.Value["c39DoubleTruncation"];
            int maxBase = (int)spec["maxBaseChars"];
            int maxGroup = (int)spec["maxGroupChars"];

            string group = AutoIconset.GroupFor(new string('A', 200));

            Assert.True(group.Length <= maxGroup, $"group was {group.Length} chars, cap is {maxGroup}");
            Assert.EndsWith(" Icons", group, StringComparison.Ordinal);
            Assert.Equal(maxBase, group.Length - " Icons".Length);
        }

        [Fact]
        public void IconsetPath_matches_the_fixture()
        {
            var c = Cases("usericonPath").First();
            Assert.Equal((string)c["out"],
                AutoIconset.IconsetPath((string)c["uid"], (string)c["group"], (string)c["filename"]));
        }

        /// <summary>Whatever we generate must survive our own outbound guard, or the plugin
        /// silently drops its own icons at the CoT boundary.</summary>
        [Theory]
        [InlineData("Damage (2024)")]
        [InlineData("A/B\\C")]
        [InlineData("naïve <hazard> & \"quotes\"")]
        public void Generated_paths_pass_the_outbound_iconset_guard(string layerName)
        {
            string uid = AutoIconset.Uid("https://h/a/arcgis/rest/services/X/FeatureServer/0", "f");
            string path = AutoIconset.IconsetPath(uid, AutoIconset.GroupFor(layerName),
                AutoIconset.FileName(layerName));

            Assert.True(UrlGuard.IsSafeIconsetPath(path), "generated path was rejected by UrlGuard: " + path);
        }

        // ── §3 renderer extraction ──────────────────────────────────────────────────

        private static JObject Pms(string base64) =>
            new JObject { ["type"] = "esriPMS", ["imageData"] = base64 };

        private static readonly string TinyPng = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });

        [Fact]
        public void Extract_takes_picture_symbols_in_renderer_order_and_skips_simple_markers()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = "damage_level",
                ["uniqueValueInfos"] = new JArray
                {
                    new JObject { ["value"] = "Minor", ["label"] = "Minor", ["symbol"] = Pms(TinyPng) },
                    new JObject { ["value"] = "Plain", ["label"] = "Plain",
                                  ["symbol"] = new JObject { ["type"] = "esriSMS" } },
                    new JObject { ["value"] = "Major", ["label"] = "Major", ["symbol"] = Pms(TinyPng) },
                },
            };

            var extracted = AutoIconset.ExtractPictureSymbols(renderer);

            Assert.Equal("damage_level", extracted.Field);
            Assert.Equal(new[] { "Minor.png", "Major.png" }, extracted.Symbols.Select(s => s.FileName));
        }

        [Fact]
        public void An_unlabelled_default_symbol_becomes_Other_png()
        {
            var renderer = new JObject { ["type"] = "uniqueValue", ["defaultSymbol"] = Pms(TinyPng) };

            var extracted = AutoIconset.ExtractPictureSymbols(renderer);

            var only = Assert.Single(extracted.Symbols);
            Assert.Equal(AutoIconset.DefaultIconFileName, only.FileName);
            Assert.True(only.IsDefault);
        }

        [Fact]
        public void A_labelled_default_symbol_is_named_from_its_label()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["defaultLabel"] = "Unclassified",
                ["defaultSymbol"] = Pms(TinyPng),
            };

            Assert.Equal("Unclassified.png", AutoIconset.ExtractPictureSymbols(renderer).Symbols.Single().FileName);
        }

        [Fact]
        public void A_picture_symbol_with_no_image_data_is_skipped_rather_than_emitted_empty()
        {
            var renderer = new JObject
            {
                ["type"] = "simple",
                ["symbol"] = new JObject { ["type"] = "esriPMS" },
            };

            Assert.True(AutoIconset.ExtractPictureSymbols(renderer).IsEmpty);
        }

        [Fact]
        public void CIM_symbols_are_counted_so_a_thin_result_is_explainable()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = "kind",
                ["uniqueValueInfos"] = new JArray
                {
                    new JObject { ["value"] = "A", ["label"] = "A",
                                  ["symbol"] = new JObject { ["type"] = "CIMSymbolReference" } },
                },
            };

            var extracted = AutoIconset.ExtractPictureSymbols(renderer);

            Assert.True(extracted.IsEmpty);
            Assert.Equal(1, extracted.UnsupportedSymbols);
        }

        [Fact]
        public void A_field_override_wins_over_the_renderer_field()
        {
            var renderer = new JObject { ["type"] = "uniqueValue", ["field1"] = "a" };
            Assert.Equal("b", AutoIconset.ExtractPictureSymbols(renderer, "b").Field);
        }

        // ── §7 iconset.xml and the zip ──────────────────────────────────────────────

        /// <summary>The regression guard that matters most in this file. A stray attribute on
        /// &lt;icon&gt; aborts ATAK's strict parse of the WHOLE document, after which it discards
        /// our uid and hashes the zip — breaking cross-platform matching silently.</summary>
        [Fact]
        public void Icon_elements_carry_only_a_name_attribute()
        {
            string xml = AutoIconset.BuildIconsetXml("abc", "Damage Icons", new[] { "Minor.png", "Major.png" });

            Assert.Contains("<icon name=\"Minor.png\"/>", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("group=", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("type2525b", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("skipResize", xml, StringComparison.Ordinal);
        }

        [Fact]
        public void Iconset_xml_declares_the_uid_group_and_spec_version()
        {
            string xml = AutoIconset.BuildIconsetXml("deadbeef", "Damage Icons", new[] { "Minor.png" });

            Assert.Contains("uid=\"deadbeef\"", xml, StringComparison.Ordinal);
            Assert.Contains("name=\"Damage Icons\"", xml, StringComparison.Ordinal);
            Assert.Contains("defaultGroup=\"Damage Icons\"", xml, StringComparison.Ordinal);
            Assert.Contains("version=\"1\"", xml, StringComparison.Ordinal);
        }

        [Fact]
        public void Iconset_xml_escapes_xml_significant_characters()
        {
            string xml = AutoIconset.BuildIconsetXml("u", "A & B \"C\" Icons", new[] { "x.png" });

            Assert.Contains("A &amp; B &quot;C&quot; Icons", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("A & B", xml, StringComparison.Ordinal);
        }

        /// <summary>ATAK derives the group from the entry's first path segment, so a backslash
        /// here — which <c>Path.Combine</c> would produce on Windows — yields one bogus group and
        /// no icon ever resolves.</summary>
        [Fact]
        public void Zip_entries_use_forward_slashes_and_a_single_group_level()
        {
            var symbols = new[]
            {
                new AutoIconset.PictureSymbol { FileName = "Minor.png", Png = new byte[] { 1, 2, 3 } },
                new AutoIconset.PictureSymbol { FileName = "Major.png", Png = new byte[] { 4, 5 } },
            };

            byte[] zipBytes = AutoIconset.BuildZipBytes("uid1", "Damage Icons", symbols);

            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();

                Assert.Contains("iconset.xml", names);
                Assert.Contains("Damage Icons/Minor.png", names);
                Assert.Contains("Damage Icons/Major.png", names);
                Assert.DoesNotContain(names, n => n.Contains("\\"));
                Assert.All(names.Where(n => n != "iconset.xml"),
                    n => Assert.Equal(1, n.Count(ch => ch == '/')));
            }
        }

        [Fact]
        public void Zip_round_trips_the_png_bytes_unchanged()
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9 };
            byte[] zipBytes = AutoIconset.BuildZipBytes("uid1", "G Icons",
                new[] { new AutoIconset.PictureSymbol { FileName = "A.png", Png = png } });

            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            using (var stream = zip.GetEntry("G Icons/A.png").Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                Assert.Equal(png, buffer.ToArray());
            }
        }

        // ── config synthesis: producer and consumer must agree ──────────────────────

        /// <summary>The produced config is fed straight back through the resolver the download
        /// path actually uses, so a mistake in the key names cannot pass unnoticed.</summary>
        [Fact]
        public void Generated_config_round_trips_through_the_resolver()
        {
            var renderer = new JObject
            {
                ["type"] = "uniqueValue",
                ["field1"] = "damage_level",
                ["uniqueValueInfos"] = new JArray
                {
                    new JObject { ["value"] = "Minor", ["label"] = "Minor", ["symbol"] = Pms(TinyPng) },
                    new JObject { ["value"] = "Major", ["label"] = "Major", ["symbol"] = Pms(TinyPng) },
                },
                ["defaultSymbol"] = Pms(TinyPng),
            };

            var extracted = AutoIconset.ExtractPictureSymbols(renderer);
            string uid = AutoIconset.Uid("https://h/a/arcgis/rest/services/X/FeatureServer/0", "damage_level");
            string group = AutoIconset.GroupFor("Damage");

            JObject sym = AutoIconset.BuildIconSymConfig(uid, group, extracted);

            string resolved = DisplayStyleResolver.ResolveIconsetPath(sym,
                new Dictionary<string, string> { ["damage_level"] = "Major" });

            Assert.Equal(AutoIconset.IconsetPath(uid, group, "Major.png"), resolved);
        }

        [Fact]
        public void A_single_symbol_renderer_produces_a_plain_icon_config()
        {
            var renderer = new JObject { ["type"] = "simple", ["symbol"] = Pms(TinyPng) };
            var extracted = AutoIconset.ExtractPictureSymbols(renderer);

            JObject sym = AutoIconset.BuildIconSymConfig("uid1", "G Icons", extracted);

            Assert.Equal("ic", (string)sym["t"]);
            Assert.Equal("uid1/G Icons/Other.png", (string)sym["up"]);
        }

        [Fact]
        public void An_empty_extraction_produces_no_config_rather_than_an_empty_one()
        {
            Assert.Null(AutoIconset.BuildIconSymConfig("u", "g", new AutoIconset.Extraction()));
        }
    }
}
