// Shared layer CRUD + download logic used by LayersTab, HomeTab, AddLayerView, importConfig.ts
// and scheduler.ts — consolidated so download/add/remove behavior stays in one place rather
// than being reimplemented per caller. Ports FeatureLinkDropDownReceiver's downloadLayer(),
// fetchUserLayers(), addPublicLayer(), onLayerDelete()/confirmRemovePublicLayer(), and
// toggleLayerVisibility().

import { store } from './store.ts';
import { newLayer } from './types.ts';
import type { ArcGISLayer } from './types.ts';
import * as auth from './arcgisAuth.ts';
import * as rest from './arcgisRest.ts';
import { cotMappingOf } from './displayConfig.ts';
import { syncLayerMarkers, removeLayerMarkers } from './cot.ts';
import { generateAutoIconset } from './autoIconset.ts';

export async function downloadLayer(layer: ArcGISLayer): Promise<void> {
    // Downloading a "My ArcGIS Layers" item for the first time moves it onto the device, into
    // Private or Public Layers depending on its ArcGIS sharing scope (layer.access).
    if (store.browseLayers.some(l => l.url === layer.url)) {
        moveBrowseLayerOnDownload(layer);
    }

    const token = layer.type === 'private' ? await auth.getToken() : null;
    const displayConfig = store.displayConfigs[layer.url] ?? null;
    const mapping = displayConfig ? cotMappingOf(displayConfig.cm) : null;

    try {
        const features = await rest.downloadLayerAsCoT(layer.url, token, mapping);
        layer.lastSync = Date.now();
        layer.featureCount = features.length;
        await syncLayerMarkers(layer.url, features, layer.visible, displayConfig);
    } catch (e) {
        console.error('[featurelink] downloadLayer failed for', layer.name, e);
        throw e;
    }
}

// Moves a "My ArcGIS Layers" browse-list item onto the device, landing it in Private Layers or
// Public Layers depending on whether the ArcGIS item is shared to Everyone (layer.access) — from
// then on it behaves like any other on-device layer in that section instead of staying in the
// browse list. Ports FeatureLinkDropDownReceiver's moveMyArcGisLayerOnDownload().
function moveBrowseLayerOnDownload(layer: ArcGISLayer): void {
    const i = store.browseLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.browseLayers.splice(i, 1);
    if (layer.access === 'public') {
        layer.type = 'public';
        store.publicLayers.push(layer);
    } else {
        layer.type = 'private';
        store.privateLayers.push(layer);
    }
}

export async function refreshFeatureCounts(): Promise<void> {
    const token = await auth.getToken();
    for (const layer of [...store.browseLayers, ...store.privateLayers]) {
        try { layer.featureCount = await rest.queryFeatureCount(layer.url, token); }
        catch { layer.featureCount = -1; }
    }
    for (const layer of store.publicLayers) {
        try { layer.featureCount = await rest.queryFeatureCount(layer.url, null); }
        catch { layer.featureCount = -1; }
    }
}

// Re-fetches the signed-in user's owned Feature Services into the "My ArcGIS Layers" browse
// list, merging per-device settings (recurrence, visibility, lastSync) from the previously-saved
// browse entry of the same URL so a re-fetch never clobbers user-set refresh settings, and
// skipping anything the user previously removed (store.excludedPrivateUrls) or that's already
// been downloaded onto the device (now lives in privateLayers/publicLayers instead).
export async function fetchUserLayers(): Promise<void> {
    const token = await auth.getToken();
    const username = auth.getUsername();
    if (!token || !username) return;

    const fetched = await rest.searchUserLayers(store.portalUrl, token, username);
    const excluded = new Set(store.excludedPrivateUrls);
    const onDevice = new Set([...store.privateLayers, ...store.publicLayers].map(l => l.url));
    const prevByUrl = new Map(store.browseLayers.map(l => [l.url, l]));

    const merged: ArcGISLayer[] = [];
    for (const layer of fetched) {
        if (excluded.has(layer.url) || onDevice.has(layer.url)) continue;
        const prev = prevByUrl.get(layer.url);
        if (prev) merged.push({ ...prev, name: layer.name, access: layer.access });
        else merged.push(layer);
    }
    store.browseLayers.splice(0, store.browseLayers.length, ...merged);

    // On sign-in, show each layer's feature count without downloading any of them: queryFeatureCount
    // is a returnCountOnly query (no geometry/attributes pulled, no markers placed) — downloading
    // stays an explicit per-layer action. Iterate the reactive store entries (not `merged`) so the
    // count assignment goes through Vue's proxy and the list updates; run in parallel so a user with
    // many layers isn't waiting on a serial chain. A failed count shows as -1 ("error") in the row.
    await Promise.all(store.browseLayers.map(async (layer) => {
        try { layer.featureCount = await rest.queryFeatureCount(layer.url, token); }
        catch { layer.featureCount = -1; }
    }));
}

