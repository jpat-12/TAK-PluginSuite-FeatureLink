// Config import — replaces the ATAK plugin's QR-scan / pref-file-upload apply routing
// (FeatureLinkDropDownReceiver.applyScannedPayload / applyScannedDisplayConfig) with a
// paste-JSON / upload-.json path (AddLayerView.vue). Same priority order as the Java version:
// a DisplayConfig (Mode 1/2/3) takes priority; otherwise fall back to a generic operational
// payload ({type: pli_endpoint | layer_config}).

import { store } from './store.ts';
import * as rest from './arcgisRest.ts';
import { parseDisplayConfig } from './displayConfig.ts';
import { addPublicLayer, downloadLayerReporting, findLayer, unexcludeLayer, tokenForUrl } from './layerActions.ts';
import { isAuthenticated } from './arcgisAuth.ts';
import { setPliLayerUrl } from './store.ts';
import { layerQueryUrl } from './arcgisUrl.ts';
import type { OperationalPayload } from './types.ts';

export interface ImportResult { ok: boolean; message: string }

/**
 * Where an import came from. `auto` is the 60-second /api/import poll (importIngest.ts); `manual`
 * is an operator pasting or uploading a config, or opening one from Add Layer.
 */
export type ImportSource = 'manual' | 'auto';

/**
 * Whether a deliberate removal should block re-adding this layer.
 *
 * `removePublicLayer`/`removePrivateLayer`/`clearAllLayers` all record the URL in
 * `store.excludedPrivateUrls`, and `fetchUserLayers` honours it — but this module never did. The
 * auto-ingest poll re-applies any `FeatureLink - <layer>` package still sitting in CloudTAK's
 * Import Manager, so a layer whose share package was received days ago came straight back within
 * a minute of every delete, every time, with no way for the operator to make it stop. Deleting the
 * layer, using Clear All Layers, and deleting the row all appeared to do nothing.
 *
 * A MANUAL import is the operator asking for it explicitly, so it clears the exclusion instead of
 * being blocked by it — otherwise a layer removed once could never be re-imported by hand either.
 */
function blockedByRemoval(url: string, source: ImportSource): boolean {
    const canonical = layerQueryUrl(url);
    if (source !== 'auto') {
        unexcludeLayer(canonical);
        return false;
    }
    return store.excludedPrivateUrls.includes(canonical);
}

const REMOVED_MESSAGE = 'This layer was removed on this device — not re-adding it automatically.';

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

export async function applyConfigText(text: string, source: ImportSource = 'manual'): Promise<ImportResult> {
    const trimmed = text.trim();
    if (!trimmed) return { ok: false, message: 'Nothing to import' };

    const displayConfig = parseDisplayConfig(trimmed);
    if (displayConfig) {
        if (!displayConfig.url) {
            return { ok: false, message: 'This config has no layer URL — paste a full config that includes one' };
        }
        const url = displayConfig.url;
        // Checked BEFORE the config is written: storing styling for a layer we then refuse to add
        // would leave an orphan config that silently reappears if the layer is ever re-added.
        if (blockedByRemoval(url, source)) return { ok: false, message: REMOVED_MESSAGE };
        store.displayConfigs[url] = displayConfig;

        const existing = findLayer(url);
        if (existing) {
            void downloadLayerReporting(existing);
            return { ok: true, message: `Styling applied to existing layer: ${existing.name}` };
        }

        // A shared config routinely points at a PRIVATE layer — the sender and the recipient are
        // often the same ArcGIS account on two devices. Without a token this returned "Token
        // Required" and the import died with a generic "Could not load layer from the config URL",
        // so every private-layer share from ATAK silently failed to arrive on CloudTAK.
        const layer = await rest.fetchLayerInfo(url, await tokenForUrl(url));
        if (!layer) {
            return {
                ok: false,
                message: isAuthenticated()
                    ? 'Could not load layer from the config URL'
                    : 'Could not load that layer — sign in to ArcGIS if it is a private layer.',
            };
        }
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
        void downloadLayerReporting(layer);
        return { ok: true, message: `Added: ${layer.name}` };
    }

    const payload = parseOperationalPayload(trimmed);
    if (payload) {
        if (payload.type === 'pli_endpoint') {
            setPliLayerUrl(payload.url);
            return { ok: true, message: `PLI layer set: ${payload.url}` };
        }
        if (payload.type === 'layer_config') {
            if (blockedByRemoval(payload.url, source)) return { ok: false, message: REMOVED_MESSAGE };
            if (payload.private) {
                const existing = findLayer(payload.url);
                if (existing) {
                    void downloadLayerReporting(existing);
                    return { ok: true, message: `Styling applied to existing layer: ${existing.name}` };
                }
                const layer = await rest.fetchLayerInfo(payload.url, await tokenForUrl(payload.url));
                if (!layer) {
                    return {
                        ok: false,
                        message: isAuthenticated()
                            ? 'Could not load private layer from URL'
                            : 'Could not load that private layer — sign in to ArcGIS first.',
                    };
                }
                layer.type = 'private';
                if (payload.name) layer.name = payload.name;
                store.privateLayers.push(layer);
                void downloadLayerReporting(layer);
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
