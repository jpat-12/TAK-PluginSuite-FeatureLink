// ArcGIS REST client — fetch-based port of the ATAK plugin's arcgis/ArcGISRestClient.java.
// All functions are async; every endpoint/param shape mirrors the Java version exactly.

import { newLayer } from './types.ts';
import type { ArcGISLayer, CotFieldMapping, DownloadedFeature, PliFeatureInput } from './types.ts';

function normalizePortalUrl(portalUrl: string): string {
    return (portalUrl?.trim() || 'https://www.arcgis.com').replace(/\/+$/, '');
}

// Ensures URL points to layer index 0 of a FeatureServer.
function ensureLayerIndex(url: string): string {
    const trimmed = (url ?? '').trim().replace(/\/+$/, '');
    if (/FeatureServer$/.test(trimmed)) return `${trimmed}/0`;
    return trimmed;
}

async function readJson<T>(res: Response): Promise<T> {
    return res.json() as Promise<T>;
}

// ── Layer search ──────────────────────────────────────────────────────────────

export async function searchUserLayers(portalUrl: string, token: string, username: string): Promise<ArcGISLayer[]> {
    const q = `type:"Feature Service" AND owner:${username}`;
    const params = new URLSearchParams({ q, num: '100', f: 'json', token });
    const url = `${normalizePortalUrl(portalUrl)}/sharing/rest/search?${params.toString()}`;
    try {
        const json = await readJson<{ results?: { title?: string; url?: string; access?: string }[] }>(await fetch(url));
        const out: ArcGISLayer[] = [];
        for (const item of json.results ?? []) {
            if (item.url) {
                // Portal item "access": "public" (shared to Everyone), "org", or "private" —
                // used to decide which on-device section this layer lands in once downloaded.
                const access = item.access === 'public' || item.access === 'org' || item.access === 'private'
                    ? item.access : 'private';
                out.push(newLayer(item.title ?? 'Unnamed', item.url, 'private', access));
            }
        }
        return out;
    } catch (e) {
        console.error('[featurelink] searchUserLayers failed', e);
        return [];
    }
}

// ── Feature count ─────────────────────────────────────────────────────────────

export async function queryFeatureCount(serviceUrl: string, token: string | null): Promise<number> {
    const layerUrl = ensureLayerIndex(serviceUrl);
    const params = new URLSearchParams({ where: '1=1', returnCountOnly: 'true', f: 'json' });
    if (token) params.set('token', token);
    const json = await readJson<{ count?: number }>(await fetch(`${layerUrl}/query?${params.toString()}`));
    return json.count ?? 0;
}

// ── Layer info ─────────────────────────────────────────────────────────────────

