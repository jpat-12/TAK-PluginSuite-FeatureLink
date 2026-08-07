// Port of the ATAK plugin's DisplayConfig.java — parses the styling-rule JSON produced by
// TAK Portal's configurator (Mode 1/2 compact schema, or Mode 3 full Esri-renderer schema)
// and resolves per-feature color / iconset path / label / remarks from it. QR/deep-link
// transport is dropped (see plan); the JSON schema itself is unchanged so existing configs
// still paste/upload correctly.

import type {
    DisplayConfig, SymConfig, SymValueEntry, SymOp, CotMapping, CotFieldMapping, ShapeStyle,
} from './types.ts';
import type { AutoSymbologyResult, StrokeStyle, FillStyle } from './autoSymbology.ts';

// ── Parsing ──────────────────────────────────────────────────────────────────

function asStringArray(v: string | string[] | undefined): string[] {
    if (!v) return [];
    return Array.isArray(v) ? v : [v];
}

export function cotMappingOf(cm: CotMapping | undefined): CotFieldMapping | null {
    if (!cm) return null;
    return {
        uidFields: asStringArray(cm.uf),
        typeFields: asStringArray(cm.tf),
        callsignFields: asStringArray(cm.cf),
        remarksFields: asStringArray(cm.rf),
    };
}

interface EsriSymbol {
    type?: string; style?: string; color?: number[]; outline?: { color?: number[] };
    url?: string; // esriPMS picture marker
}
interface EsriRenderer {
    type?: string;
    symbol?: EsriSymbol;
    field1?: string;
    uniqueValueInfos?: { value?: string; symbol?: EsriSymbol }[];
    classBreakInfos?: { classMinValue?: number; classMaxValue?: number; symbol?: EsriSymbol }[];
    defaultSymbol?: EsriSymbol;
}
interface Mode3Json {
    featureLayerUrl?: string;
    layer?: { name?: string; opacity?: number; visible?: boolean };
    symbology?: EsriRenderer;
    labels?: { enabled?: boolean; field?: string; fontSize?: number; color?: string; bold?: boolean; italic?: boolean };
    popup?: { enabled?: boolean; title?: string; fields?: { f: string; a?: string }[] };
    cotMapping?: { uidFields?: string[]; typeFields?: string[]; callsignFields?: string[]; remarksFields?: string[] };
    updateFrequency?: { enabled?: boolean; intervalValue?: number; intervalUnit?: 's' | 'min' | 'hr' };
}

function clampByte(n: unknown): number {
    // `n` comes straight from JSON with no integer check: 12.7 used to render as 'c.b333…',
    // producing a malformed hex string. autoSymbology.ts:74 already rounds — these two files
    // disagreed about the same Esri colour (§10.2).
    const v = typeof n === 'number' && Number.isFinite(n) ? n : 0;
    return Math.max(0, Math.min(255, Math.round(v)));
}

function esriColorToHex(c: number[] | undefined): string | undefined {
    if (!c || c.length < 3) return undefined;
    return `#${[c[0], c[1], c[2]].map(n => clampByte(n).toString(16).padStart(2, '0')).join('')}`;
}

function shapeStyleFromEsri(style: string | undefined): string {
    switch (style) {
        case 'esriSMSSquare':   return 'square';
        case 'esriSMSDiamond':  return 'diamond';
        case 'esriSMSTriangle': return 'triangle';
        case 'esriSMSCross':    return 'cross';
        case 'esriSMSX':        return 'x';
        default:                return 'circle';
    }
}

function symFromEsriSymbol(sym: EsriSymbol | undefined): Partial<SymValueEntry> {
    if (!sym) return {};
    if (sym.type === 'esriPMS' && sym.url) return { m: 'icon', up: sym.url };
    return { c: esriColorToHex(sym.color), sh: shapeStyleFromEsri(sym.style) };
}

