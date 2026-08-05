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

    /**
     * Renew this far ahead of the advertised expiry rather than waiting for it to lapse. ArcGIS
     * access tokens are typically issued for 30 minutes, so five minutes is a generous margin that
     * still leaves the overwhelming majority of the token's life in use.
     */
    private static final long EXPIRY_MARGIN_MS = 5 * 60 * 1000L;

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
                // Guarded: put(null) *removes* the entry, so an exchange that returned no refresh
                // token would silently wipe a good one and leave no trace. Log either way — whether
                // the portal issues refresh tokens at all is the first thing worth knowing when a
                // session will not persist.
                if (tokens.refreshToken != null && !tokens.refreshToken.isEmpty()) {
                    secureStore.put(PREF_REFRESH_TOKEN, tokens.refreshToken);
                } else {
                    Log.w(TAG, "token exchange returned NO refresh_token — the session cannot be"
                            + " renewed silently and will require re-login when it expires");
                }
                Log.d(TAG, "OAuth authenticated as: " + tokens.username
                        + " (expires_in=" + tokens.expiresInSeconds + "s, refreshToken="
                        + (tokens.refreshToken != null && !tokens.refreshToken.isEmpty()) + ")");
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
        if (current != null && !isExpiringSoon()) {
            return current;
        }
        // A refresh is a network call, and on the main thread that throws
        // NetworkOnMainThreadException. That exception was caught below and turned into a `null`
        // return, so the caller issued an UNAUTHENTICATED request and ArcGIS answered 499 — which
        // reads to the operator as "your session expired" no matter how many times they sign in.
        // Combined with invalidateAccessToken() forcing a refresh after every 499, that became a
        // permanent loop. Never attempt the network here; hand back whatever is cached (possibly
        // null) and let the caller retry from a background thread.
        if (android.os.Looper.myLooper() == android.os.Looper.getMainLooper()) {
            Log.e(TAG, "getToken() called on the main thread — cannot refresh here."
                    + " Move the call inside the background task (returning cached token: "
                    + (current != null) + ")");
            return current;
        }
        synchronized (refreshLock) {
            // Re-check inside the lock: another thread may have just refreshed.
            current = accessToken;
            if (current != null && !isExpiringSoon()) return current;

            String refreshToken = secureStore.get(PREF_REFRESH_TOKEN);
            if (refreshToken == null || refreshToken.isEmpty()) {
                // Previously returned null silently, which surfaced to the operator as a bare
                // "session no longer valid" with nothing in the log to say why. If this fires,
                // the portal never issued a refresh token (or it was discarded) and no amount of
                // retrying will help — only a fresh sign-in will.
                Log.w(TAG, "no refresh token stored — cannot refresh silently, re-login required");
                return null;
            }
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

    /**
     * True when the cached access token is gone, already expired, or close enough to expiry that a
     * request issued now could arrive after the server considers it dead.
     *
     * <p>The margin matters: previously the check was a bare {@code now < tokenExpiry}, so the
     * token was only renewed <em>after</em> it had already lapsed. Any request in flight near the
     * boundary, any device clock drift, and any portal that expires a token marginally earlier than
     * the {@code expires_in} it advertised all produced a 499 that nothing recovered from.
     */
    private boolean isExpiringSoon() {
        return System.currentTimeMillis() + EXPIRY_MARGIN_MS >= tokenExpiry;
    }

    /**
     * Marks the cached access token unusable without touching the refresh token, so the next
     * {@link #getToken()} performs a silent refresh instead of handing back a token the server has
     * already rejected.
     *
     * <p>Call this whenever ArcGIS answers 498/499. The expiry clock is only ever an
     * <em>estimate</em> derived from {@code expires_in} at issue time; the server is the authority,
     * and a 499 is the server telling us our estimate is wrong. Without this the plugin would keep
     * presenting the same dead token until its own clock caught up, which is what made the session
     * look like it "kept logging out".
     */
    public void invalidateAccessToken() {
        synchronized (refreshLock) {
            accessToken = null;
            tokenExpiry = 0;
        }
        Log.d(TAG, "access token invalidated by server rejection — will refresh on next use");
    }

    /** Portal this session is signed in to, defaulting to ArcGIS Online. */
    public String getPortalUrl() {
        return prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
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
