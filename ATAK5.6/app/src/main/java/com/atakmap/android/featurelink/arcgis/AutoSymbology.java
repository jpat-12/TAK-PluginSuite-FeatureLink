package com.atakmap.android.featurelink.arcgis;

import android.graphics.Color;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * ArcGIS renderer -> shape/marker style extractor — sibling to {@link AutoIconset}, which only
 * handles esriPMS (picture-marker) symbols. This extracts esriSMS (point marker shape/color),
 * esriSLS (line stroke), and esriSFS (polygon fill + outline) symbol JSON into plain style
 * structs, keyed by renderer field value the same way AutoIconset.Result.pathByValue is — no
 * network I/O, no install side effects, pure JSON parsing.
 */
public final class AutoSymbology {

    public static final class StrokeStyle {
        public final int color;
        public final float widthPx;
        public final String dash; // "solid" | "dash" | "dot"

        StrokeStyle(int color, float widthPx, String dash) {
            this.color = color;
            this.widthPx = widthPx;
            this.dash = dash;
        }
    }

    public static final class FillStyle {
        public final int color;
        public final String style; // "solid" | "none"
        /** The fill symbol's own outline (esriSFS.outline is itself an esriSLS) — may be null. */
        public final StrokeStyle outline;

        FillStyle(int color, String style, StrokeStyle outline) {
            this.color = color;
            this.style = style;
            this.outline = outline;
        }
    }

    public static final class MarkerStyle {
        public final int color;
        public final String shape; // "circle" | "square" | "diamond" | "triangle" | "cross" | "x"
        public final float sizePx;

        MarkerStyle(int color, String shape, float sizePx) {
            this.color = color;
            this.shape = shape;
            this.sizePx = sizePx;
        }
    }

    public static final class Result {
        /** Driving field for the by-value maps below; empty for a single-symbol renderer. */
        public final String field;
        public final StrokeStyle singleStroke;
        public final FillStyle singleFill;
        public final MarkerStyle singleMarker;
        /** uniqueValue renderer: field VALUE -> style, keyed the same way AutoIconset.Result.
         * pathByValue is so a feature resolves its style on the same attribute value. */
        public final Map<String, StrokeStyle> strokeByValue;
        public final Map<String, FillStyle> fillByValue;
        public final Map<String, MarkerStyle> markerByValue;

        Result(String field, StrokeStyle singleStroke, FillStyle singleFill, MarkerStyle singleMarker,
                Map<String, StrokeStyle> strokeByValue, Map<String, FillStyle> fillByValue,
                Map<String, MarkerStyle> markerByValue) {
            this.field = field;
            this.singleStroke = singleStroke;
            this.singleFill = singleFill;
            this.singleMarker = singleMarker;
            this.strokeByValue = strokeByValue;
            this.fillByValue = fillByValue;
            this.markerByValue = markerByValue;
        }

        public boolean isEmpty() {
            return singleStroke == null && singleFill == null && singleMarker == null
                    && strokeByValue.isEmpty() && fillByValue.isEmpty() && markerByValue.isEmpty();
        }
    }

    private AutoSymbology() {}

