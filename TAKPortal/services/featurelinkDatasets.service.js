/**
 * FeatureLink Datasets — persistence for the ported FeatureLink Display
 * Configurator (assets/featurelink-configurator/index.html), the same tool
 * that runs inside infra-TAK (Infra-TAK/featurelink_displayconfig.py) —
 * ported here 1:1 (record shape, storage layout, API contract) so the
 * copied index.html needed only its hardcoded URL paths changed, no logic.
 *
 * Storage: data/featurelink-configs/datasets/<id>/record.json (+ data.bin for
 * file-sourced datasets), inside the tak_portal_data volume so it survives
 * container rebuilds.
 *
 * record.state is the configurator's own internal representation (raw cfg +
 * parsed fields) — round-tripped as-is so re-opening a saved dataset in the
 * configurator restores it exactly, no lossy conversion.
 *
 * record.exported_config is the plugin-ready Mode 3 ("_v"/"_version") JSON
 * the configurator already builds for its "Export Config JSON" button —
 * saveDataset() in index.html sends it alongside state so TAK Portal can
 * serve it straight to field users (download / Open in ATAK) without any
 * server-side re-derivation of Esri-renderer-to-plugin-schema logic.
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const DATA_DIR = path.join(__dirname, "..", "data", "featurelink-configs");
const DATASETS_DIR = path.join(DATA_DIR, "datasets");

const ID_RE = /[^a-fA-F0-9]/g;

function cleanId(id) {
  return String(id || "").replace(ID_RE, "");
}

function datasetDir(id) {
  return path.join(DATASETS_DIR, id);
}

function recordPath(id) {
  return path.join(datasetDir(id), "record.json");
}

function dataPath(id) {
  return path.join(datasetDir(id), "data.bin");
}

function listDatasets() {
  if (!fs.existsSync(DATASETS_DIR)) return [];
  const out = [];
  for (const entry of fs.readdirSync(DATASETS_DIR)) {
    const record = loadDataset(entry);
    if (!record) continue;
    out.push({
      id: record.id,
      name: record.name || "Untitled",
      group: record.group || "",
      source_type: record.source_type || "file",
      source_url: record.source_url || "",
      file_name: record.file_name || "",
      field_count: ((record.state || {}).fields || []).length,
      feature_count: record.feature_count || 0,
      has_export: !!record.exported_config,
      created_by: record.created_by || null,
      created_at: record.created_at || "",
      updated_at: record.updated_at || "",
    });
  }
  out.sort((a, b) => (b.updated_at || "").localeCompare(a.updated_at || ""));
  return out;
}

/** True if `username` created this record, or the record predates ownership tracking
 * (created_by unset) — treated as unowned/editable-by-anyone rather than locked out. */
function isOwnedBy(record, username) {
  return !record.created_by || record.created_by === username;
}

function loadDataset(id) {
  const cleaned = cleanId(id);
  if (!cleaned) return null;
  const rp = recordPath(cleaned);
  if (!fs.existsSync(rp)) return null;
  try {
    const record = JSON.parse(fs.readFileSync(rp, "utf-8"));
    record.id = cleaned;
    return record;
  } catch (_) {
    return null;
  }
}

/**
 * payload: { id?, name, source_type, source_url, file_name, state, exported_config?, file_content_b64? }
 * actorUsername: the caller's identity (server-determined — never trust a client-supplied
 * "created_by"), recorded as owner on first create only; preserved as-is on update.
 */
function saveDataset(payload, actorUsername) {
  const id = cleanId(payload.id) || crypto.randomUUID().replace(/-/g, "");
  const dir = datasetDir(id);
  fs.mkdirSync(dir, { recursive: true });

  const existing = loadDataset(id) || {};
  const now = new Date().toISOString();
  const sourceType = payload.source_type === "url" ? "url" : "file";

  const record = {
    name: String(payload.name || "Untitled").trim().slice(0, 200),
    // Set when this dataset was created as part of a multi-layer Web Map import the user
    // chose to "keep together" — datasets sharing a group are still independent records
    // (own source_url/symbology/etc.), just visually clustered on the admin hub page.
    group: String(payload.group !== undefined ? payload.group : existing.group || "").trim().slice(0, 200),
    source_type: sourceType,
    source_url: String(payload.source_url || "").trim(),
    file_name: String(payload.file_name || "").trim(),
    feature_count: payload.feature_count !== undefined ? Number(payload.feature_count) || 0 : existing.feature_count || 0,
    state: payload.state || {},
    exported_config: payload.exported_config || existing.exported_config || null,
    created_by: existing.created_by || actorUsername || null,
    created_at: existing.created_at || now,
    updated_at: now,
  };

  if (sourceType === "file" && payload.file_content_b64) {
    let raw;
    try {
      raw = Buffer.from(payload.file_content_b64, "base64");
    } catch (_) {
      throw new Error("invalid file_content_b64");
    }
    fs.writeFileSync(dataPath(id), raw);
  }

  fs.writeFileSync(recordPath(id), JSON.stringify(record), "utf-8");
  record.id = id;
  return record;
}

function deleteDataset(id) {
  const cleaned = cleanId(id);
  if (!cleaned) return;
  const dir = datasetDir(cleaned);
  if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true });
}

/** Returns an absolute path to the raw uploaded file, or null if missing/not file-sourced. */
function getDatasetFilePath(id) {
  const cleaned = cleanId(id);
  if (!cleaned) return null;
  const p = dataPath(cleaned);
  return fs.existsSync(p) ? p : null;
}

module.exports = {
  listDatasets,
  loadDataset,
  saveDataset,
  deleteDataset,
  getDatasetFilePath,
  isOwnedBy,
};
