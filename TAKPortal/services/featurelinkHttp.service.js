/**
 * Small shared HTTP concerns for the FeatureLink routers.
 *
 * Appendix D §2.1/§2.3: no route in this module set `Cache-Control: no-store` on an authenticated
 * JSON response, and none set `X-Content-Type-Options`, `X-Frame-Options` or `Referrer-Policy`.
 * The host portal may or may not run helmet; this module cannot assert that, so it sets its own.
 */

/** Authenticated JSON must not be written to a shared/back-button cache. */
function noStore(req, res, next) {
  res.setHeader("Cache-Control", "no-store, no-cache, must-revalidate, private");
  res.setHeader("Pragma", "no-cache");
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("Referrer-Policy", "same-origin");
  next();
}

/** Extra hardening for the HTML pages this module serves (configurator + admin views). */
function pageSecurityHeaders(req, res, next) {
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("X-Frame-Options", "SAMEORIGIN");
  res.setHeader("Referrer-Policy", "same-origin");
  next();
}

/** Parses a Cookie header into a plain object without assuming the host installed cookie-parser. */
function parseCookies(req) {
  const out = {};
  const raw = (req && req.headers && req.headers.cookie) || "";
  for (const part of raw.split(";")) {
    const i = part.indexOf("=");
    if (i < 0) continue;
    const k = part.slice(0, i).trim();
    if (!k) continue;
    try {
      out[k] = decodeURIComponent(part.slice(i + 1).trim());
    } catch (_) {
      out[k] = part.slice(i + 1).trim();
    }
  }
  return out;
}

/** True when this request reached us over TLS (directly or via a trusted proxy). */
function isSecureRequest(req) {
  if (req.secure) return true;
  const xf = req.headers["x-forwarded-proto"];
  if (!xf) return false;
  return String(xf).split(",")[0].trim().toLowerCase() === "https";
}

module.exports = { noStore, pageSecurityHeaders, parseCookies, isSecureRequest };
