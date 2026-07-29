package com.atakmap.android.featurelink.arcgis;

import android.content.Context;
import android.content.Intent;
import android.os.Environment;
import android.util.Base64;
import android.util.Log;

import com.atakmap.android.ipc.AtakBroadcast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * ATAK-side ArcGIS renderer → iconset generator (Phase A of
 * WebMapFeatureLayer-AutoConfigurator.md). Java port of AUTO-ICONSET-SPEC.md.
 *
 * Reads a FeatureServer layer's renderer, pulls every picture-marker (esriPMS) symbol's
 * embedded base64 imageData, and builds an ATAK iconset zip on-device — no server round trip.
 * The UID/group/filenames are computed *deterministically from the spec*, so this produces the
 * same {uid}/{group}/{filename} strings that TAK Portal / WinTAK / CloudTAK independently
 * produce for the same layer. The PNG bytes need NOT match across platforms — ATAK reads the
 * UID from iconset.xml verbatim and derives group/filename from the zip entry paths (spec §0),
 * so two devices that generated their zips separately still resolve a shared CoT's icon
 * identically.
 *
 * Install mechanism mirrors ATAK-Plugin-QuickCapture's IconsetInstaller: write the zip into
 * {@code atak/iconsets/} and broadcast {@code com.atakmap.app.REFRESH_ICONSET} so ATAK imports
 * it (ATAK's IconsMapAdapter.addIconset() then reads iconset.xml's uid and the zip paths).
 *
 * Resize is deliberately skipped (spec §0 — cosmetic only, and irrelevant to string matching);
 * the decoded PNG bytes are written as-is, exactly like the TAK Portal generator.
 */
public final class AutoIconset {

    private static final String TAG = "FeatureLink.AutoIconset";
    public static final int SPEC_VERSION = 1;

    /** Outcome of a successful generation. */
    public static final class Result {
        /** Deterministic spec UID (written verbatim into iconset.xml). */
        public final String uid;
        /** Iconset group ("<layer name> Icons"). */
        public final String group;
        /** Number of icons written. */
        public final int iconCount;
        /** The installed zip in atak/iconsets/. */
        public final File zipFile;
        /** The renderer's driving field (empty for a single-symbol renderer). Lets the caller
         * build a display config that matches features on the right attribute. */
        public final String field;
        /** For a single-symbol (one esriPMS) renderer: the lone "uid/group/filename" path; else null. */
        public final String singleIconPath;
        /** For a uniqueValue renderer: field VALUE → "uid/group/filename" iconsetpath. Keyed by the
         * value a feature is matched on (not the label), so a synthesized display config resolves
         * per-feature correctly. Empty for single-symbol/class-break renderers. */
        public final Map<String, String> pathByValue;

        Result(String uid, String group, int iconCount, File zipFile,
                String field, String singleIconPath, Map<String, String> pathByValue) {
            this.uid = uid;
            this.group = group;
            this.iconCount = iconCount;
            this.zipFile = zipFile;
            this.field = field;
            this.singleIconPath = singleIconPath;
            this.pathByValue = pathByValue;
        }
    }

    private AutoIconset() {}

    // -------------------------------------------------------------------------
    // §2 — canonicalization
    // -------------------------------------------------------------------------

    /**
     * §2.1–§2.2: normalize to {scheme}://{authority}{/path}/FeatureServer|MapServer/{layerId},
     * scheme+authority lowercased, path case preserved, query/fragment/trailing slash removed,
     * service root defaulting to layer 0. Returns null for a Web Map link (§2.3 not yet
     * implemented — reject rather than hash a raw web-map URL, which would produce a
     * non-matching UID) or a non-FeatureServer URL.
     */
    public static String canonicalize(String sourceUrl) {
        if (sourceUrl == null) return null;
        String raw = sourceUrl.trim();
        if (raw.isEmpty()) return null;
        if (raw.matches("(?i).*/home/item\\.html.*") || raw.matches("(?i).*/sharing/rest/content/items/.*")) {
            Log.w(TAG, "Web Map links not resolved yet (spec §2.3) — paste the FeatureServer layer URL");
            return null;
        }
        try {
            java.net.URI u = new java.net.URI(raw);
            String scheme = u.getScheme();
            String authority = u.getAuthority();
            String path = u.getRawPath();
            if (scheme == null || authority == null || path == null) return null;
            scheme = scheme.toLowerCase();
            authority = authority.toLowerCase();
            path = path.replaceAll("/{2,}", "/").replaceAll("/+$", "");

            java.util.regex.Matcher m = java.util.regex.Pattern
                    .compile("^(.*/(?:FeatureServer|MapServer))(?:/(\\d+))?$", java.util.regex.Pattern.CASE_INSENSITIVE)
                    .matcher(path);
            if (!m.matches()) {
                Log.w(TAG, "Not a FeatureServer/MapServer layer URL: " + raw);
                return null;
            }
            String base = m.group(1);
            String layerId = m.group(2) != null ? m.group(2) : "0";
            return scheme + "://" + authority + base + "/" + layerId;
        } catch (Exception e) {
            Log.w(TAG, "canonicalize failed for " + raw, e);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // §4 — UID
    // -------------------------------------------------------------------------

    /** §4: lowercase hex SHA-256 of {@code canonicalUrl + "/" + fieldName}. */
    public static String uid(String canonicalUrl, String fieldName) {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] d = md.digest((canonicalUrl + "/" + (fieldName == null ? "" : fieldName))
                    .getBytes(StandardCharsets.UTF_8));
            StringBuilder sb = new StringBuilder(d.length * 2);
            for (byte b : d) sb.append(String.format("%02x", b));
            return sb.toString();
        } catch (Exception e) {
            throw new RuntimeException("SHA-256 unavailable", e);
        }
    }

