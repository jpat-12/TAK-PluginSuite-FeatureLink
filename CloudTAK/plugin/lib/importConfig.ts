// Config import — replaces the ATAK plugin's QR-scan / pref-file-upload apply routing
// (FeatureLinkDropDownReceiver.applyScannedPayload / applyScannedDisplayConfig) with a
// paste-JSON / upload-.json path (AddLayerView.vue). Same priority order as the Java version:
// a DisplayConfig (Mode 1/2/3) takes priority; otherwise fall back to a generic operational
// payload ({type: pli_endpoint | layer_config}).

import { store } from './store.ts';
import * as rest from './arcgisRest.ts';
import { parseDisplayConfig } from './displayConfig.ts';
import { addPublicLayer, downloadLayer, findLayer } from './layerActions.ts';
import { setPliLayerUrl } from './store.ts';
import type { OperationalPayload } from './types.ts';

export interface ImportResult { ok: boolean; message: string }

function parseOperationalPayload(text: string): OperationalPayload | null {
    try {
        const obj = JSON.parse(text) as Record<string, unknown>;
        if (obj.type === 'pli_endpoint' && typeof obj.url === 'string') {
            return { v: 1, type: 'pli_endpoint', url: obj.url };
        }
        if (obj.type === 'layer_config' && typeof obj.url === 'string') {
            return {
                v: 1, type: 'layer_config', url: obj.url,
                name: typeof obj.name === 'string' ? obj.name : undefined,
                private: obj.private === true,
            };
        }
    } catch { /* not JSON, or not this shape */ }
    return null;
}

export async function applyConfigText(text: string): Promise<ImportResult> {
    const trimmed = text.trim();
    if (!trimmed) return { ok: false, message: 'Nothing to import' };

    const displayConfig = parseDisplayConfig(trimmed);
    if (displayConfig) {
        if (!displayConfig.url) {
            return { ok: false, message: 'This config has no layer URL — paste a full config that includes one' };
        }
        const url = displayConfig.url;
        store.displayConfigs[url] = displayConfig;

        const existing = findLayer(url);
        if (existing) {
            void downloadLayer(existing);
            return { ok: true, message: `Styling applied to existing layer: ${existing.name}` };
        }

        const layer = await rest.fetchLayerInfo(url);
        if (!layer) return { ok: false, message: 'Could not load layer from the config URL' };
        if (displayConfig.freq) {
            layer.recurrenceInterval = displayConfig.freq.iv;
            layer.recurrenceUnit = displayConfig.freq.u;
        }
        // LayerShareHelper.java's Mode-2 share JSON parses through here (parseDisplayConfig's
        // passthrough branch) and carries a "private" flag this DisplayConfig type doesn't
        // declare but the raw JSON still has — route to the matching on-device section rather
        // than always landing in Public Layers regardless of the source layer's actual type.
        if ((displayConfig as { private?: boolean }).private === true) {
            layer.type = 'private';
            store.privateLayers.push(layer);
        } else {
            store.publicLayers.push(layer);
        }
        void downloadLayer(layer);
        return { ok: true, message: `Added: ${layer.name}` };
    }

    const payload = parseOperationalPayload(trimmed);
    if (payload) {
        if (payload.type === 'pli_endpoint') {
            setPliLayerUrl(payload.url);
            return { ok: true, message: `PLI layer set: ${payload.url}` };
        }
        if (payload.type === 'layer_config') {
            if (payload.private) {
                const existing = findLayer(payload.url);
                if (existing) {
                    void downloadLayer(existing);
                    return { ok: true, message: `Styling applied to existing layer: ${existing.name}` };
                }
                const layer = await rest.fetchLayerInfo(payload.url);
                if (!layer) return { ok: false, message: 'Could not load private layer from URL' };
                layer.type = 'private';
                if (payload.name) layer.name = payload.name;
                store.privateLayers.push(layer);
                void downloadLayer(layer);
                return { ok: true, message: `Added: ${layer.name}` };
            }
            return addPublicLayer(payload.url);
        }
    }

    return { ok: false, message: 'Unrecognized config format' };
}

export async function applyConfigFile(file: File): Promise<ImportResult> {
    const text = await file.text();
    return applyConfigText(text);
}
