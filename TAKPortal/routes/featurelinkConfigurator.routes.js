/**
 * Serves the ported FeatureLink Display Configurator (assets/featurelink-configurator/).
 *
 * NOTE (Appendix D §1.3): the previous comment here claimed this asset is the same file
 * Infra-TAK serves "except for its three hardcoded URL references". That is false — the two
 * copies diverge by roughly 2,000 lines (the Portal fork adds the staged wizard, Web Map bulk
 * import, the Icon Sets manager and auto-symbology). Treat them as two products; a fix to one
 * does NOT reach the other.
 *
 * C-10: this route issues the double-submit CSRF cookie the configurator's own fetch() calls
 * echo back in X-FeatureLink-CSRF.
 */

const express = require("express");
const path = require("path");

const router = express.Router();
const { issueCsrfCookie } = require("../services/featurelinkCsrf.service");
const { pageSecurityHeaders } = require("../services/featurelinkHttp.service");
const ASSETS_DIR = path.join(__dirname, "..", "assets", "featurelink-configurator");

router.get(["/", ""], pageSecurityHeaders, issueCsrfCookie, (req, res) => {
  res.setHeader("Cache-Control", "no-store, no-cache, must-revalidate");
  res.sendFile(path.join(ASSETS_DIR, "index.html"));
});

// The icon tree is immutable content addressed by name; the parent page is no-store, but these
// 3,771 files must be cacheable or every picker open re-downloads a 208 KB manifest plus icons.
router.use(
  "/icons",
  express.static(path.join(ASSETS_DIR, "icons"), {
    maxAge: "7d",
    dotfiles: "deny",
    index: false,
    setHeaders(res) {
      res.setHeader("X-Content-Type-Options", "nosniff");
    },
  })
);

module.exports = router;
