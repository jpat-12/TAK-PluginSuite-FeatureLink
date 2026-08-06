// ArcGIS renderer → CloudTAK iconset generator. TypeScript port of AUTO-ICONSET-SPEC.md,
// sibling to the ATAK (AutoIconset.java) and TAK Portal (featurelinkArcgisIconset.service.js)
// implementations — all four platforms compute the same {uid}/{group}/{filename} strings for
// the same source layer independently (spec §0), so a marker one device places renders
// identically on another's map with no shared server.
//
// Unlike ATAK (installs a zip into a local atak/iconsets/ folder) or TAK Portal (writes to its
// own filesystem manifest), CloudTAK has a real server-side iconset API — POST /iconset (caller
// supplies the uid) + POST /iconset/:iconset/icon — that its own map rendering already resolves
// `{uid}/{group}/{filename}` against (api/web/src/base/cot.ts's icon-property handling). That API
// isn't exposed through PluginAPI though, so this calls it directly with a session token read
// out of storage — the same workaround as importIngest.ts, with the same caveat: see
// cloudtakInternals.ts.

import { getCloudTakToken } from './cloudtakInternals.ts';
import { extractAutoSymbology, isAutoSymbologyEmpty } from './autoSymbology.ts';
import { buildShapeStyleFields } from './displayConfig.ts';
import { canonicalizeLayerUrl } from './arcgisUrl.ts';
import { fetchLayerMeta } from './arcgisRest.ts';
import type { AutoSymbologyResult } from './autoSymbology.ts';
import type { DisplayConfig, SymConfig, SymValueEntry } from './types.ts';

export const SPEC_VERSION = 1;

const GROUP_BAD_RE = /[^A-Za-z0-9 _-]/g;
const FILE_BAD_RE = /[^A-Za-z0-9._-]/g;

/** A hostile or misauthored renderer must not be able to push unbounded data into the operator's CloudTAK account (§10.3). */
const MAX_ICONS = 256;
const MAX_ICON_BYTES = 512 * 1024;

/** CloudTAK's `Default.NameField` (api/lib/limits.ts) — applies to the iconset's display name. */
const ICONSET_NAME_MAX = 64;

// ── §2 — URL canonicalization ──────────────────────────────────────────────────

// Canonicalization moved to arcgisUrl.ts so the REST client and the iconset generator cannot drift
// apart again (they previously had two different URL contracts — C-08). Re-exported because
// AUTO-ICONSET-SPEC.md names this function.
export function canonicalizeUrl(sourceUrl: string): string {
    return canonicalizeLayerUrl(sourceUrl).url;
}

// ── §4 — UID ────────────────────────────────────────────────────────────────────

// §4: lowercase hex SHA-256 of `canonicalUrl + "/" + fieldName`, written verbatim into the
// registered iconset's uid. Web Crypto instead of Node's crypto module (this runs in-browser).
export async function uidFor(canonicalUrl: string, fieldName: string): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(`${canonicalUrl}/${fieldName || ''}`));
    return Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
}

// ── §5 — naming ─────────────────────────────────────────────────────────────────

// §5.1: sanitize + collapse whitespace + trim + cap 60. Caller appends " Icons".
export function sanitizeGroupBase(s: string): string {
    return (s || '').replace(GROUP_BAD_RE, '_').replace(/\s+/g, ' ').trim().slice(0, 60);
}

// §5.2: label/value → strip path prefix → strip trailing .png → sanitize → append .png.
export function fileNameFor(rawLabel: string | null): string {
    let base = rawLabel ?? '';
    const parts = base.split(/[\\/]/);
    base = parts[parts.length - 1] ?? '';
    base = base.replace(/\.png$/i, '');
    base = base.replace(FILE_BAD_RE, '_');
    if (!base) base = 'icon';
    return `${base}.png`;
}

