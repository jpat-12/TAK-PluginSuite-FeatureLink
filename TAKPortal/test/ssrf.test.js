/**
 * C-04 — server-side request forgery guard.
 * Also covers the C-21 (Bearer token, never ?token=) and C-22 (ArcGIS error body with HTTP 200)
 * conventions on the server side.
 *
 * Pre-fix, `fetchJson()` in featurelinkArcgisIconset.service.js fetched an arbitrary user-supplied
 * URL with NO host allowlist, NO private-IP block, NO redirect limit, NO timeout and NO
 * response-size cap, and `canonicalizeUrl()` only required the path to contain /FeatureServer or
 * /MapServer. Every URL in the "must be refused" list below reached the network from an
 * unprivileged portal account, and the two distinct error strings made it a usable port scanner.
 */

const test = require("node:test");
const assert = require("node:assert");
const http = require("node:http");

const SF = require("../services/featurelinkSafeFetch.service");

// ---------------------------------------------------------------------------
// Address classification
// ---------------------------------------------------------------------------

test("C-04: every private, loopback, link-local and reserved range is blocked", () => {
  const blocked = [
    "169.254.169.254", // AWS/GCP/Azure metadata — credential theft
    "169.254.170.2", // ECS task metadata
    "127.0.0.1", "127.1.2.3", // loopback
    "0.0.0.0", // "this network"
    "10.0.0.5", "10.255.255.255", // RFC1918
    "172.16.0.1", "172.31.255.254", // RFC1918
    "192.168.1.1", // RFC1918
    "100.64.0.1", "100.127.255.255", // CGNAT
    "192.0.0.1", "192.0.2.5", // IETF assignments / TEST-NET-1
    "198.18.0.1", "198.51.100.7", "203.0.113.9", // benchmarking / TEST-NETs
    "224.0.0.1", "239.255.255.250", "255.255.255.255", // multicast / broadcast
    "::1", "::", // v6 loopback / unspecified
    "fe80::1", // v6 link-local
    "fc00::1", "fd12:3456::1", // v6 unique local
    "ff02::1", // v6 multicast
    "::ffff:127.0.0.1", // v4-mapped loopback
    "::ffff:169.254.169.254", // v4-mapped metadata
    "not-an-ip",
  ];
  for (const ip of blocked) {
    assert.strictEqual(SF.isBlockedAddress(ip), true, `${ip} must be blocked`);
  }
});

test("C-04: ordinary public addresses are not blocked", () => {
  for (const ip of ["8.8.8.8", "1.1.1.1", "52.94.236.248", "2606:4700:4700::1111"]) {
    assert.strictEqual(SF.isBlockedAddress(ip), false, `${ip} must be allowed`);
  }
});

test("C-04: 172.15 and 172.32 are public — the RFC1918 boundary is not off by one", () => {
  assert.strictEqual(SF.isBlockedAddress("172.15.255.255"), false);
  assert.strictEqual(SF.isBlockedAddress("172.32.0.1"), false);
  assert.strictEqual(SF.isBlockedAddress("172.16.0.0"), true);
  assert.strictEqual(SF.isBlockedAddress("172.31.255.255"), true);
});

// ---------------------------------------------------------------------------
// Host allowlist
// ---------------------------------------------------------------------------

test("C-04: the wildcard allowlist cannot be defeated by a suffix lookalike", () => {
  const pats = ["*.arcgis.com", "portal.example.mil"];
  assert.strictEqual(SF.hostAllowed("services1.arcgis.com", pats), true);
  assert.strictEqual(SF.hostAllowed("tiles.a.arcgis.com", pats), true);
  assert.strictEqual(SF.hostAllowed("portal.example.mil", pats), true);

  // The classic bypasses.
  assert.strictEqual(SF.hostAllowed("evilarcgis.com", pats), false, "no label boundary");
  assert.strictEqual(SF.hostAllowed("arcgis.com.evil.example", pats), false, "suffix, not prefix");
  assert.strictEqual(SF.hostAllowed("arcgis.com", pats), false, "bare apex is not *.apex");
  assert.strictEqual(SF.hostAllowed("portal.example.mil.evil.example", pats), false);
  assert.strictEqual(SF.hostAllowed("", pats), false);
});

test("C-04: an empty allowlist denies everything — it never means allow-all", async () => {
  const prev = process.env.FEATURELINK_ARCGIS_ALLOWED_HOSTS;
  process.env.FEATURELINK_ARCGIS_ALLOWED_HOSTS = "";
  try {
    assert.deepStrictEqual(SF.allowedHostPatterns(), []);
    await assert.rejects(
      () => SF.safeFetchArcgisJson("https://services1.arcgis.com/x/FeatureServer/0?f=json"),
      /no ArcGIS hosts are permitted/i
    );
  } finally {
    if (prev === undefined) delete process.env.FEATURELINK_ARCGIS_ALLOWED_HOSTS;
    else process.env.FEATURELINK_ARCGIS_ALLOWED_HOSTS = prev;
  }
});