function fromMode3(raw: Mode3Json): DisplayConfig {
    const r = raw.symbology;
    let sym: SymConfig | undefined;
    if (r) {
        const base = symFromEsriSymbol(r.symbol ?? r.defaultSymbol);
        if (r.type === 'uniqueValue' || r.type === 'uniqueValueInfos') {
            sym = {
                t: 'uv', f: r.field1, c: base.c, sh: base.sh,
                uv: (r.uniqueValueInfos ?? []).map(e => ({ v: e.value ?? '', ...symFromEsriSymbol(e.symbol) })),
            };
        } else if (r.type === 'classBreaks' || r.type === 'classBreakInfos') {
            sym = {
                t: 'cb', f: r.field1, c: base.c, sh: base.sh,
                cb: (r.classBreakInfos ?? []).map(e => ({
                    mn: e.classMinValue ?? -Infinity, mx: e.classMaxValue ?? Infinity,
                    c: esriColorToHex((e.symbol ?? {}).color) ?? base.c ?? '#3388ff',
                })),
            };
        } else if (base.m === 'icon') {
            sym = { t: 'ic', up: base.up };
        } else {
            sym = { t: 's', c: base.c, sh: base.sh };
        }
    }

    return {
        v: 3,
        url: raw.featureLayerUrl,
        layer: raw.layer,
        sym,
        lbl: raw.labels?.enabled && raw.labels.field
            ? { f: raw.labels.field, sz: raw.labels.fontSize, c: raw.labels.color, b: raw.labels.bold, i: raw.labels.italic }
            : undefined,
        popup: raw.popup?.enabled
            ? { t: raw.popup.title, flds: (raw.popup.fields ?? []).map(f => (f.a ? [f.f, f.a] : f.f)) }
            : undefined,
        cm: raw.cotMapping
            ? { uf: raw.cotMapping.uidFields, tf: raw.cotMapping.typeFields, cf: raw.cotMapping.callsignFields, rf: raw.cotMapping.remarksFields }
            : undefined,
        freq: raw.updateFrequency?.enabled
            ? { iv: raw.updateFrequency.intervalValue ?? 0, u: raw.updateFrequency.intervalUnit ?? 's' }
            : undefined,
    };
}

// Parses a pasted/uploaded config JSON string or object. Returns null if it doesn't look
// like a DisplayConfig at all (caller falls back to the generic OperationalPayload dispatch).
const DANGEROUS_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

/**
 * Rejects a parsed object that carries prototype-pollution keys anywhere in its tree.
 * `store.displayConfigs[url] = config` writes an ATTACKER-CONTROLLED string as a key on a plain
 * object, so a share config with `"url": "__proto__"` mutated `Object.prototype` for the entire
 * CloudTAK tab (§7.3). The store now also uses a null-prototype map, but rejecting the input is
 * the cheaper, earlier guard.
 */
function hasDangerousKeys(value: unknown, depth = 0): boolean {
    if (depth > 32 || typeof value !== 'object' || value === null) return false;
    if (Array.isArray(value)) return value.some(v => hasDangerousKeys(v, depth + 1));
    for (const [k, v] of Object.entries(value)) {
        if (DANGEROUS_KEYS.has(k)) return true;
        if (hasDangerousKeys(v, depth + 1)) return true;
    }
    return false;
}

/**
 * Minimal structural validation. `parseDisplayConfig` used to `return obj as unknown as DisplayConfig`
 * — a raw attacker object with an asserted type — and every downstream consumer then indexed into
 * it assuming the declared shape: `sym.uv?.find(...)` on a non-array throws, `popup.flds` as a
 * string iterates characters (§8.1).
 */
function looksLikeDisplayConfig(obj: Record<string, unknown>): boolean {
    if (obj.sym !== undefined && (typeof obj.sym !== 'object' || obj.sym === null || Array.isArray(obj.sym))) return false;
    const sym = obj.sym as Record<string, unknown> | undefined;
    if (sym) {
        for (const arrayKey of ['uv', 'vs', 'rules', 'r', 'cb']) {
            const v = sym[arrayKey];
            if (v !== undefined && !Array.isArray(v)) return false;
        }
    }
    const popup = obj.popup as Record<string, unknown> | undefined;
    if (popup !== undefined) {
        if (typeof popup !== 'object' || popup === null || Array.isArray(popup)) return false;
        if (popup.flds !== undefined && !Array.isArray(popup.flds)) return false;
    }
    if (obj.url !== undefined && typeof obj.url !== 'string') return false;
    if (obj.shapeStyleByValue !== undefined
        && (typeof obj.shapeStyleByValue !== 'object' || obj.shapeStyleByValue === null || Array.isArray(obj.shapeStyleByValue))) {
        return false;
    }
    return true;
}

