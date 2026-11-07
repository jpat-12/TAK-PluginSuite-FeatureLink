package com.atakmap.android.featurelink;

import android.graphics.Bitmap;
import android.graphics.Color;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.EncodeHintType;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel;

import org.json.JSONObject;

import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.EnumMap;
import java.util.Map;

final class QrHelper {

    private static final int HTTP_TIMEOUT_MS = 15_000;

    static final String V          = "v";
    static final String TYPE       = "type";
    static final String PORTAL     = "portal";
    static final String URL        = "url";
    static final String NAME       = "name";
    static final String IS_PRIVATE = "private";

    /** Credentials only — signs in to an ArcGIS portal, no layer info. */
    static final String TYPE_CREDENTIALS  = "credentials";

    /** PLI destination only — sets the feature layer to stream PLI to, no credentials. */
    static final String TYPE_PLI_ENDPOINT = "pli_endpoint";

    /** Layer download — adds a feature layer (public or private) to the layer list. */
    static final String TYPE_LAYER_CONFIG = "layer_config";

    /** Full PLI bundle — credentials + PLI layer URL in one scan. */
    static final String TYPE_PLI_CONFIG   = "pli_config";

    private QrHelper() {}

    static Bitmap generateBitmap(String content, int sizePx) throws Exception {
        Map<EncodeHintType, Object> hints = new EnumMap<>(EncodeHintType.class);
        hints.put(EncodeHintType.ERROR_CORRECTION, ErrorCorrectionLevel.M);
        hints.put(EncodeHintType.CHARACTER_SET, "UTF-8");
        hints.put(EncodeHintType.MARGIN, 2);

        BitMatrix matrix = new QRCodeWriter()
                .encode(content, BarcodeFormat.QR_CODE, sizePx, sizePx, hints);

        Bitmap bmp = Bitmap.createBitmap(sizePx, sizePx, Bitmap.Config.ARGB_8888);
        for (int x = 0; x < sizePx; x++) {
            for (int y = 0; y < sizePx; y++) {
                bmp.setPixel(x, y, matrix.get(x, y) ? Color.BLACK : Color.WHITE);
            }
        }
        return bmp;
    }

    /**
     * PLI endpoint QR — the Feature Layer URL to stream PLI to.
     * Scanning this sets the PLI destination without changing any credentials.
     *
     * Schema: {"v":1,"type":"pli_endpoint","url":"..."}
     */
    static String buildPliEndpointPayload(String url) throws Exception {
        return new JSONObject()
                .put(V,    1)
                .put(TYPE, TYPE_PLI_ENDPOINT)
                .put(URL,  url != null ? url : "")
                .toString();
    }

    /**
     * Layer config QR — a Feature Layer URL to add to the layer list.
     * Scanning this adds the layer as public or private.
     *
     * Schema: {"v":1,"type":"layer_config","url":"...","name":"...","private":false}
     */
    static String buildLayerPayload(String name, String url, boolean isPrivate) throws Exception {
        return new JSONObject()
                .put(V,          1)
                .put(TYPE,       TYPE_LAYER_CONFIG)
                .put(NAME,       name != null ? name : "")
                .put(URL,        url  != null ? url  : "")
                .put(IS_PRIVATE, isPrivate)
                .toString();
    }

    /**
     * PLI config QR — portal URL + PLI layer URL + optional layer name.
     * Scanning this pre-fills the portal and PLI endpoint; the recipient signs in via SSO.
     *
     * Schema: {"v":1,"type":"pli_config","portal":"...","url":"...","name":"..."}
     */
    static String buildPliPayload(String portal, String url, String name) throws Exception {
        return new JSONObject()
                .put(V,      1)
                .put(TYPE,   TYPE_PLI_CONFIG)
                .put(PORTAL, portal != null ? portal : "https://www.arcgis.com")
                .put(URL,    url    != null ? url    : "")
                .put(NAME,   name   != null ? name   : "")
                .toString();
    }