test("C-04: the default allowlist reaches Esri's cloud and nothing on the operator's network", () => {
  const d = SF.DEFAULT_ALLOWED_HOSTS;
  assert.ok(SF.hostAllowed("services1.arcgis.com", d));
  assert.ok(!SF.hostAllowed("takserver", d));
  assert.ok(!SF.hostAllowed("localhost", d));
  assert.ok(!SF.hostAllowed("169.254.169.254", d));
});

// ---------------------------------------------------------------------------
// resolveAndVet — the full attack matrix from Appendix D §4.5
// ---------------------------------------------------------------------------

async function mustBlock(url, patterns, why) {
  await assert.rejects(
    () => SF.resolveAndVet(url, patterns),
    (err) => {
      assert.ok(err.blocked, `${url}: expected a BlockedRequestError, got ${err && err.name}`);
      return true;
    },
    why || url
  );
}

test("C-04: the checklist's SSRF matrix is refused", async () => {
  const pats = ["*.arcgis.com"];
  await mustBlock("http://169.254.169.254/latest/meta-data/FeatureServer/0", pats, "cloud metadata");
  await mustBlock("http://127.0.0.1:22/FeatureServer/0", pats, "loopback port scan");
  await mustBlock("http://[::1]/FeatureServer/0", pats, "v6 loopback");
  await mustBlock("http://10.0.0.5/FeatureServer/0", pats, "RFC1918");
  await mustBlock("http://takserver:8443/FeatureServer/0", pats, "docker network name");
  await mustBlock("http://localhost.evil.com/FeatureServer/0", pats, "rebinding-style host");
  await mustBlock("file:///etc/passwd", pats, "file scheme");
  await mustBlock("gopher://127.0.0.1:11211/_stats", pats, "gopher scheme");
  await mustBlock("ftp://services1.arcgis.com/x", pats, "ftp scheme");
  await mustBlock("https://user:pass@services1.arcgis.com/FeatureServer/0", pats, "embedded credentials");
  await mustBlock("not a url at all", pats, "malformed");
});

test("C-04: DNS rebinding is closed — an ALLOWLISTED name resolving to loopback is refused", async () => {
  // This is the case an allowlist alone does not stop, and the reason the address check happens
  // AFTER resolution and the connection is then made to the checked address. "localhost" is
  // explicitly permitted by name here and still refused because of where it resolves.
  await mustBlock("http://localhost:8080/FeatureServer/0", ["localhost"], "allowlisted name -> 127.0.0.1");
});

test("C-04: a permitted public host resolves and is vetted (the guard is not simply deny-all)", async (t) => {
  let vetted;
  try {
    vetted = await SF.resolveAndVet("https://services1.arcgis.com/x/FeatureServer/0", ["*.arcgis.com"]);
  } catch (err) {
    // Offline / DNS-blocked runner: the guard is correct either way, but this assertion needs DNS.
    t.skip(`DNS unavailable in this environment: ${err.message}`);
    return;
  }
  assert.strictEqual(vetted.url.hostname, "services1.arcgis.com");
  assert.strictEqual(SF.isBlockedAddress(vetted.address), false);
});

// ---------------------------------------------------------------------------
// Transport behaviour: timeout, size cap, redirects, error bodies, token handling
//
// These use a local stub server plus an injected resolver that applies the real
// scheme/credential/allowlist rules and then pins the address to the stub. The production entry
// point is unchanged; only the address-resolution dependency is substituted.
// ---------------------------------------------------------------------------

function pinnedResolver(port) {
  return async (rawUrl, patterns) => {
    const u = new URL(rawUrl);
    if (u.protocol !== "http:" && u.protocol !== "https:") {
      throw new SF.BlockedRequestError("Blocked: scheme");
    }
    if (!SF.hostAllowed(u.hostname, patterns)) {
      throw new SF.BlockedRequestError(`Blocked: "${u.hostname}" is not a permitted ArcGIS host.`);
    }
    u.port = String(port);
    return { url: u, address: "127.0.0.1", family: 4 };
  };
}

function stub(handler) {
  return new Promise((resolve) => {
    const s = http.createServer(handler);
    s.listen(0, "127.0.0.1", () => resolve({ port: s.address().port, close: () => new Promise((r) => s.close(r)) }));
  });
}

