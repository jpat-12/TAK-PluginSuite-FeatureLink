/**
 * FeatureLink Configs — catalog of display configs an admin preps for field
 * devices to list/download from the FeatureLink ATAK/WinTAK plugin.
 *
 * Storage lives under data/featurelink-configs/ (inside the tak_portal_data
 * volume, so it survives container rebuilds): catalog.json for metadata,
 * files/ for uploaded config files. URL-sourced entries store just the URL
 * and are re-fetched by the plugin directly — nothing to keep in files/.
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
    createdBy: entry.createdBy,
    createdAt: entry.createdAt,
    updatedAt: entry.updatedAt,
  };
}

function getConfig(id) {
  return readCatalog().find((c) => c.id === id) || null;
}

/**
 * uploadedFile: { path, originalname } (from multer) — only when sourceType === "file".
 */
function createConfig({ name, description, targetApp, sourceType, sourceUrl, uploadedFile, createdBy }) {
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

function updateConfig(id, { name, description, targetApp }) {
  const list = readCatalog();
  const idx = list.findIndex((c) => c.id === id);
  if (idx === -1) return { success: false, error: "Config not found." };

  if (name !== undefined) list[idx].name = String(name).trim();
  if (description !== undefined) list[idx].description = String(description).trim();
  if (targetApp !== undefined && ["ATAK", "WinTAK", "Both"].includes(targetApp)) {
    list[idx].targetApp = targetApp;
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

module.exports = {
  listConfigs,
  getConfig,
  createConfig,
  updateConfig,
  deleteConfig,
  getConfigFilePath,
};
