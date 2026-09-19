using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// ArcGIS renderer → shape/marker style extractor. Line-for-line port of the ATAK plugin's
    /// <c>com.atakmap.android.featurelink.arcgis.AutoSymbology</c> (and the identical
    /// <c>CloudTAK/plugin/lib/autoSymbology.ts</c>), which had <b>no WinTAK counterpart at all</b>
    /// — audit finding C-07, the owner's field defect, and worse on this platform than any other:
    /// <c>ArcGisFeatureService.FetchLayerInfoAsync</c> read the layer metadata and discarded
    /// <c>drawingInfo</c> outright, so a layer's own published symbology was never resolved on the
    /// download path and every feature rendered with WinTAK's default marker.
    ///
    /// Extracts esriSMS (point marker shape/colour/size), esriSLS (line stroke) and esriSFS
    /// (polygon fill + outline) symbol JSON into plain style structs, keyed by renderer field
    /// value. No network I/O, no side effects — pure JSON parsing, which is why it is the first
    /// thing the new test suite covers.
    ///
    /// Semantics deliberately match the Java original exactly, including:
    ///  • class-breaks renderers contribute only a fallback style (numeric-range matching is not
    ///    resolved per feature — same documented limitation as AutoIconset);
    ///  • which of stroke/fill/marker is populated is driven by each symbol's own <c>type</c>,
    ///    not by a geometry hint, so mixed-symbol renderers resolve without the caller knowing
    ///    the layer's geometry type;
    ///  • <see cref="EsriColor"/> preserves alpha (translucent polygon fills are common ArcGIS
    ///    styling), unlike the display-config hex path which is opaque RGB.
    /// </summary>
    public static class AutoSymbology
    {
        public sealed class StrokeStyle
        {
            public Color Color { get; }
            public float WidthPx { get; }
            /// <summary>"solid" | "dash" | "dot"</summary>
            public string Dash { get; }

            public StrokeStyle(Color color, float widthPx, string dash)
            {
                Color = color; WidthPx = widthPx; Dash = dash;
            }
        }

        public sealed class FillStyle
        {
            public Color Color { get; }
            /// <summary>"solid" | "none"</summary>
            public string Style { get; }
            /// <summary>The fill symbol's own outline (esriSFS.outline is itself an esriSLS) — may be null.</summary>
            public StrokeStyle Outline { get; }

            public FillStyle(Color color, string style, StrokeStyle outline)
            {
                Color = color; Style = style; Outline = outline;
            }
        }

        public sealed class MarkerStyle
        {
            public Color Color { get; }
            /// <summary>"circle" | "square" | "diamond" | "triangle" | "cross" | "x"</summary>
            public string Shape { get; }
            public float SizePx { get; }

            public MarkerStyle(Color color, string shape, float sizePx)
            {
                Color = color; Shape = shape; SizePx = sizePx;
            }
        }

        public sealed class Result
        {
            /// <summary>Driving field for the by-value maps; empty for a single-symbol renderer.</summary>
            public string Field { get; }
            public StrokeStyle SingleStroke { get; }
            public FillStyle SingleFill { get; }
            public MarkerStyle SingleMarker { get; }
            public IDictionary<string, StrokeStyle> StrokeByValue { get; }
            public IDictionary<string, FillStyle> FillByValue { get; }
            public IDictionary<string, MarkerStyle> MarkerByValue { get; }

            public Result(string field, StrokeStyle singleStroke, FillStyle singleFill, MarkerStyle singleMarker,
                IDictionary<string, StrokeStyle> strokeByValue, IDictionary<string, FillStyle> fillByValue,
                IDictionary<string, MarkerStyle> markerByValue)
            {
                Field = field ?? string.Empty;
                SingleStroke = singleStroke;
                SingleFill = singleFill;
                SingleMarker = singleMarker;
                StrokeByValue = strokeByValue ?? new Dictionary<string, StrokeStyle>();
                FillByValue = fillByValue ?? new Dictionary<string, FillStyle>();
                MarkerByValue = markerByValue ?? new Dictionary<string, MarkerStyle>();
            }

            public bool IsEmpty =>
                SingleStroke == null && SingleFill == null && SingleMarker == null
                && StrokeByValue.Count == 0 && FillByValue.Count == 0 && MarkerByValue.Count == 0;

            /// <summary>True when the renderer carries polyline/polygon (esriSLS/esriSFS) styling —
            /// the part of a Portal-authored config that was previously dropped on WinTAK with no
            /// warning and no log line (Appendix F §5's concrete interop break).</summary>
            public bool HasShapeStyling =>
                SingleStroke != null || SingleFill != null
                || StrokeByValue.Count > 0 || FillByValue.Count > 0;
        }

        /// <summary>One classified renderer category: the attribute value it matches, its label,
        /// and its symbol.</summary>
        public sealed class ValueEntry
        {
            public string Value { get; set; }
            public string Label { get; set; }
            public JObject Symbol { get; set; }
        }

        /// <summary>
        /// Yields a renderer's classified categories, in renderer order, from EITHER unique-value
        /// layout.
        ///
        /// <para><b>Why both.</b> ArcGIS has two shapes for the same thing. The classic one is a
        /// flat <c>uniqueValueInfos</c> array. Modern ArcGIS Online publishes
        /// <c>uniqueValueGroups[].classes[]</c> instead, where each class carries
        /// <c>values: [["A"]]</c> — an array of arrays, because a unique-value renderer can key on
        /// up to three fields.</para>
        ///
        /// <para>Reading only the classic shape is silently catastrophic rather than merely
        /// incomplete: a modern renderer's categories are simply not seen, so the extractors fall
        /// through to <c>defaultSymbol</c> and every feature gets one symbol. That is exactly what
        /// happened in the field — a layer showing twelve distinct symbols in ArcGIS produced a
        /// single orange dot on every marker, with no error anywhere, because the one thing we
        /// did find was the default.</para>
        ///
        /// <para>Order is preserved: <c>AUTO-ICONSET-SPEC.md</c> §5.3 assigns filename collision
        /// suffixes in renderer array order, and every platform must agree on it.</para>
        /// </summary>
        public static List<ValueEntry> EnumerateValueEntries(JObject renderer)
        {
            var entries = new List<ValueEntry>();
            if (renderer == null) return entries;

            // Classic: flat uniqueValueInfos.
            if (renderer["uniqueValueInfos"] is JArray infos)
            {
                foreach (var info in infos.OfType<JObject>())
                {
                    var symbol = info["symbol"] as JObject;
                    if (symbol == null) continue;
                    entries.Add(new ValueEntry
                    {
                        Value = OptString(info, "value", null),
                        Label = OptString(info, "label", null),
                        Symbol = symbol,
                    });
                }
            }

            // Modern: uniqueValueGroups -> classes -> values[][].
            if (renderer["uniqueValueGroups"] is JArray groups)
            {
                // Multi-field renderers join their key parts with this; ArcGIS defaults to ",".
                string delimiter = OptString(renderer, "fieldDelimiter", ",");

                foreach (var group in groups.OfType<JObject>())
                {
                    if (!(group["classes"] is JArray classes)) continue;
                    foreach (var cls in classes.OfType<JObject>())
                    {
                        var symbol = cls["symbol"] as JObject;
                        if (symbol == null) continue;
                        string label = OptString(cls, "label", null);

                        // One class can cover SEVERAL values sharing a symbol; each becomes its
                        // own entry so per-value resolution finds any of them.
                        var values = cls["values"] as JArray;
                        if (values == null || values.Count == 0)
                        {
                            entries.Add(new ValueEntry { Value = null, Label = label, Symbol = symbol });
                            continue;
                        }

                        foreach (var v in values)
                        {
                            string composed;
                            if (v is JArray parts)
                                composed = string.Join(delimiter,
                                    parts.Select(p => p == null || p.Type == JTokenType.Null
                                        ? string.Empty
                                        : p.ToString(Newtonsoft.Json.Formatting.None).Trim('"')));
                            else
                                composed = v.ToString(Newtonsoft.Json.Formatting.None).Trim('"');

                            entries.Add(new ValueEntry
                            {
                                Value = composed,
                                Label = label,
                                Symbol = symbol,
                            });
                        }
                    }
                }
            }

            return entries;
        }

        /// <summary>
        /// Extracts esriSMS/esriSLS/esriSFS styling from a renderer, mirroring the
        /// simple/uniqueValue/classBreaks/defaultSymbol traversal AutoIconset uses.
        /// </summary>
        /// <param name="renderer">the <c>drawingInfo.renderer</c> object, or a Web-Map per-layer
        /// style override the caller already resolved (AUTO-ICONSET-SPEC.md §2.3).</param>
        /// <param name="fieldOverride">renderer field override; wins over the renderer's own field.</param>
        public static Result Extract(JObject renderer, string fieldOverride = null)
        {
            var strokeByValue = new Dictionary<string, StrokeStyle>(StringComparer.Ordinal);
            var fillByValue = new Dictionary<string, FillStyle>(StringComparer.Ordinal);
            var markerByValue = new Dictionary<string, MarkerStyle>(StringComparer.Ordinal);
            StrokeStyle singleStroke = null;
            FillStyle singleFill = null;
            MarkerStyle singleMarker = null;
            string field = string.Empty;

            if (renderer != null)
            {
                string type = OptString(renderer, "type", "simple");

                if (type == "uniqueValue" || type == "uniqueValueRenderer")
                {
                    field = OptString(renderer, "field1", null) ?? OptString(renderer, "field", string.Empty);

                    // Covers BOTH uniqueValueInfos and uniqueValueGroups/classes — see
                    // EnumerateValueEntries. Reading only the former made a modern ArcGIS Online
                    // renderer look like it had no categories at all.
                    foreach (var entry in EnumerateValueEntries(renderer))
                    {
                        string value = entry.Value;
                        if (string.IsNullOrEmpty(value) || entry.Symbol == null) continue;
                        var s = StrokeFrom(entry.Symbol);
                        var f = FillFrom(entry.Symbol);
                        var m = MarkerFrom(entry.Symbol);
                        if (s != null) strokeByValue[value] = s;
                        if (f != null) fillByValue[value] = f;
                        if (m != null) markerByValue[value] = m;
                    }
                    if (renderer["defaultSymbol"] is JObject defSymbol)
                    {
                        singleStroke = StrokeFrom(defSymbol);
                        singleFill = FillFrom(defSymbol);
                        singleMarker = MarkerFrom(defSymbol);
                    }
                }
                else if (type == "classBreaks" || type == "classBreaksRenderer")
                {
                    field = OptString(renderer, "field", string.Empty);
                    // Class breaks match by numeric range, not value equality — not resolvable per
                    // feature here (same limitation as AutoIconset); only a fallback symbol is
                    // usable as a single style for the whole layer.
                    var fallback = renderer["defaultSymbol"] as JObject;
                    if (fallback == null)
                    {
                        // Deliberately NOT classBreakInfos[0], which this used to fall back to.
                        // Break 0 is the LOWEST-value class, so using it styles the whole layer as
                        // if every feature sat in the bottom bucket — a map that looks
                        // authoritative and is not. ATAK refuses here for exactly this reason;
                        // WinTAK doing otherwise was both a divergence and the more dangerous of
                        // the two behaviours. Refuse to style rather than mislead.
                        Log.Info("classBreaks renderer has no defaultSymbol — declining to style "
                                 + "(numeric range matching is not implemented).");
                    }
                    if (fallback != null)
                    {
                        singleStroke = StrokeFrom(fallback);
                        singleFill = FillFrom(fallback);
                        singleMarker = MarkerFrom(fallback);
                    }
                }
                else
                {
                    // simple / bare symbol renderer — single style, no field
                    var symbol = renderer["symbol"] as JObject;
                    singleStroke = StrokeFrom(symbol);
                    singleFill = FillFrom(symbol);
                    singleMarker = MarkerFrom(symbol);
                }
            }

            if (!string.IsNullOrEmpty(fieldOverride)) field = fieldOverride;

            return new Result(field, singleStroke, singleFill, singleMarker,
                strokeByValue, fillByValue, markerByValue);
        }

        // -------------------------------------------------------------------------
        // Symbol JSON → style struct
        // -------------------------------------------------------------------------

        internal static StrokeStyle StrokeFrom(JObject symbol)
        {
            if (symbol == null || OptString(symbol, "type", string.Empty) != "esriSLS") return null;
            Color color = EsriColor(symbol["color"] as JArray, Color.Blue);
            float width = (float)OptDouble(symbol, "width", 1.0);
            string dash = DashFromEsriStyle(OptString(symbol, "style", string.Empty));
            return new StrokeStyle(color, width, dash);
        }

        internal static FillStyle FillFrom(JObject symbol)
        {
            if (symbol == null || OptString(symbol, "type", string.Empty) != "esriSFS") return null;
            Color color = EsriColor(symbol["color"] as JArray, Color.Yellow);
            string style = OptString(symbol, "style", string.Empty) == "esriSFSNull" ? "none" : "solid";
            StrokeStyle outline = StrokeFrom(symbol["outline"] as JObject);
            return new FillStyle(color, style, outline);
        }

        internal static MarkerStyle MarkerFrom(JObject symbol)
        {
            if (symbol == null || OptString(symbol, "type", string.Empty) != "esriSMS") return null;
            Color color = EsriColor(symbol["color"] as JArray, Color.Red);
            string shape = MarkerShapeFromEsri(OptString(symbol, "style", string.Empty));
            float size = (float)OptDouble(symbol, "size", 8.0);
            return new MarkerStyle(color, shape, size);
        }

        /// <summary>ArcGIS symbol colours are <c>[r,g,b,a]</c> (alpha last, 0-255). Alpha is
        /// preserved here — translucent polygon fills are common ArcGIS styling.</summary>
        internal static Color EsriColor(JArray arr, Color fallback)
        {
            if (arr == null || arr.Count < 3) return fallback;
            int r = OptInt(arr, 0, 0);
            int g = OptInt(arr, 1, 0);
            int b = OptInt(arr, 2, 0);
            int a = arr.Count >= 4 ? OptInt(arr, 3, 255) : 255;
            return Color.FromArgb(Clamp255(a), Clamp255(r), Clamp255(g), Clamp255(b));
        }

        internal static string DashFromEsriStyle(string esriStyle)
        {
            switch (esriStyle)
            {
                case "esriSLSDash":
                case "esriSLSDashDot":
                case "esriSLSDashDotDot":
                    return "dash";
                case "esriSLSDot":
                    return "dot";
                default:
                    return "solid";
            }
        }

        internal static string MarkerShapeFromEsri(string esriStyle)
        {
            switch (esriStyle)
            {
                case "esriSMSSquare": return "square";
                case "esriSMSDiamond": return "diamond";
                case "esriSMSTriangle": return "triangle";
                case "esriSMSCross": return "cross";
                case "esriSMSX": return "x";
                default: return "circle";
            }
        }

        // -------------------------------------------------------------------------
        // Culture-invariant JSON accessors
        // -------------------------------------------------------------------------
        // Every numeric read below pins InvariantCulture. The audit measured zero
        // CultureInfo.InvariantCulture uses in either tree; a de-DE machine round-tripping a
        // JSON numeric through the default culture is the classic silent-wrong-symbology bug.

        private static string OptString(JObject o, string key, string fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None).Trim('"');
        }

        private static double OptDouble(JObject o, string key, double fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            double parsed;
            return double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static int OptInt(JArray arr, int index, int fallback)
        {
            if (arr == null || index >= arr.Count) return fallback;
            var t = arr[index];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (int)Math.Round((double)t);
            int parsed;
            return int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static int Clamp255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
    }
}
