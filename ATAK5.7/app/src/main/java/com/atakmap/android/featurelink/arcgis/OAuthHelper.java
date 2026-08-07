package com.atakmap.android.featurelink.arcgis;

import android.util.Base64;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;

public class OAuthHelper {

    public static class OAuthTokens {
        public final String accessToken;
        public final String refreshToken;
        public final String username;
        public final long   expiresInSeconds;

        public OAuthTokens(String accessToken, String refreshToken,
                           String username, long expiresInSeconds) {
            this.accessToken      = accessToken;
            this.refreshToken     = refreshToken;
            this.username         = username;
            this.expiresInSeconds = expiresInSeconds;
        }
    }

    /** Returns [codeVerifier, codeChallenge]. */
    public static String[] generatePkce() throws Exception {
        byte[] bytes = new byte[32];
        new SecureRandom().nextBytes(bytes);
        String codeVerifier = Base64.encodeToString(
                bytes, Base64.URL_SAFE | Base64.NO_PADDING | Base64.NO_WRAP);

        byte[] digest = MessageDigest.getInstance("SHA-256")
                .digest(codeVerifier.getBytes(StandardCharsets.US_ASCII));
        String codeChallenge = Base64.encodeToString(
                digest, Base64.URL_SAFE | Base64.NO_PADDING | Base64.NO_WRAP);

        return new String[]{codeVerifier, codeChallenge};
    }

    /** Redirect URI registered for this OAuth client. Kept in one place so the authorize and
     * token requests can never drift apart (Esri rejects a mismatch). */
    public static final String REDIRECT_URI = "featurelink://auth";

    /**
     * C-09: generates the OAuth {@code state} CSRF nonce. 32 bytes from {@link SecureRandom},
     * URL-safe base64. Held in memory only by {@link ArcGISAuthManager} and required to match on
     * the callback — without it any app on the device could invoke
     * {@code featurelink://auth?code=ATTACKER_CODE} and bind the operator's session to the
     * attacker's ArcGIS account, sending every subsequent PLI update to the attacker's portal.
     */
    public static String generateState() {
        byte[] bytes = new byte[32];
        new SecureRandom().nextBytes(bytes);
        return Base64.encodeToString(bytes, Base64.URL_SAFE | Base64.NO_PADDING | Base64.NO_WRAP);
    }

    public static String buildAuthUrl(String portalUrl, String clientId, String codeChallenge,
            String state) {
        if (state == null || state.isEmpty()) {
            throw new IllegalArgumentException("OAuth state is mandatory (C-09)");
        }
        return normalizePortal(portalUrl)
                + "/sharing/rest/oauth2/authorize"
                + "?client_id="             + enc(clientId)
                + "&response_type=code"
                + "&redirect_uri="          + enc(REDIRECT_URI)
                + "&code_challenge="        + enc(codeChallenge)
                + "&code_challenge_method=S256"
                + "&state="                 + enc(state);
    }

    /** Must be called on a background thread. */
    public static OAuthTokens exchangeCode(String portalUrl, String clientId,
                                           String code, String codeVerifier) throws Exception {
        String endpoint = normalizePortal(portalUrl) + "/sharing/rest/oauth2/token";
        String body = "grant_type=authorization_code"
                + "&client_id="     + enc(clientId)
                + "&code="          + enc(code)
                + "&redirect_uri="  + enc(REDIRECT_URI)
                + "&code_verifier=" + enc(codeVerifier);
        return postForTokens(endpoint, body);
    }

    /** Must be called on a background thread. */
    public static OAuthTokens refreshAccessToken(String portalUrl, String clientId,
                                                 String refreshToken) throws Exception {
        String endpoint = normalizePortal(portalUrl) + "/sharing/rest/oauth2/token";
        String body = "grant_type=refresh_token"
                + "&client_id="     + enc(clientId)
                + "&refresh_token=" + enc(refreshToken);
        return postForTokens(endpoint, body);
    }

    private static OAuthTokens postForTokens(String endpoint, String body) throws Exception {
        HttpURLConnection conn = (HttpURLConnection) new URL(endpoint).openConnection();
        try {
            conn.setRequestMethod("POST");
            conn.setConnectTimeout(15_000);
            conn.setReadTimeout(15_000);
            conn.setDoOutput(true);
            conn.setRequestProperty("Content-Type", "application/x-www-form-urlencoded");
            conn.setRequestProperty("Accept", "application/json");

            try (OutputStream os = conn.getOutputStream()) {
                os.write(body.getBytes(StandardCharsets.UTF_8));
            }

            // Esri returns invalid_grant / invalid_client as an HTTP 400 with the explanation in
            // the body. Reading only getInputStream() threw an IOException before the body was
            // ever read, so the json.has("error") branch below was unreachable for exactly the
            // failure the operator most needs explained — an expired refresh token
            // (Appendix A §7).
            int status = conn.getResponseCode();
            java.io.InputStream is = (status >= 200 && status < 300)
                    ? conn.getInputStream() : conn.getErrorStream();
            if (is == null) throw new Exception("OAuth endpoint returned HTTP " + status
                    + " with no body");

            StringBuilder sb = new StringBuilder();
            try (BufferedReader br = new BufferedReader(
                    new InputStreamReader(is, StandardCharsets.UTF_8))) {
                String line;
                while ((line = br.readLine()) != null) sb.append(line);
            }

            JSONObject json = new JSONObject(sb.toString());
            if (json.has("error")) {
                JSONObject err = json.optJSONObject("error");
                String message = err != null
                        ? err.optString("message", err.optString("error_description", "OAuth error"))
                        : json.optString("error_description", json.optString("error"));
                throw new Exception("OAuth error: " + message);
            }
            if (!json.has("access_token")) {
                throw new Exception("OAuth token endpoint returned no access_token (HTTP "
                        + status + ")");
            }
            return new OAuthTokens(
                    json.getString("access_token"),
                    json.optString("refresh_token", null),
                    json.optString("username", null),
                    json.optLong("expires_in", 3600)
            );
        } finally {
            conn.disconnect();
        }
    }

    private static String normalizePortal(String url) {
        return url == null ? "https://www.arcgis.com" : url.replaceAll("/+$", "");
    }

    /** UTF-8 is always available. Silently returning the unencoded string on failure (as this
     * used to) would corrupt the token request, so this fails loudly instead (Appendix A §8). */
    private static String enc(String s) {
        if (s == null) return "";
        try {
            return URLEncoder.encode(s, "UTF-8");
        } catch (java.io.UnsupportedEncodingException e) {
            throw new IllegalStateException("UTF-8 unavailable", e);
        }
    }
}
