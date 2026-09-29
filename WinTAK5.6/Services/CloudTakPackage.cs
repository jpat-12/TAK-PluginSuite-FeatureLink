using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Re-shapes a standard FeatureLink data package into the form CloudTAK's own importer can
    /// ingest. Sent to CloudTAK recipients only; ATAK and WinTAK keep the verified standard shape.
    ///
    /// <para><b>Why one package cannot serve both.</b> CloudTAK imports a received package in its
    /// events task (<c>tasks/events/src/worker.ts</c>), which walks the MANIFEST and hands every
    /// listed file that is not a <c>.cot</c>, <c>.png</c> or <c>.xml</c> to a format converter. It
    /// has no converter for <c>.zip</c> or <c>.featurelinkshare</c>, and an unsupported file throws
    /// and fails the WHOLE import — after the CoT features went in, but before iconsets are
    /// processed. It installs an iconset only from an <c>iconset.xml</c> listed in the manifest with
    /// its PNGs beside it. ATAK needs exactly the opposite: the iconset zipped, because expanded PNGs
    /// are each offered as a standalone image to place on the map (see DATA-PACKAGE-FORMAT.md).</para>
    ///
    /// <para>The CloudTAK shape therefore:</para>
    /// <list type="bullet">
    /// <item>expands each iconset zip: <c>iconset.xml</c> as <c>iconsets/&lt;uid&gt;.xml</c>, and its
    /// PNGs at their own <c>&lt;Group&gt;/&lt;file&gt;.png</c> paths, which is what makes CloudTAK
    /// register them as <c>&lt;uid&gt;/&lt;Group&gt;/&lt;file&gt;</c> — the path a marker's usericon
    /// names;</item>
    /// <item>keeps each <c>.featurelinkshare</c> in the zip but marks it <c>ignore="true"</c>, so the
    /// native importer skips it while the FeatureLink CloudTAK plugin, which reads the raw zip, still
    /// applies it;</item>
    /// <item>copies everything else, CoT features included, unchanged.</item>
    /// </list>
    /// </summary>
    public static class CloudTakPackage
    {
        /// <summary>Where expanded <c>iconset.xml</c> files go, one per iconset, named by uid.</summary>
        public const string IconsetFolder = "iconsets";

        /// <summary>The outcome of a conversion.</summary>
        public sealed class Result
        {
            public string Path { get; set; }

            public int IconsetCount { get; set; }

            /// <summary>Icon file names that appear in more than one iconset. CloudTAK matches an
            /// iconset's icons to package files by bare file name, so a repeated name can be given
            /// the other iconset's image. The FeatureLink CloudTAK plugin re-registers package
            /// iconsets correctly; a recipient without it may see the wrong symbol.</summary>
            public List<string> RepeatedIconNames { get; } = new List<string>();
        }

        /// <summary>
        /// Writes the CloudTAK-shaped copy of <paramref name="sourceZip"/> to
        /// <paramref name="destinationZip"/>, overwriting any file there.
        /// </summary>
        public static Result Convert(string sourceZip, string destinationZip)
        {
            if (string.IsNullOrEmpty(sourceZip)) throw new ArgumentException("A source package is required.", nameof(sourceZip));
            if (string.IsNullOrEmpty(destinationZip)) throw new ArgumentException("A destination is required.", nameof(destinationZip));

            var result = new Result { Path = destinationZip };

            string directory = System.IO.Path.GetDirectoryName(destinationZip);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Same temp-then-move discipline as DataPackageWriter: a half-written package must never
            // sit at the real path where it could be sent.
            string temporaryPath = destinationZip + ".tmp";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            try
            {
                using (var source = ZipFile.OpenRead(sourceZip))
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
                using (var target = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var manifestEntry = source.GetEntry(DataPackageWriter.ManifestEntryPath);
                    if (manifestEntry == null)
                        throw new InvalidDataException("The package has no " + DataPackageWriter.ManifestEntryPath + ".");

                    XDocument manifest;
                    using (var s = manifestEntry.Open()) manifest = XDocument.Load(s);

                    var root = manifest.Root;
                    var configuration = root?.Element("Configuration");
                    var contents = root?.Element("Contents");
                    if (root == null || configuration == null || contents == null)
                        throw new InvalidDataException("The package manifest is not a MissionPackageManifest.");

                    var newContents = new XElement("Contents");
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    var iconOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var content in contents.Elements("Content"))
                    {
                        string zipEntry = (string)content.Attribute("zipEntry");
                        if (string.IsNullOrEmpty(zipEntry)) continue;

                        var entry = source.GetEntry(zipEntry);
                        if (entry == null) continue;   // the manifest never lists what is absent

                        if (zipEntry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            && TryExpandIconset(entry, target, newContents, written, iconOwner, result))
                        {
                            result.IconsetCount++;
                            continue;
                        }

                        Copy(entry, target, zipEntry);
                        written.Add(zipEntry);

                        var copy = new XElement(content);
                        if (zipEntry.EndsWith(".featurelinkshare", StringComparison.OrdinalIgnoreCase))
                            copy.SetAttributeValue("ignore", "true");
                        newContents.Add(copy);
                    }

                    var newManifest = new XDocument(
                        new XDeclaration("1.0", "UTF-8", "standalone=\"yes\""),
                        new XElement(root.Name, root.Attributes(), new XElement(configuration), newContents));

                    var manifestOut = target.CreateEntry(DataPackageWriter.ManifestEntryPath, CompressionLevel.Optimal);
                    using (var s = manifestOut.Open())
                    using (var writer = new StreamWriter(s, new System.Text.UTF8Encoding(false)))
                    {
                        writer.Write(DataPackageWriter.Serialize(newManifest));
                    }
                }

                if (File.Exists(destinationZip)) File.Delete(destinationZip);
                File.Move(temporaryPath, destinationZip);
                return result;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception ex) { Log.Warn("Could not clean up the partial CloudTAK package: " + ex.Message); }
            }
        }

        /// <summary>Expands one nested iconset zip into the package. False when the zip is not an
        /// iconset (no <c>iconset.xml</c>), in which case the caller copies it as-is.</summary>
        private static bool TryExpandIconset(ZipArchiveEntry entry, ZipArchive target, XElement newContents,
            HashSet<string> written, Dictionary<string, string> iconOwner, Result result)
        {
            byte[] bytes;
            using (var s = entry.Open())
            using (var buffer = new MemoryStream())
            {
                s.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            ZipArchive nested;
            try { nested = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
            catch (InvalidDataException) { return false; }   // not a zip after all: copy it verbatim

            using (nested)
            {
                var xmlEntry = nested.GetEntry("iconset.xml");
                if (xmlEntry == null) return false;

                byte[] xml = ReadAll(xmlEntry);
                string uid;
                using (var s = new MemoryStream(xml)) uid = (string)XDocument.Load(s).Root?.Attribute("uid");
                if (string.IsNullOrWhiteSpace(uid))
                    uid = System.IO.Path.GetFileNameWithoutExtension(entry.Name);

                string xmlPath = IconsetFolder + "/" + DataPackageBuilder.Sanitize(uid, 96) + ".xml";
                if (written.Add(xmlPath))
                {
                    WriteBytes(target, xmlPath, xml);
                    newContents.Add(Listed(xmlPath));
                }

                foreach (var icon in nested.Entries)
                {
                    if (icon.FullName.EndsWith("/", StringComparison.Ordinal)) continue;          // directory
                    if (string.Equals(icon.FullName, "iconset.xml", StringComparison.Ordinal)) continue;

                    // The path inside the iconset zip is <Group>/<file>, which is exactly the part of
                    // the marker's usericon after the uid, so it is kept verbatim.
                    string path = icon.FullName.Replace('\\', '/');

                    string name = System.IO.Path.GetFileName(path);
                    string owner;
                    if (iconOwner.TryGetValue(name, out owner))
                    {
                        if (!string.Equals(owner, uid, StringComparison.Ordinal) && !result.RepeatedIconNames.Contains(name))
                            result.RepeatedIconNames.Add(name);
                    }
                    else
                    {
                        iconOwner[name] = uid;
                    }

                    if (!written.Add(path)) continue;
                    WriteBytes(target, path, ReadAll(icon));
                    newContents.Add(Listed(path));
                }
            }
            return true;
        }

        private static XElement Listed(string zipEntry)
        {
            // No uid parameter: in a TAK manifest a uid marks content as an attachment of that CoT.
            return new XElement("Content",
                new XAttribute("ignore", "false"),
                new XAttribute("zipEntry", zipEntry));
        }

        private static byte[] ReadAll(ZipArchiveEntry entry)
        {
            using (var s = entry.Open())
            using (var buffer = new MemoryStream())
            {
                s.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static void WriteBytes(ZipArchive archive, string path, byte[] bytes)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open()) s.Write(bytes, 0, bytes.Length);
        }

        private static void Copy(ZipArchiveEntry entry, ZipArchive target, string path)
        {
            var copy = target.CreateEntry(path, CompressionLevel.Optimal);
            using (var from = entry.Open())
            using (var to = copy.Open())
            {
                from.CopyTo(to);
            }
        }

        /// <summary>
        /// Whether a contact is a CloudTAK session. The platform comes from the contact's
        /// <c>takv</c> ("CloudTAK:…"); the uid prefix is CloudTAK's own
        /// (<c>ANDROID-CloudTAK-&lt;username&gt;</c>) and covers a contact whose takv has not
        /// arrived yet.
        /// </summary>
        public static bool IsCloudTakClient(string clientPlatform, string uid)
        {
            if (!string.IsNullOrEmpty(clientPlatform)
                && clientPlatform.IndexOf("CloudTAK", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return !string.IsNullOrEmpty(uid)
                && uid.StartsWith("ANDROID-CloudTAK-", StringComparison.OrdinalIgnoreCase);
        }
    }
}
