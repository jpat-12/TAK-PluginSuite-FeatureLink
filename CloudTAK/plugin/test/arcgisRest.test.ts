// C-06 (pagination), C-08 (sublayer enumeration), C-21 (token never in a URL), C-22 (error bodies),
// C-34 (spatial reference). Every one of these fails against a8f6f4a.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import {
    FakeArcGIS, SERVICE_ROOT, LAYER_0, LAYER_1, layerMeta, pointFeature, WGS84, WEB_MERCATOR,
} from './fixtures.ts';

const realFetch = globalThis.fetch;

async function freshRest(): Promise<typeof import('../lib/arcgisRest.ts')> {
    // Modules cache layer metadata; reset between tests so one test's cache cannot mask another.
    const mod = await import('../lib/arcgisRest.ts');
    mod.invalidateLayerMeta();
    return mod;
}

beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
});

afterEach(() => {
    globalThis.fetch = realFetch;
});

describe('C-06 — pagination', () => {
    it('pages until exceededTransferLimit clears and returns every feature', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && u.searchParams.get('f') === 'json' && !u.pathname.includes('query'))
            ? { body: layerMeta({ maxRecordCount: 2 }) } : null);
        fake.route((u) => {
            if (!u.pathname.endsWith('/0/query')) return null;
            const offset = Number(u.searchParams.get('resultOffset') ?? '0');
            const pages = [
                { features: [pointFeature(1, -117, 34), pointFeature(2, -118, 35)], exceededTransferLimit: true, spatialReference: WGS84 },
                { features: [pointFeature(3, -119, 36), pointFeature(4, -120, 37)], exceededTransferLimit: true, spatialReference: WGS84 },
                { features: [pointFeature(5, -121, 38)], exceededTransferLimit: false, spatialReference: WGS84 },
            ];
            return { body: pages[offset / 2] ?? { features: [], spatialReference: WGS84 } };
        });
        fake.install();

        const rest = await freshRest();
        const result = await rest.downloadLayerAsCoT(LAYER_0, null);

        expect(result.features).toHaveLength(5);
        expect(result.truncated).toBe(false);
        // Three query requests, i.e. the loop actually looped. Before the fix there was exactly one
        // and the layer silently stopped at maxRecordCount.
        expect(fake.countMatching(u => u.pathname.endsWith('/query'))).toBe(3);
    });

    it('sends resultOffset/resultRecordCount and a deterministic order', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta() } : null);
        fake.route((u) => u.pathname.endsWith('/query')
            ? { body: { features: [pointFeature(1, 0, 0)], exceededTransferLimit: false, spatialReference: WGS84 } } : null);
        fake.install();

        const rest = await freshRest();
        await rest.downloadLayerAsCoT(LAYER_0, null);

        const query = new URL(fake.calls.find(c => c.url.includes('/query'))!.url);
        expect(query.searchParams.get('resultOffset')).toBe('0');
        expect(query.searchParams.get('resultRecordCount')).toBe('2');
        expect(query.searchParams.get('orderByFields')).toBe('OBJECTID');
    });
});

describe('C-22 — ArcGIS error bodies served with HTTP 200', () => {
    it('throws a typed auth error for {"error":{"code":498}} instead of reporting 0 features', async () => {
        const fake = new FakeArcGIS();
        fake.route(() => ({ status: 200, body: { error: { code: 498, message: 'Invalid token' } } }));
        fake.install();

        const rest = await freshRest();
        const { ArcGISError } = await import('../lib/arcgisHttp.ts');

        await expect(rest.queryFeatureCount(LAYER_0, 'tok')).rejects.toBeInstanceOf(ArcGISError);
        await expect(rest.queryFeatureCount(LAYER_0, 'tok')).rejects.toMatchObject({ kind: 'auth', code: 498 });
    });

    it('throws on a non-2xx transport status instead of parsing the error page as data', async () => {
        const fake = new FakeArcGIS();
        fake.route(() => ({ status: 403, body: {}, text: '<html>Forbidden</html>' }));
        fake.install();

        const rest = await freshRest();
        await expect(rest.queryFeatureCount(LAYER_0, null)).rejects.toMatchObject({ kind: 'http', status: 403 });
    });

    it('throws on a 200 whose body is not JSON (the classic "Unexpected token <")', async () => {
        const fake = new FakeArcGIS();
        fake.route(() => ({ status: 200, body: {}, text: '<html>login</html>' }));
        fake.install();

        const rest = await freshRest();
        await expect(rest.queryFeatureCount(LAYER_0, null)).rejects.toMatchObject({ kind: 'malformed' });
    });
});

describe('C-21 — token placement', () => {
    it('never puts the token in a URL and sends it as a bearer header on a trusted host', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta() } : null);
        fake.route((u) => u.pathname.endsWith('/query')
            ? { body: { features: [], exceededTransferLimit: false, spatialReference: WGS84 } } : null);
        fake.install();

        const rest = await freshRest();
        await rest.downloadLayerAsCoT(LAYER_0, 'SECRET-TOKEN');

        expect(fake.calls.length).toBeGreaterThan(0);
        for (const call of fake.calls) {
            expect(call.url).not.toContain('SECRET-TOKEN');
            expect(call.url).not.toContain('token=');
        }
        const headers = fake.calls[0]!.init?.headers as Record<string, string>;
        expect(headers['X-Esri-Authorization']).toBe('Bearer SECRET-TOKEN');
    });

    it('C-33 — drops the token entirely for a host that is not on the signed-in portal', async () => {
        const fake = new FakeArcGIS();
        const evil = 'https://evil.example.com/arcgis/rest/services/X/FeatureServer/0';
        fake.route(() => ({ body: layerMeta() }));
        fake.install();

        const { arcgisJson } = await import('../lib/arcgisHttp.ts');
        await arcgisJson(evil, { token: 'SECRET-TOKEN', portalUrl: 'https://www.arcgis.com' });

        const headers = (fake.calls[0]!.init?.headers ?? {}) as Record<string, string>;
        expect(headers['X-Esri-Authorization']).toBeUndefined();
        expect(headers.Authorization).toBeUndefined();
        expect(fake.calls[0]!.url).not.toContain('SECRET-TOKEN');
    });
});

