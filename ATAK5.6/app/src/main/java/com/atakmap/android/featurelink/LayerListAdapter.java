package com.atakmap.android.featurelink;

import android.content.Context;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.Spinner;
import android.widget.TextView;

import androidx.annotation.NonNull;

import com.atakmap.android.featurelink.arcgis.ArcGISLayer;
import com.atakmap.android.featurelink.plugin.R;

import java.util.List;

public class LayerListAdapter extends ArrayAdapter<ArcGISLayer> {

    public interface OnLayerActionListener {
        void onAction(ArcGISLayer layer);
    }

    private static final int[]     INTERVAL_VALUES = {0, 1, 3, 5, 10, 20, 30, 45};
    private static final String[]  INTERVAL_LABELS = {"Off", "1", "3", "5", "10", "20", "30", "45"};
    private static final String[]  UNITS = {"s", "min", "hr"};

    private final OnLayerActionListener listener;

    public LayerListAdapter(Context context, List<ArcGISLayer> layers,
            OnLayerActionListener listener) {
        super(context, 0, layers);
        this.listener = listener;
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

        ImageView   eyeIcon         = convertView.findViewById(R.id.layer_eye_icon);
        TextView    nameText        = convertView.findViewById(R.id.layer_name_text);
        Spinner     intervalSpinner = convertView.findViewById(R.id.layer_interval_spinner);
        Spinner     unitSpinner     = convertView.findViewById(R.id.layer_unit_spinner);
        ImageButton actionBtn       = convertView.findViewById(R.id.layer_action_btn);

        nameText.setText(layer.name);

        boolean isPrivate = "private".equals(layer.type);
        eyeIcon.setImageResource(isPrivate ? R.drawable.ic_eye_closed : R.drawable.ic_eye_open);

        if (isPrivate) {
            intervalSpinner.setVisibility(View.VISIBLE);
            unitSpinner.setVisibility(View.VISIBLE);

            // --- Interval Spinner ---
            ArrayAdapter<String> intervalAdapter = new ArrayAdapter<>(
                    getContext(), android.R.layout.simple_spinner_item, INTERVAL_LABELS);
            intervalAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
            intervalSpinner.setOnItemSelectedListener(null);
            intervalSpinner.setAdapter(intervalAdapter);

            int intervalIdx = 0;
            for (int i = 0; i < INTERVAL_VALUES.length; i++) {
                if (INTERVAL_VALUES[i] == layer.recurrenceInterval) { intervalIdx = i; break; }
            }
            intervalSpinner.setSelection(intervalIdx, false);
            intervalSpinner.post(() -> intervalSpinner.setOnItemSelectedListener(
                    new android.widget.AdapterView.OnItemSelectedListener() {
                        @Override
                        public void onItemSelected(android.widget.AdapterView<?> p,
                                View v, int pos, long id) {
                            layer.recurrenceInterval = INTERVAL_VALUES[pos];
                            if (listener != null) listener.onAction(layer);
                        }
                        @Override
                        public void onNothingSelected(android.widget.AdapterView<?> p) {}
                    }));

            // --- Unit Spinner ---
            ArrayAdapter<String> unitAdapter = new ArrayAdapter<>(
                    getContext(), android.R.layout.simple_spinner_item, UNITS);
            unitAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
            unitSpinner.setOnItemSelectedListener(null);
            unitSpinner.setAdapter(unitAdapter);

            int unitIdx = 1; // default "min"
            for (int i = 0; i < UNITS.length; i++) {
                if (UNITS[i].equals(layer.recurrenceUnit)) { unitIdx = i; break; }
            }
            unitSpinner.setSelection(unitIdx, false);
            unitSpinner.post(() -> unitSpinner.setOnItemSelectedListener(
                    new android.widget.AdapterView.OnItemSelectedListener() {
                        @Override
                        public void onItemSelected(android.widget.AdapterView<?> p,
                                View v, int pos, long id) {
                            layer.recurrenceUnit = UNITS[pos];
                            if (listener != null) listener.onAction(layer);
                        }
                        @Override
                        public void onNothingSelected(android.widget.AdapterView<?> p) {}
                    }));

            // --- Action button: down arrow until first sync, circular refresh after ---
            boolean synced = layer.lastSync > 0;
            actionBtn.setImageResource(synced ? R.drawable.ic_refresh_circle : R.drawable.ic_download);
            actionBtn.setOnClickListener(v -> {
                if (listener != null) listener.onAction(layer);
            });

        } else {
            intervalSpinner.setVisibility(View.GONE);
            unitSpinner.setVisibility(View.GONE);
            actionBtn.setImageResource(android.R.drawable.ic_menu_delete);
            actionBtn.setOnClickListener(v -> {
                if (listener != null) listener.onAction(layer);
            });
        }

        return convertView;
    }
}
