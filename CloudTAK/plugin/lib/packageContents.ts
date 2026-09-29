// Reads a FeatureLink data package (docs/DATA-PACKAGE-FORMAT.md) into what the ingest needs: every
// layer config, and every iconset with its icon images.
//
// Both package shapes are read, because both reach CloudTAK:
//   standard  — iconsets ZIPPED at the package root (what ATAK needs; sent by ATAK, and by older
//               WinTAK builds). CloudTAK's own importer cannot install these at all.
//   cloudtak  — iconsets EXPANDED: iconsets/<uid>.xml plus <Group>/<file>.png (what WinTAK sends a
//               CloudTAK contact). CloudTAK's importer installs these, but never regenerates the
//               spritesheet the map draws from, and matches icons to files by bare file name — so
//               the ingest re-registers them either way.

import { openZip, type ZipArchive } from './zipReader.ts';

export const CONFIG_SUFFIX = '.featurelinkshare';

export interface PackagedIcon { name: string; imageData: string }

export interface PackagedIconset {
    uid: string;
    /** The icon group: the folder the PNGs live in, and the middle of a marker's usericon path. */
    group: string;
    icons: PackagedIcon[];
}

export interface PackageContents {
    /** Entry names of every layer config, in package order. */
    configNames: string[];
    configs: string[];
    iconsets: PackagedIconset[];
    /** Every entry name, for reporting what a package held when it was not what we expected. */
    entryNames: string[];
}

function toBase64(buf: ArrayBuffer): string {
    const bytes = new Uint8Array(buf);
    let binary = '';
    // Chunked: String.fromCharCode(...hugeArray) overflows the call stack on a large icon.
    for (let i = 0; i < bytes.length; i += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
}

interface IconsetXml { uid: string; group: string; iconNames: string[] }

function parseIconsetXml(xml: string): IconsetXml | null {
    let doc: Document;
    try { doc = new DOMParser().parseFromString(xml, 'application/xml'); }
    catch { return null; }
    const root = doc.documentElement;
    if (!root || root.nodeName !== 'iconset') return null;

    const uid = root.getAttribute('uid')?.trim();
    const group = (root.getAttribute('defaultGroup') || root.getAttribute('name'))?.trim();
    if (!uid || !group) return null;

    const iconNames = Array.from(root.getElementsByTagName('icon'))
        .map(icon => icon.getAttribute('name')?.trim() ?? '')
        .filter(Boolean);
    return { uid, group, iconNames };
}

/** Collects an iconset's images from `zip`, where each lives at `<group>/<icon name>`. */
async function collectIcons(zip: ZipArchive, parsed: IconsetXml): Promise<PackagedIconset> {
    const icons: PackagedIcon[] = [];
    for (const name of parsed.iconNames) {
        const path = `${parsed.group}/${name}`;
        if (!zip.names.includes(path)) continue;   // listed but absent: skip rather than upload nothing
        icons.push({ name, imageData: toBase64(await zip.bytes(path)) });
    }
    return { uid: parsed.uid, group: parsed.group, icons };
}

/** Null when the buffer is not a ZIP. */
export async function readPackageContents(zipBuf: ArrayBuffer): Promise<PackageContents | null> {
    const zip = openZip(zipBuf);
    if (!zip) return null;

    const configNames = zip.names.filter(n => n.endsWith(CONFIG_SUFFIX));
    const configs: string[] = [];
    for (const name of configNames) configs.push(await zip.text(name));

    const iconsets: PackagedIconset[] = [];
    const seen = new Set<string>();

    for (const name of zip.names) {
        const lower = name.toLowerCase();

        if (lower.endsWith('.zip')) {
            // Standard shape: the iconset is a zip within the zip.
            const nested = openZip(await zip.bytes(name));
            if (!nested || !nested.names.includes('iconset.xml')) continue;
            const parsed = parseIconsetXml(await nested.text('iconset.xml'));
            if (!parsed || seen.has(parsed.uid)) continue;
            seen.add(parsed.uid);
            iconsets.push(await collectIcons(nested, parsed));
        } else if (lower.endsWith('.xml') && !lower.startsWith('manifest/')) {
            // CloudTAK shape: iconsets/<uid>.xml, with the PNGs at the package root.
            const parsed = parseIconsetXml(await zip.text(name));
            if (!parsed || seen.has(parsed.uid)) continue;
            seen.add(parsed.uid);
            iconsets.push(await collectIcons(zip, parsed));
        }
    }

    return { configNames, configs, iconsets, entryNames: zip.names };
}
