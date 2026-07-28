// Reads CloudTAK's own session token directly out of browser storage — NOT a supported
// PluginAPI feature (PluginAPI only exposes menu/routes/map/feature/cot/breadcrumb/float/
// bottomBar; see importIngest.ts for why we need the token anyway). CloudTAK stores it via
// @capacitor/preferences, whose web implementation writes to localStorage under a
// 'CapacitorStorage.' prefix by default (capacitor-plugins/preferences/src/web.ts) — CloudTAK
// never calls Preferences.configure() to change that group, so the key is 'CapacitorStorage.token'
// (see CloudTAK's own src/std.ts getRuntimeToken(), which reads Preferences key 'token').
//
// This is reverse-engineered from CloudTAK's current source, not documented/guaranteed —
// a future CloudTAK release could change how/where this is stored without notice. Every caller
// must treat a null return as "can't do this right now", not throw — see importIngest.ts's
// error handling for how a break here surfaces to the user instead of failing silently.

const TOKEN_KEY = 'CapacitorStorage.token';

export function getCloudTakToken(): string | null {
    try {
        return localStorage.getItem(TOKEN_KEY);
    } catch {
        return null;
    }
}
