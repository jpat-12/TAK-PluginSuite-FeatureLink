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

module.exports = {
  listCustomIconSets,
  addCustomIcons,
  registerArcgisSet,
  findSetByUid,
  deleteCustomIconSet,
  getCustomIconPath,
};
