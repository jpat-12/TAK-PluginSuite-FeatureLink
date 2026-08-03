/**
 * Custom icon set upload/browse for the Display Configurator's icon picker.
 * Open to any logged-in user (same tier as the dataset API) — mounted at
 * /api/featurelink/admin/custom-icons in server.js, no requirePermission
 * wrapper. Anyone can create a set; appending to, regenerating, or deleting an
 * existing one is restricted to its uploader or a true admin
 * (page.featurelink_configs), same ownership model as
 * featurelinkDatasetsAdmin.routes.js.
 *
 * C-03: appends (POST /) and ArcGIS regeneration (POST /from-arcgis) had no
 * ownership check at all, and the latter rmSync'd the colliding set's directory.
 * C-10: every mutating verb now carries CSRF protection.
 * C-11: the upload path is rate-limited and quota'd on top of the in-service
 * decompression budget.
 */

const router = require("express").Router();
const path = require("path");
const fs = require("fs");
const crypto = require("crypto");
const multer = require("multer");

const customIconsSvc = require("../services/featurelinkCustomIcons.service");
const arcgisIconsetSvc = require("../services/featurelinkArcgisIconset.service");
const datasetsSvc = require("../services/featurelinkDatasets.service");
const auditSvc = require("../services/auditLog.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");
const access = require("../services/featurelinkAccess.service");
const { requireCsrf } = require("../services/featurelinkCsrf.service");
const { noStore } = require("../services/featurelinkHttp.service");
const rateLimits = require("../services/featurelinkRateLimit.service");

router.use(noStore);

const UPLOAD_DIR = path.join(__dirname, "..", "data", "uploads");

// Appendix D §2.3: the old limits were 25 MB x 200 files = 5 GB of disk per request, with no
// quota, no per-user limit and no rate limit; a multer-level error never reached the handler's
// cleanup, so partial uploads accumulated forever. Individual icons are a few KB and a zipped
// iconset of 500 icons is a couple of MB, so these caps are generous for the real workload.
const MAX_UPLOAD_FILE_BYTES = 8 * 1024 * 1024;
const MAX_UPLOAD_FILES = 25;
const UPLOAD_QUOTA_BYTES = 200 * 1024 * 1024; // per user
const UPLOAD_QUOTA_WINDOW_MS = 24 * 60 * 60 * 1000;
const TEMP_MAX_AGE_MS = 60 * 60 * 1000;

const upload = multer({
  storage: multer.diskStorage({
    destination: (req, file, cb) => {
      if (!fs.existsSync(UPLOAD_DIR)) fs.mkdirSync(UPLOAD_DIR, { recursive: true });
      cb(null, UPLOAD_DIR);
    },
    filename: (req, file, cb) => {
      const base = (file.originalname || "icon").replace(/[^a-zA-Z0-9._-]/g, "_").slice(0, 100);
      cb(null, `flicon_${Date.now()}_${crypto.randomUUID()}_${base}`);
    },
  }),
  limits: { fileSize: MAX_UPLOAD_FILE_BYTES, files: MAX_UPLOAD_FILES, fields: 10, parts: MAX_UPLOAD_FILES + 10 },
});

function cleanupTempFiles(files) {
  (files || []).forEach((f) => {
    try {
      if (f.path && fs.existsSync(f.path)) fs.unlinkSync(f.path);
    } catch (err) {
      console.warn("[featurelink] could not remove temp upload:", err && err.message);
    }
  });
}

/**
 * Startup janitor: a multer-level error (LIMIT_FILE_SIZE, aborted connection) is delivered to
 * next(err) and never reached cleanupTempFiles, so stale flicon_* files accumulated forever.
 */
function sweepStaleTempFiles() {
  try {
    if (!fs.existsSync(UPLOAD_DIR)) return;
    const now = Date.now();
    for (const name of fs.readdirSync(UPLOAD_DIR)) {
      if (!name.startsWith("flicon_")) continue;
      const p = path.join(UPLOAD_DIR, name);
      try {
        if (now - fs.statSync(p).mtimeMs > TEMP_MAX_AGE_MS) fs.unlinkSync(p);
      } catch (_) { /* raced with another sweep */ }
    }
  } catch (err) {
    console.warn("[featurelink] temp-upload sweep failed:", err && err.message);
  }
}
sweepStaleTempFiles();
setInterval(sweepStaleTempFiles, TEMP_MAX_AGE_MS).unref();

/** Wraps multer so its errors produce a JSON response here instead of leaking to the host's
 *  error middleware (which this module neither installs nor can assert exists). */
