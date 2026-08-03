package com.atakmap.android.featurelink;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.util.Log;

/**
 * Intercepts the featurelink://auth redirect from the browser after Esri SSO.
 * Extracts the auth code (or error) and broadcasts it to FeatureLinkDropDownReceiver,
 * then immediately finishes so ATAK returns to the foreground.
 *
 * <p>C-09 hardening:
 * <ul>
 *   <li>The {@code state} nonce is forwarded and validated by
 *       {@code ArcGISAuthManager.handleAuthCode} against the in-memory pending flow. A callback
 *       with no state, or a state that does not match, is rejected — this is what stops any
 *       installed app invoking {@code featurelink://auth?code=ATTACKER_CODE} and binding the
 *       operator's session to the attacker's ArcGIS org.</li>
 *   <li>The broadcast is no longer implicit. It is restricted to ATAK's own package
 *       <b>and</b> requires a signature-level permission this plugin defines, so it is
 *       unreadable by any other app on the device.</li>
 *   <li>A callback carrying neither {@code code} nor {@code error} is dropped here rather than
 *       waking the plugin.</li>
 * </ul>
 *
 * This Activity deliberately contains no plugin class references — it only uses
 * standard Android APIs to avoid ClassNotFoundException when launched from the
 * plugin's classloader context.
 */
public class OAuthCallbackActivity extends Activity {

    private static final String TAG = "FeatureLink.OAuthCallback";

    public static final String BROADCAST_ACTION =
            "com.atakmap.android.featurelink.OAUTH_CALLBACK";

    /** Signature-level permission declared in this plugin's AndroidManifest. Only code signed
     * with the plugin's own key holds it, so no third-party app can receive this broadcast. */
    public static final String INTERNAL_PERMISSION =
            "com.atakmap.android.featurelink.permission.INTERNAL_BROADCAST";

    /** Matches BuildConfig.ATAK_PACKAGE_NAME — see ImportConfigActivity for why the literal is
     * duplicated rather than referenced. */
    private static final String ATAK_PACKAGE_NAME = "com.atakmap.app.civ";

    /** An OAuth authorization code is short; anything longer is not one. */
    private static final int MAX_PARAM_LENGTH = 4096;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Uri data = getIntent().getData();
        if (data == null) {
            finish();
            return;
        }

        String code      = capped(safeParam(data, "code"));
        String error     = capped(safeParam(data, "error"));
        String errorDesc = capped(safeParam(data, "error_description"));
        String state     = capped(safeParam(data, "state"));

        if (code == null && error == null) {
            Log.w(TAG, "Ignoring featurelink://auth callback with neither code nor error");
            finish();
            return;
        }

        Intent broadcast = new Intent(BROADCAST_ACTION);
        broadcast.setPackage(ATAK_PACKAGE_NAME);
        if (code != null)  broadcast.putExtra("code",  code);
        if (error != null) broadcast.putExtra("error", errorDesc != null ? errorDesc : error);
        if (state != null) broadcast.putExtra("state", state);
        sendBroadcast(broadcast, INTERNAL_PERMISSION);

        finish();
    }

    /** {@code getQueryParameter} throws on a malformed opaque URI; a hostile deep link must not
     * crash the plugin's own activity. */
    private static String safeParam(Uri uri, String key) {
        try {
            return uri.getQueryParameter(key);
        } catch (Exception e) {
            return null;
        }
    }

    private static String capped(String s) {
        if (s == null) return null;
        return s.length() > MAX_PARAM_LENGTH ? s.substring(0, MAX_PARAM_LENGTH) : s;
    }
}
