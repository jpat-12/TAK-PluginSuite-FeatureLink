/**
 * In-process rate limiting, per-user byte quotas, and per-user concurrency caps.
 *
 * Appendix D §2.3/§2.4: no rate limit existed on any of the 19 routes. `/from-arcgis` makes an
 * outbound fetch, `POST /custom-icons` accepted 25 MB x 200 files = 5 GB of disk per request, and
 * `/usage` performs two synchronous full-file JSON reads per saved dataset. Any logged-in user
 * could wedge the portal or fill its disk by polling one endpoint.
 *
 * Deliberately in-process and dependency-free: the TAK Portal deployment this installs into is a
 * single Docker Compose service with one Node process, and adding a Redis dependency to a
 * DDIL-targeted product to rate-limit an admin tool is the wrong trade. If the host is ever scaled
 * horizontally these limits become per-instance — recorded in docs/remediation/wp4-server.md.
 */

const buckets = new Map(); // key -> { tokens, updatedAt }
const quotas = new Map(); // key -> { bytes, windowStart }
const inflight = new Map(); // key -> count

/**
 * Token-bucket limiter.
 * @param {string} key     usually `${routeName}:${username}`
 * @param {number} limit   burst size / tokens
 * @param {number} windowMs time to fully refill
 * @returns {{allowed: boolean, retryAfterMs: number}}
 */
function take(key, limit, windowMs) {
  const now = Date.now();
  let b = buckets.get(key);
  if (!b) {
    b = { tokens: limit, updatedAt: now };
    buckets.set(key, b);
  }
  const refill = ((now - b.updatedAt) / windowMs) * limit;
  b.tokens = Math.min(limit, b.tokens + refill);
  b.updatedAt = now;
  if (b.tokens < 1) {
    return { allowed: false, retryAfterMs: Math.ceil(((1 - b.tokens) / limit) * windowMs) };
  }
  b.tokens -= 1;
  return { allowed: true, retryAfterMs: 0 };
}

/**
 * Express middleware factory.
 * @param {string} name   route label for the bucket key
 * @param {number} limit  requests per window
 * @param {number} windowMs
 */
function rateLimit(name, limit, windowMs) {
  return function rateLimitMiddleware(req, res, next) {
    const who = (req.authentikUser && req.authentikUser.username) || req.ip || "anonymous";
    const { allowed, retryAfterMs } = take(`${name}:${who}`, limit, windowMs);
    if (!allowed) {
      res.setHeader("Retry-After", Math.ceil(retryAfterMs / 1000));
      return res.status(429).json({ ok: false, error: "Too many requests — slow down and try again." });
    }
    return next();
  };
}

/**
 * Rolling per-user byte quota.
 * @returns {{allowed: boolean, used: number, limit: number}}
 */
function consumeQuota(key, bytes, limitBytes, windowMs) {
  const now = Date.now();
  let q = quotas.get(key);
  if (!q || now - q.windowStart > windowMs) {
    q = { bytes: 0, windowStart: now };
    quotas.set(key, q);
  }
  if (q.bytes + bytes > limitBytes) {
    return { allowed: false, used: q.bytes, limit: limitBytes };
  }
  q.bytes += bytes;
  return { allowed: true, used: q.bytes, limit: limitBytes };
}

/** Per-user concurrency guard. Returns a release function, or null when at capacity. */
function acquire(key, max) {
  const n = inflight.get(key) || 0;
  if (n >= max) return null;
  inflight.set(key, n + 1);
  let released = false;
  return function release() {
    if (released) return;
    released = true;
    const cur = inflight.get(key) || 1;
    if (cur <= 1) inflight.delete(key);
    else inflight.set(key, cur - 1);
  };
}

/** Test hook — drops all counters. */
function _reset() {
  buckets.clear();
  quotas.clear();
  inflight.clear();
}

module.exports = { take, rateLimit, consumeQuota, acquire, _reset };
