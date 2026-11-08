package com.atakmap.android.featurelink.arcgis;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Pure helpers for walking an ArcGIS Server <b>Services Directory</b> — the
 * {@code …/rest/services} table of contents a public GIS server exposes — so an operator can
 * start from a server address and drill down to a FeatureServer/MapServer instead of having to
 * know the exact service URL up front.
 *
 * <p>No Android dependencies (only {@code org.json}), so the URL and parsing rules can be checked
 * off-device. Network access lives in {@link ArcGISRestClient#browseServicesDirectory}.
 *
 * <p>Directory JSON shape ({@code GET <dir>?f=json}):
 * <pre>
 * { "currentVersion": 11.1,
 *   "folders":  ["Hydrography", "Transportation"],
 *   "services": [ {"name": "Transportation/Roads", "type": "FeatureServer"},
 *                 {"name": "Basemap", "type": "MapServer"} ] }
 * </pre>
 * A service's {@code name} is folder-qualified and relative to the {@code rest/services} root —
 * not to the folder being listed — which is why every URL here is built from {@link #root}.
 * ArcGIS Online hosted servers ({@code servicesN.arcgis.com/<orgId>/arcgis/rest/services}) also
 * return a ready-made {@code url} per service; it is preferred when present.
 */
public final class ServicesDirectory {

    private ServicesDirectory() { }

    /** Service types FeatureLink can download features from (they support {@code /query}). */
    private static final List<String> SUPPORTED_TYPES =
            Collections.unmodifiableList(java.util.Arrays.asList("FeatureServer", "MapServer"));

    /** {@code scheme://host[:port]/<prefix>/rest/services} followed by an optional folder path. */
    private static final Pattern REST_SERVICES =
            Pattern.compile("(?i)^(https?://[^/?#]+(?:/[^?#]*?)?/rest/services)(/[^?#]*)?$");

    /** A URL that already names a service root or one of its sublayers. */
    private static final Pattern SERVICE_URL =
            Pattern.compile("(?i).*/(?:FeatureServer|MapServer)(?:/\\d+)?$");

    /** One entry of a directory listing that can be handed to the Add Layer flow. */
    public static final class ServiceRef {
        /** Last path segment of the service name, for display ({@code "Roads"}). */
        public final String label;
        /** Folder-qualified name as the server reports it ({@code "Transportation/Roads"}). */
        public final String name;
        /** {@code FeatureServer} or {@code MapServer}. */
        public final String type;
        /** Fully-qualified service root URL. */
        public final String url;

        ServiceRef(String label, String name, String type, String url) {
            this.label = label;
            this.name = name;
            this.type = type;
            this.url = url;
        }
    }

    /** One level of a Services Directory. */
    public static final class Listing {
        /** The server's {@code …/rest/services} root. */
        public final String root;
        /** The folder path being listed relative to {@link #root} ({@code ""} at the top). */
        public final String folder;
        /** Sub-folder paths, relative to {@link #root} ({@code "Transportation/Bridges"}). */
        public final List<String> folders;
        public final List<ServiceRef> services;
        /** Services of other types (ImageServer, GPServer, GeocodeServer, …) that were hidden. */
        public final int unsupportedCount;

        Listing(String root, String folder, List<String> folders, List<ServiceRef> services,
                int unsupportedCount) {
            this.root = root;
            this.folder = folder;
            this.folders = Collections.unmodifiableList(folders);
            this.services = Collections.unmodifiableList(services);
            this.unsupportedCount = unsupportedCount;
        }

        /** URL of this level of the directory. */
        public String url() {
            return folder.isEmpty() ? root : root + "/" + folder;
        }

        public boolean isEmpty() {
            return folders.isEmpty() && services.isEmpty();
        }
    }

    /** A pasted address split into the server root and the folder within it. */
    public static final class Location {
        public final String root;
        public final String folder;

        Location(String root, String folder) {
            this.root = root;
            this.folder = folder;
        }

        public String url() {
            return folder.isEmpty() ? root : root + "/" + folder;
        }
    }

    /**
     * Strips what operators commonly paste along with an address: whitespace, a query string
     * ({@code ?f=pjson}, which the list's own tips recommend), a fragment, and trailing slashes.
     */
    public static String clean(String raw) {
        if (raw == null) return "";
        String u = raw.trim();
        int cut = indexOfAny(u, '?', '#');
        if (cut >= 0) u = u.substring(0, cut);
        return u.replaceAll("/+$", "");
    }

    /** True when {@code raw} already names a FeatureServer/MapServer (root or sublayer). */
    public static boolean isServiceUrl(String raw) {
        return SERVICE_URL.matcher(clean(raw)).matches();
    }

    /**
     * Splits a pasted directory address into root + folder. Accepts the server root
     * ({@code …/rest/services}) or any folder below it. A bare host ({@code https://gis.example.gov})
     * is assumed to be a default ArcGIS Server install at {@code /arcgis/rest/services}.
     *
     * @return the location, or {@code null} if this does not look like a Services Directory.
     */
    public static Location parseLocation(String raw) {
        String u = clean(raw);
        if (u.isEmpty()) return null;
        if (u.matches("(?i)^https?://[^/?#]+$")) u = u + "/arcgis/rest/services";

        Matcher m = REST_SERVICES.matcher(u);
        if (!m.matches()) return null;
        String root = m.group(1);
        String folder = m.group(2) == null ? "" : m.group(2).replaceAll("^/+", "");
        // A folder path never contains a service type segment; if it does, the caller should
        // have routed it as a service URL instead.
        if (SERVICE_URL.matcher(u).matches()) return null;
        return new Location(root, folder);
    }

    /** Parent folder of {@code folder}, or {@code ""} at (or one level below) the top. */
    public static String parentFolder(String folder) {
        if (folder == null) return "";
        int i = folder.lastIndexOf('/');
        return i < 0 ? "" : folder.substring(0, i);
    }

    /**
     * Parses one directory level. Unknown/unsupported service types are counted, not listed,
     * so the operator is never offered a row that cannot download.
     */
    public static Listing parse(String root, String folder, JSONObject json) {
        String f = folder == null ? "" : folder;
        List<String> folders = new ArrayList<>();
        List<ServiceRef> services = new ArrayList<>();
        int unsupported = 0;

        JSONArray fa = json == null ? null : json.optJSONArray("folders");
        if (fa != null) {
            for (int i = 0; i < fa.length(); i++) {
                String name = fa.optString(i, "").trim();
                if (name.isEmpty()) continue;
                // Folder names are reported relative to the root at the top level, but some
                // servers (and nested folders) report them already qualified. Normalize to
                // root-relative either way.
                String qualified = (f.isEmpty() || name.startsWith(f + "/")) ? name : f + "/" + name;
                folders.add(qualified);
            }
        }

        JSONArray sa = json == null ? null : json.optJSONArray("services");
        if (sa != null) {
            for (int i = 0; i < sa.length(); i++) {
                JSONObject s = sa.optJSONObject(i);
                if (s == null) continue;
                String name = s.optString("name", "").trim();
                String type = canonicalType(s.optString("type", ""));
                if (name.isEmpty()) continue;
                if (type == null) {
                    unsupported++;
                    continue;
                }
                String url = s.optString("url", "").trim();
                if (url.isEmpty() || !url.toLowerCase(Locale.ROOT).startsWith("http")) {
                    url = root + "/" + name + "/" + type;
                }
                services.add(new ServiceRef(lastSegment(name), name, type, clean(url)));
            }
        }

        // Stable, predictable ordering regardless of how the server lists them.
        Collections.sort(folders, String.CASE_INSENSITIVE_ORDER);
        Collections.sort(services, (a, b) -> {
            int c = a.label.compareToIgnoreCase(b.label);
            return c != 0 ? c : a.type.compareTo(b.type);
        });
        return new Listing(root, f, folders, services, unsupported);
    }

    /** Display label for a folder path. */
    public static String folderLabel(String folder) {
        return lastSegment(folder);
    }

    private static String canonicalType(String type) {
        for (String t : SUPPORTED_TYPES) if (t.equalsIgnoreCase(type)) return t;
        return null;
    }

    private static String lastSegment(String path) {
        if (path == null) return "";
        int i = path.lastIndexOf('/');
        return i < 0 ? path : path.substring(i + 1);
    }

    private static int indexOfAny(String s, char a, char b) {
        int ia = s.indexOf(a), ib = s.indexOf(b);
        if (ia < 0) return ib;
        if (ib < 0) return ia;
        return Math.min(ia, ib);
    }
}