    // -------------------------------------------------------------------------
    // §5 — naming
    // -------------------------------------------------------------------------

    /** §5.1: sanitize (non [A-Za-z0-9 _-] → _), collapse whitespace, trim, cap 60. Suffix " Icons" appended by caller. */
    static String sanitizeGroupBase(String s) {
        if (s == null) s = "";
        String out = s.replaceAll("[^A-Za-z0-9 _-]", "_").replaceAll("\\s+", " ").trim();
        return out.length() > 60 ? out.substring(0, 60) : out;
    }

    /** §5.2: strip path prefix → strip trailing .png → sanitize (non [A-Za-z0-9._-] → _) → append .png. */
    static String fileName(String rawLabel) {
        String base = rawLabel == null ? "" : rawLabel;
        int slash = Math.max(base.lastIndexOf('/'), base.lastIndexOf('\\'));
        if (slash >= 0) base = base.substring(slash + 1);
        base = base.replaceAll("(?i)\\.png$", "");
        base = base.replaceAll("[^A-Za-z0-9._-]", "_");
        if (base.isEmpty()) base = "icon";
        return base + ".png";
    }

    /** §5.3: append _2/_3/... before .png until unique within {@code seen}. */
    static String dedupe(String fileName, Set<String> seen) {
        if (seen.add(fileName)) return fileName;
        String base = fileName.replaceAll("(?i)\\.png$", "");
        int i = 2;
        while (seen.contains(base + "_" + i + ".png")) i++;
        String out = base + "_" + i + ".png";
        seen.add(out);
        return out;
    }

    // -------------------------------------------------------------------------
    // §3 — renderer → esriPMS entries
    // -------------------------------------------------------------------------

    /** One picture-marker entry. */
    private static final class Pms {
        final String value;         // renderer match value (uniqueValue); null for default/single/class-break
        final String rawLabel;      // drives the filename; null → default (Other.png)
        final String imageData;     // base64 PNG
        final boolean isDefault;
        Pms(String value, String rawLabel, String imageData, boolean isDefault) {
            this.value = value; this.rawLabel = rawLabel; this.imageData = imageData; this.isDefault = isDefault;
        }
    }

    /** What §3 extraction found besides the entries: the driving field and whether it's single-symbol. */
    private static final class Extraction {
        String field = "";
        boolean single = false;
    }

    private static Pms pmsFrom(JSONObject symbol, String value, String rawLabel, boolean isDefault) {
        if (symbol == null) return null;
        if (!"esriPMS".equals(symbol.optString("type", ""))) return null;
        String img = symbol.optString("imageData", "");
        if (img.isEmpty()) return null;
        return new Pms(value, rawLabel, img, isDefault);
    }

