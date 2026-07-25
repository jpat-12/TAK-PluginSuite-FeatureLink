package com.atakmap.android.featurelink;

import android.graphics.Bitmap;
import android.graphics.Color;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.EncodeHintType;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
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

    /** True if the scanned payload is a Mode 4 "Saved Dataset Link" — a bare URL, not JSON. */
    static boolean isSavedDatasetLink(String payload) {
        String trimmed = payload.trim();
        return trimmed.startsWith("http://") || trimmed.startsWith("https://");
    }

    /**
     * Fetches a Mode 4 "Saved Dataset Link" URL and returns the response body, which is
     * expected to be a Mode 1/2/3 JSON config. Blocking — must be called off the main thread.
     */
    static String fetchSavedDatasetLink(String url) throws Exception {
        HttpURLConnection conn = null;
        try {
            conn = (HttpURLConnection) new URL(url).openConnection();
            conn.setRequestMethod("GET");
            conn.setConnectTimeout(HTTP_TIMEOUT_MS);
            conn.setReadTimeout(HTTP_TIMEOUT_MS);
            conn.setRequestProperty("Accept", "application/json");
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
        } finally {
            if (conn != null) conn.disconnect();
        }
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
