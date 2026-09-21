using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Writes a plan from <see cref="DataPackageBuilder"/> out as a TAK data package: a zip with a
    /// <c>MANIFEST/manifest.xml</c> describing its contents.
    ///
    /// <para><b>Why this does not use <c>WinTak.MissionPackages</c>.</b> The SDK does expose a
    /// package builder (<c>MissionPackage</c> + <c>FilePackageContent</c> + <c>Save()</c>), and
    /// using it would be the obvious route. It is not used for two reasons. It would add an eighth
    /// SDK assembly reference to a project CI already cannot compile, widening the surface that
    /// only builds on a licensed workstation. And the manifest is the one part of this feature that
    /// every other TAK client has to agree with, which makes it exactly the part worth having under
    /// test — the format below is asserted against fixtures, where a call into a host type could
    /// only be verified by running WinTAK.</para>
    ///
    /// <para>The format is the long-stable TAK one: manifest version 2, a <c>Configuration</c>
    /// block carrying the package UID and name, and one <c>Content</c> element per zip entry.
    /// <c>onReceiveImport</c> is true so a recipient's client imports the contents rather than
    /// merely filing the zip.</para>
    /// </summary>
    public static class DataPackageWriter
    {
        /// <summary>Where the manifest lives inside the zip. Fixed by the format.</summary>
        public const string ManifestEntryPath = "MANIFEST/manifest.xml";

        /// <summary>
        /// Where packages are written when the caller does not supply a folder.
        ///
        /// <para>A last-resort fallback only. The dock pane passes WinTAK's own Data Packages
        /// folder, because a package written to %TEMP% cannot be listed in the host: the record
        /// would point at a file that is cleaned up underneath it.</para>
        /// </summary>
        public static string FallbackDirectory
        {
            get { return Path.Combine(Path.GetTempPath(), "FeatureLinkPackages"); }
        }

        // -------------------------------------------------------------------------
        // Manifest
        // -------------------------------------------------------------------------

        /// <summary>
        /// Builds the manifest document for a plan.
        /// </summary>
        /// <param name="plan">The planned contents.</param>
        /// <param name="packageName">Operator-visible package name, as it appears in a recipient's
        /// package list.</param>
        /// <param name="packageUid">Stable identity for the package. A recipient keys on this, so
        /// re-sending an edited package under the same UID replaces rather than duplicates.</param>
        public static XDocument BuildManifest(
            DataPackageBuilder.PackagePlan plan, string packageName, string packageUid)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var configuration = new XElement("Configuration",
                Parameter("uid", packageUid),
                // The receiving client shows this, and names the file after it.
                Parameter("name", packageName),
                Parameter("onReceiveImport", "true"),
                // The zip stays on the recipient's disk: the iconsets inside are referenced by
                // markers for as long as the layer is on their map, so deleting it after import
                // would strip symbology from exactly the offline peer this package exists for.
                Parameter("onReceiveDelete", "false"));

            var contents = new XElement("Contents");
            foreach (var entry in plan.Entries)
            {
                contents.Add(new XElement("Content",
                    new XAttribute("ignore", "false"),
                    new XAttribute("zipEntry", entry.PackagePath),
                    Parameter("uid", entry.Uid)));
            }

            return new XDocument(
                new XDeclaration("1.0", "UTF-8", "standalone=\"yes\""),
                new XElement("MissionPackageManifest",
                    new XAttribute("version", "2"),
                    configuration,
                    contents));
        }

        private static XElement Parameter(string name, string value)
        {
            return new XElement("Parameter",
                new XAttribute("name", name),
                new XAttribute("value", value ?? string.Empty));
        }

        /// <summary>A stable package UID derived from the package name and its entry paths, so the
        /// same selection sent twice in the same minute is one package to the recipient, and a
        /// different selection is a different one.</summary>
        public static string PackageUid(DataPackageBuilder.PackagePlan plan, string packageName)
        {
            var sb = new StringBuilder(packageName ?? string.Empty);
            if (plan != null)
                foreach (var entry in plan.Entries.OrderBy(e => e.PackagePath, StringComparer.Ordinal))
                    sb.Append('\n').Append(entry.PackagePath);

            return "featurelink-pkg-" + ArcGisFeatureService.ShortHash(sb.ToString());
        }

        // -------------------------------------------------------------------------
        // Zip
        // -------------------------------------------------------------------------

        /// <summary>The outcome of writing a package.</summary>
        public sealed class WriteResult
        {
            /// <summary>Absolute path of the written zip.</summary>
            public string Path { get; set; }

            /// <summary>The package's UID, as written into the manifest.</summary>
            public string Uid { get; set; }

            /// <summary>Entries whose source file could not be read. The package is still written
            /// without them — losing one iconset should not cost the operator the whole send — but
            /// the caller reports them.</summary>
            public List<string> Skipped { get; } = new List<string>();

            /// <summary>Number of content entries actually written, excluding the manifest.</summary>
            public int EntryCount { get; set; }
        }

        /// <summary>
        /// Writes the package to <paramref name="destinationPath"/>, overwriting any file there.
        /// </summary>
        /// <param name="plan">Planned contents. Layer-config entries are written from their
        /// <c>Content</c> string; iconset entries are copied from <c>SourcePath</c>.</param>
        /// <param name="packageName">Operator-visible name recorded in the manifest.</param>
        /// <param name="destinationPath">Where to write the zip.</param>
        public static WriteResult Write(
            DataPackageBuilder.PackagePlan plan, string packageName, string destinationPath)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrEmpty(destinationPath)) throw new ArgumentException(
                "A destination path is required.", nameof(destinationPath));
            if (plan.Entries.Count == 0) throw new InvalidOperationException(
                "The package is empty — select at least one layer.");

            var result = new WriteResult { Uid = PackageUid(plan, packageName) };

            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Written to a temporary name and moved into place, so an interrupted write cannot
            // leave a half-built zip that looks like a finished package.
            string temporaryPath = destinationPath + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var written = new List<DataPackageBuilder.PlannedEntry>();

                    foreach (var entry in plan.Entries)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.PackagePath)) continue;

                        try
                        {
                            // Configs and CoT are held in memory as text; only an iconset is a
                            // file on disk that has to be copied.
                            if (entry.Kind != DataPackageBuilder.EntryKind.Iconset)
                            {
                                WriteText(archive, entry.PackagePath, entry.Content ?? string.Empty);
                            }
                            else
                            {
                                if (!File.Exists(entry.SourcePath))
                                {
                                    result.Skipped.Add(entry.DisplayName);
                                    continue;
                                }
                                WriteFile(archive, entry.PackagePath, entry.SourcePath);
                            }
                            written.Add(entry);
                        }
                        catch (Exception ex)
                        {
                            // One unreadable iconset must not cost the operator the whole package.
                            Log.Warn("Leaving \"" + entry.PackagePath + "\" out of the data package: "
                                + ex.Message);
                            result.Skipped.Add(entry.DisplayName);
                        }
                    }

                    if (written.Count == 0)
                        throw new InvalidOperationException(
                            "Nothing could be written into the package.");

                    // The manifest lists only what actually went in, so a recipient is never told
                    // to import an entry the zip does not contain.
                    var actual = new DataPackageBuilder.PackagePlan();
                    actual.Entries.AddRange(written);
                    result.Uid = PackageUid(actual, packageName);
                    result.EntryCount = written.Count;

                    var manifest = BuildManifest(actual, packageName, result.Uid);
                    WriteText(archive, ManifestEntryPath, Serialize(manifest));
                }

                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(temporaryPath, destinationPath);
                result.Path = destinationPath;
                return result;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception ex)
                {
                    Log.Warn("Could not clean up the partial package file: " + ex.Message);
                }
            }
        }

        /// <summary>Serializes the manifest with its XML declaration, which the format requires.</summary>
        public static string Serialize(XDocument manifest)
        {
            if (manifest == null) return string.Empty;
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                manifest.Save(writer);
                writer.Flush();
            }
            // XDocument.Save to a TextWriter omits the declaration; the format expects it.
            if (manifest.Declaration != null && !sb.ToString().StartsWith("<?xml", StringComparison.Ordinal))
                sb.Insert(0, manifest.Declaration + Environment.NewLine);
            return sb.ToString();
        }

        private static void WriteText(ZipArchive archive, string entryPath, string content)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            // No BOM: some TAK manifest parsers read the first byte expecting '<'.
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static void WriteFile(ZipArchive archive, string entryPath, string sourcePath)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            using (var target = entry.Open())
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                source.CopyTo(target);
            }
        }

        /// <summary>
        /// Full path for a package of the given name, inside <paramref name="directory"/>.
        /// </summary>
        /// <param name="packageName">Operator-visible name; sanitized into a legal file stem.</param>
        /// <param name="directory">Where to put it. Null or blank falls back to
        /// <see cref="FallbackDirectory"/>.</param>
        public static string PathIn(string packageName, string directory)
        {
            string stem = DataPackageBuilder.Sanitize(packageName, 96);
            if (stem.Length == 0) stem = "FeatureLink-package";

            string folder = string.IsNullOrWhiteSpace(directory) ? FallbackDirectory : directory;
            return Path.Combine(folder, stem + ".zip");
        }
    }
}
