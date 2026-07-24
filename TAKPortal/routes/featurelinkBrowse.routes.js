/**
 * FeatureLink Configs — field-user browse/download API, backing the
 * /featurelink Onboarding page. Any logged-in TAK Portal user can reach this
 * (not just admins) — portalAuth.middleware.js's isAllowedNonAdminPath list
 * and permissions.registry.js's carve-outs are what let a non-admin user
 * through; by the time these handlers run, req.authentikUser is already
 * guaranteed set. No separate token/auth of our own — the user's normal
 * portal session (Authentik-backed) is the only gate.
 *
 * The FeatureLink ATAK/WinTAK plugin isn't a party to this at all: the user
 * downloads the config file here, in their browser, then opens/imports it
 * into the plugin the same way as any other exported FeatureLink display
 * config (see Infra-TAK/README.md's "Payload formats" section).
 */

const router = require("express").Router();
const path = require("path");

const configsSvc = require("../services/featurelinkConfigs.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");

/**
 * GET /api/featurelink/configs
 * Optional query: targetApp=ATAK|WinTAK (filters; entries tagged "Both" always included)
 */
router.get("/configs", (req, res) => {
  try {
    const wanted = String(req.query.targetApp || "").trim();
    let configs = configsSvc.listConfigs();
    if (wanted === "ATAK" || wanted === "WinTAK") {
      configs = configs.filter((c) => c.targetApp === "Both" || c.targetApp === wanted);
    }
    res.json({ configs });
  } catch (err) {
    res.status(500).json({ error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/configs/:id/download
 * File-backed entries: streams the file. URL-backed entries: redirects to the source URL.
 */
router.get("/configs/:id/download", (req, res) => {
  try {
    const { id } = req.params;
    const entry = configsSvc.getConfig(id);
    if (!entry) {
      return res.status(404).json({ error: "Config not found." });
    }
    if (entry.sourceType === "url") {
      return res.redirect(302, entry.sourceUrl);
    }
    const filePath = configsSvc.getConfigFilePath(id);
    if (!filePath) {
      return res.status(404).json({ error: "Config file not found." });
    }
    const filename = path.basename(filePath);
    res.setHeader("Content-Disposition", `attachment; filename="${filename}"`);
    res.sendFile(filePath);
  } catch (err) {
    res.status(500).json({ error: toSafeApiError(err) });
  }
});

module.exports = router;