// §5.3: append _2/_3/... before .png until unique within `seen`.
export function dedupe(fileName: string, seen: Set<string>): string {
    if (!seen.has(fileName)) { seen.add(fileName); return fileName; }
    const base = fileName.replace(/\.png$/i, '');
    let i = 2;
    while (seen.has(`${base}_${i}.png`)) i++;
    const out = `${base}_${i}.png`;
    seen.add(out);
    return out;
}

// ── §3 — renderer fetch + esriPMS extraction ────────────────────────────────────

interface EsriPmsSymbol { type?: string; imageData?: string }
interface EsriRendererJson {
    type?: string;
    field1?: string; field?: string;
    label?: string;
    symbol?: EsriPmsSymbol;
    uniqueValueInfos?: { value?: string; label?: string; symbol?: EsriPmsSymbol }[];
    classBreakInfos?: { label?: string; symbol?: EsriPmsSymbol }[];
    defaultSymbol?: EsriPmsSymbol;
    defaultLabel?: string;
}

interface PmsEntry { value: string | null; rawLabel: string | null; imageData: string; isDefault: boolean }

/**
 * Why a renderer produced the icon count it did. Without this the far more common
 * found-only-the-default case was completely silent: a layer declaring 10 symbols of which 3 were
 * extractable rendered as one repeated marker with nothing anywhere saying so, and diagnosing it
 * took a device, a pulled log and a source read. Port of AutoIconset.java's Extraction counters.
 */
export interface ExtractionDiagnostics {
    /** Renderer `type` as declared ('simple' when absent). '' when there is no renderer at all. */
    rendererType: string;
    /** Per-value / per-break symbols the renderer declared, excluding defaultSymbol. */
    declared: number;
    /** Symbols that yielded a usable icon (including the default symbol, if it did). */
    extracted: number;
    /** Whether `extracted` includes a usable defaultSymbol — which is why it can exceed `declared`. */
    defaultExtracted: boolean;
    /** Distinct symbol `type` values that were not usable, in first-seen order. */
    skipped: string[];
}

/**
 * @param skipped records why a symbol was not usable. A symbol is usable only if it is an
 *                `esriPMS` carrying embedded `imageData`. Symbols authored in the modern ArcGIS
 *                Map Viewer are typically `CIMSymbolReference` and are NOT handled here — that is
 *                the single most common reason a richly-styled layer yields only Other.png.
 */
function pmsFrom(symbol: EsriPmsSymbol | undefined, value: string | null, rawLabel: string | null, isDefault: boolean, skipped?: Set<string>): PmsEntry | null {
    if (!symbol) { skipped?.add('(absent)'); return null; }
    if (symbol.type !== 'esriPMS') { skipped?.add(symbol.type || '(untyped)'); return null; }
    if (!symbol.imageData) { skipped?.add('esriPMS(no imageData)'); return null; }
    return { value, rawLabel, imageData: symbol.imageData, isDefault };
}

/**
 * One line explaining the extraction. Debug on the clean path; a warning whenever declared symbols
 * were dropped, because that outcome is invisible to the operator otherwise — the layer simply
 * renders every feature with the same default marker.
 */
function logExtraction(canonicalUrl: string, d: ExtractionDiagnostics): void {
    // `declared` counts per-value/per-break symbols only, so a renderer whose defaultSymbol was
    // also usable legitimately reports extracted > declared — said out loud so the line does not
    // read as a miscount to whoever finds it in a console dump.
    const plusDefault = d.defaultExtracted ? ' (includes the default symbol)' : '';
    const head = `renderer '${d.rendererType}' for ${canonicalUrl}: declared=${d.declared} extracted=${d.extracted}${plusDefault}`;
    if (!d.skipped.length) { console.debug(`[featurelink] ${head}`); return; }
    let why = '';
    if (d.skipped.includes('esriSMS')) {
        why += ' esriSMS is a geometric marker (shape + colour, no embedded image), so there are no'
            + ' icon bytes to extract — those values are styled as shapes instead of an iconset.';
    }
    if (d.skipped.includes('CIMSymbolReference')) {
        why += ' CIMSymbolReference is the modern ArcGIS Map Viewer encoding and is not parsed by this extractor.';
    }
    console.warn(`[featurelink] ${head} — SKIPPED symbol types [${d.skipped.join(', ')}].`
        + ` Only esriPMS with embedded imageData yields an icon.${why}`
        + ' Values using a skipped type fall back to the default marker.');
}

