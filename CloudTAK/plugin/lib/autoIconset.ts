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
import type { DisplayConfig, SymConfig, SymValueEntry } from './types.ts';

export const SPEC_VERSION = 1;

const GROUP_BAD_RE = /[^A-Za-z0-9 _-]/g;
const FILE_BAD_RE = /[^A-Za-z0-9._-]/g;

// ── §2 — URL canonicalization ──────────────────────────────────────────────────

function isWebMapLink(url: string): boolean {
    return /\/home\/item\.html/i.test(url) || /\/sharing\/rest\/content\/items\//i.test(url);
}

// §2.1–§2.2: normalize to {scheme}://{host}{/path}/FeatureServer|MapServer/{layerId}, with
// scheme+host lowercased, path case preserved, query/fragment/trailing-slash stripped, and a
// bare service root defaulting to layer 0. Throws on a Web Map link (§2.3 not implemented here
// either — reject rather than hash a raw web-map URL, which would produce a non-matching UID).
export function canonicalizeUrl(sourceUrl: string): string {
    const raw = (sourceUrl || '').trim();
    if (!raw) throw new Error('A FeatureServer layer URL is required.');
    if (isWebMapLink(raw)) {
        throw new Error(
            "Web Map links aren't resolved yet — paste the FeatureServer layer URL "
            + '(…/FeatureServer/0). See AUTO-ICONSET-SPEC.md §2.3.',
        );
    }

    let u: URL;
    try { u = new URL(raw); }
    catch { throw new Error(`Not a valid URL: ${raw}`); }

    const scheme = u.protocol.toLowerCase().replace(/:$/, '');
    const host = u.host.toLowerCase();
    const path = u.pathname.replace(/\/{2,}/g, '/').replace(/\/+$/, '');

    const m = path.match(/^(.*\/(?:FeatureServer|MapServer))(?:\/(\d+))?$/i);
    if (!m) {
        throw new Error(
            `URL does not point at a FeatureServer/MapServer layer: ${raw} `
            + '(expected …/FeatureServer or …/FeatureServer/{layerId}).',
        );
    }
    const base = m[1];
    const layerId = m[2] ?? '0'; // §2.2 — service root defaults to layer 0
    return `${scheme}://${host}${base}/${layerId}`;
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
    base = parts[parts.length - 1];
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

async function apiPost(path: string, body: unknown, token: string): Promise<void> {
    const res = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`${path} → HTTP ${res.status}`);
}

// Creates the iconset (user-scoped — a plugin has no admin rights to make it server-wide) then
// uploads each icon. CloudTAK's own map already resolves the resulting {uid}/{group}/{filename}
// paths on demand (api/web/src/base/cot.ts + the atlas-sync IconManager), so nothing else on the
// rendering side needs to change — this is purely "does the icon exist on the server yet".
async function registerIconset(uid: string, group: string, icons: IconToUpload[], token: string): Promise<void> {
    await apiPost('/api/iconset', {
        uid, version: SPEC_VERSION, name: group, scope: 'USER', default_group: group,
    }, token);

    for (const icon of icons) {
        await apiPost(`/api/iconset/${encodeURIComponent(uid)}/icon`, {
            name: `${group}/${icon.name}`,
            data: `data:image/png;base64,${icon.imageData}`,
        }, token);
    }
}

// ── Self-render — synthesize a DisplayConfig from the just-registered icons ────

// Port of DisplayConfig.forAutoIcons() — a "ic" (single icon) or "adv" (per-value) SymConfig
// pointing at the iconset paths just registered, so THIS device renders the layer's custom icons
// immediately rather than only on devices that later receive its CoT.
function buildAutoIconDisplayConfig(url: string, field: string, singleIconPath: string | null, pathByValue: Map<string, string>): DisplayConfig {
    let sym: SymConfig;
    if (singleIconPath) {
        sym = { t: 'ic', up: singleIconPath };
    } else {
        const vs: SymValueEntry[] = Array.from(pathByValue.entries()).map(([v, up]) => ({ v, m: 'icon', up }));
        sym = { t: 'adv', f: field, vs };
    }
    return { v: 3, url, sym };
}

export interface AutoIconsetResult {
    uid: string;
    group: string;
    iconCount: number;
    field: string;
    displayConfig: DisplayConfig;
}

// Generate + register an iconset from a FeatureServer layer link, per AUTO-ICONSET-SPEC.md.
// Returns null (not an error) when the renderer simply has no esriPMS symbols to extract —
// that's the common case (colored-shape layers), not a failure. Throws on an actual problem
// (bad URL, network/API failure, no CloudTAK session).
export async function generateAutoIconset(sourceUrl: string, fieldOverride?: string, arcgisToken?: string | null): Promise<AutoIconsetResult | null> {
    const canonicalUrl = canonicalizeUrl(sourceUrl);

    const tokenQs = arcgisToken ? `&token=${encodeURIComponent(arcgisToken)}` : '';
    const res = await fetch(`${canonicalUrl}?f=json${tokenQs}`, { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(`ArcGIS request failed (${res.status}) for ${canonicalUrl}`);
    const meta = await res.json() as { name?: string; serviceDescription?: string; error?: unknown; drawingInfo?: { renderer?: EsriRendererJson } };
    if (meta.error) throw new Error(`ArcGIS error fetching ${canonicalUrl}`);

    const layerName = meta.name || meta.serviceDescription || 'Layer';
    const { field, single, entries } = extractPmsEntries(meta.drawingInfo?.renderer, fieldOverride);
    if (!entries.length) return null; // no picture-marker symbols — nothing to generate

    const uid = await uidFor(canonicalUrl, field);
    const group = `${sanitizeGroupBase(layerName)} Icons`;

    const seen = new Set<string>();
    const icons: IconToUpload[] = [];
    const pathByValue = new Map<string, string>();
    let singleIconPath: string | null = null;

    for (const e of entries) {
        const fname = dedupe(e.isDefault && !e.rawLabel ? 'Other.png' : fileNameFor(e.rawLabel), seen);
        icons.push({ name: fname, imageData: e.imageData });
        const path = `${uid}/${group}/${fname}`;
        if (e.value) pathByValue.set(e.value, path);
        else if (single) singleIconPath = path;
    }
    if (!icons.length) throw new Error('All picture-marker symbols failed to decode.');

    const cloudtakToken = getCloudTakToken();
    if (!cloudtakToken) throw new Error('Not signed into CloudTAK — can\'t register icons on this server.');
    await registerIconset(uid, group, icons, cloudtakToken);

    return {
        uid, group, iconCount: icons.length, field,
        displayConfig: buildAutoIconDisplayConfig(canonicalUrl, field, singleIconPath, pathByValue),
    };
}
