package com.atakmap.android.featurelink.arcgis;

/**
 * Small URL string helpers shared by the ArcGIS client classes.
 *
 * <p>Trailing slashes are stripped with a character loop rather than
 * {@code replaceAll("/+$", "")}: on input with a long run of {@code /} that does not end the
 * string, that regex backtracks quadratically (CodeQL java/polynomial-redos). Every URL here can
 * come from an operator-typed address or a remote server's JSON.
 */
public final class UrlUtil {

    private UrlUtil() { }

    /** {@code s} without trailing {@code '/'} characters; {@code null} stays {@code null}. */
    public static String stripTrailingSlashes(String s) {
        if (s == null) return null;
        int end = s.length();
        while (end > 0 && s.charAt(end - 1) == '/') end--;
        return s.substring(0, end);
    }

    /** {@code s} without leading {@code '/'} characters; {@code null} stays {@code null}. */
    public static String stripLeadingSlashes(String s) {
        if (s == null) return null;
        int start = 0;
        while (start < s.length() && s.charAt(start) == '/') start++;
        return s.substring(start);
    }
}
