# Esri SSO Integration — Implementation Guide

This document is a self-contained implementation reference. A developer with no prior context on
this conversation can read it top-to-bottom and complete the work.

---

## What This Does

Replaces the current username/password → `generateToken` authentication with OAuth 2.0 +
PKCE so that Esri's own login page handles credentials, MFA/2FA, and enterprise SSO. The plugin
never collects or stores a password again.

ArcGIS OAuth access tokens are used identically to `generateToken` tokens — they are passed as
`&token=<value>` in REST URLs, so every method in `ArcGISRestClient` that makes feature queries
works without any changes.

---

## Architecture Decision: WebView, Not Chrome Custom Tabs

The originally planned approach used Chrome Custom Tabs + a separate `OAuthCallbackActivity` to
catch the OAuth redirect URI. This does not work reliably in ATAK plugins.

**Why:** ATAK loads plugins via `DexClassLoader`. Activities in plugin APKs run outside the
plugin's own classloader context. The SDK source comment in `NotificationService.java` states
explicitly: *"This Service cannot reference anything from ATAK CORE because it is started up by a
classloader that knows nothing about the plugin interface."* The same constraint applies to
Activities. An `OAuthCallbackActivity` therefore cannot call any plugin classes (including
`OAuthHelper` and `ArcGISAuthManager`) without risking `ClassNotFoundException` at runtime.

**The fix:** Embed a `WebView` inside the existing `FeatureLinkDropDownReceiver` on Page 1.
Override `shouldOverrideUrlLoading` to intercept the OAuth redirect before the WebView tries to
navigate to the custom scheme. This runs entirely inside the plugin's existing classloader — no
separate Activity, no custom URI intent filter, no `androidx.browser` dependency.

The helloworld SDK sample (`WebViewDropDownReceiver.java`) is the proven template for this pattern.
Its critical note: **always create the WebView with `mapView.getContext()`, never `pluginContext`.**

---

## One-Time Developer Setup