    /** True if the scanned payload is a bare URL (Mode 5 Config Download Link or Mode 4 Saved Dataset Link), not JSON. */
    static boolean isSavedDatasetLink(String payload) {
        String trimmed = payload.trim();
        return trimmed.startsWith("http://") || trimmed.startsWith("https://");
    }

    /** Max config body we'll read from a scanned link — real configs are a few KB. */
    private static final int MAX_LINK_BODY_BYTES = 2 * 1024 * 1024;
    private static final int MAX_REDIRECTS = 5;

    /** "?load=<id>" on either configurator page (infra-TAK or TAK Portal). */
    private static final java.util.regex.Pattern LOAD_LINK = java.util.regex.Pattern.compile(
            "^(https?://[^/?#]+)/(?:featurelink/featurelink-display-config|featurelink-display-config"
                    + "|featurelink-configs/configurator)/?\\?(?:.*&)?load=([a-fA-F0-9]{32})(?:[&#].*)?$");

    /**
     * Maps a scanned link to the URL that actually serves the config JSON.
     *
     * A Mode 5 "Config Download Link" (".../public/featurelink/<id>.json") is returned as-is.
     * A Mode 4 "Saved Dataset Link" ("...configurator?load=<id>") points at the configurator's
     * HTML page, which also sits behind a login — so it's rewritten to the same server's public
     * download URL for that dataset. That makes older Saved Dataset Link QRs scannable in ATAK too
     * (as long as the dataset has been re-saved since the server gained the download route).
     */
    static String toConfigDownloadUrl(String scanned) {
        String url = scanned.trim();
        java.util.regex.Matcher m = LOAD_LINK.matcher(url);
        if (m.matches()) {
            return m.group(1) + "/public/featurelink/" + m.group(2).toLowerCase(java.util.Locale.US) + ".json";
        }
        return url;
    }

    /** A scanned link that couldn't be turned into a config — message is user-facing. */
    static final class LinkFetchException extends Exception {
        LinkFetchException(String message) { super(message); }
        LinkFetchException(String message, Throwable cause) { super(message, cause); }
    }

    /**
     * Downloads the config a scanned link points to (Mode 5 Config Download Link, or a Mode 4
     * Saved Dataset Link via {@link #toConfigDownloadUrl}) and returns the JSON body. Follows
     * redirects itself, including http→https, which HttpURLConnection won't do on its own.
     * Blocking — must be called off the main thread.
     *
     * @throws LinkFetchException with a message suitable for a Toast when the server answers
     *         with an error, a login/HTML page, or something that isn't JSON.
     */
    static String fetchSavedDatasetLink(String scanned) throws LinkFetchException {
        String target = toConfigDownloadUrl(scanned);
        for (int hop = 0; hop <= MAX_REDIRECTS; hop++) {
            HttpURLConnection conn = null;
            try {
                java.net.URL u = new java.net.URL(target);
                conn = (HttpURLConnection) u.openConnection();
                conn.setRequestMethod("GET");
                conn.setInstanceFollowRedirects(false);
                conn.setConnectTimeout(HTTP_TIMEOUT_MS);
                conn.setReadTimeout(HTTP_TIMEOUT_MS);
                conn.setRequestProperty("Accept", "application/json");
                int code = conn.getResponseCode();

                if (code >= 300 && code < 400) {
                    String loc = conn.getHeaderField("Location");
                    if (loc == null || loc.isEmpty()) {
                        throw new LinkFetchException("Config link redirected nowhere (HTTP " + code + ")");
                    }
                    target = new java.net.URL(u, loc).toString();
                    continue;
                }
                if (code == 404) {
                    throw new LinkFetchException("Config not found on server — re-Save it in the configurator");
                }
                if (code == 401 || code == 403) {
                    throw new LinkFetchException("Server wants a login for this link — use a Config Download Link QR");
                }
                if (code < 200 || code >= 300) {
                    throw new LinkFetchException("Config download failed (HTTP " + code + ")");
                }

                String body = readCapped(conn.getInputStream());
                String trimmed = body.trim();
                String ctype = conn.getContentType() != null ? conn.getContentType().toLowerCase(java.util.Locale.US) : "";
                if (ctype.contains("text/html") || trimmed.startsWith("<")) {
                    // Typically an SSO/login page (Authentik forward_auth) or the configurator itself.
                    throw new LinkFetchException("Link opened a web page, not a config — use a Config Download Link QR");
                }
                if (!trimmed.startsWith("{")) {
                    throw new LinkFetchException("Link didn't return a FeatureLink config");
                }
                return trimmed;
            } catch (LinkFetchException e) {
                throw e;
            } catch (java.net.SocketTimeoutException e) {
                throw new LinkFetchException("Config download timed out", e);
            } catch (java.net.UnknownHostException e) {
                throw new LinkFetchException("Can't reach " + e.getMessage() + " — check network", e);
            } catch (Exception e) {
                throw new LinkFetchException("Could not download config: " + e.getMessage(), e);
            } finally {
                if (conn != null) conn.disconnect();
            }
        }
        throw new LinkFetchException("Too many redirects on config link");
    }

