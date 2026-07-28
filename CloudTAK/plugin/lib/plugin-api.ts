// Stores the PluginAPI reference so lib/ modules and components can reach it
// without prop-drilling through every component tree.
// Set once in install(); read anywhere.

import type { PluginAPI } from '@tak-ps/cloudtak';

let _api: PluginAPI | null = null;

export function initPluginApi(api: PluginAPI): void { _api = api; }
export function getPluginApi(): PluginAPI | null { return _api; }