export function parseDisplayConfig(input: unknown): DisplayConfig | null {
    let raw: unknown = input;
    if (typeof input === 'string') {
        try { raw = JSON.parse(input); } catch { return null; }
    }
    if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null;
    const obj = raw as Record<string, unknown>;
    if (hasDangerousKeys(obj)) return null;

    // A discriminating `type` wins over every structural guess. `{v:1,type:'pli_endpoint',url}` has
    // both `v` and `url`, so the old ordering matched it as a DisplayConfig and added the shared PLI
    // endpoint as a *layer* — the entire "Share PLI Endpoint" round trip was broken (§4.2).
    if (typeof obj.type === 'string') return null;

    if ('symbology' in obj || 'featureLayerUrl' in obj) {
        return fromMode3(obj as Mode3Json);
    }
    if ('sym' in obj || ('v' in obj && ('url' in obj || 'layer' in obj))) {
        if (!looksLikeDisplayConfig(obj)) return null;
        return obj as unknown as DisplayConfig;
    }
    return null;
}

// ── Resolution (per-feature, given the downloaded attribute map) ──────────────

function blendOpacity(hex: string, opacity: number | undefined): string {
    if (opacity === undefined || opacity >= 1) return hex;
    const alpha = Math.round(Math.max(0, Math.min(1, opacity)) * 255);
    return hex + alpha.toString(16).padStart(2, '0');
}

function matchesRule(fieldVal: string, op: SymOp, ruleVal: string): boolean {
    switch (op) {
        case '=':            return fieldVal === ruleVal;
        case '!=':            return fieldVal !== ruleVal;
        case 'contains':      return fieldVal.includes(ruleVal);
        case 'startswith':    return fieldVal.startsWith(ruleVal);
        case 'isempty':       return fieldVal === '';
        case 'isnotempty':    return fieldVal !== '';
        case '>': case '<': case '>=': case '<=': {
            const a = parseFloat(fieldVal), b = parseFloat(ruleVal);
            if (Number.isNaN(a) || Number.isNaN(b)) return false;
            if (op === '>') return a > b;
            if (op === '<') return a < b;
            if (op === '>=') return a >= b;
            return a <= b;
        }
        default: return false;
    }
}

/** `parseFloat('12abc')` returns 12, silently accepting garbage as a class-break value (§10.2). */
function strictNumber(v: string | undefined): number {
    if (v === undefined || v.trim() === '') return NaN;
    return Number(v);
}

const DEFAULT_COLOR = '#3388ff';

export function resolveColor(config: DisplayConfig, attrs: Record<string, string>): string {
    const sym = config.sym;
    if (!sym) return DEFAULT_COLOR;
    const base = blendOpacity(sym.c ?? DEFAULT_COLOR, sym.op);

    switch (sym.t) {
        case 's': case 'ic':
            return base;
        case 'uv': {
            const val = sym.f ? attrs[sym.f] ?? '' : '';
            const hit = sym.uv?.find(e => e.v === val);
            return hit?.c ? blendOpacity(hit.c, sym.op) : base;
        }
        case 'rb': {
            const rules = sym.rules ?? sym.r ?? [];
            for (const rule of rules) {
                const val = attrs[rule.f] ?? '';
                if (matchesRule(val, rule.o, rule.v)) return rule.c ? blendOpacity(rule.c, sym.op) : base;
            }
            return sym.dc ? blendOpacity(sym.dc, sym.op) : base;
        }
        case 'cb': {
            const val = sym.f ? strictNumber(attrs[sym.f]) : NaN;
            if (!Number.isNaN(val)) {
                const breaks = sym.cb ?? [];
                const last = breaks.length - 1;
                // The final break is INCLUSIVE of its maximum: `val >= mn && val < mx` let a value
                // exactly equal to the last break's max fall through to the base colour (§10.2).
                const bucket = breaks.find((b, i) => val >= b.mn && (i === last ? val <= b.mx : val < b.mx));
                if (bucket) return blendOpacity(bucket.c, sym.op);
            }
            return base;
        }
        case 'adv': {
            const val = sym.f ? attrs[sym.f] ?? '' : '';
            const valHit = sym.vs?.find(e => e.v === val);
            if (valHit?.c) return blendOpacity(valHit.c, sym.op);
            const rules = sym.rules ?? sym.r ?? [];
            for (const rule of rules) {
                const rv = attrs[rule.f] ?? '';
                if (matchesRule(rv, rule.o, rule.v) && rule.c) return blendOpacity(rule.c, sym.op);
            }
            return base;
        }
        default:
            return base;
    }
}

