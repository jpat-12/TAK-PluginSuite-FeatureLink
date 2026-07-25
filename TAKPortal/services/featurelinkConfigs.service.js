/**
 * FeatureLink Configs — catalog of display configs an admin preps for field
 * users to browse and download from TAK Portal.
 *
 * Storage lives under data/featurelink-configs/ (inside the tak_portal_data
 * volume, so it survives container rebuilds): catalog.json for metadata,
 * files/ for uploaded config files.
 *
 * "url" entries carry an optional `display` block — the same compact sym/lbl/
 * popup schema the FeatureLink ATAK plugin's Mode 2 QR payload already uses
 * (see DisplayConfig.fromJson in the plugin). buildDownloadPayload() wraps it
 * into that exact {"v":2,"url",...} shape, so what a field user downloads (or
 * sends to the plugin via the Open in ATAK deep link) needs no translation on
 * the plugin side — it's the same schema the QR scanner already parses.
 * "file" entries have no display config; they're a raw file passthrough.
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const DATA_DIR = path.join(__dirname, "..", "data", "featurelink-configs");
const FILES_DIR = path.join(DATA_DIR, "files");
const CATALOG_PATH = path.join(DATA_DIR, "catalog.json");

function ensureDirs() {
  if (!fs.existsSync(FILES_DIR)) fs.mkdirSync(FILES_DIR, { recursive: true });
}

function readCatalog() {
  ensureDirs();
  if (!fs.existsSync(CATALOG_PATH)) return [];
  try {
    const raw = fs.readFileSync(CATALOG_PATH, "utf-8");
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch (_) {
    return [];
  }
}

function writeCatalog(list) {
  ensureDirs();
  fs.writeFileSync(CATALOG_PATH, JSON.stringify(list, null, 2), "utf-8");
}

function listConfigs() {
  return readCatalog().map(publicShape);
}

function publicShape(entry) {
  return {
    id: entry.id,
    name: entry.name,
    description: entry.description || "",
    targetApp: entry.targetApp || "Both",
    sourceType: entry.sourceType,
    sourceUrl: entry.sourceType === "url" ? entry.sourceUrl : undefined,
    filename: entry.sourceType === "file" ? entry.filename : undefined,
    display: entry.sourceType === "url" ? entry.display || null : undefined,
    createdBy: entry.createdBy,
    createdAt: entry.createdAt,
    updatedAt: entry.updatedAt,
  };
}

function getConfig(id) {
  return readCatalog().find((c) => c.id === id) || null;
}

/** Only pass through the handful of top-level keys the plugin's parser understands. */
function sanitizeDisplay(display) {
  if (!display || typeof display !== "object") return null;
  const out = {};
  if (display.layer && typeof display.layer === "object") {
    out.layer = {
      opacity: typeof display.layer.opacity === "number" ? display.layer.opacity : 1,
      visible: display.layer.visible !== false,
    };
  }
  if (display.sym && typeof display.sym === "object") out.sym = display.sym;
  if (display.lbl && typeof display.lbl === "object") out.lbl = display.lbl;
  if (display.popup && typeof display.popup === "object") out.popup = display.popup;
  return Object.keys(out).length ? out : null;
}

/**
 * uploadedFile: { path, originalname } (from multer) — only when sourceType === "file".
 * display: optional compact {layer,sym,lbl,popup} — only meaningful when sourceType === "url".
 */
