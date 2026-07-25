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

const DATA_DIR = path.join(__dirname, "..", "data", "featurelink-configs");
const CUSTOM_ICONS_DIR = path.join(DATA_DIR, "custom-icons");
const MANIFEST_PATH = path.join(CUSTOM_ICONS_DIR, "manifest.json");

const NAME_RE = /[^a-zA-Z0-9 _-]/g;
const FILE_RE = /[^a-zA-Z0-9._-]/g;

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

/**
 * Adds (or appends to, if the set name already exists) a custom icon set.
 * files: [{ path, originalname }] (from multer's disk storage).
 */
function addCustomIcons(setName, files, actorUsername) {
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

  for (const file of files) {
    const safeName = (file.originalname || "icon.png").replace(FILE_RE, "_");
    fs.copyFileSync(file.path, path.join(setDir, safeName));
    if (!entry.icons.includes(safeName)) entry.icons.push(safeName);
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
