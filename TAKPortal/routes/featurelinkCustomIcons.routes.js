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
const datasetsSvc = require("../services/featurelinkDatasets.service");
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
 * Body: { url, field?, token?, renderer? }. Reads a FeatureServer layer's renderer, extracts its
 * picture-marker (esriPMS) symbols, and registers them as an icon set whose uid/group/
 * filenames are computed per AUTO-ICONSET-SPEC.md — string-identical to what ATAK/WinTAK/
 * CloudTAK independently produce for the same layer. This is the automatic "one-link" hook:
 * the configurator calls it the moment a layer is loaded from a URL, not behind a button.
 *
 * `renderer`, when supplied, is used instead of re-fetching the layer's own default — the
 * configurator sends this when the layer came from a Web Map with its own per-layer style
 * override (§2.3), so extraction matches what's actually being imported rather than the
 * service's generic default.
 */
router.post("/from-arcgis", async (req, res) => {
  try {
    const { url, field, token, renderer } = req.body || {};
    if (!url) return res.status(400).json({ ok: false, error: "A FeatureServer layer URL is required." });
    const actorUsername = req.authentikUser && req.authentikUser.username;
    const result = await arcgisIconsetSvc.generateFromArcgis({ sourceUrl: url, field, token, actorUsername, renderer });
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

/**
 * GET /api/featurelink/admin/custom-icons/usage — every icon set plus which saved datasets
 * reference it, backing the Icon Sets manager's usage column and its delete confirmation.
 * Registered BEFORE the /:name/:file catch-all so "usage" isn't read as a set name.
 */
router.get("/usage", (req, res) => {
  try {
    const sets = customIconsSvc.listCustomIconSets();
    const records = datasetsSvc.listDatasets().map((d) => datasetsSvc.loadDataset(d.id)).filter(Boolean);
    const usage = customIconsSvc.computeIconsetUsage(records);
    res.json({
      ok: true,
      iconsets: sets.map((s) => ({
        name: s.name,
        uid: s.uid || null,
        defaultGroup: s.defaultGroup || null,
        iconCount: (s.icons || []).length,
        source: s.source || "upload",
        sourceUrl: s.sourceUrl || null,
        created_by: s.created_by || null,
        created_at: s.created_at || null,
        usedBy: usage[s.name] || [],
      })),
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * DELETE /api/featurelink/admin/custom-icons/:name — pass ?force=1 to delete a set that saved
 * datasets still reference. Without it, an in-use set returns 409 plus the referencing dataset
 * list, so the confirmation can name them; the client-side prompt is a convenience, this is the
 * actual guard (deleting an in-use set makes those configs fall back to default markers on
 * every device that hasn't already installed the iconset).
 */
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

    const force = req.query.force === "1" || req.query.force === "true";
    if (!force) {
      const records = datasetsSvc.listDatasets().map((d) => datasetsSvc.loadDataset(d.id)).filter(Boolean);
      const usedBy = (customIconsSvc.computeIconsetUsage(records)[entry.name]) || [];
      if (usedBy.length) {
        return res.status(409).json({
          ok: false,
          inUse: true,
          usedBy,
          error: `"${entry.name}" is still used by ${usedBy.length} saved config(s).`,
        });
      }
    }

    const result = customIconsSvc.deleteCustomIconSet(req.params.name);
    if (!result.success) return res.status(404).json({ ok: false, error: result.error });
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/admin/custom-icons/:name/download — the set repackaged as an
 * ATAK-installable iconset zip (spec §7 layout), for handing to a device directly via
 * Settings > Import Content.
 *
 * Can't be shadowed by the /:name/:file icon route below despite sharing its shape: every real
 * icon file has an image extension (enforced by IMAGE_EXT_RE on upload), so a stored file named
 * exactly "download" can't exist.
 */
router.get("/:name/download", (req, res) => {
  try {
    const result = customIconsSvc.buildIconsetZip(req.params.name);
    if (!result.success) return res.status(404).json({ ok: false, error: result.error });
    res.setHeader("Content-Type", "application/zip");
    res.setHeader("Content-Disposition", `attachment; filename="${result.fileName.replace(/[^a-zA-Z0-9._-]/g, "_")}"`);
    res.send(result.buffer);
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
