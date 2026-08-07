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
        // The existence probe must 404, or creation is skipped and nothing is captured. (The
        // fixture's fallback answers 200 with an ArcGIS error envelope, which reads as "exists".)
        fake.route((u) => (u.pathname.startsWith('/api/iconset/') && !u.pathname.endsWith('/icon'))
            ? { status: 404, body: { message: 'not found' } } : null);
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

describe('an iconset that already exists is reused, not treated as a failure', () => {
    // The UID is a deterministic hash of layer URL + field, so every add after the first collides.
    // This server answers a duplicate with 400; the throw landed before the upload loop, leaving an
    // iconset with zero icons on the server and every {uid}/{group}/{file} path 404ing. The only
    // recovery was deleting the iconset by hand, which is exactly what happened in production.
    it('deletes the stale set and rebuilds it from the current renderer', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        const methods: string[] = [];
        fake.route((u, init) => {
            if (!u.pathname.startsWith('/api/iconset') || u.pathname.endsWith('/icon')) return null;
            methods.push(String(init?.method ?? 'GET'));
            return { body: { uid: 'existing' } };   // exists, and DELETE succeeds
        });
        fake.route((u) => u.pathname.endsWith('/icon') ? { body: { ok: true } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });

        expect(methods).toContain('DELETE');
        expect(methods).toContain('POST');   // recreated after the delete
        expect(result!.iconCount).toBe(1);
        expect(result!.warnings).toEqual([]);
        expect(JSON.stringify(result!.displayConfig)).toContain(result!.uid);
    });

    it('reuses the existing set when the delete is refused, rather than giving up', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        // Someone else's iconset: exists, DELETE forbidden, POST conflicts.
        fake.route((u, init) => {
            if (u.pathname.endsWith('/icon')) return null;
            const method = String(init?.method ?? 'GET');
            if (u.pathname === '/api/iconset' && method === 'POST') return { status: 400, body: { message: 'duplicate key' } };
            if (method === 'DELETE') return { status: 403, body: { message: 'not yours' } };
            return { body: { uid: 'existing' } };
        });
        fake.route((u) => u.pathname.endsWith('/icon') ? { body: { ok: true } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });
        // A stale-but-present set still beats no icons.
        expect(result!.iconCount).toBe(1);
        expect(result!.warnings).toEqual([]);
    });

    it('recovers when the create loses a race but the iconset ends up present', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        let probes = 0;
        fake.route((u) => {
            if (!u.pathname.startsWith('/api/iconset/') || u.pathname.endsWith('/icon')) return null;
            probes++;
            return probes === 1 ? { status: 404, body: { message: 'not found' } } : { body: { uid: 'now-there' } };
        });
        fake.route((u) => u.pathname === '/api/iconset' ? { status: 400, body: { message: 'duplicate key' } } : null);
        fake.route((u) => u.pathname.endsWith('/icon') ? { body: { ok: true } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });
        expect(result!.iconCount).toBe(1);
        expect(result!.warnings).toEqual([]);
    });

    it('still reports a create failure that is NOT a duplicate', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        fake.route((u) => (u.pathname.startsWith('/api/iconset/') && !u.pathname.endsWith('/icon'))
            ? { status: 404, body: { message: 'not found' } } : null);
        fake.route((u) => u.pathname === '/api/iconset' ? { status: 400, body: { message: 'must have required property' } } : null);
        fake.install();

        const result = await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });
        expect(result!.iconCount).toBe(0);
        expect(result!.warnings.join(' ')).toMatch(/could not be registered/);
    });
});

