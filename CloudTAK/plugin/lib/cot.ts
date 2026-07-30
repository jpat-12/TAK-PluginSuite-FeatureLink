// CoT / TAK map integration.
//
// INTERNAL REACH-IN: CloudTAK's public PluginAPI doesn't expose a CoT write path (or a
// confirmed self-position read path) yet, so — exactly like CloudTAK-Plugin_StatusBoard_CAP's
// cot.ts — this reaches into `mapStore.worker.db` and node-cot's normalizer. Isolated here so
// there's one place to fix on a CloudTAK upgrade.
//
// Covers three things the ATAK plugin did with real ATAK map APIs:
//   1. One marker per downloaded layer feature, styled via lib/displayConfig.ts
//      (FeatureLinkDropDownReceiver.downloadLayer's marker-build logic).
//   2. A 5-point fading PLI history breadcrumb trail (PliHistoryOverlay.java).
//   3. listCotMarkers() for the send-to-layer picker (radial-menu replacement) and a
//      best-effort self-position reader for PLI auto-send (MapView.getSelfMarker()).

import { getPluginApi } from './plugin-api.ts';
import { store } from './store.ts';
import { resolveColor, resolveIconsetPath, resolveLabel, buildRemarks } from './displayConfig.ts';
import type { DisplayConfig, DownloadedFeature } from './types.ts';

let _normalize: ((f: unknown) => Promise<unknown>) | null = null;
let _mapStore: { worker: { db: {
    add: (f: unknown, opts?: unknown) => Promise<void>;
    remove: (uid: string, opts?: unknown) => Promise<void>;
} }; currentUser?: unknown } | null = null;

export async function initCot(): Promise<void> {
    try {
        const [cotMod, storeMod] = await Promise.all([
            import('@tak-ps/node-cot/normalize_geojson' as string),
            import('../../../src/stores/map.ts' as string),
        ]);
        _normalize = (cotMod as { normalize_geojson: (f: unknown) => Promise<unknown> }).normalize_geojson;
        const useMapStore = (storeMod as { useMapStore: () => typeof _mapStore }).useMapStore;
        _mapStore = useMapStore();
    } catch {
        console.warn('[featurelink] CoT helpers could not initialize — map markers disabled');
    }
}

function hexToArgb(hex: string): number {
    const clean = hex.replace('#', '');
    const rgb = clean.length >= 6 ? clean.slice(0, 6) : clean.padEnd(6, clean);
    const alphaHex = clean.length === 8 ? clean.slice(6, 8) : 'FF';
    const alpha = parseInt(alphaHex, 16);
    return ((alpha << 24) | parseInt(rgb, 16)) >>> 0;
}

async function upsertFeature(
    uid: string, lat: number, lon: number, callsign: string,
    cotType: string, argbColor: number, remarks: string, iconsetPath?: string | null,
): Promise<void> {
    if (!_normalize || !_mapStore) return;
    try {
        const feat = {
            id: uid, type: 'Feature',
            properties: { callsign, remarks },
            geometry: { type: 'Point', coordinates: [lon, lat] },
        };
        const norm = await _normalize(feat) as Record<string, unknown>;
        const props = norm.properties as Record<string, unknown>;
        props.type = cotType;
        props.how = 'h-g-i-g-o';
        props.color = argbColor;
        if (iconsetPath) props.icon = iconsetPath;
        await _mapStore.worker.db.add(JSON.parse(JSON.stringify(norm)), { authored: true });
    } catch (e) {
        console.warn('[featurelink] upsertFeature failed', e);
    }
}

async function removeMarker(uid: string): Promise<void> {
    if (!_mapStore) return;
    try { await _mapStore.worker.db.remove(uid, { mission: true }); }
    catch (e) { console.warn('[featurelink] removeMarker failed', e); }
}

// ── Downloaded-layer markers ────────────────────────────────────────────────

function markerUid(layerUrl: string, featureUid: string): string {
    return `fl-${layerUrl.length}-${hashCode(layerUrl)}-${featureUid}`;
}

function hashCode(s: string): string {
    let h = 0;
    for (let i = 0; i < s.length; i++) { h = (h * 31 + s.charCodeAt(i)) | 0; }
    return (h >>> 0).toString(36);
}

// Places (or replaces) markers for every downloaded feature of a layer, removing any
// markers left over from a previous download that no longer have a matching feature —
// mirrors the Java receiver's layerMarkers diff-and-remove-old-then-add-new behaviour.
export async function syncLayerMarkers(
    layerUrl: string, features: DownloadedFeature[], visible: boolean, displayConfig: DisplayConfig | null,
): Promise<void> {
    const previous = store.layerMarkerUids[layerUrl] ?? [];
    const nextUids: string[] = [];

    for (const f of features) {
        const uid = markerUid(layerUrl, f.uid);
        // When hidden, don't record the uid as present — leaving nextUids empty means the
        // removal loop below tears down every previously-placed marker for this layer (that's
        // what makes Hide actually clear the map, not just flip the eye icon). Re-showing
        // re-downloads and re-adds them fresh.
        if (!visible) continue;
        nextUids.push(uid);

        let color: number;
        let label = f.callsign;
        let remarks = f.remarks;
        let iconsetPath: string | null = null;

        if (displayConfig) {
            iconsetPath = resolveIconsetPath(displayConfig, f.attributes);
            const hex = iconsetPath ? '#FFFFFF' : resolveColor(displayConfig, f.attributes);
            color = hexToArgb(hex);
            label = resolveLabel(displayConfig, f.attributes, f.callsign);
            const popupRemarks = buildRemarks(displayConfig, f.attributes);
            remarks = popupRemarks && f.remarks ? `${popupRemarks}\n${f.remarks}` : (popupRemarks || f.remarks);
        } else {
            color = hexToArgb('#3388ff');
        }

        await upsertFeature(uid, f.lat, f.lon, label, f.cotType, color, remarks, iconsetPath);
    }

    for (const uid of previous) {
        if (!nextUids.includes(uid)) await removeMarker(uid);
    }
    store.layerMarkerUids[layerUrl] = nextUids;
}

