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

function pmsFrom(symbol: EsriPmsSymbol | undefined, value: string | null, rawLabel: string | null, isDefault: boolean): PmsEntry | null {
    if (!symbol || symbol.type !== 'esriPMS' || !symbol.imageData) return null;
    return { value, rawLabel, imageData: symbol.imageData, isDefault };
}

// §3: driving field + ordered esriPMS entries. esriSMS (colored shape) symbols are skipped —
// those are the existing color/shape display-config path's job. Renderer array order is
// preserved (needed for deterministic §5.3 collision suffixes).
function extractPmsEntries(renderer: EsriRendererJson | undefined, fieldOverride: string | undefined): { field: string; single: boolean; entries: PmsEntry[] } {
    const entries: PmsEntry[] = [];
    let field = fieldOverride || '';
    let single = false;
    if (!renderer) return { field, single, entries };

    const type = renderer.type || 'simple';
    const pushDefault = (): void => {
        const d = pmsFrom(renderer.defaultSymbol, null, renderer.defaultLabel || null, !renderer.defaultLabel);
        if (d) entries.push(d);
    };

    if (type === 'uniqueValue' || type === 'uniqueValueRenderer') {
        field = renderer.field1 || renderer.field || '';
        for (const info of renderer.uniqueValueInfos ?? []) {
            const e = pmsFrom(info.symbol, info.value ?? '', info.label ?? info.value ?? '', false);
            if (e) entries.push(e);
        }
        pushDefault();
    } else if (type === 'classBreaks' || type === 'classBreaksRenderer') {
        field = renderer.field || '';
        for (const info of renderer.classBreakInfos ?? []) {
            const e = pmsFrom(info.symbol, null, info.label ?? '', false);
            if (e) entries.push(e);
        }
        pushDefault();
    } else {
        const label = renderer.label || '';
        const e = pmsFrom(renderer.symbol, null, label || null, !label);
        if (e) { entries.push(e); single = true; }
    }

    if (fieldOverride) field = fieldOverride;
    return { field, single, entries };
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
    if (!res.ok && res.status !== 409) throw new Error(`${path} → HTTP ${res.status}`);
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
    await apiPost('/api/iconset', {
        uid, version: SPEC_VERSION, name: group, scope: 'USER', default_group: group,
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

    const { field: iconField, single, entries } = extractPmsEntries(renderer ?? undefined, fieldOverride);
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
                const { uploaded, failed } = await registerIconset(uid, group, icons, cloudtakToken);
                iconCount = uploaded;
                if (failed.length) warnings.push(`${failed.length} of ${icons.length} icons failed to upload: ${failed[0]}`);
            }
        } else {
            warnings.push('all picture-marker symbols failed to decode');
        }
    }

    const field = entries.length ? iconField : shapeResult.field;
    return {
        uid, group, iconCount, field, warnings,
        displayConfig: buildAutoDisplayConfig(canonicalUrl, field, singleIconPath, pathByValue, shapeResult),
    };
}
