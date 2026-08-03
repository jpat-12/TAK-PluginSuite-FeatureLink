/**
 * CSRF protection for the FeatureLink routers (C-10).
 *
 * Audit background: `grep -r 'csrf\|SameSite' TAKPortal/` returned zero files. Every mutating
 * route — POST /datasets, POST /custom-icons, POST /from-arcgis, DELETE /datasets/:id,
 * DELETE /custom-icons/:name — is cookie-authenticated through the portal's Authentik session and
 * accepts simple/multipart/JSON bodies, so any external page a portal admin visited could silently
 * delete their configs and icon sets, or point an icon set at attacker-chosen images.
 *
 * This module is installed into a host TAK Portal whose session cookie and middleware stack we do
 * not own, so we cannot rely on the host's cookie flags or on a server-side session store being
 * available. The defence is therefore layered and self-contained:
 *
 *   1. **Origin/Referer assertion.** Every mutating verb must carry an `Origin` (or, failing that,
 *      a `Referer`) whose host matches the host the request was addressed to. A request with
 *      neither is refused: these routes are only ever called by this module's own pages, and the
 *      ATAK/WinTAK/CloudTAK plugins never call them (they download configs through the field-user
 *      browse API, which is read-only).
 *   2. **Double-submit token.** A CSPRNG token is issued in the `fl_csrf` cookie
 *      (`SameSite=Lax`, `Path=/`, `Secure` on TLS, deliberately NOT HttpOnly so the page's own
 *      script can echo it) and must be echoed in the `X-FeatureLink-CSRF` header. A cross-site
 *      page cannot read the cookie, so it cannot produce the header. Compared timing-safely.
 *
 * `SameSite=Lax` on our own cookie does not protect the *portal session* cookie — that one belongs
 * to the host. See docs/remediation/wp4-server.md and QUESTIONS-FOR-OWNER.md (WP4) for the
 * recommendation that the host portal set `SameSite=Lax; Secure; HttpOnly` on its session cookie.
 */

const crypto = require("crypto");
const { parseCookies, isSecureRequest } = require("./featurelinkHttp.service");

const COOKIE_NAME = "fl_csrf";
const HEADER_NAME = "x-featurelink-csrf";
const TOKEN_BYTES = 32;
const SAFE_METHODS = new Set(["GET", "HEAD", "OPTIONS"]);

function newToken() {
  return crypto.randomBytes(TOKEN_BYTES).toString("hex");
}

function timingSafeEqual(a, b) {
  const ab = Buffer.from(String(a || ""), "utf-8");
  const bb = Buffer.from(String(b || ""), "utf-8");
  if (ab.length === 0 || ab.length !== bb.length) return false;
  return crypto.timingSafeEqual(ab, bb);
}

/** Appends a Set-Cookie without clobbering anything the host portal already queued. */
function appendSetCookie(res, value) {
  const existing = res.getHeader("Set-Cookie");
  if (!existing) res.setHeader("Set-Cookie", value);
  else res.setHeader("Set-Cookie", [].concat(existing, value));
}

/**
 * Middleware for the HTML page routes: makes sure the browser holds a token cookie, and exposes
 * it to the template as `res.locals.flCsrfToken`.
 */
function issueCsrfCookie(req, res, next) {
  const cookies = parseCookies(req);
  let token = cookies[COOKIE_NAME];
  if (!token || !/^[a-f0-9]{64}$/.test(token)) {
    token = newToken();
    const attrs = [
      `${COOKIE_NAME}=${token}`,
      "Path=/",
      "SameSite=Lax",
      `Max-Age=${12 * 60 * 60}`,
    ];
    if (isSecureRequest(req)) attrs.push("Secure");
    appendSetCookie(res, attrs.join("; "));
  }
  res.locals.flCsrfToken = token;
  next();
}

/** Host (incl. port) this request was addressed to, honouring a trusted proxy's forwarded host. */
function expectedHost(req) {
  const fwd = req.headers["x-forwarded-host"];
  const host = (fwd ? String(fwd).split(",")[0] : req.headers.host) || "";
  return host.trim().toLowerCase();
}

function originHost(value) {
  if (!value) return null;
  try {
    return new URL(value).host.toLowerCase();
  } catch (_) {
    return null;
  }
}

/**
 * Middleware for every mutating verb. Safe methods pass through untouched.
 */
function requireCsrf(req, res, next) {
  if (SAFE_METHODS.has((req.method || "GET").toUpperCase())) return next();

  // 1. Origin / Referer
  const want = expectedHost(req);
  const got = originHost(req.headers.origin) || originHost(req.headers.referer);
  if (!want || !got || got !== want) {
    return res.status(403).json({
      ok: false,
      error: "Request blocked: cross-site or missing Origin. Reload the page and try again.",
    });
  }

  // 2. Double-submit token
  const cookies = parseCookies(req);
  const cookieToken = cookies[COOKIE_NAME];
  const headerToken = req.headers[HEADER_NAME] || (req.body && req.body._csrf);
  if (!cookieToken || !headerToken || !timingSafeEqual(cookieToken, headerToken)) {
    return res.status(403).json({
      ok: false,
      error: "Request blocked: missing or invalid CSRF token. Reload the page and try again.",
    });
  }

  return next();
}

module.exports = {
  COOKIE_NAME,
  HEADER_NAME,
  issueCsrfCookie,
  requireCsrf,
  newToken,
  // exported for tests
  _timingSafeEqual: timingSafeEqual,
};
