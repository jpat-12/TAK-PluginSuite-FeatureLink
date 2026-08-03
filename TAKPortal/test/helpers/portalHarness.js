/**
 * Boots the FeatureLink routers inside a real Express app, exactly as TAKPortal/install.sh mounts
 * them into the host portal's server.js.
 *
 * Two of the modules the routers require — `auditLog.service` and `apiErrorPayload.service` —
 * belong to the HOST TAK Portal, not to this repository. They are stubbed here through a
 * Module._load hook rather than by creating real files under services/, because install.sh copies
 * everything in services/ into the host portal and would overwrite the host's genuine
 * implementations with our stubs.
 *
 * The host's session middleware is likewise stubbed: it normally attaches `req.authentikUser` and
 * `res.locals.perm`. The harness drives both from request headers so a test can be user A, user B,
 * an admin, or anonymous.
 */

const Module = require("node:module");
const path = require("node:path");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");

const SERVICES = path.join(__dirname, "..", "..", "services");

// ---------------------------------------------------------------------------
// Host-portal module stubs
// ---------------------------------------------------------------------------

const auditEvents = [];

const HOST_STUBS = {
  [path.join(SERVICES, "auditLog.service.js")]: {
    logEvent(ev) {
      auditEvents.push(ev);
    },
  },
  [path.join(SERVICES, "apiErrorPayload.service.js")]: {
    toSafeApiError(err) {
      // The host's real implementation redacts paths/errnos. Mirroring that shape is enough.
      return { message: (err && err.message) || "error" };
    },
  },
};

let hooked = false;
function installHostStubs() {
  if (hooked) return;
  hooked = true;
  const origLoad = Module._load;
  Module._load = function (request, parent, isMain) {
    if (parent && (request.endsWith("auditLog.service") || request.endsWith("apiErrorPayload.service"))) {
      const resolved = path.resolve(path.dirname(parent.filename), request + ".js");
      if (HOST_STUBS[resolved]) return HOST_STUBS[resolved];
    }
    return origLoad.call(this, request, parent, isMain);
  };
}

// ---------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------

/**
 * @param {object} [opts]
 * @param {string} [opts.dataDir] overrides where the services persist (see note below)
 */
function buildApp() {
  installHostStubs();
  const express = require("express");
  const app = express();
  app.use(express.json({ limit: "10mb" }));

  // Stand-in for the host portal's Authentik session + permission middleware.
  app.use((req, res, next) => {
    const user = req.headers["x-test-user"];
    if (user) req.authentikUser = { username: String(user) };
    if (req.headers["x-test-perm-missing"] !== "1") {
      const isAdmin = req.headers["x-test-admin"] === "1";
      res.locals.perm = (id) => isAdmin && id === "page.featurelink_configs";
    }
    next();
  });

  // Mounted exactly as install.sh patches server.js.
  app.use("/api/featurelink/admin/datasets", require("../../routes/featurelinkDatasetsAdmin.routes"));
  app.use("/api/featurelink/admin/custom-icons", require("../../routes/featurelinkCustomIcons.routes"));
  app.use("/featurelink-configs/configurator", require("../../routes/featurelinkConfigurator.routes"));
  app.use("/api/featurelink", require("../../routes/featurelinkBrowse.routes"));

  // The host portal's own error middleware. Present so an unhandled route error surfaces as a
  // 500 with a body rather than a hang — and so a test can prove nothing reaches it.
  const unhandled = [];
  app.use((err, req, res, _next) => {
    unhandled.push(err);
    if (!res.headersSent) res.status(500).json({ ok: false, error: "unhandled" });
  });
  app.__unhandled = unhandled;
  return app;
}

/** Starts the app on an ephemeral port. Returns {url, close}. */
function listen(app) {
  return new Promise((resolve) => {
    const server = http.createServer(app);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      resolve({
        url: `http://127.0.0.1:${port}`,
        close: () => new Promise((r) => server.close(r)),
      });
    });
  });
}

/**
 * Minimal request helper. `as` selects the identity; `csrf` supplies the double-submit pair.
 * @param {object} o
 * @param {string} o.url base url
 * @param {string} o.method
 * @param {string} o.path
 * @param {string|null} [o.as]        username, or null for anonymous
 * @param {boolean} [o.admin]
 * @param {boolean} [o.permMissing]   simulate the host failing to attach res.locals.perm
 * @param {string} [o.csrf]           token used for BOTH the cookie and the header
 * @param {string} [o.csrfCookie]     cookie token, when it must differ from the header
 * @param {string} [o.csrfHeader]     header token, when it must differ from the cookie
 * @param {string|null} [o.origin]    Origin header; null omits it entirely
 * @param {object} [o.json]           JSON body
 */
function request(o) {
  const u = new URL(o.path, o.url);
  const headers = {};
  if (o.as) headers["x-test-user"] = o.as;
  if (o.admin) headers["x-test-admin"] = "1";
  if (o.permMissing) headers["x-test-perm-missing"] = "1";

  const cookieTok = o.csrfCookie !== undefined ? o.csrfCookie : o.csrf;
  const headerTok = o.csrfHeader !== undefined ? o.csrfHeader : o.csrf;
  if (cookieTok) headers.cookie = `fl_csrf=${cookieTok}`;
  if (headerTok) headers["x-featurelink-csrf"] = headerTok;

  if (o.origin !== null) headers.origin = o.origin || o.url;

  let body;
  if (o.json !== undefined) {
    body = Buffer.from(JSON.stringify(o.json), "utf-8");
    headers["content-type"] = "application/json";
    headers["content-length"] = body.length;
  }

  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: u.hostname, port: u.port, path: u.pathname + u.search, method: o.method || "GET", headers },
      (res) => {
        const chunks = [];
        res.on("data", (c) => chunks.push(c));
        res.on("end", () => {
          const text = Buffer.concat(chunks).toString("utf-8");
          let json = null;
          try {
            json = JSON.parse(text);
          } catch (_) { /* not JSON */ }
          resolve({ status: res.statusCode, headers: res.headers, text, json });
        });
      }
    );
    req.on("error", reject);
    if (body) req.write(body);
    req.end();
  });
}

/**
 * The services resolve their storage from __dirname at require time, so tests operate on the
 * module's real data root. It is gitignored; this wipes it between suites.
 */
const DATA_ROOT = path.join(__dirname, "..", "..", "data");
function resetStore() {
  try {
    fs.rmSync(path.join(DATA_ROOT, "featurelink-configs"), { recursive: true, force: true });
    fs.rmSync(path.join(DATA_ROOT, "uploads"), { recursive: true, force: true });
  } catch (_) { /* first run */ }
}

function tmpDir(prefix) {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

const VALID_CSRF = "a".repeat(64);

module.exports = { buildApp, listen, request, resetStore, tmpDir, auditEvents, DATA_ROOT, VALID_CSRF, installHostStubs };
