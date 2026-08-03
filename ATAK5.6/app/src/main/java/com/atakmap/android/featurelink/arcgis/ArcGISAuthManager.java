package com.atakmap.android.featurelink.arcgis;

import android.content.SharedPreferences;
import android.os.Handler;
import android.util.Log;

import java.util.concurrent.ExecutorService;

public class ArcGISAuthManager {

    private static final String TAG               = "ArcGISAuthManager";
    private static final String CLIENT_ID         = "RXtGmClVuYd1Sp7d";
    /** Legacy key — the PKCE verifier is now held in memory only (see {@link #pendingVerifier}).
     * Retained solely so any value left on disk by an older build is purged on first run. */
    private static final String PREF_CODE_VERIFIER = "oauth_code_verifier";
    private static final String PREF_PORTAL_URL   = "portal_url";
    private static final String PREF_TOKEN_EXPIRY = "oauth_token_expiry";
    private static final String PREF_USERNAME     = "oauth_username";
    private static final String PREF_REFRESH_TOKEN = "oauth_refresh_token";

    public interface FailureCallback {
        void onFailure(String message);
    }

    private final SharedPreferences prefs;
    /** C-20 — the refresh token is written through this, never as plaintext. */
    private final SecureTokenStore secureStore;

    // Written from executor threads (handleAuthCode, getToken's refresh) and read from the main
    // thread (isAuthenticated/getUsername, called on every navigation). Without volatile the UI
    // can show "Not Signed In" indefinitely after a successful background sign-in
    // (Appendix A §5).
    private volatile String accessToken = null;
    private volatile String username    = null;
    private volatile long   tokenExpiry = 0;

    /**
     * C-09 — the OAuth CSRF nonce and the PKCE verifier for the flow currently in progress.
     * <b>In memory only</b>, per the brief: persisting them meant (a) a cancelled sign-in left a
     * usable verifier on disk indefinitely, and (b) there was nothing at all to check a callback
     * against, so any app could push an attacker-controlled authorization code into this plugin
     * and bind the operator's session to the attacker's ArcGIS org.
     *
     * <p>Guarded by {@link #authLock}. Both are cleared on success, on failure, and on cancel.
     */
    private String pendingState = null;
    private String pendingVerifier = null;
    private final Object authLock = new Object();

    /** Serialises token refresh so two threads cannot issue two concurrent refresh requests —
     * on a portal with refresh-token rotation the second invalidates the first and silently logs
     * the operator out (Appendix A §5). */
    private final Object refreshLock = new Object();

    public ArcGISAuthManager(SharedPreferences prefs) {
        this.prefs = prefs;
        this.secureStore = new SecureTokenStore(prefs);
        String savedUsername = prefs.getString(PREF_USERNAME, null);
        if (savedUsername != null && !savedUsername.isEmpty()) {
            this.username = savedUsername;
        }
        // Purge any verifier a previous plaintext build left behind.
        if (prefs.contains(PREF_CODE_VERIFIER)) {
            prefs.edit().remove(PREF_CODE_VERIFIER).apply();
        }
    }

    /**
     * Starts the OAuth PKCE flow. The verifier and the {@code state} nonce are held in memory for
     * the duration of the browser round trip and are required to match on the callback.
     *
     * @return authorization URL to load in a WebView, or null on error
     */
    public String startOAuthFlow(String portalUrl) {
        try {
            String[] pkce = OAuthHelper.generatePkce();
            String state = OAuthHelper.generateState();
            synchronized (authLock) {
                pendingVerifier = pkce[0];
                pendingState    = state;
            }
            prefs.edit().putString(PREF_PORTAL_URL, portalUrl).apply();
            return OAuthHelper.buildAuthUrl(portalUrl, CLIENT_ID, pkce[1], state);
        } catch (Exception e) {
            Log.e(TAG, "startOAuthFlow failed", e);
            cancelOAuthFlow();
            return null;
        }
    }

    /** True when a sign-in is in progress. A callback arriving with no pending flow is rejected. */
    public boolean isOAuthFlowPending() {
        synchronized (authLock) {
            return pendingState != null && pendingVerifier != null;
        }
    }

