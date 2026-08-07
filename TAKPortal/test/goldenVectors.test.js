/**
 * C-39 — the cross-language conformance suite for AUTO-ICONSET-SPEC.md.
 *
 * Asserts the reference implementation against the SHARED fixture at
 * test/fixtures/auto-iconset-golden-vectors.json, which ATAK, WinTAK and CloudTAK also consume.
 * If this suite and one of theirs disagree, one implementation is wrong and icons silently fail to
 * resolve on-device — the exact failure the spec exists to prevent.
 *
 * Appendix D §3.1 recorded that not one of the spec's §9 conformance items was implemented and
 * that no test file existed anywhere in the repository.
 */

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");

const svc = require("../services/featurelinkArcgisIconset.service");
const icons = require("../services/featurelinkCustomIcons.service");

const FIXTURE = path.join(__dirname, "fixtures", "auto-iconset-golden-vectors.json");
const V = JSON.parse(fs.readFileSync(FIXTURE, "utf-8"));

test("the fixture is present and declares the spec version the code implements", () => {
  assert.strictEqual(V.specVersion, svc.SPEC_VERSION);
});

test("§2 canonicalizeUrl matches every golden vector", () => {
  for (const c of V.canonicalizeUrl.cases) {
    assert.strictEqual(svc.canonicalizeUrl(c.in), c.out, c.name || c.in);
  }
});

test("§2 canonicalizeUrl rejects what it must reject", () => {
  for (const c of V.canonicalizeUrl.mustThrow) {
    assert.throws(() => svc.canonicalizeUrl(c.in), c.name || c.in);
  }
});

test("§4 uidFor matches every golden vector, including the published spec vector", () => {
  for (const c of V.uidFor.cases) {
    assert.strictEqual(svc.uidFor(c.canonicalUrl, c.field), c.out, c.name);
  }
});

test("§5.1 groupFor matches every golden vector", () => {
  for (const c of V.groupFor.cases) {
    assert.strictEqual(svc.groupFor(c.in), c.out, c.name || JSON.stringify(c.in));
  }
});

test("§5.2 fileNameFor matches every golden vector", () => {
  for (const c of V.fileNameFor.cases) {
    assert.strictEqual(svc.fileNameFor(c.in), c.out, c.name || JSON.stringify(c.in));
  }
});

test("§5.3 dedupe matches every golden sequence", () => {
  for (const seq of V.dedupe.sequences) {
    const seen = new Set();
    const got = seq.labels.map((l) => svc.dedupe(svc.fileNameFor(l), seen));
    assert.deepStrictEqual(got, seq.out, seq.name);
  }
});

test("§5.4 usericonPath is a plain slash join of uid, group and filename", () => {
  for (const c of V.usericonPath.cases) {
    assert.strictEqual([c.uid, c.group, c.filename].join("/"), c.out);
  }
});

test("case folding matches the golden vectors (JS toLowerCase is locale-invariant)", () => {
  for (const c of V.caseFolding.cases) {
    assert.strictEqual(c.in.toLowerCase(), c.out, JSON.stringify(c.in));
  }
});

// ---------------------------------------------------------------------------
// C-39 — the double truncation itself
// ---------------------------------------------------------------------------

test("C-39: the 60-character cap is applied exactly ONCE, to the base, before the suffix", () => {
  const meta = V.c39DoubleTruncation;
  const B = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

  // Below the threshold nothing was ever wrong.
  const short = B.slice(0, meta.affectedThresholdSanitizedBaseChars);
  assert.strictEqual(svc.groupFor(short).length, 60);

  // One character past the threshold is where the old code silently truncated.
  const justOver = B.slice(0, meta.affectedThresholdSanitizedBaseChars + 1);
  assert.strictEqual(svc.groupFor(justOver).length, 61, "must NOT be capped back to 60");

  // The base cap still applies, once.
  const long = B + B;
  assert.strictEqual(svc.sanitizeGroupBase(long).length, meta.maxBaseChars);
  assert.strictEqual(svc.groupFor(long).length, meta.maxGroupChars);
});

test("C-39: storing a spec group does not truncate it a second time", () => {
  // This is the join between the two files that was broken: featurelinkArcgisIconset produced the
  // group, and featurelinkCustomIcons.cleanSetName() then cut the whole thing back to 60.
  const B = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (const n of [54, 55, 60, 62, 80]) {
    const group = svc.groupFor(B.repeat(2).slice(0, n));
    assert.strictEqual(
      icons.cleanSetName(group),
      group,
      `cleanSetName must be an identity on a spec group (base length ${n})`
    );
  }
});

test("C-39: the sanitizer REPLACES illegal characters, it does not delete them", () => {
  // The two functions disagreed: sanitizeGroupBase replaced with '_' (per the spec) while
  // cleanSetName deleted, so `Damage (2024)` produced two different strings from two code paths.
  assert.strictEqual(svc.sanitizeGroupBase("Damage (2024)"), "Damage _2024_");
  assert.strictEqual(icons.sanitizeSetName("Damage (2024)"), "Damage _2024_");
});

test("C-39: the UID does not depend on the layer name, so no UID changed", () => {
  const url = "https://services1.arcgis.com/abc/arcgis/rest/services/Damage/FeatureServer/0";
  const a = svc.uidFor(url, "damage_level");
  const b = svc.uidFor(url, "damage_level");
  assert.strictEqual(a, b);
  assert.strictEqual(a, V.uidFor.cases[0].out);
  assert.strictEqual(V.c39DoubleTruncation.uidChanged, false);
});

test("the fixture's own metadata agrees with the implementation's constants", () => {
  assert.strictEqual(V.c39DoubleTruncation.maxBaseChars, svc.GROUP_BASE_MAX);
  assert.strictEqual(V.c39DoubleTruncation.maxGroupChars, icons.SET_NAME_MAX);
  assert.strictEqual(
    V.c39DoubleTruncation.affectedThresholdSanitizedBaseChars,
    svc.GROUP_BASE_MAX - " Icons".length
  );
});
