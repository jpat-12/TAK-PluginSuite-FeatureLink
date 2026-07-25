/**
 * Custom icon sets — lets an admin/user upload their own icon images into the
 * Display Configurator's icon picker, alongside the bundled iconsets shipped
 * in assets/featurelink-configurator/icons/.
 *
 * Storage: data/featurelink-configs/custom-icons/<setName>/<file>, inside the
 * tak_portal_data volume so uploads survive rebuilds. manifest.json (in the
 * same directory) tracks {name, icons: [filenames], created_by, created_at}
 * per set — same shape the configurator's bundled icons/manifest.json uses
 * for an iconset entry, so the frontend can merge both lists identically.
 *
 * KNOWN LIMITATION: uploaded icons have no corresponding ATAK iconset UID
 * (resolveUsericonPath() in index.html only resolves bundled sets against
 * icons/manifest.json's uid mappings). A custom icon previews fine in the
 * configurator, but the ATAK plugin currently has no code path to fetch and
 * render an arbitrary uploaded bitmap as a marker icon — selecting one will
 * fall back to the plugin's default marker styling until that's built.
 */

const fs = require("fs");
const path = require("path");
const unzipper = require("unzipper");

const DATA_DIR = path.join(__dirname, "..", "data", "featurelink-configs");
const CUSTOM_ICONS_DIR = path.join(DATA_DIR, "custom-icons");
const MANIFEST_PATH = path.join(CUSTOM_ICONS_DIR, "manifest.json");

const NAME_RE = /[^a-zA-Z0-9 _-]/g;
const FILE_RE = /[^a-zA-Z0-9._-]/g;
const IMAGE_EXT_RE = /\.(png|jpe?g|gif|bmp|webp|svg)$/i;
const ZIP_ENTRY_LIMIT = 500; // guards against zip-bomb-style entry counts
const ZIP_ENTRY_MAX_BYTES = 5 * 1024 * 1024; // per-icon uncompressed size cap

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

/** List of {name, icons, created_by, created_at} — same iconset shape the configurator expects. */
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
 * Unpacks a .zip iconset (the common ATAK iconset packaging — a flat or nested folder
 * of icon images, sometimes alongside an iconset.xml the plugin doesn't need here) into
 * individual icon files. Non-image entries (manifests, __MACOSX junk, directories) are
 * skipped; nested folder structure is flattened since custom sets are stored flat.
 */
async function extractZipIcons(zipPath, setDir, entry, skipped) {
  const directory = await unzipper.Open.file(zipPath);
  let extracted = 0;
  for (const zipEntry of directory.files) {
    if (zipEntry.type !== "File") continue;
    if (extracted >= ZIP_ENTRY_LIMIT) break;
    const baseName = path.basename(zipEntry.path);
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
    extracted++;
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
    entry = { name, icons: [], created_by: actorUsername || null, created_at: new Date().toISOString() };
    sets.push(entry);
  }

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
  deleteCustomIconSet,
  getCustomIconPath,
};
