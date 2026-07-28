// setInterval-based ports of FeatureLinkDropDownReceiver's two background loops:
//   - checkLayerRecurrence(): periodic per-layer auto-refresh, driven by each layer's own
//     recurrenceInterval/recurrenceUnit (ArcGISLayer.recurrenceMillis()).
//   - startPliScheduler()/sendPliUpdate(): a hardcoded 30-second PLI auto-send tick with
//     add-then-update-by-objectId semantics.

import { watch } from 'vue';
import { store } from './store.ts';
import { recurrenceMillis } from './types.ts';
import type { PliFeatureInput } from './types.ts';
import * as auth from './arcgisAuth.ts';
import * as rest from './arcgisRest.ts';
import { downloadLayer } from './layerActions.ts';
import { getSelfPosition, pushPliHistory } from './cot.ts';

// ── Layer recurrence auto-refresh ──────────────────────────────────────────────

const RECURRENCE_CHECK_MS = 10_000;
let recurrenceTimer: ReturnType<typeof setInterval> | null = null;

async function checkLayerRecurrence(): Promise<void> {
    const now = Date.now();
    for (const layer of [...store.privateLayers, ...store.publicLayers]) {
        // lastSync === 0 means never manually downloaded yet — skip it here rather than treating
        // "never synced" as "infinitely overdue" and auto-downloading every newly-listed layer
        // (e.g. right after sign-in fetches the user's whole owned-layer list) before the user
        // has asked for any of them. Auto-refresh only kicks in once a layer's had its first
        // manual download (the ⬇→↻ action button, see LayerRow.vue).
        if (layer.lastSync <= 0) continue;
        const period = recurrenceMillis(layer);
        if (period > 0 && now - layer.lastSync >= period) {
            try { await downloadLayer(layer); }
            catch (e) { console.warn('[featurelink] recurrence auto-refresh failed for', layer.name, e); }
        }
    }
}

export function startRecurrenceScheduler(): void {
    if (recurrenceTimer) return;
    recurrenceTimer = setInterval(() => { void checkLayerRecurrence(); }, RECURRENCE_CHECK_MS);
}

export function stopRecurrenceScheduler(): void {
    if (recurrenceTimer) { clearInterval(recurrenceTimer); recurrenceTimer = null; }
}

// ── PLI auto-send (hardcoded 30s tick, matching the Java scheduler) ───────────

const PLI_TICK_MS = 30_000;
let pliTimer: ReturnType<typeof setInterval> | null = null;

async function sendPliUpdate(): Promise<void> {
    if (!store.pliLayerUrl) return;
    const token = await auth.getToken();
    if (!token) return; // session expired — user must sign in again

    const self = getSelfPosition();
    if (!self) return; // no confirmed self-position source available (see cot.ts)

    const now = Date.now();
    const input: PliFeatureInput = {
        uid: `cloudtak-self-${auth.getUsername() ?? 'unknown'}`,
        cotType: 'a-f-G-U-C',
        callsign: self.callsign,
        iconPath: '',
        remarks: '',
        how: 'm-g',
        sentByUser: auth.getUsername() ?? '',
        groupName: '',
        groupRole: '',
        lat: self.lat, lon: self.lon, hae: NaN, ce: NaN, le: NaN,
        timeMs: now, startMs: now, staleMs: now + PLI_TICK_MS,
        rawCotXml: '',
    };

    try {
        if (store.pliObjectId === -1) {
            const objectId = await rest.addPliFeature(store.pliLayerUrl, token, input);
            if (objectId >= 0) store.pliObjectId = objectId;
        } else {
            const ok = await rest.updatePliFeature(store.pliLayerUrl, token, store.pliObjectId, input);
            if (!ok) store.pliObjectId = -1; // feature likely gone server-side — re-add next tick
        }
        await pushPliHistory(self.lat, self.lon, self.callsign);
    } catch (e) {
        console.warn('[featurelink] PLI auto-send tick failed', e);
    }
}

export function startPliScheduler(): void {
    if (pliTimer) return;
    pliTimer = setInterval(() => { void sendPliUpdate(); }, PLI_TICK_MS);
    void sendPliUpdate(); // first tick immediately, matching scheduleAtFixedRate(..., 0, 30, SECONDS)
}

export function stopPliScheduler(): void {
    if (pliTimer) { clearInterval(pliTimer); pliTimer = null; }
}

// Keep the PLI scheduler's running state in sync with the (persisted) auto-send toggle,
// so a page reload with auto-send previously on resumes sending without extra wiring.
let pliWatchStarted = false;
export function watchPliAutoSend(): void {
    if (pliWatchStarted) return;
    pliWatchStarted = true;
    watch(() => store.pliAutoSend, (on) => { if (on) startPliScheduler(); else stopPliScheduler(); }, { immediate: true });
}
