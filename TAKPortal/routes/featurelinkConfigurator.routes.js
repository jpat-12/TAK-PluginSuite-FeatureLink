/**
 * Serves the ported FeatureLink Display Configurator (assets/featurelink-configurator/)
 * — the same tool infra-TAK runs, copied in as-is except for its three
 * hardcoded URL references (see index.html's edit comments). Mounted at
 * /featurelink-configs/configurator with requirePermission("page.featurelink_configs")
 * in server.js.
 */

const express = require("express");
const path = require("path");

const router = express.Router();
const ASSETS_DIR = path.join(__dirname, "..", "assets", "featurelink-configurator");

router.get(["/", ""], (req, res) => {
  res.setHeader("Cache-Control", "no-store, no-cache, must-revalidate");
  res.sendFile(path.join(ASSETS_DIR, "index.html"));
});

router.use("/icons", express.static(path.join(ASSETS_DIR, "icons")));

module.exports = router;
