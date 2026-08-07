// Reactive state + localStorage persistence — the CloudTAK-side equivalent of the ATAK
// plugin's SharedPreferences (PREF_LAYERS_JSON, PREF_PUBLIC_LAYERS_JSON, PREF_PLI_LAYER_URL,
// PREF_PLI_OBJECT_ID, PREF_PLI_AUTO_SEND, PREF_EXCLUDED_PRIVATE_URLS), collapsed into one
// JSON blob.

import { reactive, watch } from 'vue';
import type { ArcGISLayer, DisplayConfig } from './types.ts';
import { migrateLayer } from './types.ts';

export interface FeatureLinkState {
    // "My ArcGIS Layers" — browse list of the signed-in user's own ArcGIS content, refreshed
    // from a portal search. Not yet on the device; moves into privateLayers/publicLayers the
    // first time it's downloaded.
    browseLayers: ArcGISLayer[];
    // "Private Layers" — on-device layers not shared to Everyone.
    privateLayers: ArcGISLayer[];
    publicLayers: ArcGISLayer[];
    excludedPrivateUrls: string[];
    displayConfigs: Record<string, DisplayConfig>;
    layerMarkerUids: Record<string, string[]>; // layer url -> CoT uids currently on the map
    portalUrl: string;
    pliLayerUrl: string;
    pliObjectId: number; // -1 = not yet created on the server
    pliAutoSend: boolean;
    pliTeamColor: string; // hex, mirrors PliHistoryOverlay's ATAK team-color concept
    /** Set when persistence itself fails, so the UI can warn instead of losing state silently. */
    persistError: string;
}

const KEY = 'cloudtak-featurelink:v1';
const SCHEMA_VERSION = 2;
const VERSION_KEY = 'cloudtak-featurelink:schema-version';

/**
 * Plain `{}` maps keyed by an ATTACKER-CONTROLLED URL string are a prototype-pollution sink:
 * `store.displayConfigs['__proto__'] = cfg` mutates Object.prototype for the whole CloudTAK tab
 * (§7.3). Null-prototype objects have no `__proto__` setter, so such an assignment becomes an
 * ordinary own property. `parseDisplayConfig` also rejects these keys; this is the structural
 * guarantee behind it.
 */
function emptyMap<T>(): Record<string, T> {
    return Object.create(null) as Record<string, T>;
}

function defaults(): FeatureLinkState {
    return {
        browseLayers: [],
        privateLayers: [],
        publicLayers: [],
        excludedPrivateUrls: [],
        displayConfigs: emptyMap<DisplayConfig>(),
        layerMarkerUids: emptyMap<string[]>(),
        portalUrl: 'https://www.arcgis.com',
        pliLayerUrl: '',
        pliObjectId: -1,
        pliAutoSend: false,
        pliTeamColor: '#00FFFF',
        persistError: '',
    };
}

const DANGEROUS_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

function sanitizeMap<T>(raw: unknown, valueOk: (v: unknown) => boolean): Record<string, T> {
    const out = emptyMap<T>();
    if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return out;
    for (const [k, v] of Object.entries(raw)) {
        if (DANGEROUS_KEYS.has(k)) continue;
        if (!valueOk(v)) continue;
        out[k] = v as T;
    }
    return out;
}

function sanitizeLayers(raw: unknown): ArcGISLayer[] {
    if (!Array.isArray(raw)) return [];
    const out: ArcGISLayer[] = [];
    const seen = new Set<string>();
    for (const item of raw) {
        if (typeof item !== 'object' || item === null) continue;
        const layer = migrateLayer(item as Partial<ArcGISLayer>);
        // A URL must be globally unique or two rows share one store.layerMarkerUids bucket and
        // each one's sync tears down the other's markers (§3.2).
        if (!layer || seen.has(layer.url)) continue;
        seen.add(layer.url);
        out.push(layer);
    }
    return out;
}

function sanitizeString(raw: unknown, fallback: string): string {
    return typeof raw === 'string' ? raw : fallback;
}

/**
 * Validates every field of the persisted blob. The old `{...defaults(), ...JSON.parse(raw)}` was a
 * SHALLOW merge, so a persisted `{"privateLayers": "not-an-array"}` replaced the array with a
 * string and every `.find`/`.splice`/`.map` threw at first use — the plugin was bricked with no
 * recovery path (§7.2). Anything that does not validate falls back to its default, and legacy
 * layer objects are run through migrateLayer so missing `visible`/`access`/`geometryType` no longer
 * silently hide them.
 */
