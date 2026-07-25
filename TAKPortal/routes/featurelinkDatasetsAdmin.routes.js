/**
 * FeatureLink Datasets — Admin API backing the ported Display Configurator
 * (assets/featurelink-configurator/index.html). Mirrors
 * Infra-TAK/featurelink_displayconfig.py's Flask routes 1:1 (same paths,
 * same payload shapes) — mounted with requirePermission("page.featurelink_configs")
 * in server.js.
 */

const router = require("express").Router();

const datasetsSvc = require("../services/featurelinkDatasets.service");
const auditSvc = require("../services/auditLog.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");

function auditUserOf(req) {
  return req.authentikUser;
}

/**
 * GET /api/featurelink/admin/datasets
 * POST /api/featurelink/admin/datasets — body: { id?, name, source_type, source_url,
 *   file_name, state, exported_config?, file_content_b64? }
 */
router.get("/", (req, res) => {
  try {
    res.json({ ok: true, datasets: datasetsSvc.listDatasets() });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

router.post("/", (req, res) => {
  try {
    const data = req.body || {};
    if (!String(data.name || "").trim()) {
      return res.status(400).json({ ok: false, error: "name is required" });
    }
    const record = datasetsSvc.saveDataset(data);

    auditSvc.logEvent({
      actor: auditUserOf(req),
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_DATASET_SAVED",
      targetType: "featurelink_dataset",
      targetId: record.id,
      details: { name: record.name, sourceType: record.source_type },
    });

    res.json(record);
  } catch (err) {
    res.status(400).json({ ok: false, error: err.message || "save failed" });
  }
});

/**
 * GET /api/featurelink/admin/datasets/:id — full record (state + exported_config)
 * DELETE /api/featurelink/admin/datasets/:id
 */
router.get("/:id", (req, res) => {
  try {
    const record = datasetsSvc.loadDataset(req.params.id);
    if (!record) return res.status(404).json({ ok: false, error: "not found" });
    res.json(record);
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

router.delete("/:id", (req, res) => {
  try {
    datasetsSvc.deleteDataset(req.params.id);

    auditSvc.logEvent({
      actor: auditUserOf(req),
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_DATASET_DELETED",
      targetType: "featurelink_dataset",
      targetId: req.params.id,
      details: {},
    });

    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/admin/datasets/:id/file — raw uploaded file bytes (file-sourced datasets)
 */
router.get("/:id/file", (req, res) => {
  try {
    const filePath = datasetsSvc.getDatasetFilePath(req.params.id);
    if (!filePath) return res.status(404).end();
    const record = datasetsSvc.loadDataset(req.params.id) || {};
    res.sendFile(filePath, {
      headers: record.file_name ? { "Content-Disposition": `inline; filename="${record.file_name}"` } : {},
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

module.exports = router;
