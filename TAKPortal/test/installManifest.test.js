/**
 * install.sh must deliver every file the module's own routes and services require.
 *
 * The regression: install.sh copied a hand-kept list naming 3 of the 8 featurelink*.service.js
 * files. Every route requires featurelinkAccess/Csrf/Http (and RateLimit/SafeFetch), so a real
 * install hit MODULE_NOT_FOUND and the whole host TAK Portal failed to start. The rest of this
 * suite loads the files from this repository, where all of them exist, so nothing caught it.
 *
 * This test checks the delivery contract instead: every relative require resolves to either a
 * featurelink*-prefixed file in routes/ or services/ (which install.sh copies by pattern) or
 * one of the host portal's own services, and install.sh still copies by pattern rather than
 * by an explicit list that can fall out of date again.
 */

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");

const ROOT = path.join(__dirname, "..");
// Provided by the host TAK Portal, never copied (stubbed in test/helpers/portalHarness.js).
const HOST_SERVICES = new Set(["services/auditLog.service.js", "services/apiErrorPayload.service.js"]);

function moduleFiles() {
  const out = [];
  for (const dir of ["routes", "services"]) {
    for (const f of fs.readdirSync(path.join(ROOT, dir))) {
      if (f.endsWith(".js")) out.push(`${dir}/${f}`);
    }
  }
  return out;
}

test("every relative require is a featurelink* file install.sh copies, or a host service", () => {
  const problems = [];
  for (const rel of moduleFiles()) {
    const src = fs.readFileSync(path.join(ROOT, rel), "utf-8");
    for (const m of src.matchAll(/require\(\s*["'](\.{1,2}\/[^"']+)["']\s*\)/g)) {
      let target = path.posix.normalize(path.posix.join(path.posix.dirname(rel), m[1]));
      if (!target.endsWith(".js")) target += ".js";
      if (HOST_SERVICES.has(target)) continue;
      const base = path.posix.basename(target);
      const dir = path.posix.dirname(target);
      if (!["routes", "services"].includes(dir) || !base.startsWith("featurelink")) {
        problems.push(`${rel} requires ${m[1]}: not a featurelink* routes/services file, so install.sh will not copy it`);
      } else if (!fs.existsSync(path.join(ROOT, target))) {
        problems.push(`${rel} requires ${m[1]}: ${target} does not exist`);
      }
    }
  }
  assert.deepStrictEqual(problems, []);
});

test("every file in routes/ and services/ is featurelink*-prefixed (so the copy glob includes it)", () => {
  const stray = moduleFiles().filter((f) => !path.posix.basename(f).startsWith("featurelink"));
  assert.deepStrictEqual(stray, []);
});

test("install.sh copies routes and services by pattern, not by a hand-kept list", () => {
  const sh = fs.readFileSync(path.join(ROOT, "install.sh"), "utf-8");
  assert.match(sh, /\$kind"\/featurelink\*\.js/, "install.sh must glob featurelink*.js for routes and services");
  assert.doesNotMatch(
    sh,
    /cp -f "\$SRC_DIR\/services\/featurelink\w+\.service\.js"/,
    "a per-file service copy is back; it will drift from what the routes require",
  );
});
