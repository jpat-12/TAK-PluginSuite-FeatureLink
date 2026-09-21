using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Finds the data packages this plugin has built.
    ///
    /// <para>Reads them back off disk rather than keeping a list of what was created. Disk is the
    /// truth: a package deleted from WinTAK's own Data Packages list, or moved, or built by a
    /// previous run of the plugin, all have to produce the right answer, and a remembered list
    /// gets every one of those wrong. The cost is opening each zip's manifest, which is a few
    /// kilobytes per file in a folder that holds tens of items.</para>
    ///
    /// <para>SDK-free, so the identification rule — what counts as "ours" — is tested. Getting it
    /// wrong in the generous direction would offer the operator a Delete button for another
    /// plugin's package.</para>
    /// </summary>
    public static class PackageLibrary
    {
        /// <summary>Prefix on every package UID this plugin writes, from
        /// <see cref="DataPackageWriter.PackageUid"/>. This is the identifying mark: it is in the
        /// manifest, so it survives the file being renamed or moved.</summary>
        public const string UidPrefix = "featurelink-pkg-";

        /// <summary>One package found on disk.</summary>
        public sealed class BuiltPackage
        {
            /// <summary>Operator-visible name from the manifest, falling back to the file name.</summary>
            public string Name { get; set; }

            /// <summary>The manifest's package UID.</summary>
            public string Uid { get; set; }

            /// <summary>Absolute path of the zip.</summary>
            public string Path { get; set; }

            /// <summary>When the file was last written.</summary>
            public DateTime ModifiedLocal { get; set; }

            public long SizeBytes { get; set; }

            /// <summary>Counts of what is inside, by folder.</summary>
            public int LayerCount { get; set; }
            public int FeatureCount { get; set; }
            public int IconsetCount { get; set; }

            /// <summary>"3 features from 1 layer, 2 iconsets" — the same shape of line the
            /// workflow shows before sending, so the two read alike.</summary>
            public string Contents
            {
                get
                {
                    var parts = new List<string>(3);
                    if (FeatureCount > 0)
                        parts.Add(FeatureCount == 1 ? "1 feature" : FeatureCount + " features");
                    parts.Add(LayerCount == 1 ? "1 layer" : LayerCount + " layers");
                    if (IconsetCount > 0)
                        parts.Add(IconsetCount == 1 ? "1 iconset" : IconsetCount + " iconsets");
                    return string.Join(", ", parts);
                }
            }

            /// <summary>Size and date, for the row's second line.</summary>
            public string Detail
            {
                get
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0} · {1}",
                        FormatSize(SizeBytes),
                        ModifiedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                }
            }
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (kb / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// Every FeatureLink package in a folder, newest first.
        /// </summary>
        /// <param name="folder">Where to look. A missing folder is not an error — it only means
        /// nothing has been built yet.</param>
        public static List<BuiltPackage> Scan(string folder)
        {
            var found = new List<BuiltPackage>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return found;

            string[] files;
            try { files = Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                Log.Warn("Could not list the packages folder: " + ex.Message);
                return found;
            }

            foreach (string file in files)
            {
                var package = Describe(file);
                if (package != null) found.Add(package);
            }

            return found.OrderByDescending(p => p.ModifiedLocal).ToList();
        }

        /// <summary>
        /// Reads one zip's manifest and returns it when it is one of ours, or null otherwise.
        ///
        /// <para>Never throws. The packages folder holds other clients' packages and whatever else
        /// has been dropped there, so an unreadable or unrelated zip is the normal case, not an
        /// exceptional one.</para>
        /// </summary>
        public static BuiltPackage Describe(string zipPath)
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return null;

            try
            {
                var info = new FileInfo(zipPath);

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = archive.GetEntry(DataPackageWriter.ManifestEntryPath);
                    if (entry == null) return null;   // not a data package at all

                    XDocument manifest;
                    using (var stream = entry.Open())
                        manifest = XDocument.Load(stream);

                    string uid = Parameter(manifest, "uid");
                    if (uid == null || !uid.StartsWith(UidPrefix, StringComparison.Ordinal))
                        return null;   // a package, but not ours

                    var package = new BuiltPackage
                    {
                        Uid = uid,
                        Name = Parameter(manifest, "name") ?? Path.GetFileNameWithoutExtension(zipPath),
                        Path = zipPath,
                        ModifiedLocal = info.LastWriteTime,
                        SizeBytes = info.Length,
                    };

                    foreach (var content in manifest.Root
                                 .Elements("Contents").Elements("Content"))
                    {
                        var attribute = content.Attribute("zipEntry");
                        string path = attribute != null ? attribute.Value : null;
                        if (string.IsNullOrEmpty(path)) continue;

                        if (path.StartsWith(DataPackageBuilder.ConfigFolder + "/", StringComparison.Ordinal))
                            package.LayerCount++;
                        else if (path.StartsWith(DataPackageBuilder.FeatureFolder + "/", StringComparison.Ordinal))
                            package.FeatureCount++;
                        else if (path.StartsWith(DataPackageBuilder.IconsetFolder + "/", StringComparison.Ordinal))
                            package.IconsetCount++;
                    }

                    return package;
                }
            }
            catch (Exception ex)
            {
                // Includes the file being written right now by another part of the plugin.
                Log.Info("Skipping " + Path.GetFileName(zipPath) + " while listing packages: " + ex.Message);
                return null;
            }
        }

        private static string Parameter(XDocument manifest, string name)
        {
            if (manifest == null || manifest.Root == null) return null;

            return manifest.Root.Elements("Configuration").Elements("Parameter")
                .Where(p =>
                {
                    var n = p.Attribute("name");
                    return n != null && n.Value == name;
                })
                .Select(p =>
                {
                    var v = p.Attribute("value");
                    return v != null ? v.Value : null;
                })
                .FirstOrDefault();
        }
    }
}
