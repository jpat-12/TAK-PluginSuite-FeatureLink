/**
 * C-11 — zip decompression bomb and entry flood.
 *
 * Pre-fix, `extractZipIcons()` decided whether an entry was safe by reading
 * `zipEntry.vars.uncompressedSize` — a number the ATTACKER wrote into their own zip's central
 * directory — and then called `zipEntry.buffer()`, which inflates the entry fully into memory with
 * no independent limit. A zip declaring 1 KB and delivering gigabytes of deflated zeroes OOM'd the
 * Node process: a single-request remote crash of the whole TAK Portal.
 *
 * The entry cap was equally hollow: it counted only successfully extracted IMAGES, so an archive
 * of 100,000 entries all named iconset.xml (or all non-images) never tripped it, and every one of
 * those was still buffered and regex-parsed.
 *
 * These tests build real zips with a real deflate stream, so the declared header size and the
 * delivered byte count genuinely disagree.
 */

const test = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");
const crypto = require("node:crypto");

const svc = require("../services/featurelinkCustomIcons.service");
const H = require("./helpers/portalHarness");

// ---------------------------------------------------------------------------
// A minimal zip writer that lets us LIE in the size fields.
// ---------------------------------------------------------------------------

function crc32(buf) {
  let table = crc32._t;
  if (!table) {
    table = crc32._t = new Int32Array(256);
    for (let i = 0; i < 256; i++) {
      let c = i;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      table[i] = c;
    }
  }
  let crc = -1;
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ table[(crc ^ buf[i]) & 0xff];
  return (crc ^ -1) >>> 0;
}

/**
 * @param {{name: string, data: Buffer, declaredSize?: number}[]} entries
 *        `declaredSize` overrides the uncompressed size written into BOTH headers — this is the
 *        lie the old code trusted.
 */
function makeZip(entries) {
  const local = [];
  const central = [];
  let offset = 0;
  for (const e of entries) {
    const nameBuf = Buffer.from(e.name, "utf8");
    const deflated = zlib.deflateRawSync(e.data, { level: 9 });
    const crc = crc32(e.data);
    const declared = e.declaredSize === undefined ? e.data.length : e.declaredSize;

    const lfh = Buffer.alloc(30);
    lfh.writeUInt32LE(0x04034b50, 0);
    lfh.writeUInt16LE(20, 4);
    lfh.writeUInt16LE(0, 6);
    lfh.writeUInt16LE(8, 8); // deflate
    lfh.writeUInt32LE(crc, 14);
    lfh.writeUInt32LE(deflated.length, 18);
    lfh.writeUInt32LE(declared, 22);
    lfh.writeUInt16LE(nameBuf.length, 26);
    local.push(lfh, nameBuf, deflated);

    const cdh = Buffer.alloc(46);
    cdh.writeUInt32LE(0x02014b50, 0);
    cdh.writeUInt16LE(20, 4);
    cdh.writeUInt16LE(20, 6);
    cdh.writeUInt16LE(0, 8);
    cdh.writeUInt16LE(8, 10);
    cdh.writeUInt32LE(crc, 16);
    cdh.writeUInt32LE(deflated.length, 20);
    cdh.writeUInt32LE(declared, 24);
    cdh.writeUInt16LE(nameBuf.length, 28);
    cdh.writeUInt32LE(offset, 42);
    central.push(cdh, nameBuf);

    offset += 30 + nameBuf.length + deflated.length;
  }
  const cd = Buffer.concat(central);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  eocd.writeUInt16LE(entries.length, 8);
  eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(cd.length, 12);
  eocd.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, cd, eocd]);
}

function png(sizeBytes) {
  // A real PNG magic header padded out — passes the magic-byte check.
  const head = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  return Buffer.concat([head, Buffer.alloc(Math.max(0, sizeBytes - head.length), 0)]);
}

let tmp;
function writeZip(name, buf) {
  if (!tmp) tmp = H.tmpDir("flzip-");
  const p = path.join(tmp, name);
  fs.writeFileSync(p, buf);
  return { path: p, originalname: name, mimetype: "application/zip", size: buf.length };
}

const ACTOR = { username: "alice", isAdmin: false };

