// A FeatureLink data package reaches CloudTAK in two shapes (docs/DATA-PACKAGE-FORMAT.md): the
// standard one, iconsets zipped (what ATAK needs), and the CloudTAK one WinTAK sends a CloudTAK
// contact, iconsets expanded. The ingest has to read every layer config and every iconset from
// both — the original ingest applied only the first config and ignored iconsets entirely, so a
// multi-layer package lost layers and a recipient offline from ArcGIS got default symbols.

import { describe, it, expect } from 'vitest';
import { readPackageContents } from '../lib/packageContents.ts';

const enc = new TextEncoder();

/** Builds a STORED (uncompressed) zip. The reader does not check CRCs, so they are left zero. */
function zip(entries: Record<string, string | Uint8Array>): ArrayBuffer {
    const locals: Uint8Array[] = [];
    const centrals: Uint8Array[] = [];
    let offset = 0;

    for (const [name, value] of Object.entries(entries)) {
        const nameBytes = enc.encode(name);
        const data = typeof value === 'string' ? enc.encode(value) : value;

        const local = new Uint8Array(30 + nameBytes.length + data.length);
        const lv = new DataView(local.buffer);
        lv.setUint32(0, 0x04034b50, true);
        lv.setUint32(18, data.length, true);
        lv.setUint32(22, data.length, true);
        lv.setUint16(26, nameBytes.length, true);
        local.set(nameBytes, 30);
        local.set(data, 30 + nameBytes.length);

        const central = new Uint8Array(46 + nameBytes.length);
        const cv = new DataView(central.buffer);
        cv.setUint32(0, 0x02014b50, true);
        cv.setUint32(20, data.length, true);
        cv.setUint32(24, data.length, true);
        cv.setUint16(28, nameBytes.length, true);
        cv.setUint32(42, offset, true);
        central.set(nameBytes, 46);

        locals.push(local);
        centrals.push(central);
        offset += local.length;
    }

    const cdSize = centrals.reduce((n, c) => n + c.length, 0);
    const eocd = new Uint8Array(22);
    const ev = new DataView(eocd.buffer);
    ev.setUint32(0, 0x06054b50, true);
    ev.setUint16(8, centrals.length, true);
    ev.setUint16(10, centrals.length, true);
    ev.setUint32(12, cdSize, true);
    ev.setUint32(16, offset, true);

    const out = new Uint8Array(offset + cdSize + 22);
    let p = 0;
    for (const part of [...locals, ...centrals, eocd]) { out.set(part, p); p += part.length; }
    return out.buffer;
}

const PNG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]);
const PNG_B64 = btoa(String.fromCharCode(...PNG));
const UID = 'a'.repeat(64);

const ICONSET_XML = `<iconset name="Teams Icons" uid="${UID}" defaultGroup="Teams Icons" version="1">
  <icon name="Active.png"/>
  <icon name="Out.png"/>
</iconset>`;

const CONFIG_A = JSON.stringify({ v: 3, url: 'https://h/a/FeatureServer/0' });
const CONFIG_B = JSON.stringify({ v: 3, url: 'https://h/b/FeatureServer/0' });
const MANIFEST = '<MissionPackageManifest version="2"><Configuration/><Contents/></MissionPackageManifest>';

describe('readPackageContents', () => {
    it('reads every layer config, not just the first', async () => {
        const pkg = await readPackageContents(zip({
            'MANIFEST/manifest.xml': MANIFEST,
            'featurelink/A-111.featurelinkshare': CONFIG_A,
            'featurelink/B-222.featurelinkshare': CONFIG_B,
        }));

        expect(pkg?.configs).toEqual([CONFIG_A, CONFIG_B]);
    });

    it('reads a zipped iconset from the standard shape', async () => {
        const pkg = await readPackageContents(zip({
            'MANIFEST/manifest.xml': MANIFEST,
            'Teams Icons.zip': new Uint8Array(zip({
                'iconset.xml': ICONSET_XML,
                'Teams Icons/Active.png': PNG,
                'Teams Icons/Out.png': PNG,
            })),
            'featurelink/A-111.featurelinkshare': CONFIG_A,
        }));

        expect(pkg?.iconsets).toEqual([{
            uid: UID,
            group: 'Teams Icons',
            icons: [{ name: 'Active.png', imageData: PNG_B64 }, { name: 'Out.png', imageData: PNG_B64 }],
        }]);
    });

    it('reads an expanded iconset from the CloudTAK shape', async () => {
        const pkg = await readPackageContents(zip({
            'MANIFEST/manifest.xml': MANIFEST,
            [`iconsets/${UID}.xml`]: ICONSET_XML,
            'Teams Icons/Active.png': PNG,
            'Teams Icons/Out.png': PNG,
            'featurelink/A-111.featurelinkshare': CONFIG_A,
        }));

        expect(pkg?.iconsets.map(s => [s.uid, s.group, s.icons.map(i => i.name)]))
            .toEqual([[UID, 'Teams Icons', ['Active.png', 'Out.png']]]);
        expect(pkg?.configs).toEqual([CONFIG_A]);
    });

    it('does not mistake the manifest for an iconset', async () => {
        const pkg = await readPackageContents(zip({ 'MANIFEST/manifest.xml': MANIFEST }));
        expect(pkg?.iconsets).toEqual([]);
    });

    it('skips an icon the iconset lists but the package does not hold', async () => {
        const pkg = await readPackageContents(zip({
            [`iconsets/${UID}.xml`]: ICONSET_XML,
            'Teams Icons/Active.png': PNG,
        }));
        expect(pkg?.iconsets[0]?.icons.map(i => i.name)).toEqual(['Active.png']);
    });

    it('returns null for something that is not a zip', async () => {
        expect(await readPackageContents(enc.encode('not a zip').buffer)).toBeNull();
    });
});