    /** §3: driving field + ordered esriPMS entries (esriSMS shapes skipped; renderer array order preserved). */
    private static Extraction extract(JSONObject renderer, String fieldOverride, List<Pms> out) {
        Extraction ex = new Extraction();
        if (renderer == null) {
            ex.field = fieldOverride != null ? fieldOverride : "";
            return ex;
        }
        String type = renderer.optString("type", "simple");

        if ("uniqueValue".equals(type) || "uniqueValueRenderer".equals(type)) {
            ex.field = renderer.optString("field1", renderer.optString("field", ""));
            JSONArray infos = renderer.optJSONArray("uniqueValueInfos");
            if (infos != null) {
                for (int i = 0; i < infos.length(); i++) {
                    JSONObject info = infos.optJSONObject(i);
                    if (info == null) continue;
                    String value = info.optString("value", "");
                    String label = info.optString("label", value);
                    Pms p = pmsFrom(info.optJSONObject("symbol"), value, label, false);
                    if (p != null) out.add(p);
                }
            }
            addDefault(renderer, out);
        } else if ("classBreaks".equals(type) || "classBreaksRenderer".equals(type)) {
            ex.field = renderer.optString("field", "");
            // Class breaks match by numeric range, not value equality — the icons still get
            // generated (for sharing) but can't be mapped to a self-render display config here.
            JSONArray infos = renderer.optJSONArray("classBreakInfos");
            if (infos != null) {
                for (int i = 0; i < infos.length(); i++) {
                    JSONObject info = infos.optJSONObject(i);
                    if (info == null) continue;
                    Pms p = pmsFrom(info.optJSONObject("symbol"), null, info.optString("label", ""), false);
                    if (p != null) out.add(p);
                }
            }
            addDefault(renderer, out);
        } else {
            // simple / bare symbol — single symbol, no field
            String label = renderer.optString("label", "");
            Pms p = pmsFrom(renderer.optJSONObject("symbol"), null,
                    label.isEmpty() ? null : label, label.isEmpty());
            if (p != null) {
                out.add(p);
                ex.single = true;
            }
        }

        if (fieldOverride != null && !fieldOverride.isEmpty()) ex.field = fieldOverride;
        return ex;
    }

    private static void addDefault(JSONObject renderer, List<Pms> out) {
        String defLabel = renderer.optString("defaultLabel", "");
        Pms d = pmsFrom(renderer.optJSONObject("defaultSymbol"), null,
                defLabel.isEmpty() ? null : defLabel, defLabel.isEmpty());
        if (d != null) out.add(d);
    }

    // -------------------------------------------------------------------------
    // Orchestration
    // -------------------------------------------------------------------------

    /**
     * Generate + install an iconset from a FeatureServer layer, per the spec. Blocking (does a
     * network fetch) — call from a background thread. Returns null if the URL is invalid, the
     * renderer has no esriPMS symbols, or install fails (all logged).
     *
     * @param ctx           an ATAK/host context (for the broadcast + filesystem)
     * @param client        the ArcGIS REST client (supplies the renderer JSON fetch)
     * @param sourceUrl     pasted FeatureServer/{layerId} URL
     * @param fieldOverride optional renderer field override (null → taken from the renderer)
     * @param token         ArcGIS token, or null for public layers
     */
    public static Result generate(Context ctx, ArcGISRestClient client,
            String sourceUrl, String fieldOverride, String token) {
        String canonicalUrl = canonicalize(sourceUrl);
        if (canonicalUrl == null) return null;

        JSONObject meta;
        try {
            meta = client.fetchJson(canonicalUrl, token);
        } catch (Exception e) {
            Log.e(TAG, "renderer fetch failed for " + canonicalUrl, e);
            return null;
        }
        if (meta == null || meta.has("error")) {
            Log.w(TAG, "no layer metadata for " + canonicalUrl);
            return null;
        }

        String layerName = meta.optString("name", meta.optString("serviceDescription", "Layer"));
        JSONObject drawingInfo = meta.optJSONObject("drawingInfo");
        JSONObject renderer = drawingInfo != null ? drawingInfo.optJSONObject("renderer") : null;

        List<Pms> entries = new ArrayList<>();
        Extraction ex = extract(renderer, fieldOverride, entries);
        String field = ex.field;
        if (entries.isEmpty()) {
            Log.d(TAG, "no esriPMS symbols in renderer for " + canonicalUrl + " — nothing to generate");
            return null;
        }

        String uid = uid(canonicalUrl, field);
        String group = sanitizeGroupBase(layerName) + " Icons";

        // §5.2/§5.3 naming + base64 decode, preserving renderer order for deterministic _N suffixes
        Set<String> seen = new HashSet<>();
        Map<String, byte[]> files = new LinkedHashMap<>();       // filename → PNG bytes
        Map<String, String> pathByValue = new LinkedHashMap<>(); // field value → iconsetpath (self-render)
        String singleIconPath = null;
        for (Pms p : entries) {
            String fname = dedupe(p.isDefault && p.rawLabel == null ? "Other.png" : fileName(p.rawLabel), seen);
            byte[] bytes;
            try {
                bytes = Base64.decode(p.imageData, Base64.DEFAULT);
            } catch (Exception e) {
                Log.w(TAG, "skipping undecodable symbol " + fname, e);
                continue;
            }
            if (bytes == null || bytes.length == 0) continue;
            files.put(fname, bytes);
            String path = uid + "/" + group + "/" + fname;
            if (p.value != null && !p.value.isEmpty()) {
                pathByValue.put(p.value, path);   // uniqueValue → matchable per feature
            } else if (ex.single) {
                singleIconPath = path;            // lone simple-renderer icon
            }
        }
        if (files.isEmpty()) {
            Log.w(TAG, "all symbols failed to decode for " + canonicalUrl);
            return null;
        }

        File zip = buildAndInstall(ctx, uid, group, files);
        if (zip == null) return null;
        Log.i(TAG, "installed iconset uid=" + uid + " group='" + group + "' icons=" + files.size());
        return new Result(uid, group, files.size(), zip, field, singleIconPath, pathByValue);
    }

