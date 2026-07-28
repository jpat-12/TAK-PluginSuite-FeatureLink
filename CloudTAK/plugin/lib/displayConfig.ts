// Port of the ATAK plugin's DisplayConfig.java — parses the styling-rule JSON produced by
// TAK Portal's configurator (Mode 1/2 compact schema, or Mode 3 full Esri-renderer schema)
// and resolves per-feature color / iconset path / label / remarks from it. QR/deep-link
// transport is dropped (see plan); the JSON schema itself is unchanged so existing configs
// still paste/upload correctly.

import type {
    DisplayConfig, SymConfig, SymValueEntry, SymOp, CotMapping, CotFieldMapping,
} from './types.ts';

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

function esriColorToHex(c: number[] | undefined): string | undefined {
    if (!c || c.length < 3) return undefined;
    const [r, g, b] = c;
    return `#${[r, g, b].map(n => Math.max(0, Math.min(255, n)).toString(16).padStart(2, '0')).join('')}`;
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
export function parseDisplayConfig(input: unknown): DisplayConfig | null {
    let raw: unknown = input;
    if (typeof input === 'string') {
        try { raw = JSON.parse(input); } catch { return null; }
    }
    if (typeof raw !== 'object' || raw === null) return null;
    const obj = raw as Record<string, unknown>;

    if ('symbology' in obj || 'featureLayerUrl' in obj) {
        return fromMode3(obj as Mode3Json);
    }
    if ('sym' in obj || ('v' in obj && ('url' in obj || 'layer' in obj))) {
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
            const val = sym.f ? parseFloat(attrs[sym.f] ?? '') : NaN;
            if (!Number.isNaN(val)) {
                const bucket = sym.cb?.find(b => val >= b.mn && val < b.mx);
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

export function resolveLabel(config: DisplayConfig, attrs: Record<string, string>, fallback: string): string {
    const field = config.lbl?.f;
    if (!field) return fallback;
    const v = attrs[field];
    return v && v !== '' ? v : fallback;
}

export function buildRemarks(config: DisplayConfig, attrs: Record<string, string>): string {
    const flds = config.popup?.flds;
    if (!flds || flds.length === 0) return '';
    const lines: string[] = [];
    for (const f of flds) {
        const [field, alias] = Array.isArray(f) ? f : [f, f];
        const v = attrs[field];
        if (v && v !== '') lines.push(`${alias}: ${v}`);
    }
    return lines.join('\n');
}