    private static String readCapped(java.io.InputStream is) throws Exception {
        if (is == null) return "";
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        try (java.io.InputStream in = is) {
            while ((n = in.read(buf)) != -1) {
                if (out.size() + n > MAX_LINK_BODY_BYTES) {
                    throw new LinkFetchException("Config download is too large");
                }
                out.write(buf, 0, n);
            }
        }
        return new String(out.toByteArray(), StandardCharsets.UTF_8);
    }

    /** Returns the parsed object if this is a valid FeatureLink QR payload, null otherwise. */
    static JSONObject parse(String json) {
        try {
            JSONObject o = new JSONObject(json.trim());
            if (o.optInt(V, 0) != 1) return null;
            switch (o.optString(TYPE, "")) {
                case TYPE_CREDENTIALS:
                    if (!o.optString(PORTAL, "").isEmpty()) return o;
                    break;
                case TYPE_PLI_ENDPOINT:
                    if (!o.optString(URL, "").isEmpty()) return o;
                    break;
                case TYPE_LAYER_CONFIG:
                    if (!o.optString(URL, "").isEmpty()) return o;
                    break;
                case TYPE_PLI_CONFIG:
                    if (!o.optString(URL, "").isEmpty()) return o;
                    break;
            }
        } catch (Exception ignored) {}
        return null;
    }

    /**
     * Returns a {@link DisplayConfig} if this JSON is a Mode 3 ("_v" full config), Mode 2
     * (v:2 url+config), or Mode 1 (v:1 compact / display-only) config. Returns null otherwise.
     *
     * Mode 3 schema: {"_v":"1.1","featureLayerUrl":"...","layer":{...},"symbology":{...},
     *                 "labels":{...},"popup":{...}}
     * Mode 2 schema: {"v":2,"url":"...","layer":{...},"sym":{...},"lbl":{...},"popup":{...}}
     * Mode 1 display-only: same shape as Mode 2 but "v":1, no "url" field.
     */
    static DisplayConfig parseDisplayConfig(String json) {
        try {
            JSONObject o = new JSONObject(json.trim());

            // Mode 3 — discriminated by the presence of "_v" rather than "v"
            if (o.has("_v")) {
                return DisplayConfig.fromJsonV3(o);
            }

            int version = o.optInt(V, 0);
            if (version == 2) {
                // v:2 must have a URL
                if (o.optString(URL, "").isEmpty()) return null;
                return DisplayConfig.fromJson(o);
            }
            if (version == 1 && o.isNull(TYPE) || (version == 1 && o.optString(TYPE, "").isEmpty())) {
                // v:1 display-only: has sym or layer but no type
                if (o.has("sym") || o.has("layer")) return DisplayConfig.fromJson(o);
            }
        } catch (Exception ignored) {}
        return null;
    }
}
