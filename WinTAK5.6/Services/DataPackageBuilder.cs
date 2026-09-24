using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Plans the contents of a FeatureLink data package: what files go in, under what names, and
    /// what the package is called.
    ///
    /// <para>Deliberately free of any WinTAK type. Everything the host actually does — building a
    /// <c>MissionPackage</c>, writing the manifest, handing it to the communication service — lives
    /// in <c>DataPackageWriter</c>, which is a thin shell over this. The split exists because the
    /// interesting failure modes are all here (a layer whose icons were never generated, two layers
    /// whose names collapse to the same file name, a config naming an iconset no longer on disk)
    /// and none of them are reachable through the SDK.</para>
    ///
    /// <para>A package carries, per selected layer, the same <c>.featurelinkshare</c> config the
    /// single-layer Share button sends, plus every generated iconset zip that layer's display
    /// config references. Shipping the icons is the point: a peer derives the same iconset UID from
    /// the same layer URL, so the reference resolves — but only once the set exists on their
    /// machine, and it only gets there after they have downloaded the layer from ArcGIS themselves.
    /// Bundling the zips is what makes the markers render for a peer who cannot reach the portal at
    /// all.</para>
    /// </summary>
    public static class DataPackageBuilder
    {
        /// <summary>Where <c>IconsetInstaller</c> keeps the zips it generated. Defined here rather
        /// than there so the planner can find them without referencing SDK-touching code.</summary>
        public static string IconsetDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinTAK", "FeatureLink", "iconsets");

        /// <summary>Folder inside the package holding the layer configs.</summary>
        public const string ConfigFolder = "featurelink";

        /// <summary>Folder inside the package holding the generated iconset zips.</summary>
        public const string IconsetFolder = "iconsets";

        /// <summary>Folder inside the package holding individually selected features, as CoT.</summary>
        public const string FeatureFolder = "cot";

        /// <summary>A section-4 iconset UID: the full SHA-256 digest, lower-case hex.</summary>
        private static readonly Regex IconsetUid = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);

        /// <summary>Windows device names, illegal as a file stem at any extension.</summary>
        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
                    "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6",
                    "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);

        private static readonly char[] EdgeTrim = { ' ', '_', '-', '.' };

        // -------------------------------------------------------------------------
        // Naming
        // -------------------------------------------------------------------------

        /// <summary>
        /// The package's default name, which the operator may overwrite in the dialog.
        ///
        /// <para>A timestamp is always appended. Packages are identified by name in every
        /// recipient's package list, so two sends of the same layers an hour apart must not look
        /// like one item — the recipient needs to tell which is current.</para>
        /// </summary>
        public static string AutoName(IEnumerable<string> layerNames, DateTime timestamp)
        {
            var names = (layerNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            string stamp = timestamp.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

            string subject;
            if (names.Count == 1) subject = Sanitize(names[0], 48);
            else if (names.Count > 1)
            {
                // Beyond one layer, listing them makes an unreadably long name. The count is what
                // the recipient can actually use; the manifest carries the full list.
                subject = names.Count.ToString(CultureInfo.InvariantCulture) + "-layers";
            }
            else subject = string.Empty;

            if (subject.Length == 0) subject = "layers";
            return "FeatureLink-" + subject + "-" + stamp;
        }

        /// <summary>Strips characters illegal in a file name or manifest path, collapses runs of
        /// separators, and caps the length.</summary>
        public static string Sanitize(string raw, int maxLength)
        {
            if (maxLength < 1) maxLength = 1;
            // Spaces fold to underscores rather than surviving: this stem becomes both a file
            // name and a manifest zipEntry path, and a space in either is legal but needs quoting
            // in every tool that later touches the package.
            string cleaned = Regex.Replace(raw ?? string.Empty, "[^a-zA-Z0-9_-]", "_");
            cleaned = Regex.Replace(cleaned, "_{2,}", "_").Trim(EdgeTrim);
            if (cleaned.Length > maxLength) cleaned = cleaned.Substring(0, maxLength).Trim(EdgeTrim);
            if (cleaned.Length == 0) return string.Empty;
            // A reserved stem is illegal even with an extension, so displace it rather than blank it.
            if (ReservedDeviceNames.Contains(cleaned)) cleaned = cleaned + "_";
            return cleaned;
        }

        // -------------------------------------------------------------------------
        // Iconset discovery
        // -------------------------------------------------------------------------

        /// <summary>
        /// Every iconset UID referenced by a layer's persisted display config.
        ///
        /// <para>The config stores resolved icon paths in the section-5.4 form
        /// <c>uid/group/file.png</c> under keys this method does not need to know — it walks every
        /// string in the document and recognises the shape. That is deliberate:
        /// <c>MergeIconAndColorConfig</c> writes those paths under several keys (<c>up</c> at the
        /// top level and once per value entry) and has gained keys before. Keying off the path
        /// shape rather than a key list means a new key cannot silently drop icons out of a
        /// package.</para>
        /// </summary>
        public static SortedSet<string> ExtractIconsetUids(string configJson)
        {
            var uids = new SortedSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(configJson)) return uids;

            JToken root;
            try { root = JToken.Parse(configJson); }
            catch (Exception) { return uids; }   // a corrupt config costs icons, never the package

            // A config is normally an object, but the caller may hand over an array wrapping
            // several. Only a container has descendants; a bare scalar is checked directly.
            var container = root as JContainer;
            IEnumerable<JToken> tokens = container != null
                ? container.DescendantsAndSelf()
                : new[] { root };

            foreach (var value in tokens.OfType<JValue>())
            {
                if (value.Type != JTokenType.String) continue;
                string uid = UidFromIconsetPath(value.Value<string>());
                if (uid != null) uids.Add(uid);
            }
            return uids;
        }

        /// <summary>The UID in a <c>uid/group/file.png</c> path, or null when the string is not
        /// one. Rejects anything whose first segment is not a full digest, so an ordinary URL or a
        /// relative path cannot be mistaken for an iconset reference.</summary>
        public static string UidFromIconsetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split('/');
            if (parts.Length != 3) return null;
            if (!IconsetUid.IsMatch(parts[0])) return null;
            if (parts[1].Length == 0 || parts[2].Length == 0) return null;
            return parts[0];
        }

        // -------------------------------------------------------------------------
        // Content planning
        // -------------------------------------------------------------------------

        /// <summary>What a planned entry is, so the writer and the dialog can describe it.</summary>
        public enum EntryKind
        {
            /// <summary>A layer's <c>.featurelinkshare</c> config.</summary>
            LayerConfig,

            /// <summary>A generated iconset zip.</summary>
            Iconset,

            /// <summary>One selected feature, as a CoT event.</summary>
            Feature,
        }

        /// <summary>One file destined for the package.</summary>
        public sealed class PlannedEntry
        {
            /// <summary>Absolute path on this machine. Null for a layer config until the writer
            /// has staged it; already set for an iconset, which exists.</summary>
            public string SourcePath { get; set; }

            /// <summary>Path inside the package, forward-slashed, as it appears in the manifest.</summary>
            public string PackagePath { get; set; }

            /// <summary>Stable identifier for the manifest entry.</summary>
            public string Uid { get; set; }

            public EntryKind Kind { get; set; }

            /// <summary>Config content to stage before packaging. Null for an iconset.</summary>
            public string Content { get; set; }

            /// <summary>File name inside the package, for the dialog's contents list.</summary>
            public string DisplayName
            {
                get
                {
                    int slash = (PackagePath ?? string.Empty).LastIndexOf('/');
                    return slash < 0 ? PackagePath : PackagePath.Substring(slash + 1);
                }
            }
        }

        /// <summary>A layer as the planner sees it — no model dependency, so a plan can be tested
        /// against hand-built inputs.</summary>
        public sealed class LayerPlanInput
        {
            public string Name { get; set; }
            public string Url { get; set; }

            /// <summary>The <c>.featurelinkshare</c> JSON for this layer.</summary>
            public string ConfigJson { get; set; }

            /// <summary>Persisted display config, searched for iconset references. A fallback
            /// source, used together with <see cref="IconsetUids"/>: the config is only populated
            /// for a layer synced in this session.</summary>
            public string DisplayConfigJson { get; set; }

            /// <summary>Iconset UIDs recorded against the layer when they were installed. The
            /// authoritative source, because it survives a restart.</summary>
            public IEnumerable<string> IconsetUids { get; set; }

            /// <summary>
            /// The individually selected features from this layer, as ready-made CoT XML.
            ///
            /// <para>Null or empty means "the whole layer": the package carries only the config
            /// and the recipient downloads the features from ArcGIS themselves. When features are
            /// listed here the package carries the points <b>themselves</b>, which is the only way
            /// a subset can travel — a layer config names a service, and a service cannot express
            /// "these nineteen of its markers".</para>
            /// </summary>
            public IEnumerable<FeatureContent> Features { get; set; }
        }

        /// <summary>One selected feature bound for the package.</summary>
        public sealed class FeatureContent
        {
            /// <summary>The feature's CoT UID, which is also its file name.</summary>
            public string Uid { get; set; }

            /// <summary>The serialized CoT event.</summary>
            public string CotXml { get; set; }
        }

        /// <summary>The planned package, plus what could not be included.</summary>
        public sealed class PackagePlan
        {
            public List<PlannedEntry> Entries { get; } = new List<PlannedEntry>();

            /// <summary>Iconset UIDs a config referenced that are not on disk. Not an error — the
            /// recipient regenerates them on their first sync — but the operator is told, because a
            /// package sent to a peer who cannot reach ArcGIS will then render unstyled.</summary>
            public List<string> MissingIconsets { get; } = new List<string>();

            public int LayerCount { get { return Entries.Count(e => e.Kind == EntryKind.LayerConfig); } }
            public int IconsetCount { get { return Entries.Count(e => e.Kind == EntryKind.Iconset); } }
            public int FeatureCount { get { return Entries.Count(e => e.Kind == EntryKind.Feature); } }
        }

        /// <summary>Plans a package for the selected layers.</summary>
        /// <param name="layers">The selected layers, in display order.</param>
        /// <param name="iconsetExists">Tests whether a generated iconset zip is present. Injected
        /// so a plan is testable without touching <c>%AppData%</c>.</param>
        public static PackagePlan Plan(IEnumerable<LayerPlanInput> layers, Func<string, bool> iconsetExists)
        {
            if (iconsetExists == null) iconsetExists = uid => File.Exists(IconsetZipPath(uid));

            var plan = new PackagePlan();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIconsets = new HashSet<string>(StringComparer.Ordinal);

            foreach (var layer in layers ?? Enumerable.Empty<LayerPlanInput>())
            {
                if (layer == null || string.IsNullOrEmpty(layer.ConfigJson)) continue;

                // Two layers can carry the same display name — the same service published twice, or
                // two names truncated to the same 48 characters. The URL hash disambiguates, and the
                // counter catches even a hash collision, because a duplicate entry path would mean
                // one layer silently overwriting the other inside the zip.
                string stem = Sanitize(layer.Name, 48);
                if (stem.Length == 0) stem = "layer";
                stem = stem + "-" + ArcGisFeatureService.ShortHash(layer.Url ?? stem);
                string unique = stem;
                for (int n = 2; !usedNames.Add(unique); n++)
                    unique = stem + "_" + n.ToString(CultureInfo.InvariantCulture);

                // ── ICONSETS FIRST. ──────────────────────────────────────────────
                // A marker resolves its icon when it is CREATED and keeps what it resolved — the
                // same rule that made replacing an installed iconset so awkward. So an iconset
                // that arrives after the CoT it belongs to is too late: the markers are already
                // on the recipient's map wearing default symbols, and nothing re-resolves them.
                //
                // Entries were previously emitted config → features → iconsets, i.e. exactly the
                // wrong way round. They now go iconsets → config → features, in the manifest and
                // in the zip (the writer preserves this order), so any importer that honours
                // either one installs the icons before it creates the markers.
                //
                // NOTE this is necessary but not proven sufficient: WinTAK's ImportPackage hands
                // the whole zip to IImportManager.ImportAsync, and whether that dispatches in
                // manifest order is not documented. See docs/DATA-PACKAGE-FORMAT.md.
                var referenced = ExtractIconsetUids(layer.DisplayConfigJson);
                foreach (string recorded in layer.IconsetUids ?? Enumerable.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(recorded)) referenced.Add(recorded.Trim());

                foreach (string uid in referenced)
                {
                    // Layers sharing a renderer field share a UID; the zip goes in once.
                    if (!seenIconsets.Add(uid)) continue;

                    if (!iconsetExists(uid)) { plan.MissingIconsets.Add(uid); continue; }

                    plan.Entries.Add(new PlannedEntry
                    {
                        SourcePath = IconsetZipPath(uid),
                        PackagePath = IconsetFolder + "/" + uid + ".zip",
                        Uid = "featurelink-iconset-" + uid,
                        Kind = EntryKind.Iconset,
                    });
                }

                plan.Entries.Add(new PlannedEntry
                {
                    PackagePath = ConfigFolder + "/" + unique + ".featurelinkshare",
                    Uid = "featurelink-cfg-" + ArcGisFeatureService.ShortHash(layer.Url ?? unique),
                    Kind = EntryKind.LayerConfig,
                    Content = layer.ConfigJson,
                });

                foreach (var feature in layer.Features ?? Enumerable.Empty<FeatureContent>())
                {
                    if (feature == null) continue;
                    if (string.IsNullOrEmpty(feature.Uid) || string.IsNullOrWhiteSpace(feature.CotXml))
                        continue;

                    // The same feature selected twice (two layers serving one source) travels once.
                    string entryPath = FeatureFolder + "/" + Sanitize(feature.Uid, 96) + ".cot";
                    if (!usedNames.Add(entryPath)) continue;

                    plan.Entries.Add(new PlannedEntry
                    {
                        PackagePath = entryPath,
                        Uid = feature.Uid,
                        Kind = EntryKind.Feature,
                        Content = feature.CotXml,
                    });
                }
            }

            // With several layers the per-layer loop still interleaves: layer A's features precede
            // layer B's iconsets. Reordering the finished plan puts EVERY iconset ahead of every
            // CoT, which is what the ordering is for.
            //
            // OrderBy, not List.Sort: LINQ's OrderBy is documented stable, List.Sort is an
            // introsort and is not. Within a group the build order carries meaning — features
            // follow their own layer's config — and an unstable sort would shuffle it for free.
            var ordered = plan.Entries.OrderBy(e => ImportRank(e.Kind)).ToList();
            plan.Entries.Clear();
            plan.Entries.AddRange(ordered);

            return plan;
        }

        /// <summary>Import precedence: lower goes into the package first. Icons must exist before
        /// the markers that reference them are created.</summary>
        private static int ImportRank(EntryKind kind)
        {
            switch (kind)
            {
                case EntryKind.Iconset: return 0;
                case EntryKind.LayerConfig: return 1;
                default: return 2;   // Feature
            }
        }

        /// <summary>Where a generated iconset zip lives on this machine.</summary>
        public static string IconsetZipPath(string uid)
        {
            return Path.Combine(IconsetDirectory, (uid ?? string.Empty) + ".zip");
        }

        /// <summary>One line describing what a package contains, for the dialog and the status bar.</summary>
        public static string Describe(PackagePlan plan)
        {
            if (plan == null || plan.LayerCount == 0) return "nothing selected";

            string layers = plan.LayerCount == 1 ? "1 layer" : plan.LayerCount + " layers";
            string icons = plan.IconsetCount == 0
                ? "no iconsets"
                : plan.IconsetCount == 1 ? "1 iconset" : plan.IconsetCount + " iconsets";

            string text = plan.FeatureCount > 0
                ? (plan.FeatureCount == 1 ? "1 feature" : plan.FeatureCount + " features")
                  + " from " + layers + ", " + icons
                : layers + ", " + icons;

            if (plan.MissingIconsets.Count > 0)
                text += " (" + plan.MissingIconsets.Count + " not generated yet)";
            return text;
        }
    }
}