// §3: driving field + ordered esriPMS entries. esriSMS (colored shape) symbols are skipped —
// those are the existing color/shape display-config path's job. Renderer array order is
// preserved (needed for deterministic §5.3 collision suffixes).
export function extractPmsEntries(renderer: EsriRendererJson | undefined, fieldOverride: string | undefined): { field: string; single: boolean; entries: PmsEntry[]; diagnostics: ExtractionDiagnostics } {
    const entries: PmsEntry[] = [];
    let field = fieldOverride || '';
    let single = false;
    const skipped = new Set<string>();
    let defaultExtracted = false;
    const diag = (rendererType: string, declared: number): ExtractionDiagnostics =>
        ({ rendererType, declared, extracted: entries.length, defaultExtracted, skipped: [...skipped] });

    if (!renderer) return { field, single, entries, diagnostics: diag('', 0) };

    const type = renderer.type || 'simple';
    const pushDefault = (): void => {
        // A renderer that declares no defaultSymbol at all is not a skip — only an unusable one is.
        if (!renderer.defaultSymbol) return;
        const d = pmsFrom(renderer.defaultSymbol, null, renderer.defaultLabel || null, !renderer.defaultLabel, skipped);
        if (d) { entries.push(d); defaultExtracted = true; }
    };

    let declared: number;
    if (type === 'uniqueValue' || type === 'uniqueValueRenderer') {
        field = renderer.field1 || renderer.field || '';
        const infos = renderer.uniqueValueInfos ?? [];
        declared = infos.length;
        for (const info of infos) {
            const e = pmsFrom(info.symbol, info.value ?? '', info.label ?? info.value ?? '', false, skipped);
            if (e) entries.push(e);
        }
        pushDefault();
    } else if (type === 'classBreaks' || type === 'classBreaksRenderer') {
        field = renderer.field || '';
        const infos = renderer.classBreakInfos ?? [];
        declared = infos.length;
        for (const info of infos) {
            const e = pmsFrom(info.symbol, null, info.label ?? '', false, skipped);
            if (e) entries.push(e);
        }
        pushDefault();
    } else {
        declared = 1;
        const label = renderer.label || '';
        const e = pmsFrom(renderer.symbol, null, label || null, !label, skipped);
        if (e) { entries.push(e); single = true; }
    }

    if (fieldOverride) field = fieldOverride;
    return { field, single, entries, diagnostics: diag(type, declared) };
}

// ── CloudTAK iconset registration (the fragile, unofficial part) ───────────────

interface IconToUpload { name: string; imageData: string }

async function apiPost(path: string, body: unknown, token: string): Promise<number> {
    const res = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(30_000),
    });
    // 409 is returned when the deterministic UID already exists — which is the NORMAL case on a
    // second add of the same layer. Treating it as an error made re-adding a layer silently lose
    // its icons (§10.3).
    if (!res.ok && res.status !== 409) {
        // CloudTAK answers a schema violation with 400 and a body naming the offending field. The
        // status alone said only "HTTP 400", which cost a long field investigation to learn that
        // the payload was missing a required property — include what the server actually said.
        let detail = '';
        try { detail = (await res.text()).slice(0, 300); } catch { /* body already consumed/absent */ }
        throw new Error(`${path} → HTTP ${res.status}${detail ? `: ${detail}` : ''}`);
    }
    return res.status;
}

/** Rejects anything that is not a real PNG before it is pushed into the operator's account (§10.3). */
function isPlausiblePng(base64: string): boolean {
    if (base64.length > MAX_ICON_BYTES * 2) return false;
    let head: string;
    try { head = atob(base64.slice(0, 16)); } catch { return false; }
    return head.charCodeAt(0) === 0x89 && head.slice(1, 4) === 'PNG';
}

