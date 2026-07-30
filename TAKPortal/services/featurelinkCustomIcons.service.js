/**
 * Custom icon sets — lets an admin/user upload their own icon images into the
 * Display Configurator's icon picker, alongside the bundled iconsets shipped
 * in assets/featurelink-configurator/icons/.
 *
 * Storage: data/featurelink-configs/custom-icons/<setName>/<file>, inside the
 * tak_portal_data volume so uploads survive rebuilds. manifest.json (in the
 * same directory) tracks {name, uid, defaultGroup, icons: [filenames],
 * groups: {filename: atakGroup}, atakNames: {filename: atakRawFileName},
 * created_by, created_at} per set. icons/groups/atakNames are keyed by the
 * sanitized filename used for local storage/serving (see uniqueFileName());
 * groups/atakNames carry the *real* values ATAK will use to look the icon up
 * on-device, which can differ from the sanitized name — see below.
 *
 * uid resolution mirrors com.atakmap.android.icons.IconsMapAdapter.addIconset()
 * exactly (decompiled from the ATAK 5.6 SDK's main.jar to confirm this — see
 * commit history): ATAK reads iconset.xml from the zip and, if it parses with
 * a non-empty name AND uid (UserIconSet.isValid()), trusts its uid verbatim.
 * Only when iconset.xml is missing/empty/invalid does ATAK fall back to
 * HashingUtils.sha256sum(zipFile) — a SHA-256 hex digest of the *raw zip file
 * bytes* — as a synthetic uid (see sha256sumFile()). We replicate both paths
 * so a set's resolved uid always matches what ATAK would compute from the
 * same zip bytes, whether or not it ships an iconset.xml.
 *
 * group/filename resolution (also decompiled from the same method): for
 * *every* image entry in the zip, regardless of iconset.xml validity, ATAK
 * derives:
 *   - group = the entry's first path segment, in its original case, e.g.
 *     "MyGroup/icon.png" -> "MyGroup" — or the literal string "Other" if the
 *     entry has no folder (sits at the zip root).
 *   - filename = the entry's last path segment, in its original case —
 *     spaces and all, NOT sanitized.
 * Neither value comes from iconset.xml's defaultGroup/per-icon attributes,
 * and neither is lowercased (a lowercase pass exists in ATAK's import code,
 * but only feeds an in-memory duplicate-filename check — the persisted
 * UserIcon.group/fileName keep their original case). So resolveUsericonPath()
 * in index.html must use each icon's *real* zip-derived group/filename, not
 * a single defaultGroup for the whole set — that's what groups/atakNames
 * carry per icon.
 *
 * Loose (non-.zip) image uploads have no zip path structure and no zip file
 * for ATAK to have hashed, so they can never resolve to a real ATAK uid —
 * those sets still preview fine in the configurator but fall back to the
 * plugin's default marker styling in the field.
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const unzipper = require("unzipper");

const DATA_DIR = path.join(__dirname, "..", "data", "featurelink-configs");
const CUSTOM_ICONS_DIR = path.join(DATA_DIR, "custom-icons");
const MANIFEST_PATH = path.join(CUSTOM_ICONS_DIR, "manifest.json");

const NAME_RE = /[^a-zA-Z0-9 _-]/g;
const FILE_RE = /[^a-zA-Z0-9._-]/g;
const IMAGE_EXT_RE = /\.(png|jpe?g|gif|bmp|webp|svg)$/i;
const ICONSET_XML_RE = /^iconset\.xml$/i;
const ZIP_ENTRY_LIMIT = 500; // guards against zip-bomb-style entry counts
const ZIP_ENTRY_MAX_BYTES = 5 * 1024 * 1024; // per-icon uncompressed size cap
const ICONSET_XML_MAX_BYTES = 1 * 1024 * 1024; // iconset.xml is just attribute/text data
const OTHER_GROUP = "Other"; // exact literal ATAK uses for icons with no containing folder

/**
 * Pulls {uid, defaultGroup} out of an ATAK iconset.xml's root <iconset ...> tag, e.g.
 * <iconset name="..." uid="db450cbe-..." defaultGroup="Incident Management" version="1">.
 * Plain attribute regex rather than a full XML parser — the root tag is all that's needed.
 * Mirrors UserIconSet.isValid(): both name and uid must be present and non-empty, or ATAK
 * treats the whole file as invalid (and falls back to hashing the zip — see sha256sumFile()).
 */
