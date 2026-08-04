/**
 * C-03 authorisation matrix, and C-10 CSRF rejection.
 *
 * Every test here fails against a8f6f4a. The pre-fix behaviour is stated per test.
 *
 * Users: alice and bob are ordinary logged-in portal users; root is an admin (holds
 * page.featurelink_configs).
 */

const test = require("node:test");
const assert = require("node:assert");

const H = require("./helpers/portalHarness");
const DS = "/api/featurelink/admin/datasets";

let srv;
let app;

test.before(async () => {
  H.resetStore();
  app = H.buildApp();
  srv = await H.listen(app);
});
test.after(async () => {
  if (srv) await srv.close();
  H.resetStore();
});

const CSRF = H.VALID_CSRF;

/** Creates a dataset owned by `who` and returns its id. */
async function createAs(who, name, extra = {}) {
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: who, csrf: CSRF,
    json: { name, source_type: "file", state: { cfg: { secret: `${who}-private` } }, ...extra },
  });
  assert.strictEqual(res.status, 200, `create as ${who}: ${res.text}`);
  return res.json.id;
}

// ---------------------------------------------------------------------------
// C-03 — cross-tenant read
// ---------------------------------------------------------------------------

test("C-03: GET /:id no longer discloses another user's full record", async () => {
  const id = await createAs("alice", "Alice private config");

  // Pre-fix: 200 with the complete record, including state.cfg and source_url — for ANY logged-in
  // user. `state.cfg` and `source_url` can carry ArcGIS service URLs and pasted credentials.
  const asBob = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "bob" });
  assert.strictEqual(asBob.status, 404);
  assert.ok(!asBob.text.includes("alice-private"), "record contents must not leak");

  // The owner still gets it.
  const asAlice = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "alice" });
  assert.strictEqual(asAlice.status, 200);
  assert.strictEqual(asAlice.json.state.cfg.secret, "alice-private");

  // An admin still gets it.
  const asAdmin = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "root", admin: true });
  assert.strictEqual(asAdmin.status, 200);
});

test("C-03: a non-owner gets 404, not 403 — no id-existence oracle", async () => {
  const id = await createAs("alice", "Oracle check");
  const real = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "bob" });
  const fake = await H.request({ url: srv.url, path: `${DS}/${"f".repeat(32)}`, as: "bob" });
  assert.strictEqual(real.status, fake.status, "an existing id must be indistinguishable from a missing one");
});

test("C-03: GET /:id/file no longer streams another user's uploaded dataset bytes", async () => {
  const payload = Buffer.from("lat,lon,name\n1,2,SENSITIVE-CAP-PII\n", "utf-8").toString("base64");
  const id = await createAs("alice", "Alice upload", { file_content_b64: payload, file_name: "roster.csv" });

  // Pre-fix: 200 with the raw bytes to any logged-in user — for CAP/SAR use that is PII and
  // operational location data.
  const asBob = await H.request({ url: srv.url, path: `${DS}/${id}/file`, as: "bob" });
  assert.strictEqual(asBob.status, 404);
  assert.ok(!asBob.text.includes("SENSITIVE-CAP-PII"));

  const asAlice = await H.request({ url: srv.url, path: `${DS}/${id}/file`, as: "alice" });
  assert.strictEqual(asAlice.status, 200);
  assert.ok(asAlice.text.includes("SENSITIVE-CAP-PII"));
  // Was `inline` with no nosniff, so stored HTML was same-origin XSS on a sniffing browser.
  assert.match(asAlice.headers["content-disposition"], /^attachment/);
  assert.match(asAlice.headers["x-content-type-options"], /nosniff/);
});

test("C-03: a Content-Disposition filename cannot be spoofed or inject a header", async () => {
  // Pre-fix: file_name went into the header unsanitized. A `"` spoofed the download name; a CRLF
  // threw ERR_INVALID_CHAR inside sendFile() for a trivially reachable unhandled 500.
  const id = await createAs("alice", "Header injection", {
    file_content_b64: Buffer.from("x").toString("base64"),
    file_name: 'evil".exe\r\nX-Injected: yes',
  });
  const res = await H.request({ url: srv.url, path: `${DS}/${id}/file`, as: "alice" });
  assert.strictEqual(res.status, 200, "must not 500");
  assert.ok(!("x-injected" in res.headers), "no header injection");
  assert.ok(!res.headers["content-disposition"].includes('"evil"'), "quote must be sanitized");
});

