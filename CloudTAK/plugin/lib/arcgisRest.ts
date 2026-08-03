// ArcGIS REST client — fetch-based port of the ATAK plugin's arcgis/ArcGISRestClient.java.
//
// Every request in this file goes through arcgisHttp.arcgisJson(), which is the single place that
// checks transport status AND the ArcGIS `{"error":{…}}` envelope that ships with HTTP 200 (C-22),
// carries the token in a header instead of the query string (C-21), refuses to send that token to a
// host outside the signed-in portal's deployment (C-33), and applies a timeout (C-26).

import { newLayer } from './types.ts';
import type {
    ArcGISLayer, LayerAccess, CotFieldMapping, DownloadedFeature, LayerDownloadResult, PliFeatureInput,
} from './types.ts';
import { arcgisJson, ArcGISError } from './arcgisHttp.ts';
import { canonicalizeLayerUrl, layerQueryUrl, redactUrl } from './arcgisUrl.ts';
import { getPortalUrl } from './arcgisAuth.ts';

function normalizePortalUrl(portalUrl: string): string {
    return (portalUrl?.trim() || 'https://www.arcgis.com').replace(/\/+$/, '');
}

// ── Layer metadata, fetched once ──────────────────────────────────────────────
//
// RC-3: `fetchLayerInfo`, `fetchGeometryType` and `generateAutoIconset` each issued their own
// `?f=json` against the same layer endpoint — the same document fetched three times, with the
// `drawingInfo.renderer` it contains read zero times. One cached fetch now serves all three, and
// the renderer is retained so the download path can resolve symbology from it (C-07 / FIX-1).

export interface EsriRendererJson {
    type?: string;
    field?: string; field1?: string;
    label?: string; defaultLabel?: string;
    symbol?: unknown; defaultSymbol?: unknown;
    uniqueValueInfos?: unknown[];
    classBreakInfos?: unknown[];
}

export interface LayerMeta {
    name: string;
    geometryType: string;
    objectIdField: string;
    maxRecordCount: number;
    /** Complete `drawingInfo` block, renderer included. Null when the service declares none. */
    renderer: EsriRendererJson | null;
    /** Raw `drawingInfo` for the hot-load design's rendererHash (Appendix B N.2 "Invalidation"). */
    drawingInfo: unknown;
    capabilities: string;
}

interface RawLayerMeta {
    name?: string;
    serviceDescription?: string;
    geometryType?: string;
    objectIdField?: string;
    maxRecordCount?: number;
    capabilities?: string;
    drawingInfo?: { renderer?: EsriRendererJson };
}

interface CacheEntry { at: number; meta: LayerMeta }

const META_TTL_MS = 5 * 60_000;
const metaCache = new Map<string, CacheEntry>();

/** Drops cached metadata — call after anything that could change a service definition. */
export function invalidateLayerMeta(serviceUrl?: string): void {
    if (serviceUrl === undefined) { metaCache.clear(); return; }
    metaCache.delete(layerQueryUrl(serviceUrl));
}

export async function fetchLayerMeta(serviceUrl: string, token: string | null): Promise<LayerMeta> {
    const url = layerQueryUrl(serviceUrl);
    const cached = metaCache.get(url);
    if (cached && Date.now() - cached.at < META_TTL_MS) return cached.meta;

    const raw = await arcgisJson<RawLayerMeta>(url, {
        params: { f: 'json' }, token, portalUrl: getPortalUrl(),
    });

    const meta: LayerMeta = {
        name: raw.name || raw.serviceDescription || 'Unknown Layer',
        geometryType: raw.geometryType ?? '',
        objectIdField: raw.objectIdField ?? 'OBJECTID',
        // ArcGIS caps every query at the service's maxRecordCount; 1000 is the AGOL default and
        // the right conservative page size when the service does not declare one.
        maxRecordCount: typeof raw.maxRecordCount === 'number' && raw.maxRecordCount > 0 ? raw.maxRecordCount : 1000,
        renderer: raw.drawingInfo?.renderer ?? null,
        drawingInfo: raw.drawingInfo ?? null,
        capabilities: raw.capabilities ?? '',
    };
    metaCache.set(url, { at: Date.now(), meta });
    return meta;
}

// ── Sublayer enumeration (C-08) ───────────────────────────────────────────────
//
// `ensureLayerIndex` used to hard-append `/0`, and a portal search returns the *service root*, so a
// FeatureServer exposing layers 0/1/2 collapsed to a single row pinned at layer 0. If the operator's
// symbology lived on layer 1 it was never seen — and neither were its features.

