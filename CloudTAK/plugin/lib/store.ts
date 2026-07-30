// Reactive state + localStorage persistence — the CloudTAK-side equivalent of the ATAK
// plugin's SharedPreferences (PREF_LAYERS_JSON, PREF_PUBLIC_LAYERS_JSON, PREF_PLI_LAYER_URL,
// PREF_PLI_OBJECT_ID, PREF_PLI_AUTO_SEND, PREF_EXCLUDED_PRIVATE_URLS), collapsed into one
// JSON blob. Mirrors the shape of CloudTAK-Plugin_StatusBoard_CAP's lib/store.ts.

import { reactive, watch } from 'vue';
import type { ArcGISLayer, DisplayConfig } from './types.ts';

export interface FeatureLinkState {
    // "My ArcGIS Layers" — browse list of the signed-in user's own ArcGIS content, refreshed
    // from a portal search. Not yet on the device; moves into privateLayers/publicLayers (see
    // moveBrowseLayerOnDownload in layerActions.ts) the first time it's downloaded.
    browseLayers: ArcGISLayer[];
    // "Private Layers" — on-device layers not shared to Everyone: either shared to you by
    // another user/config import, or downloaded from browseLayers and not public.
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
}

const KEY = 'cloudtak-featurelink:v1';

function defaults(): FeatureLinkState {
    return {
        browseLayers: [],
        privateLayers: [],
        publicLayers: [],
        excludedPrivateUrls: [],
        displayConfigs: {},
        layerMarkerUids: {},
        portalUrl: 'https://www.arcgis.com',
        pliLayerUrl: '',
        pliObjectId: -1,
        pliAutoSend: false,
        pliTeamColor: '#00FFFF',
    };
}

function load(): FeatureLinkState {
    try {
        const raw = localStorage.getItem(KEY);
        if (raw) return { ...defaults(), ...(JSON.parse(raw) as Partial<FeatureLinkState>) };
    } catch { /* fall through to defaults */ }
    return defaults();
}

export const store = reactive<FeatureLinkState>(load());

export function save(): void {
    try { localStorage.setItem(KEY, JSON.stringify(store)); } catch { /* quota */ }
}

watch(store, save, { deep: true });

export function layersOf(kind: 'private' | 'public'): ArcGISLayer[] {
    return kind === 'private' ? store.privateLayers : store.publicLayers;
}

export function findLayer(url: string): ArcGISLayer | undefined {
    return store.privateLayers.find(l => l.url === url) ?? store.publicLayers.find(l => l.url === url);
}

export function isPliConnected(authenticated: boolean): boolean {
    return store.pliLayerUrl !== '' && authenticated;
}

export function setPliLayerUrl(url: string): void {
    store.pliLayerUrl = url;
    store.pliObjectId = -1; // new endpoint — forget any remembered feature id
}
