using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
                    if (renderer["uniqueValueInfos"] is JArray infos)
                    {
                        foreach (var infoToken in infos)
                        {
                            if (!(infoToken is JObject info)) continue;
                            string value = OptString(info, "value", string.Empty);
                            var symbol = info["symbol"] as JObject;
                            if (value.Length == 0 || symbol == null) continue;
                            var s = StrokeFrom(symbol);
                            var f = FillFrom(symbol);
                            var m = MarkerFrom(symbol);
                            if (s != null) strokeByValue[value] = s;
                            if (f != null) fillByValue[value] = f;
                            if (m != null) markerByValue[value] = m;
                        }
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
                    if (fallback == null && renderer["classBreakInfos"] is JArray breaks && breaks.Count > 0)
                        fallback = (breaks[0] as JObject)?["symbol"] as JObject;
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