export interface SublayerRef { id: number; name: string; geometryType: string; url: string }

interface ServiceRootJson {
    layers?: { id?: number; name?: string; geometryType?: string; subLayerIds?: number[] | null }[];
    tables?: { id?: number; name?: string }[];
}

/**
 * Lists a service's queryable sublayers. Returns a single entry for a URL that already names an
 * explicit layer index. Group layers (`subLayerIds` non-null) are skipped — they hold no features.
 */
export async function fetchServiceLayers(serviceUrl: string, token: string | null): Promise<SublayerRef[]> {
    let canonical;
    try { canonical = canonicalizeLayerUrl(serviceUrl); }
    catch { return []; }

    if (!canonical.layerIdAssumed) {
        const meta = await fetchLayerMeta(canonical.url, token);
        return [{ id: canonical.layerId, name: meta.name, geometryType: meta.geometryType, url: canonical.url }];
    }

    const root = await arcgisJson<ServiceRootJson>(canonical.serviceRoot, {
        params: { f: 'json' }, token, portalUrl: getPortalUrl(),
    });

    const out: SublayerRef[] = [];
    for (const l of root.layers ?? []) {
        if (typeof l.id !== 'number') continue;
        if (l.subLayerIds !== undefined && l.subLayerIds !== null) continue; // group layer
        out.push({
            id: l.id,
            name: l.name ?? `Layer ${l.id}`,
            geometryType: l.geometryType ?? '',
            url: `${canonical.serviceRoot}/${l.id}`,
        });
    }
    // A service that reports no `layers[]` at all (older MapServers, or a root that is really a
    // single layer) still has to be addressable — fall back to the assumed layer 0.
    if (out.length === 0) {
        out.push({ id: canonical.layerId, name: '', geometryType: '', url: canonical.url });
    }
    return out;
}

// ── Layer search ──────────────────────────────────────────────────────────────

