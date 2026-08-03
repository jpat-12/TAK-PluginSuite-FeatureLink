/**
 * C-01 regression suite — docs/featurelink-oauth-relay.html.
 *
 * The relay is a static page shared by CloudTAK and TAK Portal. Before this fix it derived the
 * postMessage target from `state.split('::')[0]`, i.e. from a value that round-trips through the
 * attacker's browser, so a crafted authorize link delivered the victim's ArcGIS authorization
 * code to any origin the attacker named.
 *
 * These tests execute the page's real inline script inside a vm sandbox with a stub DOM, so they
 * assert the shipped file's behaviour rather than a reimplementation of it.
 */

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const RELAY_PATH = path.join(__dirname, "..", "..", "docs", "featurelink-oauth-relay.html");
const HTML = fs.readFileSync(RELAY_PATH, "utf-8");

/** The page's single inline <script> body. */
function relayScript() {
  const m = HTML.match(/<script>([\s\S]*?)<\/script>/);
  assert.ok(m, "relay page must contain an inline script");
  return m[1];
}

/**
 * Runs the relay with a given query string and allowlist, capturing every postMessage the page
 * attempts and the message it renders. `openerOrigin` models the browser's own enforcement:
 * postMessage only delivers when targetOrigin === the opener's real origin.
 */
function runRelay({ search, allowlist, openerOrigin = null, hasOpener = true }) {
  const attempts = [];
  const delivered = [];
  const rendered = { text: "" };

  const sandbox = {
    URL,
    URLSearchParams,
    console: { error() {}, warn() {}, log() {} },
    document: {
      getElementById() {
        return {
          set textContent(v) { rendered.text = v; },
          get textContent() { return rendered.text; },
        };
      },
    },
    window: {
      location: { search },
      opener: hasOpener
        ? {
            postMessage(payload, targetOrigin) {
              attempts.push({ payload, targetOrigin });
              if (openerOrigin && targetOrigin === openerOrigin) delivered.push(payload);
            },
          }
        : null,
      setTimeout() {},
      close() {},
    },
  };
  sandbox.window.window = sandbox.window;

  let src = relayScript();
  // Substitute the operator-configured allowlist; the shipped file ships it empty.
  src = src.replace(
    /var REGISTERED_ORIGINS = \[[\s\S]*?\];/,
    "var REGISTERED_ORIGINS = " + JSON.stringify(allowlist) + ";"
  );

  vm.createContext(sandbox);
  vm.runInContext(src, sandbox);
  return { attempts, delivered, rendered: rendered.text };
}

const GOOD = "https://cloudtak.example.org";
const EVIL = "https://evil.example";

test("C-01: the exact attack string that used to work now delivers nothing to the attacker", () => {
  // The pre-fix code did `state.split('::')[0]` -> "https://evil.example" and posted the code there.
  const { attempts, delivered } = runRelay({
    search: `?code=SECRET_AUTH_CODE&state=${encodeURIComponent(EVIL + "::abc123nonce")}`,
    allowlist: [GOOD],
    openerOrigin: EVIL, // the attacker's page is the opener
  });

  assert.ok(
    attempts.every((a) => a.targetOrigin !== EVIL),
    "must never target an origin taken from `state`"
  );
  assert.deepStrictEqual(delivered, [], "attacker origin must receive no message at all");
});

test("C-01: the target is never taken from `state`, even when state names an allowlisted origin", () => {
  const { attempts } = runRelay({
    search: `?code=X&state=${encodeURIComponent(EVIL + "::n")}`,
    allowlist: [GOOD],
  });
  assert.deepStrictEqual(
    attempts.map((a) => a.targetOrigin),
    [GOOD],
    "targets must come from the allowlist only"
  );
});

test("C-01: a registered origin still receives the code (the relay keeps working)", () => {
  const { delivered } = runRelay({
    search: "?code=SECRET_AUTH_CODE&state=nonce-only",
    allowlist: [GOOD, "https://portal.example.org"],
    openerOrigin: GOOD,
  });
  assert.strictEqual(delivered.length, 1);
  assert.strictEqual(delivered[0].code, "SECRET_AUTH_CODE");
  assert.strictEqual(delivered[0].source, "featurelink-oauth-relay");
});

test("C-01: `state` is echoed back verbatim so the opener can validate its nonce (C-09)", () => {
  const state = "9f2c4a1b7e0d";
  const { delivered } = runRelay({
    search: `?code=X&state=${state}`,
    allowlist: [GOOD],
    openerOrigin: GOOD,
  });
  assert.strictEqual(delivered[0].state, state);
});

test("C-01: legacy `origin::nonce` state is passed through unparsed", () => {
  const state = "https://cloudtak.example.org::9f2c4a1b";
  const { delivered } = runRelay({
    search: `?code=X&state=${encodeURIComponent(state)}`,
    allowlist: [GOOD],
    openerOrigin: GOOD,
  });
  assert.strictEqual(delivered[0].state, state, "state must be echoed whole, not split");
});

test("C-01: malformed allowlist entries are dropped, never used as targets", () => {
  const { attempts } = runRelay({
    search: "?code=X&state=n",
    allowlist: ["*", "https://a.example/path", "not-a-url", "https://u:p@b.example", GOOD, ""],
    openerOrigin: GOOD,
  });
  assert.deepStrictEqual(attempts.map((a) => a.targetOrigin), [GOOD]);
});

test("C-01: an empty allowlist posts nothing and says so", () => {
  const { attempts, rendered } = runRelay({
    search: "?code=X&state=n",
    allowlist: [],
  });
  assert.deepStrictEqual(attempts, []);
  assert.match(rendered, /not configured/i);
});

test("C-01: no opener means no postMessage", () => {
  const { attempts, rendered } = runRelay({
    search: "?code=X&state=n",
    allowlist: [GOOD],
    hasOpener: false,
  });
  assert.deepStrictEqual(attempts, []);
  assert.match(rendered, /part of FeatureLink sign-in/i);
});

test("C-01: the shipped page contains no state-derived target expression", () => {
  assert.ok(
    !/state\s*\.\s*split\s*\(/.test(HTML),
    "state.split(...) must not reappear — that is the C-01 defect"
  );
  assert.ok(/REGISTERED_ORIGINS/.test(HTML), "allowlist must be present in the shipped page");
});
