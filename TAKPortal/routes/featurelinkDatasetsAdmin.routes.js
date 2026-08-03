/**
 * FeatureLink Datasets — API backing the ported Display Configurator
 * (assets/featurelink-configurator/index.html). Mirrors
 * Infra-TAK/featurelink_displayconfig.py's Flask routes 1:1 (same paths,
 * same payload shapes).
 *
 * Open to any logged-in TAK Portal user (not gated by requirePermission in
 * server.js) — anyone can create a config, but reading, editing and deleting an
 * existing one is restricted to whoever created it, unless the requester holds
 * the full page.featurelink_configs permission (true admins can manage anyone's).
 * The Administration hub page (/featurelink-configs, listing/QR/delete for
 * every saved config) stays permission-gated separately in server.js.
 *
 * C-03: read access used to be missing entirely on GET /:id and GET /:id/file —
 * any logged-in user could pull another tenant's full record (including
 * `state.cfg`, `source_url` and anything pasted into them) and stream their raw
 * uploaded dataset bytes, which for CAP/SAR use is PII and operational location
 * data. Every route in this file now resolves an actor through
 * featurelinkAccess.service.js and checks it.
 */

const router = require("express").Router();

const datasetsSvc = require("../services/featurelinkDatasets.service");
const auditSvc = require("../services/auditLog.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");
const access = require("../services/featurelinkAccess.service");
const { requireCsrf } = require("../services/featurelinkCsrf.service");
const { noStore } = require("../services/featurelinkHttp.service");

router.use(noStore);

function auditUserOf(req) {
  return req.authentikUser;
}

/**
 * GET /api/featurelink/admin/datasets
 * Non-admins see only their own records. Previously every logged-in user got the whole list
 * including each record's `created_by` and `source_url`.
 */
router.get("/", (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const all = datasetsSvc.listDatasets();
    const visible = actor.isAdmin ? all : all.filter((d) => d.created_by && d.created_by === actor.username);
    res.json({
      ok: true,
      datasets: visible.map((d) => (actor.isAdmin ? d : { ...d, created_by: undefined })),
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * POST /api/featurelink/admin/datasets — body: { id?, name, source_type, source_url,
 *   file_name, state, exported_config?, file_content_b64? }
 */
router.post("/", requireCsrf, (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const data = req.body || {};
    if (!String(data.name || "").trim()) {
      return res.status(400).json({ ok: false, error: "name is required" });
    }

    const existingId = data.id ? String(data.id).trim() : "";
    if (existingId) {
      const existing = datasetsSvc.loadDataset(existingId);
      if (!existing) {
        // Appendix D §2.1: a client-chosen id for a record that does not exist used to skip the
        // ownership branch entirely and create the record at that id — letting a user pre-squat
        // the id another user's already-shared QR link will later resolve to.
        return res.status(400).json({
          ok: false,
          error: "Unknown config id. Omit `id` to create a new config; ids are assigned by the server.",
        });
      }
      if (!access.canAccessRecord(actor, existing)) {
        return res.status(403).json({ ok: false, error: "Only the creator (or an admin) can edit this config." });
      }
    }

    const record = datasetsSvc.saveDataset(data, actor.username);

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
    // Was `err.message` verbatim, which leaked absolute container paths and errno detail out of
    // the fs layer — the only handler in the file that did not use toSafeApiError.
    res.status(400).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/admin/datasets/:id — full record (state + exported_config)
 * DELETE /api/featurelink/admin/datasets/:id
 */
router.get("/:id", (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const record = datasetsSvc.loadDataset(req.params.id);
    if (!record) return res.status(404).json({ ok: false, error: "not found" });
    if (!access.canAccessRecord(actor, record)) {
      // 404 rather than 403: a 403 confirms the id exists, turning this into an enumeration
      // oracle over other tenants' config ids (which are shared as QR links).
      return res.status(404).json({ ok: false, error: "not found" });
    }
    res.json(record);
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

router.delete("/:id", requireCsrf, (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const record = datasetsSvc.loadDataset(req.params.id);
    if (!record) return res.status(404).json({ ok: false, error: "not found" });
    if (!access.canAccessRecord(actor, record)) {
      return res.status(403).json({ ok: false, error: "Only the creator (or an admin) can delete this config." });
    }

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
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const record = datasetsSvc.loadDataset(req.params.id);
    if (!record) return res.status(404).end();
    if (!access.canAccessRecord(actor, record)) return res.status(404).end();

    const filePath = datasetsSvc.getDatasetFilePath(req.params.id);
    if (!filePath) return res.status(404).end();

    // Was `inline; filename="${record.file_name}"` with no sanitization: a `"` in the stored name
    // spoofed the download filename and a CR/LF threw ERR_INVALID_CHAR inside sendFile() for a
    // trivially reachable unhandled 500. Serving it `inline` with no nosniff also made a stored
    // HTML payload same-origin XSS on a sniffing browser.
    const safeName = String(record.file_name || "dataset").replace(/[^a-zA-Z0-9._-]/g, "_").slice(0, 120) || "dataset";
    res.setHeader("Content-Disposition", `attachment; filename="${safeName}"`);
    res.setHeader("X-Content-Type-Options", "nosniff");
    res.setHeader("Content-Type", "application/octet-stream");

    // sendFile() reports errors asynchronously to next(err), outside any surrounding try/catch,
    // which on a portal with no error middleware is a hanging request. Handle it here.
    res.sendFile(filePath, (err) => {
      if (err && !res.headersSent) res.status(500).json({ ok: false, error: toSafeApiError(err) });
      else if (err) res.destroy();
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

module.exports = router;