test.before(() => H.resetStore());
test.after(() => {
  H.resetStore();
  if (tmp) fs.rmSync(tmp, { recursive: true, force: true });
});

// ---------------------------------------------------------------------------

test("C-11: a lying header does not get an entry past the budget", async () => {
  // Declares 1 KB, delivers 24 MB of zeroes that deflate to almost nothing. The old per-entry
  // check read the declared 1 KB, decided it was fine, and inflated all 24 MB.
  const bomb = png(24 * 1024 * 1024);
  const zip = writeZip("bomb.zip", makeZip([{ name: "Group/bomb.png", data: bomb, declaredSize: 1024 }]));
  assert.ok(fs.statSync(zip.path).size < 200 * 1024, "the archive itself must be tiny — that is the bomb");

  const res = await svc.addCustomIcons("Bomb set", [zip], ACTOR);
  assert.strictEqual(res.success, false, "the oversized entry must not be extracted");
  assert.match(res.error, /No image files found|too large/i);
});

test("C-11: the whole-archive decompressed budget is enforced across entries", async () => {
  // Each entry is individually under the per-file cap; together they blow the archive budget.
  const per = 1.5 * 1024 * 1024;
  const entries = [];
  for (let i = 0; i < 60; i++) {
    entries.push({ name: `G/f${i}.png`, data: png(per), declaredSize: 512 });
  }
  const zip = writeZip("many.zip", makeZip(entries));
  const res = await svc.addCustomIcons("Budget set", [zip], ACTOR);
  assert.strictEqual(res.success, false);
  assert.match(res.error, /decompressed limit|too large|No image files/i);
});

test("C-11: an entry flood is capped before any per-entry work", async () => {
  // 100,000 entries all named iconset.xml never tripped the old counter, because it only counted
  // extracted images — yet every one was buffered and regex-parsed.
  const entries = [];
  for (let i = 0; i < svc.ZIP_MAX_ENTRIES_SCANNED + 50; i++) {
    entries.push({ name: `d${i}/iconset.xml`, data: Buffer.from("<iconset name='x' uid='y'/>") });
  }
  const zip = writeZip("flood.zip", makeZip(entries));
  const res = await svc.addCustomIcons("Flood set", [zip], ACTOR);
  assert.strictEqual(res.success, false);
  assert.match(res.error, /entries|limit/i);
});

test("C-11: the extracted-image count is still capped", async () => {
  const entries = [];
  for (let i = 0; i < svc.ZIP_ENTRY_LIMIT + 25; i++) {
    entries.push({ name: `G/icon${i}.png`, data: png(64) });
  }
  const zip = writeZip("lots.zip", makeZip(entries));
  const res = await svc.addCustomIcons("Capped set", [zip], ACTOR);
  assert.strictEqual(res.success, true);
  assert.strictEqual(res.set.icons.length, svc.ZIP_ENTRY_LIMIT);
});

test("C-11: path-traversal entry names are rejected outright", async () => {
  const bad = [
    "../../../../etc/cron.d/x.png",
    "/etc/passwd.png",
    "C:\\Windows\\System32\\evil.png",
    "..\\..\\evil.png",
    "./x/../../../evil.png",
    "//server/share/evil.png",
  ];
  for (const name of bad) {
    assert.strictEqual(svc.isUnsafeEntryPath(name), true, `${name} must be rejected`);
  }
  for (const name of ["Group/icon.png", "icon.png", "A Group/Sub/icon.png"]) {
    assert.strictEqual(svc.isUnsafeEntryPath(name), false, `${name} must be accepted`);
  }
  assert.strictEqual(svc.isUnsafeEntryPath("evil\u0000.png"), true, "NUL byte");
});

test("C-11: a traversal group never propagates into the outbound iconset (zip-slip in the export)", async () => {
  // Local writes were already safe via path.basename(), but the ATAK-facing group is the entry's
  // FIRST path segment and is written verbatim into every generated config AND into the exported
  // zip's entry paths — so `../../evil` escaped through the archive this portal produces.
  const g = svc.resolveAtakGroupAndName("../../evil/icon.png");
  assert.ok(!g.group.includes(".."), `group must be sanitized, got ${g.group}`);
  assert.ok(!g.group.includes("/") && !g.group.includes("\\"));
});

