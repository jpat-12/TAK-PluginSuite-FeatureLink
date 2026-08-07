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
import java.util.Locale;
import java.util.Map;
import java.util.regex.Pattern;

/**
 * Handles all ArcGIS REST API communication using plain HttpURLConnection.
 * All methods are blocking — call from a background thread.
 *
 * <p>Security/correctness invariants established by the audit remediation (C-06/C-21/C-22):
 * <ul>
 *   <li><b>C-21</b> — the access token is <b>never</b> placed in a URL query string. It travels
 *       in an {@code Authorization: Bearer} header, and every logged URL is passed through
 *       {@link #redact(String)} first.</li>
 *   <li><b>C-22</b> — ArcGIS returns {@code {"error":{…}}} bodies with HTTP 200. Every response
 *       parse site goes through the single guard {@link #parseChecked(String)}, which raises a
 *       typed {@link ArcGisException} instead of degrading to "0 features".</li>
 *   <li><b>C-06</b> — feature downloads and portal searches paginate; truncation is reported to
 *       the caller rather than presented as a complete result.</li>
 *   <li><b>C-08</b> — a FeatureServer's sublayers are enumerable via {@link #listSubLayers}; the
 *       hardcoded {@code /0} is only ever a last-resort fallback.</li>
 * </ul>
 */
public class ArcGISRestClient {

    private static final String TAG = "ArcGISRestClient";
    private static final int TIMEOUT_MS = 15_000;

    /** Page size used when the service does not advertise a {@code maxRecordCount}. */
    private static final int DEFAULT_PAGE_SIZE = 1000;
    /** Hard ceiling on features pulled for one layer, so a runaway service cannot OOM the device.
     * Exceeding it sets {@link DownloadResult#truncated}, which the UI must surface. */
    static final int MAX_TOTAL_FEATURES = 50_000;
    /** Defence against a server that ignores {@code resultOffset} and re-serves page 1 forever. */
    private static final int MAX_PAGES = 200;
    /** Portal search page size, and the ceiling on total portal items enumerated. */
    private static final int SEARCH_PAGE_SIZE = 100;
    private static final int MAX_SEARCH_RESULTS = 1000;

    /** Matches a URL whose final path segment is a FeatureServer/MapServer *root* (no layer id). */
    private static final Pattern SERVICE_ROOT =
            Pattern.compile("(?i).*/(?:FeatureServer|MapServer)$");
    /** Matches a URL already addressing a specific sublayer. */
    private static final Pattern LAYER_ADDRESSED =
            Pattern.compile("(?i).*/(?:FeatureServer|MapServer)/\\d+$");
    /** Anything that looks like a credential in a query string. */
    private static final Pattern SECRET_PARAM = Pattern.compile(
            "(?i)([?&](?:token|access_token|refresh_token|code|code_verifier)=)[^&]*");

    // -------------------------------------------------------------------------
    // C-22 — one central ArcGIS response guard
    // -------------------------------------------------------------------------

    /**
     * A typed ArcGIS failure. ArcGIS signals application-level errors with HTTP 200 plus an
     * {@code error} object, so this is thrown from the parse guard rather than inferred from the
     * transport status. {@link #code} is Esri's own error code (498/499 = invalid/missing token,
     * 403 = not authorised, -1 = transport or unparseable body).
     */
    public static class ArcGisException extends Exception {
        public final int code;

        public ArcGisException(int code, String message) {
            super(message);
            this.code = code;
        }

        /** True for the two codes that mean "your session is no longer valid" — the UI must say
         * "sign in again" rather than render an empty layer. */
        public boolean isAuthFailure() {
            return code == 498 || code == 499 || code == 403;
        }
    }

    /**
     * C-22: the single parse site for every ArcGIS response body in this class. Rejects a null
     * body (transport failure) and any {@code {"error":{…}}} payload with a typed exception.
     */
    private static JSONObject parseChecked(String body) throws Exception {
        if (body == null) throw new ArcGisException(-1, "No response from server");
        JSONObject json;
        try {
            json = new JSONObject(body);
        } catch (Exception e) {
            throw new ArcGisException(-1, "Server returned a non-JSON response");
        }
        checkArcGisError(json);
        return json;
    }

    /** Raises a typed exception if {@code json} carries an ArcGIS {@code error}. Public so
     * callers that already hold a parsed object (e.g. {@link AutoIconset}) use the same guard. */
    public static void checkArcGisError(JSONObject json) throws ArcGisException {
        if (json == null || !json.has("error")) return;
        JSONObject err = json.optJSONObject("error");
        if (err == null) throw new ArcGisException(-1, json.optString("error", "ArcGIS request failed"));
        int code = err.optInt("code", -1);
        StringBuilder msg = new StringBuilder(err.optString("message", "ArcGIS request failed"));
        JSONArray details = err.optJSONArray("details");
        if (details != null && details.length() > 0) {
            String d = details.optString(0, "");
            if (!d.isEmpty() && !msg.toString().contains(d)) msg.append(": ").append(d);
        }
        throw new ArcGisException(code, msg.toString());
    }

    /** C-21: strips credential-valued query parameters before any URL reaches a log sink. */
    static String redact(String url) {
        if (url == null) return "";
        return SECRET_PARAM.matcher(url).replaceAll("$1<redacted>");
    }

    // -------------------------------------------------------------------------
    // Layer search
    // -------------------------------------------------------------------------

