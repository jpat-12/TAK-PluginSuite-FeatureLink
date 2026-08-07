// Self-scheduling ports of FeatureLinkDropDownReceiver's two background loops:
//   - checkLayerRecurrence(): periodic per-layer auto-refresh, driven by each layer's own
//     recurrenceInterval/recurrenceUnit (ArcGISLayer.recurrenceMillis()).
//   - startPliScheduler()/sendPliUpdate(): a 30-second PLI auto-send tick with
//     add-then-update-by-objectId semantics.
//
// C-26: both loops used `setInterval`, whose callback awaited work that can take minutes. Overlapping
// executions therefore stacked without bound — after ten minutes of one slow layer there were sixty
// concurrent sweeps all hammering ArcGIS and all racing on `store.layerMarkerUids`. Both are now
// scheduleWithFixedDelay chains: the next tick is scheduled only after the previous one finishes,
// and each tick additionally carries an in-flight guard and a hard per-operation timeout.

import { reactive, watch } from 'vue';
import type { WatchStopHandle } from 'vue';
import { store } from './store.ts';
import { recurrenceMillis } from './types.ts';
import type { ArcGISLayer, PliFeatureInput } from './types.ts';
import * as auth from './arcgisAuth.ts';
import * as rest from './arcgisRest.ts';
import { describeError } from './arcgisHttp.ts';
import { downloadLayer, layerErrors } from './layerActions.ts';
import { getSelfPosition, pushPliHistory } from './cot.ts';
import { withTimeout } from './asyncLock.ts';

// ── Layer recurrence auto-refresh ──────────────────────────────────────────────

const RECURRENCE_CHECK_MS = 10_000;
/** One layer may not stall every other layer's refresh (§9.2). */
const PER_LAYER_TIMEOUT_MS = 120_000;
const MAX_BACKOFF_MS = 30 * 60_000;

let recurrenceTimer: ReturnType<typeof setTimeout> | null = null;
let recurrenceRunning = false;

/** Consecutive-failure counts, for exponential backoff. Not persisted — a reload is a fresh start. */
const failureCounts = new Map<string, number>();

function backoffMs(url: string): number {
    const failures = failureCounts.get(url) ?? 0;
    if (failures === 0) return 0;
    return Math.min(MAX_BACKOFF_MS, RECURRENCE_CHECK_MS * 2 ** Math.min(failures, 8));
}

function isDue(layer: ArcGISLayer, now: number): boolean {
    // lastSync === 0 means never manually downloaded — auto-refresh only kicks in once a layer has
    // had its first explicit download, rather than treating "never synced" as infinitely overdue.
    if (layer.lastSync <= 0) return false;
    const period = recurrenceMillis(layer);
    if (period <= 0) return false;
    return now - layer.lastSync >= period + backoffMs(layer.url);
}

export async function checkLayerRecurrence(): Promise<void> {
    if (recurrenceRunning) return; // re-entrancy guard
    recurrenceRunning = true;
    try {
        const now = Date.now();
        for (const layer of [...store.privateLayers, ...store.publicLayers]) {
            if (!isDue(layer, now)) continue;
            try {
                await withTimeout(PER_LAYER_TIMEOUT_MS, `refresh of "${layer.name}" timed out`, () => downloadLayer(layer));
                failureCounts.delete(layer.url);
            } catch (e) {
                // A layer whose downloads always fail used to be retried every period forever with
                // no backoff and no error anywhere in the UI (§10.6).
                failureCounts.set(layer.url, (failureCounts.get(layer.url) ?? 0) + 1);
                layerErrors[layer.url] = `Auto-refresh failed: ${describeError(e)}`;
                console.warn('[featurelink] recurrence auto-refresh failed for', layer.name, e);
            }
        }
    } finally {
        recurrenceRunning = false;
    }
}

export function startRecurrenceScheduler(): void {
    if (recurrenceTimer) return;
    const tick = (): void => {
        void checkLayerRecurrence().finally(() => {
            if (recurrenceTimer !== null) recurrenceTimer = setTimeout(tick, RECURRENCE_CHECK_MS);
        });
    };
    recurrenceTimer = setTimeout(tick, RECURRENCE_CHECK_MS);
}

export function stopRecurrenceScheduler(): void {
    if (recurrenceTimer) { clearTimeout(recurrenceTimer); recurrenceTimer = null; }
}

// ── PLI auto-send ─────────────────────────────────────────────────────────────

const PLI_TICK_MS = 30_000;
/** Stale time is a MULTIPLE of the send interval — equal to it meant the contact went stale at the exact moment the next fix arrived (§10.6). */
const PLI_STALE_MULTIPLIER = 3;
const PLI_TIMEOUT_MS = 20_000;

let pliTimer: ReturnType<typeof setTimeout> | null = null;
let pliRunning = false;