test("C-11: a file that is not really an image is rejected despite its extension", async () => {
  // Extension-only validation let a PHP/JSP/HTML payload named x.png be stored and then served
  // from the portal origin.
  const evil = Buffer.from("<?php system($_GET['c']); ?>", "utf-8");
  const zip = writeZip("fake.zip", makeZip([{ name: "G/shell.png", data: evil }]));
  const res = await svc.addCustomIcons("Fake image set", [zip], ACTOR);
  assert.strictEqual(res.success, false);
  assert.match(res.error, /No image files found/i);
});

test("C-11: SVG is no longer an accepted icon type", async () => {
  const svg = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>', "utf-8");
  const zip = writeZip("svg.zip", makeZip([{ name: "G/x.svg", data: svg }]));
  const res = await svc.addCustomIcons("Svg set", [zip], ACTOR);
  assert.strictEqual(res.success, false, "an SVG served as image/svg+xml executes on the portal origin");
});

test("C-11: a legitimate iconset zip still imports correctly", async () => {
  const uid = crypto.randomBytes(16).toString("hex");
  const zip = writeZip(
    "good.zip",
    makeZip([
      { name: "iconset.xml", data: Buffer.from(`<iconset name="Good" uid="${uid}" defaultGroup="Good"/>`) },
      { name: "Good/alpha.png", data: png(256) },
      { name: "Good/bravo.png", data: png(256) },
    ])
  );
  const res = await svc.addCustomIcons("Good set", [zip], ACTOR);
  assert.strictEqual(res.success, true, res.error);
  assert.strictEqual(res.set.icons.length, 2);
  assert.strictEqual(res.set.uid, uid, "a valid iconset.xml uid must be trusted verbatim");
  assert.strictEqual(res.set.groups["alpha.png"], "Good");
});

test("C-03: a second user cannot append to, or re-uid, someone else's icon set", async () => {
  const zip1 = writeZip("a.zip", makeZip([{ name: "Shared/one.png", data: png(128) }]));
  const first = await svc.addCustomIcons("Shared set", [zip1], { username: "alice", isAdmin: false });
  assert.strictEqual(first.success, true, first.error);

  const zip2 = writeZip(
    "b.zip",
    makeZip([
      { name: "iconset.xml", data: Buffer.from('<iconset name="Shared set" uid="attackeruid"/>') },
      { name: "Shared/two.png", data: png(128) },
    ])
  );
  const second = await svc.addCustomIcons("Shared set", [zip2], { username: "bob", isAdmin: false });
  assert.strictEqual(second.success, false, "appending to another user's set must be refused");
  assert.strictEqual(second.status, 403);

  const sets = svc.listCustomIconSets();
  const shared = sets.find((s) => s.name === "Shared set");
  assert.strictEqual(shared.icons.length, 1, "the set must be untouched");
  assert.notStrictEqual(shared.uid, "attackeruid", "the uid must not have been repointed");
});

test("path traversal in the icon-serving path is rejected, not silently rewritten", async () => {
  // Pre-fix, getCustomIconPath() ran `"..".replace(/[^a-zA-Z0-9._-]/g, "_")`, which leaves `..`
  // intact because dots are in the allowlist — so a real parent directory resolved and a non-null
  // path was handed to res.sendFile().
  for (const [name, file] of [
    ["..", "x.png"],
    ["Good set", ".."],
    ["..", ".."],
    ["Good set", "../../../../etc/passwd"],
    ["Good set", "..%2f..%2fetc%2fpasswd"],
    ["Good/set", "x.png"],
    ["Good set", "x.png\u0000.txt"],
    ["Good set", "notanimage.txt"],
  ]) {
    assert.strictEqual(svc.getCustomIconPath(name, file), null, `${name} / ${file} must be refused`);
  }
});

test("the zip writer refuses counts and sizes it cannot represent", () => {
  // buildStoredZip writes 16-bit entry counts and 32-bit sizes with no ZIP64 support; overflowing
  // them produced a silently corrupt archive.
  const many = new Array(70000).fill(null).map((_, i) => ({ path: `g/${i}.png`, data: Buffer.alloc(0) }));
  assert.throws(() => svc.buildStoredZip(many), /65,535/);
});