test("C-03: the list endpoint no longer shows non-admins other users' records", async () => {
  await createAs("alice", "Alice list item");
  const bob = await H.request({ url: srv.url, path: DS, as: "bob" });
  assert.strictEqual(bob.status, 200);
  assert.ok(
    !bob.json.datasets.some((d) => d.name === "Alice list item"),
    "a non-admin must not see another user's configs"
  );
  const admin = await H.request({ url: srv.url, path: DS, as: "root", admin: true });
  assert.ok(admin.json.datasets.some((d) => d.name === "Alice list item"), "an admin still sees everything");
});

test("C-03: created_by is not disclosed to non-admins", async () => {
  await createAs("alice", "Owner disclosure");
  const bob = await H.request({ url: srv.url, path: DS, as: "bob" });
  assert.ok(bob.json.datasets.every((d) => d.created_by === undefined));
});

// ---------------------------------------------------------------------------
// C-03 — destructive operations
// ---------------------------------------------------------------------------

test("C-03: a non-owner cannot delete, an owner and an admin can", async () => {
  const id = await createAs("alice", "Delete me");
  const bob = await H.request({ url: srv.url, method: "DELETE", path: `${DS}/${id}`, as: "bob", csrf: CSRF });
  assert.strictEqual(bob.status, 403);

  const still = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "alice" });
  assert.strictEqual(still.status, 200, "the record must survive the refused delete");

  const alice = await H.request({ url: srv.url, method: "DELETE", path: `${DS}/${id}`, as: "alice", csrf: CSRF });
  assert.strictEqual(alice.status, 200);

  const id2 = await createAs("alice", "Admin deletes this");
  const admin = await H.request({ url: srv.url, method: "DELETE", path: `${DS}/${id2}`, as: "root", admin: true, csrf: CSRF });
  assert.strictEqual(admin.status, 200);
});

test("C-03: a non-owner cannot overwrite an existing record by POSTing its id", async () => {
  const id = await createAs("alice", "Alice original");
  const bob = await H.request({
    url: srv.url, method: "POST", path: DS, as: "bob", csrf: CSRF,
    json: { id, name: "Bob hijacked this", source_type: "file", state: {} },
  });
  assert.strictEqual(bob.status, 403);
  const check = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "alice" });
  assert.strictEqual(check.json.name, "Alice original");
});

test("C-03: a client-chosen id for a record that does not exist is refused (id squatting)", async () => {
  // Pre-fix: the ownership branch was skipped entirely when the id did not exist, and saveDataset
  // created the record AT THE CALLER'S CHOSEN ID — letting a user pre-create the record another
  // user's already-shared QR link would later resolve to.
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "mallory", csrf: CSRF,
    json: { id: "c0ffee".padEnd(32, "0"), name: "Squatted", source_type: "file", state: {} },
  });
  assert.strictEqual(res.status, 400);
  assert.match(res.json.error.message || res.json.error, /ids are assigned by the server/i);
});

// ---------------------------------------------------------------------------
// C-03 — the fail-open cases
// ---------------------------------------------------------------------------

test("C-03: an unowned (legacy / Flask-written) record is admin-only, not everyone's", async () => {
  // Pre-fix: isOwnedBy() returned TRUE when created_by was unset, so every record written before
  // ownership tracking existed — and everything the Flask module writes, which has no ownership
  // model at all — was readable, editable and deletable by ANY logged-in user.
  const fs = require("node:fs");
  const path = require("node:path");
  const id = "ab".repeat(16);
  const dir = path.join(H.DATA_ROOT, "featurelink-configs", "datasets", id);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(
    path.join(dir, "record.json"),
    JSON.stringify({ name: "Legacy record", state: {}, created_by: null, updated_at: "2020-01-01T00:00:00Z" })
  );

  const bob = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "bob" });
  assert.strictEqual(bob.status, 404, "an unowned record must not be readable by an arbitrary user");

  const del = await H.request({ url: srv.url, method: "DELETE", path: `${DS}/${id}`, as: "bob", csrf: CSRF });
  assert.strictEqual(del.status, 403, "an unowned record must not be deletable by an arbitrary user");

  const admin = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "root", admin: true });
  assert.strictEqual(admin.status, 200, "an admin must still be able to manage it");
});

