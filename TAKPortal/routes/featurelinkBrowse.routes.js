/**
 * FeatureLink Datasets — field-user browse/download API, backing the
 * /featurelink Onboarding page. Any logged-in TAK Portal user can reach this
 * (not just admins) — portalAuth.middleware.js's isAllowedNonAdminPath list
 * and permissions.registry.js's carve-outs are what let a non-admin user
 * through; by the time these handlers run, req.authentikUser is already
 * guaranteed set. No separate token/auth of our own — the user's normal
 * portal session (Authentik-backed) is the only gate.
 *
 * Reads the same dataset store the ported FeatureLink Display Configurator
 * (Administration → FeatureLink Configs) saves to. Serves each dataset's
 * exported_config verbatim — the Mode 3 plugin-ready JSON the configurator
 * already built at save time — no server-side re-derivation.
 */

const router = require("express").Router();

const datasetsSvc = require("../services/featurelinkDatasets.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");

/** GET /api/featurelink/configs */
router.get("/configs", (req, res) => {
  try {
    const configs = datasetsSvc.listDatasets().map((d) => ({
      id: d.id,
      name: d.name,
      sourceType: d.source_type,
      updatedAt: d.updated_at,
      hasLiveLayer: !!d.has_export,
      fieldCount: d.field_count,
      featureCount: d.feature_count,
    }));
    res.json({ configs });
  } catch (err) {
    res.status(500).json({ error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/configs/:id/download
 * Returns the saved exported_config (Mode 3 plugin-ready JSON) verbatim.
 */
router.get("/configs/:id/download", (req, res) => {
  try {
    const record = datasetsSvc.loadDataset(req.params.id);
    if (!record) {
      return res.status(404).json({ error: "Config not found." });
    }
    if (!record.exported_config) {
      return res.status(404).json({ error: "This config has no exported display styling yet." });
    }
    const safeName = (record.name || "config").replace(/[^a-zA-Z0-9._-]/g, "_");
    res.setHeader("Content-Disposition", `attachment; filename="${safeName}.json"`);
    res.json(record.exported_config);
  } catch (err) {
    res.status(500).json({ error: toSafeApiError(err) });
  }
});

module.exports = router;
