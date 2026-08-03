// CoT / TAK map integration.
//
// INTERNAL REACH-IN: CloudTAK's public PluginAPI doesn't expose a CoT write path (or a
// confirmed self-position read path) yet, so — exactly like CloudTAK-Plugin_StatusBoard_CAP's
// cot.ts — this reaches into `mapStore.worker.db` and node-cot's normalizer. Isolated here so
// there's one place to fix on a CloudTAK upgrade.
//
// Covers four things the ATAK plugin did with real ATAK map APIs:
//   1. One marker per downloaded point layer feature, styled via lib/displayConfig.ts
//      (FeatureLinkDropDownReceiver.downloadLayer's marker-build logic).
//   2. One polyline/polygon shape per downloaded line/polygon layer feature, styled via
//      lib/autoSymbology.ts + displayConfig.ts's resolveShapeStyle (FeatureLinkDropDownReceiver's
//      buildPolylineShapes()/buildPolygonShapes()) — see syncLayerShapes() below.
//   3. A 5-point fading PLI history breadcrumb trail (PliHistoryOverlay.java).
//   4. listCotMarkers() for the send-to-layer picker (radial-menu replacement) and a
//      best-effort self-position reader for PLI auto-send (MapView.getSelfMarker()).

import { getPluginApi } from './plugin-api.ts';
import { store } from './store.ts';
import { resolveColor, resolveIconsetPath, resolveLabel, buildRemarks, resolveShapeStyle } from './displayConfig.ts';
import type { DisplayConfig, DownloadedFeature, ShapeStyle } from './types.ts';

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

// ── Colour handling ─────────────────────────────────────────────────────────
//
// One place for hex parsing. There used to be two, and they disagreed: `hexToArgb` accepted the
// 8-digit `#rrggbbaa` that `displayConfig.blendOpacity` produces, while `upsertShape`'s guard was
// `/^#[0-9a-fA-F]{6}$/`, so the SAME opacity-bearing config coloured markers correctly and shapes
// with the default blue (§10.1). `padEnd(6, clean)` also expanded the CSS shorthand `#abc` to
// `abcabc` instead of `aabbcc` — wrong colours for every 3-digit hex in a config.

const DEFAULT_HEX = '#3388ff';

interface ParsedColor { rgb: string; alpha: number }

export function parseHexColor(hex: string | undefined): ParsedColor | null {
    if (typeof hex !== 'string') return null;
    const clean = hex.trim().replace(/^#/, '');
    if (!/^[0-9a-fA-F]+$/.test(clean)) return null;
    if (clean.length === 3 || clean.length === 4) {
        // CSS shorthand: each digit is doubled.
        const expanded = Array.from(clean).map(c => c + c).join('');
        return { rgb: expanded.slice(0, 6), alpha: clean.length === 4 ? parseInt(expanded.slice(6, 8), 16) : 255 };
    }
    if (clean.length === 6) return { rgb: clean, alpha: 255 };
    if (clean.length === 8) return { rgb: clean.slice(0, 6), alpha: parseInt(clean.slice(6, 8), 16) };
    return null;
}

function hexToArgb(hex: string): number {
    const parsed = parseHexColor(hex) ?? parseHexColor(DEFAULT_HEX);
    if (!parsed) return 0xff3388ff;
    return ((parsed.alpha << 24) | parseInt(parsed.rgb, 16)) >>> 0;
}

/** `#rrggbb` for consumers (node-cot's stroke/fill) that cannot express alpha in the colour itself. */
function hexRgb(hex: string | undefined, fallback: string): string {
    const parsed = parseHexColor(hex);
    return parsed ? `#${parsed.rgb}` : fallback;
}

/** Alpha carried by an 8-digit colour, as a 0-1 opacity, or null when none was specified. */
function hexOpacity(hex: string | undefined): number | null {
    const parsed = parseHexColor(hex);
    if (!parsed || parsed.alpha === 255) return null;
    return parsed.alpha / 255;
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
            // FIX-6: the resolved colour is kept even when an icon path resolves. Forcing
            // '#FFFFFF' meant that if the icon file 404'd — likely, given partial iconset
            // registration — every marker rendered WHITE, which is indistinguishable from "no
            // symbology" to the operator and worse than the blue default.
            color = hexToArgb(resolveColor(displayConfig, f.attributes));
            label = resolveLabel(displayConfig, f.attributes, f.callsign);
            const popupRemarks = buildRemarks(displayConfig, f.attributes);
            remarks = popupRemarks && f.remarks ? `${popupRemarks}\n${f.remarks}` : (popupRemarks || f.remarks);
        } else {
            color = hexToArgb('#3388ff');
        }

        await upsertFeature(uid, f.lat, f.lon, label, f.cotType, color, remarks, iconsetPath);
    }

    // Set, not Array.includes — the nested scan was O(n²): 10,000 features meant 100 million
    // string comparisons on the main thread (§10.1).
    const nextSet = new Set(nextUids);
    for (const uid of previous) {
        if (!nextSet.has(uid)) await removeMarker(uid);
    }
    store.layerMarkerUids[layerUrl] = nextUids;
}