// Prefers server-resolved `up` (exact "<iconset-uid>/<group>/<file>") over the legacy
// `is + "/" + ic` concat, which never resolved to a valid iconset path on its own.
function pickIconPath(entry: { up?: string | null; is?: string; ic?: string } | undefined): string | null {
    if (!entry) return null;
    if (entry.up) return entry.up;
    if (entry.is && entry.ic) return `${entry.is}/${entry.ic}`;
    return null;
}

export function resolveIconsetPath(config: DisplayConfig, attrs: Record<string, string>): string | null {
    const sym = config.sym;
    if (!sym) return null;

    if (sym.t === 'ic') return pickIconPath(sym);

    if (sym.t === 'adv') {
        const val = sym.f ? attrs[sym.f] ?? '' : '';
        const valHit = sym.vs?.find(e => e.v === val && e.m === 'icon');
        if (valHit) return pickIconPath(valHit);
        const rules = sym.rules ?? sym.r ?? [];
        for (const rule of rules) {
            const rv = attrs[rule.f] ?? '';
            if (rule.m === 'icon' && matchesRule(rv, rule.o, rule.v)) return pickIconPath(rule);
        }
    }
    return null;
}

/** Max characters of an attribute value allowed into a callsign — a 10 KB attribute became a 10 KB callsign (§10.2). */
const MAX_LABEL_CHARS = 120;
const MAX_REMARKS_CHARS = 4000;

/**
 * Strips control characters and the three XML metacharacters before an attribute value reaches
 * `callsign`/`remarks`. CoT XML serialization is owned by node-cot's normalize_geojson and this
 * plugin cannot verify its escaping, so attribute-driven content is sanitized at the source
 * rather than trusting a downstream contract it does not control (§10.2).
 */
function sanitizeAttrText(v: string, max: number): string {
    // Character-by-character rather than a control-character regex: the class is easier to
    // read, and it keeps newlines/tabs (which remarks legitimately use) while dropping every
    // other C0 control and DEL.
    let cleaned = '';
    for (const ch of v) {
        const code = ch.codePointAt(0) ?? 0;
        if (code === 0x7f) continue;
        if (code < 0x20 && code !== 10 && code !== 9) continue; // keep LF and TAB
        cleaned += (ch === '<' || ch === '>' || ch === '&') ? ' ' : ch;
    }
    return cleaned.length > max ? `${cleaned.slice(0, max)}…` : cleaned;
}

export function resolveLabel(config: DisplayConfig, attrs: Record<string, string>, fallback: string): string {
    const field = config.lbl?.f;
    if (!field) return fallback;
    const v = attrs[field];
    return v && v !== '' ? sanitizeAttrText(v, MAX_LABEL_CHARS) : fallback;
}

export function buildRemarks(config: DisplayConfig, attrs: Record<string, string>): string {
    const flds = config.popup?.flds;
    if (!Array.isArray(flds) || flds.length === 0) return '';
    const lines: string[] = [];
    for (const f of flds) {
        const [field, alias] = Array.isArray(f) ? [f[0] ?? '', f[1] ?? f[0] ?? ''] : [f, f];
        if (typeof field !== 'string' || field === '') continue;
        const v = attrs[field];
        if (v && v !== '') lines.push(`${sanitizeAttrText(String(alias), 64)}: ${sanitizeAttrText(v, 512)}`);
    }
    // Each line is already sanitized; only the total length is capped here (re-sanitizing would
    // strip the separators this just added).
    const joined = lines.join('\n');
    return joined.length > MAX_REMARKS_CHARS ? `${joined.slice(0, MAX_REMARKS_CHARS)}…` : joined;
}