// Creates the iconset (user-scoped — a plugin has no admin rights to make it server-wide) then
// uploads each icon. CloudTAK's own map already resolves the resulting {uid}/{group}/{filename}
// paths on demand (api/web/src/base/cot.ts + the atlas-sync IconManager), so nothing else on the
// rendering side needs to change — this is purely "does the icon exist on the server yet".
//
// Uploads are all-or-nothing from the caller's point of view: a partial set registered under a
// deterministic UID is worse than none, because every other platform resolves {uid}/{group}/{file}
// against it and gets a 404 (§10.3). Failures are collected and reported rather than aborting
// mid-set on the first one.
async function registerIconset(uid: string, group: string, icons: IconToUpload[], token: string): Promise<{ uploaded: number; failed: string[] }> {
    // The body must satisfy CloudTAK's TypeBox schema at api/routes/icons.ts's POST /iconset.
    // Every field below was rejected with a bare HTTP 400 in production until it matched:
    //
    //   internal  REQUIRED (Type.Boolean, not Type.Optional). Omitting it failed validation. The
    //             schema's `default: false` does not fill it in for us. False = show it in the UI.
    //   scope     Type.Enum(ResourceCreationScope), whose VALUES are 'server'|'user' — TypeBox
    //             validates the value, not the TS enum key, so the 'USER' we used to send was
    //             never valid. 'user' because a plugin has no admin rights to create server-wide.
    //   name      Default.NameField caps at 64 chars, but AUTO-ICONSET-SPEC §5.1's group is up to
    //             66 ("<60-char base> Icons"), so a long layer name overflowed it. Clamped HERE
    //             ONLY: `name` is the human-readable label, while `default_group` (unconstrained)
    //             and the per-icon `{group}/{file}` paths keep the full spec string, so
    //             cross-platform {uid}/{group}/{file} identity is preserved. Truncating the group
    //             itself would silently break icon resolution against ATAK/WinTAK/TAK Portal.
    await apiPost('/api/iconset', {
        uid,
        version: SPEC_VERSION,
        name: group.slice(0, ICONSET_NAME_MAX),
        internal: false,
        scope: 'user',
        default_group: group,
    }, token);

    const failed: string[] = [];
    let uploaded = 0;
    for (const icon of icons) {
        try {
            await apiPost(`/api/iconset/${encodeURIComponent(uid)}/icon`, {
                name: `${group}/${icon.name}`,
                data: `data:image/png;base64,${icon.imageData}`,
            }, token);
            uploaded++;
        } catch (e) {
            failed.push(`${icon.name}: ${e instanceof Error ? e.message : String(e)}`);
        }
    }
    return { uploaded, failed };
}

// ── Self-render — synthesize a DisplayConfig from the just-registered icons ────

// Port of DisplayConfig.forAutoIcons() — a "ic" (single icon) or "adv" (per-value) SymConfig
// pointing at the iconset paths just registered, so THIS device renders the layer's custom icons
// immediately rather than only on devices that later receive its CoT.
// Extends the icon-only synthesis above with esriSMS marker color/shape styling (only used when
// no custom icon was resolved — an icon always wins over a plain colored shape marker) and
// esriSLS/esriSFS stroke/fill styling for polyline/polygon layers, folded in via
// buildShapeStyleFields. Port of DisplayConfig.forAutoIcons()'s 5-arg overload.
function buildAutoDisplayConfig(
    url: string, field: string, singleIconPath: string | null, pathByValue: Map<string, string>,
    shapeResult: AutoSymbologyResult | null,
): DisplayConfig {
    let sym: SymConfig | undefined;
    if (singleIconPath) {
        sym = { t: 'ic', up: singleIconPath };
    } else if (pathByValue.size > 0) {
        const vs: SymValueEntry[] = Array.from(pathByValue.entries()).map(([v, up]) => ({ v, m: 'icon', up }));
        sym = { t: 'adv', f: field, vs };
    } else if (shapeResult && shapeResult.markerByValue.size > 0) {
        // No icons anywhere in the renderer; uniqueValue esriSMS renderer — per-value colored
        // shape markers via the plain "uv" resolveColor path.
        const uv: SymValueEntry[] = Array.from(shapeResult.markerByValue.entries()).map(([v, m]) => ({ v, c: m.color }));
        sym = { t: 'uv', f: shapeResult.field, c: shapeResult.singleMarker?.color, uv };
    } else if (shapeResult && shapeResult.singleMarker) {
        // No icons, single-symbol esriSMS marker — plain colored shape marker.
        sym = { t: 's', c: shapeResult.singleMarker.color, sh: shapeResult.singleMarker.shape };
    }

    return { v: 3, url, sym, ...buildShapeStyleFields(shapeResult) };
}

