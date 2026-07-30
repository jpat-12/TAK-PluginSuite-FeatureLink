using System.Collections.Generic;
using System.Drawing;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Resolves a feature's icon/color/label/remarks from a received share's raw compact JSON
    /// (DisplayConfig.java's schema — see CONFIG-FORMAT.md): "sym" for icon/color,
    /// "lbl" for the label field, "popup" for the remarks field list. Mirrors
    /// DisplayConfig.resolveIconsetPath()/resolveColor()/resolveLabel()/buildRemarks() closely
    /// enough to produce the same result for the same input. Formerly IconResolver — renamed
    /// once color/label/popup resolution were added alongside the original icon-only support.
    /// </summary>
    public static class DisplayStyleResolver
    {
        public static string ResolveIconsetPath(JObject sym, IReadOnlyDictionary<string, string> attrs)
        {
            if (sym == null) return null;
            string type = (string)sym["t"] ?? "s";

            if (type == "ic")
            {
                return ResolvedOrLegacy((string)sym["up"], (string)sym["is"], (string)sym["ic"]);
            }

            if (type == "adv")
            {
                string fieldName = (string)sym["f"] ?? "";
                string val = GetAttr(attrs, fieldName);

                if (sym["vs"] is JArray vs)
                {
                    foreach (var e in vs)
                    {
                        if ((string)e["v"] == val && (string)e["m"] == "icon")
                            return ResolvedOrLegacy((string)e["up"], (string)e["is"], (string)e["ic"]);
                    }
                }

                var rules = sym["r"] as JArray ?? sym["rules"] as JArray;
                if (rules != null)
                {
                    foreach (var r in rules)
                    {
                        string fVal = GetAttr(attrs, (string)r["f"] ?? "");
                        string op = (string)r["o"] ?? "=";
                        string rVal = (string)r["v"] ?? "";
                        if (MatchesRule(fVal, op, rVal) && (string)r["m"] == "icon")
                            return ResolvedOrLegacy((string)r["up"], (string)r["is"], (string)r["ic"]);
                    }
                }
            }

            return null;
        }

        /// <summary>Returns the ARGB marker color for a feature, or Blue if sym is null/unresolvable
        /// — mirrors DisplayConfig.resolveColor() across all five sym types.</summary>
        public static Color ResolveColor(JObject sym, IReadOnlyDictionary<string, string> attrs)
        {
            if (sym == null) return Color.Blue;
            string type = (string)sym["t"] ?? "s";
            float opacity = (float?)sym["op"] ?? 1.0f;
            Color baseColor = ParseHexColor((string)sym["c"], Color.FromArgb(0x33, 0x88, 0xff));

            switch (type)
            {
                case "s":
                    return BlendOpacity(baseColor, opacity);

                case "uv":
                {
                    string val = GetAttr(attrs, (string)sym["f"] ?? "");
                    if (sym["uv"] is JArray uv)
                    {
                        foreach (var e in uv)
                        {
                            if ((string)e["v"] == val)
                                return BlendOpacity(ParseHexColor((string)e["c"], baseColor), opacity);
                        }
                    }
                    return BlendOpacity(baseColor, opacity);
                }

                case "adv":
                {
                    string val = GetAttr(attrs, (string)sym["f"] ?? "");
                    if (sym["vs"] is JArray vs)
                    {
                        foreach (var e in vs)
                        {
                            if ((string)e["v"] == val)
                                return BlendOpacity(ParseHexColor((string)e["c"], baseColor), opacity);
                        }
                    }
                    var rules = sym["r"] as JArray ?? sym["rules"] as JArray;
                    if (rules != null)
                    {
                        foreach (var r in rules)
                        {
                            string fVal = GetAttr(attrs, (string)r["f"] ?? "");
                            if (MatchesRule(fVal, (string)r["o"] ?? "=", (string)r["v"] ?? ""))
                                return BlendOpacity(ParseHexColor((string)r["c"], baseColor), opacity);
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
                            string fVal = GetAttr(attrs, (string)r["f"] ?? "");
                            if (MatchesRule(fVal, (string)r["o"] ?? "=", (string)r["v"] ?? ""))
                                return BlendOpacity(ParseHexColor((string)r["c"], baseColor), opacity);
                        }
                    }
                    Color defaultColor = ParseHexColor((string)sym["dc"], Color.FromArgb(0x33, 0x88, 0xff));
                    return BlendOpacity(defaultColor, opacity);
                }

                case "cb":
                {
                    string raw = GetAttr(attrs, (string)sym["f"] ?? "");
                    if (double.TryParse(raw, out double dval) && sym["cb"] is JArray cb)
                    {
                        foreach (var b in cb)
                        {
                            double min = (double?)b["mn"] ?? double.NegativeInfinity;
                            double max = (double?)b["mx"] ?? double.PositiveInfinity;
                            if (dval >= min && dval < max)
                                return BlendOpacity(ParseHexColor((string)b["c"], baseColor), opacity);
                        }
                    }
                    return BlendOpacity(baseColor, opacity);
                }

                case "ic":
                    return BlendOpacity(baseColor, opacity);

                default:
                    return Color.Blue;
            }
        }

        /// <summary>Label text for a feature from lbl.f — falls back to the given default if lbl
        /// is null, has no field, or the field is missing/empty on this feature.</summary>
        public static string ResolveLabel(JObject lbl, IReadOnlyDictionary<string, string> attrs, string fallback)
        {
            string field = (string)lbl?["f"];
            if (string.IsNullOrEmpty(field)) return fallback;
            string val = GetAttr(attrs, field);
            return !string.IsNullOrEmpty(val) ? val : fallback;
        }

        /// <summary>Builds a remarks string from popup.flds field values: "Alias: Value\nAlias:
        /// Value…". Empty string if popup is null or has no fields with values on this feature.
        /// flds entries are either a bare field name (alias == name) or a [name, alias] pair.</summary>
        public static string BuildRemarks(JObject popup, IReadOnlyDictionary<string, string> attrs)
        {
            if (!(popup?["flds"] is JArray flds) || flds.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var entry in flds)
            {
                string fieldName, alias;
                if (entry.Type == JTokenType.Array)
                {
                    var arr = (JArray)entry;
                    fieldName = (string)arr[0] ?? "";
                    alias = arr.Count > 1 ? (string)arr[1] : fieldName;
                }
                else
                {
                    fieldName = (string)entry ?? "";
                    alias = fieldName;
                }
                if (fieldName.Length == 0) continue;
                string val = GetAttr(attrs, fieldName);
                if (!string.IsNullOrEmpty(val))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(alias).Append(": ").Append(val);
                }
            }
            return sb.ToString();
        }

        private static string GetAttr(IReadOnlyDictionary<string, string> attrs, string field) =>
            attrs != null && field.Length > 0 && attrs.TryGetValue(field, out var v) ? v : "";

        /// <summary>Prefers the server-resolved "up" (usericonPath) — the exact
        /// "uid/group/filename" string a config source already resolved. Falls back to the
        /// legacy iconset-name + filename concat only for older payloads that predate "up".</summary>
        private static string ResolvedOrLegacy(string usericonPath, string iconset, string iconFile)
        {
            if (!string.IsNullOrEmpty(usericonPath)) return usericonPath;
            if (string.IsNullOrEmpty(iconset) || string.IsNullOrEmpty(iconFile)) return null;
            return iconset + "/" + iconFile;
        }

        private static Color ParseHexColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                string h = hex.TrimStart('#');
                if (h.Length == 6)
                {
                    int r = System.Convert.ToInt32(h.Substring(0, 2), 16);
                    int g = System.Convert.ToInt32(h.Substring(2, 2), 16);
                    int b = System.Convert.ToInt32(h.Substring(4, 2), 16);
                    return Color.FromArgb(255, r, g, b);
                }
            }
            catch { /* malformed hex string — use fallback */ }
            return fallback;
        }

        private static Color BlendOpacity(Color color, float opacity)
        {
            int alpha = (int)System.Math.Round(opacity * color.A);
            alpha = System.Math.Max(0, System.Math.Min(255, alpha));
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static bool MatchesRule(string fieldVal, string op, string ruleVal)
        {
            switch (op)
            {
                case "=": return fieldVal == ruleVal;
                case "≠": return fieldVal != ruleVal;
                case "contains": return fieldVal.Contains(ruleVal);
                case "starts with": return fieldVal.StartsWith(ruleVal);
                case "is empty": return fieldVal.Length == 0;
                case "is not empty": return fieldVal.Length > 0;
                default:
                    if (double.TryParse(fieldVal, out double fv) && double.TryParse(ruleVal, out double rv))
                    {
                        switch (op)
                        {
                            case ">": return fv > rv;
                            case "<": return fv < rv;
                            case ">=": return fv >= rv;
                            case "<=": return fv <= rv;
                        }
                    }
                    return false;
            }
        }
    }
}
