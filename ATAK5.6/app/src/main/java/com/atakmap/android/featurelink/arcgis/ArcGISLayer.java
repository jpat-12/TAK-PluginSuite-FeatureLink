package com.atakmap.android.featurelink.arcgis;

import org.json.JSONObject;

public class ArcGISLayer {

    public String name;
    public String url;
    public String type;           // "private" or "public"
    /** ArcGIS portal sharing scope for a "My ArcGIS Layers" item: "public" (shared to
     * Everyone), "org", or "private". Only meaningful for items returned by
     * ArcGISRestClient.searchUserLayers() — layers added via URL (page_add_layer) or received
     * via a share default to "org" since there's no portal item to ask. */
    public String access = "org";
    /** Owner username of the portal item — only meaningful for a "Shared with me" item
     * (see ArcGISRestClient.searchSharedWithMeLayers()). Empty for owned/public/URL-added layers. */
    public String sharedBy = "";
    /** Raw Esri geometryType of the layer ("esriGeometryPoint"/"esriGeometryPolyline"/
     * "esriGeometryPolygon"), resolved lazily via a layer metadata fetch and cached here so it's
     * only fetched once per layer. Empty until resolved. */
    public String geometryType = "";
    public long featureCount = 0;
    public long lastSync = 0;
    public boolean downloadEnabled = false;
    public int recurrenceInterval = 180;  // 0 = disabled; >0 = auto-refresh every N units
    public String recurrenceUnit = "s";   // "s", "min", "hr" — UI only edits in seconds now,
                                           // "min"/"hr" only still occur on layers carried over
                                           // from before that change (recurrenceMillis() still
                                           // handles all three correctly either way)
    public boolean isPliLayer = false;
    public boolean visible = true;        // whether this layer's markers show on the map
    /**
     * C-08: which sublayer of the parent FeatureServer this row addresses. {@link #url} is
     * always fully qualified (it already ends in {@code /<layerId>}) so every URL-keyed
     * structure in the plugin keeps working unchanged; this field exists so the UI can show
     * "layer 3 of 5" and so a service root can be re-enumerated later. -1 = not yet resolved.
     */
    public int layerId = -1;
    /** The service's advertised {@code maxRecordCount} (C-06). 0 = unknown. Surfaced in the UI
     * so an operator can tell a genuinely small layer from a paginated one. */
    public int maxRecordCount = 0;
    /** True when the last download hit the pagination cap and the on-map picture is incomplete
     * (C-06). Never present a truncated download as a complete one. */
    public boolean lastDownloadTruncated = false;

    public ArcGISLayer(String name, String url, String type) {
        this.name = name;
        this.url  = url;
        this.type = type;
    }

    /**
     * Canonical form of {@link #url} used for identity. {@code https://x/FeatureServer/0},
     * {@code .../0/} and {@code https://X/FeatureServer/0} are the same layer; without this they
     * were three distinct layers to the plugin, producing duplicate rows, orphaned map items and
     * display configs that silently never applied (Appendix A §9).
     */
    public String canonicalUrl() {
        return canonicalUrl(url);
    }

    public static String canonicalUrl(String raw) {
        if (raw == null) return "";
        String u = raw.trim().replaceAll("/+$", "");
        int scheme = u.indexOf("://");
        if (scheme < 0) return u.toLowerCase(java.util.Locale.ROOT);
        int hostEnd = u.indexOf('/', scheme + 3);
        if (hostEnd < 0) return u.toLowerCase(java.util.Locale.ROOT);
        // scheme + authority are case-insensitive; the path is not.
        return u.substring(0, hostEnd).toLowerCase(java.util.Locale.ROOT) + u.substring(hostEnd);
    }

    /**
     * Identity is the canonical URL. Without this, every {@code contains}/{@code remove}/
     * {@code indexOf} call in the drop-down receiver was reference equality, so a layer object
     * captured in a listener stopped matching the list contents after a reload and
     * {@code saveLayerOfSection} fell through to writing a private layer into the public
     * preference list (Appendix A §9 — a real data-corruption path).
     */
    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof ArcGISLayer)) return false;
        return canonicalUrl().equals(((ArcGISLayer) o).canonicalUrl());
    }

    @Override
    public int hashCode() {
        return canonicalUrl().hashCode();
    }

    /** Returns the auto-refresh period in milliseconds, or 0 if disabled. */
    public long recurrenceMillis() {
        if (recurrenceInterval <= 0) return 0;
        switch (recurrenceUnit != null ? recurrenceUnit : "min") {
            case "s":  return recurrenceInterval * 1_000L;
            case "hr": return recurrenceInterval * 3_600_000L;
            default:   return recurrenceInterval * 60_000L;
        }
    }

    public JSONObject toJson() throws Exception {
        JSONObject obj = new JSONObject();
        obj.put("name",               name);
        obj.put("url",                url);
        obj.put("type",               type);
        obj.put("access",             access);
        obj.put("sharedBy",           sharedBy);
        obj.put("geometryType",       geometryType);
        obj.put("featureCount",       featureCount);
        obj.put("lastSync",           lastSync);
        obj.put("downloadEnabled",    downloadEnabled);
        obj.put("recurrenceInterval", recurrenceInterval);
        obj.put("recurrenceUnit",     recurrenceUnit);
        obj.put("isPliLayer",         isPliLayer);
        obj.put("visible",            visible);
        obj.put("layerId",            layerId);
        obj.put("maxRecordCount",     maxRecordCount);
        return obj;
    }

    public static ArcGISLayer fromJson(JSONObject obj) throws Exception {
        ArcGISLayer layer = new ArcGISLayer(
                obj.optString("name", "Unknown"),
                obj.optString("url",  ""),
                obj.optString("type", "public"));
        layer.access           = obj.optString("access", "org");
        layer.sharedBy         = obj.optString("sharedBy", "");
        layer.geometryType     = obj.optString("geometryType", "");
        layer.featureCount    = obj.optLong("featureCount", 0);
        layer.lastSync        = obj.optLong("lastSync", 0);
        layer.downloadEnabled = obj.optBoolean("downloadEnabled", false);
        layer.isPliLayer      = obj.optBoolean("isPliLayer", false);
        layer.visible         = obj.optBoolean("visible", true);
        layer.layerId         = obj.optInt("layerId", -1);
        layer.maxRecordCount  = obj.optInt("maxRecordCount", 0);

        if (obj.has("recurrenceInterval")) {
            layer.recurrenceInterval = obj.optInt("recurrenceInterval", 0);
            layer.recurrenceUnit     = obj.optString("recurrenceUnit", "min");
        } else {
            // Migrate from old "recurrence" string field
            switch (obj.optString("recurrence", "never")) {
                case "daily":  layer.recurrenceInterval = 24;  layer.recurrenceUnit = "hr";  break;
                case "weekly": layer.recurrenceInterval = 168; layer.recurrenceUnit = "hr";  break;
                default:       layer.recurrenceInterval = 0;   layer.recurrenceUnit = "min"; break;
            }
        }
        return layer;
    }
}
