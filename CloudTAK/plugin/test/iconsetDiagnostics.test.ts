// Renderer extraction diagnostics.
//
// The blind spot these cover: a renderer declaring 10 unique-value symbols of which only 3 carry
// embedded PNG bytes silently produced 3 icons and 7 features rendered with the default marker,
// with nothing anywhere saying so. "declared=10 extracted=3 SKIPPED [esriSMS]" is the line that
// turns that from a device-and-a-pulled-log investigation into a glance at the console.

import { describe, it, expect } from 'vitest';
import { extractPmsEntries } from '../lib/autoIconset.ts';

const PNG_1PX = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

const pms = (): Record<string, unknown> => ({ type: 'esriPMS', imageData: PNG_1PX });
const sms = (): Record<string, unknown> => ({ type: 'esriSMS', style: 'esriSMSCircle', color: [255, 0, 0, 255], size: 8 });

describe('uniqueValue renderer', () => {
    it('reports declared vs extracted and names the skipped symbol types', () => {
        const { diagnostics } = extractPmsEntries({
            type: 'uniqueValue',
            field1: 'CATEGORY',
            uniqueValueInfos: [
                { value: 'A', symbol: pms() },
                { value: 'B', symbol: pms() },
                { value: 'C', symbol: pms() },
                ...['D', 'E', 'F', 'G'].map(value => ({ value, symbol: sms() })),
            ],
        } as never, undefined);

        expect(diagnostics.rendererType).toBe('uniqueValue');
        expect(diagnostics.declared).toBe(7);
        expect(diagnostics.extracted).toBe(3);
        expect(diagnostics.skipped).toEqual(['esriSMS']);
    });

    it('counts a usable defaultSymbol as extracted without counting it as declared', () => {
        const { diagnostics } = extractPmsEntries({
            type: 'uniqueValue', field1: 'CATEGORY',
            uniqueValueInfos: [{ value: 'A', symbol: pms() }],
            defaultSymbol: pms(),
        } as never, undefined);

        expect(diagnostics.declared).toBe(1);
        expect(diagnostics.extracted).toBe(2);
        expect(diagnostics.skipped).toEqual([]);
    });

    it('a renderer with NO defaultSymbol does not record a skip for it', () => {
        // "(absent)" here would be noise — declaring no default is normal, not a dropped symbol.
        const { diagnostics } = extractPmsEntries({
            type: 'uniqueValue', field1: 'CATEGORY',
            uniqueValueInfos: [{ value: 'A', symbol: pms() }],
        } as never, undefined);
        expect(diagnostics.skipped).toEqual([]);
    });

    it('names CIMSymbolReference — the modern Map Viewer encoding this extractor does not parse', () => {
        const { diagnostics } = extractPmsEntries({
            type: 'uniqueValue', field1: 'CATEGORY',
            uniqueValueInfos: [{ value: 'A', symbol: { type: 'CIMSymbolReference' } }],
        } as never, undefined);
        expect(diagnostics.extracted).toBe(0);
        expect(diagnostics.skipped).toEqual(['CIMSymbolReference']);
    });

    it('distinguishes an esriPMS with no imageData from a wrong symbol type', () => {
        const { diagnostics } = extractPmsEntries({
            type: 'uniqueValue', field1: 'CATEGORY',
            uniqueValueInfos: [
                { value: 'A', symbol: { type: 'esriPMS' } },
                { value: 'B', symbol: { size: 8 } },
                { value: 'C' },
            ],
        } as never, undefined);
        expect(diagnostics.skipped).toEqual(['esriPMS(no imageData)', '(untyped)', '(absent)']);
    });
});

describe('other renderer shapes', () => {
    it('a simple renderer declares exactly one symbol', () => {
        const { diagnostics } = extractPmsEntries({ type: 'simple', symbol: pms() } as never, undefined);
        expect(diagnostics).toMatchObject({ rendererType: 'simple', declared: 1, extracted: 1, skipped: [] });
    });

    it('a classBreaks renderer counts its breaks', () => {
        const { diagnostics } = extractPmsEntries({
            type: 'classBreaks', field: 'POP',
            classBreakInfos: [{ symbol: pms() }, { symbol: sms() }],
        } as never, undefined);
        expect(diagnostics).toMatchObject({ rendererType: 'classBreaks', declared: 2, extracted: 1, skipped: ['esriSMS'] });
    });

    it('an absent renderer reports nothing declared rather than throwing', () => {
        const { diagnostics } = extractPmsEntries(undefined, undefined);
        expect(diagnostics).toEqual({ rendererType: '', declared: 0, extracted: 0, defaultExtracted: false, skipped: [] });
    });
});