export async function removeLayerMarkers(layerUrl: string): Promise<void> {
    const uids = store.layerMarkerUids[layerUrl] ?? [];
    for (const uid of uids) await removeMarker(uid);
    delete store.layerMarkerUids[layerUrl];
}

// ── Downloaded-layer shapes (polyline/polygon) ──────────────────────────────
//
// Port of FeatureLinkDropDownReceiver's buildPolylineShapes()/buildPolygonShapes(). Styling
// comes from displayConfig.ts's resolveShapeStyle (esriSLS/esriSFS auto-symbology — see
// autoSymbology.ts), falling back to a default blue/solid/2px stroke with no fill when nothing
// resolves, same as the ATAK/WinTAK ports. NOTE: @tak-ps/node-cot's normalize_geojson (see
// initCot() above) has no dasharray concept, so ShapeStyle.strokeDash has no visible effect here
// — carried through the model for parity, not currently renderable.


async function upsertShape(
    uid: string, geomType: 'LineString' | 'Polygon' | 'MultiPolygon',
    coordinates: number[][] | number[][][] | number[][][][],
    callsign: string, cotType: string, remarks: string, style: ShapeStyle | null,
): Promise<void> {
    if (!_normalize || !_mapStore) return;
    try {
        // An 8-digit colour carries its own alpha; fold it into the separate opacity property
        // rather than rejecting the whole colour (§10.1).
        const strokeAlpha = hexOpacity(style?.strokeColor);
        const properties: Record<string, unknown> = {
            callsign, remarks,
            stroke: hexRgb(style?.strokeColor, DEFAULT_HEX),
            'stroke-opacity': strokeAlpha ?? style?.strokeOpacity ?? 1,
            'stroke-width': style?.strokeWidthPx ?? 2,
        };
        if (style && style.fillStyle !== 'none') {
            const fillAlpha = hexOpacity(style.fillColor);
            properties.fill = hexRgb(style.fillColor, DEFAULT_HEX);
            properties['fill-opacity'] = fillAlpha ?? style.fillOpacity;
        }
        const feat = {
            id: uid, type: 'Feature',
            properties,
            geometry: { type: geomType, coordinates },
        };
        const norm = await _normalize(feat) as Record<string, unknown>;
        const props = norm.properties as Record<string, unknown>;
        props.type = cotType;
        await _mapStore.worker.db.add(JSON.parse(JSON.stringify(norm)), { authored: true });
    } catch (e) {
        console.warn('[featurelink] upsertShape failed', e);
    }
}

// GeoJSON requires each Polygon ring to be closed (first coordinate === last); ArcGIS rings
// normally already are, but this closes defensively rather than trusting the source data.
function closeRing(ring: number[][]): number[][] {
    const first = ring[0];
    const last = ring[ring.length - 1];
    if (!first || !last) return ring;
    if (first[0] === last[0] && first[1] === last[1]) return ring;
    // Clone rather than appending the first vertex by reference — sharing the coordinate array
    // between the first and last positions is a latent aliasing bug (§10.1).
    return [...ring, [...first]];
}

