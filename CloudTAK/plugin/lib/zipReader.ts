// Minimal read-only ZIP reader: finds one entry by filename suffix and returns its decompressed
// text. Deliberately dependency-free (no jszip/fflate) — this plugin can't add its own runtime
// npm dependencies (see package.json's comment: it only compiles against CloudTAK's own,
// pre-existing dependencies at build time), so this parses the ZIP Central Directory by hand and
// decompresses with the browser's native DecompressionStream('deflate-raw') instead.

const EOCD_SIG = 0x06054b50;
const CD_SIG = 0x02014b50;

interface ZipEntry {
    name: string;
    localHeaderOffset: number;
    compressedSize: number;
    method: number;
}

function findEocd(view: DataView): number {
    // EOCD is near the end of the file, but a trailing per-archive comment (up to 65535 bytes)
    // can push it earlier — scan backward from the end for the signature.
    const searchStart = Math.max(0, view.byteLength - 22 - 65535);
    for (let i = view.byteLength - 22; i >= searchStart; i--) {
        if (view.getUint32(i, true) === EOCD_SIG) return i;
    }
    throw new Error('Not a valid ZIP file (End Of Central Directory not found)');
}

function listEntries(buf: ArrayBuffer): ZipEntry[] {
    const view = new DataView(buf);
    const eocd = findEocd(view);
    const totalEntries = view.getUint16(eocd + 10, true);
    const cdOffset = view.getUint32(eocd + 16, true);

    const entries: ZipEntry[] = [];
    let p = cdOffset;
    for (let i = 0; i < totalEntries; i++) {
        if (view.getUint32(p, true) !== CD_SIG) break; // corrupt/unexpected — stop rather than misread
        const method = view.getUint16(p + 10, true);
        const compressedSize = view.getUint32(p + 20, true);
        const nameLen = view.getUint16(p + 28, true);
        const extraLen = view.getUint16(p + 30, true);
        const commentLen = view.getUint16(p + 32, true);
        const localHeaderOffset = view.getUint32(p + 42, true);
        const name = new TextDecoder().decode(new Uint8Array(buf, p + 46, nameLen));

        entries.push({ name, localHeaderOffset, compressedSize, method });
        p += 46 + nameLen + extraLen + commentLen;
    }
    return entries;
}

async function readEntryBytes(buf: ArrayBuffer, entry: ZipEntry): Promise<ArrayBuffer> {
    const view = new DataView(buf);
    // The local file header's own name/extra-field lengths can differ slightly from the Central
    // Directory's copy — read them fresh to find exactly where compressed data starts.
    const localNameLen = view.getUint16(entry.localHeaderOffset + 26, true);
    const localExtraLen = view.getUint16(entry.localHeaderOffset + 28, true);
    const dataStart = entry.localHeaderOffset + 30 + localNameLen + localExtraLen;
    const compressed = new Uint8Array(buf, dataStart, entry.compressedSize);

    if (entry.method === 0) return compressed.slice().buffer; // stored, no compression
    if (entry.method === 8) {
        const stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
        return await new Response(stream).arrayBuffer();
    }
    throw new Error(`Unsupported ZIP compression method: ${entry.method}`);
}

async function readEntryText(buf: ArrayBuffer, entry: ZipEntry): Promise<string> {
    return new TextDecoder().decode(await readEntryBytes(buf, entry));
}

/** A ZIP opened once, so a package's entries can be read by name without re-parsing it. */
export interface ZipArchive {
    names: string[];
    text(name: string): Promise<string>;
    bytes(name: string): Promise<ArrayBuffer>;
}

/** Opens a ZIP for reading by entry name, or returns null when the buffer is not a ZIP. */
export function openZip(zipBuf: ArrayBuffer): ZipArchive | null {
    let entries: ZipEntry[];
    try {
        entries = listEntries(zipBuf);
    } catch {
        return null;
    }
    const byName = new Map(entries.map(e => [e.name, e]));
    const get = (name: string): ZipEntry => {
        const entry = byName.get(name);
        if (!entry) throw new Error(`No "${name}" in the ZIP`);
        return entry;
    };
    return {
        names: entries.map(e => e.name),
        text: name => readEntryText(zipBuf, get(name)),
        bytes: name => readEntryBytes(zipBuf, get(name)),
    };
}

// Returns the text content of the first entry whose name ends with `suffix`, or null if none
// match (including if `zipBuf` isn't a valid ZIP at all — callers treat that as "not ours").
export async function findZipEntryText(zipBuf: ArrayBuffer, suffix: string): Promise<string | null> {
    let entries: ZipEntry[];
    try {
        entries = listEntries(zipBuf);
    } catch {
        return null;
    }
    const match = entries.find(e => e.name.endsWith(suffix));
    if (!match) return null;
    return readEntryText(zipBuf, match);
}

/**
 * Entry names in a ZIP, for reporting what a package actually contained when the entry we wanted
 * was not in it. Never throws — a buffer that is not a ZIP yields an empty list.
 */
export function listZipEntryNames(zipBuf: ArrayBuffer): string[] {
    try {
        return listEntries(zipBuf).map(e => e.name);
    } catch {
        return [];
    }
}