    /**
     * C-09 — constant-time-ish comparison of the callback's {@code state} against the pending
     * nonce. Returns false when no flow is pending, when the callback carries no state, or when
     * it does not match.
     */
    public boolean isCallbackStateValid(String state) {
        synchronized (authLock) {
            if (pendingState == null || state == null) return false;
            byte[] a = pendingState.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            byte[] b = state.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            if (a.length != b.length) return false;
            int diff = 0;
            for (int i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /** Clears the pending flow — call from the Cancel handler and from every failure path so a
     * stale verifier can never survive a cancelled sign-in. */
    public void cancelOAuthFlow() {
        synchronized (authLock) {
            pendingState = null;
            pendingVerifier = null;
        }
        prefs.edit().remove(PREF_CODE_VERIFIER).apply();
    }

    /**
     * Exchanges the authorization code for tokens. Runs on a background thread via executor;
     * onSuccess / onFailure are posted to mainHandler (UI thread).
     *
     * @param state the {@code state} value the callback carried. Mandatory (C-09) — a callback
     *              whose state does not match the pending flow is rejected outright.
     */
    public void handleAuthCode(String code, String state, ExecutorService executor,
            Handler mainHandler, Runnable onSuccess, FailureCallback onFailure) {
        String portalUrl = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");

        final String codeVerifier;
        synchronized (authLock) {
            if (pendingState == null || pendingVerifier == null) {
                mainHandler.post(() -> onFailure.onFailure(
                        "No sign-in is in progress — tap Sign In again"));
                return;
            }
            if (!isCallbackStateValid(state)) {
                Log.e(TAG, "OAuth callback rejected: state mismatch (possible login-CSRF attempt)");
                pendingState = null;
                pendingVerifier = null;
                mainHandler.post(() -> onFailure.onFailure(
                        "Sign-in rejected: the response did not match this device's request"));
                return;
            }
            codeVerifier = pendingVerifier;
            // One-shot: consume the pending flow so a replayed callback cannot be exchanged twice.
            pendingState = null;
            pendingVerifier = null;
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
                        .putString(PREF_USERNAME,   tokens.username)
                        .putLong(PREF_TOKEN_EXPIRY, tokenExpiry)
                        .remove(PREF_CODE_VERIFIER)
                        .apply();
                secureStore.put(PREF_REFRESH_TOKEN, tokens.refreshToken);
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
        String current = accessToken;
        if (current != null && System.currentTimeMillis() < tokenExpiry) {
            return current;
        }
        synchronized (refreshLock) {
            // Re-check inside the lock: another thread may have just refreshed.
            current = accessToken;
            if (current != null && System.currentTimeMillis() < tokenExpiry) return current;

            String refreshToken = secureStore.get(PREF_REFRESH_TOKEN);
            if (refreshToken == null || refreshToken.isEmpty()) return null;
            String portal = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
            try {
                OAuthHelper.OAuthTokens tokens =
                        OAuthHelper.refreshAccessToken(portal, CLIENT_ID, refreshToken);
                if (tokens == null) return null;
                accessToken = tokens.accessToken;
                tokenExpiry = System.currentTimeMillis() + tokens.expiresInSeconds * 1000L;
                SharedPreferences.Editor ed = prefs.edit().putLong(PREF_TOKEN_EXPIRY, tokenExpiry);
                if (tokens.username != null && !tokens.username.isEmpty()) {
                    username = tokens.username;
                    ed.putString(PREF_USERNAME, tokens.username);
                }
                ed.apply();
                if (tokens.refreshToken != null && !tokens.refreshToken.isEmpty()) {
                    secureStore.put(PREF_REFRESH_TOKEN, tokens.refreshToken);
                }
                Log.d(TAG, "Token silently refreshed");
                return accessToken;
            } catch (Exception e) {
                Log.w(TAG, "Silent token refresh failed — re-login required", e);
                return null;
            }
        }
    }

    public boolean isAuthenticated() {
        return username != null;
    }

    public String getUsername() { return username; }

    public void logout() {
        accessToken = null;
        tokenExpiry = 0;
        username    = null;
        cancelOAuthFlow();
        secureStore.remove(PREF_REFRESH_TOKEN);
        prefs.edit()
                .remove(PREF_USERNAME)
                .remove(PREF_REFRESH_TOKEN)
                .remove(PREF_CODE_VERIFIER)
                .remove(PREF_TOKEN_EXPIRY)
                .apply();
        Log.d(TAG, "Logged out");
    }
}
