using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// WinTAK's ArcGIS renderer → iconset generator. The C# port of ATAK's
    /// <c>AutoIconset.java</c>, implementing <c>AUTO-ICONSET-SPEC.md</c>.
    ///
    /// <para><b>Why this exists.</b> WinTAK could already read a layer's renderer and could
    /// already emit a <c>&lt;usericon iconsetpath&gt;</c> on each marker — but nothing ever
    /// created the iconset those paths referred to, so every picture-marker layer fell back to
    /// default icons. The plugin said so in its own log, once per layer, per sync:
    /// <c>"has a renderer this build cannot translate (picture-marker-only renderers need an
    /// installed iconset)"</c>. This class closes that gap.</para>
    ///
    /// <para><b>The contract is string identity, not pixels.</b> ATAK, WinTAK, CloudTAK and TAK
    /// Portal each generate their own zip from the same ArcGIS renderer, independently. The PNG
    /// bytes need not match and the zips need not be byte-identical. What MUST match is the
    /// triple <c>{uid}/{group}/{filename}</c>, because that string is what travels in a CoT and
    /// what every peer resolves against its own installed set. Every derivation below is
    /// therefore fixed by spec rather than by taste, and is asserted against the shared golden
    /// vectors in <c>TAKPortal/test/fixtures/auto-iconset-golden-vectors.json</c>.</para>
    ///
    /// <para><b>Culture is the live hazard on this platform.</b> Under <c>tr-TR</c>,
    /// <c>"I".ToLower()</c> is <c>"ı"</c> (dotless), which would change a hashed URL and silently
    /// desynchronise this device's icons from every other platform's — the failure would appear
    /// only on Turkish-locale machines and only as "icons don't match". Every case fold and every
    /// numeric format here is explicitly invariant, and the test suite runs the whole surface
    /// under a culture matrix.</para>
    ///
    /// <para>Deliberately free of <c>WinTak.*</c>, WPF and <c>System.Drawing</c> so the whole file
    /// compiles into the SDK-free test project. The one part that needs the host —
    /// installing the zip — lives in <see cref="IconsetInstaller"/>.</para>
    /// </summary>
    public static class AutoIconset
    {
        /// <summary>Mirrors <c>AUTO-ICONSET-SPEC.md</c>'s version and the <c>version</c> attribute
        /// written into <c>iconset.xml</c>. Bumping the spec bumps all three (§8).</summary>
        public const int SpecVersion = 1;

        /// <summary>§5.1 — the sanitized group base is capped at 60 characters, ONCE, before the
        /// suffix is appended. Re-truncating downstream is a real bug the golden vectors carry a
        /// dedicated section for (<c>c39DoubleTruncation</c>).</summary>
        public const int MaxGroupBaseChars = 60;

        public const string GroupSuffix = " Icons";

        /// <summary>§6 — the filename for a default symbol that carries no label.</summary>
        public const string DefaultIconFileName = "Other.png";

        // ─────────────────────────────────────────────────────────────────────────
        // §2 Canonicalization
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// §2 — turns a pasted link into the canonical
        /// <c>{scheme}://{host}/…/FeatureServer/{layerId}</c> form that the UID hashes.
        ///
        /// <para>Two operators pasting textually different but equivalent links must converge
        /// here, or they generate different UIDs for the same layer and their icons stop
        /// resolving against each other.</para>
        ///
        /// <para>Throws <see cref="ArgumentException"/> for a Web Map link rather than hashing it
        /// raw (§2.3): a Web Map item id is not a layer URL, and hashing it would mint a UID no
        /// other platform can reproduce. Web-map resolution is explicitly allowed to be
        /// unimplemented, but silently hashing the wrong thing is not.</para>
        /// </summary>
        public static string Canonicalize(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                throw new ArgumentException("The layer URL is empty.", nameof(rawUrl));

            string url = rawUrl.Trim();

            if (LooksLikeWebMap(url))
                throw new ArgumentException(
                    "That is a Web Map link, not a feature layer. Open the layer itself and use its "
                    + "/FeatureServer/{id} URL.", nameof(rawUrl));

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                throw new ArgumentException("That is not a valid absolute URL.", nameof(rawUrl));

            // §2.1(2): scheme and host lowercased, path case preserved — ArcGIS REST paths are
            // case-sensitive, so folding the path would break the fetch as well as the hash.
            string scheme = uri.Scheme.ToLowerInvariant();
            string host = uri.Host.ToLowerInvariant();
            if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            // §2.1(3): query and fragment dropped. Uri.AbsolutePath already excludes both.
            string path = uri.AbsolutePath;

            // §2.1(5): collapse duplicate slashes inside the path.
            while (path.IndexOf("//", StringComparison.Ordinal) >= 0)
                path = path.Replace("//", "/");

            // §2.1(4): drop a trailing slash.
            path = path.TrimEnd('/');

            string canonical = scheme + "://" + host + path;
            return EnsureLayerId(canonical);
        }

        /// <summary>§2.2 — append <c>/0</c> to a bare service root; accept an explicit layer id.
        /// The <c>FeatureServer</c>/<c>MapServer</c> match is case-insensitive, but the segment is
        /// preserved exactly as written because the path is not folded.</summary>
        private static string EnsureLayerId(string canonical)
        {
            string[] segments = canonical.Split('/');
            string last = segments[segments.Length - 1];
            string previous = segments.Length >= 2 ? segments[segments.Length - 2] : string.Empty;

            bool lastIsServer = IsServerSegment(last);
            bool previousIsServer = IsServerSegment(previous);

            if (lastIsServer) return canonical + "/0";

            if (previousIsServer && IsLayerId(last)) return canonical;

            throw new ArgumentException(
                "That URL does not look like an ArcGIS feature layer — it should end in "
                + "/FeatureServer, /FeatureServer/0 or /MapServer/0.");
        }

        private static bool IsServerSegment(string segment) =>
            string.Equals(segment, "FeatureServer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "MapServer", StringComparison.OrdinalIgnoreCase);

        private static bool IsLayerId(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            foreach (char c in segment) if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool LooksLikeWebMap(string url) =>
            url.IndexOf("/home/item.html", StringComparison.OrdinalIgnoreCase) >= 0
            || url.IndexOf("/sharing/rest/content/items/", StringComparison.OrdinalIgnoreCase) >= 0;

        // ─────────────────────────────────────────────────────────────────────────
        // §4 UID
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// §4 — <c>lowercase_hex(SHA-256(utf8(canonicalUrl + "/" + fieldName)))</c>, written
        /// verbatim as <c>iconset.xml/@uid</c>.
        ///
        /// <para><paramref name="fieldName"/> case is <b>significant</b> and must never be folded:
        /// <c>DAMAGE_LEVEL</c> and <c>damage_level</c> are different ArcGIS fields and hash to
        /// different UIDs. Pass <see cref="string.Empty"/> for a single-symbol renderer.</para>
        /// </summary>
        public static string Uid(string canonicalUrl, string fieldName)
        {
            if (canonicalUrl == null) throw new ArgumentNullException(nameof(canonicalUrl));
            string input = canonicalUrl + "/" + (fieldName ?? string.Empty);

            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(64);
                foreach (byte b in digest)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // §5 Group and filename derivation
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>§5.1 — <c>[^A-Za-z0-9 _-]</c> → <c>_</c>, whitespace runs collapsed to one
        /// space, trimmed, then capped at 60. The cap is applied here and nowhere else.</summary>
        public static string SanitizeGroupBase(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var sb = new StringBuilder(name.Length);
            bool pendingSpace = false;
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }

                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }

                bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                            || (c >= '0' && c <= '9') || c == '_' || c == '-';
                sb.Append(allowed ? c : '_');
            }

            string cleaned = sb.ToString().Trim();
            return cleaned.Length > MaxGroupBaseChars
                ? cleaned.Substring(0, MaxGroupBaseChars).Trim()
                : cleaned;
        }

        /// <summary>§5.1 — the sanitized layer name plus the literal <c>" Icons"</c> suffix,
        /// appended AFTER sanitization and after the cap.</summary>
        public static string GroupFor(string layerName) => SanitizeGroupBase(layerName) + GroupSuffix;

        /// <summary>
        /// §5.2 — last path segment → strip a trailing <c>.png</c> (case-insensitive) →
        /// <c>[^A-Za-z0-9._-]</c> → <c>_</c> → append <c>.png</c>.
        ///
        /// <para>Not lowercased, and a space becomes <c>_</c> rather than being stripped — both
        /// are spec, and both are the kind of "tidy-up" that would silently break cross-platform
        /// resolution if someone improved it later.</para>
        /// </summary>
        public static string FileName(string rawLabel)
        {
            string raw = rawLabel ?? string.Empty;

            int cut = raw.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) raw = raw.Substring(cut + 1);

            if (raw.Length >= 4 && raw.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                            || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                sb.Append(allowed ? c : '_');
            }

            string baseName = sb.ToString();
            if (baseName.Length == 0) baseName = "icon";
            return baseName + ".png";
        }

        /// <summary>§5.3 — on collision append <c>_2</c>, <c>_3</c>, … before the extension.
        /// Assignment follows renderer array order, so every platform lands on the same suffix
        /// for the same renderer. <paramref name="taken"/> is updated with the result.</summary>
        public static string Dedupe(string fileName, ISet<string> taken)
        {
            if (taken == null) throw new ArgumentNullException(nameof(taken));
            if (taken.Add(fileName)) return fileName;

            string stem = fileName;
            string extension = string.Empty;
            int dot = fileName.LastIndexOf('.');
            if (dot > 0) { stem = fileName.Substring(0, dot); extension = fileName.Substring(dot); }

            for (int n = 2; n < int.MaxValue; n++)
            {
                string candidate = stem + "_" + n.ToString(CultureInfo.InvariantCulture) + extension;
                if (taken.Add(candidate)) return candidate;
            }
            throw new InvalidOperationException("Could not find a unique icon file name.");
        }

        /// <summary>§5.4 — the string that travels in a CoT and that every peer resolves.</summary>
        public static string IconsetPath(string uid, string group, string fileName) =>
            uid + "/" + group + "/" + fileName;

        // ─────────────────────────────────────────────────────────────────────────
        // §3 Renderer extraction (esriPMS only)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>One extracted picture-marker symbol, in renderer array order.</summary>
        public sealed class PictureSymbol
        {
            /// <summary>The <c>uniqueValue</c>/<c>classBreaks</c> value this symbol applies to;
            /// null for the default/single symbol.</summary>
            public string Value { get; set; }
            public string FileName { get; set; }
            public byte[] Png { get; set; }
            public bool IsDefault { get; set; }
        }

        public sealed class Extraction
        {
            /// <summary>§3 driving field; empty for a single-symbol renderer.</summary>
            public string Field { get; set; } = string.Empty;

            /// <summary>In renderer array order — §5.3 depends on it.</summary>
            public List<PictureSymbol> Symbols { get; } = new List<PictureSymbol>();

            /// <summary>Symbols skipped because the renderer used a format no platform supports
            /// (notably <c>CIMSymbolReference</c>). Surfaced so a richly-styled modern layer
            /// yielding only a default icon is visible rather than mysterious.</summary>
            public int UnsupportedSymbols { get; set; }

            /// <summary>The renderer's own type string, for diagnostics.</summary>
            public string RendererType { get; set; } = string.Empty;

            /// <summary>How many classified categories the renderer DECLARED, before any were
            /// skipped. The single most valuable diagnostic in this class: declared=0 against a
            /// layer the operator can see is richly styled means the categories are in a layout
            /// this build does not read — which is exactly how the uniqueValueGroups gap hid for
            /// as long as it did.</summary>
            public int DeclaredEntries { get; set; }

            /// <summary>Distinct symbol types that were seen and not used, e.g. "esriSMS",
            /// "CIMSymbolReference". Drives <see cref="ExplainExtraction"/>.</summary>
            public HashSet<string> SkippedTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

            public bool IsEmpty => Symbols.Count == 0;
        }

        /// <summary>
        /// §3 — pulls every <c>esriPMS</c> symbol with embedded <c>imageData</c> out of a
        /// renderer, preserving array order.
        ///
        /// <para>Complements <see cref="AutoSymbology.Extract"/> rather than replacing it: that
        /// one handles <c>esriSMS</c>/<c>esriSLS</c>/<c>esriSFS</c> (colour and shape), this one
        /// handles the bitmap symbols. The traversal shapes are deliberately parallel so a change
        /// in ArcGIS's renderer format gets fixed in both.</para>
        /// </summary>
        public static Extraction ExtractPictureSymbols(JObject renderer, string fieldOverride = null)
        {
            var result = new Extraction();
            if (renderer == null) return result;

            string type = (string)renderer["type"] ?? string.Empty;
            result.RendererType = type.Length == 0 ? "(untyped)" : type;

            if (!string.IsNullOrEmpty(fieldOverride)) result.Field = fieldOverride;
            else if (type.IndexOf("uniqueValue", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Field = (string)renderer["field1"] ?? (string)renderer["field"] ?? string.Empty;
            else if (type.IndexOf("classBreaks", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Field = (string)renderer["field"] ?? string.Empty;

            var taken = new HashSet<string>(StringComparer.Ordinal);

            // Classified categories, from EITHER unique-value layout. Reading only the classic
            // uniqueValueInfos array meant a modern ArcGIS Online renderer
            // (uniqueValueGroups/classes) yielded no categories, fell through to defaultSymbol,
            // and stamped one icon on every feature — see AutoSymbology.EnumerateValueEntries.
            var classified = AutoSymbology.EnumerateValueEntries(renderer);
            result.DeclaredEntries = classified.Count;
            foreach (var entry in classified)
            {
                string raw = string.IsNullOrEmpty(entry.Label) ? entry.Value : entry.Label;
                AddSymbol(result, taken, entry.Symbol, raw, entry.Value, isDefault: false);
            }

            // Class breaks match by numeric range rather than value equality, so they cannot be
            // resolved per feature; only their symbols are harvested, as they were before.
            if (renderer["classBreakInfos"] is JArray breaks)
            {
                result.DeclaredEntries += breaks.Count;
                foreach (var entry in breaks.OfType<JObject>())
                {
                    // Stringify through JToken rather than casting: a JSON NUMBER cast straight
                    // to string yields null on some token types, and class-break values are
                    // numeric by definition. ATAK reads these with optString, which stringifies.
                    string raw = TokenText(entry["label"]);
                    if (string.IsNullOrEmpty(raw))
                        raw = TokenText(entry["value"]) ?? TokenText(entry["classMaxValue"]);

                    AddSymbol(result, taken, entry["symbol"] as JObject, raw,
                        TokenText(entry["value"]), isDefault: false);
                }
            }

            // The default symbol, and the single-symbol renderer, share one shape: a bare
            // `symbol` with an optional `defaultLabel`. §6 names it Other.png when unlabelled.
            var defaultSymbol = renderer["defaultSymbol"] as JObject ?? renderer["symbol"] as JObject;
            if (defaultSymbol != null)
            {
                string label = (string)renderer["defaultLabel"];
                AddSymbol(result, taken, defaultSymbol, label, value: null, isDefault: true);
            }

            return result;
        }

        /// <summary>Reads a token as text regardless of its JSON type. ArcGIS writes category
        /// values as numbers as often as strings, and a direct string cast does not survive
        /// that.</summary>
        private static string TokenText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.String
                ? (string)token
                : token.ToString(Newtonsoft.Json.Formatting.None).Trim('"');
        }

        private static void AddSymbol(Extraction result, ISet<string> taken, JObject symbol,
            string rawLabel, string value, bool isDefault)
        {
            if (symbol == null) return;

            string symbolType = (string)symbol["type"] ?? string.Empty;
            if (!string.Equals(symbolType, "esriPMS", StringComparison.OrdinalIgnoreCase))
            {
                // esriSMS/SLS/SFS are AutoSymbology's job. Anything else — CIMSymbolReference
                // above all — is unsupported everywhere. Record the TYPE, not just a count: an
                // operator looking at a layer that produced one icon needs to know whether the
                // symbols were geometric (so colour styling applies) or an encoding nothing
                // parses. ATAK has logged this for a while; WinTAK not doing so is why the
                // uniqueValueGroups gap stayed invisible in the logs.
                result.SkippedTypes.Add(symbolType.Length == 0 ? "(untyped)" : symbolType);
                if (symbolType.IndexOf("CIM", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.UnsupportedSymbols++;
                return;
            }

            string imageData = (string)symbol["imageData"];
            if (string.IsNullOrEmpty(imageData))
            {
                result.SkippedTypes.Add("esriPMS(no imageData)");
                return;
            }

            byte[] png;
            try { png = Convert.FromBase64String(imageData); }
            catch (FormatException)
            {
                result.SkippedTypes.Add("esriPMS(bad base64)");
                return;
            }
            if (png.Length == 0) { result.SkippedTypes.Add("esriPMS(empty)"); return; }

            // §6: an unlabelled default becomes Other.png; everything else goes through §5.2.
            string fileName = isDefault && string.IsNullOrEmpty(rawLabel)
                ? DefaultIconFileName
                : FileName(rawLabel);

            result.Symbols.Add(new PictureSymbol
            {
                Value = value,
                FileName = Dedupe(fileName, taken),
                Png = png,
                IsDefault = isDefault,
            });
        }

        /// <summary>
        /// One line explaining why a renderer produced the icon count it did.
        ///
        /// <para>Ported from ATAK's <c>AutoIconset.logExtraction</c>, which WinTAK lacked. That
        /// absence is why a renderer layout this build could not read stayed invisible for as
        /// long as it did: the extractors behaved correctly for what they saw, produced one
        /// default icon, and said nothing. <c>declared=0</c> against a layer the operator can see
        /// is richly styled is the tell, and it only helps if it is written down.</para>
        ///
        /// <para>Returns null when there is nothing worth saying — a fully successful extraction
        /// is not news.</para>
        /// </summary>
        public static string ExplainExtraction(Extraction extraction)
        {
            if (extraction == null) return null;

            var reasons = new List<string>();

            if (extraction.DeclaredEntries == 0 && extraction.Symbols.Count <= 1)
                reasons.Add("the renderer declared no classified categories this build can read — "
                          + "if the layer shows several symbols in ArcGIS, its categories are in a "
                          + "layout this extractor does not parse");

            if (extraction.SkippedTypes.Contains("esriSMS"))
                reasons.Add("esriSMS is a geometric marker (shape and colour, no embedded image), "
                          + "so there are no icon bytes to extract — those categories get colour "
                          + "styling instead");

            if (extraction.SkippedTypes.Any(t => t.IndexOf("CIM", StringComparison.OrdinalIgnoreCase) >= 0))
                reasons.Add("CIMSymbolReference is the modern ArcGIS Map Viewer encoding and is not "
                          + "parsed by any TAK platform");

            foreach (string skipped in extraction.SkippedTypes)
                if (skipped.StartsWith("esriPMS(", StringComparison.Ordinal))
                    reasons.Add("a picture symbol carried no usable image data (" + skipped + ")");

            string summary = string.Format(CultureInfo.InvariantCulture,
                "renderer '{0}': declared={1} extracted={2}",
                extraction.RendererType, extraction.DeclaredEntries, extraction.Symbols.Count);

            if (reasons.Count == 0)
                return extraction.Symbols.Count == 0 ? summary : null;

            return summary + " — " + string.Join(" ", reasons) + ".";
        }

        // ─────────────────────────────────────────────────────────────────────────
        // §7 iconset.xml and the zip
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// §7 — the manifest ATAK reads to learn the UID.
        ///
        /// <para><b>Each <c>&lt;icon&gt;</c> carries <c>name</c> and nothing else.</b> ATAK parses
        /// this with <c>org.simpleframework.xml</c> in strict mode; an unrecognised attribute
        /// such as <c>group</c> throws and aborts the <i>entire document</i>, at which point ATAK
        /// discards our UID and hashes the raw zip instead — which silently breaks the
        /// cross-platform matching this whole mechanism exists for. That was a real, shipped bug
        /// on the ATAK side. Do not add attributes here.</para>
        /// </summary>
        public static string BuildIconsetXml(string uid, string group, IEnumerable<string> fileNames)
        {
            if (string.IsNullOrEmpty(uid)) throw new ArgumentException("uid is required.", nameof(uid));
            if (string.IsNullOrEmpty(group)) throw new ArgumentException("group is required.", nameof(group));

            var sb = new StringBuilder();
            sb.Append("<iconset name=\"").Append(XmlEscape(group))
              .Append("\" uid=\"").Append(XmlEscape(uid))
              .Append("\" defaultGroup=\"").Append(XmlEscape(group))
              .Append("\" version=\"").Append(SpecVersion.ToString(CultureInfo.InvariantCulture))
              .Append("\">\n");

            foreach (string file in fileNames)
                sb.Append("  <icon name=\"").Append(XmlEscape(file)).Append("\"/>\n");

            sb.Append("</iconset>\n");
            return sb.ToString();
        }

        private static string XmlEscape(string s) => s
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>
        /// Builds the iconset zip in memory: <c>iconset.xml</c> at the root and one
        /// <c>{group}/{filename}</c> entry per PNG, with no deeper nesting.
        ///
        /// <para>Entry names are joined with a literal <c>'/'</c> and never with
        /// <see cref="Path.Combine"/>, which yields <c>'\'</c> on Windows — ATAK derives the group
        /// from the entry's first path segment, so a backslash would produce one group named
        /// <c>"{group}\{file}"</c> and no icon would ever resolve.</para>
        /// </summary>
        public static byte[] BuildZipBytes(string uid, string group, IEnumerable<PictureSymbol> symbols)
        {
            var list = symbols?.ToList() ?? new List<PictureSymbol>();

            using (var buffer = new MemoryStream())
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = zip.CreateEntry("iconset.xml", CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                        writer.Write(BuildIconsetXml(uid, group, list.Select(s => s.FileName)));

                    foreach (var symbol in list)
                    {
                        var entry = zip.CreateEntry(group + "/" + symbol.FileName, CompressionLevel.Optimal);
                        using (var stream = entry.Open())
                            stream.Write(symbol.Png, 0, symbol.Png.Length);
                    }
                }
                return buffer.ToArray();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Display-config synthesis
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Emits the compact <c>sym</c> config that <see cref="DisplayStyleResolver.ResolveIconsetPath"/>
        /// already knows how to read — <c>{"t":"ic","up":…}</c> for a single icon, or
        /// <c>{"t":"adv","f":field,"vs":[{"v":…,"m":"icon","up":…}]}</c> for a per-value set.
        /// Mirrors ATAK's <c>DisplayConfig.forAutoIcons</c>, so producer and consumer stay one
        /// format rather than two.
        /// </summary>
        /// <summary>
        /// Merges picture-marker icons and simple-marker colours into ONE <c>adv</c> config, so a
        /// renderer that mixes both — which is the normal case — renders both.
        ///
        /// <para><b>Why this exists.</b> The first cut treated the two as alternatives: if any
        /// picture symbol was found, icons "won" and the colour config was never built. On a real
        /// layer whose renderer had a handful of badge icons and a dozen coloured circles, that
        /// produced the worst of both — a single icon stamped on every feature and no colours at
        /// all, while ArcGIS showed twelve distinct symbols. The precedence has to be per VALUE,
        /// not per layer.</para>
        ///
        /// <para>The schema supports this directly and always did:
        /// <see cref="DisplayStyleResolver.ResolveColor"/> reads <c>c</c> from any <c>vs</c> entry,
        /// while <see cref="DisplayStyleResolver.ResolveIconsetPath"/> returns a path only for an
        /// entry marked <c>m:"icon"</c>. So one entry can carry a colour, an icon, or both, and
        /// each resolver takes what it understands.</para>
        /// </summary>
        /// <param name="colorConfig">Output of
        /// <see cref="DisplayStyleResolver.BuildMarkerSymConfigJson"/>, or null.</param>
        public static JObject MergeIconAndColorConfig(string uid, string group, Extraction icons,
            JObject colorConfig)
        {
            bool hasIcons = icons != null && !icons.IsEmpty;
            if (!hasIcons) return colorConfig;

            var iconByValue = new Dictionary<string, string>(StringComparer.Ordinal);
            string defaultIconPath = null;
            foreach (var symbol in icons.Symbols)
            {
                string path = IconsetPath(uid, group, symbol.FileName);
                if (symbol.IsDefault || symbol.Value == null) defaultIconPath = path;
                else iconByValue[symbol.Value] = path;
            }

            // The colour config's driving field wins when present: it comes from the same renderer
            // and is what the colour entries are keyed on.
            string field = icons.Field ?? string.Empty;
            if (colorConfig != null)
            {
                string colorField = (string)colorConfig["f"];
                if (!string.IsNullOrEmpty(colorField)) field = colorField;
            }

            var entries = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Start from the colour entries so every value the renderer classified keeps its
            // colour, then attach an icon to the ones that have a picture symbol.
            var colorValues = colorConfig?["uv"] as JArray ?? colorConfig?["vs"] as JArray;
            if (colorValues != null)
            {
                foreach (var e in colorValues.OfType<JObject>())
                {
                    string value = (string)e["v"];
                    if (value == null || !seen.Add(value)) continue;

                    var entry = new JObject { ["v"] = value };
                    string color = (string)e["c"];
                    if (!string.IsNullOrEmpty(color)) entry["c"] = color;

                    string iconPath;
                    if (iconByValue.TryGetValue(value, out iconPath))
                    {
                        entry["m"] = "icon";
                        entry["up"] = iconPath;
                    }
                    entries.Add(entry);
                }
            }

            // Values that have an icon but no colour entry (a renderer category whose symbol is a
            // picture and therefore produced no simple-marker colour).
            foreach (var kv in iconByValue)
            {
                if (!seen.Add(kv.Key)) continue;
                entries.Add(new JObject { ["v"] = kv.Key, ["m"] = "icon", ["up"] = kv.Value });
            }

            if (entries.Count == 0)
            {
                // No per-value classification anywhere — a single picture symbol for the layer.
                return defaultIconPath == null
                    ? colorConfig
                    : new JObject { ["t"] = "ic", ["up"] = defaultIconPath };
            }

            var merged = new JObject
            {
                ["t"] = "adv",
                ["f"] = field,
                ["vs"] = entries,
            };

            // Layer-wide fallbacks, used for a feature whose value matches no entry.
            string fallbackColor = (string)colorConfig?["c"];
            if (!string.IsNullOrEmpty(fallbackColor)) merged["c"] = fallbackColor;
            if (defaultIconPath != null) merged["up"] = defaultIconPath;
            merged["op"] = colorConfig?["op"] ?? 1.0;

            return merged;
        }

        public static JObject BuildIconSymConfig(string uid, string group, Extraction extraction)
        {
            if (extraction == null || extraction.IsEmpty) return null;

            var valued = extraction.Symbols.Where(s => !s.IsDefault && s.Value != null).ToList();
            var fallback = extraction.Symbols.FirstOrDefault(s => s.IsDefault);

            if (valued.Count == 0)
            {
                var only = fallback ?? extraction.Symbols[0];
                return new JObject
                {
                    ["t"] = "ic",
                    ["up"] = IconsetPath(uid, group, only.FileName),
                };
            }

            var values = new JArray();
            foreach (var symbol in valued)
            {
                values.Add(new JObject
                {
                    ["v"] = symbol.Value,
                    ["m"] = "icon",
                    ["up"] = IconsetPath(uid, group, symbol.FileName),
                });
            }

            var config = new JObject
            {
                ["t"] = "adv",
                ["f"] = extraction.Field ?? string.Empty,
                ["vs"] = values,
            };
            if (fallback != null) config["up"] = IconsetPath(uid, group, fallback.FileName);
            return config;
        }
    }
}