export async function fetchLayerInfo(serviceUrl: string): Promise<ArcGISLayer | null> {
    try {
        const url = ensureLayerIndex(serviceUrl);
        const json = await readJson<{ error?: unknown; name?: string; serviceDescription?: string }>(
            await fetch(`${url}?f=json`),
        );
        if (json.error) return null;
        const name = json.name || json.serviceDescription || 'Unknown Layer';
        return newLayer(name, serviceUrl, 'public');
    } catch (e) {
        console.error('[featurelink] fetchLayerInfo failed for', serviceUrl, e);
        return null;
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

function extractFirstVertex(geom: Record<string, unknown>): [number, number] | null {
    const paths = geom.paths as number[][][] | undefined;
    if (paths?.[0]?.[0]) return [paths[0][0][0], paths[0][0][1]];
    const rings = geom.rings as number[][][] | undefined;
    if (rings?.[0]?.[0]) return [rings[0][0][0], rings[0][0][1]];
    return null;
}

export async function downloadLayerAsCoT(
    serviceUrl: string, token: string | null, mapping: CotFieldMapping | null = null,
): Promise<DownloadedFeature[]> {
    const results: DownloadedFeature[] = [];
    const layerUrl = ensureLayerIndex(serviceUrl);
    const params = new URLSearchParams({ where: '1=1', outFields: '*', outSR: '4326', f: 'json' });
    if (token) params.set('token', token);

    let json: { features?: { attributes?: Record<string, unknown>; geometry?: Record<string, unknown> }[] };
    try {
        json = await readJson(await fetch(`${layerUrl}/query?${params.toString()}`));
    } catch (e) {
        console.error('[featurelink] downloadLayerAsCoT query failed', e);
        return results;
    }

    const features = json.features ?? [];
    features.forEach((feat, i) => {
        try {
            const geom = feat.geometry;
            if (!geom) return;
            let lat = NaN, lon = NaN;
            if (typeof geom.x === 'number' && typeof geom.y === 'number') {
                lon = geom.x; lat = geom.y;
            } else {
                const v = extractFirstVertex(geom);
                if (v) { lon = v[0]; lat = v[1]; }
            }
            if (Number.isNaN(lat) || Number.isNaN(lon)) return;

            const attrs = feat.attributes ?? {};
            const attrMap: Record<string, string> = {};
            for (const [k, v] of Object.entries(attrs)) {
                if (v !== undefined && v !== null) attrMap[k] = String(v);
            }

            let uid = resolveField(attrs, mapping?.uidFields ?? [], 'uid');
            let cotType = resolveField(attrs, mapping?.typeFields ?? [], 'cot_type');
            let callsign = resolveField(attrs, mapping?.callsignFields ?? [], 'tak_callsign');
            const remarks = resolveField(attrs, mapping?.remarksFields ?? [], 'tak_remarks');
            const hae = typeof attrs.hae === 'number' ? attrs.hae : NaN;

            if (!uid) uid = `FL-${i}-${Date.now()}`;
            if (!cotType) cotType = 'a-f-G';
            if (!callsign) callsign = `Feature-${i}`;

            results.push({ uid, cotType, callsign, remarks, lat, lon, hae, attributes: attrMap });
        } catch (e) {
            console.warn('[featurelink] Skipping malformed feature at index', i, e);
        }
    });

    console.debug('[featurelink] downloadLayerAsCoT:', results.length, 'features from', serviceUrl);
    return results;
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
        source_layer: '',
        source_objectid: '',
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

async function fetchObjectIdField(layerUrl: string): Promise<string> {
    try {
        const json = await readJson<{ objectIdField?: string }>(await fetch(`${layerUrl}?f=json`));
        return json.objectIdField ?? 'OBJECTID';
    } catch {
        return 'OBJECTID';
    }
}

// Adds a new PLI feature. Returns the new feature's objectId, or -1 if the server didn't
// report one — callers should persist this and switch to updatePliFeature() for subsequent
// sends rather than calling this repeatedly and creating duplicate rows.
export async function addPliFeature(serviceUrl: string, token: string | null, input: PliFeatureInput): Promise<number> {
    const layerUrl = ensureLayerIndex(serviceUrl);
    const feature = { geometry: buildPliGeometry(input.lat, input.lon), attributes: buildPliAttributes(input) };
    const body = new URLSearchParams({ adds: JSON.stringify([feature]), f: 'json' });
    if (token) body.set('token', token);

    const json = await readJson<{ addResults?: { success?: boolean; objectId?: number }[] }>(
        await fetch(`${layerUrl}/applyEdits`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString() }),
    );
    const result = json.addResults?.[0];
    if (!result?.success) return -1;
    return result.objectId ?? -1;
}

// Updates an existing PLI feature (by objectId, from a prior addPliFeature call) in place.
// Returns false if the server reports the update didn't succeed (e.g. the feature no longer
// exists) — callers should forget the objectId and add a fresh feature next time.
export async function updatePliFeature(
    serviceUrl: string, token: string | null, objectId: number, input: PliFeatureInput,
): Promise<boolean> {
    const layerUrl = ensureLayerIndex(serviceUrl);
    const objectIdField = await fetchObjectIdField(layerUrl);

    const attributes = { ...buildPliAttributes(input), [objectIdField]: objectId };
    const feature = { geometry: buildPliGeometry(input.lat, input.lon), attributes };
    const body = new URLSearchParams({ updates: JSON.stringify([feature]), f: 'json' });
    if (token) body.set('token', token);

    const json = await readJson<{ updateResults?: { success?: boolean }[] }>(
        await fetch(`${layerUrl}/applyEdits`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString() }),
    );
    return json.updateResults?.[0]?.success === true;
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

async function uploadCsvItem(portal: string, username: string, token: string, title: string): Promise<string | null> {
    const form = new FormData();
    form.set('title', title);
    form.set('type', 'CSV');
    form.set('tags', 'FeatureLink,CloudTAK,PLI');
    form.set('description', 'FeatureLink PLI schema — created by CloudTAK FeatureLink plugin');
    form.set('f', 'json');
    form.set('token', token);
    form.set('file', new Blob([SCHEMA_CSV], { type: 'text/csv' }), 'featurelink_schema.csv');

    const endpoint = `${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/addItem`;
    const json = await readJson<{ id?: string; error?: { message?: string } }>(
        await fetch(endpoint, { method: 'POST', body: form }),
    );
    if (json.error) {
        console.error('[featurelink] addItem error:', json.error.message);
        return null;
    }
    return json.id ?? null;
}

async function publishCsvItem(
    portal: string, username: string, token: string, itemId: string, name: string,
): Promise<{ serviceItemId: string | null; serviceUrl: string; jobId: string | null } | null> {
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

    const endpoint = `${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/publish`;
    const body = new URLSearchParams({
        itemId, filetype: 'csv', publishParameters: JSON.stringify(publishParams), f: 'json', token,
    });
    const json = await readJson<{
        error?: { message?: string };
        services?: { serviceItemId?: string; serviceurl?: string; encodedServiceURL?: string; jobId?: string }[];
    }>(await fetch(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString() }));

    if (json.error) {
        console.error('[featurelink] publish error:', json.error.message);
        return null;
    }
    const svc = json.services?.[0];
    const serviceUrl = svc?.serviceurl ?? svc?.encodedServiceURL;
    if (!svc || !serviceUrl) {
        console.error('[featurelink] publish response missing serviceurl');
        return null;
    }
    return { serviceItemId: svc.serviceItemId ?? null, serviceUrl: serviceUrl.replace(/\/+$/, ''), jobId: svc.jobId ?? null };
}

async function waitForPublishJob(portal: string, username: string, token: string, serviceItemId: string | null, jobId: string | null): Promise<boolean> {
    if (!serviceItemId || !jobId) return true;
    const statusUrl = `${portal}/sharing/rest/content/users/${encodeURIComponent(username)}/items/${encodeURIComponent(serviceItemId)}/status`;
    const params = new URLSearchParams({ jobId, jobType: 'publish', f: 'json', token });
    for (let i = 0; i < 30; i++) {
        await new Promise(r => setTimeout(r, 2000));
        try {
            const json = await readJson<{ status?: string }>(await fetch(`${statusUrl}?${params.toString()}`));
            if (json.status === 'completed') return true;
            if (json.status === 'failed' || json.status === 'cancelled') return false;
        } catch { /* keep polling */ }
    }
    return false;
}

async function enableEditing(featureServerUrl: string, token: string): Promise<void> {
    try {
        const def = { capabilities: 'Create,Delete,Query,Update,Editing', hasStaticData: false, allowGeometryUpdates: true };
        const body = new URLSearchParams({ updateDefinition: JSON.stringify(def), f: 'json', token });
        await fetch(`${featureServerUrl}/updateDefinition`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString() });
    } catch (e) {
        console.warn('[featurelink] enableEditing failed (non-fatal)', e);
    }
}

// Creates a hosted Feature Layer by uploading the schema CSV then publishing it. Returns
// the URL to layer 0 of the new service, or null on failure.
export async function createPliFeatureService(
    portalUrl: string, username: string, token: string, layerName: string | null,
): Promise<string | null> {
    try {
        const portal = normalizePortalUrl(portalUrl);
        const displayName = layerName?.trim() || `FeatureLink PLI ${username}`;

        const itemId = await uploadCsvItem(portal, username, token, displayName);
        if (!itemId) return null;

        const svcInfo = await publishCsvItem(portal, username, token, itemId, displayName);
        if (!svcInfo) return null;

        if (svcInfo.jobId) {
            const ok = await waitForPublishJob(portal, username, token, svcInfo.serviceItemId, svcInfo.jobId);
            if (!ok) console.warn('[featurelink] Publish job did not complete cleanly — proceeding anyway');
        }

        await enableEditing(svcInfo.serviceUrl, token);
        return `${svcInfo.serviceUrl}/0`;
    } catch (e) {
        console.error('[featurelink] createPliFeatureService failed', e);
        return null;
    }
}
