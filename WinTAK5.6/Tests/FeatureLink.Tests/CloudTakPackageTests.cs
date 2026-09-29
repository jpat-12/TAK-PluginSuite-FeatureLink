using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// The CloudTAK-shaped package. Each assertion is one rule of CloudTAK's own importer
    /// (<c>tasks/events/src/worker.ts</c> + node-cot <c>DataPackage</c>), broken by the standard
    /// shape: an unsupported listed file fails the whole import, and an iconset is only installed
    /// from a listed <c>iconset.xml</c> with its PNGs beside it.
    /// </summary>
    public class CloudTakPackageTests : IDisposable
    {
        private const string UidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string UidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-cloudtak-" + Guid.NewGuid().ToString("N"));

        public CloudTakPackageTests() { Directory.CreateDirectory(_dir); }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { /* best effort */ }
        }

        // ── fixtures ────────────────────────────────────────────────────────────

        private string IconsetZip(string uid, string group, params string[] icons)
        {
            string path = Path.Combine(_dir, uid + ".zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var xml = new XElement("iconset",
                    new XAttribute("name", group), new XAttribute("uid", uid),
                    new XAttribute("defaultGroup", group), new XAttribute("version", "1"),
                    icons.Select(i => new XElement("icon", new XAttribute("name", i))));
                Write(archive, "iconset.xml", xml.ToString());
                foreach (var icon in icons) Write(archive, group + "/" + icon, "png:" + uid + ":" + icon);
            }
            return path;
        }

        private static void Write(ZipArchive archive, string name, string text)
        {
            using (var w = new StreamWriter(archive.CreateEntry(name).Open())) w.Write(text);
        }

        /// <summary>A standard package as WinTAK writes it: iconsets zipped at the root, a config,
        /// and one selected feature.</summary>
        private string StandardPackage(params Tuple<string, string>[] iconsets)
        {
            var plan = new DataPackageBuilder.PackagePlan();
            foreach (var set in iconsets)
            {
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    SourcePath = set.Item1,
                    PackagePath = set.Item2 + ".zip",
                    Uid = "featurelink-iconset-x",
                    Kind = DataPackageBuilder.EntryKind.Iconset,
                });
            }
            plan.Entries.Add(new DataPackageBuilder.PlannedEntry
            {
                PackagePath = "featurelink/Teams-abc.featurelinkshare",
                Uid = "featurelink-cfg-abc",
                Kind = DataPackageBuilder.EntryKind.LayerConfig,
                Content = "{\"url\":\"https://h/a/FeatureServer/0\"}",
            });
            plan.Entries.Add(new DataPackageBuilder.PlannedEntry
            {
                PackagePath = "cot/f1.cot",
                Uid = "f1",
                Kind = DataPackageBuilder.EntryKind.Feature,
                Content = "<event uid=\"f1\"/>",
            });

            return DataPackageWriter.Write(plan, "FeatureLink-Teams", Path.Combine(_dir, "standard.zip")).Path;
        }

        private static Dictionary<string, string> Entries(string zip)
        {
            using (var archive = ZipFile.OpenRead(zip))
                return archive.Entries.ToDictionary(e => e.FullName, e =>
                {
                    using (var r = new StreamReader(e.Open())) return r.ReadToEnd();
                });
        }

        private static List<XElement> Contents(string zip)
        {
            using (var archive = ZipFile.OpenRead(zip))
            using (var s = archive.GetEntry(DataPackageWriter.ManifestEntryPath).Open())
                return XDocument.Load(s).Root.Element("Contents").Elements("Content").ToList();
        }

        private static XElement Listing(List<XElement> contents, string zipEntry) =>
            contents.SingleOrDefault(c => (string)c.Attribute("zipEntry") == zipEntry);

        private string Convert(string standard) =>
            CloudTakPackage.Convert(standard, Path.Combine(_dir, "cloudtak.zip")).Path;

        // ── rules ───────────────────────────────────────────────────────────────

        [Fact]
        public void No_nested_zip_is_listed_because_CloudTAK_has_no_converter_for_one()
        {
            var cloud = Convert(StandardPackage(Tuple.Create(IconsetZip(UidA, "Teams Icons", "Active.png"), "Teams Icons")));

            Assert.DoesNotContain(Contents(cloud), c => ((string)c.Attribute("zipEntry")).EndsWith(".zip"));
            Assert.DoesNotContain(Entries(cloud).Keys, k => k.EndsWith(".zip"));
        }

        [Fact]
        public void The_iconset_is_expanded_with_its_xml_listed_and_pngs_at_their_group_paths()
        {
            var cloud = Convert(StandardPackage(Tuple.Create(IconsetZip(UidA, "Teams Icons", "Active.png", "Out.png"), "Teams Icons")));
            var entries = Entries(cloud);
            var contents = Contents(cloud);

            // The group path is what CloudTAK stores the icon under, after the uid — so it must match
            // the usericon a marker carries: <uid>/Teams Icons/Active.png.
            Assert.Equal("png:" + UidA + ":Active.png", entries["Teams Icons/Active.png"]);
            Assert.Contains("uid=\"" + UidA + "\"", entries["iconsets/" + UidA + ".xml"]);

            foreach (var path in new[] { "iconsets/" + UidA + ".xml", "Teams Icons/Active.png", "Teams Icons/Out.png" })
            {
                var listed = Listing(contents, path);
                Assert.NotNull(listed);
                Assert.Equal("false", (string)listed.Attribute("ignore"));
                Assert.Empty(listed.Elements("Parameter"));   // a uid would make it a CoT attachment
            }
        }

        [Fact]
        public void The_layer_config_travels_but_is_ignored_by_the_native_importer()
        {
            var cloud = Convert(StandardPackage());

            Assert.Equal("{\"url\":\"https://h/a/FeatureServer/0\"}",
                Entries(cloud)["featurelink/Teams-abc.featurelinkshare"]);
            Assert.Equal("true", (string)Listing(Contents(cloud), "featurelink/Teams-abc.featurelinkshare").Attribute("ignore"));
        }

        [Fact]
        public void Cot_features_are_copied_unchanged_with_their_uid_parameter()
        {
            var cloud = Convert(StandardPackage());

            Assert.Equal("<event uid=\"f1\"/>", Entries(cloud)["cot/f1.cot"]);
            var listed = Listing(Contents(cloud), "cot/f1.cot");
            Assert.Equal("false", (string)listed.Attribute("ignore"));
            Assert.Equal("f1", (string)listed.Element("Parameter").Attribute("value"));
        }

        [Fact]
        public void The_manifest_configuration_and_utf8_declaration_are_kept()
        {
            var standard = StandardPackage();
            var cloud = Convert(standard);

            var manifest = Entries(cloud)[DataPackageWriter.ManifestEntryPath];
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"", manifest, StringComparison.OrdinalIgnoreCase);

            string Config(string zip)
            {
                using (var archive = ZipFile.OpenRead(zip))
                using (var s = archive.GetEntry(DataPackageWriter.ManifestEntryPath).Open())
                    return XDocument.Load(s).Root.Element("Configuration").ToString();
            }
            Assert.Equal(Config(standard), Config(cloud));
        }

        [Fact]
        public void Every_listed_entry_exists_in_the_zip()
        {
            var cloud = Convert(StandardPackage(
                Tuple.Create(IconsetZip(UidA, "Teams Icons", "Active.png"), "Teams Icons"),
                Tuple.Create(IconsetZip(UidB, "Assets Icons", "Truck.png"), "Assets Icons")));

            var entries = Entries(cloud);
            foreach (var c in Contents(cloud))
                Assert.True(entries.ContainsKey((string)c.Attribute("zipEntry")), (string)c.Attribute("zipEntry"));
        }

        [Fact]
        public void A_file_name_shared_by_two_iconsets_is_reported()
        {
            var result = CloudTakPackage.Convert(StandardPackage(
                    Tuple.Create(IconsetZip(UidA, "Teams Icons", "Active.png", "Out.png"), "Teams Icons"),
                    Tuple.Create(IconsetZip(UidB, "Assets Icons", "Active.png"), "Assets Icons")),
                Path.Combine(_dir, "cloudtak.zip"));

            Assert.Equal(2, result.IconsetCount);
            Assert.Equal(new[] { "Active.png" }, result.RepeatedIconNames);
        }

        [Theory]
        [InlineData("CloudTAK", "ANDROID-abc", true)]
        [InlineData("cloudtak-web", "x", true)]
        [InlineData(null, "ANDROID-CloudTAK-jon@example.org", true)]
        [InlineData("ATAK-CIV", "ANDROID-123", false)]
        [InlineData("WinTAK-CIV", "S-1-5-21", false)]
        [InlineData(null, null, false)]
        public void CloudTAK_contacts_are_recognised_by_platform_or_uid(string platform, string uid, bool expected)
        {
            Assert.Equal(expected, CloudTakPackage.IsCloudTakClient(platform, uid));
        }
    }
}