function parseIconsetXmlMeta(xmlText) {
  const tagMatch = xmlText.match(/<iconset\b([^>]*)>/i);
  if (!tagMatch) return null;
  const attrs = {};
  const attrRe = /([\w:-]+)\s*=\s*"([^"]*)"/g;
  let m;
  while ((m = attrRe.exec(tagMatch[1]))) attrs[m[1]] = m[2];
  if (!attrs.uid || !attrs.name) return null;
  return { uid: attrs.uid, defaultGroup: attrs.defaultGroup || null };
}

/**
 * Mirrors ATAK's HashingUtils.sha256sum(File): MessageDigest("SHA-256") streamed over the
 * whole file in 8192-byte chunks, each output byte formatted "%02x" — a plain whole-file hex
 * digest, no salting/truncation. Must hash the exact same bytes ATAK hashed, so this only
 * produces a matching uid when the zip uploaded here is byte-identical to the zip that was
 * actually installed on-device.
 */
function sha256sumFile(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

/** {group, atakName} for a zip entry's path, per IconsMapAdapter.addIconset()'s exact rule. */
function resolveAtakGroupAndName(entryPath) {
  const parts = entryPath.replace(/\\/g, "/").split("/").filter(Boolean);
  const atakName = parts[parts.length - 1];
  const group = parts.length > 1 ? parts[0] : OTHER_GROUP;
  return { group, atakName };
}

function ensureDir() {
  if (!fs.existsSync(CUSTOM_ICONS_DIR)) fs.mkdirSync(CUSTOM_ICONS_DIR, { recursive: true });
}

function cleanSetName(name) {
  return String(name || "").replace(NAME_RE, "").trim().slice(0, 60);
}

function readManifest() {
  ensureDir();
  if (!fs.existsSync(MANIFEST_PATH)) return [];
  try {
    const parsed = JSON.parse(fs.readFileSync(MANIFEST_PATH, "utf-8"));
    return Array.isArray(parsed) ? parsed : [];
  } catch (_) {
    return [];
  }
}

function writeManifest(sets) {
  ensureDir();
  fs.writeFileSync(MANIFEST_PATH, JSON.stringify(sets, null, 2), "utf-8");
}

/** List of {name, icons, ...} — same iconset shape the configurator expects. */
function listCustomIconSets() {
  return readManifest();
}

function isZipUpload(file) {
  return /\.zip$/i.test(file.originalname || "")
    || file.mimetype === "application/zip"
    || file.mimetype === "application/x-zip-compressed";
}

/** Renames on collision (icon_2.png, icon_3.png, ...) so a repeat upload never clobbers an existing icon. */
function uniqueFileName(dir, fileName) {
  if (!fs.existsSync(path.join(dir, fileName))) return fileName;
  const ext = path.extname(fileName);
  const base = fileName.slice(0, fileName.length - ext.length);
  let i = 2;
  while (fs.existsSync(path.join(dir, `${base}_${i}${ext}`))) i++;
  return `${base}_${i}${ext}`;
}

/**
 * Unpacks a .zip iconset into individual icon files (stored flat locally, regardless of the
 * zip's own folder structure — see class doc comment for why each icon's *real* ATAK group
 * and filename, derived from that structure, are tracked separately in entry.groups/atakNames
 * rather than being inferred from local storage layout). Non-image entries (iconset.xml,
 * __MACOSX junk, directories) are skipped from icons/groups/atakNames.
 */
async function extractZipIcons(zipPath, setDir, entry, skipped) {
  const directory = await unzipper.Open.file(zipPath);
  let extracted = 0;
  let xmlMeta = null;
  for (const zipEntry of directory.files) {
    if (zipEntry.type !== "File") continue;
    const baseName = path.basename(zipEntry.path);

    if (ICONSET_XML_RE.test(baseName)) {
      if ((zipEntry.vars?.uncompressedSize || 0) <= ICONSET_XML_MAX_BYTES) {
        try {
          xmlMeta = parseIconsetXmlMeta((await zipEntry.buffer()).toString("utf-8"));
        } catch (_) { /* malformed iconset.xml — treated as missing, same as ATAK */ }
      }
      continue;
    }

    if (extracted >= ZIP_ENTRY_LIMIT) break;
    if (!IMAGE_EXT_RE.test(baseName)) {
      if (skipped.length < 10) skipped.push(baseName);
      continue;
    }
    if ((zipEntry.vars?.uncompressedSize || 0) > ZIP_ENTRY_MAX_BYTES) {
      if (skipped.length < 10) skipped.push(`${baseName} (too large)`);
      continue;
    }
    const safeName = uniqueFileName(setDir, baseName.replace(FILE_RE, "_"));
    const buffer = await zipEntry.buffer();
    fs.writeFileSync(path.join(setDir, safeName), buffer);
    entry.icons.push(safeName);
    const { group, atakName } = resolveAtakGroupAndName(zipEntry.path);
    entry.groups[safeName] = group;
    entry.atakNames[safeName] = atakName;
    extracted++;
  }

  // uid resolution: trust a valid iconset.xml's uid; otherwise hash the whole zip file, exactly
  // like IconsMapAdapter.addIconset() does. Only overwrite an existing uid if this zip actually
  // resolved one — appending more icons to an already-linked set shouldn't un-link it.
  if (xmlMeta) {
    entry.uid = xmlMeta.uid;
    entry.defaultGroup = xmlMeta.defaultGroup;
  } else if (extracted > 0) {
    entry.uid = sha256sumFile(zipPath);
    entry.defaultGroup = entry.defaultGroup || null;
  }

  return extracted;
}

/**
 * Adds (or appends to, if the set name already exists) a custom icon set. .zip uploads
 * are unpacked into their individual icon images; anything else is stored as one icon.
 * files: [{ path, originalname, mimetype }] (from multer's disk storage).
 */
async function addCustomIcons(setName, files, actorUsername) {
  const name = cleanSetName(setName);
  if (!name) return { success: false, error: "Icon set name is required." };
  if (!files || !files.length) return { success: false, error: "At least one icon file is required." };

  ensureDir();
  const setDir = path.join(CUSTOM_ICONS_DIR, name);
  fs.mkdirSync(setDir, { recursive: true });

  const sets = readManifest();
  let entry = sets.find((s) => s.name === name);
  if (!entry) {
    entry = {
      name, icons: [], groups: {}, atakNames: {}, uid: null, defaultGroup: null,
      created_by: actorUsername || null, created_at: new Date().toISOString(),
    };
    sets.push(entry);
  }
  if (!entry.groups) entry.groups = {};
  if (!entry.atakNames) entry.atakNames = {};

  let totalExtracted = 0;
  const skipped = [];
  for (const file of files) {
    if (isZipUpload(file)) {
      try {
        totalExtracted += await extractZipIcons(file.path, setDir, entry, skipped);
      } catch (err) {
        return { success: false, error: `Couldn't read "${file.originalname}" as a zip: ${err.message}` };
      }
    } else {
      const safeName = uniqueFileName(setDir, (file.originalname || "icon.png").replace(FILE_RE, "_"));
      fs.copyFileSync(file.path, path.join(setDir, safeName));
      entry.icons.push(safeName);
      totalExtracted++;
    }
  }

  if (!totalExtracted) {
    const seen = skipped.length ? ` Files found inside: ${skipped.join(", ")}${skipped.length===10?', …':''}.` : ' The zip appears to have no files in it.';
    return { success: false, error: `No image files found — supported types are PNG/JPG/GIF/BMP/WEBP/SVG.${seen}` };
  }

  writeManifest(sets);
  return { success: true, set: entry };
}

/**
 * Registers a set generated from an ArcGIS renderer (AUTO-ICONSET-SPEC.md, Phase D) without
 * going through the zip-upload path. The caller (featurelinkArcgisIconset.service.js) has
 * already decoded the PNGs and computed the *deterministic* spec values:
 *   - uid    = sha256(canonicalUrl + "/" + field), written VERBATIM (spec §4) — so a federated
 *              ATAK/WinTAK/CloudTAK generator computing the same uid cross-resolves with this
 *              set even though the PNG bytes differ (spec §0.1).
 *   - group  = "<layer name> Icons" (spec §5.1)
 *   - each icon's atakName = "<value>.png" (spec §5.2), stored as the icon's real ATAK filename.
 * Unlike a real ATAK zip import (where uid would come from iconset.xml or a zip hash), here the
 * uid is authoritative input — we do not hash anything.
 *
 * Idempotent: regenerating the same set (same name) wipes the prior files/entry first, so the
 * stored filenames stay identical to the spec atakNames (no uniqueFileName _N drift on rerun),
 * which is what keeps this set's paths equal to what other platforms independently produce.
 *
 * @param {object} a
 * @param {string} a.name    storage/display name (the spec group)
 * @param {string} a.uid     spec uid, used verbatim
 * @param {string} a.group   spec group ("<layer name> Icons")
 * @param {Array<{atakName:string, bytes:Buffer}>} a.icons
 * @param {string} [a.sourceUrl]  canonical layer URL (audit/regenerate)
 * @param {string} [a.field]      renderer driving field (audit)
 * @param {number} [a.specVersion]
 * @param {string} [a.actorUsername]
 * @returns {{success:true, set:object} | {success:false, error:string}}
 */
function registerArcgisSet({ name, uid, group, icons, sourceUrl, field, specVersion, actorUsername }) {
  const setName = cleanSetName(name);
  if (!setName) return { success: false, error: "Icon set name is required." };
  if (!uid) return { success: false, error: "A deterministic uid is required (spec §4)." };
  if (!icons || !icons.length) return { success: false, error: "At least one icon is required." };

  ensureDir();
  const setDir = path.join(CUSTOM_ICONS_DIR, setName);

  // Idempotent regenerate: drop any prior copy so stored names == spec atakNames exactly.
  const sets = readManifest().filter((s) => s.name !== setName);
  try {
    if (fs.existsSync(setDir)) fs.rmSync(setDir, { recursive: true, force: true });
  } catch (_) { /* best effort — a stale file just gets overwritten below */ }
  fs.mkdirSync(setDir, { recursive: true });

  const entry = {
    name: setName,
    icons: [],
    groups: {},
    atakNames: {},
    uid,
    defaultGroup: group || null,
    // provenance for the auto-generated case (regenerate + "which layer is this from")
    source: "arcgis-renderer",
    sourceUrl: sourceUrl || null,
    sourceField: field || "",
    specVersion: specVersion || 1,
    created_by: actorUsername || null,
    created_at: new Date().toISOString(),
  };

  const takenLocal = new Set();
  for (const icon of icons) {
    const atakName = String(icon.atakName || "icon.png");
    // atakName is already FILE_RE-safe (spec §5.2); guard local-storage collisions without
    // mutating atakName — the ATAK-facing name must stay the deterministic spec value.
    let localName = atakName.replace(FILE_RE, "_");
    if (takenLocal.has(localName)) localName = uniqueFileName(setDir, localName);
    takenLocal.add(localName);

    fs.writeFileSync(path.join(setDir, localName), icon.bytes);
    entry.icons.push(localName);
    entry.groups[localName] = group;      // ATAK group (spec §5.1) — used by resolveUsericonPath
    entry.atakNames[localName] = atakName; // real ATAK filename (spec §5.2)
  }

  sets.push(entry);
  writeManifest(sets);
  return { success: true, set: entry };
}

/** The manifest entry whose deterministic uid matches, or null. Backs a by-uid lookup (spec §10,
 * optional Portal cache) so a device seeing an unfamiliar iconsetpath uid can resolve it. */
function findSetByUid(uid) {
  if (!uid) return null;
  return readManifest().find((s) => s.uid === uid) || null;
}

function deleteCustomIconSet(setName) {
  const name = cleanSetName(setName);
  if (!name) return { success: false, error: "not found" };
  const sets = readManifest();
  const idx = sets.findIndex((s) => s.name === name);
  if (idx === -1) return { success: false, error: "not found" };
  sets.splice(idx, 1);
  writeManifest(sets);
  const setDir = path.join(CUSTOM_ICONS_DIR, name);
  try {
    if (fs.existsSync(setDir)) fs.rmSync(setDir, { recursive: true, force: true });
  } catch (_) {}
  return { success: true };
}

/** Absolute path to a specific icon file within a custom set, or null if it doesn't exist. */
function getCustomIconPath(setName, fileName) {
  const name = cleanSetName(setName);
  const file = String(fileName || "").replace(FILE_RE, "_");
  if (!name || !file) return null;
  const p = path.join(CUSTOM_ICONS_DIR, name, file);
  return fs.existsSync(p) ? p : null;
}

// -------------------------------------------------------------------------
// Zip export (ATAK-installable iconset package)
// -------------------------------------------------------------------------

let CRC_TABLE = null;

/** Standard CRC-32 (same polynomial zip uses). Hand-rolled rather than zlib.crc32() because
 * that's only available from Node 20.12 and this module targets the Node 18 floor. */
function crc32(buf) {
  if (!CRC_TABLE) {
    CRC_TABLE = new Int32Array(256);
    for (let i = 0; i < 256; i++) {
      let c = i;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      CRC_TABLE[i] = c;
    }
  }
  let crc = -1;
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ CRC_TABLE[(crc ^ buf[i]) & 0xff];
  return (crc ^ -1) >>> 0;
}

/**
 * Minimal store-only (compression method 0) zip writer — no new host dependency, matching this
 * module's "fs/crypto only" constraint. Store-only is a fully valid zip that Java's
 * ZipFile/ZipInputStream (what ATAK's importer uses) reads normally, and PNG payloads are already
 * compressed so deflating them would buy almost nothing anyway.
 *
 * @param {{path:string, data:Buffer}[]} entries in the order they should appear in the archive
 */
function buildStoredZip(entries) {
  const local = [];
  const central = [];
  let offset = 0;
  const DOS_DATE = 0x21; // 1980-01-01: ((year-1980)<<9)|(month<<5)|day — fixed, so output is deterministic
  const UTF8_FLAG = 0x0800;

  for (const e of entries) {
    const nameBuf = Buffer.from(e.path, "utf8");
    const crc = crc32(e.data);
    const size = e.data.length;

    const lfh = Buffer.alloc(30);
    lfh.writeUInt32LE(0x04034b50, 0); // local file header signature
    lfh.writeUInt16LE(20, 4); // version needed
    lfh.writeUInt16LE(UTF8_FLAG, 6);
    lfh.writeUInt16LE(0, 8); // method: stored
    lfh.writeUInt16LE(0, 10); // mod time
    lfh.writeUInt16LE(DOS_DATE, 12);
    lfh.writeUInt32LE(crc, 14);
    lfh.writeUInt32LE(size, 18); // compressed size == uncompressed (stored)
    lfh.writeUInt32LE(size, 22);
    lfh.writeUInt16LE(nameBuf.length, 26);
    lfh.writeUInt16LE(0, 28); // extra field length
    local.push(lfh, nameBuf, e.data);

    const cdh = Buffer.alloc(46);
    cdh.writeUInt32LE(0x02014b50, 0); // central directory header signature
    cdh.writeUInt16LE(20, 4); // version made by
    cdh.writeUInt16LE(20, 6); // version needed
    cdh.writeUInt16LE(UTF8_FLAG, 8);
    cdh.writeUInt16LE(0, 10); // method: stored
    cdh.writeUInt16LE(0, 12); // mod time
    cdh.writeUInt16LE(DOS_DATE, 14);
    cdh.writeUInt32LE(crc, 16);
    cdh.writeUInt32LE(size, 20);
    cdh.writeUInt32LE(size, 24);
    cdh.writeUInt16LE(nameBuf.length, 28);
    cdh.writeUInt16LE(0, 30); // extra
    cdh.writeUInt16LE(0, 32); // comment
    cdh.writeUInt16LE(0, 34); // disk number start
    cdh.writeUInt16LE(0, 36); // internal attrs
    cdh.writeUInt32LE(0, 38); // external attrs
    cdh.writeUInt32LE(offset, 42); // relative offset of local header
    central.push(cdh, nameBuf);

    offset += lfh.length + nameBuf.length + size;
  }

  const cdBuf = Buffer.concat(central);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0); // end of central directory signature
  eocd.writeUInt16LE(0, 4); // this disk
  eocd.writeUInt16LE(0, 6); // disk with cd start
  eocd.writeUInt16LE(entries.length, 8);
  eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(cdBuf.length, 12);
  eocd.writeUInt32LE(offset, 16);
  eocd.writeUInt16LE(0, 20); // comment length

  return Buffer.concat([...local, cdBuf, eocd]);
}

