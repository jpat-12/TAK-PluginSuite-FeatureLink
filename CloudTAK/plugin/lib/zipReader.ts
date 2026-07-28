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

async function readEntryText(buf: ArrayBuffer, entry: ZipEntry): Promise<string> {
    const view = new DataView(buf);
    // The local file header's own name/extra-field lengths can differ slightly from the Central
    // Directory's copy — read them fresh to find exactly where compressed data starts.
    const localNameLen = view.getUint16(entry.localHeaderOffset + 26, true);
    const localExtraLen = view.getUint16(entry.localHeaderOffset + 28, true);
    const dataStart = entry.localHeaderOffset + 30 + localNameLen + localExtraLen;
    const compressed = new Uint8Array(buf, dataStart, entry.compressedSize);

    if (entry.method === 0) return new TextDecoder().decode(compressed); // stored, no compression
    if (entry.method === 8) {
        const stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
        return await new Response(stream).text();
    }
    throw new Error(`Unsupported ZIP compression method: ${entry.method}`);
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