/** Signed area of a ring; positive = clockwise in Esri's screen convention. */
function signedArea(ring: number[][]): number {
    let sum = 0;
    for (let i = 0; i < ring.length; i++) {
        const a = ring[i];
        const b = ring[(i + 1) % ring.length];
        if (!a || !b || a[0] === undefined || a[1] === undefined || b[0] === undefined || b[1] === undefined) continue;
        sum += (b[0] - a[0]) * (b[1] + a[1]);
    }
    return sum;
}

function rewind(ring: number[][], wantClockwise: boolean): number[][] {
    const isClockwise = signedArea(ring) > 0;
    return isClockwise === wantClockwise ? ring : [...ring].reverse();
}

/**
 * Splits an Esri `rings` array into GeoJSON polygons by winding order, and rewinds each to
 * RFC 7946 (outer counter-clockwise, holes clockwise). Returns a MultiPolygon when the feature has
 * more than one outer ring.
 */
export function ringsToGeoJson(rings: number[][][]): {
    type: 'Polygon' | 'MultiPolygon'; coordinates: number[][][] | number[][][][];
} {
    const polygons: number[][][] = [];
    const grouped: number[][][][] = [];
    for (const raw of rings) {
        if (raw.length < 3) continue;
        const ring = closeRing(raw);
        const isOuter = signedArea(raw) > 0;
        if (isOuter || grouped.length === 0) grouped.push([rewind(ring, false)]);
        else grouped[grouped.length - 1]?.push(rewind(ring, true));
        polygons.push(ring);
    }
    if (grouped.length === 0) return { type: 'Polygon', coordinates: [] };
    if (grouped.length === 1) return { type: 'Polygon', coordinates: grouped[0] as number[][][] };
    return { type: 'MultiPolygon', coordinates: grouped as number[][][][] };
}

// One shape-uid per path, sharing the same "{uid}-p{i}" sub-uid scheme as the ATAK/WinTAK ports
// for a multi-part feature; single-part features keep the feature's own uid unchanged.
function partUid(layerUrl: string, featureUid: string, index: number, total: number): string {
    const base = markerUid(layerUrl, featureUid);
    return total > 1 ? `${base}-p${index}` : base;
}

// Places (or replaces) polyline/polygon shapes for every downloaded feature of a layer, removing
// any shapes left over from a previous download that no longer have a matching feature — same
// diff-and-remove-old-then-add-new behaviour as syncLayerMarkers, and shares its store bucket
// (store.layerMarkerUids is keyed by layer URL regardless of geometry kind, since removal is
// just "delete this uid from the map").
export async function syncLayerShapes(
    layerUrl: string, features: DownloadedFeature[], visible: boolean, displayConfig: DisplayConfig | null,
    geometryKind: 'polyline' | 'polygon',
): Promise<void> {
    const previous = store.layerMarkerUids[layerUrl] ?? [];
    const nextUids: string[] = [];

    for (const f of features) {
        if (!visible) continue;

        const style = displayConfig ? resolveShapeStyle(displayConfig, f.attributes) : null;
        const label = displayConfig ? resolveLabel(displayConfig, f.attributes, f.callsign) : f.callsign;
        const popupRemarks = displayConfig ? buildRemarks(displayConfig, f.attributes) : '';
        const remarks = popupRemarks && f.remarks ? `${popupRemarks}\n${f.remarks}` : (popupRemarks || f.remarks);

        if (geometryKind === 'polyline') {
            const parts = f.paths.filter(p => p.length >= 2);
            // ATAK counts parts BEFORE filtering degenerate ones (FeatureLinkDropDownReceiver:1554).
            // Using the post-filter count here gave a 2-part polyline with one degenerate part a
            // different UID on CloudTAK than on ATAK — cross-platform UID divergence (§10.1).
            const atakPartCount = f.paths.length;
            for (let i = 0; i < parts.length; i++) {
                const part = parts[i];
                if (!part) continue;
                const uid = partUid(layerUrl, f.uid, i, atakPartCount);
                nextUids.push(uid);
                await upsertShape(uid, 'LineString', part, label, f.cotType, remarks, style);
            }
        } else {
            const outer = f.rings[0];
            if (!outer || outer.length < 3) continue;
            const uid = markerUid(layerUrl, f.uid);
            nextUids.push(uid);
            // Esri encodes multi-part polygons (several disjoint OUTER rings) in the same `rings`
            // array, distinguished by winding order: clockwise = outer, counter-clockwise = hole.
            // Emitting one GeoJSON Polygon treated every ring after the first as a hole, so a
            // county with islands rendered with its outer parts punched out (§10.1).
            const { type, coordinates } = ringsToGeoJson(f.rings);
            await upsertShape(uid, type, coordinates, label, f.cotType, remarks, style);
        }
    }

    // Set, not Array.includes — the nested scan was O(n²): 10,000 features meant 100 million
    // string comparisons on the main thread (§10.1).
    const nextSet = new Set(nextUids);
    for (const uid of previous) {
        if (!nextSet.has(uid)) await removeMarker(uid);
    }
    store.layerMarkerUids[layerUrl] = nextUids;
}

