/**
 * FeatureLink access control — one place that decides "who is calling and what may they touch".
 *
 * Audit background (C-03 + Appendix D §2.1):
 *   - Ownership checks were applied to some routes and simply omitted on others, so any logged-in
 *     user could read another tenant's full saved config (including `state.cfg`, `source_url` and
 *     any pasted token) and stream their raw uploaded dataset bytes.
 *   - `isOwnedBy()` returned true when `created_by` was unset, so every record written before
 *     ownership tracking existed — and everything written by the Flask module, which has no
 *     ownership model at all — was editable and deletable by anyone. Fail-open authorization.
 *   - Admin detection was an inline `typeof res.locals.perm === "function" && res.locals.perm(...)`
 *     duplicated per route, which silently degrades to "not an admin" if the host portal ever
 *     stops attaching `res.locals.perm` (rename, mount-order change, error path).
 *
 * Policy implemented here:
 *   - A request with no resolved portal user is refused (401). These routers are only ever
 *     mounted behind the portal's session middleware; if that stops being true we fail closed
 *     rather than serving anonymously.
 *   - "Admin" means holding `page.featurelink_configs`. If `res.locals.perm` is missing we treat
 *     the caller as a non-admin AND log loudly, because that combination means the host
 *     middleware contract has been broken and every admin in the deployment just lost access.
 *   - An unowned record (`created_by` null/empty) is ADMIN-ONLY. This is the deliberate reversal
 *     of the old fail-open behaviour. Legacy/Flask-written records are therefore readable and
 *     deletable by admins only until an owner is stamped on them.
 */

const PERM_ID = "page.featurelink_configs";

let warnedMissingPerm = false;

/**
 * @returns {{username: string|null, isAdmin: boolean, permAvailable: boolean}}
 */
function actorOf(req, res) {
  const username = (req && req.authentikUser && req.authentikUser.username) || null;
  const permFn = res && res.locals && res.locals.perm;
  const permAvailable = typeof permFn === "function";

  if (!permAvailable && !warnedMissingPerm) {
    warnedMissingPerm = true;
    console.error(
      "[featurelink] res.locals.perm is not attached to this request — portalAuth.middleware.js " +
        "did not run before the FeatureLink routers, or its contract changed. Treating every " +
        `caller as a non-admin; holders of ${PERM_ID} will be denied cross-record access until ` +
        "the mount order is fixed."
    );
  }

  let isAdmin = false;
  if (permAvailable) {
    try {
      isAdmin = !!permFn(PERM_ID);
    } catch (err) {
      console.error("[featurelink] res.locals.perm threw — treating caller as non-admin:", err && err.message);
      isAdmin = false;
    }
  }

  return { username, isAdmin, permAvailable };
}

/**
 * Resolves the caller and short-circuits with 401 when there is no portal identity.
 * @returns {{username: string, isAdmin: boolean}|null} null means a response has been sent.
 */
function requireActor(req, res) {
  const actor = actorOf(req, res);
  if (!actor.username) {
    res.status(401).json({ ok: false, error: "Not signed in." });
    return null;
  }
  return actor;
}

/**
 * May this actor read/modify/delete this record? Owner or admin. An unowned record is admin-only
 * (fail closed — see the header note).
 * @param {{created_by?: string|null}} record
 */
function canAccessRecord(actor, record) {
  if (!actor) return false;
  if (actor.isAdmin) return true;
  if (!record) return false;
  const owner = record.created_by;
  if (!owner) return false; // unowned => admin-only
  return owner === actor.username;
}

/** Human-readable refusal, reused so the wording (and therefore the oracle it gives) is uniform. */
const DENIED = "Only the creator (or an admin) can do this.";

module.exports = { PERM_ID, actorOf, requireActor, canAccessRecord, DENIED };
