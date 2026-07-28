package com.atakmap.android.featurelink;

import android.content.Context;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.EditText;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.TextView;

import androidx.annotation.NonNull;

import com.atakmap.android.featurelink.arcgis.ArcGISLayer;
import com.atakmap.android.featurelink.plugin.R;

import java.util.List;
import java.util.Set;

public class LayerListAdapter extends ArrayAdapter<ArcGISLayer> {

    public interface OnLayerActionListener {
        void onAction(ArcGISLayer layer);
    }

    public interface OnVisibilityToggleListener {
        void onToggleVisibility(ArcGISLayer layer);
    }

    /** Fired when a layer's refresh interval/unit spinner changes — public layers only (see
     * getView()); private layers keep routing interval changes through OnLayerActionListener,
     * which already saves + re-syncs on any change, so a second listener would be redundant. */
    public interface OnIntervalChangeListener {
        void onIntervalChanged(ArcGISLayer layer);
    }

    /** Fired by the Share button — sends this layer (public or private) to a picked ATAK
     * contact as a Mission Package; see FeatureLinkDropDownReceiver.onLayerShare(). A shared
     * private layer needs the recipient's own ArcGIS access (e.g. group membership) to
     * actually download — sharing it doesn't grant access, just the layer reference. */
    public interface OnShareListener {
        void onShare(ArcGISLayer layer);
    }

    /** Fired by the trash-can button. Public layers use their action button for this instead
     * (see onAction's "public" branch) — this is only wired for private-type layers (both "My
     * ArcGIS Layers" and "Private Layers"/shared sections use this same adapter class, just
     * with different callback instances per section). */
    public interface OnDeleteListener {
        void onDelete(ArcGISLayer layer);
    }

    private final OnLayerActionListener listener;
    private final OnVisibilityToggleListener visibilityListener;
    private final OnIntervalChangeListener intervalChangeListener;
    private final OnShareListener shareListener;
    private final OnDeleteListener deleteListener;
    private final Set<String> styledLayerUrls;

    public LayerListAdapter(Context context, List<ArcGISLayer> layers,
            OnLayerActionListener listener, OnVisibilityToggleListener visibilityListener,
            OnIntervalChangeListener intervalChangeListener, OnShareListener shareListener,
            OnDeleteListener deleteListener, Set<String> styledLayerUrls) {
        super(context, 0, layers);
        this.listener               = listener;
        this.visibilityListener     = visibilityListener;
        this.intervalChangeListener = intervalChangeListener;
        this.shareListener          = shareListener;
        this.deleteListener         = deleteListener;
        this.styledLayerUrls        = styledLayerUrls;
    }

    @NonNull
    @Override
    public View getView(int position, View convertView, @NonNull ViewGroup parent) {
        if (convertView == null) {
            convertView = LayoutInflater.from(getContext())
                    .inflate(R.layout.item_layer, parent, false);
        }

        ArcGISLayer layer = getItem(position);
        if (layer == null) return convertView;

        ImageButton eyeIcon         = convertView.findViewById(R.id.layer_eye_icon);
        TextView    nameText        = convertView.findViewById(R.id.layer_name_text);
        TextView    typeBadge       = convertView.findViewById(R.id.layer_type_badge);
        ImageView   stylingIcon     = convertView.findViewById(R.id.layer_styling_icon);
        View        intervalRow     = convertView.findViewById(R.id.layer_interval_row);
        EditText    intervalSecondsEdit = convertView.findViewById(R.id.layer_interval_seconds_edit);
        ImageButton actionBtn       = convertView.findViewById(R.id.layer_action_btn);
        ImageButton shareBtn        = convertView.findViewById(R.id.layer_share_btn);
        ImageButton deleteBtn       = convertView.findViewById(R.id.layer_delete_btn);

        nameText.setText(layer.name);

        boolean isPrivate = "private".equals(layer.type);
        typeBadge.setText(isPrivate ? "[Private]" : "[Public]");
        typeBadge.setTextColor(getContext().getResources().getColor(
                isPrivate ? R.color.fl_badge_private : R.color.fl_badge_public));

        stylingIcon.setVisibility(styledLayerUrls != null && styledLayerUrls.contains(layer.url)
                ? View.VISIBLE : View.GONE);

        shareBtn.setVisibility(View.VISIBLE);
        shareBtn.setOnClickListener(v -> {
            if (shareListener != null) shareListener.onShare(layer);
        });

        eyeIcon.setImageResource(layer.visible ? R.drawable.ic_eye_open : R.drawable.ic_eye_closed);
        eyeIcon.setAlpha(layer.visible ? 1.0f : 0.4f);
        eyeIcon.setOnClickListener(v -> {
            if (visibilityListener != null) visibilityListener.onToggleVisibility(layer);
        });

        // --- Interval row: shown for both private and public layers. Private layers route
        // changes through the same listener the action button uses (existing behavior: saves
        // and immediately re-syncs). Public layers route through the dedicated
        // intervalChangeListener instead, since their action button means "delete" — reusing
        // onAction there would pop the delete-confirmation dialog just from picking an interval.
        intervalRow.setVisibility(View.VISIBLE);

        // Editable purely in seconds now — recurrenceMillis() still handles a layer whose
        // recurrenceUnit is "min"/"hr" from before this change; edited layers always land back
        // on recurrenceUnit="s" via the commit below, showing the equivalent second count here.
        long currentSeconds = layer.recurrenceMillis() / 1000L;
        intervalSecondsEdit.setOnFocusChangeListener(null);
        intervalSecondsEdit.setText(String.valueOf(currentSeconds));
        intervalSecondsEdit.setOnFocusChangeListener((v, hasFocus) -> {
            if (hasFocus) return;
            int seconds;
            try {
                seconds = Integer.parseInt(intervalSecondsEdit.getText().toString().trim());
            } catch (NumberFormatException e) {
                seconds = 0;
            }
            if (seconds < 0) seconds = 0;
            layer.recurrenceInterval = seconds;
            layer.recurrenceUnit = "s";
            intervalSecondsEdit.setText(String.valueOf(seconds));
            if (isPrivate) {
                if (listener != null) listener.onAction(layer);
            } else if (intervalChangeListener != null) {
                intervalChangeListener.onIntervalChanged(layer);
            }
        });

        if (isPrivate) {
            // --- Action button: down arrow until first sync, circular refresh after ---
            boolean synced = layer.lastSync > 0;
            actionBtn.setImageResource(synced ? R.drawable.ic_refresh_circle : R.drawable.ic_download);
            actionBtn.setOnClickListener(v -> {
                if (listener != null) listener.onAction(layer);
            });
            deleteBtn.setVisibility(View.VISIBLE);
            deleteBtn.setOnClickListener(v -> {
                if (deleteListener != null) deleteListener.onDelete(layer);
            });
        } else {
            actionBtn.setImageResource(android.R.drawable.ic_menu_delete);
            actionBtn.setOnClickListener(v -> {
                if (listener != null) listener.onAction(layer);
            });
            deleteBtn.setVisibility(View.GONE);
        }

        return convertView;
    }
}
