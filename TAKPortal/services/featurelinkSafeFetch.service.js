/**
 * Guarded outbound HTTP for the ArcGIS iconset generator (C-04).
 *
 * `fetchJson()` in featurelinkArcgisIconset.service.js fetched an arbitrary user-supplied URL with
 * no host allowlist, no private-IP block, no redirect limit, no timeout and no response-size cap,
 * and `canonicalizeUrl()` only required the path to contain `/FeatureServer` or `/MapServer`. So
 * every one of these reached the network from an unprivileged portal account:
 *
 *     http://169.254.169.254/latest/meta-data/FeatureServer/0   (cloud metadata credentials)
 *     http://127.0.0.1:8080/FeatureServer/0                     (loopback services)
 *     http://takserver:8443/FeatureServer/0                     (the whole Docker network)
 *
 * and the distinct error strings at the two failure sites made it a usable port scanner.
 *
 * What this module enforces, in order:
 *   1. Scheme must be http/https. No file:, gopher:, data:, ftp:.
 *   2. Host must match the allowlist (FEATURELINK_ARCGIS_ALLOWED_HOSTS, defaulting to the Esri
 *      public cloud). An empty allowlist means "deny everything" — never "allow everything".
 *   3. DNS is resolved HERE, every address is checked against the private/loopback/link-local/
 *      CGNAT/multicast/reserved ranges, and the connection is then made **to the checked IP** with
 *      the original hostname carried in the Host/SNI. That closes DNS rebinding: an allowlisted
 *      name that resolves to 169.254.169.254 is rejected, and a name that resolves differently on
 *      a second lookup cannot matter because there is no second lookup.
 *   4. Redirects are followed manually, capped, and every hop is re-validated from step 1.
 *   5. `AbortSignal.timeout()` bounds the whole exchange, and the body is read through a counting
 *      reader that aborts past a byte budget (a hostile host streaming 10 GB of JSON used to be
 *      an OOM).
 *
 * Uses node:https/node:http rather than global fetch precisely because fetch gives no hook to pin
 * the resolved address, which is what makes the rebinding defence possible.
 */

const dns = require("node:dns").promises;
const http = require("node:http");
const https = require("node:https");
const net = require("node:net");

// Esri's public multi-tenant cloud. An ArcGIS Enterprise deployment must add its own portal host
// via FEATURELINK_ARCGIS_ALLOWED_HOSTS — that is deliberate: the default must not reach anything
// on the operator's own network.
const DEFAULT_ALLOWED_HOSTS = ["*.arcgis.com", "*.arcgisonline.com", "*.esri.com"];

const REDIRECT_LIMIT = 3;
const DEFAULT_TIMEOUT_MS = 10_000;
const DEFAULT_MAX_BYTES = 8 * 1024 * 1024;

class BlockedRequestError extends Error {
  constructor(message) {
    super(message);
    this.name = "BlockedRequestError";
    this.blocked = true;
  }
}

/**
 * Operator-configured allowlist. Entries are hostnames or `*.suffix` wildcards, comma or
 * whitespace separated. Set FEATURELINK_ARCGIS_ALLOWED_HOSTS to point at an ArcGIS Enterprise
 * portal instead of / as well as ArcGIS Online.
 */
function allowedHostPatterns() {
  const raw = process.env.FEATURELINK_ARCGIS_ALLOWED_HOSTS;
  if (raw === undefined) return DEFAULT_ALLOWED_HOSTS.slice();
  return String(raw)
    .split(/[\s,]+/)
    .map((s) => s.trim().toLowerCase())
    .filter(Boolean);
}

function hostAllowed(hostname, patterns) {
  const h = String(hostname || "").toLowerCase().replace(/\.$/, "");
  if (!h) return false;
  for (const p of patterns) {
    if (p === h) return true;
    if (p.startsWith("*.")) {
      const suffix = p.slice(1); // ".arcgis.com"
      // Require a real label before the suffix so "*.arcgis.com" never matches "evilarcgis.com".
      if (h.endsWith(suffix) && h.length > suffix.length) return true;
    }
  }
  return false;
}