const PATS = ["*.arcgis.com"];
const URL_OK = "http://services1.arcgis.com/org/FeatureServer/0?f=json";

test("C-04: a host that accepts the connection and never responds is aborted", async () => {
  const s = await stub(() => { /* never respond — a tarpit */ });
  try {
    const started = Date.now();
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), timeoutMs: 700 }),
      /did not respond in time/i
    );
    assert.ok(Date.now() - started < 5000, "must abort promptly, not hang the handler");
  } finally {
    await s.close();
  }
});

test("C-04: an oversized response body is aborted rather than buffered", async () => {
  const s = await stub((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    const chunk = Buffer.alloc(64 * 1024, 0x20);
    // Stream indefinitely — pre-fix this was `await res.json()` with no cap at all.
    const timer = setInterval(() => {
      if (!res.write(chunk)) { /* backpressure is fine */ }
    }, 1);
    res.on("close", () => clearInterval(timer));
  });
  try {
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), maxBytes: 256 * 1024, timeoutMs: 5000 }),
      /too large|size limit/i
    );
  } finally {
    await s.close();
  }
});

test("C-04: a declared Content-Length over the cap is refused before reading the body", async () => {
  const s = await stub((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json", "Content-Length": String(50 * 1024 * 1024) });
    res.write("{");
  });
  try {
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), maxBytes: 1024, timeoutMs: 5000 }),
      /too large/i
    );
  } finally {
    await s.close();
  }
});

test("C-04: redirects are capped", async () => {
  let hops = 0;
  const s = await stub((req, res) => {
    hops++;
    res.writeHead(302, { Location: "http://services1.arcgis.com/next" });
    res.end();
  });
  try {
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), timeoutMs: 5000 }),
      /too many redirects/i
    );
    assert.ok(hops <= SF.REDIRECT_LIMIT + 1, `followed ${hops} hops, limit is ${SF.REDIRECT_LIMIT}`);
  } finally {
    await s.close();
  }
});

test("C-04: a redirect to a non-allowlisted host is re-validated and refused", async () => {
  const s = await stub((req, res) => {
    // The classic bypass: pass the allowlist on hop 1, then 302 to the metadata service.
    res.writeHead(302, { Location: "http://169.254.169.254/latest/meta-data/" });
    res.end();
  });
  try {
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), timeoutMs: 5000 }),
      /not a permitted ArcGIS host/i
    );
  } finally {
    await s.close();
  }
});

test("C-21: the ArcGIS token travels as a Bearer header and never in the URL", async () => {
  let seen = null;
  const s = await stub((req, res) => {
    seen = { auth: req.headers.authorization, url: req.url };
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ name: "Damage" }));
  });
  try {
    const json = await SF.safeFetchArcgisJson(URL_OK, {
      resolver: pinnedResolver(s.port), token: "SECRET-TOKEN", timeoutMs: 5000,
    });
    assert.strictEqual(json.name, "Damage");
    assert.strictEqual(seen.auth, "Bearer SECRET-TOKEN");
    assert.ok(!seen.url.includes("SECRET-TOKEN"), "the token must not appear in the request line");
    assert.ok(!seen.url.includes("token="), "no ?token= query parameter");
  } finally {
    await s.close();
  }
});

test("C-22: an ArcGIS error body returned with HTTP 200 is treated as a failure", async () => {
  const s = await stub((req, res) => {
    // This is what ArcGIS actually does: HTTP 200 with an error object in the body. Pre-fix at
    // most parse sites this rendered as success — a failure shown to the operator as "0 features".
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: { code: 498, message: "Invalid token", details: [] } }));
  });
  try {
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), timeoutMs: 5000 }),
      (err) => {
        assert.match(err.message, /Invalid token/);
        assert.strictEqual(err.arcgisCode, 498, "the error must be typed, not just a string");
        return true;
      }
    );
  } finally {
    await s.close();
  }
});

test("C-22: a non-2xx transport status is also a failure", async () => {
  const s = await stub((req, res) => { res.writeHead(503); res.end("busy"); });
  try {
    await assert.rejects(
      () => SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), timeoutMs: 5000 }),
      /HTTP 503/
    );
  } finally {
    await s.close();
  }
});

test("C-04: a successful fetch still works end to end", async () => {
  const s = await stub((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ name: "Damage Assessment", drawingInfo: { renderer: { type: "simple" } } }));
  });
  try {
    const json = await SF.safeFetchArcgisJson(URL_OK, { resolver: pinnedResolver(s.port), timeoutMs: 5000 });
    assert.strictEqual(json.name, "Damage Assessment");
  } finally {
    await s.close();
  }
});
