// Shared layer CRUD + download logic used by LayersTab, HomeTab, AddLayerView, importConfig.ts
// and scheduler.ts — consolidated so download/add/remove behavior stays in one place rather
// than being reimplemented per caller. Ports FeatureLinkDropDownReceiver's downloadLayer(),
// fetchUserLayers(), addPublicLayer(), onLayerDelete()/confirmRemovePublicLayer(), and
// toggleLayerVisibility().

import { reactive } from 'vue';
import { store } from './store.ts';
import { newLayer, clampRecurrenceSeconds } from './types.ts';
import type { ArcGISLayer, DisplayConfig, DownloadedFeature } from './types.ts';
import * as auth from './arcgisAuth.ts';
import * as rest from './arcgisRest.ts';
import { describeError } from './arcgisHttp.ts';
import { isTokenTrustedHost, layerQueryUrl } from './arcgisUrl.ts';
import { cotMappingOf } from './displayConfig.ts';
import { syncLayerMarkers, syncLayerShapes, removeLayerMarkers } from './cot.ts';
import { ensureLayerSymbology } from './symbology.ts';
import { withLock, singleFlight } from './asyncLock.ts';

/**
 * Per-layer error state, keyed by URL (C-32). The Layers tab used to wire every action as
 * `@action='downloadLayer(l).catch(() => {})'` — an empty catch IN THE TEMPLATE — so a user
 * clicking ⬇ on a layer that 403s, 404s or times out got no feedback at all and the row read
 * "never synced" forever. Deliberately NOT persisted: an error is about this session.
 */
export const layerErrors = reactive<Record<string, string>>({});
export const layerBusy = reactive<Record<string, boolean>>({});

function setError(url: string, message: string | null): void {
    if (message === null) delete layerErrors[url];
    else layerErrors[url] = message;
}

// Places (or replaces) the map items for one already-downloaded feature list, dispatching by the
// layer's geometryType — mirrors FeatureLinkDropDownReceiver.downloadLayer()'s per-feature
// buildMarker()/buildPolylineShapes()/buildPolygonShapes() dispatch, done once for the whole
// batch here since geometryType doesn't vary per feature within a layer.
async function syncLayerMapItems(layer: ArcGISLayer, features: DownloadedFeature[], displayConfig: DisplayConfig | null): Promise<void> {
    if (layer.geometryType === 'esriGeometryPolyline') {
        await syncLayerShapes(layer.url, features, layer.visible, displayConfig, 'polyline');
    } else if (layer.geometryType === 'esriGeometryPolygon') {
        await syncLayerShapes(layer.url, features, layer.visible, displayConfig, 'polygon');
    } else {
        await syncLayerMarkers(layer.url, features, layer.visible, displayConfig);
    }
}

/**
 * The token to use for this layer, or null.
 *
 * C-33: `layer.type === 'private'` is attacker-controllable — an imported `.featurelinkshare` can
 * set `"private": true` alongside an arbitrary `"url"`. The type alone is therefore NOT sufficient
 * authority to attach the operator's ArcGIS token; the target must also be on the signed-in
 * portal's own deployment. (arcgisHttp enforces this again at the request itself; this is the
 * outer guard so nothing even asks for a token it may not use.)
 */
async function tokenForLayer(layer: ArcGISLayer): Promise<string | null> {
    if (layer.type !== 'private') return null;
    if (!isTokenTrustedHost(layer.url, auth.getPortalUrl())) {
        console.warn('[featurelink] layer is marked private but is not on the signed-in portal — no token will be sent:', layer.url);
        return null;
    }
    return auth.getToken();
}

