# WP4 — TAK Portal + Infra-TAK + OAuth Relay — Remediation Report

**Scope:** `TAKPortal/**`, `Infra-TAK/**`, `docs/featurelink-oauth-relay.html`.
**Branch:** `audit-remediation`, forked at `a8f6f4a`.
**Toolchain actually used:** node 24.17.0, npm 11.13.0, python 3.13.14. Tests were **executed**,
not merely written; output is pasted below.

---

## CROSS-PACKAGE REQUESTS — read this first

### 1. The reference implementation changed behaviour (C-39). WP1, WP2, WP3 must read this.

`TAKPortal/services/featurelinkArcgisIconset.service.js` is the checklist's *"only correct
implementation in the suite"* for symbology resolution and the other three platforms are being
ported from it. Fixing C-39 is a **behaviour change**, not just a security posture change:

- **No UID changed, for any input.** `uid = SHA-256(canonicalUrl + "/" + field)` — the layer name
  is not an input to it. Every previously-computed UID is still correct. (The coordinator's brief
  described this as a UID change; it is not. It is a **group** change.)
- **The group changed** for layer names whose sanitized base exceeds **54 characters**. The Portal
  used to apply the 60-char cap twice — once to the base per spec §5.1, then again to the whole
  `"<base> Icons"` string — so it stored a 60-char truncation while ATAK, WinTAK and CloudTAK all
  produced the full string of up to 66. **ATAK's own truncation was correct; the second cap was
  ours.**
- The Portal also disagreed with *itself*: the truncated value was returned to the caller as
  `group` while the manifest's per-icon `groups` map was written untruncated.
- **Migration:** no UID migration, and no saved config's `usericonPath` needs rewriting — those
  already carried the untruncated group. What needs attention is the *stored icon-set name*.
  Detection recipe and remediation steps: `docs/testing/server.md` §9.

**Shared golden-vector fixture — the path you need:**

```
TAKPortal/test/fixtures/auto-iconset-golden-vectors.json
```

Load it; do not regenerate it to make a port pass. A disagreement means one implementation is
wrong, and the field failure is silent (icons do not resolve on-device). It covers
`canonicalizeUrl` (6 cases + 5 required rejections), `uidFor` (including the published spec §4
vector `9aa866…2ad9`), `groupFor` (13 cases including the 54/55/60/61 boundary, CJK, latin
diacritics and an emoji surrogate pair), `fileNameFor`, `dedupe`, `usericonPath` and case folding.

Three traps recorded in the fixture that a port will otherwise get wrong:

1. **An emoji is a surrogate pair.** `"Fire 🔥 Layer"` must yield `"Fire __ Layer Icons"` — **two**
   underscores. Java, C# and JS iterate UTF-16 code units and get this right naturally; a Python
   port iterating code points would produce **one** and diverge.
2. **A slash in a label is a path separator, not an illegal character.** `fileNameFor("A/B")` is
   `"B.png"`, not `"A_B.png"` — the path prefix is stripped first (§5.2). I got this wrong in my
   own first draft of the fixture and the test caught it.
3. **U+0130 (İ) case folding diverges in .NET.** Java `toLowerCase(Locale.ROOT)`, JS and Python all
   produce `i` + U+0307; some .NET runtimes' `ToLowerInvariant` produce a bare `i`. This cannot bite
   today because the only string `canonicalizeUrl` folds is the **host**, which is ASCII by
   DNS/IDNA. It is recorded so nobody extends folding to the path or the layer name.

Also note the sanitizer semantics: the spec **replaces** illegal characters with `_` and does not
delete them. `"Damage (2024)"` → `"Damage _2024_ Icons"`. The Portal had one function replacing and
another deleting.

### 2. WP2 (CloudTAK) — the OAuth relay contract changed

`docs/featurelink-oauth-relay.html` no longer derives its `postMessage` target from `state`.
Consequences for the CloudTAK client:

