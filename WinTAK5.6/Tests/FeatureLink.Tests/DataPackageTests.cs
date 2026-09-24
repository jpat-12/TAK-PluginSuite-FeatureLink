using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers the data-package feature: naming, what goes in, and the manifest.
    ///
    /// <para>The manifest is the part worth the most tests. It is the only piece of this feature
    /// another TAK client has to agree with, and a malformed one fails on the <b>recipient's</b>
    /// machine — where nobody can see it — while the sender's status bar still says the package
    /// went out. Everything here runs without the WinTAK SDK, which is why the package is written
    /// by <see cref="DataPackageWriter"/> rather than by the host's own builder.</para>
    /// </summary>
    public class DataPackageTests
    {
        private const string UidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string UidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        /// <summary>Stands in for a generated iconset on disk: the two files a minimal one holds.
        /// The real reader returns exactly this shape — <c>iconset.xml</c> beside a group folder
        /// of PNGs.</summary>
        private static readonly Func<string, IReadOnlyList<string>> Iconset2 =
            uid => new[] { "iconset.xml", "Grp/a.png" };

        /// <summary>No iconset has been generated for this uid.</summary>
        private static readonly Func<string, IReadOnlyList<string>> NoIconset = uid => null;

        private static readonly DateTime Noon = new DateTime(2026, 9, 20, 12, 34, 0, DateTimeKind.Local);

        // ── naming ──────────────────────────────────────────────────────────────

        [Fact]
        public void One_layer_is_named_after_it()
        {
            Assert.Equal("FeatureLink-Ground_Teams-20260920-1234",
                DataPackageBuilder.AutoName(new[] { "Ground Teams" }, Noon));
        }

        [Fact]
        public void Several_layers_are_named_by_count_not_by_listing_them()
        {
            string name = DataPackageBuilder.AutoName(
                new[] { "Ground Teams", "Air Assets", "ICP" }, Noon);

            Assert.Equal("FeatureLink-3-layers-20260920-1234", name);
        }

        [Fact]
        public void The_name_always_carries_a_timestamp_so_two_sends_are_distinguishable()
        {
            string noon = DataPackageBuilder.AutoName(new[] { "Teams" }, Noon);
            string later = DataPackageBuilder.AutoName(new[] { "Teams" }, Noon.AddHours(1));

            Assert.NotEqual(noon, later);
        }

        [Fact]
        public void An_empty_or_unnameable_selection_still_produces_a_usable_name()
        {
            Assert.Equal("FeatureLink-layers-20260920-1234",
                DataPackageBuilder.AutoName(null, Noon));
            Assert.Equal("FeatureLink-layers-20260920-1234",
                DataPackageBuilder.AutoName(new[] { "   ", null }, Noon));
            // A name of nothing but illegal characters sanitizes away entirely.
            Assert.Equal("FeatureLink-layers-20260920-1234",
                DataPackageBuilder.AutoName(new[] { "///" }, Noon));
        }

        [Theory]
        [InlineData("Ground Teams", "Ground_Teams")]
        [InlineData("a/b\\c:d", "a_b_c_d")]
        [InlineData("  padded  ", "padded")]
        [InlineData("dots...", "dots")]
        [InlineData("multi____underscores", "multi_underscores")]
        public void Sanitize_produces_a_legal_file_stem(string raw, string expected)
        {
            Assert.Equal(expected, DataPackageBuilder.Sanitize(raw, 48));
        }

        /// <summary>A reserved device name is illegal as a stem even with an extension, so
        /// <c>CON.zip</c> cannot be created at all.</summary>
        [Theory]
        [InlineData("CON")]
        [InlineData("nul")]
        [InlineData("LPT1")]
        public void Sanitize_displaces_windows_device_names(string reserved)
        {
            string result = DataPackageBuilder.Sanitize(reserved, 48);

            Assert.NotEqual(reserved, result, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(reserved, result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Sanitize_caps_the_length_without_leaving_a_trailing_separator()
        {
            string result = DataPackageBuilder.Sanitize(new string('x', 40) + "        y", 44);

            Assert.True(result.Length <= 44);
            Assert.DoesNotContain(result.Last(), new[] { ' ', '_', '-', '.' });
        }

        // ── iconset references ──────────────────────────────────────────────────

        [Fact]
        public void An_iconset_path_yields_its_uid()
        {
            Assert.Equal(UidA, DataPackageBuilder.UidFromIconsetPath(UidA + "/MyGroup/icon.png"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("https://example.com/arcgis/rest")]          // three segments, first not a digest
        [InlineData("abc/group/icon.png")]                       // too short to be a digest
        [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/g/i.png")] // upper case
        [InlineData("group/icon.png")]                           // two segments
        [InlineData("a/b/c/d")]                                  // four segments
        public void Anything_that_is_not_an_iconset_path_is_rejected(string path)
        {
            Assert.Null(DataPackageBuilder.UidFromIconsetPath(path));
        }

        [Fact]
        public void A_uid_is_found_wherever_it_sits_in_the_config()
        {
            // Shaped like a real merged icon+colour config: one path at the top level and one per
            // value entry. The extractor walks the document rather than keying off field names,
            // so a new key cannot silently drop icons out of a package.
            string config = "{\"up\":\"" + UidA + "/Grp/default.png\","
                + "\"vals\":[{\"v\":\"TEAM\",\"up\":\"" + UidA + "/Grp/team.png\"},"
                + "{\"v\":\"ICP\",\"up\":\"" + UidB + "/Grp/icp.png\"}]}";

            var uids = DataPackageBuilder.ExtractIconsetUids(config);

            Assert.Equal(new[] { UidA, UidB }, uids.ToArray());
        }

        [Fact]
        public void An_array_of_configs_is_searched_too()
        {
            // The dock pane wraps a layer's explicit and auto-derived configs in one array.
            string wrapped = "[{\"up\":\"" + UidA + "/Grp/a.png\"},{\"up\":\"" + UidB + "/Grp/b.png\"}]";

            Assert.Equal(2, DataPackageBuilder.ExtractIconsetUids(wrapped).Count);
        }

        [Fact]
        public void A_corrupt_config_costs_icons_never_the_package()
        {
            Assert.Empty(DataPackageBuilder.ExtractIconsetUids("{not json"));
            Assert.Empty(DataPackageBuilder.ExtractIconsetUids(null));
            Assert.Empty(DataPackageBuilder.ExtractIconsetUids("   "));
        }

        // ── planning ────────────────────────────────────────────────────────────

        private static DataPackageBuilder.LayerPlanInput Layer(
            string name, string url, string displayConfig = null, params string[] recordedUids)
        {
            return new DataPackageBuilder.LayerPlanInput
            {
                Name = name,
                Url = url,
                ConfigJson = "{\"v\":1,\"url\":\"" + url + "\"}",
                DisplayConfigJson = displayConfig,
                IconsetUids = recordedUids,
            };
        }

        [Fact]
        public void Each_layer_contributes_its_config()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0"),
                        Layer("ICP", "https://h/b/FeatureServer/0") },
                NoIconset);

            Assert.Equal(2, plan.LayerCount);
            Assert.All(plan.Entries, e => Assert.StartsWith("featurelink/", e.PackagePath));
            Assert.All(plan.Entries, e => Assert.EndsWith(".featurelinkshare", e.PackagePath));
        }

        /// <summary>
        /// An iconset travels EXPANDED, as its own files, not as a nested zip.
        ///
        /// <para>Shipping the generated zip inside the package was the original shape and it never
        /// worked: ATAK does not unpack a zip within a package, so the whole thing was filed under
        /// <c>atak/attachments/</c> and no icons were installed. A working iconset appears as
        /// <c>iconset.xml</c> beside its group folder of PNGs.</para>
        /// </summary>
        [Fact]
        public void A_layers_generated_iconset_travels_expanded_not_as_a_nested_zip()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0", null, UidA) },
                Iconset2);

            var paths = plan.Entries
                .Where(e => e.Kind == DataPackageBuilder.EntryKind.Iconset)
                .Select(e => e.PackagePath).ToList();

            Assert.Equal(new[] { "iconsets/" + UidA + "/iconset.xml",
                                 "iconsets/" + UidA + "/Grp/a.png" }, paths);
            Assert.DoesNotContain(paths, path => path.EndsWith(".zip", StringComparison.Ordinal));

            // One iconset, however many files it expands into.
            Assert.Equal(1, plan.IconsetCount);
        }

        /// <summary>Each expanded file names the entry inside the generated zip it is copied
        /// from, so the writer can pull it across without unpacking to disk.</summary>
        [Fact]
        public void An_expanded_iconset_file_points_back_into_the_generated_zip()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0", null, UidA) },
                Iconset2);

            var first = plan.Entries.First(e => e.Kind == DataPackageBuilder.EntryKind.Iconset);

            Assert.Equal("iconset.xml", first.SourceZipEntry);
            Assert.EndsWith(UidA + ".zip", first.SourcePath);
        }

        /// <summary>The recorded UID is what survives a restart; the display config only exists for
        /// a layer synced this session. Both are read, so neither source alone can lose icons.</summary>
        [Fact]
        public void Recorded_uids_and_config_references_are_both_used()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0",
                              "{\"up\":\"" + UidB + "/Grp/b.png\"}", UidA) },
                Iconset2);

            var packaged = plan.Entries
                .Where(e => e.Kind == DataPackageBuilder.EntryKind.Iconset)
                .Select(e => e.PackagePath).ToList();

            Assert.Contains("iconsets/" + UidA + "/iconset.xml", packaged);
            Assert.Contains("iconsets/" + UidB + "/iconset.xml", packaged);
            Assert.Equal(2, plan.IconsetCount);
        }

        [Fact]
        public void An_iconset_shared_by_two_layers_goes_in_once()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0", null, UidA),
                        Layer("ICP", "https://h/b/FeatureServer/0", null, UidA) },
                Iconset2);

            Assert.Equal(2, plan.LayerCount);
            Assert.Equal(1, plan.IconsetCount);
        }

        [Fact]
        public void An_iconset_that_was_never_generated_is_reported_not_packaged()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0", null, UidA) },
                NoIconset);

            Assert.Equal(0, plan.IconsetCount);
            Assert.Equal(new[] { UidA }, plan.MissingIconsets.ToArray());
            Assert.Contains("not generated yet", DataPackageBuilder.Describe(plan));
        }

        /// <summary>Two layers can carry the same display name — the same service published twice,
        /// or two long names truncated to the same 48 characters. A duplicate entry path would mean
        /// one silently overwriting the other inside the zip.</summary>
        [Fact]
        public void Two_layers_with_the_same_name_get_distinct_entries()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0"),
                        Layer("Teams", "https://h/b/FeatureServer/0") },
                NoIconset);

            var paths = plan.Entries.Select(e => e.PackagePath).ToList();
            Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Two_layers_with_the_same_name_and_url_still_get_distinct_entries()
        {
            // Belt and braces: even if the URL hash matched, the counter must separate them.
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0"),
                        Layer("Teams", "https://h/a/FeatureServer/0") },
                NoIconset);

            var paths = plan.Entries.Select(e => e.PackagePath).ToList();
            Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void A_layer_with_no_config_is_skipped_rather_than_packaged_empty()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { new DataPackageBuilder.LayerPlanInput { Name = "Teams", Url = "https://h/a" },
                        null },
                NoIconset);

            Assert.Empty(plan.Entries);
            Assert.Equal("nothing selected", DataPackageBuilder.Describe(plan));
        }

        [Theory]
        [InlineData(1, 0, "1 layer, no iconsets")]
        [InlineData(2, 1, "2 layers, 1 iconset")]
        [InlineData(2, 3, "2 layers, 3 iconsets")]
        public void Describe_reads_naturally(int layers, int iconsets, string expected)
        {
            var inputs = Enumerable.Range(0, layers)
                .Select(i => Layer("L" + i, "https://h/" + i + "/FeatureServer/0", null,
                    Enumerable.Range(0, i == 0 ? iconsets : 0)
                        .Select(n => n.ToString("x").PadLeft(64, '0')).ToArray()))
                .ToList();

            Assert.Equal(expected, DataPackageBuilder.Describe(
                DataPackageBuilder.Plan(inputs, Iconset2)));
        }

        // ── selected features ───────────────────────────────────────────────────

        private static DataPackageBuilder.LayerPlanInput LayerWithFeatures(
            string name, string url, params string[] uids)
        {
            var layer = Layer(name, url);
            layer.Features = uids.Select(u => new DataPackageBuilder.FeatureContent
            {
                Uid = u,
                CotXml = "<event uid=\"" + u + "\"/>",
            }).ToList();
            return layer;
        }

        [Fact]
        public void Selected_features_travel_as_cot_alongside_the_layer_config()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "f1", "f2") },
                NoIconset);

            Assert.Equal(1, plan.LayerCount);
            Assert.Equal(2, plan.FeatureCount);
            Assert.All(plan.Entries.Where(e => e.Kind == DataPackageBuilder.EntryKind.Feature),
                e => Assert.StartsWith("cot/", e.PackagePath));
            Assert.All(plan.Entries.Where(e => e.Kind == DataPackageBuilder.EntryKind.Feature),
                e => Assert.EndsWith(".cot", e.PackagePath));
        }

        /// <summary>A layer with no listed features means "the whole layer" — the recipient
        /// downloads it from ArcGIS, which is the original share behaviour.</summary>
        [Fact]
        public void A_layer_with_no_listed_features_carries_only_its_config()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0") }, NoIconset);

            Assert.Equal(1, plan.LayerCount);
            Assert.Equal(0, plan.FeatureCount);
        }

        [Fact]
        public void A_feature_with_no_cot_is_left_out_rather_than_packaged_empty()
        {
            var layer = Layer("Teams", "https://h/a/FeatureServer/0");
            layer.Features = new[]
            {
                new DataPackageBuilder.FeatureContent { Uid = "good", CotXml = "<event/>" },
                new DataPackageBuilder.FeatureContent { Uid = "blank", CotXml = "   " },
                new DataPackageBuilder.FeatureContent { Uid = null, CotXml = "<event/>" },
            };

            var plan = DataPackageBuilder.Plan(new[] { layer }, NoIconset);

            Assert.Equal(1, plan.FeatureCount);
        }

        /// <summary>The same feature reached through two layers travels once — a duplicate zip
        /// entry would mean one silently overwriting the other.</summary>
        [Fact]
        public void The_same_feature_selected_from_two_layers_travels_once()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "shared"),
                        LayerWithFeatures("ICP", "https://h/b/FeatureServer/0", "shared") },
                NoIconset);

            Assert.Equal(2, plan.LayerCount);
            Assert.Equal(1, plan.FeatureCount);
        }

        [Fact]
        public void Describe_leads_with_the_feature_count_when_features_were_selected()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "f1", "f2") },
                NoIconset);

            Assert.Equal("2 features from 1 layer, no iconsets", DataPackageBuilder.Describe(plan));
        }

        [Fact]
        public void A_packaged_feature_is_written_into_the_zip_as_its_cot()
        {
            using (var dir = new TempDir())
            {
                var plan = DataPackageBuilder.Plan(
                    new[] { LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "f1") },
                    NoIconset);

                var result = DataPackageWriter.Write(plan, "Pkg", Path.Combine(dir.Path, "out.zip"));
                var entries = ReadZip(result.Path);

                Assert.Equal("<event uid=\"f1\"/>", entries["cot/f1.cot"]);
            }
        }

        // ── import ordering ───────────────────────────────────

        /// <summary>
        /// Icons must be installed before the markers that reference them are created.
        ///
        /// <para>A marker resolves its icon at creation and keeps it — the same rule that made
        /// replacing an installed iconset so awkward. An iconset arriving after its CoT is
        /// therefore too late: the markers are already on the recipient's map wearing defaults and
        /// nothing re-resolves them. Entries used to be emitted config, features, iconsets —
        /// exactly backwards.</para>
        /// </summary>
        [Fact]
        public void Every_iconset_is_ordered_before_every_feature()
        {
            var layer = LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "f1", "f2");
            layer.IconsetUids = new[] { UidA };

            var plan = DataPackageBuilder.Plan(new[] { layer }, Iconset2);

            int lastIconset = plan.Entries.FindLastIndex(
                e => e.Kind == DataPackageBuilder.EntryKind.Iconset);
            int firstFeature = plan.Entries.FindIndex(
                e => e.Kind == DataPackageBuilder.EntryKind.Feature);

            Assert.True(lastIconset >= 0, "no iconset in the plan");
            Assert.True(firstFeature >= 0, "no feature in the plan");
            Assert.True(lastIconset < firstFeature,
                "iconset at " + lastIconset + " comes after the first feature at " + firstFeature);
        }

        /// <summary>The per-layer loop interleaves, so layer A's features would otherwise precede
        /// layer B's iconsets. The whole plan is reordered, not each layer's slice.</summary>
        [Fact]
        public void Ordering_holds_across_several_layers()
        {
            var a = LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "f1");
            a.IconsetUids = new[] { UidA };
            var b = LayerWithFeatures("ICP", "https://h/b/FeatureServer/0", "f2");
            b.IconsetUids = new[] { UidB };

            var plan = DataPackageBuilder.Plan(new[] { a, b }, Iconset2);

            int lastIconset = plan.Entries.FindLastIndex(
                e => e.Kind == DataPackageBuilder.EntryKind.Iconset);
            int firstFeature = plan.Entries.FindIndex(
                e => e.Kind == DataPackageBuilder.EntryKind.Feature);

            Assert.Equal(2, plan.IconsetCount);
            Assert.True(lastIconset < firstFeature,
                "an iconset for the second layer landed after the first layer's features");
        }

        /// <summary>Reordering must not shuffle within a group: OrderBy is stable, List.Sort is
        /// not, and the features carry a meaningful build order.</summary>
        [Fact]
        public void Features_keep_the_order_they_were_selected_in()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { LayerWithFeatures("Teams", "https://h/a/FeatureServer/0",
                                          "f1", "f2", "f3", "f4", "f5") },
                Iconset2);

            var features = plan.Entries
                .Where(e => e.Kind == DataPackageBuilder.EntryKind.Feature)
                .Select(e => e.Uid).ToList();

            Assert.Equal(new[] { "f1", "f2", "f3", "f4", "f5" }, features);
        }

        /// <summary>The zip must carry the same order as the manifest, since an importer may
        /// honour either one.</summary>
        [Fact]
        public void The_zip_entries_are_written_in_the_same_order_as_the_manifest()
        {
            using (var dir = new TempDir())
            {
                var plan = new DataPackageBuilder.PackagePlan();
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "iconsets/" + UidA + ".zip",
                    Uid = "icon-1",
                    Kind = DataPackageBuilder.EntryKind.Iconset,
                    SourcePath = dir.File("set.zip"),
                });
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "cot/f1.cot",
                    Uid = "f1",
                    Kind = DataPackageBuilder.EntryKind.Feature,
                    Content = "<event/>",
                });

                var result = DataPackageWriter.Write(plan, "Pkg", Path.Combine(dir.Path, "out.zip"));

                using (var archive = ZipFile.OpenRead(result.Path))
                {
                    var names = archive.Entries.Select(e => e.FullName)
                        .Where(n => n != DataPackageWriter.ManifestEntryPath).ToList();
                    Assert.Equal(new[] { "iconsets/" + UidA + ".zip", "cot/f1.cot" }, names);
                }
            }
        }

        // ── manifest ────────────────────────────────────────────────────────────

        private static DataPackageBuilder.PackagePlan SimplePlan()
        {
            return DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0", null, UidA) },
                Iconset2);
        }

        [Fact]
        public void The_manifest_declares_format_version_two()
        {
            var manifest = DataPackageWriter.BuildManifest(SimplePlan(), "Pkg", "uid-1");

            Assert.Equal("MissionPackageManifest", manifest.Root.Name.LocalName);
            Assert.Equal("2", manifest.Root.Attribute("version").Value);
        }

        [Fact]
        public void The_manifest_carries_the_package_uid_and_name()
        {
            var manifest = DataPackageWriter.BuildManifest(SimplePlan(), "My Package", "uid-1");

            Assert.Equal("uid-1", Parameter(manifest, "Configuration", "uid"));
            Assert.Equal("My Package", Parameter(manifest, "Configuration", "name"));
        }

        /// <summary>A recipient must import the contents, not merely file the zip.</summary>
        [Fact]
        public void The_manifest_asks_the_recipient_to_import()
        {
            var manifest = DataPackageWriter.BuildManifest(SimplePlan(), "Pkg", "uid-1");

            Assert.Equal("true", Parameter(manifest, "Configuration", "onReceiveImport"));
        }

        /// <summary>The zip must survive import: the iconsets inside are referenced by markers for
        /// as long as the layer is on the recipient's map, so deleting it would strip symbology
        /// from exactly the offline peer the package exists for.</summary>
        [Fact]
        public void The_manifest_does_not_ask_the_recipient_to_delete_the_package()
        {
            var manifest = DataPackageWriter.BuildManifest(SimplePlan(), "Pkg", "uid-1");

            Assert.Equal("false", Parameter(manifest, "Configuration", "onReceiveDelete"));
        }

        [Fact]
        public void Every_planned_entry_appears_as_a_content_element()
        {
            var plan = SimplePlan();
            var manifest = DataPackageWriter.BuildManifest(plan, "Pkg", "uid-1");

            var zipEntries = manifest.Root.Element("Contents").Elements("Content")
                .Select(c => c.Attribute("zipEntry").Value).ToList();

            Assert.Equal(plan.Entries.Count, zipEntries.Count);
            Assert.Equal(plan.Entries.Select(e => e.PackagePath).OrderBy(x => x),
                         zipEntries.OrderBy(x => x));
            Assert.All(manifest.Root.Element("Contents").Elements("Content"),
                c => Assert.Equal("false", c.Attribute("ignore").Value));
        }

        /// <summary>
        /// ONLY CoT content carries a uid parameter.
        ///
        /// <para>An earlier version of this test asserted the opposite — that every content
        /// element carries one — and that was the bug, not the contract. In ATAK a
        /// <c>uid</c> parameter means "this content is an attachment of the CoT item with that
        /// uid". Our iconsets and layer configs carried uids matching no item, so both were filed
        /// into <c>atak/attachments/&lt;uid&gt;/</c> as orphan folders and neither was ever
        /// imported. Real packages carry a uid only on CoT.</para>
        /// </summary>
        [Fact]
        public void Only_cot_content_carries_a_uid_parameter()
        {
            var plan = DataPackageBuilder.Plan(
                new[] { LayerWithFeatures("Teams", "https://h/a/FeatureServer/0", "f1") },
                Iconset2);
            var manifest = DataPackageWriter.BuildManifest(plan, "Pkg", "uid-1");

            foreach (var content in manifest.Root.Element("Contents").Elements("Content"))
            {
                string path = content.Attribute("zipEntry").Value;
                bool hasUid = content.Elements("Parameter")
                    .Any(p => p.Attribute("name").Value == "uid");

                if (path.StartsWith(DataPackageBuilder.FeatureFolder + "/", StringComparison.Ordinal))
                    Assert.True(hasUid, "CoT content should carry its uid: " + path);
                else
                    Assert.False(hasUid,
                        "a uid here makes ATAK file it as an attachment: " + path);
            }
        }

        [Fact]
        public void The_serialized_manifest_starts_with_an_xml_declaration()
        {
            string xml = DataPackageWriter.Serialize(
                DataPackageWriter.BuildManifest(SimplePlan(), "Pkg", "uid-1"));

            Assert.StartsWith("<?xml", xml, StringComparison.Ordinal);
            // Round-trips: whatever we wrote, a parser reads back.
            Assert.Equal("MissionPackageManifest", XDocument.Parse(xml).Root.Name.LocalName);
        }

        /// <summary>
        /// The manifest must declare the encoding its bytes are actually in.
        ///
        /// <para>It shipped declaring <c>utf-16</c> while being written as UTF-8, because
        /// <c>XDocument.Save(TextWriter)</c> takes the declared encoding from the writer and a
        /// plain <c>StringWriter</c> reports UTF-16. The test above did not catch it: parsing a
        /// STRING ignores the declaration, so the round-trip passed while the shipped bytes were
        /// wrong. A recipient reads the manifest out of a zip as a STREAM, where the declaration
        /// is honoured and the mismatch is fatal.</para>
        /// </summary>
        [Fact]
        public void The_manifest_declares_utf8_not_the_writers_encoding()
        {
            string xml = DataPackageWriter.Serialize(
                DataPackageWriter.BuildManifest(SimplePlan(), "Pkg", "uid-1"));

            Assert.DoesNotContain("utf-16", xml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("utf-8", xml, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the manifest back the way a recipient does — from the zip, as a stream —
        /// which is the path the encoding mismatch actually broke.</summary>
        [Fact]
        public void The_manifest_can_be_loaded_from_the_zip_as_a_stream()
        {
            using (var dir = new TempDir())
            {
                var plan = SimplePlanWithRealIconset(dir);
                var result = DataPackageWriter.Write(plan, "Pkg", Path.Combine(dir.Path, "out.zip"));

                using (var archive = ZipFile.OpenRead(result.Path))
                using (var stream = archive.GetEntry(DataPackageWriter.ManifestEntryPath).Open())
                {
                    var manifest = XDocument.Load(stream);
                    Assert.Equal("MissionPackageManifest", manifest.Root.Name.LocalName);
                }
            }
        }

        private static DataPackageBuilder.PackagePlan SimplePlanWithRealIconset(TempDir dir)
        {
            var plan = new DataPackageBuilder.PackagePlan();
            plan.Entries.Add(new DataPackageBuilder.PlannedEntry
            {
                PackagePath = "featurelink/a.featurelinkshare",
                Uid = "cfg-1",
                Kind = DataPackageBuilder.EntryKind.LayerConfig,
                Content = "{}",
            });
            return plan;
        }

        [Fact]
        public void The_package_uid_is_stable_for_the_same_contents_and_changes_with_them()
        {
            var plan = SimplePlan();
            Assert.Equal(DataPackageWriter.PackageUid(plan, "Pkg"),
                         DataPackageWriter.PackageUid(SimplePlan(), "Pkg"));

            Assert.NotEqual(DataPackageWriter.PackageUid(plan, "Pkg"),
                            DataPackageWriter.PackageUid(plan, "Other"));

            var bigger = DataPackageBuilder.Plan(
                new[] { Layer("Teams", "https://h/a/FeatureServer/0", null, UidA),
                        Layer("ICP", "https://h/b/FeatureServer/0") },
                Iconset2);
            Assert.NotEqual(DataPackageWriter.PackageUid(plan, "Pkg"),
                            DataPackageWriter.PackageUid(bigger, "Pkg"));
        }

        // ── writing the zip ─────────────────────────────────────────────────────

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }

            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "fl-pkg-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string File(string name, string content = "icon-zip-bytes")
            {
                string full = System.IO.Path.Combine(Path, name);
                System.IO.File.WriteAllText(full, content);
                return full;
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch (IOException) { /* test temp */ }
            }
        }

        private static Dictionary<string, string> ReadZip(string path)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var archive = ZipFile.OpenRead(path))
                foreach (var entry in archive.Entries)
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        entries[entry.FullName] = reader.ReadToEnd();
            return entries;
        }

        [Fact]
        public void A_written_package_contains_the_manifest_and_every_entry()
        {
            using (var dir = new TempDir())
            {
                var plan = new DataPackageBuilder.PackagePlan();
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "featurelink/teams.featurelinkshare",
                    Uid = "cfg-1",
                    Kind = DataPackageBuilder.EntryKind.LayerConfig,
                    Content = "{\"v\":1}",
                });
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "iconsets/" + UidA + ".zip",
                    Uid = "icon-1",
                    Kind = DataPackageBuilder.EntryKind.Iconset,
                    SourcePath = dir.File("set.zip"),
                });

                string destination = Path.Combine(dir.Path, "out.zip");
                var result = DataPackageWriter.Write(plan, "Pkg", destination);

                var entries = ReadZip(result.Path);
                Assert.True(entries.ContainsKey(DataPackageWriter.ManifestEntryPath));
                Assert.Equal("{\"v\":1}", entries["featurelink/teams.featurelinkshare"]);
                Assert.Equal("icon-zip-bytes", entries["iconsets/" + UidA + ".zip"]);
                Assert.Equal(2, result.EntryCount);
                Assert.Empty(result.Skipped);
            }
        }

        /// <summary>One unreadable iconset must not cost the operator the whole send — but the
        /// manifest must then not name it, or the recipient is told to import something absent.</summary>
        [Fact]
        public void A_missing_source_file_is_skipped_and_left_out_of_the_manifest()
        {
            using (var dir = new TempDir())
            {
                var plan = new DataPackageBuilder.PackagePlan();
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "featurelink/teams.featurelinkshare",
                    Uid = "cfg-1",
                    Kind = DataPackageBuilder.EntryKind.LayerConfig,
                    Content = "{}",
                });
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "iconsets/" + UidA + ".zip",
                    Uid = "icon-1",
                    Kind = DataPackageBuilder.EntryKind.Iconset,
                    SourcePath = Path.Combine(dir.Path, "does-not-exist.zip"),
                });

                var result = DataPackageWriter.Write(plan, "Pkg", Path.Combine(dir.Path, "out.zip"));

                var entries = ReadZip(result.Path);
                Assert.False(entries.ContainsKey("iconsets/" + UidA + ".zip"));
                Assert.Equal(1, result.EntryCount);
                Assert.Single(result.Skipped);

                var manifest = XDocument.Parse(entries[DataPackageWriter.ManifestEntryPath]);
                var listed = manifest.Root.Element("Contents").Elements("Content")
                    .Select(c => c.Attribute("zipEntry").Value).ToList();
                Assert.Equal(new[] { "featurelink/teams.featurelinkshare" }, listed);
            }
        }

        [Fact]
        public void The_manifest_uid_matches_what_was_actually_written()
        {
            using (var dir = new TempDir())
            {
                var plan = new DataPackageBuilder.PackagePlan();
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "featurelink/a.featurelinkshare",
                    Uid = "cfg-1",
                    Kind = DataPackageBuilder.EntryKind.LayerConfig,
                    Content = "{}",
                });

                var result = DataPackageWriter.Write(plan, "Pkg", Path.Combine(dir.Path, "out.zip"));

                var manifest = XDocument.Parse(ReadZip(result.Path)[DataPackageWriter.ManifestEntryPath]);
                Assert.Equal(result.Uid, Parameter(manifest, "Configuration", "uid"));
            }
        }

        [Fact]
        public void An_empty_plan_is_refused_rather_than_sending_an_empty_package()
        {
            using (var dir = new TempDir())
            {
                Assert.Throws<InvalidOperationException>(() => DataPackageWriter.Write(
                    new DataPackageBuilder.PackagePlan(), "Pkg", Path.Combine(dir.Path, "out.zip")));
            }
        }

        /// <summary>An interrupted write must not leave a half-built zip that looks finished.</summary>
        [Fact]
        public void No_temporary_file_survives_a_successful_write()
        {
            using (var dir = new TempDir())
            {
                var plan = new DataPackageBuilder.PackagePlan();
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "featurelink/a.featurelinkshare",
                    Uid = "cfg-1",
                    Kind = DataPackageBuilder.EntryKind.LayerConfig,
                    Content = "{}",
                });

                string destination = Path.Combine(dir.Path, "out.zip");
                DataPackageWriter.Write(plan, "Pkg", destination);

                Assert.False(File.Exists(destination + ".tmp"));
                Assert.True(File.Exists(destination));
            }
        }

        [Fact]
        public void Writing_over_an_existing_package_replaces_it()
        {
            using (var dir = new TempDir())
            {
                string destination = Path.Combine(dir.Path, "out.zip");
                File.WriteAllText(destination, "stale");

                var plan = new DataPackageBuilder.PackagePlan();
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = "featurelink/a.featurelinkshare",
                    Uid = "cfg-1",
                    Kind = DataPackageBuilder.EntryKind.LayerConfig,
                    Content = "{}",
                });

                DataPackageWriter.Write(plan, "Pkg", destination);

                Assert.True(ReadZip(destination).ContainsKey(DataPackageWriter.ManifestEntryPath));
            }
        }

        [Fact]
        public void A_package_path_is_a_legal_file_name_even_for_an_awkward_package_name()
        {
            string path = DataPackageWriter.PathIn("Teams / ICP: 2026", @"C:\packages");

            Assert.EndsWith(".zip", path);
            Assert.Equal(-1, Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()));
        }

        [Fact]
        public void A_blank_package_name_still_produces_somewhere_legal()
        {
            string path = DataPackageWriter.PathIn("   ", @"C:\packages");

            Assert.EndsWith(".zip", path);
            Assert.Equal(-1, Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()));
        }

        /// <summary>The package goes where the caller says — WinTAK's Data Packages folder in
        /// practice. A package written anywhere transient cannot be listed in the host, because
        /// the record would point at a file that is cleaned up underneath it.</summary>
        [Fact]
        public void A_package_is_written_into_the_folder_it_was_given()
        {
            string path = DataPackageWriter.PathIn("Teams", @"C:\WinTAK\Data Packages");

            Assert.Equal(@"C:\WinTAK\Data Packages", Path.GetDirectoryName(path));
        }

        [Fact]
        public void No_folder_falls_back_rather_than_producing_a_bare_file_name()
        {
            foreach (string folder in new[] { null, "", "   " })
            {
                string path = DataPackageWriter.PathIn("Teams", folder);

                Assert.Equal(DataPackageWriter.FallbackDirectory, Path.GetDirectoryName(path));
                Assert.True(Path.IsPathRooted(path));
            }
        }

        // ── helper ──────────────────────────────────────────────────────────────

        private static string Parameter(XDocument manifest, string section, string name)
        {
            return manifest.Root.Element(section).Elements("Parameter")
                .Where(p => p.Attribute("name").Value == name)
                .Select(p => p.Attribute("value").Value)
                .SingleOrDefault();
        }
    }
}