/** Escapes a value for the portal search DSL so a username cannot alter the query (§9.1). */
function quoteSearchTerm(v: string): string {
    return `"${v.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

interface SearchResponse {
    results?: { title?: string; url?: string; access?: string }[];
    nextStart?: number;
    total?: number;
}

const SEARCH_PAGE = 100;
const SEARCH_MAX_PAGES = 20; // 2,000 owned services is far beyond any realistic account

/**
 * All Feature Services owned by `username`, paged to exhaustion (`nextStart`), with each service's
 * sublayers enumerated so a multi-layer FeatureServer yields one selectable row per layer (C-08).
 *
 * Errors propagate: a portal outage used to be swallowed into `[]`, which the UI rendered as the
 * actively misleading "No layers found in your ArcGIS account" (Appendix B §3.2).
 */
export async function searchUserLayers(portalUrl: string, token: string, username: string): Promise<ArcGISLayer[]> {
    const portal = normalizePortalUrl(portalUrl);
    const q = `type:"Feature Service" AND owner:${quoteSearchTerm(username)}`;

    const items: { title: string; url: string; access: LayerAccess }[] = [];
    let start = 1;
    for (let page = 0; page < SEARCH_MAX_PAGES; page++) {
        const json = await arcgisJson<SearchResponse>(`${portal}/sharing/rest/search`, {
            params: { q, num: String(SEARCH_PAGE), start: String(start), f: 'json' },
            token, portalUrl: portal,
        });
        for (const item of json.results ?? []) {
            if (!item.url) continue;
            // Portal item "access": "public" (shared to Everyone), "org", or "private" — decides
            // which on-device section this layer lands in once downloaded.
            const access = item.access === 'public' || item.access === 'org' || item.access === 'private'
                ? item.access : 'private';
            items.push({ title: item.title ?? 'Unnamed', url: item.url, access });
        }
        const next = json.nextStart ?? -1;
        if (next <= 0) break;
        start = next;
    }

    // Enumerate sublayers with bounded concurrency — an account with 100 services must not fire
    // 100 simultaneous requests (ArcGIS Online rate-limits at 429, §10.5).
    const out: ArcGISLayer[] = [];
    const CONCURRENCY = 5;
    for (let i = 0; i < items.length; i += CONCURRENCY) {
        const batch = items.slice(i, i + CONCURRENCY);
        const resolved = await Promise.all(batch.map(async (item) => {
            try { return { item, subs: await fetchServiceLayers(item.url, token) }; }
            catch (e) {
                // One unreachable service must not lose the whole list; represent it as its root.
                console.warn('[featurelink] sublayer enumeration failed for', redactUrl(item.url), e);
                return { item, subs: [] as SublayerRef[] };
            }
        }));
        for (const { item, subs } of resolved) {
            if (subs.length === 0) {
                out.push(newLayer(item.title, layerQueryUrl(item.url), 'private', item.access, true));
                continue;
            }
            for (const sub of subs) {
                // A multi-layer service needs the sublayer name in the row or the three rows are
                // indistinguishable; a single-layer service keeps the portal item's title.
                const name = subs.length > 1 && sub.name ? `${item.title} — ${sub.name}` : item.title;
                const layer = newLayer(name, sub.url, 'private', item.access, true);
                layer.layerId = sub.id;
                layer.geometryType = sub.geometryType;
                out.push(layer);
            }
        }
    }
    return out;
}

// ── Feature count ─────────────────────────────────────────────────────────────

export async function queryFeatureCount(serviceUrl: string, token: string | null): Promise<number> {
    const json = await arcgisJson<{ count?: number }>(`${layerQueryUrl(serviceUrl)}/query`, {
        params: { where: '1=1', returnCountOnly: 'true', f: 'json' },
        token, portalUrl: getPortalUrl(),
    });
    return json.count ?? 0;
}

// ── Layer info ─────────────────────────────────────────────────────────────────

/**
 * Layer model built from the layer's own metadata. `null` is returned only when the layer genuinely
 * cannot be described; the reason is available on the thrown `ArcGISError` via fetchLayerInfoStrict.
 */
export async function fetchLayerInfo(serviceUrl: string, token: string | null = null): Promise<ArcGISLayer | null> {
    try {
        return await fetchLayerInfoStrict(serviceUrl, token);
    } catch (e) {
        console.error('[featurelink] fetchLayerInfo failed for', redactUrl(serviceUrl), e);
        return null;
    }
}

export async function fetchLayerInfoStrict(serviceUrl: string, token: string | null = null): Promise<ArcGISLayer> {
    const meta = await fetchLayerMeta(serviceUrl, token);
    const canonical = layerQueryUrl(serviceUrl);
    const layer = newLayer(meta.name, canonical, 'public');
    layer.geometryType = meta.geometryType;
    try { layer.layerId = canonicalizeLayerUrl(canonical).layerId; } catch { /* keep default */ }
    return layer;
}

/** Backfill for a layer whose geometryType was never resolved. Fail-soft: '' on any error. */
export async function fetchGeometryType(serviceUrl: string, token: string | null): Promise<string> {
    try {
        return (await fetchLayerMeta(serviceUrl, token)).geometryType;
    } catch (e) {
        console.warn('[featurelink] fetchGeometryType failed for', redactUrl(serviceUrl), e);
        return '';
    }
}

// ── Feature download → CoT-shaped features for map injection ──────────────────

function resolveField(attrs: Record<string, unknown>, candidates: string[], fallbackField: string): string {
    for (const field of candidates) {
        const v = attrs[field];
        if (v !== undefined && v !== null && String(v) !== '') return String(v);
    }
    const fb = attrs[fallbackField];
    return fb !== undefined && fb !== null ? String(fb) : '';
}

function finite(v: unknown): v is number {
    return typeof v === 'number' && Number.isFinite(v);
}

function extractParts(parts: unknown): number[][][] {
    if (!Array.isArray(parts)) return [];
    const result: number[][][] = [];
    for (const part of parts) {
        if (!Array.isArray(part)) continue;
        const vertices: number[][] = [];
        for (const pt of part) {
            if (!Array.isArray(pt) || pt.length < 2) continue;
            const x = pt[0], y = pt[1];
            if (!finite(x) || !finite(y)) continue;
            vertices.push([x, y]);
        }
        if (vertices.length) result.push(vertices);
    }
    return result;
}

function extractFirstVertex(geom: Record<string, unknown>): [number, number] | null {
    for (const key of ['paths', 'rings'] as const) {
        const parts = extractParts(geom[key]);
        const first = parts[0]?.[0];
        // `paths[0][0][0]` used to be read without any guard: an existing-but-empty part yielded
        // `undefined` coordinates, and `Number.isNaN(undefined)` is **false**, so the old validity
        // check passed them straight through to the CoT database (§9.4).
        if (first && finite(first[0]) && finite(first[1])) return [first[0], first[1]];
    }
    return null;
}

/** WGS84 is the only spatial reference the CoT path can consume (C-34). */
function assertWgs84(sr: unknown, url: string): void {
    if (sr === undefined || sr === null) return; // no SR declared on an empty result set
    const wkid = (sr as { wkid?: number; latestWkid?: number }).latestWkid
        ?? (sr as { wkid?: number }).wkid;
    if (wkid === 4326) return;
    throw new ArcGISError(
        'service',
        `service returned spatial reference ${String(wkid)} instead of the requested 4326 — `
        + 'coordinates would be plotted at nonsense locations, so the download was rejected',
        { url },
    );
}

interface QueryResponse {
    features?: { attributes?: Record<string, unknown>; geometry?: Record<string, unknown> }[];
    exceededTransferLimit?: boolean;
    spatialReference?: unknown;
    properties?: { exceededTransferLimit?: boolean };
}

/** Hard ceiling so a mis-configured service cannot pull an unbounded result set into the tab. */
export const MAX_DOWNLOAD_FEATURES = 50_000;

/**
 * Downloads every feature of a layer, paging on `resultOffset`/`resultRecordCount` until the
 * service stops reporting `exceededTransferLimit` (C-06).
 *
 * Before this, one query with no paging parameters was issued and whatever ArcGIS capped it at
 * (commonly 1,000–2,000) was reported as the complete layer — the UI then *confirmed* the wrong
 * feature count. The result now carries `truncated`, which the UI renders as an explicit warning.
 */
export async function downloadLayerAsCoT(
    serviceUrl: string, token: string | null, mapping: CotFieldMapping | null = null,
): Promise<LayerDownloadResult> {
    const layerUrl = layerQueryUrl(serviceUrl);
    const portalUrl = getPortalUrl();

    let pageSize = 1000;
    let objectIdField = 'OBJECTID';
    try {
        const meta = await fetchLayerMeta(serviceUrl, token);
        pageSize = Math.min(meta.maxRecordCount, 2000);
        objectIdField = meta.objectIdField;
    } catch (e) {
        // Metadata is an optimization here; a service that hides it can still be queried.
        console.warn('[featurelink] layer metadata unavailable, using default page size', e);
    }

    const results: DownloadedFeature[] = [];
    let skipped = 0;
    let truncated = false;
    let offset = 0;

    for (;;) {
        const json = await arcgisJson<QueryResponse>(`${layerUrl}/query`, {
            params: {
                where: '1=1', outFields: '*', outSR: '4326', f: 'json',
                resultOffset: String(offset), resultRecordCount: String(pageSize),
                // Without a deterministic order ArcGIS does not guarantee page stability, so a
                // paged download could duplicate and drop rows between pages (§9.5).
                orderByFields: objectIdField,
                returnGeometry: 'true',
            },
            token, portalUrl,
        });

        assertWgs84(json.spatialReference, layerUrl);

        const page = json.features ?? [];
        for (const feat of page) {
            const parsed = toDownloadedFeature(feat, mapping, objectIdField, results.length);
            if (parsed) results.push(parsed); else skipped++;
        }

        const exceeded = json.exceededTransferLimit === true || json.properties?.exceededTransferLimit === true;
        if (!exceeded || page.length === 0) break;
        offset += page.length;
        if (results.length + skipped >= MAX_DOWNLOAD_FEATURES) { truncated = true; break; }
    }

    if (skipped > 0) {
        console.warn('[featurelink] skipped', skipped, 'features with unusable geometry from', redactUrl(layerUrl));
    }
    return { features: results, truncated, skipped };
}

function toDownloadedFeature(
    feat: { attributes?: Record<string, unknown>; geometry?: Record<string, unknown> },
    mapping: CotFieldMapping | null,
    objectIdField: string,
    index: number,
): DownloadedFeature | null {
    const geom = feat.geometry;
    if (!geom) return null;

    const paths = extractParts(geom.paths);
    const rings = extractParts(geom.rings);
    let lat = NaN, lon = NaN;
    if (finite(geom.x) && finite(geom.y)) {
        lon = geom.x; lat = geom.y;
    } else {
        const v = extractFirstVertex(geom);
        if (v) { lon = v[0]; lat = v[1]; }
    }
    // Range check as well as NaN: latitudes outside ±90 and longitudes outside ±180 used to pass
    // straight through to the map (§9.4).
    if (!finite(lat) || !finite(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;

    const attrs = feat.attributes ?? {};
    const attrMap: Record<string, string> = {};
    for (const [k, v] of Object.entries(attrs)) {
        if (v !== undefined && v !== null) attrMap[k] = String(v);
    }

    let uid = resolveField(attrs, mapping?.uidFields ?? [], 'uid');
    let cotType = resolveField(attrs, mapping?.typeFields ?? [], 'cot_type');
    let callsign = resolveField(attrs, mapping?.callsignFields ?? [], 'tak_callsign');
    const remarks = resolveField(attrs, mapping?.remarksFields ?? [], 'tak_remarks');
    const hae = finite(attrs.hae) ? attrs.hae : NaN;

    // A synthesized UID must be STABLE across downloads: the old `FL-${i}-${Date.now()}` changed
    // every refresh, so every marker was torn down and re-added on every scheduler tick (§9.5).
    // The ObjectID is the service's own stable row identity.
    const oid = attrs[objectIdField];
    if (!uid) uid = oid !== undefined && oid !== null ? `FL-oid-${String(oid)}` : `FL-idx-${index}`;
    // 'a-f-G' is FRIENDLY ground. An unclassified feature must not be presented as friendly on a
    // tactical display (§9.5) — unknown is the honest default.
    if (!cotType) cotType = 'a-u-G';
    if (!callsign) callsign = oid !== undefined && oid !== null ? `Feature-${String(oid)}` : `Feature-${index}`;

    return { uid, cotType, callsign, remarks, lat, lon, hae, attributes: attrMap, paths, rings };
}

// ── Add / update PLI-style features (applyEdits) ───────────────────────────────

function safeNumber(v: number): number | null {
    return Number.isFinite(v) ? v : null;
}

function buildPliAttributes(input: PliFeatureInput): Record<string, unknown> {
    const now = Date.now();
    return {
        uid: input.uid ?? '',
        source_system: 'CloudTAK',
        source_layer: input.sourceLayer ?? '',
        source_objectid: input.sourceObjectId ?? '',
        cot_type: input.cotType ?? '',
        tak_callsign: input.callsign ?? '',
        tak_icon: input.iconPath ?? '',
        tak_remarks: input.remarks ?? '',
        group_name: input.groupName ?? '',
        group_role: input.groupRole ?? '',
        latitude: safeNumber(input.lat),
        longitude: safeNumber(input.lon),
        hae: safeNumber(input.hae),
        ce: safeNumber(input.ce),
        le: safeNumber(input.le),
        time: input.timeMs,
        start_time: input.startMs,
        stale_time: input.staleMs,
        sent_toFL_time: now,
        sent_by_user: input.sentByUser ?? '',
        how: input.how ?? '',
        sync_status: 'synced',
        last_synced: now,
        raw_cot_xml: input.rawCotXml ?? '',
    };
}

function buildPliGeometry(lat: number, lon: number) {
    return { x: lon, y: lat, spatialReference: { wkid: 4326 } };
}

const oidFieldCache = new Map<string, string>();

/**
 * The layer's ObjectID field name. Previously re-fetched on *every* PLI update (every 30 s) with no
 * token, so on a secured layer it always failed and silently fell back to the literal 'OBJECTID' —
 * and on a service whose OID field is named anything else, every update then failed and the
 * scheduler added a brand-new row every 30 seconds forever (§9.3).
 */
async function fetchObjectIdField(serviceUrl: string, token: string | null): Promise<string> {
    const key = layerQueryUrl(serviceUrl);
    const cached = oidFieldCache.get(key);
    if (cached) return cached;
    const field = (await fetchLayerMeta(serviceUrl, token)).objectIdField;
    oidFieldCache.set(key, field);
    return field;
}

export interface PliEditResult { ok: boolean; objectId: number; error: string | null }

interface EditResultEntry {
    success?: boolean;
    objectId?: number;
    error?: { code?: number; description?: string };
}
interface EditResponse {
    addResults?: EditResultEntry[];
    updateResults?: EditResultEntry[];
}

function readEditResult(entry: EditResultEntry | undefined): PliEditResult {
    if (!entry) return { ok: false, objectId: -1, error: 'ArcGIS returned no edit result' };
    if (entry.success !== true) {
        return { ok: false, objectId: -1, error: entry.error?.description ?? 'ArcGIS rejected the edit' };
    }
    // success === true with no objectId is NOT a failure — reporting it as -1 previously made the
    // scheduler add a fresh row on every subsequent tick (§10.6).
    return { ok: true, objectId: entry.objectId ?? -1, error: null };
}

export async function addPliFeature(
    serviceUrl: string, token: string | null, input: PliFeatureInput,
): Promise<PliEditResult> {
    const layerUrl = layerQueryUrl(serviceUrl);
    const feature = { geometry: buildPliGeometry(input.lat, input.lon), attributes: buildPliAttributes(input) };
    const json = await arcgisJson<EditResponse>(`${layerUrl}/applyEdits`, {
        method: 'POST',
        form: { adds: JSON.stringify([feature]), f: 'json' },
        token, portalUrl: getPortalUrl(),
    });
    return readEditResult(json.addResults?.[0]);
}

export async function updatePliFeature(
    serviceUrl: string, token: string | null, objectId: number, input: PliFeatureInput,
): Promise<PliEditResult> {
    const layerUrl = layerQueryUrl(serviceUrl);
    const objectIdField = await fetchObjectIdField(serviceUrl, token);
    const attributes = { ...buildPliAttributes(input), [objectIdField]: objectId };
    const feature = { geometry: buildPliGeometry(input.lat, input.lon), attributes };
    const json = await arcgisJson<EditResponse>(`${layerUrl}/applyEdits`, {
        method: 'POST',
        form: { updates: JSON.stringify([feature]), f: 'json' },
        token, portalUrl: getPortalUrl(),
    });
    return readEditResult(json.updateResults?.[0]);
}

/** Recovers a PLI row's ObjectID by its `uid` attribute — used when an add succeeded without echoing one. */
export async function findPliObjectId(serviceUrl: string, token: string | null, uid: string): Promise<number> {
    const layerUrl = layerQueryUrl(serviceUrl);
    const objectIdField = await fetchObjectIdField(serviceUrl, token);
    const json = await arcgisJson<{ features?: { attributes?: Record<string, unknown> }[] }>(`${layerUrl}/query`, {
        params: {
            where: `uid = '${uid.replace(/'/g, "''")}'`,
            outFields: objectIdField, returnGeometry: 'false', f: 'json',
            orderByFields: `${objectIdField} DESC`, resultRecordCount: '1',
        },
        token, portalUrl: getPortalUrl(),
    });
    const oid = json.features?.[0]?.attributes?.[objectIdField];
    return typeof oid === 'number' ? oid : -1;
}

