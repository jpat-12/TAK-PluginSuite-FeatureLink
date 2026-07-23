package com.atakmap.android.featurelink;

import android.graphics.Color;

import com.atakmap.android.maps.MapGroup;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.maps.Marker;
import android.util.Log;
import com.atakmap.coremap.maps.coords.GeoPoint;

import java.util.LinkedList;

/**
 * Maintains up to 5 PLI history markers on the ATAK map.
 * Each marker is tinted with the operator's team color; alpha fades
 * from the newest (fully opaque) to the oldest (~24% opacity).
 *
 * Colour is applied via marker metadata ("color" key) which ATAK's
 * standard renderer respects for icon tinting — no Icon class needed.
 * Markers are owned by the root MapGroup so they appear on the map
 * without needing a separate overlay registration.
 */
public class PliHistoryOverlay {

    private static final String TAG = "PliHistoryOverlay";
    private static final int MAX_HISTORY = 5;

    // Alpha for history positions 0 (newest) … 4 (oldest)
    private static final int[] ALPHAS = {255, 210, 160, 110, 60};

    private final MapView mapView;
    private final MapGroup parentGroup;
    private final LinkedList<Marker> markers = new LinkedList<>();

    public PliHistoryOverlay(MapView mapView) {
        this.mapView = mapView;
        // Root group is always concrete and accessible; markers are tagged
        // with nevercot=true so they are never broadcast as CoT events.
        this.parentGroup = mapView.getRootGroup();
    }

    /** Thread-safe entry point — posts to the UI/GL thread internally. */
    public synchronized void addPoint(final GeoPoint pt) {
        mapView.post(() -> addPointOnUiThread(pt));
    }

    private void addPointOnUiThread(GeoPoint pt) {
        try {
            int baseColor = getTeamBaseColor();
            int r = Color.red(baseColor);
            int g = Color.green(baseColor);
            int b = Color.blue(baseColor);

            // Create marker at the current PLI position
            String uid = "featurelink.pli.history." + System.currentTimeMillis();
            Marker m = new Marker(pt, uid);
            m.setType(mapView.getSelfMarker().getType());
            m.setMetaString("callsign", "PLI");
            m.setMetaBoolean("nevercot", true);      // never broadcast as CoT
            m.setMetaBoolean("addToObjList", false); // hide from overlay manager list
            m.setMetaInteger("color", Color.argb(ALPHAS[0], r, g, b));

            markers.addFirst(m);
            parentGroup.addItem(m);

            // Trim beyond the limit
            while (markers.size() > MAX_HISTORY) {
                Marker oldest = markers.removeLast();
                parentGroup.removeItem(oldest);
            }

            // Re-apply faded alpha to every history marker
            for (int i = 0; i < markers.size(); i++) {
                int alpha = ALPHAS[Math.min(i, ALPHAS.length - 1)];
                Marker mk = markers.get(i);
                mk.setMetaInteger("color", Color.argb(alpha, r, g, b));
                mk.refresh(mapView.getMapEventDispatcher(), null, getClass());
            }
        } catch (Exception e) {
            Log.e(TAG, "addPointOnUiThread failed", e);
        }
    }

    /** Maps ATAK team names to fully-opaque ARGB base colors. */
    private int getTeamBaseColor() {
        try {
            String team = mapView.getSelfMarker().getMetaString("team", "Cyan");
            switch (team) {
                case "Red":       return 0xFFFF0000;
                case "Blue":      return 0xFF0000FF;
                case "Green":     return 0xFF00CC00;
                case "Yellow":    return 0xFFFFFF00;
                case "Orange":    return 0xFFFF8C00;
                case "Purple":    return 0xFF9900CC;
                case "Maroon":    return 0xFF800000;
                case "Teal":      return 0xFF008080;
                case "Dark Blue": return 0xFF00008B;
                case "White":     return 0xFFFFFFFF;
                case "Cyan":
                default:          return 0xFF00FFFF;
            }
        } catch (Exception e) {
            return 0xFF00FFFF;
        }
    }

    /** Remove all history markers from the map. Call on plugin destroy. */
    public void dispose() {
        try {
            mapView.post(() -> {
                for (Marker m : markers) {
                    parentGroup.removeItem(m);
                }
                markers.clear();
            });
        } catch (Exception e) {
            Log.e(TAG, "dispose failed", e);
        }
    }
}