1. Go to [developers.arcgis.com](https://developers.arcgis.com) and sign in.
2. Create a new Application. Type: **Native Application**.
3. Copy the generated `client_id` — it will be a constant in `ArcGISAuthManager`.
4. Under **Redirect URIs**, add exactly: `featurelink://auth`
5. For ArcGIS Enterprise deployments, repeat this registration in the Portal admin under
   **Organization → Security → OAuth2 Applications**.

No `client_secret` is needed. PKCE replaces the secret for native apps.

---

## Files Changed / Not Changed

| File | Action |
|---|---|
| `arcgis/OAuthHelper.java` | **CREATE** — PKCE + token exchange + refresh |
| `arcgis/ArcGISAuthManager.java` | **REWRITE** — drop password auth, add OAuth flow |
| `arcgis/ArcGISRestClient.java` | **EDIT** — delete `generateToken()` only |
| `FeatureLinkDropDownReceiver.java` | **EDIT** — add WebView, rewire login section, fix QR |
| `QrHelper.java` | **EDIT** — remove `PASS` field, update payload/parse |
| `res/layout/page_private.xml` | **EDIT** — remove credential fields, add SSO button |
| `AndroidManifest.xml` | **NO CHANGE** — no new Activity or intent filter needed |
| `build.gradle` | **NO CHANGE** — no new dependencies needed |

---

## Step 1 — Create `arcgis/OAuthHelper.java`

Create this file from scratch. It is pure Java with no ATAK dependencies — only standard Android
(`android.util.Base64`, `android.net.Uri`) and `java.*`. This keeps it safe to call from anywhere
in the plugin.

### Package and imports

```java
package com.atakmap.android.featurelink.arcgis;

import android.net.Uri;
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
```

### Inner class: `OAuthTokens`

A simple data holder returned by the exchange and refresh methods:

```java
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
```

### Method: `generatePkce()`

Returns a two-element String array: `[0] = codeVerifier`, `[1] = codeChallenge`.

```java
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
```

### Method: `buildAuthUrl()`

Constructs the URL that will be loaded into the WebView.

```java
public static String buildAuthUrl(String portalUrl, String clientId, String codeChallenge) {
    return normalizePortal(portalUrl)
            + "/sharing/rest/oauth2/authorize"
            + "?client_id="             + enc(clientId)
            + "&response_type=code"
            + "&redirect_uri="          + enc("featurelink://auth")
            + "&code_challenge="        + enc(codeChallenge)
            + "&code_challenge_method=S256";
}
```

### Method: `exchangeCode()`

Called after the WebView intercepts the redirect and extracts the authorization code.
Must be called on a background thread (it does HTTP).

```java
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
```

### Method: `refreshAccessToken()`

Called silently when the access token expires. No browser interaction needed.
Must be called on a background thread.

```java
public static OAuthTokens refreshAccessToken(String portalUrl, String clientId,
                                              String refreshToken) throws Exception {
    String endpoint = normalizePortal(portalUrl) + "/sharing/rest/oauth2/token";
    String body = "grant_type=refresh_token"
            + "&client_id="      + enc(clientId)
            + "&refresh_token="  + enc(refreshToken);
    return postForTokens(endpoint, body);
}
```

### Private helpers

`postForTokens()` — shared POST logic for both exchange and refresh:

```java
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
        throw new Exception("OAuth error: " + json.optString("error_description", json.optString("error")));
    }
    return new OAuthTokens(
            json.getString("access_token"),
            json.optString("refresh_token", null),
            json.optString("username", null),
            json.optLong("expires_in", 3600)
    );
}
```

`normalizePortal()` — strips trailing slash:

```java
private static String normalizePortal(String url) {
    return url == null ? "https://www.arcgis.com" : url.replaceAll("/+$", "");
}
```

`enc()` — URL encoding shorthand:

```java
private static String enc(String s) {
    try { return URLEncoder.encode(s, "UTF-8"); }
    catch (Exception e) { return s; }
}
```

---

## Step 2 — Rewrite `arcgis/ArcGISAuthManager.java`

Replace the entire file. Keep the same class name, package, and constructor signature so that
`FeatureLinkDropDownReceiver` needs no constructor change.

### Constants

```java
// Register at developers.arcgis.com — paste your client_id here
private static final String CLIENT_ID       = "YOUR_CLIENT_ID_HERE";
private static final String CREDENTIALS_KEY = "featurelink.arcgis";
private static final String PREF_CODE_VERIFIER = "oauth_code_verifier";
private static final String PREF_PORTAL_URL    = "portal_url";
private static final String PREF_TOKEN_EXPIRY  = "oauth_token_expiry";
```

### Fields

```java
private final SharedPreferences prefs;
private String accessToken  = null;
private String username     = null;
private long   tokenExpiry  = 0;
```

The `restClient` field is no longer needed — delete it. The constructor no longer takes
`pluginContext` for anything meaningful but keep the signature identical so callers don't break:
`ArcGISAuthManager(Context pluginContext, Context appContext, SharedPreferences prefs)`.

On construction, restore persisted username from `AtakAuthenticationDatabase` exactly as before.

### Storage layout

The existing `AtakAuthenticationDatabase` entry (`CREDENTIALS_KEY = "featurelink.arcgis"`) is
repurposed:

| `AtakAuthenticationCredentials` field | Now stores |
|---|---|
| `username` | ArcGIS username (unchanged) |
| `password` | OAuth **refresh token** (was password) |

The access token and expiry timestamp go in `SharedPreferences` — they are short-lived and
acceptable to store there.

### Method: `startOAuthFlow()`

Called from the UI thread in `FeatureLinkDropDownReceiver` when the user taps "Sign in with
ArcGIS". Returns the authorization URL that should be loaded into the WebView, and saves the
`codeVerifier` to SharedPreferences for retrieval after the redirect.

```java
public String startOAuthFlow(String portalUrl) {
    try {
        String[] pkce = OAuthHelper.generatePkce();
        String codeVerifier  = pkce[0];
        String codeChallenge = pkce[1];
        prefs.edit()
                .putString(PREF_CODE_VERIFIER, codeVerifier)
                .putString(PREF_PORTAL_URL, portalUrl)
                .apply();
        return OAuthHelper.buildAuthUrl(portalUrl, CLIENT_ID, codeChallenge);
    } catch (Exception e) {
        Log.e(TAG, "Failed to build auth URL", e);
        return null;
    }
}
```

### Method: `handleAuthCode()`

Called from the WebView intercept (still on the main thread at that point). Kicks the token
exchange onto the background executor passed in, then calls back on the main thread via the
provided `Runnable`s. This pattern matches what the rest of the drop-down already does.

```java
public void handleAuthCode(String code,
                            java.util.concurrent.ExecutorService executor,
                            android.os.Handler mainHandler,
                            Runnable onSuccess,
                            java.util.function.Consumer<String> onFailure) {
    String portalUrl     = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
    String codeVerifier  = prefs.getString(PREF_CODE_VERIFIER, null);
    prefs.edit().remove(PREF_CODE_VERIFIER).apply(); // single use

    if (codeVerifier == null) {
        mainHandler.post(() -> onFailure.accept("Missing code verifier — try signing in again"));
        return;
    }
    executor.submit(() -> {
        try {
            OAuthHelper.OAuthTokens tokens =
                    OAuthHelper.exchangeCode(portalUrl, CLIENT_ID, code, codeVerifier);
            storeTokens(tokens);
            mainHandler.post(onSuccess);
        } catch (Exception e) {
            Log.e(TAG, "Token exchange failed", e);
            mainHandler.post(() -> onFailure.accept(e.getMessage()));
        }
    });
}
```

### Method: `storeTokens()` (private)

```java
private void storeTokens(OAuthHelper.OAuthTokens tokens) {
    this.accessToken = tokens.accessToken;
    this.tokenExpiry = System.currentTimeMillis() + (tokens.expiresInSeconds * 1000L);
    this.username    = tokens.username;
    // Store refresh token in encrypted database (reuses existing infrastructure)
    AtakAuthenticationDatabase.saveCredentials(
            CREDENTIALS_KEY, "", tokens.username,
            tokens.refreshToken != null ? tokens.refreshToken : "", false);
    prefs.edit().putLong(PREF_TOKEN_EXPIRY, tokenExpiry).apply();
}
```

### Method: `getToken()`

Same contract as before — returns a valid token or null. Now uses refresh token instead of
re-sending password. Must be called on a background thread when refresh may be needed.

```java
public String getToken() {
    if (accessToken != null && System.currentTimeMillis() < tokenExpiry) {
        return accessToken;
    }
    // Attempt silent refresh
    AtakAuthenticationCredentials creds =
            AtakAuthenticationDatabase.getCredentials(CREDENTIALS_KEY, "");
    if (creds == null || creds.password == null || creds.password.isEmpty()) return null;

    String portalUrl = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
    try {
        OAuthHelper.OAuthTokens tokens =
                OAuthHelper.refreshAccessToken(portalUrl, CLIENT_ID, creds.password);
        storeTokens(tokens);
        return accessToken;
    } catch (Exception e) {
        Log.e(TAG, "Silent token refresh failed — re-login required", e);
        return null; // caller must show re-login prompt
    }
}
```

### Method: `logout()`

Clear in-memory state and delete from the database. Optionally attempt token revocation
(fire-and-forget on a new thread — don't block UI):

```java
public void logout() {
    String portal        = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
    AtakAuthenticationCredentials creds =
            AtakAuthenticationDatabase.getCredentials(CREDENTIALS_KEY, "");
    // Best-effort server-side revocation
    if (creds != null && creds.password != null && !creds.password.isEmpty()) {
        final String rt = creds.password;
        new Thread(() -> {
            try {
                // POST {portal}/sharing/rest/oauth2/revokeToken
                // body: token={rt}&client_id={CLIENT_ID}&f=json
                // Ignore response — local state is cleared regardless
            } catch (Exception ignored) {}
        }).start();
    }
    accessToken = null;
    tokenExpiry = 0;
    username    = null;
    AtakAuthenticationDatabase.delete(CREDENTIALS_KEY, "");
    Log.d(TAG, "Logged out");
}
```

### Methods to keep unchanged

- `isAuthenticated()` — same logic, just check `username != null`
- `getUsername()` — unchanged

### Methods to delete entirely

- `authenticate(String portalUrl, String user, String password)`
- `getPassword()`
- `hasSavedCredentials()` (private) — inline the check into `isAuthenticated()` if needed

---

## Step 3 — Edit `arcgis/ArcGISRestClient.java`

Delete the `generateToken()` method (lines 47–65 in the current file). That is the only change.

All other methods (`searchUserLayers`, `queryFeatureCount`, `downloadFeatures`, `applyEdits`,
`createFeatureService`, `fetchLayerInfo`, and the HTTP helpers) remain untouched — they all
receive a `token` parameter and use it as a URL query parameter, which works identically for
OAuth tokens.

---

## Step 4 — Edit `res/layout/page_private.xml`

### Remove these views

Remove the three credential input fields and their labels:

- `R.id.portal_url_edit` (`portalUrlEdit` EditText)
- `R.id.username_edit` (`usernameEdit` EditText)
- `R.id.password_edit` (`passwordEdit` EditText)
- Any corresponding `TextView` labels for those fields
- `R.id.login_btn` (`loginBtn` Button — the old username/password submit button)

### Add these views

Add a portal URL entry (keep this — enterprise users need it):

```xml
<EditText
    android:id="@+id/portal_url_edit"
    android:hint="Portal URL (default: https://www.arcgis.com)"
    android:inputType="textUri"
    android:singleLine="true" />
```

Add the SSO sign-in button:

```xml
<Button
    android:id="@+id/sso_login_btn"
    android:text="Sign in with ArcGIS" />
```

Keep these views exactly as they are:

- `R.id.logout_btn`
- `R.id.auth_status_text`
- `R.id.layers_section` and everything inside it
- `R.id.pli_section` and everything inside it

### Add a WebView container

Add a `FrameLayout` that will host the OAuth WebView. It starts hidden and slides in when the
user taps "Sign in with ArcGIS". Give it `match_parent` width and height so it covers the whole
page panel:

```xml
<FrameLayout
    android:id="@+id/oauth_webview_container"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:visibility="gone"
    android:background="@android:color/background_light" />
```

Place this FrameLayout as the last child of the page's root layout so it renders on top of
everything when visible.

---

## Step 5 — Edit `FeatureLinkDropDownReceiver.java`

This is the largest change. Work through it section by section.

### Fields to remove

```java
// DELETE these three field declarations
private EditText portalUrlEdit, usernameEdit, passwordEdit;
private Button loginBtn;
```

### Fields to add

```java
private EditText portalUrlEdit;        // keep — still need portal URL for enterprise
private Button ssoLoginBtn;
private android.widget.FrameLayout oauthWebViewContainer;
private android.webkit.WebView oauthWebView;
```

Also add this import at the top:

```java
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.net.Uri;
```

### `wirePrivatePageViews()` — update

Replace the three removed `findViewById` calls and the `loginBtn` wiring with:

```java
// Keep portal URL field
portalUrlEdit = privatePageView.findViewById(R.id.portal_url_edit);
String savedPortal = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
portalUrlEdit.setText(savedPortal);

// New SSO button
ssoLoginBtn = privatePageView.findViewById(R.id.sso_login_btn);
ssoLoginBtn.setOnClickListener(v -> performSsoLogin());

// WebView container
oauthWebViewContainer = privatePageView.findViewById(R.id.oauth_webview_container);
```

Delete `loginBtn.setOnClickListener(v -> performLogin())` and the `usernameEdit`/`passwordEdit`
finder lines.

Keep all other wiring unchanged (logoutBtn, refreshLayersBtn, pliShareQrBtn, pliScanQrBtn,
pliRadioGroup, pliActionBtn, pliAutoSendCheckbox, uploadPrefBtn).

### `syncPrivatePageAuthState()` — update

Remove the lines that enable/disable `usernameEdit` and `passwordEdit` (those views no longer
exist). Remove the `loginBtn.setVisibility()` line. Replace with:

```java
private void syncPrivatePageAuthState() {
    boolean authed = authManager.isAuthenticated();
    ssoLoginBtn.setVisibility(authed ? View.GONE    : View.VISIBLE);
    logoutBtn.setVisibility(  authed ? View.VISIBLE : View.GONE);
    portalUrlEdit.setEnabled(!authed);
    layersSection.setVisibility(authed ? View.VISIBLE : View.GONE);
    pliSection.setVisibility(   authed ? View.VISIBLE : View.GONE);

    if (authed) {
        authStatusText.setText("Signed in as: " + authManager.getUsername());
        authStatusText.setTextColor(0xFF4CAF50);
        pliLayerUrl = prefs.getString(PREF_PLI_LAYER_URL, null);
        if (pliLayerUrl != null) {
            pliLayerUrlEdit.setText(pliLayerUrl);
            pliStatusText.setText("PLI layer configured: " + pliLayerUrl);
        }
        pliAutoSendCheckbox.setChecked(prefs.getBoolean(PREF_PLI_AUTO_SEND, false));
        refreshPrivateLayerList();
    } else {
        authStatusText.setText("Not signed in");
        authStatusText.setTextColor(0xFFFF5722);
    }
    updateQrShareButton();
}
```

### New method: `performSsoLogin()`

```java
private void performSsoLogin() {
    String portal = portalUrlEdit.getText().toString().trim();
    if (portal.isEmpty()) portal = "https://www.arcgis.com";

    String authUrl = authManager.startOAuthFlow(portal);
    if (authUrl == null) {
        Toast.makeText(pluginContext, "Failed to start sign-in", Toast.LENGTH_SHORT).show();
        return;
    }

    // Create the WebView using the host Activity context (mapView.getContext()), NOT pluginContext.
    // This is required in ATAK plugins — see helloworld WebViewDropDownReceiver.java.
    oauthWebView = new WebView(getMapView().getContext());
    oauthWebView.getSettings().setJavaScriptEnabled(true);
    oauthWebView.getSettings().setDomStorageEnabled(true);

    oauthWebView.setWebViewClient(new WebViewClient() {
        @Override
        public boolean shouldOverrideUrlLoading(android.webkit.WebView view, String url) {
            if (url.startsWith("featurelink://auth")) {
                handleOAuthRedirect(Uri.parse(url));
                return true; // prevent WebView from navigating to this URL
            }
            return false; // let Esri's own redirects (SAML etc.) load normally
        }
    });

    oauthWebViewContainer.removeAllViews();
    oauthWebViewContainer.addView(oauthWebView,
            new android.widget.FrameLayout.LayoutParams(
                    android.widget.FrameLayout.LayoutParams.MATCH_PARENT,
                    android.widget.FrameLayout.LayoutParams.MATCH_PARENT));
    oauthWebViewContainer.setVisibility(View.VISIBLE);

    authStatusText.setText("Opening sign-in…");
    oauthWebView.loadUrl(authUrl);
}
```

### New method: `handleOAuthRedirect()`

```java
private void handleOAuthRedirect(Uri redirectUri) {
    String error = redirectUri.getQueryParameter("error");
    if (error != null) {
        hideOAuthWebView();
        String desc = redirectUri.getQueryParameter("error_description");
        authStatusText.setText("Sign-in failed: " + (desc != null ? desc : error));
        authStatusText.setTextColor(0xFFFF5722);
        return;
    }

    String code = redirectUri.getQueryParameter("code");
    if (code == null) {
        hideOAuthWebView();
        authStatusText.setText("Sign-in failed: no code returned");
        authStatusText.setTextColor(0xFFFF5722);
        return;
    }

    authStatusText.setText("Completing sign-in…");

    authManager.handleAuthCode(code, executor, mainHandler,
            /* onSuccess */ () -> {
                hideOAuthWebView();
                prefs.edit().putString(PREF_PORTAL_URL,
                        portalUrlEdit.getText().toString().trim()).apply();
                syncPrivatePageAuthState();
                fetchUserLayers();
            },
            /* onFailure */ message -> {
                hideOAuthWebView();
                authStatusText.setText("Sign-in failed: " + message);
                authStatusText.setTextColor(0xFFFF5722);
            }
    );
}
```

### New private helper: `hideOAuthWebView()`

```java
private void hideOAuthWebView() {
    oauthWebViewContainer.setVisibility(View.GONE);
    if (oauthWebView != null) {
        oauthWebView.stopLoading();
        oauthWebViewContainer.removeAllViews();
        oauthWebView.destroy();
        oauthWebView = null;
    }
}
```

### Delete `performLogin()` entirely

The method `performLogin()` (lines 337–366 in the current file) calls
`authManager.authenticate(portal, user, pass)` which no longer exists. Delete the whole method.

### Update `showQrCodeDialog()`

The old implementation calls `authManager.getPassword()` (line 475) which is deleted. The QR
payload no longer contains credentials — it shares only the layer URL (the receiving device must
sign in independently). Replace `showQrCodeDialog()` with:

```java
private void showQrCodeDialog() {
    if (pliLayerUrl == null || pliLayerUrl.isEmpty()) {
        Toast.makeText(pluginContext, "Configure a PLI layer first", Toast.LENGTH_SHORT).show();
        return;
    }
    String portal    = prefs.getString(PREF_PORTAL_URL, "https://www.arcgis.com");
    String layerName = pliLayerNameEdit.getText().toString().trim();
    final String url = pliLayerUrl;

    executor.submit(() -> {
        try {
            String json = QrHelper.buildPayload(portal, url, layerName);
            Bitmap qr   = QrHelper.generateBitmap(json, 512);
            mainHandler.post(() -> displayQrDialog(qr, json));
        } catch (Exception e) {
            Log.e(TAG, "QR generation failed", e);
            mainHandler.post(() -> Toast.makeText(pluginContext,
                    "Failed to generate QR code", Toast.LENGTH_SHORT).show());
        }
    });
}
```

### Update `applyScannedConfig()`

The scanned QR no longer contains `user` or `pass` fields. Update accordingly — pre-fill portal
and layer URL, then show the SSO button instead of auto-logging in with credentials:

```java
private void applyScannedConfig(JSONObject config) {
    String portal = config.optString(QrHelper.PORTAL, "https://www.arcgis.com");
    String url    = config.optString(QrHelper.URL,    "");
    String name   = config.optString(QrHelper.NAME,   "");

    navigatePage(1);

    portalUrlEdit.setText(portal);
    pliLayerUrlEdit.setText(url);
    if (!name.isEmpty()) pliLayerNameEdit.setText(name);

    joinLayerRb.setChecked(true);

    pliLayerUrl = url;
    prefs.edit()
            .putString(PREF_PLI_LAYER_URL, url)
            .putString(PREF_PORTAL_URL,    portal)
            .apply();

    Toast.makeText(pluginContext,
            "Layer config loaded — tap 'Sign in with ArcGIS' to continue",
            Toast.LENGTH_LONG).show();
    // Do NOT call performLogin() — user must sign in via SSO
}
```

### No other changes needed

`performLogout()`, `fetchUserLayers()`, `refreshHomeStats()`, `sendPliUpdate()`, and all public
page logic are unchanged. They all call `authManager.getToken()` which still works.

---

## Step 6 — Edit `QrHelper.java`

The QR payload no longer carries credentials. Make the following edits:

### Remove the `PASS` constant

```java
// DELETE this line:
static final String PASS = "pass";
```

### Remove the `USER` constant

```java
// DELETE this line:
static final String USER = "user";
```

### Rewrite `buildPayload()`

Old signature: `buildPayload(String portal, String user, String pass, String url, String name)`
New signature: `buildPayload(String portal, String url, String name)`

```java
static String buildPayload(String portal, String url, String name) throws Exception {
    return new JSONObject()
            .put(V,      1)
            .put(PORTAL, portal != null ? portal : "https://www.arcgis.com")
            .put(URL,    url    != null ? url    : "")
            .put(NAME,   name   != null ? name   : "")
            .toString();
}
```

### Rewrite `parse()`

Old validation required `USER` to be non-empty. New validation only requires `URL`:

```java
static JSONObject parse(String json) {
    try {
        JSONObject o = new JSONObject(json.trim());
        if (o.optInt(V, 0) == 1 && !o.optString(URL, "").isEmpty()) {
            return o;
        }
    } catch (Exception ignored) {}
    return null;
}
```

---

## Complete Auth Flow (for reference)

```
User taps "Sign in with ArcGIS"
        │
        ▼
performSsoLogin()
  authManager.startOAuthFlow(portalUrl)
    → generatePkce() — codeVerifier saved to SharedPreferences
    → buildAuthUrl() — returns full authorization URL
  WebView created with mapView.getContext()
  oauthWebViewContainer.setVisibility(VISIBLE)
  oauthWebView.loadUrl(authorizationUrl)
        │
        ▼
[Esri's login page renders in the WebView]
  User enters credentials, Esri challenges MFA if configured,
  SAML/enterprise SSO redirect chain happens transparently —
  the plugin does nothing during this phase
        │
        ▼  Esri redirects to: featurelink://auth?code=<authCode>
        │
        ▼
shouldOverrideUrlLoading() fires — returns true (blocks navigation)
handleOAuthRedirect(Uri) called on main thread
  reads code from Uri
        │
        ▼
authManager.handleAuthCode(code, executor, mainHandler, ...)
  retrieves codeVerifier from SharedPreferences
  clears codeVerifier immediately (single-use)
  background thread:
    OAuthHelper.exchangeCode(portal, CLIENT_ID, code, codeVerifier)
      POST {portal}/sharing/rest/oauth2/token
        │
        ▼
  Esri responds:
    { "access_token": "...",   ← stored in memory
      "refresh_token": "...",  ← stored in AtakAuthenticationDatabase (encrypted)
      "expires_in": 3600,
      "username": "jsmith" }
        │
        ▼  main thread callback
hideOAuthWebView()
syncPrivatePageAuthState() → shows "Signed in as: jsmith"
fetchUserLayers()

──────── LATER: access token expires (1 hour) ────────

authManager.getToken() detects expiry
  loads refresh_token from AtakAuthenticationDatabase
  background thread: OAuthHelper.refreshAccessToken(...)
  new access_token stored in memory
  all API calls (PLI auto-send, queries) continue transparently

──────── LATER: refresh token expires (~2 weeks) ────────

getToken() throws → returns null
caller (e.g. sendPliUpdate) sees null token
  (add a null-check in sendPliUpdate and show a Toast:
   "Session expired — open FeatureLink and sign in again")
user taps "Sign in with ArcGIS" → full flow repeats
```

---

## Null Token Handling in `sendPliUpdate()`

After the refresh token expires, `authManager.getToken()` returns null. The current
`sendPliUpdate()` at line 650 calls `getToken()` but does not check the result before using it.
Add an explicit check:

```java
private void sendPliUpdate() {
    if (pliLayerUrl == null || pliLayerUrl.isEmpty()) return;
    String token = authManager.getToken();
    if (token == null) {
        // Surface a notification so the user knows to re-authenticate.
        // Don't show a Toast here — this runs on a background thread.
        Log.w(TAG, "PLI auto-send skipped: session expired, re-login required");
        return;
    }
    // ... rest of method unchanged
}
```

---

## Testing Checklist

- [ ] "Sign in with ArcGIS" opens the WebView and Esri's login page loads
- [ ] MFA prompt appears and completes inside the WebView (no plugin involvement)
- [ ] After successful login, WebView hides and "Signed in as: username" shows
- [ ] Layer list loads after sign-in
- [ ] Logout clears state and shows SSO button again
- [ ] PLI auto-send continues after sign-in
- [ ] After 1 hour, the next API call silently refreshes (check logs for no re-login prompt)
- [ ] After simulating refresh token expiry (delete from AtakAuthenticationDatabase manually
      in debug), `getToken()` returns null and a log warning appears
- [ ] QR code dialog no longer shows or encodes any credentials
- [ ] Scanned QR from another device pre-fills portal + layer URL and shows SSO prompt
- [ ] ArcGIS Enterprise portal URL works (change portal URL EditText, sign in)
- [ ] Back button / swipe away from Page 1 while WebView is open calls `hideOAuthWebView()`
      (add a call in `onDropDownClose` or `onDropDownSizeChanged` if the WebView persists)

---

## Notes for the Implementing Agent

- `CLIENT_ID` in `ArcGISAuthManager` must be replaced with the real value from
  developers.arcgis.com before testing. The build will succeed without it but auth will fail.
- `java.util.function.Consumer` is available on Android API 24+. If the minimum SDK is lower,
  replace it with a simple interface: `interface FailureCallback { void onFailure(String msg); }`
- The WebView **must** be constructed with `getMapView().getContext()` — not `pluginContext`.
  This is the single most common mistake in ATAK plugin WebView usage and will cause a crash if
  done wrong. The helloworld SDK sample (`WebViewDropDownReceiver.java`, line 51) documents this.
- Do not add `OAuthCallbackActivity` or any intent filter to the manifest. The WebView approach
  makes both unnecessary.
- Do not add `androidx.browser` to `build.gradle`. It is not used.
