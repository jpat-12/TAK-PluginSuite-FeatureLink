// Browse rows offer only the actions that can actually do something.
//
// "My ArcGIS Layers" is a LISTING of the operator's ArcGIS account, not a set of layers held on
// this device. Share has nothing to build a config from there, and Remove reads as "delete from
// ArcGIS" — neither belongs on the row. The gate is SECTION MEMBERSHIP, not `lastSync`: a layer
// whose download failed still lives in an on-device section with `lastSync === 0`, and gating on
// the timestamp stranded exactly those rows with no way to remove them.

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import LayerRow from '../components/LayerRow.vue';
import { newLayer } from '../lib/types.ts';
import { resetStore } from '../lib/store.ts';
import type { ArcGISLayer } from '../lib/types.ts';

function layer(overrides: Partial<ArcGISLayer> = {}): ArcGISLayer {
    const url = 'https://services1.arcgis.com/abc/arcgis/rest/services/Roads/FeatureServer/0';
    return { ...newLayer('Roads', url, 'private'), ...overrides };
}

function buttonTitles(wrapper: ReturnType<typeof mount>): string[] {
    return wrapper.findAll('button').map(b => b.attributes('title') ?? '');
}

beforeEach(() => { resetStore(); });

describe('browse rows ("My ArcGIS Layers")', () => {
    it('shows neither Share nor Remove', () => {
        const wrapper = mount(LayerRow, { props: { layer: layer({ type: 'private' }), browseSection: true } });
        expect(buttonTitles(wrapper)).not.toContain('Remove');
        expect(buttonTitles(wrapper)).not.toContain('Share config');
    });

    it('keeps the download action — that is the point of the listing', () => {
        const wrapper = mount(LayerRow, { props: { layer: layer({ type: 'private', lastSync: 0 }), browseSection: true } });
        expect(buttonTitles(wrapper)).toContain('Download now');
    });
});

describe('on-device rows', () => {
    it('a never-synced on-device layer STILL offers Remove', () => {
        // The regression this test exists for: gating on `lastSync > 0` hid Remove on every layer
        // whose download failed, while the scheduler kept retrying it on every tick.
        const wrapper = mount(LayerRow, { props: { layer: layer({ type: 'private', lastSync: 0 }) } });
        expect(buttonTitles(wrapper)).toContain('Remove');
    });

    it('a public layer offers Share and Remove', () => {
        const wrapper = mount(LayerRow, { props: { layer: layer({ type: 'public', lastSync: Date.now() }) } });
        expect(buttonTitles(wrapper)).toContain('Share config');
        expect(buttonTitles(wrapper)).toContain('Remove');
    });

    it('Remove still confirms before emitting', async () => {
        const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
        const wrapper = mount(LayerRow, { props: { layer: layer({ type: 'public' }) } });
        await wrapper.findAll('button').filter(b => b.attributes('title') === 'Remove')[0]?.trigger('click');
        expect(confirm).toHaveBeenCalled();
        expect(wrapper.emitted('delete')).toBeUndefined();

        confirm.mockReturnValue(true);
        await wrapper.findAll('button').filter(b => b.attributes('title') === 'Remove')[0]?.trigger('click');
        expect(wrapper.emitted('delete')).toHaveLength(1);
    });
});