export async function downloadLayer(layer: ArcGISLayer): Promise<void> {
    // One download/sync at a time per layer (C-26). `syncLayerMarkers` reads
    // store.layerMarkerUids[url] at entry and writes it at exit with network I/O in between, so a
    // 10 s recurrence tick overlapping a manual download left the first run's markers on the map
    // with no record of their UIDs — unremovable by Hide, Delete or Clear All Layers.
    return withLock(`layer:${layer.url}`, async () => {
        layerBusy[layer.url] = true;
        try {
            const token = await tokenForLayer(layer);

            // One-time lazy backfill for layers added before geometryType existed, or whose
            // browse-list search result (a portal item, not layer metadata) never carried it.
            if (!layer.geometryType) {
                layer.geometryType = await rest.fetchGeometryType(layer.url, token);
            }

            // FIX-1 — the field defect. Resolve symbology HERE, on the path every entry route
            // shares, rather than only in addPublicLayer().
            await ensureLayerSymbology(layer, token);

            const displayConfig = store.displayConfigs[layer.url] ?? null;
            const mapping = displayConfig ? cotMappingOf(displayConfig.cm) : null;

            const result = await rest.downloadLayerAsCoT(layer.url, token, mapping);
            await syncLayerMapItems(layer, result.features, displayConfig);

            // lastSync/featureCount are set only AFTER the sync succeeds. They used to be set
            // before it, and `downloadLayerAsCoT` returned `[]` on error, so a failing layer
            // showed as "freshly synced, 0 features" (§10.5).
            layer.lastSync = Date.now();
            layer.featureCount = result.features.length;
            layer.truncated = result.truncated;

            // The browse-list → on-device move happens only on success. Doing it first meant a
            // failed download permanently relocated the layer out of the browse list (§10.5).
            if (store.browseLayers.some(l => l.url === layer.url)) moveBrowseLayerOnDownload(layer);

            setError(layer.url, result.truncated
                ? `Showing the first ${result.features.length} features — the layer has more (download was capped).`
                : null);
        } catch (e) {
            setError(layer.url, describeError(e));
            console.error('[featurelink] downloadLayer failed for', layer.name, e);
            throw e;
        } finally {
            delete layerBusy[layer.url];
        }
    });
}

/** downloadLayer with the error already surfaced on the row — for UI call sites (C-32). */
export async function downloadLayerReporting(layer: ArcGISLayer): Promise<boolean> {
    try { await downloadLayer(layer); return true; }
    catch { return false; } // the message is already in layerErrors[layer.url]
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
        if (!store.publicLayers.some(l => l.url === layer.url)) store.publicLayers.push(layer);
    } else {
        layer.type = 'private';
        if (!store.privateLayers.some(l => l.url === layer.url)) store.privateLayers.push(layer);
    }
}

const COUNT_CONCURRENCY = 5;

async function mapWithConcurrency<T>(items: T[], limit: number, fn: (item: T) => Promise<void>): Promise<void> {
    for (let i = 0; i < items.length; i += limit) {
        await Promise.allSettled(items.slice(i, i + limit).map(fn));
    }
}

/**
 * Refreshes the per-layer feature counts. Bounded concurrency rather than the previous serial loop
 * (50 layers × 300 ms = 15 s of a frozen "loading" state, §2.2) and rather than unbounded
 * `Promise.all` (100 simultaneous requests, which ArcGIS Online answers with 429s that the UI then
 * renders as "error" on most rows, §10.5).
 */
export async function refreshFeatureCounts(): Promise<void> {
    // Merely opening the Home tab must not be able to sign the user out: getToken()'s failure path
    // ends the session, so it is only called when there is a session to refresh (§2.2).
    const token = auth.isSessionValid() ? await auth.getToken() : null;

    await mapWithConcurrency([...store.browseLayers, ...store.privateLayers], COUNT_CONCURRENCY, async (layer) => {
        try {
            layer.featureCount = await rest.queryFeatureCount(layer.url, token);
            setError(layer.url, null);
        } catch (e) {
            layer.featureCount = -1;
            setError(layer.url, describeError(e));
        }
    });
    await mapWithConcurrency(store.publicLayers, COUNT_CONCURRENCY, async (layer) => {
        try {
            layer.featureCount = await rest.queryFeatureCount(layer.url, null);
            setError(layer.url, null);
        } catch (e) {
            layer.featureCount = -1;
            setError(layer.url, describeError(e));
        }
    });
}

export class NotSignedInError extends Error {
    constructor() { super('Sign in to ArcGIS to list your layers.'); this.name = 'NotSignedInError'; }
}

/**
 * Re-fetches the signed-in user's owned Feature Services into the "My ArcGIS Layers" browse list,
 * merging per-device settings from the previously-saved browse entry of the same URL, and skipping
 * anything the user removed (store.excludedPrivateUrls) or that is already on the device.
 *
 * Single-flighted: the wholesale `splice(0, len, ...merged)` interleaves destructively when two
 * refreshes overlap (Refresh double-click, or the startup call racing a manual one), duplicating or
 * dropping entries (§3.2). Errors now propagate — swallowing them into `[]` rendered a portal
 * outage as "No layers found in your ArcGIS account", which sends an operator hunting in the wrong
 * system (§3.2).
 */
