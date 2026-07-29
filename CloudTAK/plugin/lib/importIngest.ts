// Auto-detects ATAK "Share" Mission Packages that landed in CloudTAK's own generic Import
// Manager instead of being usable by this plugin directly. ATAK's Share button (LayerShareHelper
// .buildShareConfigJson + MissionPackageApi.Send, see FeatureLinkDropDownReceiver) only knows how
// to transport a layer config to another ATAK device — there's no CloudTAK-aware send path — so
// when the recipient is a CloudTAK session, the package lands in CloudTAK's built-in Import
// Manager, which tries to build a map tileset from it and fails (it's a small JSON config, not
// spatial data). The original uploaded .zip survives that failure and stays downloadable, and
// inside it is a `<name>.featurelinkshare` file (see LayerShareHelper.java) — this polls for
// exactly that and, when found, applies it the same way Add Layer → Import Config does.
// (Was ".featurelink.json" — renamed because WinTAK's own Mission-Package auto-import chain
// throws an unhandled exception trying to MGRS-decode any unrecognized ".json" attachment as a
// GRG. The file's contents are still plain JSON regardless of the extension.)
//
// This has to reach past PluginAPI's documented surface to call CloudTAK's own /api/import
// endpoints directly (see cloudtakInternals.ts for the token access, which is the fragile part).
// Every failure path here updates `ingestState` and logs to the console, so a future CloudTAK
// change that breaks this shows up as a visible "Auto-Import: Error" status (see HomeTab.vue)
// instead of silently never importing anything.

import { reactive } from 'vue';
import { getCloudTakToken } from './cloudtakInternals.ts';
import { findZipEntryText } from './zipReader.ts';
import { applyConfigText } from './importConfig.ts';

const POLL_MS = 60_000;
const HANDLED_KEY = 'cloudtak-featurelink:handled-imports';
// CloudTAK's /api/import `filter` query param is a plain substring match on the name, NOT a
// regex — an anchored '^FeatureLink' matched literally nothing and the list came back empty, so
// nothing was ever ingested. We fetch the most recent imports unfiltered and match the name
// client-side instead, which is robust regardless of the filter param's exact server semantics.
// Matches LayerShareHelper.java's "FeatureLink - <layer>.zip" package naming.
const NAME_PREFIX = 'FeatureLink';
const ENTRY_SUFFIX = '.featurelinkshare';

export const ingestState = reactive<{ status: 'idle' | 'ok' | 'error'; message: string }>({
    status: 'idle',
    message: '',
});

interface ImportListItem { id: string; name: string }
interface ImportListResponse { total?: number; items?: ImportListItem[] }

function loadHandled(): Set<string> {
    try {
        const raw = localStorage.getItem(HANDLED_KEY);
        if (raw) return new Set(JSON.parse(raw) as string[]);
    } catch { /* ignore */ }
    return new Set();
}

function saveHandled(handled: Set<string>): void {
    try { localStorage.setItem(HANDLED_KEY, JSON.stringify(Array.from(handled).slice(-200))); }
    catch { /* quota */ }
}

async function apiGet<T>(path: string, token: string): Promise<T> {
    const res = await fetch(path, { headers: { Authorization: `Bearer ${token}` } });
    if (!res.ok) throw new Error(`${path} → HTTP ${res.status}`);
    return res.json() as Promise<T>;
}

async function processPackage(item: ImportListItem, token: string): Promise<void> {
    const rawRes = await fetch(`/api/import/${encodeURIComponent(item.id)}/raw`, {
        headers: { Authorization: `Bearer ${token}` },
    });
    if (!rawRes.ok) throw new Error(`raw fetch → HTTP ${rawRes.status}`);
    const zipBuf = await rawRes.arrayBuffer();

    const text = await findZipEntryText(zipBuf, ENTRY_SUFFIX);
    if (text === null) return; // named like a FeatureLink share but no matching entry inside — ignore

    const result = await applyConfigText(text);
    ingestState.status = result.ok ? 'ok' : 'error';
    ingestState.message = result.ok
        ? `Auto-imported "${item.name}": ${result.message}`
        : `"${item.name}" failed to apply: ${result.message}`;
}

async function checkOnce(): Promise<void> {
    const token = getCloudTakToken();
    if (!token) return; // not signed into CloudTAK yet (or storage key changed — see cloudtakInternals.ts)

    const handled = loadHandled();
    // Fetch a generous recent window: since we filter by name client-side (not server-side), a
    // burst of unrelated imports between polls could otherwise push a FeatureLink share off the
    // page before it's seen. 50 is plenty of headroom at expected volume.
    const list = await apiGet<ImportListResponse>(
        `/api/import?limit=50&sort=created&order=desc`,
        token,
    );

    for (const item of list.items ?? []) {
        if (!item.name?.startsWith(NAME_PREFIX)) continue;
        // `handled` only ever contains *failed* imports (see catch below) — a successful apply
        // is safe to recheck every poll instead of being permanently skipped, since
        // applyConfigText() is itself idempotent (findLayer()/publicLayers.some() no-ops if the
        // layer's already present). That matters because CloudTAK appears to derive an import's
        // id from the package's content: re-sharing the exact same (unchanged) layer from ATAK
        // can land on the same id as before, so a permanent "seen this id" skip would silently
        // block re-ingesting a layer the user deliberately deleted and shared again.
        if (handled.has(item.id)) continue;

        try {
            await processPackage(item, token);
        } catch (e) {
            handled.add(item.id);
            saveHandled(handled);
            ingestState.status = 'error';
            ingestState.message = `Auto-import failed for "${item.name}": ${e instanceof Error ? e.message : String(e)}`;
            console.error('[featurelink] import ingest failed for', item.name, e);
        }
    }
}

function reportSchedulerFailure(e: unknown): void {
    // Reached only for failures outside the per-package try/catch above — e.g. the /api/import
    // list call itself failing. Most likely cause: CloudTAK changed its token storage or API
    // shape (see cloudtakInternals.ts) since this was written against a specific CloudTAK build.
    ingestState.status = 'error';
    ingestState.message = `Import auto-detection isn't working: ${e instanceof Error ? e.message : String(e)}`;
    console.error('[featurelink] import ingest scheduler failed — CloudTAK internals may have changed', e);
}

let timer: ReturnType<typeof setInterval> | null = null;
export function startImportIngestScheduler(): void {
    if (timer) return;
    timer = setInterval(() => { void checkOnce().catch(reportSchedulerFailure); }, POLL_MS);
    void checkOnce().catch(reportSchedulerFailure); // don't wait a full minute for the first check

    // Browser tabs throttle/suspend setInterval timers when backgrounded or the OS suspends the
    // process (laptop sleep/lock) — the interval above can silently stop firing for long
    // stretches, so a package shared while the tab was backgrounded previously only got picked
    // up once the user manually reloaded the page. Re-checking on visibility regain catches it
    // up as soon as the tab is looked at again, without needing a reload.
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') void checkOnce().catch(reportSchedulerFailure);
    });
}

// Manual trigger for the settings-menu "Check for Shared Configs" button — lets the user force
// an immediate check instead of waiting on the 60s interval (see startImportIngestScheduler()).
export async function checkForSharedConfigsNow(): Promise<void> {
    await checkOnce().catch(reportSchedulerFailure);
}

export function stopImportIngestScheduler(): void {
    if (timer) { clearInterval(timer); timer = null; }
}
