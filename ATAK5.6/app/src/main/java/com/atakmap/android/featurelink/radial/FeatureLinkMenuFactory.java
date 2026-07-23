package com.atakmap.android.featurelink.radial;

import android.content.Context;
import android.content.Intent;

import com.atakmap.android.featurelink.FeatureLinkDropDownReceiver;
import com.atakmap.android.ipc.AtakBroadcast;
import com.atakmap.android.maps.MapDataRef;
import com.atakmap.android.maps.MapItem;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.maps.assets.MapAssets;
import com.atakmap.android.menu.MapMenuButtonWidget;
import com.atakmap.android.menu.MapMenuFactory;
import com.atakmap.android.menu.MapMenuWidget;
import com.atakmap.android.menu.MenuMapAdapter;
import com.atakmap.android.menu.MenuResourceFactory;
import com.atakmap.android.menu.PluginMenuParser;
import com.atakmap.android.widgets.WidgetIcon;
import android.util.Log;

import gov.tak.api.widgets.IMapMenuButtonWidget;
import gov.tak.api.widgets.IMapWidget;

import java.io.IOException;

/**
 * Injects a "Send to Feature Layer" button into the radial menu for any
 * point-type CoT item (SA tracks, markers, etc.).  Shapes and groups are
 * skipped — they cannot be represented as a single-point ArcGIS feature.
 *
 * Sizing follows the pattern from the ATAK SDK radialmenudemo sample exactly:
 *  - setOrientation(getOrientationAngle(), menu.getInnerRadius())
 *  - setButtonSize(getButtonSpan(), menu.getButtonWidth())
 *  - setLayoutWeight(avg of children's getLayoutWeight() / total child count)
 *  - addChildWidget() via the IMapMenuWidget interface
 */
public class FeatureLinkMenuFactory implements MapMenuFactory {

    private static final String TAG = "FeatureLinkMenuFactory";

    private final Context pluginContext;
    private final Context appContext;
    private final MenuResourceFactory resourceFactory;

    public FeatureLinkMenuFactory(MapView mapView, Context pluginContext) {
        this.pluginContext = pluginContext;
        // MapAssets must use the ATAK app context (not the plugin context)
        this.appContext = mapView.getContext();

        MapAssets mapAssets = new MapAssets(appContext);
        MenuMapAdapter adapter = new MenuMapAdapter();
        try {
            adapter.loadMenuFilters(mapAssets, "filters/menu_filters.xml");
        } catch (IOException e) {
            Log.w(TAG, "Could not load menu filters — using defaults");
        }
        resourceFactory = new MenuResourceFactory(
                mapView, mapView.getMapData(), mapAssets, adapter);
    }

    @Override
    public MapMenuWidget create(MapItem mapItem) {
        if (mapItem == null) return null;

        String type = mapItem.getType();
        if (type == null) return null;

        // Skip self-marker and rubber-band drawing items
        if (type.startsWith("self") || type.startsWith("u-rb")) return null;

        // Only add for point-type CoT items
        boolean isPoint = type.startsWith("a-")
                || type.startsWith("b-")
                || type.equals("atom")
                || type.contains("marker");
        if (!isPoint) return null;

        MapMenuWidget menu = resourceFactory.create(mapItem);
        if (menu == null) return null;

        MapMenuButtonWidget btn = buildSendButton(mapItem.getUID(), menu);
        if (btn != null) {
            menu.addChildWidget(btn);
        }
        return menu;
    }

    private MapMenuButtonWidget buildSendButton(final String uid, MapMenuWidget menu) {
        try {
            MapMenuButtonWidget btn = new MapMenuButtonWidget(appContext);

            // Match the radial position and width of sibling buttons
            btn.setOrientation(btn.getOrientationAngle(), menu.getInnerRadius());
            btn.setButtonSize(btn.getButtonSpan(), menu.getButtonWidth());

            // Average the layout weight across all current children so our
            // button takes the same arc as its siblings (radialmenudemo pattern)
            float totalWeight = 0f;
            for (IMapWidget child : menu.getChildren()) {
                if (child instanceof MapMenuButtonWidget) {
                    totalWeight += ((MapMenuButtonWidget) child).getLayoutWeight();
                }
            }
            if (menu.getChildWidgetCount() > 0) {
                btn.setLayoutWeight(totalWeight / menu.getChildWidgetCount());
            }

            // Icon: prefer plugin asset, fall back to known-good ATAK built-in
            String iconUri = PluginMenuParser.getItem(
                    pluginContext, "icons/ic_send_to_layer.png");
            if (iconUri == null || iconUri.isEmpty()) {
                // "asset:///icons/incomplete.png" is confirmed present in ATAK
                // via the SDK radialmenudemo sample — safe fallback
                iconUri = "asset:///icons/incomplete.png";
            }
            WidgetIcon icon = new WidgetIcon.Builder()
                    .setAnchor(16, 16)
                    .setSize(32, 32)
                    .setImageRef(0, MapDataRef.parseUri(iconUri))
                    .build();
            btn.setIcon(icon);

            btn.setOnButtonClickHandler(new IMapMenuButtonWidget.OnButtonClickHandler() {
                @Override
                public boolean isSupported(Object o) {
                    return o instanceof MapItem;
                }

                @Override
                public void performAction(Object o) {
                    Intent intent = new Intent(FeatureLinkDropDownReceiver.SEND_TO_LAYER);
                    intent.putExtra("uid", uid);
                    AtakBroadcast.getInstance().sendBroadcast(intent);
                }
            });

            return btn;
        } catch (Exception e) {
            Log.e(TAG, "buildSendButton failed", e);
            return null;
        }
    }
}
