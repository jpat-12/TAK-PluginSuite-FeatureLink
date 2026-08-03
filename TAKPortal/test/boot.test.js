/**
 * Boot smoke test.
 *
 * Two things this catches that nothing else does:
 *
 *  1. **C-12** — install.sh never installed `multer` or `unzipper`, and nothing declared them.
 *     If either is missing, `require` throws MODULE_NOT_FOUND at startup and the ENTIRE TAK
 *     Portal fails to boot: this module takes down the whole product. This test requires every
 *     route file for real, so a missing runtime dependency fails here.
 *
 *  2. This work package added five new services and wired them into four route files. A
 *     half-wired access-control or CSRF layer is worse than none — it 403s legitimate traffic or
 *     crashes on a missing import. This asserts every router mounts and every route answers.
 */

const test = require("node:test");
const assert = require("node:assert");
const path = require("node:path");
const fs = require("node:fs");

const H = require("./helpers/portalHarness");

test("every runtime dependency the module requires is installed and resolvable (C-12)", () => {
  // The two packages install.sh forgot. `require` here is the same call the host portal makes.
  assert.doesNotThrow(() => require("multer"), "multer must be installed");
  assert.doesNotThrow(() => require("unzipper"), "unzipper must be installed");
});

test("package.json declares those dependencies (C-12)", () => {
  const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf-8"));
  assert.ok(pkg.dependencies.multer, "multer must be declared");
  assert.ok(pkg.dependencies.unzipper, "unzipper must be declared");
});

test("every route file and every new service loads without throwing", () => {
  H.installHostStubs();
  const files = [
    "../routes/featurelinkBrowse.routes",
    "../routes/featurelinkConfigurator.routes",
    "../routes/featurelinkCustomIcons.routes",
    "../routes/featurelinkDatasetsAdmin.routes",
    "../services/featurelinkAccess.service",
    "../services/featurelinkArcgisIconset.service",
    "../services/featurelinkCsrf.service",
    "../services/featurelinkCustomIcons.service",
    "../services/featurelinkDatasets.service",
    "../services/featurelinkHttp.service",
    "../services/featurelinkRateLimit.service",
    "../services/featurelinkSafeFetch.service",
  ];
  for (const f of files) {
    assert.doesNotThrow(() => require(f), `${f} must load`);
  }
});

test("the app boots and every mounted route answers (no missing import, no hang)", async () => {
  H.resetStore();
  const app = H.buildApp();
  const srv = await H.listen(app);
  try {
    // Each entry: method, path, and the statuses that prove the handler ran (rather than the
    // request dying in middleware or reaching the host's error handler).
    const probes = [
      ["GET", "/api/featurelink/configs", [200]],
      ["GET", "/api/featurelink/configs/deadbeef/download", [404]],
      ["GET", "/api/featurelink/admin/datasets", [200]],
      ["GET", "/api/featurelink/admin/datasets/deadbeef", [404]],
      ["GET", "/api/featurelink/admin/datasets/deadbeef/file", [404]],
      ["GET", "/api/featurelink/admin/custom-icons", [200]],
      ["GET", "/api/featurelink/admin/custom-icons/usage", [200]],
      ["GET", "/api/featurelink/admin/custom-icons/by-uid/" + "0".repeat(64), [404]],
      ["GET", "/api/featurelink/admin/custom-icons/NoSuchSet/download", [404]],
      ["GET", "/api/featurelink/admin/custom-icons/NoSuchSet/x.png", [404]],
    ];
    for (const [method, p, ok] of probes) {
      const res = await H.request({ url: srv.url, method, path: p, as: "alice" });
      assert.ok(ok.includes(res.status), `${method} ${p} -> ${res.status} (expected one of ${ok})`);
    }
    assert.deepStrictEqual(
      app.__unhandled.map((e) => e.message),
      [],
      "no request may fall through to the host portal's error middleware"
    );
  } finally {
    await srv.close();
  }
});

test("the configurator page is served and issues the CSRF cookie (C-10)", async () => {
  const app = H.buildApp();
  const srv = await H.listen(app);
  try {
    const res = await H.request({ url: srv.url, path: "/featurelink-configs/configurator/", as: "alice" });
    assert.strictEqual(res.status, 200);
    const setCookie = [].concat(res.headers["set-cookie"] || []).join("; ");
    assert.match(setCookie, /fl_csrf=[a-f0-9]{64}/, "must issue a 64-hex CSRF token");
    assert.match(setCookie, /SameSite=Lax/, "cookie must be SameSite=Lax at minimum");
    assert.match(setCookie, /Path=\//);
    assert.match(res.headers["x-frame-options"] || "", /SAMEORIGIN/);
    assert.match(res.headers["x-content-type-options"] || "", /nosniff/);
  } finally {
    await srv.close();
  }
});

test("authenticated JSON is never cacheable", async () => {
  const app = H.buildApp();
  const srv = await H.listen(app);
  try {
    const res = await H.request({ url: srv.url, path: "/api/featurelink/admin/datasets", as: "alice" });
    assert.match(res.headers["cache-control"] || "", /no-store/);
    assert.match(res.headers["x-content-type-options"] || "", /nosniff/);
  } finally {
    await srv.close();
  }
});