export interface AutoIconsetResult {
    uid: string;
    group: string;
    iconCount: number;
    field: string;
    displayConfig: DisplayConfig;
    /** Non-fatal problems (icons that failed to upload, symbols that failed to decode). */
    warnings: string[];
    /** Declared vs extracted vs skipped, for diagnosing "why did my styled layer render flat". */
    diagnostics: ExtractionDiagnostics;
}

export interface AutoIconsetOptions {
    /** Driving field override (AutoIconset.java's `fieldOverride`). */
    fieldOverride?: string;
    /** ArcGIS access token, used only for the metadata fetch and only on trusted hosts. */
    arcgisToken?: string | null;
    /**
     * Renderer supplied by the caller instead of being fetched. FIX-4 / parity with
     * `AutoIconset.generate(..., rendererOverride, uidOverride, groupOverride)`: a TAK Portal
     * Web Map export embeds the resolved renderer in the config, and without honoring it CloudTAK
     * self-derives a DIFFERENT uid, so the icons it registers do not match the paths in the shared
     * config and nothing renders (§10.3).
     */
    rendererOverride?: EsriRendererJson | null;
    uidOverride?: string | null;
    groupOverride?: string | null;
    /** Pre-fetched layer name, so the download path does not re-fetch metadata it already has. */
    layerName?: string;
}

