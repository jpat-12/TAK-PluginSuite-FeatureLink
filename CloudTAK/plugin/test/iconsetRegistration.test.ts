// CloudTAK's POST /api/iconset contract, and what happens when it says no.
//
// Field-found on map.prod.ilwg.us: every icon registration was answered with a bare HTTP 400 and
// the operator saw a layer with NO styling at all — no icons, but also no colours, shapes, labels
// or popups. Two separate defects:
//
//   1. the body violated CloudTAK's TypeBox schema three ways (api/routes/icons.ts), and
//   2. the resulting throw propagated out of generateAutoIconset into ensureLayerSymbology's
//      catch, which stored no display config — so an optional server-side step took the whole
//      layer's styling down with it.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FakeArcGIS, SERVICE_ROOT, LAYER_0, layerMeta, pointFeature, WGS84 } from './fixtures.ts';

const realFetch = globalThis.fetch;

const PNG_1PX = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

/** One esriPMS (an icon) and one esriSMS (a colour/shape only) on the same renderer. */
const MIXED_RENDERER = {
    type: 'uniqueValue',
    field1: 'STATUS',
    uniqueValueInfos: [
        { value: 'OPEN', label: 'Open', symbol: { type: 'esriPMS', imageData: PNG_1PX } },
        { value: 'CLOSED', label: 'Closed', symbol: { type: 'esriSMS', style: 'esriSMSCircle', color: [255, 0, 0, 255], size: 8 } },
    ],
};

function routeLayer(fake: FakeArcGIS, renderer: unknown, name = 'Roads'): void {
    fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query'))
        ? { body: layerMeta({ name, drawingInfo: { renderer } }) } : null);
    fake.route((u) => u.pathname.endsWith('/0/query')
        ? { body: { features: [pointFeature(1, -117, 34, { STATUS: 'OPEN' })], exceededTransferLimit: false, spatialReference: WGS84 } }
        : null);
}

async function freshModules() {
    const rest = await import('../lib/arcgisRest.ts');
    rest.invalidateLayerMeta();
    const store = await import('../lib/store.ts');
    store.resetStore();
    return { rest, store, autoIconset: await import('../lib/autoIconset.ts'), symbology: await import('../lib/symbology.ts') };
}

beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    localStorage.setItem('CapacitorStorage.token', 'cloudtak-session');
});

afterEach(() => { globalThis.fetch = realFetch; });

describe('POST /api/iconset body satisfies CloudTAK\'s schema', () => {
    it('sends internal, a lowercase scope, and a name within NameField\'s 64 chars', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        // 70 characters, so the spec's "<base> Icons" group overflows CloudTAK's 64-char limit.
        routeLayer(fake, MIXED_RENDERER, 'A'.repeat(70));
        let body: Record<string, unknown> | null = null;
        fake.route((u, init) => {
            if (u.pathname !== '/api/iconset') return null;
            body = JSON.parse(String(init?.body)) as Record<string, unknown>;
            return { body: { ok: true } };
        });
        fake.route((u) => u.pathname.startsWith('/api/iconset/') ? { body: { ok: true } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });
        expect(result).not.toBeNull();

        const sent = body as unknown as Record<string, unknown>;
        // `internal` is REQUIRED in the schema (Type.Boolean, not Type.Optional); omitting it 400s.
        expect(sent.internal).toBe(false);
        // Type.Enum(ResourceCreationScope) validates the VALUE ('server'|'user'), not the enum key.
        expect(sent.scope).toBe('user');
        // Default.NameField caps at 64.
        expect(String(sent.name).length).toBeLessThanOrEqual(64);
        // ...but default_group is unconstrained and MUST keep the full spec string, or the
        // {uid}/{group}/{file} paths stop matching ATAK/WinTAK/TAK Portal for the same layer.
        expect(sent.default_group).toBe(result!.group);
        expect(String(sent.default_group).length).toBeGreaterThan(64);
    });
});

describe('a rejected iconset must not cost the layer its other styling', () => {
    it('keeps esriSMS colour/shape styling when /api/iconset 400s', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        fake.route((u) => u.pathname.startsWith('/api/iconset')
            ? { status: 400, body: { message: 'must have required property \'internal\'' } } : null);
        fake.install();

        // Does not throw — that throw is what used to erase the whole display config.
        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });

        expect(result).not.toBeNull();
        expect(result!.iconCount).toBe(0);
        expect(result!.warnings.join(' ')).toMatch(/could not be registered/);
        // The esriSMS half needs no server, so it survives.
        expect(result!.displayConfig.sym?.t).toBe('uv');
        expect(JSON.stringify(result!.displayConfig)).toContain('#ff0000');
    });

    it('drops icon paths rather than pointing markers at icons the server does not have', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        fake.route((u) => u.pathname.startsWith('/api/iconset') ? { status: 400, body: { message: 'nope' } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });
        // No `{uid}/{group}/{file}` anywhere in the config: those would all 404 on this server.
        expect(JSON.stringify(result!.displayConfig)).not.toContain(result!.uid);
    });

    it('the layer still ends up with a stored display config via ensureLayerSymbology', async () => {
        const { store, symbology } = await freshModules();
        const types = await import('../lib/types.ts');
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        fake.route((u) => u.pathname.startsWith('/api/iconset') ? { status: 400, body: { message: 'nope' } } : null);
        fake.install();

        const layer = types.newLayer('Roads', LAYER_0, 'public');
        await symbology.ensureLayerSymbology(layer, null);

        // Before the fix this key was absent: the throw reached ensureLayerSymbology's catch and
        // nothing was stored, so every feature fell back to the hardcoded #3388ff marker.
        expect(store.store.displayConfigs[LAYER_0]).toBeDefined();
        expect(store.store.displayConfigs[LAYER_0]?.sym?.t).toBe('uv');
    });
});

describe('the error surfaces what the server actually said', () => {
    it('includes the response body in the warning, not just the status code', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        fake.route((u) => u.pathname.startsWith('/api/iconset')
            ? { status: 400, body: { message: 'must have required property \'internal\'' } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });
        // "HTTP 400" alone is what made this take a production investigation to identify.
        expect(result!.warnings.join(' ')).toContain('internal');
    });
});

// Keep the unused import honest — SERVICE_ROOT documents where LAYER_0 comes from.
void SERVICE_ROOT;
