// A removed layer must stay removed.
//
// Field report: a `survey` layer shared from ATAK ~7 days earlier came back after every delete.
// Clear All Layers, the row's own remove button, nothing worked — within a minute the row was back,
// reading "14 features · never synced". The share package was still sitting in CloudTAK's Import
// Manager, and importIngest's 60-second poll re-applied it every time. Removal wrote the URL to
// store.excludedPrivateUrls and fetchUserLayers honoured it, but importConfig never consulted it.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FakeArcGIS, LAYER_0, layerMeta, pointFeature, WGS84 } from './fixtures.ts';

const realFetch = globalThis.fetch;

const SHARE_CONFIG = JSON.stringify({ v: 3, url: LAYER_0, layer: { name: 'survey' } });

async function freshModules() {
    const rest = await import('../lib/arcgisRest.ts');
    rest.invalidateLayerMeta();
    const store = await import('../lib/store.ts');
    store.resetStore();
    return {
        store,
        importConfig: await import('../lib/importConfig.ts'),
        layerActions: await import('../lib/layerActions.ts'),
    };
}

function routeLayer(fake: FakeArcGIS): void {
    fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query'))
        ? { body: layerMeta({ name: 'survey' }) } : null);
    fake.route((u) => u.pathname.endsWith('/0/query')
        ? { body: { features: [pointFeature(1, -117, 34)], exceededTransferLimit: false, spatialReference: WGS84 } }
        : null);
    fake.route((u) => u.pathname.startsWith('/api/iconset') ? { body: { ok: true } } : null);
}

beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    localStorage.setItem('CapacitorStorage.token', 'cloudtak-session');
});

afterEach(() => { globalThis.fetch = realFetch; });

describe('auto-ingest respects a deliberate removal', () => {
    it('does not re-add a layer the operator deleted', async () => {
        const { store, importConfig, layerActions } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake);
        fake.install();

        // Arrives from the auto-import poll the first time.
        expect((await importConfig.applyConfigText(SHARE_CONFIG, 'auto')).ok).toBe(true);
        expect(store.store.publicLayers).toHaveLength(1);

        await layerActions.removePublicLayer(store.store.publicLayers[0]!);
        expect(store.store.publicLayers).toHaveLength(0);

        // The same package is still in CloudTAK's Import Manager and gets polled again.
        const second = await importConfig.applyConfigText(SHARE_CONFIG, 'auto');
        expect(second.ok).toBe(false);
        expect(second.message).toMatch(/removed on this device/);
        expect(store.store.publicLayers).toHaveLength(0);
    });

    it('survives Clear All Layers too', async () => {
        const { store, importConfig, layerActions } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake);
        fake.install();

        await importConfig.applyConfigText(SHARE_CONFIG, 'auto');
        await layerActions.clearAllLayers();

        await importConfig.applyConfigText(SHARE_CONFIG, 'auto');
        expect(store.store.publicLayers).toHaveLength(0);
    });

    it('leaves no orphan display config behind when it refuses', async () => {
        const { store, importConfig, layerActions } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake);
        fake.install();

        await importConfig.applyConfigText(SHARE_CONFIG, 'auto');
        await layerActions.removePublicLayer(store.store.publicLayers[0]!);
        await importConfig.applyConfigText(SHARE_CONFIG, 'auto');

        expect(store.store.displayConfigs[LAYER_0]).toBeUndefined();
    });

    it('blocks the layer_config payload shape as well', async () => {
        const { store, importConfig, layerActions } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake);
        fake.install();

        const payload = JSON.stringify({ type: 'layer_config', url: LAYER_0, private: true, name: 'survey' });
        await importConfig.applyConfigText(payload, 'auto');
        expect(store.store.privateLayers).toHaveLength(1);

        await layerActions.removePrivateLayer(store.store.privateLayers[0]!);
        await importConfig.applyConfigText(payload, 'auto');
        expect(store.store.privateLayers).toHaveLength(0);
    });
});

describe('a manual import overrides the removal', () => {
    it('re-adds a previously removed layer when the operator asks for it explicitly', async () => {
        const { store, importConfig, layerActions } = await freshModules();
        const fake = new FakeArcGIS();
        routeLayer(fake);
        fake.install();

        await importConfig.applyConfigText(SHARE_CONFIG, 'auto');
        await layerActions.removePublicLayer(store.store.publicLayers[0]!);

        // Pasting the config by hand is an explicit request — being permanently unable to re-import
        // a layer you once removed would be its own bug.
        const manual = await importConfig.applyConfigText(SHARE_CONFIG);
        expect(manual.ok).toBe(true);
        expect(store.store.publicLayers).toHaveLength(1);
        // ...and the exclusion is lifted, so the auto poll stops fighting the operator.
        expect(store.store.excludedPrivateUrls).not.toContain(LAYER_0);
    });
});

describe('a shared config for a PRIVATE layer', () => {
    // Field report: a private "USAR" layer shared from ATAK never appeared on CloudTAK, with
    //   [featurelink] fetchLayerInfo failed for …/USAR_feature_layer_…  ArcGISError: Token Required
    // in the console. The import path fetched layer metadata with no ArcGIS token, so EVERY private
    // layer share failed and reported only a generic "Could not load layer from the config URL".
    it('sends the ArcGIS token when fetching the layer metadata', async () => {
        const { store, importConfig } = await freshModules();
        const auth = await import('../lib/arcgisAuth.ts');
        auth.__setSessionForTest({ username: 'op', accessToken: 'arcgis-token', refreshToken: 'r' });

        const fake = new FakeArcGIS();
        let sawToken = false;
        fake.route((u, init) => {
            if (!u.pathname.endsWith('/0')) return null;
            const headers = (init?.headers ?? {}) as Record<string, string>;
            if (headers['X-Esri-Authorization'] === 'Bearer arcgis-token') sawToken = true;
            // What ArcGIS returns for a private layer requested without credentials.
            if (!sawToken) return { body: { error: { code: 499, message: 'Token Required' } } };
            return { body: layerMeta({ name: 'USAR' }) };
        });
        fake.route((u) => u.pathname.endsWith('/query')
            ? { body: { features: [], exceededTransferLimit: false, spatialReference: WGS84 } } : null);
        fake.route((u) => u.pathname.startsWith('/api/iconset') ? { body: { ok: true } } : null);
        fake.install();

        const result = await importConfig.applyConfigText(SHARE_CONFIG, 'auto');

        expect(sawToken).toBe(true);
        expect(result.ok).toBe(true);
        expect(store.store.publicLayers).toHaveLength(1);
    });

    it('says to sign in when there is no ArcGIS session at all', async () => {
        const { importConfig } = await freshModules();
        const auth = await import('../lib/arcgisAuth.ts');
        auth.signOut();

        const fake = new FakeArcGIS();
        fake.route((u) => u.pathname.endsWith('/0') ? { body: { error: { code: 499, message: 'Token Required' } } } : null);
        fake.install();

        const result = await importConfig.applyConfigText(SHARE_CONFIG, 'auto');
        expect(result.ok).toBe(false);
        // The old message named neither the cause nor the fix.
        expect(result.message).toMatch(/sign in to ArcGIS/i);
    });
});
