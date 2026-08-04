// C-07 — the owner's field defect, and C-24 — the precedence inversion.
//
// The headline assertion is the first one: a layer downloaded via the ⬇ button (downloadLayer)
// ends up with real symbology. Against a8f6f4a that test fails, because generateAutoIconset had
// exactly one call site and it was inside addPublicLayer().

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import {
    FakeArcGIS, SERVICE_ROOT, LAYER_0, layerMeta, pointFeature, WGS84,
    UNIQUE_VALUE_POLYGON_RENDERER, SIMPLE_LINE_RENDERER,
} from './fixtures.ts';

const realFetch = globalThis.fetch;

// A 1x1 transparent PNG, base64 — the shape a renderer's esriPMS imageData really has.
const PNG_1PX = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

const PMS_RENDERER = {
    type: 'uniqueValue',
    field1: 'CATEGORY',
    uniqueValueInfos: [
        { value: 'FIRE', label: 'Fire Station', symbol: { type: 'esriPMS', imageData: PNG_1PX } },
        { value: 'POLICE', label: 'Police', symbol: { type: 'esriPMS', imageData: PNG_1PX } },
    ],
    defaultSymbol: { type: 'esriPMS', imageData: PNG_1PX },
};

function routeLayer(fake: FakeArcGIS, renderer: unknown, geometryType = 'esriGeometryPoint'): void {
    fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query'))
        ? { body: layerMeta({ geometryType, drawingInfo: { renderer } }) } : null);
    fake.route((u) => u.pathname.endsWith('/0/query')
        ? { body: { features: [pointFeature(1, -117, 34, { CATEGORY: 'FIRE', STATUS: 'OPEN' })], exceededTransferLimit: false, spatialReference: WGS84 } }
        : null);
    fake.route((u) => u.pathname.startsWith('/api/iconset') ? { body: { ok: true } } : null);
}

async function freshModules() {
    const rest = await import('../lib/arcgisRest.ts');
    rest.invalidateLayerMeta();
    const store = await import('../lib/store.ts');
    store.resetStore();
    const layerActions = await import('../lib/layerActions.ts');
    const types = await import('../lib/types.ts');
    return { rest, store, layerActions, types };
}

beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    // The CloudTAK session token autoIconset reaches for when registering icons.
    localStorage.setItem('CapacitorStorage.token', 'cloudtak-session');
});

afterEach(() => { globalThis.fetch = realFetch; });

describe('C-07 / FIX-1 — symbology resolves on the DOWNLOAD path', () => {
    it('a browse-list layer downloaded with downloadLayer() gets per-value icon paths', async () => {
        const fake = new FakeArcGIS();
        routeLayer(fake, PMS_RENDERER);
        fake.install();

        const { store, layerActions, types } = await freshModules();
        const layer = types.newLayer('Roads', LAYER_0, 'public');
        store.store.publicLayers.push(layer);

        // This is the ⬇ button / browse-download / scheduler path — NOT "paste a public URL".
        await layerActions.downloadLayer(layer);

        const config = store.store.displayConfigs[LAYER_0];
        expect(config, 'displayConfig must exist after a plain download').toBeTruthy();
        expect(config!.sym?.t).toBe('adv');
        const byValue = Object.fromEntries((config!.sym?.vs ?? []).map(e => [e.v, e.up]));
        expect(Object.keys(byValue)).toEqual(expect.arrayContaining(['FIRE', 'POLICE']));
        // {uid}/{group}/{file} — the cross-platform string-identity contract.
        // AUTO-ICONSET-SPEC §5.1 keeps spaces in the GROUP ("Roads Icons"); §5.2 replaces them in
        // the FILENAME ("Fire Station" -> "Fire_Station.png"). ATAK and TAK Portal must produce
        // byte-identical strings, so both halves are asserted exactly.
        expect(byValue.FIRE).toMatch(/^[0-9a-f]{64}\/Roads Icons\/Fire_Station\.png$/);
        expect(byValue.POLICE).toMatch(/^[0-9a-f]{64}\/Roads Icons\/Police\.png$/);
        expect(layer.stylingStatus).toBe('ok');
    });

    it('resolves symbology on a scheduler-driven refresh too (same code path)', async () => {
        const fake = new FakeArcGIS();
        routeLayer(fake, PMS_RENDERER);
        fake.install();

        const { store, types } = await freshModules();
        const layer = types.newLayer('Roads', LAYER_0, 'public');
        layer.lastSync = 1; // already downloaded once, so the recurrence check considers it
        layer.recurrenceInterval = 30;
        store.store.publicLayers.push(layer);

        const scheduler = await import('../lib/scheduler.ts');
        await scheduler.checkLayerRecurrence();

        expect(store.store.displayConfigs[LAYER_0]?.sym?.t).toBe('adv');
    });

    it('extracts shape styling for a polyline layer with no icons at all', async () => {
        const fake = new FakeArcGIS();
        routeLayer(fake, SIMPLE_LINE_RENDERER, 'esriGeometryPolyline');
        fake.install();

        const { store, layerActions, types } = await freshModules();
        const layer = types.newLayer('Lines', LAYER_0, 'public');
        store.store.publicLayers.push(layer);
        await layerActions.downloadLayer(layer);

        const config = store.store.displayConfigs[LAYER_0];
        expect(config?.singleShapeStyle).toBeTruthy();
        expect(config!.singleShapeStyle!.strokeColor).toBe('#0a141e');
        expect(config!.singleShapeStyle!.strokeWidthPx).toBe(3);
        expect(config!.singleShapeStyle!.strokeDash).toBe('dash');
    });
});