/** RFC1918 / loopback / link-local / CGNAT / multicast / reserved, v4 and v6. */
function isBlockedAddress(ip) {
  const v = net.isIP(ip);
  if (v === 4) {
    const p = ip.split(".").map(Number);
    if (p.length !== 4 || p.some((n) => !Number.isInteger(n) || n < 0 || n > 255)) return true;
    const [a, b] = p;
    if (a === 0) return true; // "this network"
    if (a === 10) return true; // RFC1918
    if (a === 127) return true; // loopback
    if (a === 169 && b === 254) return true; // link-local incl. cloud metadata
    if (a === 172 && b >= 16 && b <= 31) return true; // RFC1918
    if (a === 192 && b === 168) return true; // RFC1918
    if (a === 192 && b === 0) return true; // IETF protocol assignments / 192.0.0.0/24, 192.0.2.0/24
    if (a === 198 && (b === 18 || b === 19)) return true; // benchmarking
    if (a === 198 && b === 51) return true; // TEST-NET-2
    if (a === 203 && b === 0) return true; // TEST-NET-3
    if (a === 100 && b >= 64 && b <= 127) return true; // CGNAT
    if (a >= 224) return true; // multicast + reserved + broadcast
    return false;
  }
  if (v === 6) {
    const lower = ip.toLowerCase().split("%")[0];
    if (lower === "::" || lower === "::1") return true;
    if (lower.startsWith("fe80") || lower.startsWith("fe9") || lower.startsWith("fea") || lower.startsWith("feb")) return true; // link-local
    if (/^f[cd]/.test(lower)) return true; // unique local
    if (lower.startsWith("ff")) return true; // multicast
    // IPv4-mapped/compatible — re-check the embedded v4 address.
    const m = lower.match(/(?:^::ffff:|^::)((?:\d{1,3}\.){3}\d{1,3})$/);
    if (m) return isBlockedAddress(m[1]);
    return false;
  }
  return true; // not an IP at all
}

/**
 * Validates a URL and resolves it to a single vetted address.
 * @returns {Promise<{url: URL, address: string, family: 4|6}>}
 */
async function resolveAndVet(rawUrl, patterns) {
  let u;
  try {
    u = new URL(String(rawUrl));
  } catch (_) {
    throw new BlockedRequestError("That is not a valid URL.");
  }
  if (u.protocol !== "http:" && u.protocol !== "https:") {
    throw new BlockedRequestError(`Blocked: only http and https are allowed (got ${u.protocol.replace(":", "")}).`);
  }
  if (u.username || u.password) {
    throw new BlockedRequestError("Blocked: URLs with embedded credentials are not allowed.");
  }

  const hostname = u.hostname.replace(/^\[|\]$/g, "");

  // A literal IP still has to be allowlisted by name, so "http://10.0.0.5/FeatureServer/0" is
  // refused at the allowlist step rather than relying on the range check alone.
  if (!hostAllowed(u.hostname, patterns)) {
    throw new BlockedRequestError(
      `Blocked: "${u.hostname}" is not a permitted ArcGIS host. ` +
        "Ask your TAK administrator to add it to FEATURELINK_ARCGIS_ALLOWED_HOSTS."
    );
  }

  let addresses;
  if (net.isIP(hostname)) {
    addresses = [{ address: hostname, family: net.isIP(hostname) }];
  } else {
    try {
      addresses = await dns.lookup(hostname, { all: true, verbatim: true });
    } catch (_) {
      // Deliberately uniform with the block message below so this is not a DNS-existence oracle.
      throw new BlockedRequestError(`Blocked: "${u.hostname}" could not be reached.`);
    }
  }
  if (!addresses.length) throw new BlockedRequestError(`Blocked: "${u.hostname}" could not be reached.`);

  // ALL resolved addresses must be public. Rejecting on any bad address (rather than picking the
  // first good one) stops a multi-A-record host from smuggling an internal target through.
  for (const a of addresses) {
    if (isBlockedAddress(a.address)) {
      throw new BlockedRequestError(
        `Blocked: "${u.hostname}" resolves to a private, loopback or link-local address.`
      );
    }
  }

  const chosen = addresses[0];
  return { url: u, address: chosen.address, family: chosen.family };
}

