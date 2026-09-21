using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// Covers the "Built packages" listing.
    ///
    /// <para>The identification rule carries the weight here. The packages folder holds every
    /// client's packages and whatever else has been dropped into it, and each row in this list
    /// offers a <b>Delete</b> button. Being generous about what counts as ours would offer to
    /// delete somebody else's package; being strict costs only a row.</para>
    /// </summary>
    public class PackageLibraryTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Path { get; }

            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "fl-lib-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch (IOException) { /* test temp */ }
            }
        }

        /// <summary>Writes a package using the real writer, so the listing is tested against what
        /// the plugin actually produces rather than a hand-made imitation of it.</summary>
        private static string WritePackage(string folder, string name,
            int layers = 1, int features = 0, int iconsets = 0)
        {
            var plan = new DataPackageBuilder.PackagePlan();

            for (int i = 0; i < layers; i++)
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = DataPackageBuilder.ConfigFolder + "/layer" + i + ".featurelinkshare",
                    Uid = "cfg-" + i,
                    Kind = DataPackageBuilder.EntryKind.LayerConfig,
                    Content = "{}",
                });

            for (int i = 0; i < features; i++)
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = DataPackageBuilder.FeatureFolder + "/f" + i + ".cot",
                    Uid = "f" + i,
                    Kind = DataPackageBuilder.EntryKind.Feature,
                    Content = "<event/>",
                });

            for (int i = 0; i < iconsets; i++)
            {
                string source = System.IO.Path.Combine(folder, "icon" + i + ".src");
                File.WriteAllText(source, "zipbytes");
                plan.Entries.Add(new DataPackageBuilder.PlannedEntry
                {
                    PackagePath = DataPackageBuilder.IconsetFolder + "/set" + i + ".zip",
                    Uid = "icon-" + i,
                    Kind = DataPackageBuilder.EntryKind.Iconset,
                    SourcePath = source,
                });
            }

            string destination = System.IO.Path.Combine(folder, name + ".zip");
            return DataPackageWriter.Write(plan, name, destination).Path;
        }

        /// <summary>A zip that is a valid data package but somebody else's.</summary>
        private static void WriteForeignPackage(string folder, string name, string uid)
        {
            string path = System.IO.Path.Combine(folder, name + ".zip");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(DataPackageWriter.ManifestEntryPath);
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                        + "<MissionPackageManifest version=\"2\"><Configuration>"
                        + "<Parameter name=\"uid\" value=\"" + uid + "\" />"
                        + "<Parameter name=\"name\" value=\"" + name + "\" />"
                        + "</Configuration><Contents /></MissionPackageManifest>");
                }
            }
        }

        // ── identification ──────────────────────────────────────────────────────

        [Fact]
        public void A_package_this_plugin_wrote_is_recognised()
        {
            using (var dir = new TempDir())
            {
                string path = WritePackage(dir.Path, "FeatureLink-Teams-20260921-1200");

                var found = PackageLibrary.Describe(path);

                Assert.NotNull(found);
                Assert.Equal("FeatureLink-Teams-20260921-1200", found.Name);
                Assert.StartsWith(PackageLibrary.UidPrefix, found.Uid);
            }
        }

        /// <summary>The guard that matters: every row offers a Delete button.</summary>
        [Fact]
        public void Another_clients_package_is_not_listed()
        {
            using (var dir = new TempDir())
            {
                WriteForeignPackage(dir.Path, "SomeoneElse", "abc-123-not-ours");

                Assert.Empty(PackageLibrary.Scan(dir.Path));
            }
        }

        [Fact]
        public void A_zip_that_is_not_a_data_package_is_ignored()
        {
            using (var dir = new TempDir())
            {
                string path = System.IO.Path.Combine(dir.Path, "holiday-photos.zip");
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                    archive.CreateEntry("photo.jpg");

                Assert.Null(PackageLibrary.Describe(path));
                Assert.Empty(PackageLibrary.Scan(dir.Path));
            }
        }

        /// <summary>The packages folder is shared, so a corrupt or half-written file is normal,
        /// not exceptional — it must not take the whole listing down.</summary>
        [Fact]
        public void A_corrupt_zip_is_skipped_without_losing_the_rest_of_the_listing()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(System.IO.Path.Combine(dir.Path, "truncated.zip"), "not a zip");
                WritePackage(dir.Path, "FeatureLink-Good-20260921-1200");

                var found = PackageLibrary.Scan(dir.Path);

                Assert.Single(found);
                Assert.Equal("FeatureLink-Good-20260921-1200", found[0].Name);
            }
        }

        [Fact]
        public void A_missing_file_or_folder_is_not_an_error()
        {
            Assert.Null(PackageLibrary.Describe(null));
            Assert.Null(PackageLibrary.Describe(@"C:\does\not\exist.zip"));
            Assert.Empty(PackageLibrary.Scan(null));
            Assert.Empty(PackageLibrary.Scan(@"C:\does\not\exist"));
        }

        // ── contents ────────────────────────────────────────────────────────────

        [Fact]
        public void The_listing_counts_what_is_inside_by_folder()
        {
            using (var dir = new TempDir())
            {
                string path = WritePackage(dir.Path, "FeatureLink-Mixed-20260921-1200",
                    layers: 2, features: 3, iconsets: 1);

                var found = PackageLibrary.Describe(path);

                Assert.Equal(2, found.LayerCount);
                Assert.Equal(3, found.FeatureCount);
                Assert.Equal(1, found.IconsetCount);
                Assert.Equal("3 features, 2 layers, 1 iconset", found.Contents);
            }
        }

        [Fact]
        public void A_whole_layer_package_reads_without_a_feature_count()
        {
            using (var dir = new TempDir())
            {
                string path = WritePackage(dir.Path, "FeatureLink-Whole-20260921-1200", layers: 1);

                Assert.Equal("1 layer", PackageLibrary.Describe(path).Contents);
            }
        }

        [Fact]
        public void The_listing_records_the_size_and_time()
        {
            using (var dir = new TempDir())
            {
                string path = WritePackage(dir.Path, "FeatureLink-Teams-20260921-1200");

                var found = PackageLibrary.Describe(path);

                Assert.True(found.SizeBytes > 0);
                Assert.Equal(new FileInfo(path).Length, found.SizeBytes);
                Assert.Contains("·", found.Detail);
            }
        }

        [Theory]
        [InlineData(512L, "512 B")]
        [InlineData(2048L, "2 KB")]
        [InlineData(1572864L, "1.5 MB")]
        public void Sizes_read_naturally(long bytes, string expected)
        {
            Assert.Equal(expected, PackageLibrary.FormatSize(bytes));
        }

        // ── ordering ────────────────────────────────────────────────────────────

        /// <summary>Newest first: the package just built is the one being looked for.</summary>
        [Fact]
        public void The_newest_package_is_listed_first()
        {
            using (var dir = new TempDir())
            {
                string older = WritePackage(dir.Path, "FeatureLink-Older-20260920-1200");
                string newer = WritePackage(dir.Path, "FeatureLink-Newer-20260921-1200");

                File.SetLastWriteTime(older, DateTime.Now.AddDays(-2));
                File.SetLastWriteTime(newer, DateTime.Now);

                var found = PackageLibrary.Scan(dir.Path);

                Assert.Equal(2, found.Count);
                Assert.Equal("FeatureLink-Newer-20260921-1200", found[0].Name);
            }
        }

        [Fact]
        public void Several_packages_are_all_found()
        {
            using (var dir = new TempDir())
            {
                WritePackage(dir.Path, "FeatureLink-A-20260921-1200");
                WritePackage(dir.Path, "FeatureLink-B-20260921-1300");
                WriteForeignPackage(dir.Path, "NotOurs", "other-plugin-uid");

                var found = PackageLibrary.Scan(dir.Path);

                Assert.Equal(2, found.Count);
                Assert.All(found, p => Assert.StartsWith(PackageLibrary.UidPrefix, p.Uid));
            }
        }

        /// <summary>A package renamed on disk keeps its identity, because the mark is in the
        /// manifest rather than in the file name.</summary>
        [Fact]
        public void A_renamed_package_is_still_recognised()
        {
            using (var dir = new TempDir())
            {
                string path = WritePackage(dir.Path, "FeatureLink-Teams-20260921-1200");
                string renamed = System.IO.Path.Combine(dir.Path, "whatever.zip");
                File.Move(path, renamed);

                var found = PackageLibrary.Describe(renamed);

                Assert.NotNull(found);
                // The manifest name survives; only the file name changed.
                Assert.Equal("FeatureLink-Teams-20260921-1200", found.Name);
            }
        }
    }
}