// ── Shape styling (polyline/polygon) — ported from DisplayConfig.java's ShapeStyle /
// combineShapeStyle / resolveShapeStyle. Only ever populated by the on-device auto-symbology
// path (see buildShapeStyleFields, called from autoIconset.ts's generateAutoIconset) — never
// round-tripped through the QR/TAK-Portal "sym" schema. ──

// Merges a renderer entry's stroke (esriSLS) and fill (esriSFS, whose own outline is also an
// esriSLS) into one ShapeStyle — a polygon's outline can come from either the fill symbol's
// outline or a separate line symbol depending on how the renderer is authored, so the fill's
// outline is preferred when there's no standalone stroke. Returns null if neither a stroke nor a
// fill was extracted (e.g. an esriPMS/esriSMS-only renderer).
function combineShapeStyle(stroke: StrokeStyle | null, fill: FillStyle | null): ShapeStyle | null {
    if (!stroke && !fill) return null;
    const outline = fill?.outline ?? null;
    return {
        strokeColor:   stroke?.color   ?? outline?.color   ?? '#3388ff',
        strokeOpacity: stroke?.opacity ?? outline?.opacity ?? 1,
        strokeWidthPx: stroke?.widthPx ?? outline?.widthPx ?? 2,
        strokeDash:    stroke?.dash    ?? outline?.dash    ?? 'solid',
        fillColor:   fill?.color ?? '#000000',
        fillOpacity: fill && fill.style !== 'none' ? fill.opacity : 0,
        fillStyle:   fill?.style ?? 'none',
    };
}

// Builds the shapeField/singleShapeStyle/shapeStyleByValue fields of a DisplayConfig from an
// AutoSymbologyResult — the TS equivalent of DisplayConfig.forAutoIcons()'s shape-folding logic.
export function buildShapeStyleFields(
    shapeResult: AutoSymbologyResult | null,
): Pick<DisplayConfig, 'shapeField' | 'singleShapeStyle' | 'shapeStyleByValue'> {
    if (!shapeResult) return {};
    const singleShapeStyle = combineShapeStyle(shapeResult.singleStroke, shapeResult.singleFill) ?? undefined;

    const shapeStyleByValue: Record<string, ShapeStyle> = {};
    const values = new Set<string>([...shapeResult.strokeByValue.keys(), ...shapeResult.fillByValue.keys()]);
    for (const v of values) {
        const s = combineShapeStyle(shapeResult.strokeByValue.get(v) ?? null, shapeResult.fillByValue.get(v) ?? null);
        if (s) shapeStyleByValue[v] = s;
    }

    return { shapeField: shapeResult.field, singleShapeStyle, shapeStyleByValue };
}

// Returns the stroke/fill style for a feature given its raw attribute map, or null if this
// config has no shape styling (e.g. a point layer, or one with no esriSLS/esriSFS symbology) —
// callers should fall back to a sensible default (CloudTAK's default blue stroke, no fill).
export function resolveShapeStyle(config: DisplayConfig, attrs: Record<string, string>): ShapeStyle | null {
    // C-24 — PRECEDENCE. This used to check `singleShapeStyle` FIRST and return it, so
    // `shapeStyleByValue` was consulted only when a uniqueValue renderer had no `defaultSymbol`.
    // Every uniqueValue renderer that declares a default — the common case — therefore painted
    // every feature with the default style and discarded all per-value styling. The per-value
    // lookup is the specific answer; the single style is the FALLBACK.
    if (config.shapeStyleByValue && config.shapeField) {
        const val = attrs[config.shapeField] ?? '';
        const s = config.shapeStyleByValue[val];
        if (s) return s;
    }
    if (config.singleShapeStyle) return config.singleShapeStyle;
    return null;
}