/** One request to a pinned address, returning status/headers/body without following redirects. */
function requestOnce({ url, address, timeoutMs, maxBytes, headers }) {
  return new Promise((resolve, reject) => {
    const isHttps = url.protocol === "https:";
    const lib = isHttps ? https : http;
    const req = lib.request(
      {
        // Connect to the vetted IP; carry the real hostname for Host and TLS SNI so certificate
        // validation still happens against the name, not the address.
        host: address,
        port: url.port || (isHttps ? 443 : 80),
        path: url.pathname + url.search,
        method: "GET",
        headers: { Host: url.host, Accept: "application/json", ...headers },
        servername: isHttps ? url.hostname : undefined,
        timeout: timeoutMs,
      },
      (res) => {
        const declared = Number(res.headers["content-length"] || 0);
        if (declared && declared > maxBytes) {
          res.destroy();
          reject(new BlockedRequestError(`Blocked: response is too large (${declared} bytes).`));
          return;
        }
        const chunks = [];
        let total = 0;
        res.on("data", (c) => {
          total += c.length;
          if (total > maxBytes) {
            res.destroy();
            reject(new BlockedRequestError("Blocked: response exceeded the size limit."));
            return;
          }
          chunks.push(c);
        });
        res.on("end", () =>
          resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString("utf-8") })
        );
        res.on("error", reject);
      }
    );
    req.on("timeout", () => {
      req.destroy(new BlockedRequestError("Blocked: the ArcGIS host did not respond in time."));
    });
    req.on("error", reject);
    req.end();
  });
}

/**
 * GET a URL under every guard above and parse it as JSON.
 *
 * C-22: ArcGIS returns `{"error": {...}}` with HTTP 200, so transport status alone is not success.
 * Both are checked here, in one place, and surfaced as a typed error.
 * C-21: the caller passes the ArcGIS token as a Bearer header — never as a `?token=` query
 * parameter — and no URL is ever logged by this module.
 */
async function safeFetchArcgisJson(rawUrl, { token, timeoutMs = DEFAULT_TIMEOUT_MS, maxBytes = DEFAULT_MAX_BYTES } = {}) {
  const patterns = allowedHostPatterns();
  if (!patterns.length) {
    throw new BlockedRequestError(
      "Blocked: no ArcGIS hosts are permitted. Set FEATURELINK_ARCGIS_ALLOWED_HOSTS."
    );
  }

  let target = rawUrl;
  const headers = token ? { Authorization: `Bearer ${token}` } : {};

  for (let hop = 0; hop <= REDIRECT_LIMIT; hop++) {
    const vetted = await resolveAndVet(target, patterns); // re-validated on EVERY hop
    const res = await requestOnce({
      url: vetted.url,
      address: vetted.address,
      timeoutMs,
      maxBytes,
      headers,
    });

    if (res.status >= 300 && res.status < 400 && res.headers.location) {
      if (hop === REDIRECT_LIMIT) {
        throw new BlockedRequestError("Blocked: too many redirects.");
      }
      target = new URL(res.headers.location, vetted.url).toString();
      continue;
    }

    if (res.status < 200 || res.status >= 300) {
      const err = new Error(`ArcGIS request failed (HTTP ${res.status}).`);
      err.arcgisStatus = res.status;
      throw err;
    }

    let json;
    try {
      json = JSON.parse(res.body);
    } catch (_) {
      throw new Error("ArcGIS returned a response that is not JSON.");
    }
    if (json && json.error) {
      // C-22 — an ArcGIS error body delivered with HTTP 200.
      const detail = json.error.message || json.error.details || "unspecified";
      const err = new Error(`ArcGIS error: ${Array.isArray(detail) ? detail.join("; ") : detail}`);
      err.arcgisError = json.error;
      err.arcgisCode = json.error.code;
      throw err;
    }
    return json;
  }

  throw new BlockedRequestError("Blocked: too many redirects.");
}

module.exports = {
  safeFetchArcgisJson,
  BlockedRequestError,
  // exported for tests
  isBlockedAddress,
  hostAllowed,
  allowedHostPatterns,
  resolveAndVet,
  DEFAULT_ALLOWED_HOSTS,
  REDIRECT_LIMIT,
};