- `state` is now treated as an **opaque nonce**. It is echoed back verbatim (so the existing
  pending-request check in C-09 still works) but it no longer selects a destination. CloudTAK may
  stop packing its origin into `state`; sending a bare CSPRNG nonce is now the correct shape. The
  legacy `origin::nonce` form still round-trips untouched, so no lockstep change is required.
- **Deployment step:** each CloudTAK/Portal origin must be listed in `REGISTERED_ORIGINS` in the
  relay page. It ships **empty**, so sign-in refuses until an operator edits it.
  `docs/testing/server.md` §0.3.
- The relay `postMessage`s once per registered origin; the browser delivers only to an opener whose
  real origin matches, so an unregistered opener receives nothing.

**PKCE is still not implemented and belongs to CloudTAK/ATAK/WinTAK, not to the relay.** The relay
fix stops the code being *delivered* to an attacker origin; PKCE is what makes a leaked code
useless. I recommend WP2 add it.

### 3. TAK Portal upstream (not in any work package's tree)

The host portal's Authentik session cookie needs `SameSite=Lax; Secure; HttpOnly`. This module
cannot set it — it owns only its own `fl_csrf` cookie. See OWNER DECISIONS #2.

---

## Per-C-ID status

| C-ID | Status | Files touched | Test | Evidence |
|---|---|---|---|---|
| **C-01** | **FIXED** | `docs/featurelink-oauth-relay.html` | `test/oauthRelay.test.js` (9) | Target now comes only from a deployment-controlled `REGISTERED_ORIGINS` allowlist; `state` is opaque and echoed, never parsed. 7 of 9 tests fail against `a8f6f4a`, including the literal attack string `https://evil.example::abc123nonce`. |
| **C-03** | **FIXED** | `routes/featurelinkDatasetsAdmin.routes.js`, `routes/featurelinkCustomIcons.routes.js`, `routes/featurelinkBrowse.routes.js`, `services/featurelinkAccess.service.js` (new), `services/featurelinkDatasets.service.js`, `services/featurelinkCustomIcons.service.js` | `test/authz.test.js` (21), `test/zipbomb.test.js` | All 19 routes audited, not just the 4 cited — see "Full route audit" below. |
| **C-04** | **FIXED** | `services/featurelinkSafeFetch.service.js` (new), `services/featurelinkArcgisIconset.service.js`, `routes/featurelinkCustomIcons.routes.js` | `test/ssrf.test.js` (18) | Host allowlist + post-DNS private-range check (closes rebinding) + 10 s timeout + 3-hop redirect cap with per-hop re-validation + 8 MB response cap + 2/user concurrency + 12/min rate limit. |
| **C-05** | **FIXED** | `views/featurelink-configs.ejs`, `views/featurelink.ejs`, both `index.html` copies | `test/configuratorXss.test.js` (13) | 4 cited sites plus 5 more found by sweep. |
| **C-10** | **FIXED (Node)** / **DEFERRED (Flask)** | `services/featurelinkCsrf.service.js` (new), `services/featurelinkHttp.service.js` (new), all 4 route files, both client pages | `test/authz.test.js` | Origin/Referer assertion + double-submit token on every mutating verb. Flask side untouched — see Deferred. |
| **C-11** | **FIXED** | `services/featurelinkCustomIcons.service.js`, `routes/featurelinkCustomIcons.routes.js` | `test/zipbomb.test.js` (12) | Budget measured **during inflation**; `uncompressedSize` is no longer read at all. |
| **C-12** | **PARTIAL** | `package.json`, `package-lock.json` | `test/boot.test.js` (6) | Dependencies declared, installed and proven to load; multer moved off the deprecated 1.x line. **The install/uninstall scripts themselves are NOT fixed** — descoped by the coordinator. |
| **C-14** | **PARTIAL** | `TAKPortal/test/**` | — | Node runner stood up, 93 tests, all executed. `pytest` for Infra-TAK **not** stood up — descoped. |
| **C-21** | **FIXED (server share)** | `services/featurelinkSafeFetch.service.js`, `services/featurelinkArcgisIconset.service.js` | `test/ssrf.test.js` | `Authorization: Bearer`; no `?token=`; no URL logged by this module. |
| **C-22** | **FIXED (server share)** | `services/featurelinkSafeFetch.service.js` | `test/ssrf.test.js` | One central guard checking transport status **and** `json.error`; typed error carrying `arcgisCode`. |
| **C-38** | **FIXED** | `TAKPortal/assets/.../vendor/**`, `Infra-TAK/.../vendor/**`, both `index.html`, `featurelink-configs.ejs` | `test/configuratorXss.test.js` | Both libraries vendored with recorded SHA-256s and licences. `featurelink_displayconfig.py:274` **not** done — deferred. |
| **C-39** | **FIXED** | `services/featurelinkArcgisIconset.service.js`, `services/featurelinkCustomIcons.service.js` | `test/goldenVectors.test.js` (14) | Cap applied once; shared fixture published. |