/** Probes a candidate PLI endpoint: reachable, a layer, and carrying the fields PLI writes (§4.2). */
export const PLI_REQUIRED_FIELDS = ['uid', 'latitude', 'longitude', 'cot_type', 'tak_callsign', 'raw_cot_xml'];

export async function validatePliEndpoint(
    serviceUrl: string, token: string | null,
): Promise<{ ok: true } | { ok: false; reason: string }> {
    let url: string;
    try { url = canonicalizeLayerUrl(serviceUrl).url; }
    catch (e) { return { ok: false, reason: e instanceof Error ? e.message : String(e) }; }

    let raw: { fields?: { name?: string }[]; capabilities?: string };
    try {
        raw = await arcgisJson<{ fields?: { name?: string }[]; capabilities?: string }>(url, {
            params: { f: 'json' }, token, portalUrl: getPortalUrl(),
        });
    } catch (e) {
        return { ok: false, reason: e instanceof ArcGISError ? e.userMessage : String(e) };
    }

    const names = new Set((raw.fields ?? []).map(f => (f.name ?? '').toLowerCase()));
    const missing = PLI_REQUIRED_FIELDS.filter(f => !names.has(f));
    if (missing.length) {
        return { ok: false, reason: `that layer is missing the PLI fields: ${missing.join(', ')}` };
    }
    if (!/create/i.test(raw.capabilities ?? '')) {
        return { ok: false, reason: 'that layer does not allow feature creation (no Create capability)' };
    }
    return { ok: true };
}

