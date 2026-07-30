package com.atakmap.android.featurelink;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;

/**
 * Intercepts the featurelink://auth redirect from the browser after Esri SSO.
 * Extracts the auth code (or error) and broadcasts it to FeatureLinkDropDownReceiver,
 * then immediately finishes so ATAK returns to the foreground.
 *
 * This Activity deliberately contains no plugin class references — it only uses
 * standard Android APIs to avoid ClassNotFoundException when launched from the
 * plugin's classloader context.
 */
public class OAuthCallbackActivity extends Activity {

    public static final String BROADCAST_ACTION =
            "com.atakmap.android.featurelink.OAUTH_CALLBACK";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Uri data = getIntent().getData();
        if (data != null) {
            Intent broadcast = new Intent(BROADCAST_ACTION);
            String code      = data.getQueryParameter("code");
            String error     = data.getQueryParameter("error");
            String errorDesc = data.getQueryParameter("error_description");

            if (code != null)  broadcast.putExtra("code",  code);
            if (error != null) broadcast.putExtra("error", errorDesc != null ? errorDesc : error);

            sendBroadcast(broadcast);
        }

        finish();
    }
}