---

## Full route audit (C-03) — all 19 routes, not just the 4 cited

The brief was right that the two cited omissions were unlikely to be the only ones. Findings:

| # | Route | Was | Now |
|---|---|---|---|
| 1 | `GET /api/featurelink/configs` | session only, no identity assertion despite a comment claiming one | identity asserted; 401 if absent |
| 2 | `GET /configs/:id/download` | any user downloads any config | unchanged **by design** — this is the field-user browse surface. Recorded as an accepted risk; see OWNER DECISIONS #3 |
| 3 | `GET /admin/datasets` | full list + `created_by` to everyone | non-admins see only their own; `created_by` stripped |
| 4 | `POST /admin/datasets` | owner check only when the id existed | + client-chosen ids refused for creation (id squatting); + CSRF; + size caps; error no longer leaks fs paths |
| 5 | `GET /admin/datasets/:id` | **no owner check** (cited) | owner/admin only; 404 not 403 (no existence oracle) |
| 6 | `DELETE /admin/datasets/:id` | owner/admin, fail-open on null owner | fail-closed; + CSRF |
| 7 | `GET /admin/datasets/:id/file` | **no owner check** (cited) | owner/admin; `attachment` + `nosniff`; filename sanitized (was header-injectable and 500-able); `sendFile` errors handled |
| 8 | `GET /admin/custom-icons` | leaked `created_by`/`sourceUrl` | stripped for non-admins |
| 9 | `POST /admin/custom-icons` | **no owner check on append** (cited) | 403 on another user's set; a second upload can no longer re-uid an existing set; + CSRF, rate limit, quota, magic-byte check |
| 10 | `POST /admin/custom-icons/from-arcgis` | **no owner check, no allowlist** (cited) | 403 on another user's set; full SSRF guard; body validated; + CSRF, rate limit, concurrency cap; audited |
| 11 | `GET /admin/custom-icons/by-uid/:uid` | leaked `created_by`, `sourceUrl`, `sourceField`; unvalidated `:uid` | stripped for non-admins; `:uid` shape-validated; rate-limited |
| 12 | `GET /admin/custom-icons/usage` | leaked `created_by`; O(n) fs fan-out unthrottled | stripped; rate-limited |
| 13 | `DELETE /admin/custom-icons/:name` | **fail-open on null owner**; `?force=yes` silently ignored | fail-closed; `force` validated; + CSRF; audited |
| 14 | `GET /admin/custom-icons/:name/download` | invariant asserted in a comment only | invariant **enforced** (every stored name must carry an image extension); rate-limited |
| 15 | `GET /admin/custom-icons/:name/:file` | traversal input silently **rewritten**, not rejected | rejected outright + `path.resolve` containment assertion; `nosniff` + sandbox CSP |
| 16 | `GET /featurelink-configs/configurator` | no CSRF cookie, no security headers | issues `fl_csrf`; `X-Frame-Options`, `nosniff`, `Referrer-Policy` |
| 17 | `GET .../configurator/icons/*` (static) | `no-store` inherited, no `nosniff` | cacheable 7d, `nosniff`, `dotfiles: deny` |
| 18 | `GET /featurelink-configs` (page) | — | unchanged; needs `issueCsrfCookie` wired in `install.sh` — see Deferred |
| 19 | `GET /featurelink` (page) | — | as above |

