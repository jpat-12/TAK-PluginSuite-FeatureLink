/**
 * FeatureLink public config download — the target of the Display Configurator's
 * "Config Download Link" QR mode. Mounted at /public/featurelink.
 *
 * A QR that carries the whole display config JSON gets huge (and unscannable)
 * fast. This lets the QR carry only a short URL instead:
 *
 *   https://takportal.<fqdn>/public/featurelink/<id>.json
 *
 * The FeatureLink ATAK plugin already fetches any bare-URL QR it scans and
 * applies the response body as a Mode 1/2 config (QrHelper.fetchSavedDatasetLink).
 *
 * No login: the plugin is a plain HTTP client with no Authentik session, so
 * this path sits under /public/* — which infra-TAK's Caddy already serves
 * without forward_auth (TAK Portal's docs/caddy.md @public matcher) — and
 * install.sh adds it to server.js's isPublicPortalBypass() and the
 * permissions registry. The unguessable 128-bit dataset id is the only gate;
 * what's served is display styling + the layer URL, the same thing a
 * full-payload QR would have printed in plain sight.
 */

const router = require("express").Router();

const datasetsSvc = require("../services/featurelinkDatasets.service");

/** GET /public/featurelink/:id(.json) */
router.get("/:id", (req, res) => {
  res.setHeader("Cache-Control", "no-store");
  res.setHeader("Access-Control-Allow-Origin", "*");
  try {
    const id = String(req.params.id || "").replace(/\.json$/i, "");
    const record = datasetsSvc.loadDataset(id);
    const cfg = record && record.share_config;
    if (!cfg) {
      return res.status(404).json({ ok: false, error: "not found" });
    }
    const safeName = (record.name || "featurelink-config").replace(/[^a-zA-Z0-9._-]/g, "_");
    res.setHeader("Content-Disposition", `attachment; filename="${safeName}.json"`);
    return res.json(cfg);
  } catch (_) {
    return res.status(500).json({ ok: false, error: "internal error" });
  }
});

module.exports = router;
