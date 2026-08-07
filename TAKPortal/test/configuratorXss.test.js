/**
 * C-05 regression suite for the two Display Configurator copies and the two EJS views.
 *
 * These are 4,000-line single-file apps with no module system, so there is no unit under test to
 * import. What IS mechanically checkable, and what actually failed before the fix, is:
 *   (a) the inline scripts still parse (the patches are large and hand-applied);
 *   (b) the specific injection sinks the audit identified no longer appear in the shipped files;
 *   (c) the escaping helper the replacements depend on is actually defined in each file;
 *   (d) the escaping helper itself neutralises the audit's proof-of-concept payloads.
 *
 * Appendix D §5 asks for a headless-browser assertion that no handler fires. That needs a browser
 * engine this repo does not have and this machine cannot install offline; it is recorded as an
 * outstanding gap in docs/remediation/wp4-server.md rather than faked here.
 */

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const REPO = path.join(__dirname, "..", "..");
const PORTAL_HTML = path.join(REPO, "TAKPortal", "assets", "featurelink-configurator", "index.html");
const INFRA_HTML = path.join(REPO, "Infra-TAK", "featurelink_displayconfig_assets", "index.html");
const CONFIGS_EJS = path.join(REPO, "TAKPortal", "views", "featurelink-configs.ejs");
const BROWSE_EJS = path.join(REPO, "TAKPortal", "views", "featurelink.ejs");

const COPIES = [
  ["TAK Portal configurator", PORTAL_HTML],
  ["Infra-TAK configurator", INFRA_HTML],
];

function inlineScripts(file) {
  // Strip HTML comments first: these files carry comments that mention a <script> tag in prose,
  // which a naive tag regex would otherwise treat as the start of a script body.
  const html = fs.readFileSync(file, "utf-8").replace(/<!--[\s\S]*?-->/g, "");
  // Only <script> tags with no src attribute carry code.
  return [...html.matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
}

for (const [label, file] of COPIES) {
  test(`${label}: every inline script still parses`, () => {
    const bodies = inlineScripts(file);
    assert.ok(bodies.length > 0, "expected at least one inline script");
    bodies.forEach((body, i) => {
      assert.doesNotThrow(() => new vm.Script(body, { filename: `${path.basename(file)}#${i}` }));
    });
  });

  test(`${label}: the C-05 injection sinks are gone`, () => {
    const html = fs.readFileSync(file, "utf-8");
    const sinks = [
      // column name straight into an inline handler — `a');alert(1);('` executed on click
      [`onclick="sortBy('\${c}')"`, "sortBy inline handler with a raw column name"],
      [`onclick="selectField('\${f.name}')"`, "selectField inline handler with a raw field name"],
      [`id="fi-\${f.name}"`, "field name interpolated into an element id"],
      // cell value straight into innerHTML and into a title attribute
      ["${isNull ? 'null' : v}", "unescaped cell value in innerHTML"],
      [`title="\${isNull?'':v}"`, "unescaped cell value in a title attribute"],
      [`title="\${name}"`, "unescaped filename in a title attribute"],
      [`title="\${f.name}"`, "unescaped field name in a title attribute"],
      [`title="\${uv.value}"`, "unescaped unique value in a title attribute"],
      // third-party error strings into innerHTML
      ["${err.message}", "third-party error message in innerHTML"],
    ];
    for (const [needle, why] of sinks) {
      assert.ok(!html.includes(needle), `${why} still present: ${needle}`);
    }
  });

  test(`${label}: the escaping helpers are defined exactly once`, () => {
    const html = fs.readFileSync(file, "utf-8");
    assert.strictEqual((html.match(/function flEsc\b/g) || []).length, 1);
    assert.strictEqual((html.match(/function flBind\b/g) || []).length, 1);
  });

  test(`${label}: no external CDN script remains (C-38)`, () => {
    const html = fs.readFileSync(file, "utf-8");
    const remoteScripts = [...html.matchAll(/<script[^>]*\bsrc="(https?:)?\/\/[^"]+"/g)].map((m) => m[0]);
    assert.deepStrictEqual(remoteScripts, [], "all browser libraries must be served from this origin");
  });
}

test("flEsc neutralises the audit's proof-of-concept payloads", () => {
  // Lift the shipped helper out of the file and run it, rather than reimplementing it here.
  const html = fs.readFileSync(PORTAL_HTML, "utf-8");
  const src = html.match(/function flEsc\b[\s\S]*?\n}\n/)[0];
  const sandbox = {};
  vm.createContext(sandbox);
  vm.runInContext(src + "\n", sandbox);
  const flEsc = sandbox.flEsc;

  const payloads = [
    ["a');alert(1);('", "column name from a crafted CSV"],
    ["<img src=x onerror=alert(1)>", "cell value"],
    ['x" onmouseover="alert(1)', "attribute breakout"],
    ["<svg onload=alert(1)>.csv", "filename"],
    ["</script><script>alert(1)</script>", "script breakout"],
  ];
  for (const [payload, why] of payloads) {
    const out = flEsc(payload);
    assert.ok(!/[<>"']/.test(out), `${why}: dangerous characters survived escaping -> ${out}`);
  }
  assert.strictEqual(flEsc(null), "");
  assert.strictEqual(flEsc(undefined), "");
  assert.strictEqual(flEsc("plain text 123 _-"), "plain text 123 _-");
});

test("C-05: featurelink-configs.ejs no longer builds an inline handler from a dataset name", () => {
  const ejs = fs.readFileSync(CONFIGS_EJS, "utf-8");
  // The exact defect: JSON.stringify escapes for JavaScript, not HTML, so the quotes it emits
  // terminated the onclick="" attribute. A dataset name is attacker-controlled by any user.
  assert.ok(!ejs.includes("JSON.stringify(d.name)"), "JSON.stringify(d.name) in markup is the C-05 sink");
  assert.ok(!/onclick="showQr/.test(ejs), "showQr inline handler must be gone");
  assert.ok(!/onclick="copyLink/.test(ejs), "copyLink inline handler must be gone");
  assert.ok(ejs.includes("data-action"), "actions must be driven by data-* attributes");
  assert.ok(ejs.includes("addEventListener"), "a delegated listener must replace the inline handlers");
});

test("C-05: neither EJS view interpolates user data into markup server-side", () => {
  for (const file of [CONFIGS_EJS, BROWSE_EJS]) {
    const ejs = fs.readFileSync(file, "utf-8");
    // Raw EJS output tags are the server-side XSS vector. The only acceptable use is an include.
    const raws = [...ejs.matchAll(/<%-([\s\S]*?)%>/g)].map((m) => m[1].trim());
    for (const r of raws) {
      assert.ok(
        r.startsWith("include("),
        `${path.basename(file)}: raw <%- %> output must be an include, got: ${r}`
      );
    }
  }
});

test("C-38: neither EJS view loads a script from a remote origin", () => {
  for (const file of [CONFIGS_EJS, BROWSE_EJS]) {
    const ejs = fs.readFileSync(file, "utf-8");
    const remote = [...ejs.matchAll(/<script[^>]*\bsrc="(https?:)?\/\/[^"]+"/g)].map((m) => m[0]);
    assert.deepStrictEqual(remote, [], `${path.basename(file)} must not load a CDN script`);
  }
});

test("C-10: the admin view sends the CSRF token on its mutating fetch", () => {
  const ejs = fs.readFileSync(CONFIGS_EJS, "utf-8");
  assert.ok(ejs.includes("X-FeatureLink-CSRF"), "DELETE must carry the double-submit token");
  assert.ok(ejs.includes("fl_csrf"), "the page must read the token cookie");
});