describe('C-34 — spatial reference', () => {
    it('rejects a response that comes back in Web Mercator despite outSR=4326', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta() } : null);
        fake.route((u) => u.pathname.endsWith('/query')
            ? { body: { features: [pointFeature(1, -13056000, 4036000)], spatialReference: WEB_MERCATOR } } : null);
        fake.install();

        const rest = await freshRest();
        // Before the fix these metre values were written straight into lat/lon.
        await expect(rest.downloadLayerAsCoT(LAYER_0, null)).rejects.toThrow(/spatial reference/i);
    });

    it('drops out-of-range and malformed coordinates rather than plotting them', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta() } : null);
        fake.route((u) => u.pathname.endsWith('/query') ? {
            body: {
                features: [
                    pointFeature(1, -117, 34),
                    pointFeature(2, 999, 34),          // longitude out of range
                    { attributes: { OBJECTID: 3 }, geometry: { paths: [[]] } }, // empty part -> undefined coords
                ],
                exceededTransferLimit: false, spatialReference: WGS84,
            },
        } : null);
        fake.install();

        const rest = await freshRest();
        const result = await rest.downloadLayerAsCoT(LAYER_0, null);
        expect(result.features).toHaveLength(1);
        expect(result.skipped).toBe(2);
    });

    it('derives a STABLE uid from the ObjectID across two downloads', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta() } : null);
        fake.route((u) => u.pathname.endsWith('/query')
            ? { body: { features: [pointFeature(42, -117, 34)], exceededTransferLimit: false, spatialReference: WGS84 } } : null);
        fake.install();

        const rest = await freshRest();
        const first = await rest.downloadLayerAsCoT(LAYER_0, null);
        const second = await rest.downloadLayerAsCoT(LAYER_0, null);
        // `FL-${i}-${Date.now()}` used to change every refresh, tearing down and re-adding every
        // marker on every scheduler tick.
        expect(first.features[0]!.uid).toBe(second.features[0]!.uid);
        expect(first.features[0]!.uid).toContain('42');
    });

    it('defaults an unclassified feature to UNKNOWN, not friendly', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta() } : null);
        fake.route((u) => u.pathname.endsWith('/query')
            ? { body: { features: [pointFeature(1, -117, 34)], exceededTransferLimit: false, spatialReference: WGS84 } } : null);
        fake.install();

        const rest = await freshRest();
        const result = await rest.downloadLayerAsCoT(LAYER_0, null);
        expect(result.features[0]!.cotType).toBe('a-u-G');
    });
});

describe('C-08 — sublayer enumeration', () => {
    it('turns a 3-sublayer FeatureServer into 3 distinct layer URLs', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => u.pathname.endsWith('/FeatureServer') ? {
            body: {
                layers: [
                    { id: 0, name: 'Boundaries', geometryType: 'esriGeometryPolygon' },
                    { id: 1, name: 'Roads', geometryType: 'esriGeometryPolyline' },
                    { id: 2, name: 'Signs', geometryType: 'esriGeometryPoint' },
                ],
            },
        } : null);
        fake.install();

        const rest = await freshRest();
        const subs = await rest.fetchServiceLayers(SERVICE_ROOT, null);

        expect(subs.map(s => s.url)).toEqual([`${SERVICE_ROOT}/0`, `${SERVICE_ROOT}/1`, `${SERVICE_ROOT}/2`]);
        expect(subs.map(s => s.id)).toEqual([0, 1, 2]);
    });

    it('skips group layers, which hold no features', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => u.pathname.endsWith('/FeatureServer') ? {
            body: { layers: [{ id: 0, name: 'Group', subLayerIds: [1] }, { id: 1, name: 'Real' }] },
        } : null);
        fake.install();

        const rest = await freshRest();
        const subs = await rest.fetchServiceLayers(SERVICE_ROOT, null);
        expect(subs).toHaveLength(1);
        expect(subs[0]!.id).toBe(1);
    });

    it('honours an explicit /{layerId} instead of collapsing it to /0', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => u.pathname.endsWith('/1') ? { body: layerMeta({ name: 'Roads' }) } : null);
        fake.install();

        const rest = await freshRest();
        const subs = await rest.fetchServiceLayers(LAYER_1, null);
        expect(subs).toHaveLength(1);
        expect(subs[0]!.url).toBe(LAYER_1);
        // ensureLayerIndex used to append /0 to a FeatureServer root and leave everything else
        // alone, so layer 1's own metadata was never fetched from a browse row.
        expect(fake.calls.every(c => !c.url.includes('/FeatureServer/0'))).toBe(true);
    });
});

describe('RC-3 — the renderer is fetched once and retained', () => {
    it('fetchLayerMeta caches, so three consumers cause one ?f=json', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query'))
            ? { body: layerMeta({ drawingInfo: { renderer: { type: 'simple' } } }) } : null);
        fake.install();

        const rest = await freshRest();
        await rest.fetchLayerInfo(LAYER_0, null);
        await rest.fetchGeometryType(LAYER_0, null);
        const meta = await rest.fetchLayerMeta(LAYER_0, null);

        expect(meta.renderer).toEqual({ type: 'simple' });
        expect(fake.countMatching(u => u.pathname.endsWith('/0'))).toBe(1);
    });
});
