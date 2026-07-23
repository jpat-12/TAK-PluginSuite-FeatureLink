package com.atakmap.android.featurelink.arcgis;

import android.content.SharedPreferences;
import android.os.Handler;
import android.util.Log;

import java.util.concurrent.ExecutorService;

public class ArcGISAuthManager {

    private static final String TAG               = "ArcGISAuthManager";
    private static final String CLIENT_ID         = "RXtGmClVuYd1Sp7d";
    private static final String PREF_CODE_VERIFIER = "oauth_code_verifier";
    private static final String PREF_PORTAL_URL   = "portal_url";
    private static final String PREF_TOKEN_EXPIRY = "oauth_token_expiry";
    private static final String PREF_USERNAME     = "oauth_username";
    private static final String PREF_REFRESH_TOKEN = "oauth_refresh_token";

    public interface FailureCallback {
        void onFailure(String message);
    }

    private final SharedPreferences prefs;

    private String accessToken = null;
    private String username    = null;
    private long   tokenExpiry = 0;

    public ArcGISAuthManager(SharedPreferences prefs) {
        this.prefs = prefs;
        String savedUsername = prefs.getString(PREF_USERNAME, null);
        if (savedUsername != null && !savedUsername.isEmpty()) {
            this.username = savedUsername;
        }
    }

    /**
     * Starts the OAuth PKCE flow. Saves the code verifier to prefs for later exchange.
     * @return authorization URL to load in a WebView, or null on error
     */
    public String startOAuthFlow(String portalUrl) {
        try {
            String[] pkce          = OAuthHelper.generatePkce();
            String   codeVerifier  = pkce[0];
            String   codeChallenge = pkce[1];
            prefs.edit()
                    .putString(PREF_CODE_VERIFIER, codeVerifier)
                    .putString(PREF_PORTAL_URL,    portalUrl)
                    .apply();
            return OAuthHelper.buildAuthUrl(portalUrl, CLIENT_ID, codeChallenge);
        } catch (Exception e) {
            Log.e(TAG, "startOAuthFlow failed", e);
            return null;
        }
    }

    /**
     * Exchanges the authorization code for tokens. Runs on a background thread via executor;
     * onSuccess / onFailure are posted to mainHandler (UI thread).
     */
    public void handleAuthCode(String code, ExecutorService executor, Handler mainHandler,
            Runnable onSuccess, FailureCallback onFailure) {
        String portalUrl    = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
        String codeVerifier = prefs.getString(PREF_CODE_VERIFIER, null);

        if (codeVerifier == null) {
            mainHandler.post(() -> onFailure.onFailure("Sign-in session expired — tap Sign In again"));
            return;
        }

        executor.submit(() -> {
            try {
                OAuthHelper.OAuthTokens tokens =
                        OAuthHelper.exchangeCode(portalUrl, CLIENT_ID, code, codeVerifier);
                if (tokens == null) {
                    mainHandler.post(() -> onFailure.onFailure("Token exchange returned no data"));
                    return;
                }
                accessToken = tokens.accessToken;
                tokenExpiry = System.currentTimeMillis() + tokens.expiresInSeconds * 1000L;
                username    = tokens.username;
                prefs.edit()
                        .putString(PREF_USERNAME,      tokens.username)
                        .putString(PREF_REFRESH_TOKEN, tokens.refreshToken != null ? tokens.refreshToken : "")
                        .putLong(PREF_TOKEN_EXPIRY,    tokenExpiry)
                        .remove(PREF_CODE_VERIFIER)
                        .apply();
                Log.d(TAG, "OAuth authenticated as: " + tokens.username);
                mainHandler.post(onSuccess);
            } catch (Exception e) {
                Log.e(TAG, "handleAuthCode failed", e);
                mainHandler.post(() -> onFailure.onFailure(
                        e.getMessage() != null ? e.getMessage() : "Unknown error"));
            }
        });
    }

    /**
     * Returns a valid access token, silently refreshing if expired.
     * Returns null if the refresh token is also expired (user must re-login).
     * Must be called from a background thread.
     */
    public String getToken() {
        if (accessToken != null && System.currentTimeMillis() < tokenExpiry) {
            return accessToken;
        }
        String refreshToken = prefs.getString(PREF_REFRESH_TOKEN, null);
        String savedUsername = prefs.getString(PREF_USERNAME, null);
        if (refreshToken != null && !refreshToken.isEmpty()) {
            String portal = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
            try {
                OAuthHelper.OAuthTokens tokens =
                        OAuthHelper.refreshAccessToken(portal, CLIENT_ID, refreshToken);
                if (tokens != null) {
                    accessToken = tokens.accessToken;
                    tokenExpiry = System.currentTimeMillis() + tokens.expiresInSeconds * 1000L;
                    SharedPreferences.Editor ed = prefs.edit()
                            .putLong(PREF_TOKEN_EXPIRY, tokenExpiry);
                    if (tokens.refreshToken != null && !tokens.refreshToken.isEmpty()) {
                        ed.putString(PREF_REFRESH_TOKEN, tokens.refreshToken);
                        if (tokens.username != null) ed.putString(PREF_USERNAME, tokens.username);
                    }
                    ed.apply();
                    Log.d(TAG, "Token silently refreshed");
                    return accessToken;
                }
            } catch (Exception e) {
                Log.w(TAG, "Silent token refresh failed — re-login required", e);
            }
        }
        return null;
    }

    public boolean isAuthenticated() {
        return username != null;
    }

    public String getUsername() { return username; }

    public void logout() {
        accessToken = null;
        tokenExpiry = 0;
        username    = null;
        prefs.edit()
                .remove(PREF_USERNAME)
                .remove(PREF_REFRESH_TOKEN)
                .remove(PREF_CODE_VERIFIER)
                .remove(PREF_TOKEN_EXPIRY)
                .apply();
        Log.d(TAG, "Logged out");
    }
}
