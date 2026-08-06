// Removing an owned layer returns it to "My ArcGIS Layers" — and it STAYS there.
//
// Field report: "if I remove a layer, then it goes back to my arcgis layers, then if I click
// refresh it disappears". Removal did two contradictory things at once — pushed the layer back into
// the browse list AND recorded it in excludedPrivateUrls — and fetchUserLayers filters excluded
// URLs out, so the row survived exactly until the next refresh.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { FakeArcGIS, LAYER_0, layerMeta } from './fixtures.ts';

const realFetch = globalThis.fetch;

async function freshModules() {
    const rest = await import('../lib/arcgisRest.ts');
    rest.invalidateLayerMeta();
    const store = await import('../lib/store.ts');
    store.resetStore();
    const auth = await import('../lib/arcgisAuth.ts');
    auth.__setSessionForTest({ username: 'op', accessToken: 'tok', refreshToken: 'r' });
    return { store, auth, layerActions: await import('../lib/layerActions.ts'), types: await import('../lib/types.ts') };
}

/** The portal search fetchUserLayers() runs, returning the one owned layer. */
function routePortalSearch(fake: FakeArcGIS): void {
    fake.route((u) => u.pathname.includes('/sharing/rest/search')
        ? {
            body: {
                results: [{
                    id: 'item-1', title: 'survey', type: 'Feature Service',
                    url: LAYER_0.replace(/\/0$/, ''), access: 'private', owner: 'op',
                }],
                nextStart: -1,
            },
        }
        : null);
    fake.route((u) => (u.pathname.endsWith('/0') && !u.pathname.includes('query')) ? { body: layerMeta({ name: 'survey' }) } : null);
    fake.route((u) => u.pathname.endsWith('/query') ? { body: { count: 3 } } : null);
    fake.route((u) => u.pathname.endsWith('/FeatureServer') ? { body: { layers: [{ id: 0, name: 'survey', geometryType: 'esriGeometryPoint' }] } } : null);
}

beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
});

afterEach(() => { globalThis.fetch = realFetch; });

describe('removing an owned on-device layer', () => {
    it('returns it to the browse list without excluding it', async () => {
        const { store, layerActions, types } = await freshModules();
        const layer = { ...types.newLayer('survey', LAYER_0, 'private'), ownedByMe: true };
        store.store.privateLayers.push(layer);

        await layerActions.removePrivateLayer(layer);

        expect(store.store.privateLayers).toHaveLength(0);
        expect(store.store.browseLayers.map(l => l.url)).toContain(LAYER_0);
        // The contradiction: excluded AND in the browse list.
        expect(store.store.excludedPrivateUrls).not.toContain(LAYER_0);
    });

    it('survives a Refresh of My ArcGIS Layers', async () => {
        const { store, layerActions, types } = await freshModules();
        const fake = new FakeArcGIS();
        routePortalSearch(fake);
        fake.install();

        const layer = { ...types.newLayer('survey', LAYER_0, 'private'), ownedByMe: true };
        store.store.privateLayers.push(layer);
        await layerActions.removePrivateLayer(layer);

        await layerActions.fetchUserLayers();

        // Before the fix the row vanished here, with no way to get it back short of Undo.
        expect(store.store.browseLayers.map(l => l.url)).toContain(LAYER_0);
    });

    it('a public owned layer behaves the same way', async () => {
        const { store, layerActions, types } = await freshModules();
        const layer = { ...types.newLayer('survey', LAYER_0, 'public'), ownedByMe: true };
        store.store.publicLayers.push(layer);

        await layerActions.removePublicLayer(layer);

        expect(store.store.browseLayers.map(l => l.url)).toContain(LAYER_0);
        expect(store.store.excludedPrivateUrls).not.toContain(LAYER_0);
    });
});

describe('removing a layer the operator does NOT own', () => {
    it('still records the exclusion, since there is no browse row to return it to', async () => {
        const { store, layerActions, types } = await freshModules();
        const layer = types.newLayer('someone-elses', LAYER_0, 'public'); // ownedByMe defaults false
        store.store.publicLayers.push(layer);

        await layerActions.removePublicLayer(layer);

        expect(store.store.browseLayers).toHaveLength(0);
        // This is what stops the auto-import poll resurrecting it.
        expect(store.store.excludedPrivateUrls).toContain(LAYER_0);
    });

    it('removing a browse row itself still hides it from future refreshes', async () => {
        const { store, layerActions, types } = await freshModules();
        const layer = { ...types.newLayer('survey', LAYER_0, 'private'), ownedByMe: true };
        store.store.browseLayers.push(layer);

        await layerActions.removeBrowseLayer(layer);

        // Hiding an account listing IS a "never show me this again", unlike removing it from the
        // device, so this one keeps the exclusion.
        expect(store.store.excludedPrivateUrls).toContain(LAYER_0);
    });
});
