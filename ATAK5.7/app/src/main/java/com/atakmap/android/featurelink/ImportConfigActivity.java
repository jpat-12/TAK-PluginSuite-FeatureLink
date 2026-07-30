package com.atakmap.android.featurelink;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;

/**
 * Intercepts the featurelink://import?config=... deep link — tapped from the
 * "Open in ATAK" button on TAK Portal's FeatureLink page. The browser already
 * fetched and JSON-encoded the config using the user's own authenticated portal
 * session, so this Activity just has to hand that payload to the plugin; it
 * makes no network call of its own. Broadcasts to FeatureLinkDropDownReceiver,
 * which applies it exactly like a scanned Mode 1/2/3 QR payload.
 *
 * This Activity's own package (the plugin's, com.atakmap.android.featurelink.plugin)
 * is a separate app/process from ATAK itself (com.atakmap.app.civ) even though the
 * plugin's code runs inside ATAK's process once loaded — confirmed via logcat that
 * Chrome launches this Activity correctly (result code=0), but with Theme.NoDisplay
 * and an immediate finish(), nothing tells Android to bring ATAK's own window
 * forward. The broadcast still reaches FeatureLinkDropDownReceiver and applies
 * silently in the background, which looks exactly like "nothing happened" since
 * the screen never leaves the browser. So: explicitly launch ATAK's own task
 * before finishing.
 *
 * This Activity deliberately contains no plugin class references — it only uses
 * standard Android APIs to avoid ClassNotFoundException when launched from the
 * plugin's classloader context (same reasoning as OAuthCallbackActivity).
 */
public class ImportConfigActivity extends Activity {

    // Deliberately duplicated (not referenced) from FeatureLinkDropDownReceiver.IMPORT_CONFIG —
    // see the class comment on why this Activity avoids plugin class references.
    public static final String BROADCAST_ACTION =
            "com.atakmap.android.featurelink.IMPORT_CONFIG";

    // Matches BuildConfig.ATAK_PACKAGE_NAME (app/build.gradle) — hardcoded there the same way
    // for every plugin flavor, so duplicating the literal here (rather than depending on the
    // generated BuildConfig class) stays consistent with that existing convention.
    private static final String ATAK_PACKAGE_NAME = "com.atakmap.app.civ";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Uri data = getIntent().getData();
        String config = data != null ? data.getQueryParameter("config") : null;

        if (config != null && !config.isEmpty()) {
            Intent broadcast = new Intent(BROADCAST_ACTION);
            broadcast.putExtra("config", config);
            sendBroadcast(broadcast);
        }

        Intent atakIntent = getPackageManager().getLaunchIntentForPackage(ATAK_PACKAGE_NAME);
        if (atakIntent != null) {
            atakIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
            startActivity(atakIntent);
        }

        finish();
    }
}
