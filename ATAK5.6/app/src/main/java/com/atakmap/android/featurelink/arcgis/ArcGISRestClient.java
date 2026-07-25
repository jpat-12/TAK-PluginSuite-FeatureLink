package com.atakmap.android.featurelink.arcgis;

import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Handles all ArcGIS REST API communication using plain HttpURLConnection.
 * All methods are blocking — call from a background thread.
 */
public class ArcGISRestClient {

    private static final String TAG = "ArcGISRestClient";
    private static final int TIMEOUT_MS = 15_000;

    // -------------------------------------------------------------------------
    // Layer search
    // -------------------------------------------------------------------------

    /**
     * Searches the portal for Feature Services owned by the authenticated user only.
     * Filters by owner so unrelated public services are excluded.
     */
    public List<ArcGISLayer> searchUserLayers(String portalUrl, String token, String username) {
        List<ArcGISLayer> layers = new ArrayList<>();
        try {
            // owner: filter ensures we only see this user's content
            String q = "type:\"Feature Service\" AND owner:" + username;
            String endpoint = normalizePortalUrl(portalUrl)
                    + "/sharing/rest/search?q=" + enc(q)
                    + "&num=100&f=json&token=" + enc(token);
            String response = httpGet(endpoint, null);
            if (response == null) return layers;

            JSONObject json = new JSONObject(response);
            JSONArray results = json.optJSONArray("results");
            if (results == null) return layers;

            for (int i = 0; i < results.length(); i++) {
                JSONObject item = results.getJSONObject(i);
                String name = item.optString("title", "Unnamed");
                String url = item.optString("url", "");
                if (!url.isEmpty()) {
                    layers.add(new ArcGISLayer(name, url, "private"));
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "searchUserLayers failed", e);
        }
        return layers;
    }

    // -------------------------------------------------------------------------
    // Feature count
    // -------------------------------------------------------------------------

    /**
     * Returns the total feature count of a layer (layer index 0).
     */
    public long queryFeatureCount(String serviceUrl, String token) throws Exception {
        String layerUrl = ensureLayerIndex(serviceUrl);
        String params = "where=1%3D1&returnCountOnly=true&f=json"
                + (token != null ? "&token=" + enc(token) : "");
        String response = httpGet(layerUrl + "/query?" + params, null);
        if (response == null) return 0;
        JSONObject json = new JSONObject(response);
        return json.optLong("count", 0);
    }

    // -------------------------------------------------------------------------
    // Layer info
    // -------------------------------------------------------------------------

    /**
     * Fetches basic metadata about a Feature Service layer.
     */
    public ArcGISLayer fetchLayerInfo(String serviceUrl) {
        try {
            String url = ensureLayerIndex(serviceUrl);
            String response = httpGet(url + "?f=json", null);
            if (response == null) return null;
            JSONObject json = new JSONObject(response);
            if (json.has("error")) return null;
            String name = json.optString("name", json.optString("serviceDescription", "Unknown Layer"));
            return new ArcGISLayer(name, serviceUrl, "public");
        } catch (Exception e) {
            Log.e(TAG, "fetchLayerInfo failed for " + serviceUrl, e);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Feature download → ATAK map injection
    // -------------------------------------------------------------------------

    /**
     * Which attribute column to read for each CoT part, overriding the hardcoded defaults
     * below (empty/null field = keep that one part's default). Deliberately its own small
     * struct rather than depending on the outer featurelink package's DisplayConfig — this
     * package doesn't otherwise know about display-config concepts, just field names.
     */
    public static final class CotFieldMapping {
        /** Candidate columns per part, tried in order — first one present with a non-empty
         * value on a given feature wins. Empty list = keep that part's hardcoded default. */
        public final List<String> uidFields, typeFields, callsignFields, remarksFields;

        public CotFieldMapping(List<String> uidFields, List<String> typeFields,
                List<String> callsignFields, List<String> remarksFields) {
            this.uidFields      = uidFields      != null ? uidFields      : Collections.emptyList();
            this.typeFields     = typeFields     != null ? typeFields     : Collections.emptyList();
            this.callsignFields = callsignFields != null ? callsignFields : Collections.emptyList();
            this.remarksFields  = remarksFields  != null ? remarksFields  : Collections.emptyList();
        }
    }

    /** Tries each candidate column in order against this feature's attributes; returns the
     * first non-empty value found, or "" if none of them (or the fallback) have one. */
    private static String resolveField(JSONObject attrs, List<String> candidates, String fallbackField) {
        for (String field : candidates) {
            String v = nullToEmpty(attrs.optString(field, null));
            if (!v.isEmpty()) return v;
        }
        return nullToEmpty(attrs.optString(fallbackField, null));
    }

    /**
     * Downloads all features from a layer and returns them as a list of
     * {@link DownloadedFeature} objects ready for direct injection into the ATAK map.
     * Supports point, polyline, and polygon geometry (polyline/polygon uses first vertex).
     *
     * @param serviceUrl Feature Service URL (with or without /0)
     * @param token      ArcGIS token, or null for public layers
     * @return list of downloaded features (never null; empty on failure)
     */
    public List<DownloadedFeature> downloadLayerAsCoT(String serviceUrl, String token)
            throws Exception {
        return downloadLayerAsCoT(serviceUrl, token, null);
    }

    /**
     * Same as {@link #downloadLayerAsCoT(String, String)}, but lets the caller map CoT
     * parts to whichever attribute columns the layer actually has (e.g. an existing
     * FeatureLayer whose asset-ID column isn't named "uid").
     *
     * @param mapping field mapping to use, or null to use the hardcoded defaults for every part
     */
    public List<DownloadedFeature> downloadLayerAsCoT(String serviceUrl, String token,
            CotFieldMapping mapping) throws Exception {
        List<DownloadedFeature> results = new ArrayList<>();
        String layerUrl = ensureLayerIndex(serviceUrl);
        String params = "where=1%3D1&outFields=*&outSR=4326&f=json"
                + (token != null ? "&token=" + enc(token) : "");
        String response = httpGet(layerUrl + "/query?" + params, null);
        if (response == null) return results;

        JSONObject json = new JSONObject(response);
        JSONArray features = json.optJSONArray("features");
        if (features == null) return results;

        for (int i = 0; i < features.length(); i++) {
            try {
                JSONObject feat  = features.getJSONObject(i);
                JSONObject attrs = feat.optJSONObject("attributes");
                JSONObject geom  = feat.optJSONObject("geometry");
                if (geom == null) continue;

                double lat = Double.NaN, lon = Double.NaN, hae = Double.NaN;
                if (geom.has("x") && geom.has("y")) {
                    lon = geom.optDouble("x", Double.NaN);
                    lat = geom.optDouble("y", Double.NaN);
                } else {
                    double[] v = extractFirstVertex(geom);
                    if (v != null) { lon = v[0]; lat = v[1]; }
                }
                if (Double.isNaN(lat) || Double.isNaN(lon)) continue;

                // Collect all attributes as strings for display config resolution
                Map<String, String> attrMap = new LinkedHashMap<>();
                String uid, cotType, callsign, remarks;
                if (attrs != null) {
                    Iterator<String> keys = attrs.keys();
                    while (keys.hasNext()) {
                        String k = keys.next();
                        Object v = attrs.opt(k);
                        if (v != null && v != JSONObject.NULL) attrMap.put(k, v.toString());
                    }
                    List<String> uidCandidates      = mapping != null ? mapping.uidFields      : Collections.emptyList();
                    List<String> typeCandidates     = mapping != null ? mapping.typeFields     : Collections.emptyList();
                    List<String> callsignCandidates = mapping != null ? mapping.callsignFields : Collections.emptyList();
                    List<String> remarksCandidates  = mapping != null ? mapping.remarksFields  : Collections.emptyList();

                    uid      = resolveField(attrs, uidCandidates,      "uid");
                    cotType  = resolveField(attrs, typeCandidates,     "cot_type");
                    callsign = resolveField(attrs, callsignCandidates, "tak_callsign");
                    remarks  = resolveField(attrs, remarksCandidates,  "tak_remarks");
                    hae      = attrs.optDouble("hae", Double.NaN);
                    if (uid.isEmpty())      uid      = "FL-" + i + "-" + System.currentTimeMillis();
                    if (cotType.isEmpty())  cotType  = "a-f-G";
                    if (callsign.isEmpty()) callsign = "Feature-" + i;
                } else {
                    uid      = "FL-" + i + "-" + System.currentTimeMillis();
                    cotType  = "a-f-G";
                    callsign = "Feature-" + i;
                    remarks  = "";
                }
                results.add(new DownloadedFeature(uid, cotType, callsign, remarks, lat, lon, hae,
                        attrMap));
            } catch (Exception ex) {
                Log.w(TAG, "Skipping malformed feature at index " + i, ex);
            }
        }
        Log.d(TAG, "downloadLayerAsCoT: " + results.size() + " features from " + serviceUrl);
        return results;
    }

    /** Returns the [x, y] of the first vertex of a polyline or polygon geometry, or null. */
    private static double[] extractFirstVertex(JSONObject geom) {
        JSONArray paths = geom.optJSONArray("paths");
        if (paths != null && paths.length() > 0) {
            JSONArray path = paths.optJSONArray(0);
            if (path != null && path.length() > 0) {
                JSONArray pt = path.optJSONArray(0);
                if (pt != null && pt.length() >= 2)
                    return new double[]{pt.optDouble(0, Double.NaN), pt.optDouble(1, Double.NaN)};
            }
        }
        JSONArray rings = geom.optJSONArray("rings");
        if (rings != null && rings.length() > 0) {
            JSONArray ring = rings.optJSONArray(0);
            if (ring != null && ring.length() > 0) {
                JSONArray pt = ring.optJSONArray(0);
                if (pt != null && pt.length() >= 2)
                    return new double[]{pt.optDouble(0, Double.NaN), pt.optDouble(1, Double.NaN)};
            }
        }
        return null;
    }

    private static String nullToEmpty(String s) { return s == null ? "" : s; }

    /** Immutable value object representing one downloaded ArcGIS feature. */
    public static final class DownloadedFeature {
        public final String uid, cotType, callsign, remarks;
        public final double lat, lon, hae;
        /** All ArcGIS attributes as strings — used by display config sym/lbl/popup resolution. */
        public final Map<String, String> attributes;

        DownloadedFeature(String uid, String cotType, String callsign, String remarks,
                          double lat, double lon, double hae, Map<String, String> attributes) {
            this.uid        = uid;
            this.cotType    = cotType;
            this.callsign   = callsign;
            this.remarks    = remarks;
            this.lat        = lat;
            this.lon        = lon;
            this.hae        = hae;
            this.attributes = attributes != null ? attributes : Collections.emptyMap();
        }
    }

    // -------------------------------------------------------------------------
    // Add features (applyEdits)
    // -------------------------------------------------------------------------

    /**
     * Adds a feature to the target layer using the full CoT-aligned schema.
     *
     * @param uid          CoT UID of the originating event
     * @param cotType      CoT type string (e.g. "a-f-G-U-C")
     * @param callsign     TAK callsign / display name
     * @param iconPath     TAK icon path (may be empty)
     * @param remarks      Free-text remarks (may be empty)
     * @param how          CoT "how" attribute (e.g. "m-g")
     * @param sentByUser   Username of the operator sending the feature
     * @param lat          WGS84 latitude
     * @param lon          WGS84 longitude
     * @param hae          Height above ellipsoid in metres
     * @param ce           Circular error in metres
     * @param le           Linear error in metres
     * @param timeMs       CoT "time" as Unix epoch ms
     * @param startMs      CoT "start" as Unix epoch ms
     * @param staleMs      CoT "stale" as Unix epoch ms
     * @param rawCotXml    Raw CoT XML string (may be empty)
     */
    public void addPliFeature(String serviceUrl, String token,
            String uid, String cotType, String callsign, String iconPath,
            String remarks, String how, String sentByUser,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs,
            String rawCotXml) throws Exception {

        String layerUrl = ensureLayerIndex(serviceUrl);
        long now = System.currentTimeMillis();

        JSONObject sr = new JSONObject();
        sr.put("wkid", 4326);
        JSONObject geometry = new JSONObject();
        geometry.put("x", lon);
        geometry.put("y", lat);
        geometry.put("spatialReference", sr);

        JSONObject attributes = new JSONObject();
        attributes.put("uid",            uid            != null ? uid            : "");
        attributes.put("source_system",  "ATAK");
        attributes.put("source_layer",   "");
        attributes.put("source_objectid","");
        attributes.put("cot_type",       cotType        != null ? cotType        : "");
        attributes.put("tak_callsign",   callsign       != null ? callsign       : "");
        attributes.put("tak_icon",       iconPath       != null ? iconPath       : "");
        attributes.put("tak_remarks",    remarks        != null ? remarks        : "");
        attributes.put("latitude",       safeDouble(lat));
        attributes.put("longitude",      safeDouble(lon));
        attributes.put("hae",            safeDouble(hae));
        attributes.put("ce",             safeDouble(ce));
        attributes.put("le",             safeDouble(le));
        attributes.put("time",           timeMs);
        attributes.put("start_time",     startMs);
        attributes.put("stale_time",     staleMs);
        attributes.put("sent_toFL_time", now);
        attributes.put("sent_by_user",   sentByUser     != null ? sentByUser     : "");
        attributes.put("how",            how            != null ? how            : "");
        attributes.put("sync_status",    "synced");
        attributes.put("last_synced",    now);
        attributes.put("raw_cot_xml",    rawCotXml      != null ? rawCotXml      : "");

        JSONObject feature = new JSONObject();
        feature.put("geometry",   geometry);
        feature.put("attributes", attributes);

        JSONArray adds = new JSONArray();
        adds.put(feature);

        String body = "adds=" + enc(adds.toString())
                + "&f=json"
                + (token != null ? "&token=" + enc(token) : "");

        String response = httpPost(layerUrl + "/applyEdits", body, null);
        if (response != null) {
            Log.d(TAG, "applyEdits response: " + new JSONObject(response).optString("addResults", "ok"));
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /** Returns JSONObject.NULL for NaN/Infinite so JSONObject.put() doesn't throw. */
    private static Object safeDouble(double v) {
        return (Double.isNaN(v) || Double.isInfinite(v)) ? JSONObject.NULL : v;
    }

    // -------------------------------------------------------------------------
    // Create Feature Service from CSV template
    // -------------------------------------------------------------------------

    // CSV schema template — one header row + one sample row so ArcGIS can infer geometry.
    // ISO-8601 dates cause ArcGIS to recognise date columns; publishParameters.layerInfo
    // then locks in the exact esriFieldType for every column.
    private static final String SCHEMA_CSV =
        "uid,source_system,source_layer,source_objectid,cot_type,tak_callsign,tak_icon," +
        "tak_remarks,latitude,longitude,hae,ce,le,time,start_time,stale_time," +
        "sent_toFL_time,sent_by_user,how,sync_status,last_synced,raw_cot_xml\n" +
        "SCHEMA_TEMPLATE,ATAK,,,a-f-G-U-C,EXAMPLE,,Schema row," +
        "34.052235,-117.307899,285.4,9.0,2.0," +
        "2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,2025-01-01T00:00:30Z," +
        "2025-01-01T00:00:00Z,admin,m-g,synced,2025-01-01T00:00:00Z,\n";

    /**
     * Creates a hosted Feature Layer by uploading the schema CSV then publishing it.
     * Editing is explicitly enabled after the publish job completes.
     *
     * @return URL to layer 0 of the new service, or null on failure
     */
    public String createPliFeatureService(String portalUrl, String username, String token,
            String layerName) {
        try {
            String portal = normalizePortalUrl(portalUrl);
            String displayName = (layerName != null && !layerName.trim().isEmpty())
                    ? layerName.trim() : "FeatureLink PLI " + username;

            // Step 1 — upload CSV as a Portal item
            String itemId = uploadCsvItem(portal, username, token, displayName);
            if (itemId == null) { Log.e(TAG, "CSV upload failed"); return null; }
            Log.d(TAG, "CSV item uploaded: " + itemId);

            // Step 2 — publish as hosted Feature Layer with explicit field types
            String[] svcInfo = publishCsvItem(portal, username, token, itemId, displayName);
            if (svcInfo == null) { Log.e(TAG, "CSV publish failed"); return null; }
            String serviceItemId = svcInfo[0];
            String serviceUrl    = svcInfo[1].replaceAll("/+$", "");
            String jobId         = svcInfo[2];

            // Step 3 — wait for the async publish job
            if (jobId != null && !jobId.isEmpty()) {
                if (!waitForPublishJob(portal, username, token, serviceItemId, jobId)) {
                    Log.w(TAG, "Publish job did not complete cleanly — proceeding anyway");
                }
            }

            // Step 4 — ensure editing is on
            enableEditing(serviceUrl, token);

            return serviceUrl + "/0";

        } catch (Exception e) {
            Log.e(TAG, "createPliFeatureService failed", e);
            return null;
        }
    }

    private String uploadCsvItem(String portal, String username, String token,
            String title) throws Exception {
        String endpoint = portal + "/sharing/rest/content/users/" + enc(username) + "/addItem";
        byte[] csvBytes = SCHEMA_CSV.getBytes(StandardCharsets.UTF_8);

        Map<String, String> params = new LinkedHashMap<>();
        params.put("title",       title);
        params.put("type",        "CSV");
        params.put("tags",        "FeatureLink,ATAK,PLI");
        params.put("description", "FeatureLink PLI schema — created by ATAK FeatureLink plugin");
        params.put("f",           "json");
        params.put("token",       token);

        String resp = httpPostMultipart(endpoint, params,
                "featurelink_schema.csv", csvBytes, "text/csv");
        if (resp == null) return null;

        JSONObject json = new JSONObject(resp);
        if (json.has("error")) {
            Log.e(TAG, "addItem error: " + json.getJSONObject("error").optString("message"));
            return null;
        }
        return json.optString("id", null);
    }

    private String[] publishCsvItem(String portal, String username, String token,
            String itemId, String name) throws Exception {

        // Build layerInfo with explicit esriFieldType for every column
        JSONArray fields = new JSONArray();
        fields.put(schemaField("uid",             "esriFieldTypeString",  "UID",             100));
        fields.put(schemaField("source_system",   "esriFieldTypeString",  "Source System",   100));
        fields.put(schemaField("source_layer",    "esriFieldTypeString",  "Source Layer",    255));
        fields.put(schemaField("source_objectid", "esriFieldTypeString",  "Source Object ID", 50));
        fields.put(schemaField("cot_type",        "esriFieldTypeString",  "CoT Type",        100));
        fields.put(schemaField("tak_callsign",    "esriFieldTypeString",  "TAK Callsign",    255));
        fields.put(schemaField("tak_icon",        "esriFieldTypeString",  "TAK Icon",        512));
        fields.put(schemaField("tak_remarks",     "esriFieldTypeString",  "TAK Remarks",    1000));
        fields.put(schemaField("latitude",        "esriFieldTypeDouble",  "Latitude",         -1));
        fields.put(schemaField("longitude",       "esriFieldTypeDouble",  "Longitude",        -1));
        fields.put(schemaField("hae",             "esriFieldTypeDouble",  "HAE (m)",          -1));
        fields.put(schemaField("ce",              "esriFieldTypeDouble",  "CE (m)",           -1));
        fields.put(schemaField("le",              "esriFieldTypeDouble",  "LE (m)",           -1));
        fields.put(schemaField("time",            "esriFieldTypeDate",    "Time",             -1));
        fields.put(schemaField("start_time",      "esriFieldTypeDate",    "Start Time",       -1));
        fields.put(schemaField("stale_time",      "esriFieldTypeDate",    "Stale Time",       -1));
        fields.put(schemaField("sent_toFL_time",  "esriFieldTypeDate",    "Sent to FL Time",  -1));
        fields.put(schemaField("sent_by_user",    "esriFieldTypeString",  "Sent By User",    255));
        fields.put(schemaField("how",             "esriFieldTypeString",  "How",              50));
        fields.put(schemaField("sync_status",     "esriFieldTypeString",  "Sync Status",      50));
        fields.put(schemaField("last_synced",     "esriFieldTypeDate",    "Last Synced",      -1));
        fields.put(schemaField("raw_cot_xml",     "esriFieldTypeString",  "Raw CoT XML",   32000));

        JSONObject layerInfo = new JSONObject();
        layerInfo.put("fields", fields);

        // Safe service name: letters, digits, spaces only (ArcGIS rejects special chars)
        String safeName = name.replaceAll("[^a-zA-Z0-9 _]", "").trim();
        if (safeName.isEmpty()) safeName = "FeatureLink PLI";

        JSONObject publishParams = new JSONObject();
        publishParams.put("name",                 safeName);
        publishParams.put("locationType",         "coordinates");
        publishParams.put("latitudeFieldName",    "latitude");
        publishParams.put("longitudeFieldName",   "longitude");
        publishParams.put("coordinateFieldType",  "LatLong");
        publishParams.put("hasStaticData",        false);
        publishParams.put("maxRecordCount",       2000);
        publishParams.put("capabilities",         "Create,Delete,Query,Update,Editing");
        publishParams.put("layerInfo",            layerInfo);

        String endpoint = portal + "/sharing/rest/content/users/" + enc(username) + "/publish";
        String body = "itemId="            + enc(itemId)
                + "&filetype=csv"
                + "&publishParameters=" + enc(publishParams.toString())
                + "&f=json"
                + "&token="             + enc(token);

        String resp = httpPost(endpoint, body, null);
        if (resp == null) return null;

        JSONObject json = new JSONObject(resp);
        if (json.has("error")) {
            Log.e(TAG, "publish error: " + json.getJSONObject("error").optString("message"));
            return null;
        }

        JSONArray services = json.optJSONArray("services");
        if (services == null || services.length() == 0) {
            Log.e(TAG, "publish returned no services: " + resp);
            return null;
        }

        JSONObject svc = services.getJSONObject(0);
        String serviceItemId = svc.optString("serviceItemId", null);
        String serviceUrl    = svc.optString("serviceurl",
                               svc.optString("encodedServiceURL", null));
        String jobId         = svc.optString("jobId", null);

        if (serviceUrl == null) {
            Log.e(TAG, "publish response missing serviceurl: " + svc);
            return null;
        }
        return new String[]{serviceItemId, serviceUrl, jobId};
    }

    private boolean waitForPublishJob(String portal, String username, String token,
            String serviceItemId, String jobId) {
        if (serviceItemId == null || jobId == null) return true;
        try {
            String statusUrl = portal + "/sharing/rest/content/users/" + enc(username)
                    + "/items/" + enc(serviceItemId)
                    + "/status?jobId=" + enc(jobId) + "&jobType=publish&f=json&token=" + enc(token);
            for (int i = 0; i < 30; i++) {
                Thread.sleep(2_000);
                String resp = httpGet(statusUrl, null);
                if (resp == null) continue;
                String status = new JSONObject(resp).optString("status", "");
                Log.d(TAG, "Publish job status: " + status);
                if ("completed".equals(status)) return true;
                if ("failed".equals(status) || "cancelled".equals(status)) return false;
            }
        } catch (Exception e) {
            Log.w(TAG, "waitForPublishJob error", e);
        }
        return false;
    }

    private void enableEditing(String featureServerUrl, String token) {
        try {
            JSONObject def = new JSONObject();
            def.put("capabilities",         "Create,Delete,Query,Update,Editing");
            def.put("hasStaticData",         false);
            def.put("allowGeometryUpdates",  true);

            String body = "updateDefinition=" + enc(def.toString())
                    + "&f=json&token=" + enc(token);
            String resp = httpPost(featureServerUrl + "/updateDefinition", body, null);
            if (resp != null) {
                JSONObject json = new JSONObject(resp);
                if (json.has("error"))
                    Log.w(TAG, "enableEditing: " + json.getJSONObject("error").optString("message"));
                else
                    Log.d(TAG, "Editing enabled: " + featureServerUrl);
            }
        } catch (Exception e) {
            Log.w(TAG, "enableEditing failed (non-fatal)", e);
        }
    }

    private static JSONObject schemaField(String name, String type, String alias, int length)
            throws Exception {
        JSONObject f = new JSONObject();
        f.put("name",     name);
        f.put("type",     type);
        f.put("alias",    alias);
        f.put("nullable", true);
        if (length > 0) f.put("length", length);
        return f;
    }

    // -------------------------------------------------------------------------
    // HTTP helpers
    // -------------------------------------------------------------------------

    private String httpGet(String urlStr, String token) {
        HttpURLConnection conn = null;
        try {
            URL url = new URL(urlStr);
            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("GET");
            conn.setConnectTimeout(TIMEOUT_MS);
            conn.setReadTimeout(TIMEOUT_MS);
            conn.setRequestProperty("Accept", "application/json");
            return readResponse(conn);
        } catch (Exception e) {
            Log.e(TAG, "GET failed: " + urlStr, e);
            return null;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private String httpPostMultipart(String urlStr, Map<String, String> params,
            String fileName, byte[] fileBytes, String fileContentType) throws Exception {
        String boundary = "FeatureLinkBoundary" + System.currentTimeMillis();
        String CRLF = "\r\n";

        HttpURLConnection conn = (HttpURLConnection) new URL(urlStr).openConnection();
        conn.setRequestMethod("POST");
        conn.setDoOutput(true);
        conn.setConnectTimeout(TIMEOUT_MS);
        conn.setReadTimeout(30_000);
        conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);

        try (OutputStream os = conn.getOutputStream()) {
            for (Map.Entry<String, String> e : params.entrySet()) {
                os.write(("--" + boundary + CRLF
                        + "Content-Disposition: form-data; name=\"" + e.getKey() + "\"" + CRLF
                        + CRLF
                        + e.getValue() + CRLF).getBytes(StandardCharsets.UTF_8));
            }
            if (fileBytes != null) {
                os.write(("--" + boundary + CRLF
                        + "Content-Disposition: form-data; name=\"file\"; filename=\""
                        + fileName + "\"" + CRLF
                        + "Content-Type: " + fileContentType + CRLF
                        + CRLF).getBytes(StandardCharsets.UTF_8));
                os.write(fileBytes);
                os.write(CRLF.getBytes(StandardCharsets.UTF_8));
            }
            os.write(("--" + boundary + "--" + CRLF).getBytes(StandardCharsets.UTF_8));
        }
        return readResponse(conn);
    }

    private String httpPost(String urlStr, String body, String token) {
        HttpURLConnection conn = null;
        try {
            URL url = new URL(urlStr);
            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setConnectTimeout(TIMEOUT_MS);
            conn.setReadTimeout(TIMEOUT_MS);
            conn.setRequestProperty("Content-Type", "application/x-www-form-urlencoded");
            conn.setRequestProperty("Accept", "application/json");
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            conn.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream os = conn.getOutputStream()) {
                os.write(bytes);
            }
            return readResponse(conn);
        } catch (Exception e) {
            Log.e(TAG, "POST failed: " + urlStr, e);
            return null;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private String readResponse(HttpURLConnection conn) throws Exception {
        int code = conn.getResponseCode();
        java.io.InputStream is = (code >= 200 && code < 300)
                ? conn.getInputStream() : conn.getErrorStream();
        if (is == null) return null;
        StringBuilder sb = new StringBuilder();
        try (BufferedReader br = new BufferedReader(
                new InputStreamReader(is, StandardCharsets.UTF_8))) {
            String line;
            while ((line = br.readLine()) != null) sb.append(line);
        }
        return sb.toString();
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private static String enc(String s) throws Exception {
        return URLEncoder.encode(s, "UTF-8");
    }

    private static String normalizePortalUrl(String portalUrl) {
        if (portalUrl == null || portalUrl.trim().isEmpty()) {
            return "https://www.arcgis.com";
        }
        return portalUrl.trim().replaceAll("/+$", "");
    }

    /** Ensures URL points to layer index 0 of a FeatureServer. */
    private static String ensureLayerIndex(String url) {
        if (url == null) return "";
        url = url.trim().replaceAll("/+$", "");
        if (url.matches(".*FeatureServer$") || url.matches(".*FeatureServer/")) {
            return url.replaceAll("/+$", "") + "/0";
        }
        return url;
    }

}