    /**
     * Searches the portal for Feature Services owned by the authenticated user only.
     * Filters by owner so unrelated public services are excluded. Paginated (C-06) — an account
     * with more than one page of Feature Services previously showed only the first 100.
     */
    public List<ArcGISLayer> searchUserLayers(String portalUrl, String token, String username) {
        List<ArcGISLayer> layers = new ArrayList<>();
        try {
            // owner: filter ensures we only see this user's content. The username is quoted and
            // internally escaped so a name containing a space or an AND/OR/NOT token cannot
            // change the meaning of the search DSL expression.
            String q = "type:\"Feature Service\" AND owner:" + quoteSearchTerm(username);
            for (JSONObject item : searchAllPages(portalUrl, token, q)) {
                String name = item.optString("title", "Unnamed");
                String url = item.optString("url", "");
                if (url.isEmpty()) continue;
                ArcGISLayer layer = new ArcGISLayer(name, url, "private");
                // Portal item "access": "public" (shared to Everyone), "org", or "private" —
                // used to decide which on-device section this layer lands in once downloaded.
                layer.access = item.optString("access", "private");
                layer.itemId = item.optString("id", "");
                layers.add(layer);
            }
        } catch (Exception e) {
            Log.e(TAG, "searchUserLayers failed", e);
        }
        return layers;
    }

    /**
     * Returns the group ids the given user belongs to, via the ArcGIS "community/users" self
     * endpoint. Used by {@link #searchSharedWithMeLayers} to enumerate which groups to search for
     * items shared into. Fail-soft: returns an empty list on any error.
     */
    private List<String> fetchUserGroupIds(String portalUrl, String token, String username) {
        List<String> ids = new ArrayList<>();
        try {
            String endpoint = normalizePortalUrl(portalUrl)
                    + "/sharing/rest/community/users/" + enc(username) + "?f=json";
            JSONObject json = parseChecked(httpGet(endpoint, token));
            JSONArray groups = json.optJSONArray("groups");
            if (groups == null) return ids;
            for (int i = 0; i < groups.length(); i++) {
                JSONObject g = groups.optJSONObject(i);
                if (g == null) continue;
                String id = g.optString("id", "");
                if (!id.isEmpty()) ids.add(id);
            }
        } catch (Exception e) {
            Log.e(TAG, "fetchUserGroupIds failed", e);
        }
        return ids;
    }

