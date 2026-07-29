package com.atakmap.android.featurelink;

import android.graphics.Color;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

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

    /** Which dataset columns feed CoT uid/type/callsign/remarks. May be null (use defaults). */
    final CotMapping cotMapping;

    /**
     * Recommended auto-refresh interval for this layer, set in TAK Portal's Display
     * Configuration (4th section) — applied only as the *initial* value when a layer is first
     * added from this config; a value the end user later changes in the plugin is a per-device
     * preference the plugin already persists to SharedPreferences, and this config is never
     * re-applied over it on a rescan. 0 = auto-refresh off. See ArcGISLayer.recurrenceInterval/
     * recurrenceUnit for the same fields on the applied side.
     */
    final int    freqInterval;
    final String freqUnit;

    /** True when this config came from sharing a "My ArcGIS Layers"/private layer (see
     * LayerShareHelper.buildShareConfigJson()) — the recipient needs their own ArcGIS access
     * (e.g. group membership) to actually download it, so it's routed into the "Private Layers"
     * section instead of "Public Layers" on their end. */
    final boolean isPrivate;

    /** Present only when this config's icon symbology came from a Web Map's per-layer style
     * override (AUTO-ICONSET-SPEC.md §2.3) rather than the FeatureServer layer's own default
     * renderer — shape {"renderer": <esri renderer JSON>, "uid": <str>, "group": <str>}, the
     * exact values TAK Portal already computed. Re-deriving {@link #url}'s renderer/uid/group
     * independently would get the SERVICE's default (a different renderer entirely) and/or drift
     * if the layer's display name was edited after generation, so all three ride along together
     * rather than letting the device recompute any of them. See
     * FeatureLinkDropDownReceiver.applyScannedDisplayConfig()'s missing-iconset regeneration. */
    final JSONObject rendererOverride;

    private DisplayConfig(String url, String name, float opacity, boolean visible,
            SymConfig sym, LabelConfig lbl, PopupConfig popup, CotMapping cotMapping,
            int freqInterval, String freqUnit, boolean isPrivate, JSONObject rendererOverride) {
        this.url          = url;
        this.name         = name;
        this.opacity      = opacity;
        this.visible      = visible;
        this.sym          = sym;
        this.lbl          = lbl;
        this.popup        = popup;
        this.cotMapping   = cotMapping;
        this.freqInterval = freqInterval;
        this.freqUnit     = freqUnit;
        this.isPrivate    = isPrivate;
        this.rendererOverride = rendererOverride;
    }

    /** Parses a v:2 or v:1-display JSONObject. Returns null if the object is not a display config. */
    static DisplayConfig fromJson(JSONObject o) {
        try {
            String url = o.optString("url", "");

            JSONObject layerJ = o.optJSONObject("layer");
            String  name    = layerJ != null ? layerJ.optString("name",    "")   : "";
            float   opacity = layerJ != null ? (float) layerJ.optDouble("opacity", 1.0) : 1.0f;
            boolean visible = layerJ == null || layerJ.optBoolean("visible", true);

            JSONObject symJ = o.optJSONObject("sym");
            JSONObject lblJ = o.optJSONObject("lbl");
            JSONObject popupJ = o.optJSONObject("popup");
            JSONObject cmJ  = o.optJSONObject("cm");
            JSONObject freqJ = o.optJSONObject("freq");

            SymConfig   sym        = symJ   != null ? SymConfig.fromJson(symJ)     : null;
            LabelConfig lbl        = lblJ   != null ? LabelConfig.fromJson(lblJ)   : null;
            PopupConfig popup      = popupJ != null ? PopupConfig.fromJson(popupJ) : null;
            CotMapping  cotMapping = cmJ    != null ? CotMapping.fromJson(cmJ)     : null;
            int    freqInterval    = freqJ  != null ? freqJ.optInt("iv", 0)        : 0;
            String freqUnit        = freqJ  != null ? freqJ.optString("u", "s")  : "s";
            boolean isPrivate      = o.optBoolean("private", false);
            JSONObject rendererOverride = o.optJSONObject("rendererOverride");

            return new DisplayConfig(url, name, opacity, visible, sym, lbl, popup, cotMapping,
                    freqInterval, freqUnit, isPrivate, rendererOverride);
        } catch (Exception e) {
            return null;
        }
    }

    /**
     * Parses a Mode 3 ("_v" schema) full-config JSONObject. Same information as
     * {@link #fromJson}, but under different key names and with "symbology" as full
     * Esri renderer JSON (simple / uniqueValue / classBreaks over esriSMS / esriPMS symbols)
     * instead of the compact "sym" shape.
     */
    static DisplayConfig fromJsonV3(JSONObject o) {
        try {
            String url = o.optString("featureLayerUrl", "");

            JSONObject layerJ = o.optJSONObject("layer");
            String  name    = layerJ != null ? layerJ.optString("name",    "")   : "";
            float   opacity = layerJ != null ? (float) layerJ.optDouble("opacity", 1.0) : 1.0f;
            boolean visible = layerJ == null || layerJ.optBoolean("visible", true);

            JSONObject symbologyJ  = o.optJSONObject("symbology");
            JSONObject labelsJ     = o.optJSONObject("labels");
            JSONObject popupJ      = o.optJSONObject("popup");
            JSONObject cotMappingJ = o.optJSONObject("cotMapping");
            JSONObject freqJ       = o.optJSONObject("updateFrequency");

            SymConfig   sym        = symbologyJ  != null ? SymConfig.fromEsriRenderer(symbologyJ) : null;
            LabelConfig lbl        = labelsJ     != null ? LabelConfig.fromJsonV3(labelsJ)         : null;
            PopupConfig popup      = popupJ      != null ? PopupConfig.fromJsonV3(popupJ)          : null;
            CotMapping  cotMapping = cotMappingJ != null ? CotMapping.fromJsonV3(cotMappingJ)      : null;
            boolean freqEnabled    = freqJ != null && freqJ.optBoolean("enabled", false);
            int    freqInterval    = freqEnabled ? freqJ.optInt("intervalValue", 0)       : 0;
            String freqUnit        = freqEnabled ? freqJ.optString("intervalUnit", "s") : "s";

            return new DisplayConfig(url, name, opacity, visible, sym, lbl, popup, cotMapping,
                    freqInterval, freqUnit, false, null);
        } catch (Exception e) {
            return null;
        }
    }

    /**
     * Builds a display config that maps a layer's own renderer values to the iconset paths a
     * local {@code AutoIconset} generation just produced (AUTO-ICONSET-SPEC.md / Phase A). This
     * is what makes a plain "paste a Feature Service URL" (method 5) add render the layer's
     * custom icons on THIS device — not only on devices that later receive its CoT. There is no
     * server and no user-authored config involved; the styling is derived entirely from the
     * layer's renderer plus the just-generated icon paths.
     *
     * @param url            the layer URL this config is keyed under (feature download target)
     * @param field          renderer driving field (attribute matched per feature); "" for single
     * @param singleIconPath one "uid/group/filename" for a single-symbol renderer, else null
     * @param pathByValue     field value → "uid/group/filename" for a uniqueValue renderer
     */
    static DisplayConfig forAutoIcons(String url, String field, String singleIconPath,
            Map<String, String> pathByValue) {
        SymConfig sym;
        if (singleIconPath != null && !singleIconPath.isEmpty()) {
            // "ic" — single custom icon for every feature (resolveIconsetPath returns usericonPath).
            sym = new SymConfig("ic", Color.BLUE, Color.BLACK, 12, "circle", 1.0f, "",
                    new ArrayList<>(), new ArrayList<>(), new ArrayList<>(), Color.BLUE,
                    new ArrayList<>(), "", "", singleIconPath);
        } else {
            // "adv" — per-value icon entries; resolveIconsetPath matches attrs[field] to e.value.
            List<UvEntry> adv = new ArrayList<>();
            if (pathByValue != null) {
                for (Map.Entry<String, String> e : pathByValue.entrySet()) {
                    adv.add(new UvEntry(e.getKey(), Color.BLUE, true, "", "", e.getValue()));
                }
            }
            sym = new SymConfig("adv", Color.BLUE, Color.BLACK, 12, "circle", 1.0f,
                    field == null ? "" : field,
                    new ArrayList<>(), adv, new ArrayList<>(), Color.BLUE,
                    new ArrayList<>(), "", "", null);
        }
        return new DisplayConfig(url, "", 1.0f, true, sym, null, null, null, 0, "s", false, null);
    }

    /**
     * Serializes back to the same compact "sym"/"lbl"/"popup"/"cm" shape {@link #fromJson}
     * parses — the plugin only ever needed to read this schema before (produced by the web
     * configurator), but sharing an already-styled layer with another device (see
     * FeatureLinkDropDownReceiver.sendLayerShare()) means writing it back out too.
     */
    JSONObject toCompactJson() throws Exception {
        JSONObject o = new JSONObject();
        if (sym != null) o.put("sym", sym.toJson());
        if (lbl != null) o.put("lbl", lbl.toJson());
        if (popup != null) o.put("popup", popup.toJson());
        if (cotMapping != null) {
            JSONObject cm = cotMapping.toJson();
            if (cm.length() > 0) o.put("cm", cm);
        }
        return o;
    }

    static String toHexColor(int argb) {
        return String.format("#%06x", argb & 0xFFFFFF);
    }

    /**
     * Every custom iconset UID this config's symbology references, across the top-level "ic"
     * symbol and any per-value/per-rule icon entries ("adv"/"uv"/"rb"). Derived directly from
     * the already-parsed sym rather than trusting a separately-provided list, so it can't drift
     * out of sync with what actually gets applied and works for every source (QR scan, TAK
     * Portal link, or a layer shared from another device) rather than only ones a config
     * source bothered to compute. Used to warn the user before applying if their device is
     * missing one of these iconsets — see FeatureLinkDropDownReceiver.getMissingIconsetUids().
     */
    Set<String> referencedIconsetUids() {
        Set<String> uids = new HashSet<>();
        if (sym == null) return uids;
        addIconsetUid(uids, sym.iconset);
        for (UvEntry e : sym.uvEntries) addIconsetUid(uids, e.iconset);
        for (UvEntry e : sym.advValues) addIconsetUid(uids, e.iconset);
        for (RbRule r : sym.rbRules) addIconsetUid(uids, r.iconset);
        return uids;
    }

    private static void addIconsetUid(Set<String> uids, String iconset) {
        if (iconset != null && !iconset.isEmpty()) uids.add(iconset);
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
     * Returns the ATAK {@code IconsetPath} meta-data value (see
     * {@code com.atakmap.android.icons.UserIcon.IconsetPath}) for a feature, or null if no
     * custom icon applies and the marker should keep its default CoT-type icon.
     *
     * Resolved for the "ic" (single icon) sym type, and for "adv" per-value entries whose
     * mode is "icon" rather than "shape".
     */
    String resolveIconsetPath(Map<String, String> attrs) {
        if (sym == null) return null;
        if ("ic".equals(sym.type)) {
            return resolvedOrLegacy(sym.usericonPath, sym.iconset, sym.iconFile);
        }
        if ("adv".equals(sym.type)) {
            String val = attrs.getOrDefault(sym.fieldName, "");
            for (UvEntry e : sym.advValues) {
                if (e.value.equals(val) && e.isIcon) {
                    return resolvedOrLegacy(e.usericonPath, e.iconset, e.iconFile);
                }
            }
            for (RbRule r : sym.rbRules) {
                String fval = attrs.getOrDefault(r.field, "");
                if (matchesRule(fval, r.op, r.value) && r.isIcon) {
                    return resolvedOrLegacy(r.usericonPath, r.iconset, r.iconFile);
                }
            }
        }
        return null;
    }

    /**
     * Prefers the server-resolved {@code usericonPath} ("up" in Modes 1/2, "usericonPath" in
     * the Mode 3 Esri "symbol" object) — the exact "<iconset-uid>/<group>/<filename>" string
     * the FeatureLink Display Configurator already resolved against icons/manifest.json.
     * Falls back to the legacy iconset-name + filename concat only for older payloads that
     * predate that field (which never actually resolved to a valid ATAK iconset path, since
     * ATAK needs the iconset UID and exact group folder, not the display name).
     */
    private static String resolvedOrLegacy(String usericonPath, String iconset, String iconFile) {
        if (usericonPath != null && !usericonPath.isEmpty()) return usericonPath;
        return buildIconsetPath(iconset, iconFile);
    }

    private static String buildIconsetPath(String iconset, String iconFile) {
        if (iconset == null || iconset.isEmpty() || iconFile == null || iconFile.isEmpty()) return null;
        return iconset + "/" + iconFile;
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

    /**
     * Like {@code o.optString(key, null)}, but actually returns Java {@code null} for a JSON
     * {@code null} value. org.json's optString(key, fallback) calls {@code .toString()} on
     * whatever {@code opt(key)} returns; for an explicit JSON null that's the JSONObject.NULL
     * sentinel, whose toString() is the literal string "null" — so optString(key, null) silently
     * returns the 4-character string "null" instead of the fallback. Every "up" (usericonPath)
     * field is serialized as an explicit JSON null whenever the web configurator has no resolved
     * path, so this bit without the guard here.
     */
    private static String optStringOrNull(JSONObject o, String key) {
        return (o.has(key) && !o.isNull(key)) ? o.optString(key) : null;
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
        /** Server-resolved "<iconset-uid>/<group>/<filename>" — see resolvedOrLegacy(). */
        final String usericonPath;

        private SymConfig(String type, int color, int outlineColor, int sizePx,
                String shape, float opacity, String fieldName,
                List<UvEntry> uvEntries, List<UvEntry> advValues, List<RbRule> rbRules,
                int defaultColor, List<CbBreak> cbBreaks,
                String iconset, String iconFile, String usericonPath) {
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
            this.usericonPath = usericonPath;
        }

        /** Reverse of {@link #fromJson} — see {@link DisplayConfig#toCompactJson()}. */
        JSONObject toJson() throws Exception {
            JSONObject j = new JSONObject();
            j.put("t", type);
            j.put("c", toHexColor(color));
            j.put("oc", toHexColor(outlineColor));
            j.put("sz", sizePx);
            j.put("sh", shape);
            j.put("op", opacity);
            if (fieldName != null && !fieldName.isEmpty()) j.put("f", fieldName);
            if (iconset != null && !iconset.isEmpty()) j.put("is", iconset);
            if (iconFile != null && !iconFile.isEmpty()) j.put("ic", iconFile);
            if (usericonPath != null) j.put("up", usericonPath);
            j.put("dc", toHexColor(defaultColor));

            if (!uvEntries.isEmpty()) {
                JSONArray uv = new JSONArray();
                for (UvEntry e : uvEntries) {
                    uv.put(new JSONObject().put("v", e.value).put("c", toHexColor(e.color)));
                }
                j.put("uv", uv);
            }
            if (!advValues.isEmpty()) {
                JSONArray vs = new JSONArray();
                for (UvEntry e : advValues) vs.put(e.toJson());
                j.put("vs", vs);
            }
            if (!rbRules.isEmpty()) {
                JSONArray rules = new JSONArray();
                for (RbRule r : rbRules) rules.put(r.toJson());
                // Compact schema: "rb" type uses the "rules" key, "adv" uses "r" — see fromJson().
                j.put("adv".equals(type) ? "r" : "rules", rules);
            }
            if (!cbBreaks.isEmpty()) {
                JSONArray cb = new JSONArray();
                for (CbBreak b : cbBreaks) {
                    cb.put(new JSONObject().put("mn", b.min).put("mx", b.max).put("c", toHexColor(b.color)));
                }
                j.put("cb", cb);
            }
            return j;
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
            String usericonPath= optStringOrNull(j, "up");
            int    defaultColor= parseHexColor(j.optString("dc", "#3388ff"), Color.BLUE);

            List<UvEntry> uvEntries = parseUvArray(j.optJSONArray("uv"));
            List<UvEntry> advValues = parseAdvVs(j.optJSONArray("vs"));
            List<RbRule>  rbRules   = parseRules(j.optJSONArray("rules"), j.optJSONArray("r"));
            List<CbBreak> cbBreaks  = parseCbArray(j.optJSONArray("cb"));

            return new SymConfig(type, color, outlineColor, sizePx, shape, opacity, fieldName,
                    uvEntries, advValues, rbRules, defaultColor, cbBreaks, iconset, iconFile, usericonPath);
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
                // vs entries: {v, m, c, sh, is, ic, up} — m ("shape"|"icon") picks which of
                // sh (shape) vs is/ic/up (iconset/icon file/resolved usericonPath) applies
                boolean isIcon = "icon".equals(e.optString("m", "shape"));
                list.add(new UvEntry(e.optString("v", ""),
                        parseHexColor(e.optString("c", "#3388ff"), Color.BLUE),
                        isIcon, e.optString("is", ""), e.optString("ic", ""), optStringOrNull(e, "up")));
            }
            return list;
        }

        // Handles both "rules" key (rb) and "r" key (adv). "adv" rule entries additionally
        // carry icon fields (m/is/ic/up), same shape as "vs" per-value entries — see
        // buildUrlConfigExport()'s "r" mapping in the web configurator.
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
                boolean isIcon = "icon".equals(r.optString("m", "shape"));
                list.add(new RbRule(
                        r.optString("f",  ""),
                        r.optString("o",  "="),
                        r.optString("v",  ""),
                        ruleColor,
                        r.optString("sh", "circle"),
                        isIcon, r.optString("is", ""), r.optString("ic", ""), optStringOrNull(r, "up")));
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

        // -------------------------------------------------------------------------
        // Mode 3 — Esri REST renderer JSON (developers.arcgis.com renderer-objects)
        // -------------------------------------------------------------------------

        /**
         * Translates a standard Esri REST renderer ("simple" / "uniqueValue" / "classBreaks"
         * over esriSMS/esriPMS symbols) into the same {@link SymConfig} shape used by the
         * compact Modes 1/2 "sym" object, so {@link DisplayConfig#resolveColor} needs no
         * renderer-specific logic. Best-effort: fields Esri renderer JSON doesn't carry
         * (e.g. custom icons) fall back to the same defaults the compact modes use.
         */
        static SymConfig fromEsriRenderer(JSONObject r) throws Exception {
            String rendererType = r.optString("type", "simple");

            switch (rendererType) {
                case "uniqueValue":
                case "uniqueValueRenderer": {
                    String field = r.optString("field1", r.optString("field", ""));
                    List<UvEntry> entries = new ArrayList<>();
                    JSONArray infos = r.optJSONArray("uniqueValueInfos");
                    if (infos != null) {
                        for (int i = 0; i < infos.length(); i++) {
                            JSONObject info = infos.getJSONObject(i);
                            entries.add(new UvEntry(info.optString("value", ""),
                                    esriSymbolColor(info.optJSONObject("symbol"))));
                        }
                    }
                    int defaultColor = esriSymbolColor(r.optJSONObject("defaultSymbol"));
                    return new SymConfig("uv", defaultColor, Color.BLACK, 12, "circle",
                            1.0f, field, entries, new ArrayList<>(), new ArrayList<>(),
                            defaultColor, new ArrayList<>(), "", "", null);
                }

                case "classBreaks":
                case "classBreaksRenderer": {
                    String field = r.optString("field", "");
                    List<CbBreak> breaks = new ArrayList<>();
                    JSONArray infos = r.optJSONArray("classBreakInfos");
                    if (infos != null) {
                        for (int i = 0; i < infos.length(); i++) {
                            JSONObject info = infos.getJSONObject(i);
                            breaks.add(new CbBreak(
                                    info.optDouble("classMinValue", Double.NEGATIVE_INFINITY),
                                    info.optDouble("classMaxValue", Double.POSITIVE_INFINITY),
                                    esriSymbolColor(info.optJSONObject("symbol"))));
                        }
                    }
                    int defaultColor = esriSymbolColor(r.optJSONObject("defaultSymbol"));
                    return new SymConfig("cb", defaultColor, Color.BLACK, 12, "circle",
                            1.0f, field, new ArrayList<>(), new ArrayList<>(), new ArrayList<>(),
                            defaultColor, breaks, "", "", null);
                }

                case "rule-based": {
                    // Not a real Esri renderer type (Esri's REST API only standardizes simple/
                    // uniqueValue/classBreaks) — this tool's own extension for Rule-Based and
                    // Advanced-with-rules symbology. Each rule carries the raw field/op/value
                    // alongside an Esri "where" clause (kept for interop with other consumers,
                    // ignored here) — see buildSymbologyExport() in index.html.
                    List<RbRule> rules = new ArrayList<>();
                    JSONArray rulesArr = r.optJSONArray("rules");
                    if (rulesArr != null) {
                        for (int i = 0; i < rulesArr.length(); i++) {
                            JSONObject rule = rulesArr.getJSONObject(i);
                            JSONObject symbol = rule.optJSONObject("symbol");
                            boolean isIcon = symbol != null && "esriPMS".equals(symbol.optString("type", ""));
                            String shape = !isIcon && symbol != null
                                    ? esriStyleToShape(symbol.optString("style", "")) : "circle";
                            String usericonPath = isIcon ? optStringOrNull(symbol, "usericonPath") : null;
                            rules.add(new RbRule(
                                    rule.optString("field", ""),
                                    rule.optString("op", "="),
                                    rule.optString("value", ""),
                                    isIcon ? Color.BLUE : esriSymbolColor(symbol),
                                    shape,
                                    isIcon, "", "", usericonPath));
                        }
                    }
                    int defaultColor = esriSymbolColor(r.optJSONObject("defaultSymbol"));
                    return new SymConfig("rb", defaultColor, Color.BLACK, 12, "circle",
                            1.0f, "", new ArrayList<>(), new ArrayList<>(), rules,
                            defaultColor, new ArrayList<>(), "", "", null);
                }

                case "simple":
                case "simpleRenderer":
                default: {
                    JSONObject symbol = r.optJSONObject("symbol");
                    if (symbol != null && "esriPMS".equals(symbol.optString("type", ""))) {
                        // Picture marker symbol. usericonPath is the FeatureLink Display
                        // Configurator's server-resolved "<iconset-uid>/<group>/<filename>"
                        // (added alongside "url" once the web tool started resolving it — see
                        // resolveUsericonPath() in index.html); null for older payloads or an
                        // iconset with no ATAK iconset UID (e.g. TAK-UserIcons).
                        int sizePx = (int) Math.round(symbol.optDouble("width", 12));
                        String usericonPath = optStringOrNull(symbol, "usericonPath");
                        return new SymConfig("ic", Color.BLUE, Color.BLACK, sizePx, "circle",
                                1.0f, "", new ArrayList<>(), new ArrayList<>(), new ArrayList<>(),
                                Color.BLUE, new ArrayList<>(), "", symbol.optString("url", ""), usericonPath);
                    }
                    int color   = esriSymbolColor(symbol);
                    int outline = symbol != null ? esriOutlineColor(symbol) : Color.BLACK;
                    int sizePx  = symbol != null ? (int) Math.round(symbol.optDouble("size", 12)) : 12;
                    String shape = symbol != null ? esriStyleToShape(symbol.optString("style", "")) : "circle";
                    return new SymConfig("s", color, outline, sizePx, shape, 1.0f, "",
                            new ArrayList<>(), new ArrayList<>(), new ArrayList<>(),
                            color, new ArrayList<>(), "", "", null);
                }
            }
        }

        /** Extracts an opaque RGB color from an esriSMS/esriPMS symbol's "color":[r,g,b,a] array. */
        private static int esriSymbolColor(JSONObject symbol) {
            if (symbol == null) return Color.parseColor("#3388ff");
            JSONArray c = symbol.optJSONArray("color");
            return esriColorArray(c, Color.parseColor("#3388ff"));
        }

        private static int esriOutlineColor(JSONObject symbol) {
            JSONObject outline = symbol.optJSONObject("outline");
            if (outline == null) return Color.BLACK;
            return esriColorArray(outline.optJSONArray("color"), Color.BLACK);
        }

        /** Esri colors are [r,g,b,a] with each channel 0-255 (alpha dropped — opaque RGB only). */
        private static int esriColorArray(JSONArray arr, int fallback) {
            if (arr == null || arr.length() < 3) return fallback;
            try {
                return Color.rgb(arr.getInt(0), arr.getInt(1), arr.getInt(2));
            } catch (Exception e) {
                return fallback;
            }
        }

        private static String esriStyleToShape(String esriStyle) {
            switch (esriStyle) {
                case "esriSMSSquare":   return "square";
                case "esriSMSDiamond":  return "diamond";
                case "esriSMSTriangle": return "triangle";
                case "esriSMSCross":    return "cross";
                case "esriSMSX":        return "x";
                case "esriSMSCircle":
                default:                return "circle";
            }
        }
    }

    static final class UvEntry {
        final String value;
        final int    color;
        /** Only meaningful for "adv" per-value entries — true if this value uses a custom icon. */
        final boolean isIcon;
        final String  iconset;
        final String  iconFile;
        /** Server-resolved "<iconset-uid>/<group>/<filename>" — see resolvedOrLegacy(). */
        final String  usericonPath;

        UvEntry(String value, int color) {
            this(value, color, false, "", "", null);
        }

        UvEntry(String value, int color, boolean isIcon, String iconset, String iconFile, String usericonPath) {
            this.value        = value;
            this.color        = color;
            this.isIcon       = isIcon;
            this.iconset      = iconset;
            this.iconFile     = iconFile;
            this.usericonPath = usericonPath;
        }

        /** Reverse of {@link SymConfig#parseAdvVs} — a "vs" per-value entry. No stored shape
         * (parseAdvVs never reads "sh" — the plugin doesn't use it, only the web configurator's
         * own preview does), so this always writes "circle"; harmless since nothing on the
         * plugin side reads it back either. */
        JSONObject toJson() throws Exception {
            JSONObject j = new JSONObject()
                    .put("v", value)
                    .put("c", DisplayConfig.toHexColor(color))
                    .put("sh", "circle")
                    .put("m", isIcon ? "icon" : "shape");
            if (iconset != null && !iconset.isEmpty()) j.put("is", iconset);
            if (iconFile != null && !iconFile.isEmpty()) j.put("ic", iconFile);
            if (usericonPath != null) j.put("up", usericonPath);
            return j;
        }
    }

    static final class RbRule {
        final String field, op, value, shape;
        final int    color;
        /** True if this rule uses a custom icon rather than a colored shape. */
        final boolean isIcon;
        final String  iconset;
        final String  iconFile;
        /** Server-resolved "<iconset-uid>/<group>/<filename>" — see resolvedOrLegacy(). */
        final String  usericonPath;

        RbRule(String field, String op, String value, int color, String shape) {
            this(field, op, value, color, shape, false, "", "", null);
        }

        RbRule(String field, String op, String value, int color, String shape,
                boolean isIcon, String iconset, String iconFile, String usericonPath) {
            this.field = field; this.op = op; this.value = value;
            this.color = color; this.shape = shape;
            this.isIcon       = isIcon;
            this.iconset      = iconset;
            this.iconFile     = iconFile;
            this.usericonPath = usericonPath;
        }

        /** Reverse of {@link SymConfig#parseRules} — one "rules"/"r" entry. */
        JSONObject toJson() throws Exception {
            JSONObject j = new JSONObject()
                    .put("f", field)
                    .put("o", op)
                    .put("v", value)
                    .put("c", DisplayConfig.toHexColor(color))
                    .put("sh", shape)
                    .put("m", isIcon ? "icon" : "shape");
            if (iconset != null && !iconset.isEmpty()) j.put("is", iconset);
            if (iconFile != null && !iconFile.isEmpty()) j.put("ic", iconFile);
            if (usericonPath != null) j.put("up", usericonPath);
            return j;
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

        /** Reverse of {@link #fromJson}. */
        JSONObject toJson() throws Exception {
            return new JSONObject()
                    .put("f", field)
                    .put("sz", sizeSp)
                    .put("c", DisplayConfig.toHexColor(color))
                    .put("b", bold)
                    .put("i", italic);
        }

        static LabelConfig fromJson(JSONObject j) {
            return new LabelConfig(
                    j.optString("f",  ""),
                    j.optInt("sz",    12),
                    parseHexColor(j.optString("c", "#ffffff"), Color.WHITE),
                    j.optBoolean("b", false),
                    j.optBoolean("i", false));
        }

        /** Mode 3 "labels" object: {enabled, field, fontSize, color, haloColor, haloSize, bold, italic}. */
        static LabelConfig fromJsonV3(JSONObject j) {
            if (!j.optBoolean("enabled", true)) return null;
            return new LabelConfig(
                    j.optString("field",    ""),
                    j.optInt("fontSize",    12),
                    parseHexColor(j.optString("color", "#ffffff"), Color.WHITE),
                    j.optBoolean("bold",   false),
                    j.optBoolean("italic", false));
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

        /** Reverse of {@link #fromJson}. */
        JSONObject toJson() throws Exception {
            JSONObject j = new JSONObject().put("t", titleField);
            JSONArray flds = new JSONArray();
            for (String[] f : fields) {
                if (f[0].equals(f[1])) flds.put(f[0]);
                else flds.put(new JSONArray().put(f[0]).put(f[1]));
            }
            j.put("flds", flds);
            return j;
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

        /**
         * Mode 3 "popup" object: {enabled, title, html, fields:[{f,a},...]}.
         * "html" (custom popup template) is ignored — ATAK remarks are plain text, matching
         * how Modes 1/2's popup is already applied via {@link DisplayConfig#buildRemarks}.
         */
        static PopupConfig fromJsonV3(JSONObject j) throws Exception {
            if (!j.optBoolean("enabled", true)) return null;
            String titleField = j.optString("title", "");
            List<String[]> fields = new ArrayList<>();
            JSONArray fldsJ = j.optJSONArray("fields");
            if (fldsJ != null) {
                for (int i = 0; i < fldsJ.length(); i++) {
                    JSONObject entry = fldsJ.getJSONObject(i);
                    String fn    = entry.optString("f", "");
                    String alias = entry.optString("a", fn);
                    fields.add(new String[]{fn, alias});
                }
            }
            return new PopupConfig(titleField, fields);
        }
    }

    /**
     * Maps dataset attribute column names to CoT roles (uid / type / callsign / remarks) —
     * lets a FeatureLayer whose own field names don't happen to be "uid"/"cot_type"/
     * "tak_callsign"/"tak_remarks" still drive those CoT parts, instead of requiring the
     * layer's schema to match ArcGISRestClient's hardcoded defaults. A blank/absent field
     * in the mapping means "keep the hardcoded default behavior for that one part" — see
     * {@link com.atakmap.android.featurelink.arcgis.ArcGISRestClient#downloadLayerAsCoT}.
     */
    static final class CotMapping {
        /** Candidate attribute columns for the CoT UID, tried in order — first non-empty value
         * wins. Empty list = use the default ("uid" column / generated). */
        final List<String> uidFields;
        /** Candidate columns for the CoT type string. Empty = use the default ("cot_type" / "a-f-G"). */
        final List<String> typeFields;
        /** Candidate columns for the callsign. Empty = use the default ("tak_callsign" / "Feature-N"). */
        final List<String> callsignFields;
        /** Candidate columns for remarks text. Empty = use the default ("tak_remarks" column). */
        final List<String> remarksFields;

        CotMapping(List<String> uidFields, List<String> typeFields, List<String> callsignFields, List<String> remarksFields) {
            this.uidFields      = uidFields      != null ? uidFields      : Collections.emptyList();
            this.typeFields     = typeFields     != null ? typeFields     : Collections.emptyList();
            this.callsignFields = callsignFields != null ? callsignFields : Collections.emptyList();
            this.remarksFields  = remarksFields  != null ? remarksFields  : Collections.emptyList();
        }

        /** Reverse of {@link #fromJson} — the compact "cm" shape (empty if nothing's mapped). */
        JSONObject toJson() throws Exception {
            JSONObject j = new JSONObject();
            if (!uidFields.isEmpty())      j.put("uf", new JSONArray(uidFields));
            if (!typeFields.isEmpty())     j.put("tf", new JSONArray(typeFields));
            if (!callsignFields.isEmpty()) j.put("cf", new JSONArray(callsignFields));
            if (!remarksFields.isEmpty())  j.put("rf", new JSONArray(remarksFields));
            return j;
        }

        private static List<String> stringList(JSONArray arr) {
            List<String> out = new ArrayList<>();
            if (arr == null) return out;
            for (int i = 0; i < arr.length(); i++) {
                String v = arr.optString(i, "");
                if (!v.isEmpty()) out.add(v);
            }
            return out;
        }

        /**
         * Mode 1/2 compact "cm" object: {uf, tf, cf, rf}, each an array of candidate field
         * names. Also accepts a single string for each key (older payloads) for compatibility.
         */
        static CotMapping fromJson(JSONObject j) {
            return new CotMapping(
                    fieldList(j, "uf"),
                    fieldList(j, "tf"),
                    fieldList(j, "cf"),
                    fieldList(j, "rf"));
        }

        /**
         * Mode 3 "cotMapping" object: {uidFields, typeFields, callsignFields, remarksFields},
         * each an array of candidate field names. Also accepts a single string for each key
         * (older payloads) for compatibility.
         */
        static CotMapping fromJsonV3(JSONObject j) {
            return new CotMapping(
                    fieldList(j, "uidFields"),
                    fieldList(j, "typeFields"),
                    fieldList(j, "callsignFields"),
                    fieldList(j, "remarksFields"));
        }

        /** Reads key as a JSON array of strings, or wraps a single string value for backward compat. */
        private static List<String> fieldList(JSONObject j, String key) {
            JSONArray arr = j.optJSONArray(key);
            if (arr != null) return stringList(arr);
            String single = j.optString(key, "");
            return single.isEmpty() ? Collections.emptyList() : Collections.singletonList(single);
        }
    }
}
