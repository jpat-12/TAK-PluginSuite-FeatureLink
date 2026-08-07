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

// Appendix D §2.3 — caps on the two unbounded client-supplied blobs.
const MAX_FILE_BYTES = 25 * 1024 * 1024;
const MAX_FILE_B64_CHARS = Math.ceil((MAX_FILE_BYTES / 3) * 4) + 4;
const MAX_STATE_BYTES = 8 * 1024 * 1024;
const B64_RE = /^[A-Za-z0-9+/\r\n]*={0,2}$/;

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

/**
 * True if `username` created this record.
 *
 * C-03 / Appendix D §2.1: this used to return true when `created_by` was unset, so every record
 * written before ownership tracking existed — and every record written by the Flask module, which
 * has no ownership model at all — was editable and deletable by *anyone*. That is fail-open
 * authorization on exactly the legacy data most likely to matter. An unowned record is now owned
 * by nobody, which `featurelinkAccess.canAccessRecord()` resolves to admin-only.
 */
function isOwnedBy(record, username) {
  if (!record || !record.created_by || !username) return false;
  return record.created_by === username;
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
  // `state` is the configurator's whole internal representation and was stored with no bound at
  // all — an unauthenticated-tier user could park an arbitrarily large object in the store, and
  // every listDatasets() call then re-reads and re-parses it (Appendix D §2.3/§2.4).
  if (payload.state !== undefined && payload.state !== null) {
    let stateBytes;
    try {
      stateBytes = Buffer.byteLength(JSON.stringify(payload.state), "utf-8");
    } catch (_) {
      throw new Error("state is not serializable JSON");
    }
    if (stateBytes > MAX_STATE_BYTES) {
      throw new Error(`Config state is too large (limit ${Math.floor(MAX_STATE_BYTES / (1024 * 1024))} MB).`);
    }
  }

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
    // Appendix D §2.3: `Buffer.from(x, "base64")` never throws — it silently drops invalid
    // characters — so the old try/catch was dead code and the "invalid file_content_b64" error
    // was unreachable. Validate the encoding explicitly and cap the decoded size; previously any
    // logged-in user could write arbitrary bytes of arbitrary length to disk.
    const b64 = String(payload.file_content_b64);
    if (b64.length > MAX_FILE_B64_CHARS) {
      throw new Error(`Uploaded file is too large (limit ${Math.floor(MAX_FILE_BYTES / (1024 * 1024))} MB).`);
    }
    if (!B64_RE.test(b64)) {
      throw new Error("invalid file_content_b64");
    }
    const raw = Buffer.from(b64, "base64");
    if (raw.length > MAX_FILE_BYTES) {
      throw new Error(`Uploaded file is too large (limit ${Math.floor(MAX_FILE_BYTES / (1024 * 1024))} MB).`);
    }
    writeFileAtomic(dataPath(id), raw);
  }

  // Atomic: a crash mid-write used to truncate record.json, after which loadDataset()'s bare
  // catch made the dataset silently vanish from every listing while its files stayed on disk.
  writeFileAtomic(recordPath(id), Buffer.from(JSON.stringify(record), "utf-8"));
  record.id = id;
  return record;
}

/** Write via a temp file + rename so a reader never observes a partial record. */
function writeFileAtomic(target, buf) {
  const tmp = `${target}.tmp-${crypto.randomBytes(6).toString("hex")}`;
  fs.writeFileSync(tmp, buf);
  fs.renameSync(tmp, target);
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