export async function fetchUserLayers(): Promise<void> {
    return singleFlight('fetchUserLayers', async () => {
        const token = await auth.getToken();
        const username = auth.getUsername();
        if (!token || !username) throw new NotSignedInError();

        const fetched = await rest.searchUserLayers(auth.getPortalUrl(), token, username);
        const excluded = new Set(store.excludedPrivateUrls);
        const onDevice = new Set([...store.privateLayers, ...store.publicLayers].map(l => l.url));
        const prevByUrl = new Map(store.browseLayers.map(l => [l.url, l]));

        const merged: ArcGISLayer[] = [];
        const seen = new Set<string>();
        for (const layer of fetched) {
            if (excluded.has(layer.url) || onDevice.has(layer.url) || seen.has(layer.url)) continue;
            seen.add(layer.url);
            const prev = prevByUrl.get(layer.url);
            if (prev) merged.push({ ...prev, name: layer.name, access: layer.access, layerId: layer.layerId, geometryType: prev.geometryType || layer.geometryType });
            else merged.push(layer);
        }
        store.browseLayers.splice(0, store.browseLayers.length, ...merged);

        await mapWithConcurrency(store.browseLayers, COUNT_CONCURRENCY, async (layer) => {
            try {
                layer.featureCount = await rest.queryFeatureCount(layer.url, token);
                setError(layer.url, null);
            } catch (e) {
                layer.featureCount = -1;
                setError(layer.url, describeError(e));
            }
        });
    });
}

export interface AddLayerResult { ok: boolean; message: string; layers: ArcGISLayer[] }

/**
 * Adds a layer (or, for a bare service root, every sublayer of it — C-08) by URL.
 *
 * The auto-symbology block that used to live here has moved to symbology.ensureLayerSymbology(),
 * invoked by downloadLayer, so this route and the five that were previously unstyled all resolve
 * symbology through one implementation (C-07).
 */
export async function addPublicLayer(url: string): Promise<AddLayerResult> {
    const trimmed = url.trim();
    if (!trimmed) return { ok: false, message: 'Enter a service URL', layers: [] };

    let subs: { id: number; name: string; geometryType: string; url: string }[];
    try {
        subs = await rest.fetchServiceLayers(trimmed, null);
    } catch (e) {
        return { ok: false, message: `Could not load that service: ${describeError(e)}`, layers: [] };
    }
    if (!subs.length) return { ok: false, message: 'Could not load layer from URL', layers: [] };

    const added: ArcGISLayer[] = [];
    const skipped: string[] = [];
    for (const sub of subs) {
        const canonical = layerQueryUrl(sub.url);
        // Duplicate check across ALL three lists, not just publicLayers (§10.5).
        if (findAnyLayer(canonical)) { skipped.push(canonical); continue; }
        let layer: ArcGISLayer | null;
        try {
            layer = await rest.fetchLayerInfoStrict(canonical, null);
        } catch (e) {
            skipped.push(`${canonical} (${describeError(e)})`);
            continue;
        }
        layer.layerId = sub.id;
        store.publicLayers.push(layer);
        added.push(layer);
    }

    if (!added.length) {
        return {
            ok: false,
            message: skipped.length ? 'That layer is already in your list' : 'Could not load layer from URL',
            layers: [],
        };
    }

    // Download each added layer, reporting rather than floating the promise — `void downloadLayer()`
    // on a function that rethrows produced a guaranteed unhandled rejection on every failed add
    // (C-32 / §10.5).
    const outcomes = await Promise.allSettled(added.map(l => downloadLayer(l)));
    const failures = outcomes.filter(o => o.status === 'rejected').length;

    const names = added.map(l => l.name).join(', ');
    const styling = added.map(l => l.stylingMessage).filter(Boolean)[0];
    let message = added.length > 1 ? `Added ${added.length} layers: ${names}` : `Added: ${names}`;
    if (styling) message += ` (${styling})`;
    if (failures) message += ` — ${failures} could not be downloaded, see the row for details`;
    return { ok: true, message, layers: added };
}

// Sends an owned layer back to "My ArcGIS Layers" on removal instead of just discarding it —
// it's still the user's own ArcGIS item, so this is what a manual Refresh would eventually
// re-add anyway; doing it immediately here means the user doesn't have to hit Refresh.
function returnOwnedLayerToBrowse(layer: ArcGISLayer): void {
    if (!layer.ownedByMe) return;
    if (store.browseLayers.some(l => l.url === layer.url)) return;
    store.browseLayers.push({ ...layer, type: 'private', lastSync: 0, featureCount: 0, truncated: false });
}

/**
 * Records a deliberate removal so the 60-second auto-ingest poll cannot resurrect it.
 * `importConfig` never consulted `excludedPrivateUrls` and `removePublicLayer`/`removePrivateLayer`
 * never wrote to it, so every individual delete and Clear All Layers was silently undone within a
 * minute (§8.2, Top-10 #9).
 */
