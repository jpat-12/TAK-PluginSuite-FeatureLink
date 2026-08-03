// Local type stub for CloudTAK's plugin host package.
//
// The plugin is compiled INSIDE CloudTAK, which supplies the real `@tak-ps/cloudtak` types.
// In isolation there is no such package on npm and the previous `file:../../../../../CloudTAK/api/web`
// devDependency pointed five levels *outside* this repository — it resolved only on the original
// author's machine, left `npm ls` in an ELSPROBLEMS state, and made `npm run check` permanently red
// with 2 x TS2307 (Appendix B §0.1). This stub declares exactly the surface the plugin uses, so
// `vue-tsc --noEmit` exits 0 on a clean clone and a *real* type regression is now distinguishable
// from expected noise.
//
// If a future CloudTAK release widens the contract, widen this file in the same commit. Anything
// declared here that CloudTAK does not actually provide is a runtime bug this stub will hide, so
// keep it minimal and keep every member traceable to a call site in this plugin.

declare module '@tak-ps/cloudtak' {
    import type { Component } from 'vue';

    export interface MenuItemConfig {
        key: string;
        label: string;
        route: string;
        tooltip?: string;
        description?: string;
        icon?: Component;
    }

    export interface RouteConfig {
        path: string;
        name: string;
        component: Component;
    }

    export interface PluginAPI {
        menu: {
            add(item: MenuItemConfig): void;
            remove(key: string): void;
        };
        routes: {
            add(route: RouteConfig, parent: string): void;
        };
    }

    export interface PluginInstance {
        enable(): Promise<void>;
        disable(): Promise<void>;
    }
}
