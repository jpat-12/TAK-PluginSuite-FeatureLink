using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FeatureLink.Services
{
    /// <summary>
    /// Resolves a feature's custom-icon "usericonPath" from a received share's raw compact "sym"
    /// JSON (DisplayConfig.java's schema — see CONFIG-FORMAT.md), mirroring
    /// DisplayConfig.resolveIconsetPath() closely enough to produce the same result for the same
    /// input. Only the icon-eligible sym types ("ic" single icon, "adv" per-value/per-rule icon
    /// entries) are handled — color/label/popup resolution is still out of scope for this port
    /// (see README "What's deferred").
    /// </summary>
    public static class IconResolver
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
                string val = attrs != null && fieldName.Length > 0 && attrs.TryGetValue(fieldName, out var v) ? v : "";

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
                        string rField = (string)r["f"] ?? "";
                        string fVal = attrs != null && rField.Length > 0 && attrs.TryGetValue(rField, out var fv) ? fv : "";
                        string op = (string)r["o"] ?? "=";
                        string rVal = (string)r["v"] ?? "";
                        if (MatchesRule(fVal, op, rVal) && (string)r["m"] == "icon")
                            return ResolvedOrLegacy((string)r["up"], (string)r["is"], (string)r["ic"]);
                    }
                }
            }

            return null;
        }

        /// <summary>Prefers the server-resolved "up" (usericonPath) — the exact
        /// "uid/group/filename" string a config source already resolved. Falls back to the
        /// legacy iconset-name + filename concat only for older payloads that predate "up".</summary>
        private static string ResolvedOrLegacy(string usericonPath, string iconset, string iconFile)
        {
            if (!string.IsNullOrEmpty(usericonPath)) return usericonPath;
            if (string.IsNullOrEmpty(iconset) || string.IsNullOrEmpty(iconFile)) return null;
            return iconset + "/" + iconFile;
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