function uploadIcons(req, res, next) {
  upload.array("icons", MAX_UPLOAD_FILES)(req, res, (err) => {
    if (!err) return next();
    cleanupTempFiles(req.files);
    const map = {
      LIMIT_FILE_SIZE: `Each file must be under ${Math.floor(MAX_UPLOAD_FILE_BYTES / (1024 * 1024))} MB.`,
      LIMIT_FILE_COUNT: `At most ${MAX_UPLOAD_FILES} files per upload.`,
      LIMIT_UNEXPECTED_FILE: 'Unexpected upload field — files must be sent as "icons".',
      LIMIT_PART_COUNT: "Too many parts in this upload.",
    };
    return res.status(400).json({ ok: false, error: map[err.code] || "Upload rejected." });
  });
}

/** GET /api/featurelink/admin/custom-icons — list, same shape as a bundled iconset entry. */
router.get("/", (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const sets = customIconsSvc.listCustomIconSets();
    res.json({ ok: true, iconsets: actor.isAdmin ? sets : sets.map(stripSetForNonAdmin) });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * Appendix D §2.1: `created_by`, `sourceUrl` and `sourceField` were returned to every logged-in
 * user by /, /usage and /by-uid — a username plus an internal ArcGIS service URL, and (because the
 * uid is a sha256 of a guessable canonical URL) an oracle for "does this org have layer X".
 */
function stripSetForNonAdmin(s) {
  const { created_by, sourceUrl, sourceField, ...rest } = s;
  return rest;
}

/** POST /api/featurelink/admin/custom-icons — multipart: name + icons (multiple files, .zip iconsets unpacked). */
router.post(
  "/",
  requireCsrf, // before multer: refuse the request before accepting any bytes
  rateLimits.rateLimit("icons-upload", 10, 60_000),
  uploadIcons,
  async (req, res) => {
    const actor = access.requireActor(req, res);
    if (!actor) return cleanupTempFiles(req.files);
    try {
      const bytes = (req.files || []).reduce((n, f) => n + (f.size || 0), 0);
      const quota = rateLimits.consumeQuota(`icons:${actor.username}`, bytes, UPLOAD_QUOTA_BYTES, UPLOAD_QUOTA_WINDOW_MS);
      if (!quota.allowed) {
        cleanupTempFiles(req.files);
        return res.status(429).json({
          ok: false,
          error: `Daily icon upload quota reached (${Math.floor(UPLOAD_QUOTA_BYTES / (1024 * 1024))} MB).`,
        });
      }

      const name = req.body && req.body.name;
      const result = await customIconsSvc.addCustomIcons(name, req.files, actor);
      cleanupTempFiles(req.files);
      if (!result.success) return res.status(result.status || 400).json({ ok: false, error: result.error });

      auditSvc.logEvent({
        actor: req.authentikUser,
        request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
        action: "FEATURELINK_ICONSET_UPLOADED",
        targetType: "featurelink_iconset",
        targetId: result.set.name,
        details: { iconCount: (result.set.icons || []).length, bytes },
      });

      res.json({ ok: true, set: result.set });
    } catch (err) {
      cleanupTempFiles(req.files);
      res.status(500).json({ ok: false, error: toSafeApiError(err) });
    }
  }
);

/**
 * POST /api/featurelink/admin/custom-icons/from-arcgis
 * Body: { url, field?, token?, renderer? }. Reads a FeatureServer layer's renderer, extracts its
 * picture-marker (esriPMS) symbols, and registers them as an icon set whose uid/group/
 * filenames are computed per AUTO-ICONSET-SPEC.md — string-identical to what ATAK/WinTAK/
 * CloudTAK independently produce for the same layer.
 *
 * C-04: the outbound fetch is now allowlisted, DNS-vetted, timed out, redirect-capped and
 * size-capped inside featurelinkSafeFetch.service.js, and this route additionally caps how many
 * such fetches one user may have in flight and how often they may start one.
 */
router.post("/from-arcgis", requireCsrf, rateLimits.rateLimit("from-arcgis", 12, 60_000), async (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;

  const release = rateLimits.acquire(`from-arcgis:${actor.username}`, 2);
  if (!release) {
    return res.status(429).json({ ok: false, error: "Another icon-set generation is already running. Wait for it to finish." });
  }

  try {
    const { url, field, token, renderer } = req.body || {};
    if (!url || typeof url !== "string") {
      return res.status(400).json({ ok: false, error: "A FeatureServer layer URL is required." });
    }
    if (url.length > 2048) {
      return res.status(400).json({ ok: false, error: "That URL is too long." });
    }
    if (field !== undefined && (typeof field !== "string" || field.length > 128)) {
      return res.status(400).json({ ok: false, error: "field must be a short string." });
    }
    if (token !== undefined && (typeof token !== "string" || token.length > 4096)) {
      return res.status(400).json({ ok: false, error: "token must be a string." });
    }
    if (renderer !== undefined && (typeof renderer !== "object" || renderer === null || Array.isArray(renderer))) {
      return res.status(400).json({ ok: false, error: "renderer must be an object." });
    }

    const result = await arcgisIconsetSvc.generateFromArcgis({
      sourceUrl: url,
      field,
      token,
      actor,
      actorUsername: actor.username,
      renderer,
    });
    if (!result.success) return res.status(result.status || 400).json({ ok: false, error: result.error });

    auditSvc.logEvent({
      actor: req.authentikUser,
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_ICONSET_GENERATED",
      targetType: "featurelink_iconset",
      targetId: result.set.name,
      // Deliberately no token, and the canonical URL only (C-21: never log a URL carrying a token).
      details: { canonicalUrl: result.canonicalUrl, iconCount: result.iconCount },
    });

    res.json({
      ok: true,
      set: actor.isAdmin ? result.set : stripSetForNonAdmin(result.set),
      uid: result.uid,
      group: result.group,
      field: result.field,
      canonicalUrl: result.canonicalUrl,
      iconCount: result.iconCount,
      rendererType: result.rendererType,
      valueMap: result.valueMap,
      defaultFilename: result.defaultFilename,
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  } finally {
    release();
  }
});

/**
 * GET /api/featurelink/admin/custom-icons/by-uid/:uid — shared-repository lookup (spec §10).
 * Registered BEFORE the /:name/:file catch-all so "by-uid" isn't read as a set name.
 */
router.get("/by-uid/:uid", rateLimits.rateLimit("by-uid", 60, 60_000), (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const uid = String(req.params.uid || "");
    if (!/^[a-fA-F0-9-]{8,128}$/.test(uid)) return res.status(404).json({ ok: false, error: "not found" });
    const set = customIconsSvc.findSetByUid(uid);
    if (!set) return res.status(404).json({ ok: false, error: "not found" });
    res.json({ ok: true, set: actor.isAdmin ? set : stripSetForNonAdmin(set) });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/admin/custom-icons/usage — every icon set plus which saved datasets
 * reference it. Rate-limited: this fans out to a full-file read per saved dataset
 * (Appendix D §2.4) and the Icon Sets manager calls it on every open.
 */
router.get("/usage", rateLimits.rateLimit("icons-usage", 20, 60_000), (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
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
        sourceUrl: actor.isAdmin ? s.sourceUrl || null : undefined,
        created_by: actor.isAdmin ? s.created_by || null : undefined,
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
 * list.
 */
router.delete("/:name", requireCsrf, (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const sets = customIconsSvc.listCustomIconSets();
    const entry = sets.find((s) => s.name === req.params.name);
    if (!entry) return res.status(404).json({ ok: false, error: "not found" });

    // Was `!isAdmin && entry.created_by && entry.created_by !== username`, which failed OPEN for
    // any set with a null owner — i.e. every auto-generated set created before actorUsername was
    // threaded through. canAccessSet() makes an unowned set admin-only.
    if (!customIconsSvc.canAccessSet(actor, entry)) {
      return res.status(403).json({ ok: false, error: "Only the uploader (or an admin) can delete this icon set." });
    }

    // `?force=yes` was silently ignored, which reads as "the force flag did nothing".
    const forceRaw = req.query.force;
    const force = forceRaw === "1" || forceRaw === "true" || forceRaw === "yes";
    if (forceRaw !== undefined && !force) {
      return res.status(400).json({ ok: false, error: "force must be 1, true or yes." });
    }
    if (!force) {
      const records = datasetsSvc.listDatasets().map((d) => datasetsSvc.loadDataset(d.id)).filter(Boolean);
      const usedBy = customIconsSvc.computeIconsetUsage(records)[entry.name] || [];
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

    auditSvc.logEvent({
      actor: req.authentikUser,
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_ICONSET_DELETED",
      targetType: "featurelink_iconset",
      targetId: entry.name,
      details: { force },
    });

    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

/**
 * GET /api/featurelink/admin/custom-icons/:name/download — the set repackaged as an
 * ATAK-installable iconset zip (spec §7 layout).
 *
 * Cannot be shadowed by /:name/:file below: registerArcgisSet() and the upload path both now
 * ENFORCE an image extension on every stored filename, so a stored file called exactly "download"
 * cannot exist. (That invariant used to be asserted in a comment only.)
 */
router.get("/:name/download", rateLimits.rateLimit("icons-download", 20, 60_000), (req, res) => {
  const actor = access.requireActor(req, res);
  if (!actor) return;
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
  const actor = access.requireActor(req, res);
  if (!actor) return;
  try {
    const filePath = customIconsSvc.getCustomIconPath(req.params.name, req.params.file);
    if (!filePath) return res.status(404).end();
    // nosniff plus an explicit disposition: stored images are user-supplied content served from
    // the portal origin, and the module cannot assert a portal-wide CSP exists.
    res.setHeader("X-Content-Type-Options", "nosniff");
    res.setHeader("Content-Security-Policy", "default-src 'none'; sandbox");
    res.sendFile(filePath, (err) => {
      if (err && !res.headersSent) res.status(404).end();
      else if (err) res.destroy();
    });
  } catch (err) {
    res.status(500).json({ ok: false, error: toSafeApiError(err) });
  }
});

module.exports = router;