/**
 * Reactive PLI health, rendered by PliTab/HomeTab. `sendPliUpdate` used to swallow every failure
 * into a `console.warn`, and both the null-token and null-self-position paths returned silently, so
 * the feed could be completely dead while the UI reported "Auto-Send: On" — unacceptable for a
 * position-reporting feature (§10.6, Top-10 #4).
 */
export const pliState = reactive<{
    lastSuccess: number;
    lastError: string;
    lastAttempt: number;
    active: boolean;
}>({ lastSuccess: 0, lastError: '', lastAttempt: 0, active: false });

/** Per-device suffix so one operator in two tabs does not produce two rows racing for one contact (§10.6). */
function deviceId(): string {
    const KEY = 'cloudtak-featurelink:device-id';
    try {
        let id = localStorage.getItem(KEY);
        if (!id) { id = crypto.randomUUID().slice(0, 8); localStorage.setItem(KEY, id); }
        return id;
    } catch {
        return 'nodevice';
    }
}

export async function sendPliUpdate(): Promise<void> {
    if (pliRunning) return;
    pliRunning = true;
    pliState.lastAttempt = Date.now();
    try {
        if (!store.pliLayerUrl) { pliState.lastError = 'No PLI layer configured.'; return; }

        const token = await auth.getToken();
        if (!token) { pliState.lastError = 'Signed out of ArcGIS — PLI is not being transmitted.'; return; }

        const self = getSelfPosition();
        if (!self) {
            pliState.lastError = 'Your own position is not available from CloudTAK — PLI is not being transmitted.';
            return;
        }

        const now = Date.now();
        const input: PliFeatureInput = {
            uid: `cloudtak-self-${auth.getUsername() ?? 'unknown'}-${deviceId()}`,
            cotType: 'a-f-G-U-C',
            callsign: self.callsign,
            iconPath: '',
            remarks: '',
            how: 'm-g',
            sentByUser: auth.getUsername() ?? '',
            groupName: '',
            groupRole: '',
            lat: self.lat, lon: self.lon, hae: NaN, ce: NaN, le: NaN,
            timeMs: now, startMs: now, staleMs: now + PLI_STALE_MULTIPLIER * PLI_TICK_MS,
            rawCotXml: '',
        };

        const pliUrl = store.pliLayerUrl;
        await withTimeout(PLI_TIMEOUT_MS, 'PLI send timed out', async () => {
            if (store.pliObjectId === -1) {
                const result = await rest.addPliFeature(pliUrl, token, input);
                if (!result.ok) throw new Error(result.error ?? 'ArcGIS rejected the PLI insert');
                if (result.objectId >= 0) {
                    store.pliObjectId = result.objectId;
                } else {
                    // The add SUCCEEDED but the server did not echo an objectId. Recover it by
                    // querying back on `uid`; treating this as a failure made the next tick add
                    // another row, growing the shared PLI layer without bound (§10.6).
                    store.pliObjectId = await rest.findPliObjectId(pliUrl, token, input.uid);
                }
            } else {
                const result = await rest.updatePliFeature(pliUrl, token, store.pliObjectId, input);
                if (!result.ok) {
                    store.pliObjectId = -1; // feature likely gone server-side — re-add next tick
                    throw new Error(result.error ?? 'ArcGIS rejected the PLI update');
                }
            }
        });

        await pushPliHistory(self.lat, self.lon, self.callsign);
        pliState.lastSuccess = Date.now();
        pliState.lastError = '';
    } catch (e) {
        pliState.lastError = describeError(e);
        console.warn('[featurelink] PLI auto-send tick failed', e);
    } finally {
        pliRunning = false;
    }
}

export function startPliScheduler(): void {
    if (pliTimer) return;
    pliState.active = true;
    const tick = (): void => {
        void sendPliUpdate().finally(() => {
            if (pliTimer !== null) pliTimer = setTimeout(tick, PLI_TICK_MS);
        });
    };
    pliTimer = setTimeout(tick, 0); // first tick immediately, matching scheduleAtFixedRate(..., 0, 30, SECONDS)
}

export function stopPliScheduler(): void {
    if (pliTimer) { clearTimeout(pliTimer); pliTimer = null; }
    pliState.active = false;
}

// Keep the PLI scheduler's running state in sync with the (persisted) auto-send toggle, so a page
// reload with auto-send previously on resumes sending without extra wiring. The watcher's stop
// handle is retained so disable() can tear it down — it used to be registered at module scope
// behind a boolean and could never be stopped, which kept PLI transmitting after plugin disable
// (§10.6 + §6).
let pliWatchStop: WatchStopHandle | null = null;

export function watchPliAutoSend(): void {
    if (pliWatchStop) return;
    pliWatchStop = watch(
        () => store.pliAutoSend,
        (on) => { if (on) startPliScheduler(); else stopPliScheduler(); },
        { immediate: true },
    );
}

export function unwatchPliAutoSend(): void {
    if (pliWatchStop) { pliWatchStop(); pliWatchStop = null; }
    stopPliScheduler();
}
