package com.atakmap.android.featurelink;

import android.content.Context;
import android.content.Intent;

import com.atakmap.android.dropdown.DropDownMapComponent;
import com.atakmap.android.featurelink.radial.FeatureLinkMenuFactory;
import com.atakmap.android.ipc.AtakBroadcast.DocumentedIntentFilter;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.menu.MapMenuReceiver;
import android.util.Log;

public class FeatureLinkMapComponent extends DropDownMapComponent {

    public static final String TAG = "FeatureLinkMapComponent";

    private FeatureLinkDropDownReceiver dropDown;
    private FeatureLinkMenuFactory menuFactory;
    private PliHistoryOverlay pliHistoryOverlay;

    @Override
    public void onCreate(final Context context, Intent intent, final MapView view) {
        context.setTheme(com.atakmap.android.featurelink.plugin.R.style.ATAKPluginTheme);
        super.onCreate(context, intent, view);

        pliHistoryOverlay = new PliHistoryOverlay(view);
        dropDown = new FeatureLinkDropDownReceiver(view, context, pliHistoryOverlay);

        DocumentedIntentFilter filter = new DocumentedIntentFilter();
        filter.addAction(FeatureLinkDropDownReceiver.SHOW_PLUGIN,
                "Show the FeatureLink panel");
        filter.addAction(FeatureLinkDropDownReceiver.SEND_TO_LAYER,
                "Send a map item to the configured ArcGIS Feature Layer");
        registerDropDownReceiver(dropDown, filter);

        menuFactory = new FeatureLinkMenuFactory(view, context);
        MapMenuReceiver.getInstance().registerMapMenuFactory(menuFactory);

        Log.d(TAG, "FeatureLink component created");
    }

    @Override
    protected void onDestroyImpl(Context context, MapView view) {
        Log.d(TAG, "FeatureLink component destroying");
        MapMenuReceiver.getInstance().unregisterMapMenuFactory(menuFactory);
        if (dropDown != null) dropDown.dispose();
        if (pliHistoryOverlay != null) pliHistoryOverlay.dispose();
        super.onDestroyImpl(context, view);
    }
}
