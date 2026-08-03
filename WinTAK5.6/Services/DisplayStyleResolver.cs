using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>Stroke + fill styling for a polyline/polygon feature. Mirrors
    /// <c>DisplayConfig.ShapeStyle</c> in the ATAK tree and <c>ShapeStyle</c> in
    /// <c>CloudTAK/plugin/lib/types.ts</c> field for field.</summary>
    public sealed class ShapeStyle
    {
        public Color StrokeColor { get; }
        public float StrokeWidthPx { get; }
        /// <summary>"solid" | "dash" | "dot"</summary>
        public string StrokeDash { get; }
        public Color FillColor { get; }
        /// <summary>"solid" | "none"</summary>
        public string FillStyle { get; }

        public ShapeStyle(Color strokeColor, float strokeWidthPx, string strokeDash,
            Color fillColor, string fillStyle)
        {
            StrokeColor = strokeColor;
            StrokeWidthPx = strokeWidthPx;
            StrokeDash = strokeDash ?? "solid";
            FillColor = fillColor;
            FillStyle = fillStyle ?? "none";
        }
    }

    /// <summary>
    /// Resolves a feature's icon / colour / label / remarks / <b>shape style</b> from a display
    /// config's compact JSON (see CONFIG-FORMAT.md): "sym" for icon+colour, "lbl" for the label
    /// field, "popup" for the remarks field list, and "shp" for polyline/polygon stroke+fill.
    ///
    /// C-07 / Appendix F §5: <c>ResolveShapeStyle</c> did not exist on WinTAK in any form — a
    /// Portal-authored config carrying polyline/polygon styling was silently and completely
    /// dropped here, with no warning and no log line, while ATAK and CloudTAK both honoured it.
    /// It is implemented below against the same semantics as <c>DisplayConfig.resolveShapeStyle</c>
    /// (Java) and <c>resolveShapeStyle</c> (TypeScript), with the C-24 precedence <b>corrected</b>:
    /// a per-value <c>shapeStyleByValue</c> match wins, and <c>singleShapeStyle</c> is the
    /// fallback. The other two platforms have that inverted (they return the single style first,
    /// making every per-value entry dead code whenever a defaultSymbol exists); WinTAK
    /// deliberately does not inherit the inversion.
    ///
    /// Culture: every numeric parse in this file pins <see cref="CultureInfo.InvariantCulture"/>
    /// and every string comparison is <see cref="StringComparison.Ordinal"/>. The audit measured
    /// zero invariant-culture uses in either tree, so on a de-DE machine a colour-break threshold
    /// of "1.5" parsed as 15 and features rendered in the wrong colour silently.
    /// </summary>
    public static class DisplayStyleResolver
    {
        /// <summary>Cap on a generated &lt;remarks&gt; body. Remarks are broadcast as CoT to the
        /// whole network; a 40-field popup over 4 KB values would otherwise emit a 160 KB detail.</summary>
        public const int MaxRemarksLength = 4096;

        private static readonly Color DefaultColor = Color.FromArgb(255, 0x33, 0x88, 0xff);

        // -------------------------------------------------------------------------
        // Icon
        // -------------------------------------------------------------------------

        public static string ResolveIconsetPath(JObject sym, IReadOnlyDictionary<string, string> attrs)
        {
            if (sym == null) return null;
            string type = Str(sym, "t", "s");

            if (type == "ic")
                return ResolvedOrLegacy(Str(sym, "up", null), Str(sym, "is", null), Str(sym, "ic", null));

            if (type == "adv")
            {
                string fieldName = Str(sym, "f", string.Empty);
                string val = GetAttr(attrs, fieldName);

                if (sym["vs"] is JArray vs)
                {
                    foreach (var e in vs)
                    {
                        if (!(e is JObject entry)) continue;
                        if (string.Equals(Str(entry, "v", null), val, StringComparison.Ordinal)
                            && string.Equals(Str(entry, "m", null), "icon", StringComparison.Ordinal))
                            return ResolvedOrLegacy(Str(entry, "up", null), Str(entry, "is", null), Str(entry, "ic", null));
                    }
                }

                var rules = sym["r"] as JArray ?? sym["rules"] as JArray;
                if (rules != null)
                {
                    foreach (var r in rules)
                    {
                        if (!(r is JObject rule)) continue;
                        string fVal = GetAttr(attrs, Str(rule, "f", string.Empty));
                        string op = Str(rule, "o", "=");
                        string rVal = Str(rule, "v", string.Empty);
                        if (MatchesRule(fVal, op, rVal)
                            && string.Equals(Str(rule, "m", null), "icon", StringComparison.Ordinal))
                            return ResolvedOrLegacy(Str(rule, "up", null), Str(rule, "is", null), Str(rule, "ic", null));
                    }
                }
            }

            return null;
        }

        // -------------------------------------------------------------------------
        // Colour
        // -------------------------------------------------------------------------

        /// <summary>Returns the ARGB marker colour for a feature, or <c>null</c> when this config
        /// cannot produce one — mirrors <c>DisplayConfig.resolveColor()</c> across all six sym
        /// types. Returning null (rather than the previous silent <c>Color.Blue</c>) lets the
        /// caller leave the marker at its native styling instead of masking a config error as an
        /// intentional blue marker; unknown sym types are logged.</summary>
        public static Color? ResolveColor(JObject sym, IReadOnlyDictionary<string, string> attrs)
        {
            if (sym == null) return null;
            string type = Str(sym, "t", "s");
            float opacity = ClampOpacity(Num(sym, "op", 1.0));
            Color baseColor = ParseHexColor(Str(sym, "c", null), DefaultColor);

            switch (type)
            {
                case "s":
                    return BlendOpacity(baseColor, opacity);

                case "uv":
                {
                    string val = GetAttr(attrs, Str(sym, "f", string.Empty));
                    if (sym["uv"] is JArray uv)
                    {
                        foreach (var e in uv)
                        {
                            if (!(e is JObject entry)) continue;
                            if (string.Equals(Str(entry, "v", null), val, StringComparison.Ordinal))
                                return BlendOpacity(ParseHexColor(Str(entry, "c", null), baseColor), opacity);
                        }
                    }
                    return BlendOpacity(baseColor, opacity);
                }

                case "adv":
                {
                    string val = GetAttr(attrs, Str(sym, "f", string.Empty));
                    if (sym["vs"] is JArray vs)
                    {
                        foreach (var e in vs)
                        {
                            if (!(e is JObject entry)) continue;
                            if (string.Equals(Str(entry, "v", null), val, StringComparison.Ordinal))
                                return BlendOpacity(ParseHexColor(Str(entry, "c", null), baseColor), opacity);
                        }
                    }
                    var rules = sym["r"] as JArray ?? sym["rules"] as JArray;
                    if (rules != null)
                    {
                        foreach (var r in rules)
                        {
                            if (!(r is JObject rule)) continue;
                            string fVal = GetAttr(attrs, Str(rule, "f", string.Empty));
                            if (MatchesRule(fVal, Str(rule, "o", "="), Str(rule, "v", string.Empty)))
                                return BlendOpacity(ParseHexColor(Str(rule, "c", null), baseColor), opacity);
                        }
                    }
                    return BlendOpacity(baseColor, opacity);
                }

                case "rb":
                {
                    if (sym["rules"] is JArray rules)
                    {
                        foreach (var r in rules)
                        {
                            if (!(r is JObject rule)) continue;
                            string fVal = GetAttr(attrs, Str(rule, "f", string.Empty));
                            if (MatchesRule(fVal, Str(rule, "o", "="), Str(rule, "v", string.Empty)))
                                return BlendOpacity(ParseHexColor(Str(rule, "c", null), baseColor), opacity);
                        }
                    }
                    return BlendOpacity(ParseHexColor(Str(sym, "dc", null), DefaultColor), opacity);
                }

                case "cb":
                {
                    string raw = GetAttr(attrs, Str(sym, "f", string.Empty));
                    double dval;
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out dval)
                        && sym["cb"] is JArray cb)
                    {
                        foreach (var b in cb)
                        {
                            if (!(b is JObject brk)) continue;
                            // Per-entry guard: one malformed break must not abort the whole layer
                            // download (it previously threw FormatException out of ResolveColor).
                            double min = Num(brk, "mn", double.NegativeInfinity);
                            double max = Num(brk, "mx", double.PositiveInfinity);
                            if (double.IsNaN(min) || double.IsNaN(max) || min > max) continue;
                            if (dval >= min && dval < max)
                                return BlendOpacity(ParseHexColor(Str(brk, "c", null), baseColor), opacity);
                        }
                    }
                    return BlendOpacity(baseColor, opacity);
                }

                case "ic":
                    return BlendOpacity(baseColor, opacity);

                default:
                    Log.Warn($"Display config has an unrecognised symbology type \"{type}\" — "
                             + "leaving features at their default styling.");
                    return null;
            }
        }

        // -------------------------------------------------------------------------
        // Shape style (polyline / polygon) — C-07 + C-24
        // -------------------------------------------------------------------------

        /// <summary>
        /// Resolves the stroke/fill style for a feature from a compact <c>"shp"</c> block, or
        /// <c>null</c> when this config carries no shape styling (a point layer, or one with no
        /// esriSLS/esriSFS symbology) — callers fall back to their platform default.
        ///
        /// Precedence (C-24, corrected here): the per-value <c>bv</c> lookup on the driving field
        /// <c>f</c> is tried <b>first</b>; <c>s</c> (the single/default style) is the fallback.
        /// </summary>
        public static ShapeStyle ResolveShapeStyle(JObject shp, IReadOnlyDictionary<string, string> attrs)
        {
            if (shp == null) return null;

            if (shp["bv"] is JObject byValue && byValue.Count > 0)
            {
                string field = Str(shp, "f", string.Empty);
                string val = GetAttr(attrs, field);
                // A missing/blank attribute must not match a "" key that a producer never meant as
                // a wildcard, so only a non-empty value is looked up.
                if (val.Length > 0 && byValue[val] is JObject match)
                {
                    var resolved = ParseShapeStyle(match);
                    if (resolved != null) return resolved;
                }
            }

            return ParseShapeStyle(shp["s"] as JObject);
        }

        /// <summary>Parses one compact shape-style object:
        /// <c>{"sc":"#aarrggbb","sw":2.0,"sd":"solid","fc":"#aarrggbb","fs":"solid"}</c>.</summary>
        public static ShapeStyle ParseShapeStyle(JObject o)
        {
            if (o == null) return null;
            Color stroke = ParseHexColor(Str(o, "sc", null), Color.Blue);
            double width = Num(o, "sw", 2.0);
            if (double.IsNaN(width) || width <= 0 || width > 64) width = 2.0;
            string dash = Str(o, "sd", "solid");
            if (dash != "dash" && dash != "dot") dash = "solid";
            Color fill = ParseHexColor(Str(o, "fc", null), Color.Transparent);
            string fillStyle = Str(o, "fs", "none") == "solid" ? "solid" : "none";
            return new ShapeStyle(stroke, (float)width, dash, fill, fillStyle);
        }

        /// <summary>Serialises an <see cref="AutoSymbology.Result"/> into the compact <c>"shp"</c>
        /// block <see cref="ResolveShapeStyle"/> reads. Returns null when the renderer carried no
        /// esriSLS/esriSFS styling. Mirrors <c>DisplayConfig.forAutoIcons</c>'s
        /// shapeField/singleShapeStyle/shapeStyleByValue population and
        /// <c>buildShapeStyleFields</c> in the TypeScript tree.</summary>
        public static JObject BuildShapeConfigJson(AutoSymbology.Result result)
        {
            if (result == null || !result.HasShapeStyling) return null;

            var shp = new JObject();
            if (!string.IsNullOrEmpty(result.Field)) shp["f"] = result.Field;

            var single = CombineShapeStyle(result.SingleStroke, result.SingleFill);
            if (single != null) shp["s"] = ShapeStyleToJson(single);

            if (result.StrokeByValue.Count > 0 || result.FillByValue.Count > 0)
            {
                var byValue = new JObject();
                var values = new List<string>();
                foreach (var k in result.StrokeByValue.Keys) if (!values.Contains(k)) values.Add(k);
                foreach (var k in result.FillByValue.Keys) if (!values.Contains(k)) values.Add(k);
                foreach (var v in values)
                {
                    AutoSymbology.StrokeStyle s; AutoSymbology.FillStyle f;
                    result.StrokeByValue.TryGetValue(v, out s);
                    result.FillByValue.TryGetValue(v, out f);
                    var combined = CombineShapeStyle(s, f);
                    if (combined != null) byValue[v] = ShapeStyleToJson(combined);
                }
                if (byValue.Count > 0) shp["bv"] = byValue;
            }

            return shp.Count > 0 ? shp : null;
        }

        /// <summary>Merges a renderer entry's stroke (esriSLS) and fill (esriSFS, whose own outline
        /// is itself an esriSLS) into one <see cref="ShapeStyle"/> — a polygon's outline can come
        /// from either the fill symbol's outline or a separate line symbol depending on how the
        /// renderer was authored, so the fill's outline is preferred when there is no standalone
        /// stroke. Byte-for-byte the same rule as <c>DisplayConfig.combineShapeStyle</c>.</summary>
        public static ShapeStyle CombineShapeStyle(AutoSymbology.StrokeStyle stroke, AutoSymbology.FillStyle fill)
        {
            if (stroke == null && fill == null) return null;
            var outline = fill?.Outline;
            Color strokeColor = stroke != null ? stroke.Color : (outline != null ? outline.Color : Color.Blue);
            float strokeWidth = stroke != null ? stroke.WidthPx : (outline != null ? outline.WidthPx : 2f);
            string strokeDash = stroke != null ? stroke.Dash : (outline != null ? outline.Dash : "solid");
            Color fillColor = fill != null ? fill.Color : Color.Transparent;
            string fillStyle = fill != null ? fill.Style : "none";
            return new ShapeStyle(strokeColor, strokeWidth, strokeDash, fillColor, fillStyle);
        }

        public static JObject ShapeStyleToJson(ShapeStyle style)
        {
            if (style == null) return null;
            return new JObject
            {
                ["sc"] = ToHex8(style.StrokeColor),
                ["sw"] = Math.Round(style.StrokeWidthPx, 3),
                ["sd"] = style.StrokeDash,
                ["fc"] = ToHex8(style.FillColor),
                ["fs"] = style.FillStyle,
            };
        }

        /// <summary>Builds the compact <c>"sym"</c> block for a renderer that carries esriSMS point
        /// markers (colour/shape, no custom icon). Port of the corresponding branches of
        /// <c>DisplayConfig.forAutoIcons</c>: per-value markers become a <c>"uv"</c> config so the
        /// existing <see cref="ResolveColor"/> machinery picks them up with no new resolution
        /// logic, and a lone marker becomes an <c>"s"</c> config. Returns null when the renderer
        /// has no marker symbology.</summary>
        public static JObject BuildMarkerSymConfigJson(AutoSymbology.Result result)
        {
            if (result == null) return null;

            if (result.MarkerByValue.Count > 0)
            {
                var uv = new JArray();
                foreach (var kv in result.MarkerByValue)
                    uv.Add(new JObject { ["v"] = kv.Key, ["c"] = ToHex6(kv.Value.Color) });
                Color fallback = result.SingleMarker != null ? result.SingleMarker.Color : DefaultColor;
                return new JObject
                {
                    ["t"] = "uv",
                    ["c"] = ToHex6(fallback),
                    ["f"] = result.Field ?? string.Empty,
                    ["op"] = 1.0,
                    ["uv"] = uv,
                };
            }

            if (result.SingleMarker != null)
            {
                return new JObject
                {
                    ["t"] = "s",
                    ["c"] = ToHex6(result.SingleMarker.Color),
                    ["sh"] = result.SingleMarker.Shape,
                    ["sz"] = (int)Math.Round(result.SingleMarker.SizePx),
                    ["op"] = 1.0,
                };
            }

            return null;
        }

        // -------------------------------------------------------------------------
        // Label / remarks
        // -------------------------------------------------------------------------

        /// <summary>Label text for a feature from <c>lbl.f</c> — falls back to
        /// <paramref name="fallback"/> when lbl is null, has no field, or the field is
        /// missing/empty on this feature.</summary>
        public static string ResolveLabel(JObject lbl, IReadOnlyDictionary<string, string> attrs, string fallback)
        {
            string field = Str(lbl, "f", null);
            if (string.IsNullOrEmpty(field)) return fallback;
            string val = GetAttr(attrs, field);
            return !string.IsNullOrEmpty(val) ? val : fallback;
        }

        /// <summary>Builds a remarks string from <c>popup.flds</c> field values:
        /// "Alias: Value\nAlias: Value…". <c>flds</c> entries are either a bare field name
        /// (alias == name) or a <c>[name, alias]</c> pair. Total length is capped at
        /// <see cref="MaxRemarksLength"/>.</summary>
        public static string BuildRemarks(JObject popup, IReadOnlyDictionary<string, string> attrs)
        {
            if (!(popup?["flds"] is JArray flds) || flds.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var entry in flds)
            {
                string fieldName, alias;
                if (entry.Type == JTokenType.Array)
                {
                    var arr = (JArray)entry;
                    // Guard the index — a "flds":[[]] entry previously threw
                    // ArgumentOutOfRangeException and aborted the whole layer download.
                    if (arr.Count == 0) continue;
                    fieldName = (string)arr[0] ?? string.Empty;
                    alias = arr.Count > 1 ? ((string)arr[1] ?? fieldName) : fieldName;
                }
                else if (entry.Type == JTokenType.String)
                {
                    fieldName = (string)entry ?? string.Empty;
                    alias = fieldName;
                }
                else continue;

                if (fieldName.Length == 0) continue;
                string val = GetAttr(attrs, fieldName);
                if (string.IsNullOrEmpty(val)) continue;

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(alias).Append(": ").Append(val);
                if (sb.Length >= MaxRemarksLength)
                {
                    sb.Length = MaxRemarksLength;
                    Log.Warn("Popup remarks truncated at " + MaxRemarksLength + " characters before CoT emission.");
                    break;
                }
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static string GetAttr(IReadOnlyDictionary<string, string> attrs, string field)
        {
            string v;
            return attrs != null && !string.IsNullOrEmpty(field) && attrs.TryGetValue(field, out v)
                ? (v ?? string.Empty) : string.Empty;
        }

        /// <summary>Prefers the server-resolved "up" (usericonPath) — the exact
        /// "uid/group/filename" string a config source already resolved. Falls back to the legacy
        /// iconset-name + filename concat only for older payloads that predate "up".</summary>
        private static string ResolvedOrLegacy(string usericonPath, string iconset, string iconFile)
        {
            if (!string.IsNullOrEmpty(usericonPath)) return usericonPath;
            if (string.IsNullOrEmpty(iconset) || string.IsNullOrEmpty(iconFile)) return null;
            return iconset + "/" + iconFile;
        }

        /// <summary>Accepts <c>#rgb</c>, <c>#rrggbb</c> and <c>#aarrggbb</c> (and the same without
        /// the leading '#', and with a <c>0x</c> prefix). Anything else logs and falls back.</summary>
        public static Color ParseHexColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string h = hex.Trim();
            if (h.StartsWith("#", StringComparison.Ordinal)) h = h.Substring(1);
            else if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);

            try
            {
                if (h.Length == 3)
                {
                    int r3 = HexByte(new string(h[0], 2)), g3 = HexByte(new string(h[1], 2)), b3 = HexByte(new string(h[2], 2));
                    return Color.FromArgb(255, r3, g3, b3);
                }
                if (h.Length == 6)
                    return Color.FromArgb(255, HexByte(h.Substring(0, 2)), HexByte(h.Substring(2, 2)), HexByte(h.Substring(4, 2)));
                if (h.Length == 8)
                    return Color.FromArgb(HexByte(h.Substring(0, 2)), HexByte(h.Substring(2, 2)),
                        HexByte(h.Substring(4, 2)), HexByte(h.Substring(6, 2)));
            }
            catch (FormatException) { /* fall through to the log + fallback below */ }
            catch (OverflowException) { }

            Log.Warn($"Display config carries an unparseable colour \"{hex}\" — using the fallback.");
            return fallback;
        }

        private static int HexByte(string twoChars) =>
            int.Parse(twoChars, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        public static string ToHex6(Color c) =>
            "#" + c.R.ToString("X2", CultureInfo.InvariantCulture)
                + c.G.ToString("X2", CultureInfo.InvariantCulture)
                + c.B.ToString("X2", CultureInfo.InvariantCulture);

        public static string ToHex8(Color c) =>
            "#" + c.A.ToString("X2", CultureInfo.InvariantCulture)
                + c.R.ToString("X2", CultureInfo.InvariantCulture)
                + c.G.ToString("X2", CultureInfo.InvariantCulture)
                + c.B.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>Clamps an opacity to [0,1]. An unvalidated NaN previously reached
        /// <c>(int)Math.Round(NaN)</c>, which is undefined in an unchecked context and produced
        /// <c>int.MinValue</c> → alpha 0 → a silently invisible marker.</summary>
        private static float ClampOpacity(double op)
        {
            if (double.IsNaN(op)) return 1.0f;
            if (op < 0) return 0f;
            if (op > 1) return 1.0f;
            return (float)op;
        }

        private static Color BlendOpacity(Color color, float opacity)
        {
            int alpha = (int)Math.Round(opacity * color.A);
            alpha = Math.Max(0, Math.Min(255, alpha));
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        /// <summary>
        /// Rule operator matching. Operators are carried in the wire format as display strings
        /// (CONFIG-FORMAT.md), so both spellings of "not equal" are accepted — a config source
        /// emitting ASCII "!=" or "&lt;&gt;" previously fell through to the numeric branch and
        /// silently returned false for every feature. Unrecognised operators are logged.
        /// All string comparisons are ordinal: <c>StartsWith(string)</c> on .NET Framework is
        /// culture-sensitive by default and misbehaves under tr-TR.
        /// </summary>
        public static bool MatchesRule(string fieldVal, string op, string ruleVal)
        {
            fieldVal = fieldVal ?? string.Empty;
            ruleVal = ruleVal ?? string.Empty;
            op = (op ?? "=").Trim();

            switch (op)
            {
                case "=":
                case "==":
                    return string.Equals(fieldVal, ruleVal, StringComparison.Ordinal);
                case "≠": // ≠
                case "!=":
                case "<>":
                    return !string.Equals(fieldVal, ruleVal, StringComparison.Ordinal);
                case "contains":
                    return fieldVal.IndexOf(ruleVal, StringComparison.Ordinal) >= 0;
                case "starts with":
                    return fieldVal.StartsWith(ruleVal, StringComparison.Ordinal);
                case "is empty":
                    return fieldVal.Length == 0;
                case "is not empty":
                    return fieldVal.Length > 0;
                case ">":
                case "<":
                case ">=":
                case "<=":
                {
                    double fv, rv;
                    if (!double.TryParse(fieldVal, NumberStyles.Float, CultureInfo.InvariantCulture, out fv)
                        || !double.TryParse(ruleVal, NumberStyles.Float, CultureInfo.InvariantCulture, out rv))
                        return false;
                    switch (op)
                    {
                        case ">": return fv > rv;
                        case "<": return fv < rv;
                        case ">=": return fv >= rv;
                        default: return fv <= rv;
                    }
                }
                default:
                    Log.Warn($"Display config uses an unrecognised rule operator \"{op}\" — the rule will never match.");
                    return false;
            }
        }

        // ── culture-invariant JSON accessors ─────────────────────────────────────

        private static string Str(JObject o, string key, string fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.String) return (string)t;
            if (t.Type == JTokenType.Object || t.Type == JTokenType.Array) return fallback;
            return ((JValue)t).ToString(CultureInfo.InvariantCulture);
        }

        private static double Num(JObject o, string key, double fallback)
        {
            var t = o?[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (double)t;
            double parsed;
            if (t.Type == JTokenType.String
                && double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }
    }
}