describe('a degraded result is retried, not cached forever', () => {
    it('re-attempts registration on the next download once the server stops rejecting it', async () => {
        const { store, symbology } = await freshModules();
        const types = await import('../lib/types.ts');
        const layer = types.newLayer('Roads', LAYER_0, 'public');

        // First download: the server rejects the iconset, so we degrade to colour/shape styling.
        const failing = new FakeArcGIS();
        routeLayer(failing, MIXED_RENDERER);
        failing.route((u) => u.pathname.startsWith('/api/iconset') ? { status: 400, body: { message: 'nope' } } : null);
        failing.install();
        await symbology.ensureLayerSymbology(layer, null);
        expect(store.store.displayConfigs[LAYER_0]?.sym?.t).toBe('uv'); // shapes only
        expect(layer.stylingStatus).toBe('failed');

        // Second download after the cause is fixed. The stored config satisfies
        // hasMeaningfulStyling(), which used to end the attempt here and strand the layer on
        // shape styling permanently.
        const working = new FakeArcGIS();
        routeLayer(working, MIXED_RENDERER);
        working.route((u) => u.pathname.startsWith('/api/iconset') ? { body: { ok: true } } : null);
        working.install();
        await symbology.ensureLayerSymbology(layer, null);

        expect(layer.stylingStatus).toBe('ok');
        // Icons resolved this time: the per-value entry now carries an iconset path.
        expect(JSON.stringify(store.store.displayConfigs[LAYER_0])).toContain('Icons/');
    });

    it('does not re-run for a layer that already resolved cleanly', async () => {
        const { store, symbology } = await freshModules();
        const types = await import('../lib/types.ts');
        const layer = types.newLayer('Roads', LAYER_0, 'public');

        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        fake.route((u) => u.pathname.startsWith('/api/iconset') ? { body: { ok: true } } : null);
        fake.install();

        // `/api/iconset` is a relative path, so count on the raw call list rather than
        // fixtures' countMatching(), which parses each URL with no base.
        const registrations = (): number => fake.calls.filter(c => c.url === '/api/iconset').length;

        await symbology.ensureLayerSymbology(layer, null);
        expect(layer.stylingStatus).toBe('ok');
        const before = registrations();

        await symbology.ensureLayerSymbology(layer, null);
        expect(registrations()).toBe(before);
        expect(store.store.displayConfigs[LAYER_0]).toBeDefined();
    });
});

describe('shared configs get their icons registered on THIS server', () => {
    // A .featurelinkshare carries styling but no image bytes — the icons live on whatever server
    // the sender used. The receiving CloudTAK has to register its own copy, under the same
    // deterministic {uid}/{group} the sender computed, or every path in the shared config 404s.
    it('an imported config still triggers registration on the first download', async () => {
        const { store, symbology } = await freshModules();
        const types = await import('../lib/types.ts');
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        const posted: string[] = [];
        fake.route((u, init) => {
            if (!u.pathname.startsWith('/api/iconset')) return null;
            if (String(init?.method) === 'POST') posted.push(u.pathname);
            return { body: { ok: true } };
        });
        fake.install();

        // What importConfig.applyConfigText writes for a received share: styling, no icons.
        store.store.displayConfigs[LAYER_0] = { v: 3, url: LAYER_0, freq: { iv: 60, u: 's' } };
        const layer = types.newLayer('Roads', LAYER_0, 'public');

        await symbology.ensureLayerSymbology(layer, null);

        expect(posted.some(p => p.endsWith('/icon'))).toBe(true);
        expect(store.store.displayConfigs[LAYER_0]?.sym).toBeDefined();
        // The freq the share carried is preserved — regeneration adds styling, never replaces
        // what the operator imported.
        expect(store.store.displayConfigs[LAYER_0]?.freq?.iv).toBe(60);
    });

    it('regenerates the spritesheet so the icons actually render', async () => {
        const { autoIconset } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake, MIXED_RENDERER);
        const posted: string[] = [];
        fake.route((u, init) => {
            if (!u.pathname.startsWith('/api/iconset')) return null;
            if (String(init?.method) === 'POST') posted.push(u.pathname);
            return { body: { ok: true } };
        });
        fake.install();

        await autoIconset.generateAutoIconset(LAYER_0, { arcgisToken: null });

        // Without this CloudTAK lists the icons but the map keeps drawing the fallback marker.
        expect(posted.some(p => p.endsWith('/regen'))).toBe(true);
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