Additional issues found and fixed while sweeping, beyond any C-ID:

- `readManifest()` returned `[]` on a JSON parse failure, so a truncated manifest made **every icon
  set silently disappear** from the UI while the files stayed on disk. Now raises.
- Manifest and dataset writes were non-atomic; a crash mid-write truncated them into exactly the
  above. Now temp-file + rename.
- `deleteCustomIconSet` wrote the manifest first and swallowed an `rmSync` failure, orphaning the
  directory. Order reversed, failure surfaced.
- Five silent `catch(_) {}` blocks now log.
- `sha256sumFile` did a synchronous whole-file read of up to 25 MB inside a request handler; now
  chunked.
- `buildStoredZip` wrote 16-bit counts and 32-bit sizes with no ZIP64 support and no overflow
  check, silently producing a corrupt archive past 65,535 entries. Now refuses.
- Multer errors reached `next(err)` and were never handled by this router (the host's error
  middleware is neither installed nor asserted by this module); temp files leaked on every
  multer-level failure. Now handled locally, with a startup + hourly janitor for stale `flicon_*`
  files.
- Temp filenames used `Math.random()`; now `crypto.randomUUID()`.

---

## Test output (actually executed)

```
$ cd TAKPortal && npm test

> featurelink-takportal-module@1.5.0 test
> node --test "test/**/*.test.js"

ℹ tests 93
ℹ suites 0
ℹ pass 93
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 8892.8449
```

Per file: `oauthRelay` 9, `boot` 6, `configuratorXss` 13, `authz` 21, `ssrf` 18, `zipbomb` 12,
`goldenVectors` 14.

```
$ npm run lint          # node --check across all routes and services
(exit 0)

$ npm audit
found 0 vulnerabilities

$ python -m py_compile Infra-TAK/featurelink_displayconfig.py
python syntax OK
```

Fail-against-baseline evidence for C-01, run explicitly:

```
$ git show a8f6f4a:docs/featurelink-oauth-relay.html > /tmp/wp4base/docs/...
$ cd /tmp/wp4base/TAKPortal && node --test 'test/**/*.test.js'
ℹ tests 9
ℹ pass 2
ℹ fail 7
✖ C-01: the exact attack string that used to work now delivers nothing to the attacker
✖ C-01: the target is never taken from `state`, even when state names an allowlisted origin
  … (5 more)
```

**Runner choice — `node:test`, not vitest.** Justification: zero additional dependencies. This is a
DDIL-targeted product being submitted with an SBOM requirement (C-30/C-13); vitest would add
several hundred transitive packages to test four route files and six services. `node:test` ships
with the Node 18 floor this module already targets, runs under `npm test` with no config file, and
produces TAP. The one thing it cost is a browser-driving XSS assertion, which vitest would not have
given either without jsdom or playwright.

**Why the harness stubs two modules.** `auditLog.service` and `apiErrorPayload.service` belong to
the host TAK Portal. They are stubbed through a `Module._load` hook in
`test/helpers/portalHarness.js` and deliberately **not** created as real files under `services/`,
because `install.sh` copies everything in `services/` into the host portal and would overwrite the
host's genuine implementations with test stubs.

---

## Design decisions worth challenging

1. **The relay allowlist is baked into the page, not fetched from a server.** The C-list said
   "server-side allowlist". The relay is a static HTML file with no server behind it — that is the
   whole point of it being a single fixed `redirect_uri`. The allowlist is therefore
   deployment-controlled page content, which is server-controlled data in the sense that matters:
   it is not request-controlled. The stronger property comes from iterating the allowlist rather
   than selecting from it — the browser refuses to deliver to a non-matching opener, so no
   request-derived value ever reaches the target argument.

2. **404, not 403, for a non-owner reading a dataset.** A 403 confirms the id exists. These ids are
   distributed as QR links, so existence is worth hiding. Delete still returns 403 because by then
   the caller has demonstrated they know the id.

