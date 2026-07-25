/**
 * FeatureLink Configs — Admin API: prep display configs for field devices.
 * Mounted with requirePermission("page.featurelink_configs") in server.js,
 * same convention as plugins.routes.js's Plugin Manager.
 */

const router = require("express").Router();
const path = require("path");
const fs = require("fs");
const multer = require("multer");

const configsSvc = require("../services/featurelinkConfigs.service");
const auditSvc = require("../services/auditLog.service");
const { toSafeApiError } = require("../services/apiErrorPayload.service");

const upload = multer({
  storage: multer.diskStorage({
    destination: (req, file, cb) => {
      const dir = path.join(__dirname, "..", "data", "uploads");
      if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
      cb(null, dir);
    },
    filename: (req, file, cb) => {
      const base = (file.originalname || "config").replace(/[^a-zA-Z0-9._-]/g, "_");
      cb(null, `flconfig_${Date.now()}_${base}`);
    },
  }),
  limits: { fileSize: 50 * 1024 * 1024 }, // 50 MB — these are small JSON/config files, not APKs
});

function toErrorPayload(err) {
  return toSafeApiError(err);
}

function auditUserOf(req) {
  return req.authentikUser;
}

/** req.body.display arrives as a JSON string (form field, alongside the optional file upload). */
function parseDisplayField(raw) {
  if (raw == null || raw === "") return undefined;
  if (typeof raw === "object") return raw;
  try {
    return JSON.parse(raw);
  } catch (_) {
    return undefined;
  }
}

/**
 * GET /api/featurelink/admin/configs
 */
router.get("/configs", (req, res) => {
  try {
    res.json({ configs: configsSvc.listConfigs() });
  } catch (err) {
    res.status(500).json({ error: toErrorPayload(err) });
  }
});

/**
 * POST /api/featurelink/admin/configs
 * multipart/form-data when sourceType=file (field "config"), JSON body when sourceType=url.
 */
router.post("/configs", upload.single("config"), (req, res) => {
  try {
    const { name, description, targetApp, sourceType, sourceUrl } = req.body || {};
    const user = auditUserOf(req);

    const result = configsSvc.createConfig({
      name,
      description,
      targetApp,
      sourceType,
      sourceUrl,
      display: parseDisplayField(req.body && req.body.display),
      uploadedFile: req.file || null,
      createdBy: user?.username || null,
    });

    try {
      if (req.file?.path && fs.existsSync(req.file.path)) fs.unlinkSync(req.file.path);
    } catch (_) {}

    if (!result.success) {
      return res.status(400).json({ error: result.error });
    }

    auditSvc.logEvent({
      actor: user,
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_CONFIG_CREATED",
      targetType: "featurelink_config",
      targetId: result.config.id,
      details: { name: result.config.name, sourceType: result.config.sourceType },
    });

    res.json({ success: true, config: result.config });
  } catch (err) {
    try {
      if (req.file?.path && fs.existsSync(req.file.path)) fs.unlinkSync(req.file.path);
    } catch (_) {}
    res.status(500).json({ error: toErrorPayload(err) });
  }
});

/**
 * PATCH /api/featurelink/admin/configs/:id
 * Body: { name?, description?, targetApp?, sourceUrl?, display? } — sourceUrl/display only
 * apply to "url" entries.
 */
router.patch("/configs/:id", (req, res) => {
  try {
    const { id } = req.params;
    const { name, description, targetApp, sourceUrl } = req.body || {};
    const result = configsSvc.updateConfig(id, {
      name,
      description,
      targetApp,
      sourceUrl,
      display: parseDisplayField(req.body && req.body.display),
    });
    if (!result.success) {
      return res.status(404).json({ error: result.error });
    }

    auditSvc.logEvent({
      actor: auditUserOf(req),
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_CONFIG_UPDATED",
      targetType: "featurelink_config",
      targetId: id,
      details: { name: result.config.name },
    });

    res.json({ success: true, config: result.config });
  } catch (err) {
    res.status(500).json({ error: toErrorPayload(err) });
  }
});

/**
 * DELETE /api/featurelink/admin/configs/:id
 */
router.delete("/configs/:id", (req, res) => {
  try {
    const { id } = req.params;
    const result = configsSvc.deleteConfig(id);
    if (!result.success) {
      return res.status(404).json({ error: result.error });
    }

    auditSvc.logEvent({
      actor: auditUserOf(req),
      request: { method: req.method, path: req.originalUrl || req.path, ip: req.ip },
      action: "FEATURELINK_CONFIG_DELETED",
      targetType: "featurelink_config",
      targetId: id,
      details: {},
    });

    res.json({ success: true });
  } catch (err) {
    res.status(500).json({ error: toErrorPayload(err) });
  }
});

module.exports = router;