// ── Create a hosted PLI Feature Service from a CSV schema template ─────────────

const SCHEMA_CSV =
    'uid,source_system,source_layer,source_objectid,cot_type,tak_callsign,tak_icon,' +
    'tak_remarks,latitude,longitude,hae,ce,le,time,start_time,stale_time,' +
    'sent_toFL_time,sent_by_user,how,sync_status,last_synced,raw_cot_xml\n' +
    'SCHEMA_TEMPLATE,CloudTAK,,,a-f-G-U-C,EXAMPLE,,Schema row,' +
    '34.052235,-117.307899,285.4,9.0,2.0,' +
    '2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,2025-01-01T00:00:30Z,' +
    '2025-01-01T00:00:00Z,admin,m-g,synced,2025-01-01T00:00:00Z,\n';

function schemaField(name: string, type: string, alias: string, length = -1) {
    const f: Record<string, unknown> = { name, type, alias, nullable: true };
    if (length > 0) f.length = length;
    return f;
}

async function uploadCsvItem(portal: string, username: string, token: string, title: string): Promise<string> {
    const form = new FormData();
    form.set('title', title);
    form.set('type', 'CSV');
    form.set('tags', 'FeatureLink,CloudTAK,PLI');
    form.set('description', 'FeatureLink PLI schema — created by CloudTAK FeatureLink plugin');
    form.set('f', 'json');
    form.set('file', new Blob([SCHEMA_CSV], { type: 'text/csv' }), 'featurelink_schema.csv');

    const json = await arcgisJson<{ id?: string }>(
        `${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/addItem`,
        { method: 'POST', formData: form, token, portalUrl: portal },
    );
    if (!json.id) throw new ArcGISError('service', 'addItem returned no item id', { url: portal });
    return json.id;
}