3. **An unowned record is admin-only.** This is fail-closed and it is **breaking** for legacy
   records and for everything Infra-TAK writes (the Flask module has no ownership model at all).
   The alternative — keep failing open — leaves the exact records most likely to matter editable by
   anyone. Flagged prominently in the test plan with a pre-rollout counting command.

4. **A resolver seam in `safeFetchArcgisJson`.** The redirect loop, timeout, size cap, Bearer path
   and error-body guard could not be tested offline without one. It is dependency injection with a
   production default of `resolveAndVet`, **not** an environment flag that could be flipped in the
   field. I considered a `FEATURELINK_ALLOW_PRIVATE` escape hatch and rejected it for exactly that
   reason.

5. **Rate limits are in-process.** A Redis dependency to rate-limit an admin tool in a DDIL product
   is the wrong trade. If the portal is ever scaled horizontally these become per-instance.

---

## NOT-A-DEFECT

- **Appendix D §2.2 zip-slip on local writes.** `extractZipIcons` already wrote to
  `path.basename(zipEntry.path)`, so local traversal was genuinely blocked, as the appendix itself
  says. The real defect was the **outbound** group string, which I fixed. Recording it so nobody
  "re-fixes" the local write.
- **Appendix D §2.2 XXE.** Confirmed: none of the 10 shipped `iconset.xml` files contains `DOCTYPE`
  or `ENTITY`, and the regex parser cannot expand entities. No XXE ships today. I did **not**
  replace the regex with an XML parser — doing so would *introduce* the XXE risk that currently
  does not exist. The decoy-comment weakness in §2.2 is real but is now bounded by the entry cap
  and the first-valid-wins rule; recorded as deferred.

---

## Deferred — with severity and estimate

| Item | Severity | Est. | Note |
|---|---|---|---|
| **C-12 install/uninstall scripts** | **CRITICAL** | 1–2 d | Descoped by the coordinator. `uninstall.sh` still orphans the three files that are the C-04 and C-11 sinks, and its `server.js` unpatcher prints success unconditionally — a mismatch leaves a `require` for a deleted file and **the portal is permanently down after an uninstall**. No backups are taken by either script. **This is the highest-severity thing still open in my scope.** |
| **Infra-TAK Flask CSRF** (C-10 half) | CRITICAL | 0.5 d | 8 routes, all cookie-authenticated, no token, no Origin check. |
| **Infra-TAK Jinja XSS** `featurelink_displayconfig.py:248` (C-05 half) | CRITICAL | 1 h | `{{ d.name\|tojson }}` inside `onclick=""`; `tojson` does not escape `"`. Also means **the QR button is functionally broken for every dataset**. Fix: `data-name` + `addEventListener`. |
| **Infra-TAK has no ownership model** | CRITICAL | 0.5 d | Any console user can read or destroy any other's dataset. This is also why the Portal's null-owner records exist. |
| **Infra-TAK `install.sh`/`uninstall.sh`** (C-12 half) | CRITICAL | 1 d | Root-enforcing, unverified self-update, no `app.py` backup; the unpatcher can leave `NameError` on every page load. |
| `featurelink_displayconfig.py:274` CDN script (C-38 half) | HIGH | 15 m | Libraries are vendored at `Infra-TAK/featurelink_displayconfig_assets/vendor/`; the hub template still points at jsdelivr. Needs a Flask static route + one edit. |
| `pytest` suite for Infra-TAK (C-14 half) | HIGH | 0.5 d | No runner stood up. |
| Page routes 18/19 need `issueCsrfCookie` | HIGH | 30 m | The EJS admin page reads `fl_csrf`, which is currently issued only by the configurator route. In practice operators reach the configurator first so the cookie exists — but that is luck. Needs an `install.sh` edit, which is inside the descoped C-12 work. **Test §6.4 is the canary.** |
| `listDatasets()` O(n) sync reads | HIGH | 0.5 d | Rate-limited now, not fixed. 500 configs still block the event loop. Needs an index/cache + async fs. |
| `buildIconsetZip` buffers everything | HIGH | 0.5 d | Bounded by the new caps, but still a single large allocation. Should stream. |
| Regex `iconset.xml` parser decoy | MEDIUM | 2 h | See NOT-A-DEFECT. Needs a safe XML parser with DTD/entity resolution disabled. |
| `Infra-TAK` `requirements.txt` | MEDIUM | 1 h | Still absent; Flask/Werkzeug versions unpinned, which is what makes `send_from_directory` traversal safety version-dependent. |
| Headless-browser XSS assertion | MEDIUM | 0.5 d | Manual only today (test plan §7). |
| Two `index.html` copies diverge ~2,000 lines | MEDIUM | 2–3 d | I corrected the false comment claiming they are identical. Every fix in this package was applied to **both**. |
| Esri `where`-clause injection (`makeWhere`) | MEDIUM | 0.5 d | Client-side config builder; unchanged. |
| Full a11y / Section 508 pass | MEDIUM | — | Untouched. Compliance blocker per Appendix D §2.8. |

