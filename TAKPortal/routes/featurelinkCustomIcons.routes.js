/**
 * Custom icon set upload/browse for the Display Configurator's icon picker.
 * Open to any logged-in user (same tier as the dataset API) — mounted at
 * /api/featurelink/admin/custom-icons in server.js, no requirePermission
 * wrapper. Anyone can add a set; deleting one is restricted to its uploader
 * or a true admin (page.featurelink_configs), same ownership model as
 * featurelinkDatasetsAdmin.routes.js.
 */

const router = require("express").Router();
const path = require("path");
const fs = require("fs");
const multer = require("multer");

const customIconsSvc = require("../services/featurelinkCustomIcons.service");
const arcgisIconsetSvc = require("../services/featurelinkArcgisIconset.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");

const upload = multer({
  storage: multer.diskStorage({
    destination: (req, file, cb) => {
      const dir = path.join(__dirname, "..", "data", "uploads");
      if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
      cb(null, dir);
    },
    filename: (req, file, cb) => {
      const base = (file.originalname || "icon").replace(/[^a-zA-Z0-9._-]/g, "_");
      cb(null, `flicon_${Date.now()}_${Math.random().toString(16).slice(2)}_${base}`);
    },
  }),
  limits: { fileSize: 25 * 1024 * 1024, files: 200 }, // individual icons are tiny, but a zipped iconset can be a few MB
});

function cleanupTempFiles(files) {
  (files || []).forEach((f) => {
    try {
      if (f.path && fs.existsSync(f.path)) fs.unlinkSync(f.path);
    } catch (_) {}
  });
}

/** GET /api/featurelink/admin/custom-icons — list, same shape as a bundled iconset entry. */
router.get("/", (req, res) => {
  try {
    res.json({ ok: true, iconsets: customIconsSvc.listCustomIconSets() });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/** POST /api/featurelink/admin/custom-icons — multipart: name + icons (multiple files, .zip iconsets unpacked). */
router.post("/", upload.array("icons", 200), async (req, res) => {
  try {
    const name = req.body && req.body.name;
    const username = req.authentikUser && req.authentikUser.username;
    const result = await customIconsSvc.addCustomIcons(name, req.files, username);
    cleanupTempFiles(req.files);
    if (!result.success) return res.status(400).json({ ok: false, error: result.error });
    res.json({ ok: true, set: result.set });
  } catch (err) {
    cleanupTempFiles(req.files);
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * POST /api/featurelink/admin/custom-icons/from-arcgis
 * Body: { url, field?, token? }. Reads a FeatureServer layer's renderer, extracts its
 * picture-marker (esriPMS) symbols, and registers them as an icon set whose uid/group/
 * filenames are computed per AUTO-ICONSET-SPEC.md — string-identical to what ATAK/WinTAK/
 * CloudTAK independently produce for the same layer. This is the automatic "one-link" hook:
 * the configurator calls it the moment a layer is loaded from a URL, not behind a button.
 */
router.post("/from-arcgis", async (req, res) => {
  try {
    const { url, field, token } = req.body || {};
    if (!url) return res.status(400).json({ ok: false, error: "A FeatureServer layer URL is required." });
    const actorUsername = req.authentikUser && req.authentikUser.username;
    const result = await arcgisIconsetSvc.generateFromArcgis({ sourceUrl: url, field, token, actorUsername });
    if (!result.success) return res.status(400).json({ ok: false, error: result.error });
    res.json({
      ok: true,
      set: result.set,
      uid: result.uid,
      group: result.group,
      field: result.field,
      canonicalUrl: result.canonicalUrl,
      iconCount: result.iconCount,
      // For a caller building display-config symbology automatically (the configurator's
      // "Add Data" auto-import path), not just registering the set for later manual picking.
      rendererType: result.rendererType,
      valueMap: result.valueMap,
      defaultFilename: result.defaultFilename,
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/admin/custom-icons/by-uid/:uid — optional shared-repository lookup
 * (spec §10): resolve a set by its deterministic uid so a client that encountered an
 * unfamiliar iconsetpath uid in incoming CoT can find/install it without re-ingesting the
 * source. Registered BEFORE the /:name/:file catch-all so "by-uid" isn't read as a set name.
 */
router.get("/by-uid/:uid", (req, res) => {
  try {
    const set = customIconsSvc.findSetByUid(req.params.uid);
    if (!set) return res.status(404).json({ ok: false, error: "not found" });
    res.json({ ok: true, set });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/** DELETE /api/featurelink/admin/custom-icons/:name */
router.delete("/:name", (req, res) => {
  try {
    const sets = customIconsSvc.listCustomIconSets();
    const entry = sets.find((s) => s.name === req.params.name);
    if (!entry) return res.status(404).json({ ok: false, error: "not found" });

    const username = req.authentikUser && req.authentikUser.username;
    const isAdmin = typeof res.locals.perm === "function" && res.locals.perm("page.featurelink_configs");
    if (!isAdmin && entry.created_by && entry.created_by !== username) {
      return res.status(403).json({ ok: false, error: "Only the uploader (or an admin) can delete this icon set." });
    }

    const result = customIconsSvc.deleteCustomIconSet(req.params.name);
    if (!result.success) return res.status(404).json({ ok: false, error: result.error });
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/** GET /api/featurelink/admin/custom-icons/:name/:file — serves one icon image. */
router.get("/:name/:file", (req, res) => {
  try {
    const filePath = customIconsSvc.getCustomIconPath(req.params.name, req.params.file);
    if (!filePath) return res.status(404).end();
    res.sendFile(filePath);
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

module.exports = router;
