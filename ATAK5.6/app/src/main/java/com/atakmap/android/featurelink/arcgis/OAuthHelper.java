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

    public static String buildAuthUrl(String portalUrl, String clientId, String codeChallenge) {
        return normalizePortal(portalUrl)
                + "/sharing/rest/oauth2/authorize"
                + "?client_id="             + enc(clientId)
                + "&response_type=code"
                + "&redirect_uri="          + enc("featurelink://auth")
                + "&code_challenge="        + enc(codeChallenge)
                + "&code_challenge_method=S256";
    }

    /** Must be called on a background thread. */
    public static OAuthTokens exchangeCode(String portalUrl, String clientId,
                                           String code, String codeVerifier) throws Exception {
        String endpoint = normalizePortal(portalUrl) + "/sharing/rest/oauth2/token";
        String body = "grant_type=authorization_code"
                + "&client_id="     + enc(clientId)
                + "&code="          + enc(code)
                + "&redirect_uri="  + enc("featurelink://auth")
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
        conn.setRequestMethod("POST");
        conn.setConnectTimeout(15_000);
        conn.setReadTimeout(15_000);
        conn.setDoOutput(true);
        conn.setRequestProperty("Content-Type", "application/x-www-form-urlencoded");

        try (OutputStream os = conn.getOutputStream()) {
            os.write(body.getBytes(StandardCharsets.UTF_8));
        }

        StringBuilder sb = new StringBuilder();
        try (BufferedReader br = new BufferedReader(
                new InputStreamReader(conn.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = br.readLine()) != null) sb.append(line);
        }

        JSONObject json = new JSONObject(sb.toString());
        if (json.has("error")) {
            throw new Exception("OAuth error: " + json.optString("error_description",
                    json.optString("error")));
        }
        return new OAuthTokens(
                json.getString("access_token"),
                json.optString("refresh_token", null),
                json.optString("username", null),
                json.optLong("expires_in", 3600)
        );
    }

    private static String normalizePortal(String url) {
        return url == null ? "https://www.arcgis.com" : url.replaceAll("/+$", "");
    }

    private static String enc(String s) {
        try { return URLEncoder.encode(s, "UTF-8"); }
        catch (Exception e) { return s; }
    }
}