---

## OWNER DECISIONS NEEDED

Each has a **defensible default already implemented** — nothing is blocked.

1. **Unowned records are now admin-only (breaking).** Default chosen: fail closed. Alternative: a
   one-time migration stamping an owner. Counting command in test plan §11.5.
2. **The host portal's session cookie flags.** Not ours to set. Default: we secure our own cookie
   and document the gap.
3. **`GET /configs/:id/download` remains open to every logged-in user.** Default: unchanged — it is
   the advertised field-user browse surface. There is no `published`/`visibility` flag, so a
   half-finished config is exposed the moment it is saved. Adding one is a schema change affecting
   all four clients.
4. **ArcGIS host allowlist default.** Default: Esri public cloud only. Enterprise operators must set
   `FEATURELINK_ARCGIS_ALLOWED_HOSTS`. Fails visibly with a message naming the host.
5. **Upload limits reduced** (25 MB×200 → 8 MB×25, plus 200 MB/user/day). Default chosen for the
   real workload; confirm against your operators' largest iconsets.
6. **SVG dropped as an icon type.** ATAK cannot render it as a marker, and serving it as
   `image/svg+xml` from the portal origin executes scripts. Any existing `.svg` in a set still
   serves; new uploads are refused.
7. **Infra-TAK remains installable and root-privileged with known-unfixed CRITICALs.** Per
   `QUESTIONS-FOR-OWNER.md` I-4 the standing decision is patch-not-archive, but the patching is
   descoped from this session. Interim default: documented as trusted-users-only in the test plan.

---

## Honest limitations

- **No live TAK Portal was available.** The boot test mounts the routers in a real Express app with
  the two host services stubbed, which proves the module loads, mounts and answers on all 10 API
  routes without falling through to error middleware. It does **not** prove your portal's
  middleware order, reverse-proxy header handling, or Authentik integration. Test plan §11.2 is the
  highest-risk item for this reason.
- **No live ArcGIS org.** The SSRF guard is tested against local stubs and the pure predicates
  exhaustively; the one test needing real DNS self-skips if DNS is unavailable. Token-Bearer and
  ArcGIS-error-body behaviour are proven against a local stub, not against Esri.
- **No browser was driven.** XSS coverage asserts sink absence in source plus escaping-helper
  behaviour. No test proves a handler does not fire in a real DOM.
- **The install and uninstall scripts were never executed** — they need a real portal and Docker.
  Given C-12 is unfixed, this is the largest untested surface. Test plan §11.1.
- **Infra-TAK's Flask module was syntax-checked only** (`py_compile`). No pytest suite exists and no
  Flask route was executed.
- **The zip-bomb tests build ~24 MB bombs, not 5 GB ones.** The budget is enforced by the same code
  path at any size, but I did not verify the process survives a genuinely multi-gigabyte archive.
- `npm audit` reports 0 vulnerabilities **for this module's own dependency tree**, not for the host
  portal's.