/**
 * Packages a stored set back into an ATAK-installable iconset zip (AUTO-ICONSET-SPEC.md §7
 * layout: iconset.xml at root + one "{group}/{filename}" entry per icon), so it can be handed
 * to a device directly (ATAK Settings > Import Content) rather than only reached through a
 * display config.
 *
 * Entry paths use each icon's *real* ATAK group/filename (groups/atakNames), not the sanitized
 * local storage name — ATAK derives group/filename from the zip entry path verbatim (§0.2), so
 * these paths are what make the downloaded zip resolve to the same iconsetpath the configs
 * reference.
 *
 * iconset.xml carries only name+uid and per-icon `name` — NO `group` attribute: ATAK's strict
 * SimpleXML parser rejects the whole file over an unrecognized attribute and silently falls back
 * to hashing the zip, which would change the uid. See §7 and AutoIconset.buildIconsetXml().
 * Omitted entirely for a set with no uid (a loose non-zip upload), which is exactly the case
 * where ATAK's zip-hash fallback is the intended behavior.
 *
 * @returns {{success:true, fileName:string, buffer:Buffer} | {success:false, error:string}}
 */
function buildIconsetZip(setName) {
  const name = cleanSetName(setName);
  if (!name) return { success: false, error: "not found" };
  const set = readManifest().find((s) => s.name === name);
  if (!set) return { success: false, error: "not found" };

  const entries = [];
  const iconEntries = [];
  for (const localName of set.icons || []) {
    const p = path.join(CUSTOM_ICONS_DIR, name, localName);
    if (!fs.existsSync(p)) continue;
    const group = (set.groups && set.groups[localName]) || set.defaultGroup || OTHER_GROUP;
    const atakName = (set.atakNames && set.atakNames[localName]) || localName;
    iconEntries.push({ path: `${group}/${atakName}`, data: fs.readFileSync(p), atakName });
  }
  if (!iconEntries.length) return { success: false, error: "This icon set has no readable icon files." };

  if (set.uid) {
    const xml =
      '<?xml version="1.0" encoding="UTF-8"?>\n' +
      `<iconset name="${xmlAttr(name)}" uid="${xmlAttr(set.uid)}"` +
      (set.defaultGroup ? ` defaultGroup="${xmlAttr(set.defaultGroup)}"` : "") +
      ' version="1">\n' +
      iconEntries.map((e) => `  <icon name="${xmlAttr(e.atakName)}"/>\n`).join("") +
      "</iconset>\n";
    entries.push({ path: "iconset.xml", data: Buffer.from(xml, "utf8") });
  }
  entries.push(...iconEntries.map((e) => ({ path: e.path, data: e.data })));

  return {
    success: true,
    fileName: `${name}.zip`,
    buffer: buildStoredZip(entries),
  };
}

