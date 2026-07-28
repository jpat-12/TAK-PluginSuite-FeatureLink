package com.atakmap.android.featurelink;

import com.atakmap.android.featurelink.arcgis.ArcGISLayer;

import org.json.JSONObject;

/**
 * Builds the JSON payload for sharing a public FeatureLink layer with another ATAK user.
 * The actual transport is an ATAK Mission Package (see FeatureLinkDropDownReceiver.
 * sendLayerShare()) — ATAK's own native "X wants to send you a file" accept/decline flow —
 * rather than a hand-rolled CoT message; a custom CoT type still got rendered as a stray map
 * marker by ATAK's default importer regardless of type, and diagnosing whether the detail
 * payload even survived the wire round-trip without a live two-device test was too fragile to
 * keep chasing. A Mission Package is a real file transfer with a well-tested accept/decline UI
 * ATAK already provides — the recipient picks it up via the Add Layer page's "Upload Pref File"
 * button once accepted.
 */
final class LayerShareHelper {

    private LayerShareHelper() {}

    /**
     * Mode 2 compact JSON: url/name/recurrence, plus this layer's current sym/lbl/popup/cm
     * styling if it has one applied (see DisplayConfig.toCompactJson()) — {@code displayConfig}
     * is whatever FeatureLinkDropDownReceiver has cached for this layer's URL in
     * layerDisplayConfigs, or null if the layer was never scanned/linked with a display config
     * (e.g. added by pasting a bare URL), in which case the recipient just gets the layer with
     * default styling. Parsed by the exact same DisplayConfig.fromJson() / applyScannedPayload()
     * path a QR scan or deep link already uses.
     */
    static String buildShareConfigJson(ArcGISLayer layer, DisplayConfig displayConfig) {
        try {
            JSONObject layerJ = new JSONObject()
                    .put("name", layer.name)
                    .put("opacity", 1.0)
                    .put("visible", true);
            JSONObject o = new JSONObject()
                    .put("v", 2)
                    .put("url", layer.url)
                    .put("layer", layerJ)
                    .put("private", "private".equals(layer.type));
            if (layer.recurrenceInterval > 0) {
                o.put("freq", new JSONObject()
                        .put("iv", layer.recurrenceInterval)
                        .put("u", layer.recurrenceUnit));
            }
            if (displayConfig != null) {
                JSONObject styling = displayConfig.toCompactJson();
                java.util.Iterator<String> keys = styling.keys();
                while (keys.hasNext()) {
                    String key = keys.next();
                    o.put(key, styling.get(key));
                }
            }
            return o.toString();
        } catch (Exception e) {
            return null;
        }
    }
}