async function deleteItem(portal: string, username: string, token: string, itemId: string): Promise<void> {
    try {
        await arcgisJson(`${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/items/${encodeURIComponent(itemId)}/delete`, {
            method: 'POST', form: { f: 'json' }, token, portalUrl: portal,
        });
    } catch (e) {
        console.warn('[featurelink] could not clean up orphaned CSV item', itemId, e);
    }
}

async function publishCsvItem(
    portal: string, username: string, token: string, itemId: string, name: string,
): Promise<{ serviceItemId: string | null; serviceUrl: string; jobId: string | null }> {
    const fields = [
        schemaField('uid', 'esriFieldTypeString', 'UID', 100),
        schemaField('source_system', 'esriFieldTypeString', 'Source System', 100),
        schemaField('source_layer', 'esriFieldTypeString', 'Source Layer', 255),
        schemaField('source_objectid', 'esriFieldTypeString', 'Source Object ID', 50),
        schemaField('cot_type', 'esriFieldTypeString', 'CoT Type', 100),
        schemaField('tak_callsign', 'esriFieldTypeString', 'TAK Callsign', 255),
        schemaField('tak_icon', 'esriFieldTypeString', 'TAK Icon', 512),
        schemaField('tak_remarks', 'esriFieldTypeString', 'TAK Remarks', 1000),
        schemaField('latitude', 'esriFieldTypeDouble', 'Latitude'),
        schemaField('longitude', 'esriFieldTypeDouble', 'Longitude'),
        schemaField('hae', 'esriFieldTypeDouble', 'HAE (m)'),
        schemaField('ce', 'esriFieldTypeDouble', 'CE (m)'),
        schemaField('le', 'esriFieldTypeDouble', 'LE (m)'),
        schemaField('time', 'esriFieldTypeDate', 'Time'),
        schemaField('start_time', 'esriFieldTypeDate', 'Start Time'),
        schemaField('stale_time', 'esriFieldTypeDate', 'Stale Time'),
        schemaField('sent_toFL_time', 'esriFieldTypeDate', 'Sent to FL Time'),
        schemaField('sent_by_user', 'esriFieldTypeString', 'Sent By User', 255),
        schemaField('how', 'esriFieldTypeString', 'How', 50),
        schemaField('sync_status', 'esriFieldTypeString', 'Sync Status', 50),
        schemaField('last_synced', 'esriFieldTypeDate', 'Last Synced'),
        schemaField('raw_cot_xml', 'esriFieldTypeString', 'Raw CoT XML', 32000),
    ];

    const safeName = (name.replace(/[^a-zA-Z0-9 _]/g, '').trim()) || 'FeatureLink PLI';
    const publishParams = {
        name: safeName,
        locationType: 'coordinates',
        latitudeFieldName: 'latitude',
        longitudeFieldName: 'longitude',
        coordinateFieldType: 'LatLong',
        hasStaticData: false,
        maxRecordCount: 2000,
        capabilities: 'Create,Delete,Query,Update,Editing',
        layerInfo: { fields },
    };

    const json = await arcgisJson<{
        services?: { serviceItemId?: string; serviceurl?: string; encodedServiceURL?: string; jobId?: string }[];
    }>(`${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/publish`, {
        method: 'POST',
        form: { itemId, filetype: 'csv', publishParameters: JSON.stringify(publishParams), f: 'json' },
        token, portalUrl: portal,
    });

    const svc = json.services?.[0];
    const serviceUrl = svc?.serviceurl ?? svc?.encodedServiceURL;
    if (!svc || !serviceUrl) {
        throw new ArcGISError('service', 'publish response contained no service URL', { url: portal });
    }
    return { serviceItemId: svc.serviceItemId ?? null, serviceUrl: serviceUrl.replace(/\/+$/, ''), jobId: svc.jobId ?? null };
}