    /**
     * Builds the iconset zip (iconset.xml at root + one {group}/{filename} entry per PNG, spec §7)
     * into atak/iconsets/ and asks ATAK to reload iconsets. Returns the zip file, or null on failure.
     */
    private static File buildAndInstall(Context ctx, String uid, String group, Map<String, byte[]> files) {
        File dir = new File(Environment.getExternalStorageDirectory(), "atak/iconsets");
        //noinspection ResultOfMethodCallIgnored
        dir.mkdirs();
        // group is already [A-Za-z0-9 _-]-safe (spec §5.1) so it's a valid file name.
        File zip = new File(dir, group + ".zip");

        try (ZipOutputStream zos = new ZipOutputStream(new FileOutputStream(zip))) {
            // iconset.xml (root) — non-empty name+uid so ATAK trusts the uid verbatim (spec §0.1/§7)
            zos.putNextEntry(new ZipEntry("iconset.xml"));
            zos.write(buildIconsetXml(uid, group, files.keySet()).getBytes(StandardCharsets.UTF_8));
            zos.closeEntry();

            for (Map.Entry<String, byte[]> e : files.entrySet()) {
                // "{group}/{filename}" — ATAK derives group from the first segment, filename from
                // the last (spec §0.2), so these paths ARE the resolved iconsetpath.
                zos.putNextEntry(new ZipEntry(group + "/" + e.getKey()));
                zos.write(e.getValue());
                zos.closeEntry();
            }
        } catch (Exception e) {
            Log.e(TAG, "failed to write iconset zip " + zip.getAbsolutePath(), e);
            //noinspection ResultOfMethodCallIgnored
            zip.delete();
            return null;
        }

        try {
            // The actual broadcast ATAK's IconsMapComponent listens for — confirmed by
            // decompiling com.atakmap.android.icons.IconsMapComponent/IconsMapAdapter from the
            // SDK jar. There is no "com.atakmap.app.REFRESH_ICONSET" action anywhere in ATAK
            // core; a previous version of this method sent that and ATAK silently never
            // imported the zip (UserIconDatabase.getIconSet() always came back empty, so this
            // method kept re-running on every reopen without ever actually registering).
            // IconsMapComponent's own default-iconset bootstrap sends exactly this shape when
            // it installs a bundled set, which is what this mirrors.
            Intent intent = new Intent("com.atakmap.android.icons.ADD_ICONSET");
            intent.putExtra("show_progress", false);
            intent.putExtra("filepath", zip.getAbsolutePath());
            AtakBroadcast.getInstance().sendBroadcast(intent);
        } catch (Exception e) {
            Log.w(TAG, "ADD_ICONSET broadcast failed (zip still written)", e);
        }
        return zip;
    }

    /**
     * §7: iconset.xml with non-empty name+uid, one <icon> per PNG, version marker.
     *
     * The per-icon element carries ONLY {@code name} — no {@code group} attribute. ATAK's real
     * parser (org.simpleframework.xml, strict mode) deserializes each <icon> into
     * com.atakmap.android.icons.UserIcon, whose only @Attribute-annotated fields are "name" and
     * "type2525b"; UserIcon.group is a plain unannotated Java field, not part of the XML schema.
     * A stray "group" attribute with no matching annotation throws AttributeException and fails
     * the ENTIRE document parse — confirmed live via `adb logcat` against a real device
     * (org.simpleframework.xml.core.AttributeException: Attribute 'group' does not have a match
     * in class com.atakmap.android.icons.UserIcon) — at which point ATAK discards our uid
     * entirely and falls back to hashing the raw zip bytes (spec §0.1's documented fallback),
     * breaking the whole cross-platform UID-matching guarantee. This matches §0.2 anyway: ATAK
     * only ever reads group from the zip ENTRY PATH, never from this XML.
     */
    private static String buildIconsetXml(String uid, String group, Set<String> fileNames) {
        StringBuilder sb = new StringBuilder();
        sb.append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.append("<iconset name=\"").append(xml(group)).append("\" uid=\"").append(xml(uid))
          .append("\" defaultGroup=\"").append(xml(group)).append("\" version=\"")
          .append(SPEC_VERSION).append("\">\n");
        for (String f : fileNames) {
            sb.append("  <icon name=\"").append(xml(f)).append("\"/>\n");
        }
        sb.append("</iconset>\n");
        return sb.toString();
    }

    private static String xml(String s) {
        if (s == null) return "";
        return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;");
    }
}