export async function removeLayerMarkers(layerUrl: string): Promise<void> {
    const uids = store.layerMarkerUids[layerUrl] ?? [];
    for (const uid of uids) await removeMarker(uid);
    delete store.layerMarkerUids[layerUrl];
}

// ── PLI history breadcrumbs (ported from PliHistoryOverlay.java) ──────────────

const PLI_HISTORY_UID_PREFIX = 'fl-pli-history-';
const MAX_HISTORY = 5;
const ALPHA_TABLE = [255, 210, 160, 110, 60]; // index 0 = newest

interface PliHistoryPoint { lat: number; lon: number }
let pliHistory: PliHistoryPoint[] = [];

export async function pushPliHistory(lat: number, lon: number, callsign: string): Promise<void> {
    pliHistory = [{ lat, lon }, ...pliHistory].slice(0, MAX_HISTORY);
    const baseHex = store.pliTeamColor.replace('#', '');

    // Re-tint every position from the fade table on each add rather than aging by timer.
    for (let i = 0; i < pliHistory.length; i++) {
        const uid = `${PLI_HISTORY_UID_PREFIX}${i}`;
        const alpha = ALPHA_TABLE[i] ?? ALPHA_TABLE[ALPHA_TABLE.length - 1];
        const argb = ((alpha << 24) | parseInt(baseHex, 16)) >>> 0;
        const p = pliHistory[i];
        await upsertFeature(uid, p.lat, p.lon, `${callsign} (PLI history)`, 'a-f-G', argb, '');
    }
}

export async function clearPliHistory(): Promise<void> {
    for (let i = 0; i < MAX_HISTORY; i++) await removeMarker(`${PLI_HISTORY_UID_PREFIX}${i}`);
    pliHistory = [];
}

// ── Map utilities ───────────────────────────────────────────────────────────

export function flyToLocation(lat: number, lon: number, zoom = 13): void {
    getPluginApi()?.map.flyTo({ center: [lon, lat], zoom });
}

export interface CotMarker { uid: string; callsign: string; lat: number; lon: number; cotType: string }

// On-screen CoT markers, for the Send-to-Layer picker (radial-menu replacement). Uses the
// public MapLibre queryRenderedFeatures() — viewport-limited to on-screen features; a manual
// paste-a-UID field covers anything off-screen. Skips this plugin's own markers.
export function listCotMarkers(): CotMarker[] {
    const api = getPluginApi();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const map = (api as any)?.map;
    let feats: unknown[];
    try { feats = map?.queryRenderedFeatures?.() ?? []; } catch { feats = []; }

    const seen = new Set<string>();
    const out: CotMarker[] = [];
    for (const f of feats) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const ff = f as any;
        const uid = String(ff?.id ?? ff?.properties?.uid ?? ff?.properties?.id ?? '');
        const callsign = String(ff?.properties?.callsign ?? ff?.properties?.name ?? '');
        const coords = ff?.geometry?.coordinates;
        if (!uid || !callsign || uid.startsWith('fl-') || seen.has(uid) || !Array.isArray(coords)) continue;
        seen.add(uid);
        out.push({ uid, callsign, lon: coords[0], lat: coords[1], cotType: String(ff?.properties?.type ?? 'a-f-G') });
    }
    return out.sort((a, b) => a.callsign.localeCompare(b.callsign));
}

// Looks up a single on-screen marker by UID — used when the user pastes a UID manually
// instead of picking from the list (still viewport-limited; same caveat as listCotMarkers).
export function findCotMarker(uid: string): CotMarker | undefined {
    return listCotMarkers().find(m => m.uid === uid);
}

// Best-effort read of the operator's own CoT position, for PLI auto-send. NOT CONFIRMED —
// CloudTAK's public PluginAPI has no documented self-position accessor at the time of writing
// (same gap StatusBoard's cot.ts flags for pullCotPosition()); this tries the plausible
// mapStore.currentUser shape and returns null if that's not what's actually there, so callers
// degrade to "auto-send unavailable" rather than sending garbage coordinates.
let warnedNoSelfPosition = false;
export function getSelfPosition(): { lat: number; lon: number; callsign: string } | null {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const cu = (_mapStore as any)?.currentUser;
    const coords = cu?.geometry?.coordinates;
    if (Array.isArray(coords) && coords.length >= 2) {
        return { lat: coords[1], lon: coords[0], callsign: cu?.properties?.callsign ?? 'Self' };
    }
    if (!warnedNoSelfPosition) {
        warnedNoSelfPosition = true;
        console.warn('[featurelink] self position not available from mapStore — PLI auto-send disabled until CloudTAK exposes a confirmed self-position API');
    }
    return null;
}