async function waitForPublishJob(
    portal: string, username: string, token: string, serviceItemId: string | null, jobId: string | null,
): Promise<boolean> {
    if (!serviceItemId || !jobId) return true;
    const statusUrl = `${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/items/${encodeURIComponent(serviceItemId)}/status`;
    for (let i = 0; i < 30; i++) {
        // Check first, then sleep — the old order added a guaranteed 2 s to every publish (§9.6).
        try {
            const json = await arcgisJson<{ status?: string }>(statusUrl, {
                params: { jobId, jobType: 'publish', f: 'json' }, token, portalUrl: portal,
            });
            if (json.status === 'completed') return true;
            if (json.status === 'failed' || json.status === 'cancelled') return false;
        } catch { /* transient — keep polling until the attempt budget runs out */ }
        await new Promise(r => setTimeout(r, 2000));
    }
    return false;
}

async function enableEditing(featureServerUrl: string, token: string, portal: string): Promise<boolean> {
    try {
        const def = { capabilities: 'Create,Delete,Query,Update,Editing', hasStaticData: false, allowGeometryUpdates: true };
        await arcgisJson(`${featureServerUrl}/updateDefinition`, {
            method: 'POST', form: { updateDefinition: JSON.stringify(def), f: 'json' },
            token, portalUrl: portal,
        });
        return true;
    } catch (e) {
        // Not fatal, but no longer silent: without editing every later PLI send fails forever.
        console.warn('[featurelink] enableEditing failed', e);
        return false;
    }
}