function rememberRemoval(url: string): void {
    if (!store.excludedPrivateUrls.includes(url)) store.excludedPrivateUrls.push(url);
}

/**
 * What removing an ON-DEVICE layer should do, which depends on whether the operator owns it.
 *
 * These two behaviours used to both run, and they contradict each other: the layer was pushed back
 * into "My ArcGIS Layers" AND recorded as excluded, so it appeared in the browse list and then
 * vanished the moment anything called fetchUserLayers — which filters excluded URLs out. Removing a
 * layer therefore looked like it half-worked, and the row could not be got back without Undo.
 *
 * An owned layer is still in the operator's ArcGIS account, so removing it from the device means
 * "put it back in the listing", not "never show it again" — a manual Refresh would re-list it
 * anyway. Only a layer the operator does NOT own has nowhere to go, so only that one is excluded.
 */
function rememberOrReturn(layer: ArcGISLayer): void {
    if (layer.ownedByMe) {
        unexcludeLayer(layer.url);
        returnOwnedLayerToBrowse(layer);
        return;
    }
    rememberRemoval(layer.url);
}

export async function removePublicLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.publicLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.publicLayers.splice(i, 1);
    await removeLayerMarkers(layer.url);
    delete store.displayConfigs[layer.url];
    setError(layer.url, null);
    rememberOrReturn(layer);
}

// Removes a "My ArcGIS Layers" browse-list entry. The layer still exists in the user's ArcGIS
// account — this only hides it on this device and remembers that choice.
export async function removeBrowseLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.browseLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.browseLayers.splice(i, 1);
    rememberRemoval(layer.url);
}

// Removes an on-device Private Layers entry. Nothing to delete server-side — just hides it and its
// markers on this device.
export async function removePrivateLayer(layer: ArcGISLayer): Promise<void> {
    const i = store.privateLayers.findIndex(l => l.url === layer.url);
    if (i !== -1) store.privateLayers.splice(i, 1);
    await removeLayerMarkers(layer.url);
    delete store.displayConfigs[layer.url];
    setError(layer.url, null);
    rememberOrReturn(layer);
}

/** Undoes a removal so the layer can be re-listed/re-imported (there was no way back before — §7.3). */
export function unexcludeLayer(url: string): void {
    const i = store.excludedPrivateUrls.indexOf(url);
    if (i !== -1) store.excludedPrivateUrls.splice(i, 1);
}

export async function toggleLayerVisibility(layer: ArcGISLayer): Promise<void> {
    const wasVisible = layer.visible;
    layer.visible = !wasVisible;
    if (!layer.visible) {
        await removeLayerMarkers(layer.url);
        return;
    }
    try {
        await downloadLayer(layer);
    } catch (e) {
        // The eye icon must not claim the layer is shown when nothing was placed (§10.5).
        layer.visible = wasVisible;
        setError(layer.url, describeError(e));
        console.warn('[featurelink] toggleLayerVisibility resync failed', e);
    }
}

/** Clamped + NaN-rejecting, and it reflects the accepted value back so the input cannot lie (§3.2). */
export function setLayerRecurrence(layer: ArcGISLayer, seconds: number): number {
    const clamped = clampRecurrenceSeconds(seconds);
    layer.recurrenceInterval = clamped;
    layer.recurrenceUnit = 's';
    return clamped;
}

export function findLayer(url: string): ArcGISLayer | undefined {
    const canonical = layerQueryUrl(url);
    return store.privateLayers.find(l => l.url === canonical) ?? store.publicLayers.find(l => l.url === canonical);
}

/** Every list, so a URL cannot exist twice and have two rows share one marker-UID bucket (§3.2). */
export function findAnyLayer(url: string): ArcGISLayer | undefined {
    const canonical = layerQueryUrl(url);
    return findLayer(canonical) ?? store.browseLayers.find(l => l.url === canonical);
}

export interface ClearAllResult { removed: number; failed: number }

/** Settings menu "Clear All Layers". Reports what happened rather than failing silently (§1.3). */
export async function clearAllLayers(): Promise<ClearAllResult> {
    let removed = 0, failed = 0;
    for (const layer of [...store.privateLayers]) {
        try { await removePrivateLayer(layer); removed++; } catch { failed++; }
    }
    for (const layer of [...store.publicLayers]) {
        try { await removePublicLayer(layer); removed++; } catch { failed++; }
    }
    return { removed, failed };
}

export { newLayer };
