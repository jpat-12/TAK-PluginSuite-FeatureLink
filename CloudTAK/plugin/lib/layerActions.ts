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

export async function downloadLayer(layer: ArcGISLayer): Promise<void> {
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

export async function refreshFeatureCounts(): Promise<void> {
    const token = await auth.getToken();
    for (const layer of store.privateLayers) {
        try { layer.featureCount = await rest.queryFeatureCount(layer.url, token); }
        catch { layer.featureCount = -1; }
    }
    for (const layer of store.publicLayers) {
        try { layer.featureCount = await rest.queryFeatureCount(layer.url, null); }
        catch { layer.featureCount = -1; }
    }
}

// Re-fetches the signed-in user's owned Feature Services, merging per-device settings
// (recurrence, visibility, lastSync) from the previously-saved layer of the same URL so a
// re-fetch never clobbers user-set refresh settings, and skipping anything the user
// previously removed (store.excludedPrivateUrls).
export async function fetchUserLayers(): Promise<void> {
    const token = await auth.getToken();
    const username = auth.getUsername();
    if (!token || !username) return;

    const fetched = await rest.searchUserLayers(store.portalUrl, token, username);
    const excluded = new Set(store.excludedPrivateUrls);
    const prevByUrl = new Map(store.privateLayers.map(l => [l.url, l]));

    const merged: ArcGISLayer[] = [];
    for (const layer of fetched) {
        if (excluded.has(layer.url)) continue;
        const prev = prevByUrl.get(layer.url);
        if (prev) merged.push({ ...prev, name: layer.name });
        else merged.push(layer);
    }
    store.privateLayers.splice(0, store.privateLayers.length, ...merged);

    // On sign-in, show each layer's feature count without downloading any of them: queryFeatureCount
    // is a returnCountOnly query (no geometry/attributes pulled, no markers placed) — downloading
    // stays an explicit per-layer action. Iterate the reactive store entries (not `merged`) so the
    // count assignment goes through Vue's proxy and the list updates; run in parallel so a user with
    // many layers isn't waiting on a serial chain. A failed count shows as -1 ("error") in the row.
    await Promise.all(store.privateLayers.map(async (layer) => {
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
    store.publicLayers.push(layer);
    void downloadLayer(layer);
    return { ok: true, message: `Added: ${layer.name}`, layer };
}

export async function removePublicLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.publicLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.publicLayers.splice(i, 1);
    await removeLayerMarkers(layer.url);
    delete store.displayConfigs[layer.url];
}

// Unlike removePublicLayer, the layer still exists in the user's ArcGIS account — this only
// hides it on this device and remembers that choice so a later sign-in/refresh doesn't bring
// it back (mirrors onLayerDelete()'s excludedPrivateUrls bookkeeping).
export async function removePrivateLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.privateLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.privateLayers.splice(i, 1);
    await removeLayerMarkers(layer.url);
    delete store.displayConfigs[layer.url];
    if (!store.excludedPrivateUrls.includes(layer.url)) store.excludedPrivateUrls.push(layer.url);
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