/** Deletes the SCHEMA_TEMPLATE row the CSV publish creates, so no phantom "EXAMPLE" contact remains (§9.6). */
async function deleteSchemaRow(layerUrl: string, token: string, portal: string): Promise<void> {
    try {
        await arcgisJson(`${layerUrl}/applyEdits`, {
            method: 'POST',
            form: { deletes: '', where: "uid = 'SCHEMA_TEMPLATE'", f: 'json' },
            token, portalUrl: portal,
        });
    } catch (e) {
        console.warn('[featurelink] could not remove the PLI schema template row', e);
    }
}

export interface CreatePliResult { ok: boolean; url: string | null; message: string }

/**
 * Creates a hosted Feature Layer by uploading the schema CSV then publishing it. Every distinct
 * failure now carries the ArcGIS message instead of collapsing to `null` (§4.2), and a failed
 * publish deletes the orphaned CSV item instead of littering the operator's ArcGIS content.
 */
export async function createPliFeatureService(
    portalUrl: string, username: string, token: string, layerName: string | null,
): Promise<CreatePliResult> {
    const portal = normalizePortalUrl(portalUrl);
    const displayName = layerName?.trim() || `FeatureLink PLI ${username}`;

    let itemId: string;
    try {
        itemId = await uploadCsvItem(portal, username, token, displayName);
    } catch (e) {
        return { ok: false, url: null, message: `Could not upload the schema: ${describe(e)}` };
    }

    let svcInfo;
    try {
        svcInfo = await publishCsvItem(portal, username, token, itemId, displayName);
    } catch (e) {
        await deleteItem(portal, username, token, itemId);
        return { ok: false, url: null, message: `Could not publish the service: ${describe(e)}` };
    }

    if (svcInfo.jobId) {
        const ok = await waitForPublishJob(portal, username, token, svcInfo.serviceItemId, svcInfo.jobId);
        if (!ok) {
            return {
                ok: false, url: null,
                message: 'ArcGIS did not finish publishing the service within 60 s — check your ArcGIS content before retrying',
            };
        }
    }

    const editable = await enableEditing(svcInfo.serviceUrl, token, portal);
    // §9.6: the published layer is not always index 0. Ask the service which layers it has.
    let layerUrl = `${svcInfo.serviceUrl}/0`;
    try {
        const subs = await fetchServiceLayers(svcInfo.serviceUrl, token);
        if (subs[0]) layerUrl = subs[0].url;
    } catch { /* fall back to /0 */ }

    await deleteSchemaRow(layerUrl, token, portal);

    return {
        ok: true, url: layerUrl,
        message: editable
            ? `Created: ${displayName}`
            : `Created: ${displayName} — WARNING: editing could not be enabled, PLI sends may fail`,
    };
}

function describe(e: unknown): string {
    return e instanceof ArcGISError ? e.userMessage : (e instanceof Error ? redactUrl(e.message) : String(e));
}
