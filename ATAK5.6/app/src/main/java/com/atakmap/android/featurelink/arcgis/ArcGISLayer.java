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

    public ArcGISLayer(String name, String url, String type) {
        this.name = name;
        this.url  = url;
        this.type = type;
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
        obj.put("featureCount",       featureCount);
        obj.put("lastSync",           lastSync);
        obj.put("downloadEnabled",    downloadEnabled);
        obj.put("recurrenceInterval", recurrenceInterval);
        obj.put("recurrenceUnit",     recurrenceUnit);
        obj.put("isPliLayer",         isPliLayer);
        obj.put("visible",            visible);
        return obj;
    }

    public static ArcGISLayer fromJson(JSONObject obj) throws Exception {
        ArcGISLayer layer = new ArcGISLayer(
                obj.optString("name", "Unknown"),
                obj.optString("url",  ""),
                obj.optString("type", "public"));
        layer.access           = obj.optString("access", "org");
        layer.featureCount    = obj.optLong("featureCount", 0);
        layer.lastSync        = obj.optLong("lastSync", 0);
        layer.downloadEnabled = obj.optBoolean("downloadEnabled", false);
        layer.isPliLayer      = obj.optBoolean("isPliLayer", false);
        layer.visible         = obj.optBoolean("visible", true);

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