    /**
     * Extracts esriSMS/esriSLS/esriSFS styling from a renderer, mirroring the same
     * simple/uniqueValue/classBreaks/defaultSymbol traversal {@link AutoIconset#generate} uses.
     * Class-breaks entries can't be resolved per-feature by value (numeric-range matching isn't
     * supported here, same caveat as AutoIconset) — only a fallback symbol (defaultSymbol, or the
     * first break if there's no defaultSymbol) is extracted as a single style for that renderer
     * type. Which of stroke/fill/marker actually gets populated per entry is driven entirely by
     * each symbol's own "type" (esriSLS/esriSFS/esriSMS) rather than a geometry-type hint, so
     * mixed-symbol renderers resolve correctly without the caller needing to know the layer's
     * geometry type ahead of time.
     */
    public static Result extract(JSONObject renderer, String fieldOverride) {
        Map<String, StrokeStyle> strokeByValue = new LinkedHashMap<>();
        Map<String, FillStyle> fillByValue = new LinkedHashMap<>();
        Map<String, MarkerStyle> markerByValue = new LinkedHashMap<>();
        StrokeStyle singleStroke = null;
        FillStyle singleFill = null;
        MarkerStyle singleMarker = null;
        String field = "";

        if (renderer != null) {
            String type = renderer.optString("type", "simple");

            if ("uniqueValue".equals(type) || "uniqueValueRenderer".equals(type)) {
                field = renderer.optString("field1", renderer.optString("field", ""));
                JSONArray infos = renderer.optJSONArray("uniqueValueInfos");
                if (infos != null) {
                    for (int i = 0; i < infos.length(); i++) {
                        JSONObject info = infos.optJSONObject(i);
                        if (info == null) continue;
                        String value = info.optString("value", "");
                        JSONObject symbol = info.optJSONObject("symbol");
                        if (value.isEmpty() || symbol == null) continue;
                        StrokeStyle s = strokeFrom(symbol);
                        FillStyle f = fillFrom(symbol);
                        MarkerStyle m = markerFrom(symbol);
                        if (s != null) strokeByValue.put(value, s);
                        if (f != null) fillByValue.put(value, f);
                        if (m != null) markerByValue.put(value, m);
                    }
                }
                JSONObject defSymbol = renderer.optJSONObject("defaultSymbol");
                if (defSymbol != null) {
                    singleStroke = strokeFrom(defSymbol);
                    singleFill = fillFrom(defSymbol);
                    singleMarker = markerFrom(defSymbol);
                }
            } else if ("classBreaks".equals(type) || "classBreaksRenderer".equals(type)) {
                field = renderer.optString("field", "");
                // Class breaks match by numeric range, not value equality — can't be resolved
                // per-feature here (same limitation as AutoIconset); only a fallback symbol is
                // usable as a single style for the whole layer.
                JSONObject fallback = renderer.optJSONObject("defaultSymbol");
                if (fallback == null) {
                    JSONArray infos = renderer.optJSONArray("classBreakInfos");
                    if (infos != null && infos.length() > 0) {
                        JSONObject first = infos.optJSONObject(0);
                        if (first != null) fallback = first.optJSONObject("symbol");
                    }
                }
                if (fallback != null) {
                    singleStroke = strokeFrom(fallback);
                    singleFill = fillFrom(fallback);
                    singleMarker = markerFrom(fallback);
                }
            } else {
                // simple / bare symbol renderer — single style, no field
                JSONObject symbol = renderer.optJSONObject("symbol");
                singleStroke = strokeFrom(symbol);
                singleFill = fillFrom(symbol);
                singleMarker = markerFrom(symbol);
            }
        }

        if (fieldOverride != null && !fieldOverride.isEmpty()) field = fieldOverride;

        return new Result(field, singleStroke, singleFill, singleMarker,
                strokeByValue, fillByValue, markerByValue);
    }

    // -------------------------------------------------------------------------
    // Symbol JSON -> style struct
    // -------------------------------------------------------------------------

    private static StrokeStyle strokeFrom(JSONObject symbol) {
        if (symbol == null || !"esriSLS".equals(symbol.optString("type", ""))) return null;
        int color = esriColor(symbol.optJSONArray("color"), Color.BLUE);
        float width = (float) symbol.optDouble("width", 1.0);
        String dash = dashFromEsriStyle(symbol.optString("style", ""));
        return new StrokeStyle(color, width, dash);
    }

    private static FillStyle fillFrom(JSONObject symbol) {
        if (symbol == null || !"esriSFS".equals(symbol.optString("type", ""))) return null;
        int color = esriColor(symbol.optJSONArray("color"), Color.YELLOW);
        String style = "esriSFSNull".equals(symbol.optString("style", "")) ? "none" : "solid";
        StrokeStyle outline = strokeFrom(symbol.optJSONObject("outline"));
        return new FillStyle(color, style, outline);
    }

    private static MarkerStyle markerFrom(JSONObject symbol) {
        if (symbol == null || !"esriSMS".equals(symbol.optString("type", ""))) return null;
        int color = esriColor(symbol.optJSONArray("color"), Color.RED);
        String shape = markerShapeFromEsri(symbol.optString("style", ""));
        float size = (float) symbol.optDouble("size", 8.0);
        return new MarkerStyle(color, shape, size);
    }

    /** ArcGIS symbol colors are [r,g,b,a] (alpha last, 0-255); unlike DisplayConfig's own
     * esriSymbolColor helper (which drops alpha), this preserves it — translucent polygon
     * fills are common ArcGIS styling and worth carrying over. */
    private static int esriColor(JSONArray arr, int fallback) {
        if (arr == null || arr.length() < 3) return fallback;
        int r = arr.optInt(0, 0);
        int g = arr.optInt(1, 0);
        int b = arr.optInt(2, 0);
        int a = arr.length() >= 4 ? arr.optInt(3, 255) : 255;
        return Color.argb(a, r, g, b);
    }

    private static String dashFromEsriStyle(String esriStyle) {
        if (esriStyle == null) return "solid";
        switch (esriStyle) {
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

    private static String markerShapeFromEsri(String esriStyle) {
        if (esriStyle == null) return "circle";
        switch (esriStyle) {
            case "esriSMSSquare":   return "square";
            case "esriSMSDiamond":  return "diamond";
            case "esriSMSTriangle": return "triangle";
            case "esriSMSCross":    return "cross";
            case "esriSMSX":        return "x";
            default:                return "circle";
        }
    }
}