// Generate + register an iconset from a FeatureServer layer link, per AUTO-ICONSET-SPEC.md, and
// also fold in esriSMS/esriSLS/esriSFS auto-symbology (marker color/shape, line stroke, polygon
// fill) extracted from the same renderer fetch — mirrors FeatureLinkDropDownReceiver's
// single-fetch-feeds-both-extractors pattern (avoids a duplicate renderer round trip).
// Returns null (not an error) when the renderer has neither esriPMS symbols nor any
// esriSMS/esriSLS/esriSFS styling to extract — that's an unstyled/default-symbology layer, not a
// failure. Throws on an actual problem (bad URL, network/API failure, no CloudTAK session — the
// latter only when there ARE icons to register; a shape-only result needs no CloudTAK session).
export async function generateAutoIconset(
    sourceUrl: string, options: AutoIconsetOptions = {},
): Promise<AutoIconsetResult | null> {
    const { fieldOverride, arcgisToken = null, rendererOverride = null, uidOverride = null, groupOverride = null } = options;
    const canonicalUrl = canonicalizeUrl(sourceUrl);
    const warnings: string[] = [];

    // One metadata fetch for the whole plugin: fetchLayerMeta is cached, so the download path that
    // already resolved geometryType/objectIdField does NOT pay for a second `?f=json` here. Before
    // this, fetchLayerInfo, fetchGeometryType and this function fetched the same document three
    // times and read its renderer zero times (RC-3).
    let renderer = rendererOverride;
    let layerName = options.layerName ?? '';
    if (!renderer || !layerName) {
        const meta = await fetchLayerMeta(canonicalUrl, arcgisToken);
        if (!renderer) renderer = meta.renderer as EsriRendererJson | null;
        if (!layerName) layerName = meta.name;
    }
    if (!layerName) layerName = 'Layer';

    const { field: iconField, single, entries, diagnostics } = extractPmsEntries(renderer ?? undefined, fieldOverride);
    // Logged for EVERY extraction, not just the zero-icon case: "declared 10, extracted 3" is the
    // outcome that actually reaches the field, and it used to be entirely silent.
    logExtraction(canonicalUrl, diagnostics);
    const shapeResult = extractAutoSymbology(renderer ?? undefined, fieldOverride);
    const hasShapeStyle = !isAutoSymbologyEmpty(shapeResult);
    if (!entries.length && !hasShapeStyle) return null;

    let uid = '';
    let group = '';
    let iconCount = 0;
    const pathByValue = new Map<string, string>();
    let singleIconPath: string | null = null;

    if (entries.length) {
        uid = uidOverride ?? await uidFor(canonicalUrl, iconField);
        // §5.1: the 60-char cap is applied ONCE, to the base, BEFORE the " Icons" suffix, and never
        // again — re-applying it after the suffix is the confirmed TAK Portal double-truncation bug
        // that breaks {uid}/{group}/{file} string identity for names over 54 chars (C-39).
        group = groupOverride ?? `${sanitizeGroupBase(layerName)} Icons`;

        const seen = new Set<string>();
        const icons: IconToUpload[] = [];
        for (const e of entries) {
            if (icons.length >= MAX_ICONS) {
                warnings.push(`renderer declares more than ${MAX_ICONS} icons — the rest were ignored`);
                break;
            }
            if (!isPlausiblePng(e.imageData)) {
                warnings.push(`symbol "${e.rawLabel ?? e.value ?? 'default'}" is not a usable PNG and was skipped`);
                continue;
            }
            const fname = dedupe(e.isDefault && !e.rawLabel ? 'Other.png' : fileNameFor(e.rawLabel), seen);
            icons.push({ name: fname, imageData: e.imageData });
            const path = `${uid}/${group}/${fname}`;
            if (e.value) pathByValue.set(e.value, path);
            else if (single) singleIconPath = path;
        }

        if (icons.length) {
            const cloudtakToken = getCloudTakToken();
            if (!cloudtakToken) {
                // ATAK logs and degrades here rather than failing the whole add (AutoIconset.java:374).
                warnings.push('not signed into CloudTAK — icons could not be registered on this server');
            } else {
                // NON-FATAL, and this matters more than any single payload bug. Registration is an
                // optional server-side step: it decides whether custom ICONS resolve, not whether
                // the layer is styled at all. Letting it throw propagated out of generateAutoIconset
                // into ensureLayerSymbology's catch, which stored NO display config — so one 400
                // from /api/iconset cost the operator every colour, shape, label and popup on the
                // layer too, and presented as "no symbology at all" rather than "no icons". ATAK
                // degrades here (AutoIconset.java:374); now so do we.
                try {
                    const { uploaded, failed } = await registerIconset(uid, group, icons, cloudtakToken);
                    iconCount = uploaded;
                    if (failed.length) warnings.push(`${failed.length} of ${icons.length} icons failed to upload: ${failed[0]}`);
                } catch (e) {
                    warnings.push(`icons could not be registered on this CloudTAK server: ${e instanceof Error ? e.message : String(e)}`);
                    console.warn('[featurelink] iconset registration failed — falling back to shape/colour styling', e);
                }
            }
            // With no icons on the server, the {uid}/{group}/{file} paths would resolve to 404s and
            // every marker would render blank. Drop them so buildAutoDisplayConfig falls through to
            // the esriSMS colour/shape branch, which needs no server at all.
            if (iconCount === 0) {
                pathByValue.clear();
                singleIconPath = null;
            }
        } else {
            warnings.push('all picture-marker symbols failed to decode');
        }
    }

    const field = entries.length ? iconField : shapeResult.field;
    return {
        uid, group, iconCount, field, warnings, diagnostics,
        displayConfig: buildAutoDisplayConfig(canonicalUrl, field, singleIconPath, pathByValue, shapeResult),
    };
}