test("C-03: a request with no portal identity is refused, not served anonymously", async () => {
  for (const p of [DS, `${DS}/${"a".repeat(32)}`, "/api/featurelink/configs", "/api/featurelink/admin/custom-icons"]) {
    const res = await H.request({ url: srv.url, path: p, as: null });
    assert.strictEqual(res.status, 401, `${p} must 401 without a session`);
  }
});

test("C-03: a missing res.locals.perm degrades to non-admin and never to admin", async () => {
  const id = await createAs("alice", "Perm-missing check");
  // The host portal failing to attach res.locals.perm must not silently grant admin.
  const res = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "bob", admin: true, permMissing: true });
  assert.strictEqual(res.status, 404, "no perm function => not an admin");
});

// ---------------------------------------------------------------------------
// C-10 — CSRF
// ---------------------------------------------------------------------------

test("C-10: a cross-origin POST is rejected", async () => {
  // Pre-fix: grep for csrf|SameSite across TAKPortal/ returned zero files. Any page a portal admin
  // visited could delete their configs and icon sets.
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice", csrf: CSRF,
    origin: "https://evil.example",
    json: { name: "CSRF", source_type: "file", state: {} },
  });
  assert.strictEqual(res.status, 403);
  assert.match(res.json.error, /cross-site or missing Origin/i);
});

test("C-10: a POST with no Origin or Referer at all is rejected", async () => {
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice", csrf: CSRF, origin: null,
    json: { name: "CSRF", source_type: "file", state: {} },
  });
  assert.strictEqual(res.status, 403);
});

test("C-10: a same-origin POST with no CSRF token is rejected", async () => {
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice",
    json: { name: "CSRF", source_type: "file", state: {} },
  });
  assert.strictEqual(res.status, 403);
  assert.match(res.json.error, /CSRF token/i);
});

test("C-10: a header token that does not match the cookie is rejected", async () => {
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice",
    csrfCookie: CSRF, csrfHeader: "b".repeat(64),
    json: { name: "CSRF", source_type: "file", state: {} },
  });
  assert.strictEqual(res.status, 403);
});

test("C-10: DELETE is protected too", async () => {
  const id = await createAs("alice", "CSRF delete target");
  const res = await H.request({
    url: srv.url, method: "DELETE", path: `${DS}/${id}`, as: "alice",
    origin: "https://evil.example", csrf: CSRF,
  });
  assert.strictEqual(res.status, 403);
  const still = await H.request({ url: srv.url, path: `${DS}/${id}`, as: "alice" });
  assert.strictEqual(still.status, 200, "the record must survive the CSRF attempt");
});

test("C-10: safe methods are not blocked", async () => {
  const res = await H.request({ url: srv.url, path: DS, as: "alice", origin: "https://evil.example" });
  assert.strictEqual(res.status, 200, "GET must not require a CSRF token");
});

// ---------------------------------------------------------------------------
// Input bounds (Appendix D §2.3)
// ---------------------------------------------------------------------------

test("invalid base64 file content is rejected instead of silently accepted", async () => {
  // Pre-fix: Buffer.from(x, "base64") never throws — it drops invalid characters — so the
  // try/catch was dead code and the "invalid file_content_b64" error was unreachable.
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice", csrf: CSRF,
    json: { name: "Bad b64", source_type: "file", file_content_b64: "!!!!not base64!!!!", state: {} },
  });
  assert.strictEqual(res.status, 400);
});

test("an oversized state object is rejected", async () => {
  const big = { blob: "x".repeat(9 * 1024 * 1024) };
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice", csrf: CSRF,
    json: { name: "Huge", source_type: "file", state: big },
  });
  assert.ok(res.status === 400 || res.status === 413, `expected 400/413, got ${res.status}`);
});

test("the POST error path does not leak filesystem paths", async () => {
  // Pre-fix: this handler alone returned err.message verbatim, leaking absolute container paths
  // and errno detail out of the fs layer.
  const res = await H.request({
    url: srv.url, method: "POST", path: DS, as: "alice", csrf: CSRF,
    json: { name: "Bad b64", source_type: "file", file_content_b64: "!!!", state: {} },
  });
  assert.ok(!/[A-Za-z]:\\|\/app\/data|\/home\//.test(res.text), `path leaked: ${res.text}`);
});