function xmlAttr(s) {
  return String(s == null ? "" : s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

// -------------------------------------------------------------------------
// Usage cross-reference (which saved datasets reference which icon set)
// -------------------------------------------------------------------------

/**
 * Every iconset name and uid a single saved dataset record refers to, across both the
 * configurator's internal state (state.cfg.symbology) and the plugin-facing exported_config
 * (compact "sym" shape). Both are checked because a set can be referenced by *name* in the
 * internal state while the exported config only carries the resolved "{uid}/{group}/{file}"
 * usericonPath — matching either one counts as in-use.
 */
function collectIconsetRefs(record) {
  const names = new Set();
  const uids = new Set();
  const addName = (n) => { if (n) names.add(String(n)); };
  /** A usericonPath's first segment is the iconset uid (spec §5.4). */
  const addPath = (p) => {
    if (!p) return;
    const first = String(p).split("/")[0];
    if (first) uids.add(first);
  };

  const s = (((record.state || {}).cfg) || {}).symbology || {};
  addName((s.icon || {}).iconset);
  (((s.advanced || {}).valueSymbols) || []).forEach((v) => addName((v.symbol || {}).iconset));
  (((s.advanced || {}).rules) || []).forEach((r) => addName((r.symbol || {}).iconset));

  const ec = record.exported_config || {};
  const sym = ec.sym || {};
  addName(sym.is);
  addPath(sym.up);
  (sym.vs || []).forEach((v) => { addName(v.is); addPath(v.up); });
  (sym.r || []).forEach((r) => { addName(r.is); addPath(r.up); });
  if (ec.rendererOverride) {
    if (ec.rendererOverride.uid) uids.add(String(ec.rendererOverride.uid));
    addName(ec.rendererOverride.group);
  }

  return { names, uids };
}

/**
 * Maps each custom icon set to the saved datasets using it — backs the "this set is in use"
 * confirmation before a delete, and the usage column in the Icon Sets manager.
 *
 * Required as an argument rather than imported so this service keeps no dependency on the
 * datasets service (callers already hold it).
 *
 * @param {Array} datasetRecords full dataset records (need .state and .exported_config)
 * @returns {Object<string, {id:string,name:string}[]>} keyed by icon set name
 */
function computeIconsetUsage(datasetRecords) {
  const sets = readManifest();
  const usage = {};
  for (const set of sets) usage[set.name] = [];

  for (const rec of datasetRecords || []) {
    const { names, uids } = collectIconsetRefs(rec);
    for (const set of sets) {
      if (names.has(set.name) || (set.uid && uids.has(set.uid))) {
        usage[set.name].push({ id: rec.id, name: rec.name || "Untitled" });
      }
    }
  }
  return usage;
}

module.exports = {
  listCustomIconSets,
  addCustomIcons,
  registerArcgisSet,
  findSetByUid,
  deleteCustomIconSet,
  getCustomIconPath,
  buildIconsetZip,
  computeIconsetUsage,
};
