// FeatureLink CloudTAK plugin — entry point.
//
// When installed, this file lands at: CloudTAK/api/web/plugins/featurelink/index.ts
// CloudTAK discovers it at build time via: plugins/*/index.ts
//
// Lifecycle:
//   install()  — once at startup; register routes here.
//   enable()   — every time the map becomes ready; add the menu item.
//   disable()  — every time the map is NOT ready (including before first enable()); remove
//                the menu item only. NEVER remove routes here.
//
// Ported from the ATAK plugin's plugin/FeatureLinkLifecycle.java + FeatureLinkMapComponent.java
// (toolbar tool + DropDownMapComponent registration), following the same install/enable/disable
// shape as CloudTAK-Plugin_StatusBoard_CAP/plugin/index.ts.

import type { App } from 'vue';
import { markRaw } from 'vue';
import type { PluginAPI, PluginInstance, MenuItemConfig } from '@tak-ps/cloudtak';
import FeatureLinkMain from './components/FeatureLinkMain.vue';
import PluginIcon from './components/PluginIcon.vue';
import { initPluginApi } from './lib/plugin-api.ts';
import { initCot } from './lib/cot.ts';
import { startRecurrenceScheduler, watchPliAutoSend } from './lib/scheduler.ts';
import { startImportIngestScheduler } from './lib/importIngest.ts';
import { isAuthenticated } from './lib/arcgisAuth.ts';
import { fetchUserLayers } from './lib/layerActions.ts';

const MENU_KEY   = 'plugin-featurelink';
const ROUTE_NAME = 'home-menu-featurelink';

export default class FeatureLinkPlugin implements PluginInstance {
    api: PluginAPI;

    constructor(api: PluginAPI) {
        this.api = api;
    }

    static async install(app: App, api: PluginAPI): Promise<FeatureLinkPlugin> {
        void app;

        initPluginApi(api);
        await initCot();

        // Register the panel route once.
        api.routes.add(
            { path: 'featurelink', name: ROUTE_NAME, component: markRaw(FeatureLinkMain) },
            'home-menu',
        );

        // Background loops: layer auto-refresh always runs; PLI auto-send is gated on the
        // persisted toggle (lib/store.ts's pliAutoSend), same as the Java plugin's behavior.
        startRecurrenceScheduler();
        watchPliAutoSend();
        startImportIngestScheduler();

        // If a previous session left us signed in, refresh the owned-layer list on startup.
        if (isAuthenticated()) void fetchUserLayers();

        return new FeatureLinkPlugin(api);
    }

    async enable(): Promise<void> {
        this.api.menu.add({
            key:         MENU_KEY,
            label:       'FeatureLink',
            route:       ROUTE_NAME,
            tooltip:     'ArcGIS Feature Service bridge',
            description: 'Sign in to ArcGIS, browse/download feature layers, stream PLI, and send map items to a layer',
            icon:        markRaw(PluginIcon) as unknown as MenuItemConfig['icon'],
        } as MenuItemConfig);
    }

    async disable(): Promise<void> {
        try { this.api.menu.remove(MENU_KEY); } catch { /* map not loaded yet — ignore */ }
    }
}
