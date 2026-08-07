// Test-only stand-ins for the two CloudTAK-internal modules `lib/cot.ts` dynamically imports.
//
// They exist only inside a CloudTAK checkout (`@tak-ps/node-cot/normalize_geojson` and
// `../../../src/stores/map.ts`, a path relative to the plugin's INSTALL location). Vite resolves
// dynamic-import specifiers statically, so without these the whole module graph fails to load in
// tests — which is itself worth knowing: it is the same coupling that makes `initCot()` fail
// silently at runtime when CloudTAK moves either module (§6).

export const placed: { uid: string; feature: unknown; opts: unknown }[] = [];
export const removed: string[] = [];

export function reset(): void {
    placed.length = 0;
    removed.length = 0;
}

export async function normalize_geojson(feature: unknown): Promise<unknown> {
    const f = feature as { id: string; properties: Record<string, unknown>; geometry: unknown };
    return { id: f.id, type: 'Feature', properties: { ...f.properties }, geometry: f.geometry };
}

export function useMapStore(): unknown {
    return {
        worker: {
            db: {
                add: async (f: unknown, opts: unknown) => {
                    placed.push({ uid: (f as { id: string }).id, feature: f, opts });
                },
                remove: async (uid: string) => { removed.push(uid); },
            },
        },
        currentUser: null,
    };
}