function load(): FeatureLinkState {
    const base = defaults();
    let raw: unknown;
    try {
        const text = localStorage.getItem(KEY);
        if (!text) return base;
        raw = JSON.parse(text);
    } catch {
        return base;
    }
    if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return base;
    const o = raw as Record<string, unknown>;

    const privateLayers = sanitizeLayers(o.privateLayers);
    const publicLayers = sanitizeLayers(o.publicLayers).filter(l => !privateLayers.some(p => p.url === l.url));
    const onDeviceUrls = new Set([...privateLayers, ...publicLayers].map(l => l.url));
    const browseLayers = sanitizeLayers(o.browseLayers).filter(l => !onDeviceUrls.has(l.url));

    return {
        browseLayers,
        privateLayers,
        publicLayers,
        excludedPrivateUrls: Array.isArray(o.excludedPrivateUrls)
            ? o.excludedPrivateUrls.filter((u): u is string => typeof u === 'string')
            : [],
        displayConfigs: sanitizeMap<DisplayConfig>(o.displayConfigs, v => typeof v === 'object' && v !== null && !Array.isArray(v)),
        layerMarkerUids: sanitizeMap<string[]>(o.layerMarkerUids, v => Array.isArray(v) && v.every(x => typeof x === 'string')),
        portalUrl: sanitizeString(o.portalUrl, base.portalUrl),
        pliLayerUrl: sanitizeString(o.pliLayerUrl, ''),
        pliObjectId: typeof o.pliObjectId === 'number' && Number.isFinite(o.pliObjectId) ? o.pliObjectId : -1,
        pliAutoSend: o.pliAutoSend === true,
        pliTeamColor: /^#[0-9a-fA-F]{3,8}$/.test(sanitizeString(o.pliTeamColor, ''))
            ? sanitizeString(o.pliTeamColor, base.pliTeamColor)
            : base.pliTeamColor,
        persistError: '',
    };
}

export const store = reactive<FeatureLinkState>(load());

try { localStorage.setItem(VERSION_KEY, String(SCHEMA_VERSION)); } catch { /* non-fatal */ }

/**
 * `-Infinity`/`Infinity` (written by displayConfig.fromMode3 for class breaks with absent bounds)
 * serialize to `null`, and after one persistence round trip `val >= null && val < null` is false
 * for everything — class-break symbology silently stopped working after a page reload (§7.3).
 * Finite sentinels survive the round trip.
 */
function replacer(_key: string, value: unknown): unknown {
    if (typeof value === 'number' && !Number.isFinite(value)) {
        if (value === Infinity) return Number.MAX_SAFE_INTEGER;
        if (value === -Infinity) return Number.MIN_SAFE_INTEGER;
        return 0; // NaN
    }
    return value;
}

export function save(): void {
    try {
        localStorage.setItem(KEY, JSON.stringify(store, replacer));
        if (store.persistError) store.persistError = '';
    } catch (e) {
        // A quota failure used to be swallowed, which meant TOTAL silent loss of persistence:
        // layers, PLI endpoint and configs vanished on the next reload with no warning (§7.3).
        store.persistError = e instanceof Error && /quota/i.test(e.message)
            ? 'Browser storage is full — your layers and settings are no longer being saved. Remove some layers to free space.'
            : 'Your layers and settings could not be saved to this browser.';
        console.error('[featurelink] persistence failed', e);
    }
}

// Debounced: the deep watcher serialized the ENTIRE store on every mutation, including every
// `layer.featureCount` assignment during a refresh sweep, with `layerMarkerUids` holding one string
// per placed marker. A 10,000-feature layer therefore re-stringified a 10,000-element array on
// every single assignment — main-thread stalls measured in seconds (§7.3).
const SAVE_DEBOUNCE_MS = 500;
let saveTimer: ReturnType<typeof setTimeout> | null = null;

export function scheduleSave(): void {
    if (saveTimer) return;
    saveTimer = setTimeout(() => { saveTimer = null; save(); }, SAVE_DEBOUNCE_MS);
}

/** Flushes any pending debounced write immediately (tests, and beforeunload). */
export function flushSave(): void {
    if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; }
    save();
}

watch(store, scheduleSave, { deep: true });
if (typeof window !== 'undefined') {
    window.addEventListener('beforeunload', () => { if (saveTimer) flushSave(); });
}

export function layersOf(kind: 'private' | 'public'): ArcGISLayer[] {
    return kind === 'private' ? store.privateLayers : store.publicLayers;
}

/** Reads authentication itself rather than taking it as a parameter every caller could get wrong (§7.3). */
export function isPliConnected(): boolean {
    return store.pliLayerUrl !== '';
}

export function setPliLayerUrl(url: string): void {
    store.pliLayerUrl = url.trim();
    store.pliObjectId = -1; // new endpoint — forget any remembered feature id
}

/** Full reset — the only recovery path when persisted state is unusable (§7.2). */
export function resetStore(): void {
    Object.assign(store, defaults());
    try { localStorage.removeItem(KEY); } catch { /* ignore */ }
}
