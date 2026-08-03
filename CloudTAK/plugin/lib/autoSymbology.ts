// ArcGIS renderer -> shape/marker style extractor. TypeScript port of the ATAK plugin's
// arcgis/AutoSymbology.java — sibling to autoIconset.ts's esriPMS (picture-marker) extraction,
// this pulls esriSLS (line stroke), esriSFS (polygon fill + outline) and esriSMS (point marker
// shape/color) symbol JSON into plain style structs, keyed by renderer field value the same way
// autoIconset.ts's pathByValue is. No network I/O, no side effects — pure JSON parsing over an
// already-fetched renderer object.

export interface StrokeStyle {
    color: string;   // '#rrggbb'
    opacity: number; // 0-1, from the esri color's alpha byte
    widthPx: number;
    dash: 'solid' | 'dash' | 'dot';
}

export interface FillStyle {
    color: string;
    opacity: number;
    style: 'solid' | 'none';
    /** The fill symbol's own outline (esriSFS.outline is itself an esriSLS) — may be null. */
    outline: StrokeStyle | null;
}

export interface MarkerStyle {
    color: string;
    opacity: number;
    shape: 'circle' | 'square' | 'diamond' | 'triangle' | 'cross' | 'x';
    sizePx: number;
}

export interface AutoSymbologyResult {
    /** Driving field for the by-value maps below; empty for a single-symbol renderer. */
    field: string;
    singleStroke: StrokeStyle | null;
    singleFill: FillStyle | null;
    singleMarker: MarkerStyle | null;
    /** uniqueValue renderer: field VALUE -> style, keyed the same way autoIconset.ts's
     * pathByValue is, so a feature resolves its style on the same attribute value. */
    strokeByValue: Map<string, StrokeStyle>;
    fillByValue: Map<string, FillStyle>;
    markerByValue: Map<string, MarkerStyle>;
}

export function isAutoSymbologyEmpty(r: AutoSymbologyResult): boolean {
    return !r.singleStroke && !r.singleFill && !r.singleMarker
        && r.strokeByValue.size === 0 && r.fillByValue.size === 0 && r.markerByValue.size === 0;
}

interface EsriSymbolJson {
    type?: string;
    style?: string;
    width?: number;
    size?: number;
    color?: number[];
    outline?: EsriSymbolJson;
}

export interface EsriRendererJson {
    type?: string;
    field?: string;
    field1?: string;
    symbol?: EsriSymbolJson;
    uniqueValueInfos?: { value?: string; symbol?: EsriSymbolJson }[];
    classBreakInfos?: { symbol?: EsriSymbolJson }[];
    defaultSymbol?: EsriSymbolJson;
}

// ArcGIS symbol colors are [r,g,b,a] (alpha last, 0-255). Unlike displayConfig.ts's own
// esriColorToHex helper (which drops alpha), this preserves it as a separate opacity — translucent
// polygon fills are common ArcGIS styling and worth carrying over.
function esriColor(arr: number[] | undefined, fallbackHex: string): { color: string; opacity: number } {
    if (!arr || arr.length < 3) return { color: fallbackHex, opacity: 1 };
    const [r, g, b] = arr;
    const a = arr.length >= 4 ? arr[3] : 255;
    const clamp = (n: number) => Math.max(0, Math.min(255, Math.round(n)));
    const hex = `#${[r, g, b].map(n => clamp(n).toString(16).padStart(2, '0')).join('')}`;
    return { color: hex, opacity: clamp(a) / 255 };
}

function dashFromEsriStyle(style: string | undefined): 'solid' | 'dash' | 'dot' {
    switch (style) {
        case 'esriSLSDash':
        case 'esriSLSDashDot':
        case 'esriSLSDashDotDot':
            return 'dash';
        case 'esriSLSDot':
            return 'dot';
        default:
            return 'solid';
    }
}

function markerShapeFromEsri(style: string | undefined): MarkerStyle['shape'] {
    switch (style) {
        case 'esriSMSSquare':   return 'square';
        case 'esriSMSDiamond':  return 'diamond';
        case 'esriSMSTriangle': return 'triangle';
        case 'esriSMSCross':    return 'cross';
        case 'esriSMSX':        return 'x';
        default:                return 'circle';
    }
}

