package com.atakmap.android.featurelink;

import android.graphics.Color;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;

/**
 * Parsed display configuration from a v:2 (or v:1 display-only) FeatureLink QR code.
 * Carries sym/lbl/popup settings that are applied to ATAK markers at download time.
 */
final class DisplayConfig {

    /** Feature server URL. Empty for v:1 display-only (applied to an already-open layer). */
    final String  url;
    final String  name;
    final float   opacity;
    final boolean visible;

    /** Symbol renderer config. May be null if not specified. */
    final SymConfig sym;

    /** Label config. Null means labels are disabled. */
    final LabelConfig lbl;

    /** Popup config. May be null if not specified. */
    final PopupConfig popup;

    private DisplayConfig(String url, String name, float opacity, boolean visible,
            SymConfig sym, LabelConfig lbl, PopupConfig popup) {
        this.url     = url;
        this.name    = name;
        this.opacity = opacity;
        this.visible = visible;
        this.sym     = sym;
        this.lbl     = lbl;
        this.popup   = popup;
    }

    /** Parses a v:2 or v:1-display JSONObject. Returns null if the object is not a display config. */
    static DisplayConfig fromJson(JSONObject o) {
        try {
            String url = o.optString("url", "");

            JSONObject layerJ = o.optJSONObject("layer");
            String  name    = layerJ != null ? layerJ.optString("name",    "")   : "";
            float   opacity = layerJ != null ? (float) layerJ.optDouble("opacity", 1.0) : 1.0f;
            boolean visible = layerJ == null || layerJ.optBoolean("visible", true);

            JSONObject symJ   = o.optJSONObject("sym");
            JSONObject lblJ   = o.optJSONObject("lbl");
            JSONObject popupJ = o.optJSONObject("popup");

            SymConfig   sym   = symJ   != null ? SymConfig.fromJson(symJ)     : null;
            LabelConfig lbl   = lblJ   != null ? LabelConfig.fromJson(lblJ)   : null;
            PopupConfig popup = popupJ != null ? PopupConfig.fromJson(popupJ) : null;

            return new DisplayConfig(url, name, opacity, visible, sym, lbl, popup);
        } catch (Exception e) {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Runtime resolution helpers — called per-feature at download time
    // -------------------------------------------------------------------------

    /**
     * Returns the ATAK marker color (ARGB) for a feature given its raw attribute map.
     * Falls back to a default blue if sym is null or unresolvable.
     */
    int resolveColor(Map<String, String> attrs) {
        if (sym == null) return Color.BLUE;
        switch (sym.type) {
            case "s":
                return blendOpacity(sym.color, sym.opacity);

            case "uv": {
                String val = attrs.getOrDefault(sym.fieldName, "");
                for (UvEntry e : sym.uvEntries) {
                    if (e.value.equals(val)) return blendOpacity(e.color, sym.opacity);
                }
                return blendOpacity(sym.color, sym.opacity);
            }

            case "adv": {
                // value-based first, then rules
                String val = attrs.getOrDefault(sym.fieldName, "");
                for (UvEntry e : sym.advValues) {
                    if (e.value.equals(val)) return blendOpacity(e.color, sym.opacity);
                }
                for (RbRule r : sym.rbRules) {
                    String fval = attrs.getOrDefault(r.field, "");
                    if (matchesRule(fval, r.op, r.value))
                        return blendOpacity(r.color, sym.opacity);
                }
                return blendOpacity(sym.color, sym.opacity);
            }

            case "rb": {
                for (RbRule r : sym.rbRules) {
                    String fval = attrs.getOrDefault(r.field, "");
                    if (matchesRule(fval, r.op, r.value))
                        return blendOpacity(r.color, sym.opacity);
                }
                return blendOpacity(sym.defaultColor, sym.opacity);
            }

            case "cb": {
                String raw = attrs.getOrDefault(sym.fieldName, "");
                try {
                    double dval = Double.parseDouble(raw);
                    for (CbBreak b : sym.cbBreaks) {
                        if (dval >= b.min && dval < b.max)
                            return blendOpacity(b.color, sym.opacity);
                    }
                } catch (NumberFormatException ignored) {}
                return blendOpacity(sym.color, sym.opacity);
            }

            case "ic":
                return blendOpacity(sym.color, sym.opacity);

            default:
                return Color.BLUE;
        }
    }

    /**
     * Returns the label text for a feature. Uses lbl.field from the attribute map;
     * falls back to {@code fallback} if lbl is null or the field is missing.
     */
    String resolveLabel(Map<String, String> attrs, String fallback) {
        if (lbl == null || lbl.field.isEmpty()) return fallback;
        String val = attrs.get(lbl.field);
        return (val != null && !val.isEmpty()) ? val : fallback;
    }

    /**
     * Builds a remarks string from popup field values. Format: "Alias: Value\nAlias: Value…"
     * Returns empty string if popup is null or has no fields with values.
     */
    String buildRemarks(Map<String, String> attrs) {
        if (popup == null || popup.fields.isEmpty()) return "";
        StringBuilder sb = new StringBuilder();
        for (String[] field : popup.fields) {
            String val = attrs.get(field[0]);
            if (val != null && !val.isEmpty()) {
                if (sb.length() > 0) sb.append('\n');
                sb.append(field[1]).append(": ").append(val);
            }
        }
        return sb.toString();
    }

    // -------------------------------------------------------------------------
    // Rule evaluation
    // -------------------------------------------------------------------------

    private static boolean matchesRule(String fieldVal, String op, String ruleVal) {
        switch (op) {
            case "=":            return fieldVal.equals(ruleVal);
            case "≠":            return !fieldVal.equals(ruleVal);
            case "contains":     return fieldVal.contains(ruleVal);
            case "starts with":  return fieldVal.startsWith(ruleVal);
            case "is empty":     return fieldVal.isEmpty();
            case "is not empty": return !fieldVal.isEmpty();
            default:
                try {
                    double fv = Double.parseDouble(fieldVal);
                    double rv = Double.parseDouble(ruleVal);
                    switch (op) {
                        case ">":  return fv >  rv;
                        case "<":  return fv <  rv;
                        case ">=": return fv >= rv;
                        case "<=": return fv <= rv;
                    }
                } catch (NumberFormatException ignored) {}
                return false;
        }
    }

    private static int blendOpacity(int color, float opacity) {
        int alpha = Math.round(opacity * ((color >>> 24) & 0xFF));
        alpha = Math.max(0, Math.min(255, alpha));
        return (color & 0x00FFFFFF) | (alpha << 24);
    }

    static int parseHexColor(String hex, int fallback) {
        if (hex == null || hex.isEmpty()) return fallback;
        try { return Color.parseColor(hex); } catch (Exception e) { return fallback; }
    }

    // =========================================================================
    // Nested model classes
    // =========================================================================

    static final class SymConfig {
        final String type;          // "s" | "uv" | "rb" | "cb" | "ic" | "adv"
        final int    color;         // fill / default color
        final int    outlineColor;
        final int    sizePx;
        final String shape;
        final float  opacity;
        final String fieldName;     // used by uv / rb / cb / adv

        final List<UvEntry> uvEntries;   // "uv" and "adv" vs
        final List<UvEntry> advValues;   // "adv" vs entries (value→color)
        final List<RbRule>  rbRules;     // "rb" and "adv" r entries
        final int           defaultColor; // "rb" dc

        final List<CbBreak> cbBreaks;   // "cb"

        final String iconset;       // "ic"
        final String iconFile;      // "ic"

        private SymConfig(String type, int color, int outlineColor, int sizePx,
                String shape, float opacity, String fieldName,
                List<UvEntry> uvEntries, List<UvEntry> advValues, List<RbRule> rbRules,
                int defaultColor, List<CbBreak> cbBreaks,
                String iconset, String iconFile) {
            this.type         = type;
            this.color        = color;
            this.outlineColor = outlineColor;
            this.sizePx       = sizePx;
            this.shape        = shape;
            this.opacity      = opacity;
            this.fieldName    = fieldName;
            this.uvEntries    = uvEntries;
            this.advValues    = advValues;
            this.rbRules      = rbRules;
            this.defaultColor = defaultColor;
            this.cbBreaks     = cbBreaks;
            this.iconset      = iconset;
            this.iconFile     = iconFile;
        }

        static SymConfig fromJson(JSONObject j) throws Exception {
            String type        = j.optString("t",  "s");
            int    color       = parseHexColor(j.optString("c",  "#3388ff"), Color.BLUE);
            int    outlineColor= parseHexColor(j.optString("oc", "#000000"), Color.BLACK);
            int    sizePx      = j.optInt("sz",    12);
            String shape       = j.optString("sh", "circle");
            float  opacity     = (float) j.optDouble("op", 1.0);
            String fieldName   = j.optString("f",  "");
            String iconset     = j.optString("is", "");
            String iconFile    = j.optString("ic", "");
            int    defaultColor= parseHexColor(j.optString("dc", "#3388ff"), Color.BLUE);

            List<UvEntry> uvEntries = parseUvArray(j.optJSONArray("uv"));
            List<UvEntry> advValues = parseAdvVs(j.optJSONArray("vs"));
            List<RbRule>  rbRules   = parseRules(j.optJSONArray("rules"), j.optJSONArray("r"));
            List<CbBreak> cbBreaks  = parseCbArray(j.optJSONArray("cb"));

            return new SymConfig(type, color, outlineColor, sizePx, shape, opacity, fieldName,
                    uvEntries, advValues, rbRules, defaultColor, cbBreaks, iconset, iconFile);
        }

        private static List<UvEntry> parseUvArray(JSONArray arr) throws Exception {
            List<UvEntry> list = new ArrayList<>();
            if (arr == null) return list;
            for (int i = 0; i < arr.length(); i++) {
                JSONObject e = arr.getJSONObject(i);
                list.add(new UvEntry(e.optString("v", ""),
                        parseHexColor(e.optString("c", "#3388ff"), Color.BLUE)));
            }
            return list;
        }

        private static List<UvEntry> parseAdvVs(JSONArray arr) throws Exception {
            List<UvEntry> list = new ArrayList<>();
            if (arr == null) return list;
            for (int i = 0; i < arr.length(); i++) {
                JSONObject e = arr.getJSONObject(i);
                // vs entries: {v, m, c, sh, is, ic} — extract value→color
                list.add(new UvEntry(e.optString("v", ""),
                        parseHexColor(e.optString("c", "#3388ff"), Color.BLUE)));
            }
            return list;
        }

        // Handles both "rules" key (rb) and "r" key (adv)
        private static List<RbRule> parseRules(JSONArray rulesArr, JSONArray rArr) throws Exception {
            JSONArray arr = rulesArr != null ? rulesArr : rArr;
            List<RbRule> list = new ArrayList<>();
            if (arr == null) return list;
            for (int i = 0; i < arr.length(); i++) {
                JSONObject r = arr.getJSONObject(i);
                // Rule entry may nest the color inside a symbol sub-object
                int ruleColor;
                if (r.has("c")) {
                    ruleColor = parseHexColor(r.optString("c", "#3388ff"), Color.BLUE);
                } else {
                    // adv rule embeds color in the symbol object (same object, keyed "c")
                    ruleColor = Color.BLUE;
                }
                list.add(new RbRule(
                        r.optString("f",  ""),
                        r.optString("o",  "="),
                        r.optString("v",  ""),
                        ruleColor,
                        r.optString("sh", "circle")));
            }
            return list;
        }

        private static List<CbBreak> parseCbArray(JSONArray arr) throws Exception {
            List<CbBreak> list = new ArrayList<>();
            if (arr == null) return list;
            for (int i = 0; i < arr.length(); i++) {
                JSONObject b = arr.getJSONObject(i);
                list.add(new CbBreak(
                        b.optDouble("mn", Double.NEGATIVE_INFINITY),
                        b.optDouble("mx", Double.POSITIVE_INFINITY),
                        parseHexColor(b.optString("c", "#3388ff"), Color.BLUE)));
            }
            return list;
        }
    }

    static final class UvEntry {
        final String value;
        final int    color;
        UvEntry(String value, int color) { this.value = value; this.color = color; }
    }

    static final class RbRule {
        final String field, op, value, shape;
        final int    color;
        RbRule(String field, String op, String value, int color, String shape) {
            this.field = field; this.op = op; this.value = value;
            this.color = color; this.shape = shape;
        }
    }

    static final class CbBreak {
        final double min, max;
        final int    color;
        CbBreak(double min, double max, int color) {
            this.min = min; this.max = max; this.color = color;
        }
    }

    static final class LabelConfig {
        final String  field;
        final int     sizeSp;
        final int     color;
        final boolean bold;
        final boolean italic;

        LabelConfig(String field, int sizeSp, int color, boolean bold, boolean italic) {
            this.field  = field;
            this.sizeSp = sizeSp;
            this.color  = color;
            this.bold   = bold;
            this.italic = italic;
        }

        static LabelConfig fromJson(JSONObject j) {
            return new LabelConfig(
                    j.optString("f",  ""),
                    j.optInt("sz",    12),
                    parseHexColor(j.optString("c", "#ffffff"), Color.WHITE),
                    j.optBoolean("b", false),
                    j.optBoolean("i", false));
        }
    }

    static final class PopupConfig {
        final String        titleField;
        /** Each entry is [fieldName, alias]. */
        final List<String[]> fields;

        PopupConfig(String titleField, List<String[]> fields) {
            this.titleField = titleField;
            this.fields     = fields;
        }

        static PopupConfig fromJson(JSONObject j) throws Exception {
            String titleField = j.optString("t", "");
            List<String[]> fields = new ArrayList<>();
            JSONArray fldsJ = j.optJSONArray("flds");
            if (fldsJ != null) {
                for (int i = 0; i < fldsJ.length(); i++) {
                    Object entry = fldsJ.get(i);
                    if (entry instanceof String) {
                        fields.add(new String[]{(String) entry, (String) entry});
                    } else if (entry instanceof JSONArray) {
                        JSONArray a = (JSONArray) entry;
                        String fn    = a.optString(0, "");
                        String alias = a.optString(1, fn);
                        fields.add(new String[]{fn, alias});
                    }
                }
            }
            return new PopupConfig(titleField, fields);
        }
    }
}
