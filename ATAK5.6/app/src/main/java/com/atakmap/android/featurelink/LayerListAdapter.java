package com.atakmap.android.featurelink;

import android.content.Context;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.EditText;
import android.widget.ImageButton;
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
    /**
     * True when this adapter renders a BROWSE section ("My ArcGIS Layers"/"Shared with me"), which
     * is a listing of the operator's ArcGIS account rather than layers held on this device.
     *
     * <p>Section membership, not {@code lastSync}, is what decides whether share/remove apply. A
     * layer whose download FAILED still lives in an on-device section with {@code lastSync == 0};
     * keying off the timestamp stranded exactly those rows with no remove button while the
     * recurrence scheduler retried them every 30 seconds.
     */
    private final boolean browseSection;

    public LayerListAdapter(Context context, List<ArcGISLayer> layers,
            OnLayerActionListener listener, OnVisibilityToggleListener visibilityListener,
            OnIntervalChangeListener intervalChangeListener, OnShareListener shareListener,
            OnDeleteListener deleteListener, Set<String> styledLayerUrls, boolean browseSection) {
        super(context, 0, layers);
        this.browseSection          = browseSection;
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
        TextView    configBadge     = convertView.findViewById(R.id.layer_config_badge);
        TextView    featureCountBadge = convertView.findViewById(R.id.layer_feature_count_badge);
        View        intervalRow     = convertView.findViewById(R.id.layer_interval_row);
        View        intervalEditor  = convertView.findViewById(R.id.layer_interval_editor);
        EditText    intervalSecondsEdit = convertView.findViewById(R.id.layer_interval_seconds_edit);
        ImageButton actionBtn       = convertView.findViewById(R.id.layer_action_btn);
        ImageButton shareBtn        = convertView.findViewById(R.id.layer_share_btn);
        ImageButton deleteBtn       = convertView.findViewById(R.id.layer_delete_btn);

        nameText.setText(layer.name);

        boolean isPrivate = "private".equals(layer.type);
        typeBadge.setText(isPrivate ? "[Private]" : "[Public]");
        typeBadge.setTextColor(getContext().getResources().getColor(
                isPrivate ? R.color.fl_badge_private : R.color.fl_badge_public));

        boolean hasConfig = styledLayerUrls != null && styledLayerUrls.contains(layer.url);
        configBadge.setText(hasConfig ? "Config" : "No Config");
        configBadge.setBackgroundResource(hasConfig
                ? R.drawable.bg_pill_config : R.drawable.bg_pill_no_config);
        configBadge.setTextColor(getContext().getResources().getColor(hasConfig
                ? R.color.fl_badge_config_text : R.color.fl_badge_no_config_text));

        featureCountBadge.setText(layer.featureCount < 0 ? "error"
                : layer.featureCount + (layer.featureCount == 1 ? " feature" : " features"));

        // Share and remove only apply to a layer that actually exists on this device. On a browse
        // row ("My ArcGIS Layers"/"Shared with me") there is nothing to share — the Mission Package
        // is built from downloaded features — and nothing to remove, since the row is just a
        // listing of the operator's ArcGIS account. Showing them there offered two actions that
        // could not do anything useful, and "remove" in particular read as "delete from ArcGIS".
        boolean onDevice = !browseSection;
        shareBtn.setVisibility(onDevice ? View.VISIBLE : View.GONE);
        shareBtn.setOnClickListener(v -> {
            if (shareListener != null) shareListener.onShare(layer);
        });

        eyeIcon.setImageResource(layer.visible ? R.drawable.ic_eye_open : R.drawable.ic_eye_closed);
        eyeIcon.setAlpha(layer.visible ? 1.0f : 0.4f);
        eyeIcon.setOnClickListener(v -> {
            if (visibilityListener != null) visibilityListener.onToggleVisibility(layer);
        });

        // --- Interval row: always visible — it also carries the action/delete buttons, which
        // must stay usable on a browse-list row ("My ArcGIS Layers"/"Shared with me") since the
        // action button IS how a browse item gets downloaded in the first place. Only the
        // interval EDITOR (label/field/"seconds") is conditionally hidden: editing a refresh
        // interval before the layer even exists on-device doesn't make sense, and previously
        // wiring its focus-loss listener there risked an accidental auto-download of a layer the
        // user only meant to browse (that listener call is now skipped entirely, not just hidden,
        // for the same reason).
        boolean synced = layer.lastSync > 0;
        boolean editableInterval = !isPrivate || synced;
        intervalRow.setVisibility(View.VISIBLE);
        intervalEditor.setVisibility(editableInterval ? View.VISIBLE : View.GONE);

        intervalSecondsEdit.setOnFocusChangeListener(null);
        if (editableInterval) {
            // Editable purely in seconds now — recurrenceMillis() still handles a layer whose
            // recurrenceUnit is "min"/"hr" from before this change; edited layers always land
            // back on recurrenceUnit="s" via the commit below, showing the equivalent second
            // count here.
            long currentSeconds = layer.recurrenceMillis() / 1000L;
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
        }

        if (isPrivate) {
            // --- Action button: down arrow until first sync, circular refresh after ---
            actionBtn.setImageResource(synced ? R.drawable.ic_refresh_circle : R.drawable.ic_download);
            actionBtn.setOnClickListener(v -> {
                if (listener != null) listener.onAction(layer);
            });
            deleteBtn.setVisibility(onDevice ? View.VISIBLE : View.GONE);
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