describe('C-07 / FIX-2 — the config guard is content-based, not truthiness-based', () => {
    it('hasMeaningfulStyling rejects the empty config a .featurelinkshare produces', async () => {
        const { hasMeaningfulStyling } = await import('../lib/symbology.ts');
        const { buildShareConfigJson } = await import('../lib/layerShare.ts');
        const { newLayer } = await import('../lib/types.ts');

        const shared = JSON.parse(buildShareConfigJson(newLayer('Roads', LAYER_0, 'public'))) as Record<string, unknown>;
        // The share file carries v/url/layer/freq — and NO styling whatsoever.
        expect(shared.sym).toBeUndefined();
        expect(hasMeaningfulStyling(shared as never)).toBe(false);
        expect(hasMeaningfulStyling({ v: 2, sym: { t: 's', c: '#ff0000' } } as never)).toBe(true);
        expect(hasMeaningfulStyling(null)).toBe(false);
    });

    it('an imported empty config does not block generation on the next download', async () => {
        const fake = new FakeArcGIS();
        routeLayer(fake, PMS_RENDERER);
        fake.install();

        const { store, layerActions, types } = await freshModules();
        // Exactly what importing a .featurelinkshare leaves behind: a truthy but styling-free config.
        store.store.displayConfigs[LAYER_0] = { v: 2, url: LAYER_0, layer: { name: 'Roads' } };

        const layer = types.newLayer('Roads', LAYER_0, 'public');
        store.store.publicLayers.push(layer);
        await layerActions.downloadLayer(layer);

        // Before FIX-2 the truthy object made the guard skip generation forever, while the UI
        // showed a green "Config" badge over default blue markers.
        expect(store.store.displayConfigs[LAYER_0]?.sym?.t).toBe('adv');
        // Fields the share carried are preserved, not clobbered.
        expect(store.store.displayConfigs[LAYER_0]?.layer?.name).toBe('Roads');
    });
});

describe('C-24 — uniqueValue renderer WITH a defaultSymbol', () => {
    it('resolves the per-value style, not the default', async () => {
        const { extractAutoSymbology } = await import('../lib/autoSymbology.ts');
        const { buildShapeStyleFields, resolveShapeStyle } = await import('../lib/displayConfig.ts');

        const result = extractAutoSymbology(UNIQUE_VALUE_POLYGON_RENDERER as never, undefined);
        const config = { v: 3, ...buildShapeStyleFields(result) };

        // Both are populated — this is precisely the case the inversion broke.
        expect(config.singleShapeStyle, 'defaultSymbol must produce a singleShapeStyle').toBeTruthy();
        expect(Object.keys(config.shapeStyleByValue ?? {})).toEqual(expect.arrayContaining(['OPEN', 'CLOSED']));

        const open = resolveShapeStyle(config as never, { STATUS: 'OPEN' });
        const closed = resolveShapeStyle(config as never, { STATUS: 'CLOSED' });
        expect(open!.fillColor).toBe('#00ff00');
        expect(closed!.fillColor).toBe('#ff0000');
        // The grey default must NOT win.
        expect(open!.fillColor).not.toBe(config.singleShapeStyle!.fillColor);
    });

    it('falls back to singleShapeStyle for a value with no per-value entry', async () => {
        const { extractAutoSymbology } = await import('../lib/autoSymbology.ts');
        const { buildShapeStyleFields, resolveShapeStyle } = await import('../lib/displayConfig.ts');

        const config = { v: 3, ...buildShapeStyleFields(extractAutoSymbology(UNIQUE_VALUE_POLYGON_RENDERER as never, undefined)) };
        const other = resolveShapeStyle(config as never, { STATUS: 'SOMETHING-ELSE' });
        expect(other).toEqual(config.singleShapeStyle);
    });
});

describe('C-08 — a multi-sublayer service produces one row per sublayer', () => {
    it('addPublicLayer on a bare service root adds every sublayer', async () => {
        const fake = new FakeArcGIS();
        fake.route((u) => u.pathname.endsWith('/FeatureServer') ? {
            body: {
                layers: [
                    { id: 0, name: 'Boundaries', geometryType: 'esriGeometryPolygon' },
                    { id: 1, name: 'Roads', geometryType: 'esriGeometryPolyline' },
                ],
            },
        } : null);
        fake.route((u) => /\/FeatureServer\/[01]$/.test(u.pathname)
            ? { body: layerMeta({ name: u.pathname.endsWith('/0') ? 'Boundaries' : 'Roads', drawingInfo: { renderer: SIMPLE_LINE_RENDERER } }) }
            : null);
        fake.route((u) => u.pathname.includes('/query')
            ? { body: { features: [], exceededTransferLimit: false, spatialReference: WGS84 } } : null);
        fake.install();

        const { store, layerActions } = await freshModules();
        const result = await layerActions.addPublicLayer(SERVICE_ROOT);

        expect(result.ok).toBe(true);
        expect(store.store.publicLayers.map(l => l.url)).toEqual([`${SERVICE_ROOT}/0`, `${SERVICE_ROOT}/1`]);
        expect(store.store.publicLayers.map(l => l.layerId)).toEqual([0, 1]);
    });
});