export async function addPublicLayer(url: string): Promise<{ ok: boolean; message: string; layer?: ArcGISLayer }> {
    const trimmed = url.trim();
    if (!trimmed) return { ok: false, message: 'Enter a service URL' };
    if (store.publicLayers.some(l => l.url === trimmed)) {
        return { ok: false, message: 'That layer is already in your list' };
    }
    const layer = await rest.fetchLayerInfo(trimmed);
    if (!layer) return { ok: false, message: 'Could not load layer from URL' };

    // Best-effort: a plain paste-URL add also tries to auto-generate + register this layer's
    // picture-marker icons (AUTO-ICONSET-SPEC.md), so it renders its own custom markers on this
    // device immediately rather than only on devices that later receive its CoT. Never blocks
    // adding the layer — a colored-shape renderer (no esriPMS symbols) isn't a failure, and a
    // real failure (network, no CloudTAK session) just means no icons this time.
    let message = `Added: ${layer.name}`;
    if (!store.displayConfigs[layer.url]) {
        try {
            const iconset = await generateAutoIconset(trimmed);
            if (iconset) {
                store.displayConfigs[layer.url] = iconset.displayConfig;
                message += ` (${iconset.iconCount} custom icon${iconset.iconCount === 1 ? '' : 's'})`;
            }
        } catch (e) {
            console.warn('[featurelink] auto-iconset generation failed for', layer.name, e);
        }
    }

    store.publicLayers.push(layer);
    void downloadLayer(layer);
    return { ok: true, message, layer };
}

// Sends an owned layer back to "My ArcGIS Layers" on removal instead of just discarding it —
// it's still the user's own ArcGIS item, so this is what a manual Refresh would eventually
// re-add anyway (fetchUserLayers only skips a URL that's still on-device or excluded); doing it
// immediately here means the user doesn't have to remember to hit Refresh to get it back.
function returnOwnedLayerToBrowse(layer: ArcGISLayer): void {
    if (!layer.ownedByMe) return;
    if (store.browseLayers.some(l => l.url === layer.url)) return;
    layer.type = 'private';
    layer.lastSync = 0;
    store.browseLayers.push(layer);
}

export async function removePublicLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.publicLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.publicLayers.splice(i, 1);
    await removeLayerMarkers(layer.url);
    delete store.displayConfigs[layer.url];
    returnOwnedLayerToBrowse(layer);
}

// Removes a "My ArcGIS Layers" browse-list entry. The layer still exists in the user's ArcGIS
// account — this only hides it on this device and remembers that choice so a later
// sign-in/refresh doesn't bring it back (mirrors onLayerDelete()'s excludedPrivateUrls
// bookkeeping). Layers that have actually been downloaded live in store.privateLayers instead
// (see removePrivateLayer) and don't need this tracking — a shared-private layer only ever gets
// (re)added by another share/import.
export async function removeBrowseLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.browseLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.browseLayers.splice(i, 1);
    if (!store.excludedPrivateUrls.includes(layer.url)) store.excludedPrivateUrls.push(layer.url);
}

// Removes an on-device Private Layers entry (downloaded from "My ArcGIS Layers", or shared to
// you privately by another user/config import). Nothing to delete server-side — just hides it
// and its markers on this device. If it came from "My ArcGIS Layers" (layer.ownedByMe), it goes
// back there instead of disappearing entirely — see returnOwnedLayerToBrowse().
export async function removePrivateLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.privateLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.privateLayers.splice(i, 1);
    await removeLayerMarkers(layer.url);
    delete store.displayConfigs[layer.url];
    returnOwnedLayerToBrowse(layer);
}

export async function toggleLayerVisibility(layer: ArcGISLayer): Promise<void> {
    layer.visible = !layer.visible;
    // Hiding shouldn't depend on a successful re-download — just tear down the placed markers
    // directly. (Re-showing below re-downloads and re-adds them.)
    if (!layer.visible) {
        await removeLayerMarkers(layer.url);
        return;
    }
    const displayConfig = store.displayConfigs[layer.url] ?? null;
    // Re-run the sync so markers actually appear rather than just flipping a flag.
    const token = layer.type === 'private' ? await auth.getToken() : null;
    const mapping = displayConfig ? cotMappingOf(displayConfig.cm) : null;
    try {
        const features = await rest.downloadLayerAsCoT(layer.url, token, mapping);
        await syncLayerMarkers(layer.url, features, layer.visible, displayConfig);
    } catch (e) {
        console.warn('[featurelink] toggleLayerVisibility resync failed', e);
    }
}

export function setLayerRecurrence(layer: ArcGISLayer, seconds: number): void {
    layer.recurrenceInterval = seconds;
    layer.recurrenceUnit = 's';
}

export function findLayer(url: string): ArcGISLayer | undefined {
    return store.privateLayers.find(l => l.url === url) ?? store.publicLayers.find(l => l.url === url);
}

export { newLayer };