    /**
     * Searches for Feature Services shared with the authenticated user through group
     * membership (ArcGIS has no single "shared with me" endpoint — this is the standard
     * approach: enumerate the user's groups, then search each group's shared content).
     * Excludes items the user owns themselves (already covered by {@link #searchUserLayers}) and
     * dedupes items shared into more than one of the user's groups.
     */
    public List<ArcGISLayer> searchSharedWithMeLayers(String portalUrl, String token, String username) {
        Map<String, ArcGISLayer> byId = new LinkedHashMap<>();
        try {
            String portal = normalizePortalUrl(portalUrl);
            List<String> groupIds = fetchUserGroupIds(portal, token, username);
            for (String groupId : groupIds) {
                String q = "group:" + quoteSearchTerm(groupId) + " AND type:\"Feature Service\"";
                for (JSONObject item : searchAllPages(portal, token, q)) {
                    String owner = item.optString("owner", "");
                    if (owner.equalsIgnoreCase(username)) continue; // already in "My ArcGIS Layers"
                    String url = item.optString("url", "");
                    if (url.isEmpty()) continue;
                    String id = item.optString("id", url);
                    if (byId.containsKey(id)) continue;

                    String name = item.optString("title", "Unnamed");
                    ArcGISLayer layer = new ArcGISLayer(name, url, "private");
                    layer.access = item.optString("access", "org");
                    layer.sharedBy = owner;
                    layer.itemId = item.optString("id", "");
                    byId.put(id, layer);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "searchSharedWithMeLayers failed", e);
        }
        return new ArrayList<>(byId.values());
    }

    /**
     * C-06: pages the portal {@code /sharing/rest/search} endpoint via {@code start}/{@code num}
     * until {@code nextStart} is exhausted (ArcGIS reports {@code -1} for "no more"), capped at
     * {@link #MAX_SEARCH_RESULTS}.
     */
    private List<JSONObject> searchAllPages(String portalUrl, String token, String q)
            throws Exception {
        List<JSONObject> out = new ArrayList<>();
        String portal = normalizePortalUrl(portalUrl);
        int start = 1;
        while (start > 0 && out.size() < MAX_SEARCH_RESULTS) {
            String endpoint = portal + "/sharing/rest/search?q=" + enc(q)
                    + "&num=" + SEARCH_PAGE_SIZE + "&start=" + start + "&f=json";
            JSONObject json = parseChecked(httpGet(endpoint, token));
            JSONArray results = json.optJSONArray("results");
            if (results == null || results.length() == 0) break;
            for (int i = 0; i < results.length(); i++) {
                JSONObject item = results.optJSONObject(i);
                if (item != null) out.add(item);
            }
            int next = json.optInt("nextStart", -1);
            if (next <= start) break;      // -1 (done) or a non-advancing server
            start = next;
        }
        return out;
    }

    /** Quotes and escapes a value being interpolated into the ArcGIS search-query DSL. */
    static String quoteSearchTerm(String raw) {
        String v = raw == null ? "" : raw.replace("\\", "\\\\").replace("\"", "\\\"");
        return "\"" + v + "\"";
    }

    // -------------------------------------------------------------------------
    // C-08 — sublayer enumeration
    // -------------------------------------------------------------------------

    /** One addressable sublayer of a FeatureServer/MapServer. */
    public static final class SubLayerRef {
        public final int id;
        public final String name;
        public final String geometryType;
        /** Fully-qualified URL for this sublayer — {@code <serviceRoot>/<id>}. This, not the
         * service root, is what an {@link ArcGISLayer} is keyed by, so every existing
         * URL-keyed structure (layerItems, layerDisplayConfigs, dedupe checks) keeps working. */
        public final String url;

        SubLayerRef(int id, String name, String geometryType, String url) {
            this.id = id;
            this.name = name;
            this.geometryType = geometryType;
            this.url = url;
        }
    }

    /**
     * C-08: enumerates the sublayers of a FeatureServer so a multi-layer service becomes N
     * selectable rows instead of collapsing to layer 0.
     *
     * <p>If {@code serviceUrl} already addresses a specific sublayer ({@code …/FeatureServer/3})
     * this returns exactly that one. If the service root advertises no {@code layers} array
     * (or the request fails), an empty list is returned and the caller should fall back to the
     * legacy {@code /0} behaviour — never silently drop the layer.
     */
    public List<SubLayerRef> listSubLayers(String serviceUrl, String token) {
        List<SubLayerRef> out = new ArrayList<>();
        if (serviceUrl == null) return out;
        String url = serviceUrl.trim().replaceAll("/+$", "");
        if (url.isEmpty()) return out;

        if (LAYER_ADDRESSED.matcher(url).matches()) {
            int id = 0;
            try {
                id = Integer.parseInt(url.substring(url.lastIndexOf('/') + 1));
            } catch (NumberFormatException ignored) { /* keep 0 */ }
            String name = "";
            String geom = "";
            try {
                JSONObject meta = parseChecked(httpGet(url + "?f=json", token));
                name = meta.optString("name", "");
                geom = meta.optString("geometryType", "");
            } catch (Exception e) {
                Log.w(TAG, "listSubLayers: metadata fetch failed for " + redact(url), e);
            }
            out.add(new SubLayerRef(id, name, geom, url));
            return out;
        }

        if (!SERVICE_ROOT.matcher(url).matches()) return out;

        try {
            JSONObject root = parseChecked(httpGet(url + "?f=json", token));
            JSONArray layers = root.optJSONArray("layers");
            if (layers == null) return out;
            for (int i = 0; i < layers.length(); i++) {
                JSONObject l = layers.optJSONObject(i);
                if (l == null) continue;
                // A group layer has no geometry and cannot be queried; skip it rather than
                // offering the operator a row that can never download.
                if (l.has("subLayerIds") && !l.isNull("subLayerIds")) continue;
                int id = l.optInt("id", -1);
                if (id < 0) continue;
                out.add(new SubLayerRef(id,
                        l.optString("name", "Layer " + id),
                        l.optString("geometryType", ""),
                        url + "/" + id));
            }
        } catch (Exception e) {
            Log.w(TAG, "listSubLayers failed for " + redact(url), e);
        }
        return out;
    }

    // -------------------------------------------------------------------------
    // Feature count
    // -------------------------------------------------------------------------

    /**
     * Returns the total feature count of a layer. Throws {@link ArcGisException} rather than
     * returning 0 when ArcGIS reports an error (C-22) — a failed count must never be
     * indistinguishable from an empty layer.
     */
    public long queryFeatureCount(String serviceUrl, String token) throws Exception {
        String layerUrl = ensureLayerIndex(serviceUrl);
        String params = "where=" + enc("1=1") + "&returnCountOnly=true&f=json";
        JSONObject json = parseChecked(httpGet(layerUrl + "/query?" + params, token));
        return json.optLong("count", 0);
    }

    // -------------------------------------------------------------------------
    // Layer info
    // -------------------------------------------------------------------------

    /**
     * Fetches a URL's raw JSON response with {@code f=json} appended (the token, when given,
     * travels as an Authorization header — C-21). Used by {@link AutoIconset} to read a layer's
     * {@code drawingInfo.renderer}. Blocking — call from a background thread. Throws
     * {@link ArcGisException} for both transport failures and ArcGIS error bodies (C-22).
     */
    /**
     * Fetches the renderer an operator configured on the portal item's <b>Visualization</b> tab.
     *
     * <p>ArcGIS stores this as an item-level override at
     * {@code /sharing/rest/content/items/{itemId}/data}, under
     * {@code layers[].layerDefinition.drawingInfo.renderer}. It is a <b>different document</b> from
     * the service's own {@code {serviceUrl}/{layerId}?f=json}, and saving it does not modify the
     * service. A layer published with one default symbol and then styled by unique value in the
     * web UI therefore still reports a {@code simple} renderer at the service endpoint, which is
     * why such layers rendered as a single repeated marker.
     *
     * @param layerId sublayer index to match within {@code layers[]}; {@code -1} accepts the first.
     * @return the override renderer, or {@code null} if the item has no data, no layer override, or
     *         cannot be read. Callers fall back to the service renderer.
     */
    public JSONObject fetchItemRenderer(String portalUrl, String itemId, int layerId, String token) {
        if (itemId == null || itemId.isEmpty()) return null;
        String base = portalUrl == null || portalUrl.isEmpty() ? "https://www.arcgis.com" : portalUrl;
        String url = base.replaceAll("/+$", "") + "/sharing/rest/content/items/"
                + itemId + "/data?f=json";
        try {
            JSONObject data = fetchJson(url, token);
            if (data == null) return null;
            JSONArray layers = data.optJSONArray("layers");
            if (layers == null || layers.length() == 0) return null;
            for (int i = 0; i < layers.length(); i++) {
                JSONObject l = layers.optJSONObject(i);
                if (l == null) continue;
                if (layerId >= 0 && l.has("id") && l.optInt("id", -1) != layerId) continue;
                JSONObject def = l.optJSONObject("layerDefinition");
                JSONObject di = def != null ? def.optJSONObject("drawingInfo") : null;
                JSONObject renderer = di != null ? di.optJSONObject("renderer") : null;
                if (renderer != null) return renderer;
            }
        } catch (Exception e) {
            // An item with no /data (or no read access to it) is entirely normal — the caller
            // simply uses the service renderer. Debug, not warn.
            Log.d(TAG, "no item-level renderer for item " + itemId + ": " + e.getMessage());
        }
        return null;
    }

    public JSONObject fetchJson(String url, String token) throws Exception {
        if (url == null || url.isEmpty()) return null;
        return parseChecked(httpGet(appendQuery(url, "f=json"), token));
    }

    /**
     * Fetches basic metadata about a Feature Service layer. Returns null only when the layer
     * genuinely cannot be described; the typed reason is available via
     * {@link #fetchLayerInfoChecked} for callers that need to tell the operator *why*.
     */
    public ArcGISLayer fetchLayerInfo(String serviceUrl) {
        return fetchLayerInfo(serviceUrl, null);
    }

    public ArcGISLayer fetchLayerInfo(String serviceUrl, String token) {
        try {
            return fetchLayerInfoChecked(serviceUrl, token);
        } catch (Exception e) {
            Log.e(TAG, "fetchLayerInfo failed for " + redact(serviceUrl), e);
            return null;
        }
    }

    /** As {@link #fetchLayerInfo}, but propagates the typed failure so the UI can distinguish
     * "bad URL" from "expired session" from "no network" (Appendix A §8). */
    public ArcGISLayer fetchLayerInfoChecked(String serviceUrl, String token) throws Exception {
        String url = ensureLayerIndex(serviceUrl);
        JSONObject json = parseChecked(httpGet(url + "?f=json", token));
        String name = json.optString("name", json.optString("serviceDescription", "Unknown Layer"));
        ArcGISLayer layer = new ArcGISLayer(name, serviceUrl, "public");
        layer.geometryType = json.optString("geometryType", "");
        layer.maxRecordCount = json.optInt("maxRecordCount", 0);
        return layer;
    }

    /** The service's own {@code maxRecordCount}, or {@link #DEFAULT_PAGE_SIZE} when it is not
     * advertised. This is the exact value the pagination loop must use as its page size. */
    private int fetchMaxRecordCount(String layerUrl, String token) {
        try {
            JSONObject meta = parseChecked(httpGet(layerUrl + "?f=json", token));
            int n = meta.optInt("maxRecordCount", 0);
            if (n > 0) return Math.min(n, MAX_TOTAL_FEATURES);
        } catch (Exception e) {
            Log.w(TAG, "maxRecordCount lookup failed for " + redact(layerUrl) + " — using default", e);
        }
        return DEFAULT_PAGE_SIZE;
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

    /**
     * Outcome of a paginated feature download (C-06). {@link #truncated} is the field the UI
     * must render — a truncated download reported as a complete one is the single most dangerous
     * failure mode this class has.
     */
    public static final class DownloadResult {
        public final List<DownloadedFeature> features;
        /** True when the service still had more features than we were willing to pull. */
        public final boolean truncated;
        /** Features present in the response but rejected (malformed geometry, out-of-range
         * coordinates). Reported so "downloaded 0 features" is never silently wrong. */
        public final int skipped;
        /** The service's advertised page size, for display in the layer detail UI. */
        public final int maxRecordCount;

        DownloadResult(List<DownloadedFeature> features, boolean truncated, int skipped,
                int maxRecordCount) {
            this.features = features;
            this.truncated = truncated;
            this.skipped = skipped;
            this.maxRecordCount = maxRecordCount;
        }

        public int size() { return features.size(); }
    }

    /** Tries each candidate column in order against this feature's attributes; returns the
     * first non-empty value found, or "" if none of them (or the fallback) have one. Uses an
     * explicit null check rather than {@code optString(key, null)}, which returns the literal
     * four-character string "null" for a JSON null (Appendix A §9). */
    private static String resolveField(JSONObject attrs, List<String> candidates, String fallbackField) {
        for (String field : candidates) {
            String v = optStringOrEmpty(attrs, field);
            if (!v.isEmpty()) return v;
        }
        return optStringOrEmpty(attrs, fallbackField);
    }

    private static String optStringOrEmpty(JSONObject o, String key) {
        if (key == null || !o.has(key) || o.isNull(key)) return "";
        return o.optString(key, "");
    }

    /**
     * Downloads all features from a layer, paginating until the service reports no more
     * (C-06), and returns them ready for direct injection into the ATAK map.
     *
     * @param serviceUrl Feature Service URL (with or without a sublayer index)
     * @param token      ArcGIS token, or null for public layers
     * @param mapping    field mapping to use, or null for the hardcoded defaults
     */
    public DownloadResult downloadLayerFeatures(String serviceUrl, String token,
            CotFieldMapping mapping) throws Exception {
        List<DownloadedFeature> results = new ArrayList<>();
        String layerUrl = ensureLayerIndex(serviceUrl);
        int pageSize = fetchMaxRecordCount(layerUrl, token);

        int offset = 0;
        int skipped = 0;
        boolean truncated = false;
        int pages = 0;

        while (true) {
            String params = "where=" + enc("1=1") + "&outFields=*&outSR=4326&f=json"
                    + "&resultOffset=" + offset
                    + "&resultRecordCount=" + pageSize
                    + "&returnExceededLimitFeatures=true";
            JSONObject json = parseChecked(httpGet(layerUrl + "/query?" + params, token));

            JSONArray features = json.optJSONArray("features");
            int n = features == null ? 0 : features.length();
            for (int i = 0; i < n; i++) {
                try {
                    DownloadedFeature f = parseFeature(features.getJSONObject(i), offset + i, mapping);
                    if (f == null) skipped++;
                    else results.add(f);
                } catch (Exception ex) {
                    skipped++;
                    Log.w(TAG, "Skipping malformed feature at index " + (offset + i), ex);
                }
            }

            pages++;
            offset += n;

            if (results.size() >= MAX_TOTAL_FEATURES) { truncated = true; break; }
            if (pages >= MAX_PAGES) { truncated = true; break; }
            if (n == 0) break;

            // ArcGIS sets exceededTransferLimit (top level on ArcGIS Server, under "properties"
            // on some hosted services) when more records remain. Some services omit it entirely
            // and simply return a full page, so a full page is also treated as "keep going".
            JSONObject props = json.optJSONObject("properties");
            boolean exceeded = json.optBoolean("exceededTransferLimit", false)
                    || (props != null && props.optBoolean("exceededTransferLimit", false));
            if (!exceeded && n < pageSize) break;
        }

        Log.d(TAG, "downloadLayerFeatures: " + results.size() + " features"
                + (truncated ? " (TRUNCATED)" : "") + ", " + skipped + " skipped, from "
                + redact(serviceUrl));
        return new DownloadResult(results, truncated, skipped, pageSize);
    }

    /**
     * Legacy list-returning form, kept for callers that do not surface truncation. New code
     * should use {@link #downloadLayerFeatures} so the truncation flag is not discarded.
     */
    public List<DownloadedFeature> downloadLayerAsCoT(String serviceUrl, String token,
            CotFieldMapping mapping) throws Exception {
        return downloadLayerFeatures(serviceUrl, token, mapping).features;
    }

    /** Parses one ArcGIS feature. Returns null when the feature must be skipped. */
    private static DownloadedFeature parseFeature(JSONObject feat, int index,
            CotFieldMapping mapping) throws Exception {
        JSONObject attrs = feat.optJSONObject("attributes");
        JSONObject geom  = feat.optJSONObject("geometry");
        if (geom == null) return null;

        double lat = Double.NaN, lon = Double.NaN, hae = Double.NaN;
        List<List<double[]>> paths = extractParts(geom.optJSONArray("paths"));
        List<List<double[]>> rings = extractParts(geom.optJSONArray("rings"));
        if (geom.has("x") && geom.has("y")) {
            lon = geom.optDouble("x", Double.NaN);
            lat = geom.optDouble("y", Double.NaN);
        } else {
            double[] v = extractFirstVertex(geom);
            if (v != null) { lon = v[0]; lat = v[1]; }
        }
        if (Double.isNaN(lat) || Double.isNaN(lon)) return null;
        // outSR=4326 was requested, but a service that silently ignores it returns Web Mercator
        // metres. Range-validating here keeps markers off nonsense coordinates rather than
        // handing ATAK a longitude of -13,000,000 (Appendix A §9).
        if (lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0) {
            Log.w(TAG, "Skipping feature with out-of-range WGS84 coordinate: " + lat + "," + lon);
            return null;
        }

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
            if (uid.isEmpty())      uid      = syntheticUid(attrs, index);
            if (cotType.isEmpty())  cotType  = "a-f-G";
            if (callsign.isEmpty()) callsign = "Feature-" + index;
        } else {
            uid      = "FL-" + index;
            cotType  = "a-f-G";
            callsign = "Feature-" + index;
            remarks  = "";
        }
        return new DownloadedFeature(uid, cotType, callsign, remarks, lat, lon, hae,
                attrMap, paths, rings);
    }

    /**
     * A stable synthetic UID for a feature with no uid column. Derived from the feature's
     * OBJECTID where present, so a re-download updates markers in place instead of churning the
     * map with a fresh {@code System.currentTimeMillis()} identity every time (Appendix A §9).
     */
    private static String syntheticUid(JSONObject attrs, int index) {
        Iterator<String> keys = attrs.keys();
        while (keys.hasNext()) {
            String k = keys.next();
            if ("objectid".equalsIgnoreCase(k) || "fid".equalsIgnoreCase(k)
                    || "globalid".equalsIgnoreCase(k)) {
                String v = optStringOrEmpty(attrs, k);
                if (!v.isEmpty()) return "FL-" + v;
            }
        }
        return "FL-idx-" + index;
    }

    /** Parses a "paths" or "rings" JSON array (array of parts, each an array of [x,y,...]
     * vertices) into a list of [lon,lat] vertex lists, one per part. Null/empty input → empty
     * list (point geometry, or the other of paths/rings for this feature's geometry type). */
    private static List<List<double[]>> extractParts(JSONArray parts) {
        if (parts == null || parts.length() == 0) return Collections.emptyList();
        List<List<double[]>> result = new ArrayList<>(parts.length());
        for (int i = 0; i < parts.length(); i++) {
            JSONArray part = parts.optJSONArray(i);
            if (part == null) continue;
            List<double[]> vertices = new ArrayList<>(part.length());
            for (int j = 0; j < part.length(); j++) {
                JSONArray pt = part.optJSONArray(j);
                if (pt == null || pt.length() < 2) continue;
                vertices.add(new double[]{pt.optDouble(0, Double.NaN), pt.optDouble(1, Double.NaN)});
            }
            if (!vertices.isEmpty()) result.add(vertices);
        }
        return result;
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

    /** Immutable value object representing one downloaded ArcGIS feature. */
    public static final class DownloadedFeature {
        public final String uid, cotType, callsign, remarks;
        public final double lat, lon, hae;
        /** All ArcGIS attributes as strings — used by display config sym/lbl/popup resolution. */
        public final Map<String, String> attributes;
        /** Full polyline geometry: one inner list per part, each a [lon,lat] vertex in order.
         * Empty for point/polygon features. */
        public final List<List<double[]>> paths;
        /** Full polygon geometry: one inner list per ring (first ring is the outer boundary,
         * subsequent rings are holes per Esri's ring-orientation convention), each a [lon,lat]
         * vertex in order. Empty for point/polyline features. */
        public final List<List<double[]>> rings;

        DownloadedFeature(String uid, String cotType, String callsign, String remarks,
                          double lat, double lon, double hae, Map<String, String> attributes,
                          List<List<double[]>> paths, List<List<double[]>> rings) {
            this.uid        = uid;
            this.cotType    = cotType;
            this.callsign   = callsign;
            this.remarks    = remarks;
            this.lat        = lat;
            this.lon        = lon;
            this.hae        = hae;
            this.attributes = attributes != null ? attributes : Collections.emptyMap();
            this.paths      = paths != null ? paths : Collections.emptyList();
            this.rings      = rings != null ? rings : Collections.emptyList();
        }
    }

    // -------------------------------------------------------------------------
    // Add features (applyEdits)
    // -------------------------------------------------------------------------

    /**
     * PLI feature attributes, aligned to the CoT + team schema every consumer of this "TEST_NC"-
     * style PLI layer already expects (group_name/group_role match ATAK's own CoT &lt;__group
     * name="..." role="..."/&gt; detail — see MapView.getSelfMarker().getMetaString("team", ...)
     * / .getMetaString("atakRoleType", ...)).
     */
    private static JSONObject buildPliAttributes(
            String uid, String cotType, String callsign, String iconPath,
            String remarks, String how, String sentByUser,
            String groupName, String groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs,
            String rawCotXml) throws Exception {
        long now = System.currentTimeMillis();

        JSONObject attributes = new JSONObject();
        attributes.put("uid",            uid            != null ? uid            : "");
        attributes.put("source_system",  "ATAK");
        attributes.put("source_layer",   "");
        attributes.put("source_objectid","");
        attributes.put("cot_type",       cotType        != null ? cotType        : "");
        attributes.put("tak_callsign",   callsign       != null ? callsign       : "");
        attributes.put("tak_icon",       iconPath       != null ? iconPath       : "");
        attributes.put("tak_remarks",    remarks        != null ? remarks        : "");
        attributes.put("group_name",     groupName      != null ? groupName      : "");
        attributes.put("group_role",     groupRole      != null ? groupRole      : "");
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
        return attributes;
    }

    private static JSONObject buildPliGeometry(double lat, double lon) throws Exception {
        JSONObject sr = new JSONObject();
        sr.put("wkid", 4326);
        JSONObject geometry = new JSONObject();
        geometry.put("x", lon);
        geometry.put("y", lat);
        geometry.put("spatialReference", sr);
        return geometry;
    }

    /**
     * Adds a new PLI feature to the target layer. Returns the new feature's objectId (from
     * applyEdits' addResults), or -1 if the server didn't report one — callers should store
     * this and switch to {@link #updatePliFeature} for every subsequent send, rather than
     * calling this again and creating a duplicate row per send.
     */
    public long addPliFeature(String serviceUrl, String token,
            String uid, String cotType, String callsign, String iconPath,
            String remarks, String how, String sentByUser,
            String groupName, String groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs,
            String rawCotXml) throws Exception {

        String layerUrl = ensureLayerIndex(serviceUrl);

        JSONObject feature = new JSONObject();
        feature.put("geometry", buildPliGeometry(lat, lon));
        feature.put("attributes", buildPliAttributes(uid, cotType, callsign, iconPath, remarks,
                how, sentByUser, groupName, groupRole, lat, lon, hae, ce, le,
                timeMs, startMs, staleMs, rawCotXml));

        JSONArray adds = new JSONArray();
        adds.put(feature);

        String body = "adds=" + enc(adds.toString()) + "&f=json";

        JSONObject json = parseChecked(httpPost(layerUrl + "/applyEdits", body, token));
        JSONArray addResults = json.optJSONArray("addResults");
        if (addResults == null || addResults.length() == 0) return -1;
        JSONObject result = addResults.getJSONObject(0);
        if (!result.optBoolean("success", false)) {
            JSONObject err = result.optJSONObject("error");
            Log.w(TAG, "applyEdits (add) rejected: "
                    + (err != null ? err.optString("description", err.optString("message", "")) : ""));
            return -1;
        }
        return result.optLong("objectId", -1);
    }

    /**
     * Updates an existing PLI feature (by objectId, from a prior {@link #addPliFeature} call)
     * in place instead of adding a new row every send. Returns false if the server reports the
     * update didn't succeed (e.g. the feature no longer exists) — callers should treat that as
     * "start over": clear the remembered objectId and add a fresh feature next time.
     */
    public boolean updatePliFeature(String serviceUrl, String token, long objectId,
            String uid, String cotType, String callsign, String iconPath,
            String remarks, String how, String sentByUser,
            String groupName, String groupRole,
            double lat, double lon, double hae, double ce, double le,
            long timeMs, long startMs, long staleMs,
            String rawCotXml) throws Exception {

        String layerUrl = ensureLayerIndex(serviceUrl);
        String objectIdField = objectIdFieldFor(layerUrl, token);

        JSONObject attributes = buildPliAttributes(uid, cotType, callsign, iconPath, remarks,
                how, sentByUser, groupName, groupRole, lat, lon, hae, ce, le,
                timeMs, startMs, staleMs, rawCotXml);
        attributes.put(objectIdField, objectId);

        JSONObject feature = new JSONObject();
        feature.put("geometry", buildPliGeometry(lat, lon));
        feature.put("attributes", attributes);

        JSONArray updates = new JSONArray();
        updates.put(feature);

        String body = "updates=" + enc(updates.toString()) + "&f=json";

        JSONObject json = parseChecked(httpPost(layerUrl + "/applyEdits", body, token));
        JSONArray updateResults = json.optJSONArray("updateResults");
        if (updateResults == null || updateResults.length() == 0) return false;
        return updateResults.getJSONObject(0).optBoolean("success", false);
    }

    /**
     * Layer's actual ObjectID field name (usually "OBJECTID", but not guaranteed) — needed to
     * key an applyEdits "updates" entry correctly.
     *
     * <p>Cached per layer URL for the lifetime of this client. The previous code re-fetched the
     * full layer metadata on <em>every</em> PLI send (every 30 s, forever), doubling request
     * volume and adding a round trip of latency to every position update on exactly the kind of
     * constrained link where that matters most (Appendix A §10).
     */
    private final Map<String, String> objectIdFieldCache =
            Collections.synchronizedMap(new LinkedHashMap<String, String>());

    private String objectIdFieldFor(String layerUrl, String token) {
        String cached = objectIdFieldCache.get(layerUrl);
        if (cached != null) return cached;
        String field = "OBJECTID";
        try {
            JSONObject meta = parseChecked(httpGet(layerUrl + "?f=json", token));
            field = meta.optString("objectIdField", "OBJECTID");
            if (field.isEmpty()) field = "OBJECTID";
        } catch (Exception e) {
            Log.w(TAG, "objectIdField lookup failed for " + redact(layerUrl) + " — assuming OBJECTID");
            return field;   // do not cache a guess made after a failure
        }
        objectIdFieldCache.put(layerUrl, field);
        return field;
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
    static final String SCHEMA_CSV =
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
                    Log.e(TAG, "Publish job did not complete — not returning a service URL that "
                            + "may not exist");
                    return null;
                }
            }

            // Step 4 — ensure editing is on
            enableEditing(serviceUrl, token);

            // The published layer is not guaranteed to be index 0 — read the real id from the
            // service root rather than hardcoding "/0" (Appendix A §9).
            List<SubLayerRef> subs = listSubLayers(serviceUrl, token);
            if (!subs.isEmpty()) return subs.get(0).url;
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

        String resp = httpPostMultipart(endpoint, params,
                "featurelink_schema.csv", csvBytes, "text/csv", token);
        JSONObject json = parseChecked(resp);
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

        JSONObject publishParams = new JSONObject();
        publishParams.put("name",                 safeServiceName(name, username));
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
                + "&f=json";

        JSONObject json = parseChecked(httpPost(endpoint, body, token));

        JSONArray services = json.optJSONArray("services");
        if (services == null || services.length() == 0) {
            Log.e(TAG, "publish returned no services");
            return null;
        }

        JSONObject svc = services.getJSONObject(0);
        String serviceItemId = svc.optString("serviceItemId", null);
        String serviceUrl    = svc.optString("serviceurl",
                               svc.optString("encodedServiceURL", null));
        String jobId         = svc.optString("jobId", null);

        if (serviceUrl == null) {
            Log.e(TAG, "publish response missing serviceurl");
            return null;
        }
        return new String[]{serviceItemId, serviceUrl, jobId};
    }

