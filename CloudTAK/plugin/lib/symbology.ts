// THE shared symbology-resolution path (C-07 / FIX-1 / FIX-2 — the owner's field defect).
//
// `generateAutoIconset()` used to have exactly ONE call site in the entire plugin: inside
// `addPublicLayer()`, i.e. the "paste a public URL" button. `downloadLayer()` — which backs the ⬇/↻
// button, the browse-list download AND the recurrence scheduler — never called it, never called
// `extractAutoSymbology`, and never fetched `drawingInfo`. It read styling only from what already
// existed (`store.displayConfigs[layer.url] ?? null`), so on five of the six routes by which a layer
// reaches the device `displayConfig` stayed null and every feature rendered as a hardcoded #3388ff
// marker with no icon.
//
// Both callers now go through ensureLayerSymbology(). `addPublicLayer` no longer contains a
// duplicate of this logic, so there is one code path and it cannot drift back apart.
//
// NOTE FOR WP6 (server-side iconset hot-load, Appendix B N.4): the *extraction* performed here is
// what Step 4 replaces with a `by-uid` probe + `from-arcgis` call against TAK Portal. What is NOT
// throwaway is this module's shape — one resolution point on the download path, a content-based
// (not truthiness) guard, `stylingStatus` reporting, and the retained renderer + `rendererHash`
// that Step 5's drift detection needs. Swap the body of resolveSymbology(); keep the seam.

import { store } from './store.ts';
import { generateAutoIconset } from './autoIconset.ts';
import { fetchLayerMeta, fetchItemRenderer } from './arcgisRest.ts';
import { describeError } from './arcgisHttp.ts';
import type { ArcGISLayer, DisplayConfig } from './types.ts';

/**
 * FIX-2. The old guard was `if (!store.displayConfigs[layer.url])` — a pure truthiness test.
 * A `.featurelinkshare` produced by `layerShare.buildShareConfigJson` carries only `v/url/layer/freq`
 * — no `sym`, no icons, no shape styles — so importing one wrote an EMPTY config object that
 * permanently blocked generation, while `LayerRow`'s badge showed a green "Config" pill over a
 * completely unstyled layer. Content, not truthiness.
 */
export function hasMeaningfulStyling(config: DisplayConfig | null | undefined): boolean {
    if (!config) return false;
    if (config.sym) return true;
    if (config.singleShapeStyle) return true;
    if (config.shapeStyleByValue && Object.keys(config.shapeStyleByValue).length > 0) return true;
    return false;
}

/** Stable hash of a renderer, for the hot-load design's drift detection (Appendix B N.2). */
export async function rendererHash(renderer: unknown): Promise<string> {
    if (renderer === null || renderer === undefined) return '';
    const canonical = JSON.stringify(renderer, Object.keys(renderer as object).sort());
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(canonical));
    return Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
}

/** Keeps everything an imported config carried (freq, cotMapping, labels, popup) and adds the derived styling. */
function mergeStyling(existing: DisplayConfig | undefined, derived: DisplayConfig): DisplayConfig {
    if (!existing) return derived;
    return {
        ...existing,
        sym: existing.sym ?? derived.sym,
        shapeField: existing.shapeField ?? derived.shapeField,
        singleShapeStyle: existing.singleShapeStyle ?? derived.singleShapeStyle,
        shapeStyleByValue: existing.shapeStyleByValue ?? derived.shapeStyleByValue,
        rendererHash: derived.rendererHash ?? existing.rendererHash,
    };
}

/**
 * Resolves and stores this layer's symbology if it does not already have meaningful styling.
 * Never throws: a layer with a default-symbology renderer is not a failure, and a real failure
 * (network, no CloudTAK session) must not block the download. The outcome is recorded on the layer
 * as `stylingStatus`/`stylingMessage` so the UI can say WHY icons are missing (FIX-7) instead of
 * the previous console-only `console.warn`.
 */
export async function ensureLayerSymbology(layer: ArcGISLayer, token: string | null): Promise<void> {
    const existing = store.displayConfigs[layer.url];
    if (hasMeaningfulStyling(existing)) {
        layer.stylingStatus = 'ok';
        layer.stylingMessage = '';
        return;
    }

    try {
        // A config imported from TAK Portal can carry the renderer/uid/group the exporter used;
        // honoring them is what keeps {uid}/{group}/{file} identical across platforms (FIX-4).
        const meta = await fetchLayerMeta(layer.url, token);

        // Styling applied on the item's Visualization tab in ArcGIS Online is saved as an
        // item-level override and never touches the service's own drawingInfo, so a layer styled
        // by unique value still reports a single-symbol `simple` renderer here. The override is
        // what the operator actually sees in ArcGIS, so it wins. Verified against a live org on
        // ATAK: service said simple/1 symbol, item said uniqueValue/10.
        const itemRenderer = await fetchItemRenderer(layer.itemId, layer.layerId, token);
        if (itemRenderer) {
            console.debug('[featurelink] using item-level renderer override from item', layer.itemId,
                '(service renderer was', meta.renderer?.type ?? 'absent', ')');
        }
        const effectiveRenderer = itemRenderer ?? meta.renderer;

        const result = await generateAutoIconset(layer.url, {
            arcgisToken: token,
            layerName: meta.name,
            rendererOverride: (existing?.rendererOverride ?? effectiveRenderer) as never,
            uidOverride: existing?.iconsetUid ?? null,
            groupOverride: existing?.iconsetGroup ?? null,
        });

        if (!result) {
            layer.stylingStatus = 'none';
            layer.stylingMessage = 'This layer uses default ArcGIS symbology — nothing to extract.';
            return;
        }

        const derived: DisplayConfig = {
            ...result.displayConfig,
            rendererHash: await rendererHash(effectiveRenderer),
        };
        store.displayConfigs[layer.url] = mergeStyling(existing, derived);
        layer.stylingStatus = result.warnings.length ? 'failed' : 'ok';
        layer.stylingMessage = result.warnings.length
            ? result.warnings.join('; ')
            : (result.iconCount > 0
                ? `${result.iconCount} custom icon${result.iconCount === 1 ? '' : 's'}`
                : 'auto-styled');
    } catch (e) {
        layer.stylingStatus = 'failed';
        layer.stylingMessage = describeError(e);
        console.warn('[featurelink] auto-symbology resolution failed for', layer.name, e);
    }
}
