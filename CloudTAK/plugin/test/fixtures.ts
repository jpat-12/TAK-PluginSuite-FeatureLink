// Shared fixtures + a scriptable `fetch` double.
//
// Everything here models a REAL ArcGIS response shape, including the two that used to be
// misread as success: an `{"error":{…}}` envelope served with HTTP 200, and a query response whose
// `spatialReference` is Web Mercator despite `outSR=4326` having been requested.

export interface RouteHandler {
    (url: URL, init: RequestInit | undefined): { status?: number; body: unknown; text?: string } | null;
}

export interface FetchCall { url: string; init: RequestInit | undefined }

export class FakeArcGIS {
    readonly calls: FetchCall[] = [];
    private readonly handlers: RouteHandler[] = [];

    route(handler: RouteHandler): this {
        this.handlers.push(handler);
        return this;
    }

    /** Number of requests whose path ends with `suffix` and which carried the given query param value. */
    countMatching(predicate: (u: URL) => boolean): number {
        return this.calls.filter(c => predicate(new URL(c.url))).length;
    }

    install(): void {
        globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
            const raw = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
            this.calls.push({ url: raw, init });
            const url = new URL(raw, 'https://services1.arcgis.com');
            for (const handler of this.handlers) {
                const result = handler(url, init);
                if (result) {
                    const status = result.status ?? 200;
                    const text = result.text ?? JSON.stringify(result.body);
                    return new Response(text, { status, headers: { 'Content-Type': 'application/json' } });
                }
            }
            return new Response(JSON.stringify({ error: { code: 404, message: `no fixture for ${url.pathname}` } }), { status: 200 });
        }) as typeof fetch;
    }
}

export const SERVICE_ROOT = 'https://services1.arcgis.com/abc/arcgis/rest/services/Roads/FeatureServer';
export const LAYER_0 = `${SERVICE_ROOT}/0`;
export const LAYER_1 = `${SERVICE_ROOT}/1`;

/** A uniqueValue renderer that ALSO declares a defaultSymbol — the C-24 case. */
export const UNIQUE_VALUE_POLYGON_RENDERER = {
    type: 'uniqueValue',
    field1: 'STATUS',
    defaultSymbol: {
        type: 'esriSFS', style: 'esriSFSSolid', color: [128, 128, 128, 255],
        outline: { type: 'esriSLS', style: 'esriSLSSolid', color: [0, 0, 0, 255], width: 1 },
    },
    defaultLabel: 'Other',
    uniqueValueInfos: [
        {
            value: 'OPEN', label: 'Open',
            symbol: {
                type: 'esriSFS', style: 'esriSFSSolid', color: [0, 255, 0, 255],
                outline: { type: 'esriSLS', style: 'esriSLSSolid', color: [0, 128, 0, 255], width: 2 },
            },
        },
        {
            value: 'CLOSED', label: 'Closed',
            symbol: {
                type: 'esriSFS', style: 'esriSFSSolid', color: [255, 0, 0, 255],
                outline: { type: 'esriSLS', style: 'esriSLSSolid', color: [128, 0, 0, 255], width: 2 },
            },
        },
    ],
};

/** Simple esriSLS line renderer — produces singleShapeStyle only. */
export const SIMPLE_LINE_RENDERER = {
    type: 'simple',
    symbol: { type: 'esriSLS', style: 'esriSLSDash', color: [10, 20, 30, 128], width: 3 },
};

export function layerMeta(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        name: 'Roads',
        geometryType: 'esriGeometryPoint',
        objectIdField: 'OBJECTID',
        maxRecordCount: 2,
        capabilities: 'Query',
        ...overrides,
    };
}

export function pointFeature(oid: number, lon: number, lat: number, attrs: Record<string, unknown> = {}) {
    return { attributes: { OBJECTID: oid, ...attrs }, geometry: { x: lon, y: lat } };
}

export const WGS84 = { wkid: 4326, latestWkid: 4326 };
export const WEB_MERCATOR = { wkid: 102100, latestWkid: 3857 };