    /**
     * ArcGIS rejects special characters in a service name. A name that sanitises to nothing (a
     * fully non-Latin callsign) previously collapsed to the literal "FeatureLink PLI" for every
     * such operator; a short discriminator derived from the original keeps them distinct
     * (Appendix A §9).
     */
    static String safeServiceName(String name, String username) {
        String safe = (name == null ? "" : name).replaceAll("[^a-zA-Z0-9 _]", "").trim();
        if (!safe.isEmpty()) return safe;
        String userSafe = (username == null ? "" : username).replaceAll("[^a-zA-Z0-9 _]", "").trim();
        String discriminator = Integer.toHexString(
                (name == null ? "" : name).hashCode() & 0xFFFFFF);
        return ("FeatureLink PLI " + userSafe + " " + discriminator).replaceAll("\\s+", " ").trim();
    }

    private boolean waitForPublishJob(String portal, String username, String token,
            String serviceItemId, String jobId) {
        if (serviceItemId == null || jobId == null) return true;
        try {
            String statusUrl = portal + "/sharing/rest/content/users/" + enc(username)
                    + "/items/" + enc(serviceItemId)
                    + "/status?jobId=" + enc(jobId) + "&jobType=publish&f=json";
            for (int i = 0; i < 30; i++) {
                Thread.sleep(2_000);
                String status;
                try {
                    status = parseChecked(httpGet(statusUrl, token)).optString("status", "");
                } catch (Exception e) {
                    continue;
                }
                Log.d(TAG, "Publish job status: " + status);
                if ("completed".equals(status)) return true;
                if ("failed".equals(status) || "cancelled".equals(status)) return false;
            }
        } catch (InterruptedException ie) {
            Thread.currentThread().interrupt();
            return false;
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

            String body = "updateDefinition=" + enc(def.toString()) + "&f=json";
            parseChecked(httpPost(featureServerUrl + "/updateDefinition", body, token));
            Log.d(TAG, "Editing enabled: " + redact(featureServerUrl));
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
    // HTTP helpers — C-21: the token is a header, never a query parameter
    // -------------------------------------------------------------------------

    private static void applyAuth(HttpURLConnection conn, String token) {
        if (token != null && !token.isEmpty()) {
            conn.setRequestProperty("Authorization", "Bearer " + token);
            // Esri honours the header, but some proxies strip it; X-Esri-Authorization is the
            // documented alternate header and is equally never logged.
            conn.setRequestProperty("X-Esri-Authorization", "Bearer " + token);
        }
    }

    private String httpGet(String urlStr, String token) {
        HttpURLConnection conn = null;
        try {
            URL url = new URL(urlStr);
            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("GET");
            conn.setConnectTimeout(TIMEOUT_MS);
            conn.setReadTimeout(TIMEOUT_MS);
            conn.setRequestProperty("Accept", "application/json");
            applyAuth(conn, token);
            return readResponse(conn);
        } catch (Exception e) {
            Log.e(TAG, "GET failed: " + redact(urlStr), e);
            return null;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private String httpPostMultipart(String urlStr, Map<String, String> params,
            String fileName, byte[] fileBytes, String fileContentType, String token)
            throws Exception {
        String boundary = "FeatureLinkBoundary" + System.currentTimeMillis();
        String CRLF = "\r\n";

        HttpURLConnection conn = (HttpURLConnection) new URL(urlStr).openConnection();
        try {
            conn.setRequestMethod("POST");
            conn.setDoOutput(true);
            conn.setConnectTimeout(TIMEOUT_MS);
            conn.setReadTimeout(30_000);
            conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);
            applyAuth(conn, token);

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
        } finally {
            // Appendix A §7: this was the one helper that leaked its connection on every call.
            conn.disconnect();
        }
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
            applyAuth(conn, token);
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            conn.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream os = conn.getOutputStream()) {
                os.write(bytes);
            }
            return readResponse(conn);
        } catch (Exception e) {
            Log.e(TAG, "POST failed: " + redact(urlStr), e);
            return null;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    /** Hard ceiling on a single response body, so a hostile or broken service cannot OOM the
     * ATAK process (Appendix A §7). Sized well above any realistic paginated feature page. */
    private static final int MAX_RESPONSE_BYTES = 24 * 1024 * 1024;

    private String readResponse(HttpURLConnection conn) throws Exception {
        int code = conn.getResponseCode();
        java.io.InputStream is = (code >= 200 && code < 300)
                ? conn.getInputStream() : conn.getErrorStream();
        if (is == null) return null;
        StringBuilder sb = new StringBuilder();
        char[] buf = new char[8192];
        try (BufferedReader br = new BufferedReader(
                new InputStreamReader(is, StandardCharsets.UTF_8))) {
            int n;
            while ((n = br.read(buf)) > 0) {
                if (sb.length() + n > MAX_RESPONSE_BYTES)
                    throw new ArcGisException(-1, "Response exceeded the "
                            + (MAX_RESPONSE_BYTES / (1024 * 1024)) + " MB limit");
                sb.append(buf, 0, n);
            }
        }
        return sb.toString();
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    /** UTF-8 is guaranteed present on every JVM/Android runtime, so this cannot actually fail —
     * wrapped locally rather than forcing {@code throws Exception} up through every caller
     * (Appendix A §8). */
    private static String enc(String s) {
        if (s == null) return "";
        try {
            return URLEncoder.encode(s, "UTF-8");
        } catch (java.io.UnsupportedEncodingException e) {
            throw new IllegalStateException("UTF-8 unavailable", e);
        }
    }

    /** Appends a query parameter, correctly handling an existing query string, a trailing
     * separator, and a fragment (which must stay last). */
    static String appendQuery(String url, String param) {
        if (url == null) return null;
        String frag = "";
        int hash = url.indexOf('#');
        if (hash >= 0) { frag = url.substring(hash); url = url.substring(0, hash); }
        String sep;
        if (!url.contains("?")) sep = "?";
        else if (url.endsWith("?") || url.endsWith("&")) sep = "";
        else sep = "&";
        return url + sep + param + frag;
    }

    private static String normalizePortalUrl(String portalUrl) {
        if (portalUrl == null || portalUrl.trim().isEmpty()) {
            return "https://www.arcgis.com";
        }
        return portalUrl.trim().replaceAll("/+$", "");
    }

    /**
     * Ensures a URL addresses a specific sublayer. Case-insensitive, matching
     * {@code AutoIconset.canonicalize} so the two never disagree about the same layer's identity
     * (Appendix A §9). Defaults to index 0 only when the caller supplied a bare service root and
     * did not resolve the real sublayer list — see {@link #listSubLayers} (C-08).
     */
    static String ensureLayerIndex(String url) {
        return ensureLayerIndex(url, 0);
    }

    static String ensureLayerIndex(String url, int layerId) {
        if (url == null) return "";
        String u = url.trim().replaceAll("/+$", "");
        if (SERVICE_ROOT.matcher(u).matches()) {
            return u + "/" + Math.max(0, layerId);
        }
        return u;
    }

    /** True when {@code url} names a service root that may expose more than one sublayer. */
    public static boolean isServiceRoot(String url) {
        if (url == null) return false;
        return SERVICE_ROOT.matcher(url.trim().replaceAll("/+$", "")).matches();
    }

    /** Lower-cases with {@link Locale#ROOT} — never the default locale (C-39 class of defect). */
    static String lower(String s) {
        return s == null ? "" : s.toLowerCase(Locale.ROOT);
    }
}