function createConfig({ name, description, targetApp, sourceType, sourceUrl, uploadedFile, display, createdBy }) {
  const cleanName = String(name || "").trim();
  if (!cleanName) return { success: false, error: "Name is required." };
  if (sourceType !== "file" && sourceType !== "url") {
    return { success: false, error: "sourceType must be 'file' or 'url'." };
  }
  if (sourceType === "url" && !String(sourceUrl || "").trim()) {
    return { success: false, error: "sourceUrl is required when sourceType is 'url'." };
  }
  if (sourceType === "file" && !uploadedFile) {
    return { success: false, error: "A file upload is required when sourceType is 'file'." };
  }

  const id = crypto.randomUUID();
  const now = new Date().toISOString();
  const entry = {
    id,
    name: cleanName,
    description: String(description || "").trim(),
    targetApp: ["ATAK", "WinTAK", "Both"].includes(targetApp) ? targetApp : "Both",
    sourceType,
    createdBy: createdBy || null,
    createdAt: now,
    updatedAt: now,
  };

  if (sourceType === "url") {
    entry.sourceUrl = String(sourceUrl).trim();
    entry.display = sanitizeDisplay(display);
  } else {
    ensureDirs();
    const safeName = (uploadedFile.originalname || "config.json").replace(/[^a-zA-Z0-9._-]/g, "_");
    const filename = `${id}_${safeName}`;
    fs.copyFileSync(uploadedFile.path, path.join(FILES_DIR, filename));
    entry.filename = filename;
  }

  const list = readCatalog();
  list.push(entry);
  writeCatalog(list);
  return { success: true, config: publicShape(entry) };
}

function updateConfig(id, { name, description, targetApp, sourceUrl, display }) {
  const list = readCatalog();
  const idx = list.findIndex((c) => c.id === id);
  if (idx === -1) return { success: false, error: "Config not found." };

  if (name !== undefined) list[idx].name = String(name).trim();
  if (description !== undefined) list[idx].description = String(description).trim();
  if (targetApp !== undefined && ["ATAK", "WinTAK", "Both"].includes(targetApp)) {
    list[idx].targetApp = targetApp;
  }
  if (list[idx].sourceType === "url") {
    if (sourceUrl !== undefined && String(sourceUrl).trim()) {
      list[idx].sourceUrl = String(sourceUrl).trim();
    }
    if (display !== undefined) {
      list[idx].display = sanitizeDisplay(display);
    }
  }
  list[idx].updatedAt = new Date().toISOString();

  writeCatalog(list);
  return { success: true, config: publicShape(list[idx]) };
}

function deleteConfig(id) {
  const list = readCatalog();
  const idx = list.findIndex((c) => c.id === id);
  if (idx === -1) return { success: false, error: "Config not found." };

  const [removed] = list.splice(idx, 1);
  if (removed.sourceType === "file" && removed.filename) {
    const p = path.join(FILES_DIR, removed.filename);
    try {
      if (fs.existsSync(p)) fs.unlinkSync(p);
    } catch (_) {}
  }
  writeCatalog(list);
  return { success: true };
}

/** Returns an absolute file path to stream, or null if this entry isn't file-backed. */
function getConfigFilePath(id) {
  const entry = getConfig(id);
  if (!entry || entry.sourceType !== "file" || !entry.filename) return null;
  const p = path.join(FILES_DIR, entry.filename);
  return fs.existsSync(p) ? p : null;
}

/**
 * Builds the plugin-ready Mode 2 JSON for a "url" entry:
 * {"v":2,"url":...,"layer":{"name","opacity","visible"},"sym":?,"lbl":?,"popup":?}
 * — the exact schema DisplayConfig.fromJson() in the ATAK plugin parses. Returns
 * null for "file" entries (those are served as a raw passthrough instead).
 */
function buildDownloadPayload(id) {
  const entry = getConfig(id);
  if (!entry || entry.sourceType !== "url") return null;
  const d = entry.display || {};
  const payload = {
    v: 2,
    url: entry.sourceUrl,
    layer: {
      name: entry.name,
      opacity: (d.layer && typeof d.layer.opacity === "number") ? d.layer.opacity : 1,
      visible: !d.layer || d.layer.visible !== false,
    },
  };
  if (d.sym) payload.sym = d.sym;
  if (d.lbl) payload.lbl = d.lbl;
  if (d.popup) payload.popup = d.popup;
  return payload;
}

module.exports = {
  listConfigs,
  getConfig,
  createConfig,
  updateConfig,
  deleteConfig,
  getConfigFilePath,
  buildDownloadPayload,
};
