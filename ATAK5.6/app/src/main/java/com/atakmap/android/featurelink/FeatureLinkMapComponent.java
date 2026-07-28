package com.atakmap.android.featurelink;

import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Build;

import com.atakmap.android.dropdown.DropDownMapComponent;
import com.atakmap.android.featurelink.radial.FeatureLinkMenuFactory;
import com.atakmap.android.importexport.ImportExportMapComponent;
import com.atakmap.android.importexport.ImporterManager;
import com.atakmap.android.importexport.MarshalManager;
import com.atakmap.android.importfiles.sort.ImportInPlaceResolver;
import com.atakmap.android.importfiles.task.ImportFilesTask;
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
        filter.addAction(FeatureLinkDropDownReceiver.IMPORT_CONFIG,
                "Import a FeatureLink display config from the featurelink://import deep link");
        registerDropDownReceiver(dropDown, filter);

        // IMPORT_CONFIG also needs a genuine cross-process registration: it's sent via
        // Context.sendBroadcast() from ImportConfigActivity, which runs in this plugin's own
        // separate app process (com.atakmap.android.featurelink.plugin), not inside ATAK's
        // process — registerDropDownReceiver()/AtakBroadcast above is same-process-only (it
        // exists so other apps on the device can't spoof internal ATAK/plugin events), so it
        // can never see a broadcast sent from a different process no matter what. Confirmed via
        // logcat: ImportConfigActivity launches and runs correctly every time, but onReceive()
        // never logs anything for IMPORT_CONFIG without this second registration.
        IntentFilter importConfigFilter = new IntentFilter(FeatureLinkDropDownReceiver.IMPORT_CONFIG);
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(dropDown, importConfigFilter, Context.RECEIVER_EXPORTED);
        } else {
            context.registerReceiver(dropDown, importConfigFilter);
        }

        menuFactory = new FeatureLinkMenuFactory(view, context);
        MapMenuReceiver.getInstance().registerMapMenuFactory(menuFactory);

        // Lets an incoming Mission Package containing a shared layer config (see
        // FeatureLinkDropDownReceiver.sendLayerShare()) get automatically applied once accepted,
        // instead of requiring a manual "Upload Pref File" pick — same pattern as the SDK's
        // importexportexample sample (ExFmtMarshal/ExFmtImporter/ImportInPlaceResolver).
        FeatureLinkImporter.receiver = dropDown;
        ImporterManager.registerImporter(FeatureLinkImporter.INSTANCE);
        MarshalManager.registerMarshal(FeatureLinkMarshal.INSTANCE);
        ImportExportMapComponent.getInstance().addImporterClass(
                ImportInPlaceResolver.fromMarshal(FeatureLinkMarshal.INSTANCE));
        ImportFilesTask.registerExtension(".featurelink.json");

        Log.d(TAG, "FeatureLink component created");
    }

    @Override
    protected void onDestroyImpl(Context context, MapView view) {
        Log.d(TAG, "FeatureLink component destroying");
        ImporterManager.unregisterImporter(FeatureLinkImporter.INSTANCE);
        MarshalManager.unregisterMarshal(FeatureLinkMarshal.INSTANCE);
        FeatureLinkImporter.receiver = null;
        MapMenuReceiver.getInstance().unregisterMapMenuFactory(menuFactory);
        try {
            context.unregisterReceiver(dropDown);
        } catch (IllegalArgumentException ignored) {
            // Not registered (e.g. never got past onCreate) — fine to ignore.
        }
        if (dropDown != null) dropDown.dispose();
        if (pliHistoryOverlay != null) pliHistoryOverlay.dispose();
        super.onDestroyImpl(context, view);
    }
}