function strokeFrom(symbol: EsriSymbolJson | undefined): StrokeStyle | null {
    if (!symbol || symbol.type !== 'esriSLS') return null;
    const { color, opacity } = esriColor(symbol.color, '#0000ff');
    return { color, opacity, widthPx: symbol.width ?? 1, dash: dashFromEsriStyle(symbol.style) };
}

function fillFrom(symbol: EsriSymbolJson | undefined): FillStyle | null {
    if (!symbol || symbol.type !== 'esriSFS') return null;
    const { color, opacity } = esriColor(symbol.color, '#ffff00');
    const style = symbol.style === 'esriSFSNull' ? 'none' : 'solid';
    return { color, opacity, style, outline: strokeFrom(symbol.outline) };
}

function markerFrom(symbol: EsriSymbolJson | undefined): MarkerStyle | null {
    if (!symbol || symbol.type !== 'esriSMS') return null;
    const { color, opacity } = esriColor(symbol.color, '#ff0000');
    return { color, opacity, shape: markerShapeFromEsri(symbol.style), sizePx: symbol.size ?? 8 };
}

// Extracts esriSMS/esriSLS/esriSFS styling from a renderer, mirroring the same
// simple/uniqueValue/classBreaks/defaultSymbol traversal autoIconset.ts's extractPmsEntries uses.
// Class-breaks entries can't be resolved per-feature by value (numeric-range matching isn't
// supported here, same caveat as the icon extractor) — only a fallback symbol (defaultSymbol, or
// the first break if there's no defaultSymbol) is extracted as a single style for that renderer
// type. Which of stroke/fill/marker actually gets populated per entry is driven entirely by each
// symbol's own "type" (esriSLS/esriSFS/esriSMS) rather than a geometry-type hint, so mixed-symbol
// renderers resolve correctly without the caller needing to know the layer's geometry type ahead
// of time.
export function extractAutoSymbology(renderer: EsriRendererJson | undefined, fieldOverride?: string): AutoSymbologyResult {
    const strokeByValue = new Map<string, StrokeStyle>();
    const fillByValue = new Map<string, FillStyle>();
    const markerByValue = new Map<string, MarkerStyle>();
    let singleStroke: StrokeStyle | null = null;
    let singleFill: FillStyle | null = null;
    let singleMarker: MarkerStyle | null = null;
    let field = '';

    if (renderer) {
        const type = renderer.type || 'simple';

        if (type === 'uniqueValue' || type === 'uniqueValueRenderer') {
            field = renderer.field1 || renderer.field || '';
            for (const info of renderer.uniqueValueInfos ?? []) {
                const value = info.value ?? '';
                const symbol = info.symbol;
                if (!value || !symbol) continue;
                const s = strokeFrom(symbol);
                const f = fillFrom(symbol);
                const m = markerFrom(symbol);
                if (s) strokeByValue.set(value, s);
                if (f) fillByValue.set(value, f);
                if (m) markerByValue.set(value, m);
            }
            if (renderer.defaultSymbol) {
                singleStroke = strokeFrom(renderer.defaultSymbol);
                singleFill = fillFrom(renderer.defaultSymbol);
                singleMarker = markerFrom(renderer.defaultSymbol);
            }
        } else if (type === 'classBreaks' || type === 'classBreaksRenderer') {
            field = renderer.field || '';
            const fallback = renderer.defaultSymbol ?? renderer.classBreakInfos?.[0]?.symbol;
            if (fallback) {
                singleStroke = strokeFrom(fallback);
                singleFill = fillFrom(fallback);
                singleMarker = markerFrom(fallback);
            }
        } else {
            // simple / bare symbol renderer — single style, no field
            singleStroke = strokeFrom(renderer.symbol);
            singleFill = fillFrom(renderer.symbol);
            singleMarker = markerFrom(renderer.symbol);
        }
    }

    if (fieldOverride) field = fieldOverride;

    return { field, singleStroke, singleFill, singleMarker, strokeByValue, fillByValue, markerByValue };
}