// ── PLI history breadcrumbs (ported from PliHistoryOverlay.java) ──────────────

const PLI_HISTORY_UID_PREFIX = 'fl-pli-history-';
const MAX_HISTORY = 5;
const ALPHA_TABLE = [255, 210, 160, 110, 60]; // index 0 = newest

interface PliHistoryPoint { lat: number; lon: number }
let pliHistory: PliHistoryPoint[] = [];

export async function pushPliHistory(lat: number, lon: number, callsign: string): Promise<void> {
    pliHistory = [{ lat, lon }, ...pliHistory].slice(0, MAX_HISTORY);
    // A persisted malformed team colour yielded NaN and therefore transparent-black breadcrumbs
    // (§10.1); fall back to the default cyan instead.
    const parsedTeam = parseHexColor(store.pliTeamColor);
    const baseHex = parsedTeam ? parsedTeam.rgb : '00FFFF';

    // Re-tint every position from the fade table on each add rather than aging by timer.
    for (let i = 0; i < pliHistory.length; i++) {
        const p = pliHistory[i];
        if (!p) continue;
        const uid = `${PLI_HISTORY_UID_PREFIX}${i}`;
        const alpha = ALPHA_TABLE[i] ?? 60;
        const argb = ((alpha << 24) | parseInt(baseHex, 16)) >>> 0;
        await upsertFeature(uid, p.lat, p.lon, `${callsign} (PLI history)`, 'a-f-G', argb, '');
    }
}

export async function clearPliHistory(): Promise<void> {
    for (let i = 0; i < MAX_HISTORY; i++) await removeMarker(`${PLI_HISTORY_UID_PREFIX}${i}`);
    pliHistory = [];
}

// ── Map utilities ───────────────────────────────────────────────────────────

export function flyToLocation(lat: number, lon: number, zoom = 13): void {
    // `map` is not part of the declared PluginAPI surface — this is a reach-in like the rest of
    // this module, so it is cast explicitly rather than assumed to exist.
    const map = (getPluginApi() as unknown as { map?: { flyTo?: (o: unknown) => void } } | null)?.map;
    map?.flyTo?.({ center: [lon, lat], zoom });
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
        const coords: unknown = ff?.geometry?.coordinates;
        if (!uid || !callsign || uid.startsWith('fl-') || seen.has(uid) || !Array.isArray(coords)) continue;
        const lon: unknown = coords[0], lat: unknown = coords[1];
        if (typeof lon !== 'number' || typeof lat !== 'number' || !Number.isFinite(lon) || !Number.isFinite(lat)) continue;
        seen.add(uid);
        out.push({ uid, callsign, lon, lat, cotType: String(ff?.properties?.type ?? 'a-f-G') });
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
