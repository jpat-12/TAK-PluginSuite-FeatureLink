// Port of LayerShareHelper.java — builds the same Mode-2 compact DisplayConfig JSON for
// sharing a *public* layer with another user. Transport differs from the ATAK version (which
// sent an ATAK Mission Package to a picked contact): here it's copy-to-clipboard or
// download-as-.json, consumed by the recipient via AddLayerView's paste/upload import.

import type { ArcGISLayer } from './types.ts';

export function buildShareConfigJson(layer: ArcGISLayer): string {
    return JSON.stringify({
        v: 2,
        url: layer.url,
        layer: { name: layer.name, opacity: 1.0, visible: true },
        freq: layer.recurrenceInterval > 0
            ? { iv: layer.recurrenceInterval, u: layer.recurrenceUnit }
            : undefined,
    }, null, 2);
}

export async function copyToClipboard(text: string): Promise<boolean> {
    try { await navigator.clipboard.writeText(text); return true; }
    catch { return false; }
}

export function downloadAsFile(text: string, filename: string): void {
    const blob = new Blob([text], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}
