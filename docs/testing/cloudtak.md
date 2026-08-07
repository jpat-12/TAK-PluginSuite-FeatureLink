# FeatureLink — CloudTAK plugin: hands-on test guide (WP2)

Covers everything changed on branch `audit-remediation` by work package **WP2 (CloudTAK)**, commits
`f9e138c`, `4addb5b`, `2d0a881`. Written for testing against a **live CloudTAK, a live ArcGIS org
and a live TAK server**.

Read §0 and §1 before touching the deploy. §2 is the automated suite. §3 is the manual matrix, one
numbered test per C-item, each with **what the failure looked like before the fix** so a pass is
distinguishable from a coincidence. §4 lists what to hammer hardest and §5 what was deliberately
*not* fixed, so you don't waste a session testing it.

---

## 0. Read this first — three things that will bite

### 0.1 `NGINX_CSP_CONNECT_SRC` is still required. Nothing changed that.

The plugin still calls ArcGIS **directly from the browser**. Moving iconset generation server-side
is the Appendix B §N design, scheduled for WP6; it is not built. So the CloudTAK deployment still
needs its Content-Security-Policy `connect-src` to permit your ArcGIS hosts, or **every** network
call the plugin makes fails with an opaque `Failed to fetch` and nothing appears in the UI.

In your CloudTAK `.env` / `docker-compose.yml`:

```
NGINX_CSP_CONNECT_SRC="'self' https://*.arcgis.com https://www.arcgis.com"
```

Add your ArcGIS Enterprise host too if you are not on ArcGIS Online. `install.sh` still does not
check for or mention this (that fix is deferred — see §5), so verify it by hand:

```bash
grep -R NGINX_CSP_CONNECT_SRC /path/to/CloudTAK/.env /path/to/CloudTAK/docker-compose.yml
```

**If the plugin appears totally dead — no layers, no counts, no errors — check this first.**

### 0.2 NEW risk: the ArcGIS token now travels in a header, which triggers a CORS preflight

C-21 moved the access token out of the `?token=` query string and into
`X-Esri-Authorization: Bearer …` (plus `Authorization:` as a fallback for Enterprise reverse
proxies). ArcGIS supports this, but a custom request header makes the browser send a **preflight
`OPTIONS`** before every authenticated request, where previously there was none.

ArcGIS Online answers preflights correctly. An ArcGIS **Enterprise** deployment behind a
locked-down reverse proxy might not. **This is the single highest-risk change in the package** —
see §4.1. Symptom would be: unauthenticated/public layers work fine, every *private* layer fails.

### 0.3 `install.sh` copies `node_modules/`, and `node_modules/` just got bigger

`install.sh:84` still does `rm -rf "$WEB_DEST"; cp -R "$REPO_DIR/plugin" "$WEB_DEST"` — the whole
directory, `node_modules` included, straight into the API docker image (C-31, deferred). WP2 added
vitest, happy-dom and eslint-plugin-vue, so that tree is now ~230 packages.

**Delete or move `node_modules` before installing.** The plugin does not need it at CloudTAK build
time — CloudTAK supplies the real Vue/TypeScript:

```bash
cd CloudTAK/plugin
mv node_modules /tmp/fl-node-modules      # keep it; you'll want it for re-running tests
cd ..
./install.sh /path/to/CloudTAK
mv /tmp/fl-node-modules plugin/node_modules
```

---

## 1. Build and deploy

### 1.1 Verify locally first (2 minutes, catches everything cheap)

```bash
cd CloudTAK/plugin
npm ci            # NOTE: package-lock.json is still gitignored (C-30, deferred) —
                  # if `npm ci` errors "no lock file", use `npm install` for now.
npm run check     # vue-tsc --noEmit
npm run lint      # eslint .
npm test          # vitest run
```

All three must exit 0. Expected output is in §2.

### 1.2 Deploy

```bash
cd CloudTAK
./install.sh /path/to/CloudTAK          # copies plugin/ -> <CloudTAK>/api/web/plugins/featurelink/
                                        # then: docker compose build --no-cache api  (5-15 min)
                                        #       docker compose up -d --force-recreate api
```

Options: `--no-build` to copy only, `--remove` to uninstall, `--pull` to git-pull this repo first.

### 1.3 Activate in the browser — a hard refresh is NOT enough

CloudTAK's service worker intercepts requests, so **Ctrl-Shift-R will serve you the old plugin**.

> In CloudTAK: **Settings → Refresh App**

Then open the right-side menu → **FeatureLink**.

### 1.4 If CloudTAK's own typecheck complains about `@tak-ps/cloudtak`

WP2 added `CloudTAK/plugin/types/cloudtak.d.ts`, an ambient `declare module '@tak-ps/cloudtak'`
stub, so `npm run check` can pass outside a CloudTAK checkout (it was permanently red before). The
runtime build is esbuild and does not typecheck, so this cannot break the docker build — but if
CloudTAK's own `vue-tsc` picks the file up it may report a duplicate module declaration against the
real package.

Fix if that happens: `rm -rf <CloudTAK>/api/web/plugins/featurelink/types/` and rebuild. Tell me and
I will scope the stub behind a tsconfig `paths` entry instead.

---

## 2. The automated suite

```bash
cd CloudTAK/plugin && npm run check && npm run lint && npm test
```

Real output as of `2d0a881`:

```
> cloudtak-plugin-featurelink@0.1.0 check
> vue-tsc --noEmit
                                    <- no output, exit 0

> cloudtak-plugin-featurelink@0.1.0 lint
> eslint .
                                    <- no output, exit 0

> cloudtak-plugin-featurelink@0.1.0 test
> vitest run

 ✓ test/arcgisRest.test.ts (15 tests) 407ms
 ✓ test/symbology.test.ts (8 tests) 1710ms

 Test Files  2 passed (2)
      Tests  23 passed (23)
```

Two `stderr` lines are printed during the run and are **expected** — they are the guards firing:

```
[featurelink] refusing to send the ArcGIS token to an untrusted host: https://evil.example.com/... — portal is https://www.arcgis.com
[featurelink] skipped 2 features with unusable geometry from https://services1.arcgis.com/...
```

Baseline for comparison — at `a8f6f4a`, `npm run check` emitted 2 × TS2307, `npm run lint` could not
execute at all ("ESLint couldn't find an eslint.config file"), and there was no test runner, no test
script and zero test files.

---

## 3. Manual test matrix

Preconditions common to all: CloudTAK running with the plugin installed and the app refreshed
(§1.3), CSP set (§0.1), signed into ArcGIS via **Account → Sign In**.

Keep the browser devtools console open throughout. Several error paths are recorded but not yet
*rendered* (C-32 is deferred — see §5), so the console is currently the only place some failures
appear.

---

### Test 1 — C-07: symbology on the download path ⭐ **THE FIELD DEFECT**

> Reported symptom: *"It's still not downloading individual layers with their symbology."*

**What was wrong.** `generateAutoIconset()` had exactly one call site in the entire plugin, inside
`addPublicLayer()` — the *"paste a public URL"* button. `downloadLayer()`, which backs the ⬇/↻
button, the browse-list download **and** the recurrence scheduler, never called it, never called
`extractAutoSymbology`, and never fetched `drawingInfo`. So on **five of the six** routes by which a
layer reaches the device, `displayConfig` stayed `null` and every feature rendered as a hardcoded
`#3388ff` marker with no icon.

**Preconditions.** An ArcGIS Feature Layer you own whose renderer is a **uniqueValue renderer with
picture-marker (esriPMS) symbols** — i.e. a layer that visibly has custom icons in ArcGIS Online's
map viewer, driven by an attribute. A layer with esriSLS/esriSFS line/polygon styling works too for
step 6.

#### 1a — the broken route (this is the actual bug)

1. Layers tab → **My ArcGIS Layers** → **Refresh**.
2. Find your layer in that browse list.
3. Click the **⬇** button on its row.
4. Wait for the row to show a sync time and a feature count.
5. Look at the map.

**PASS:** features render with the layer's **real ArcGIS icons** — the same PNGs you see in ArcGIS
Online — and different attribute values show *different* icons.

**FAIL (pre-fix behaviour):** every feature is an identical **plain blue circular marker**, colour
`#3388ff`, no icon, regardless of attribute value.

> How to tell a real pass from a coincidence: `#3388ff` is a distinctive medium blue. If *all*
> markers are that exact blue and identical to each other, symbology did not resolve. If markers
> differ by attribute value, it did.

Cross-check in the console:

```js
JSON.parse(localStorage['cloudtak-featurelink:v1']).displayConfigs
```

The entry for your layer URL must exist and contain `sym.t === 'adv'` with a `vs` array whose
entries carry `up` paths shaped `"<64-hex>/<Layer Name> Icons/<Value>.png"`. Before the fix this
key was **absent entirely** after a browse-list download.

#### 1b — the route that always worked (control)

6. Layers tab → **Add Layer** → paste the same layer's FeatureServer URL → **Add Layer**.

**PASS:** identical rendering to 1a. This path was never broken; it is the control that proves the
two paths now agree.

#### 1c — refresh, scheduler and visibility routes

7. On a downloaded row, click **↻**. Symbology must persist.
8. Set the row's interval to 30 s, wait ~40 s. The layer re-downloads and keeps its symbology.
9. Toggle the eye icon off then on. Markers disappear, then come back **styled**.

#### 1d — FIX-2: the empty-config poisoning case

10. On a downloaded public layer, click **📤** (share). It downloads `<name>.featurelinkshare`.
11. Delete that layer (🗑).
12. **Add Layer → Import Config**, select that `.featurelinkshare` file.
13. The layer is re-added. Look at the map.

**PASS:** real symbology.

**FAIL (pre-fix):** default blue markers, *and* a green **"Config"** badge on the row — actively
lying to you. The share file carries only `v/url/layer/freq` and no styling at all, but the old
guard was a truthiness test (`if (!store.displayConfigs[layer.url])`), so that empty-but-truthy
object permanently blocked generation. Only deleting the layer cleared it.

#### 1e — polygon/line styling

14. Download a polyline or polygon layer with real ArcGIS stroke/fill styling.

**PASS:** strokes and fills use the layer's colours and widths.
**FAIL (pre-fix):** every shape is `#3388ff`, 2 px, no fill.

---

### Test 2 — C-08: individual sublayers of a multi-layer FeatureServer

**What was wrong.** `ensureLayerIndex()` hard-appended `/0` to anything ending in `FeatureServer`,
and a portal search returns the **service root**. A FeatureServer exposing layers 0, 1 and 2
therefore collapsed to a **single row pinned at layer 0**. Layers 1 and 2 were unreachable — their
features never downloaded and their renderers never seen. If your symbology lives on layer 1, the
observed result is exactly "downloaded, but no symbology" *and the wrong features*.

**Preconditions.** A FeatureServer with more than one layer. Its root URL looks like:

```
https://services7.arcgis.com/<orgid>/arcgis/rest/services/<ServiceName>/FeatureServer
```

(no trailing `/0`). Confirm it is multi-layer by opening `…/FeatureServer?f=json` in a browser tab
and checking that `"layers"` has more than one entry.

**Steps.**
1. Layers tab → **Add Layer** → paste the **root** URL (no `/0`) → **Add Layer**.

**PASS:** you get **one row per sublayer** — three layers means three rows. Each row's name is
`<Service title> — <Sublayer name>`. Each downloads its own features and resolves its own
symbology independently.

**FAIL (pre-fix):** exactly one row, always layer 0.

2. Also refresh **My ArcGIS Layers**: a multi-layer owned service must now appear as several rows
   there too, not one.
3. Paste an explicit `…/FeatureServer/1` URL. **PASS:** one row, and it is genuinely layer 1 —
   check the feature count matches layer 1's, not layer 0's.

---

### Test 3 — C-06: pagination past `maxRecordCount`

**What was wrong.** One query with `where=1=1&outFields=*`, no `resultOffset`, no
`resultRecordCount`, and `exceededTransferLimit` never inspected. ArcGIS caps every response at the
service's `maxRecordCount` (commonly 1,000–2,000), so **every layer larger than that silently
truncated** — and `layer.featureCount` was then set from the truncated array, so the UI *confirmed*
the wrong number.

**Preconditions.** A layer with **more features than its `maxRecordCount`**. Check both:

```
https://…/FeatureServer/0?f=json                                   -> "maxRecordCount": 1000
https://…/FeatureServer/0/query?where=1=1&returnCountOnly=true&f=json  -> "count": 4213
```

**Steps.**
1. Download that layer.
2. Compare the row's feature count against the true `count` above.
3. Open the Network tab and filter on `/query`.

**PASS:** the count matches the true total, and you see **multiple** `/query` requests with
increasing `resultOffset` (0, 1000, 2000, …).

**FAIL (pre-fix):** exactly one `/query` request, and the count equals `maxRecordCount` exactly —
a suspiciously round 1000 or 2000.

4. For a layer above the 50,000 hard cap, the row shows a *"Showing the first N features"* warning
   rather than silently presenting a partial picture as complete.

---

### Test 4 — C-22: ArcGIS errors served with HTTP 200

**What was wrong.** `readJson` never checked `res.ok`, and the ArcGIS `{"error":{…}}` envelope —
which comes back with **HTTP 200** — was checked at only 4 of ~14 parse sites. A 403 rendered as
"0 features"; an expired token rendered as "0 features"; a 404 produced a layer named
"Unknown Layer".

**Steps.**
1. Add a public layer, then in ArcGIS **unshare** it (make it private) without signing out.
2. Click **↻** on that row.

**PASS:** console shows a typed error naming the cause — e.g.
`ArcGIS rejected the request: Invalid token` or `ArcGIS returned HTTP 403`. The row does **not**
update its sync time and does **not** claim 0 features.

**FAIL (pre-fix):** the row cheerfully reports "0 features" and a fresh sync time.

3. Paste a deliberately wrong URL (`…/FeatureServer/99`). **PASS:** "Could not load that service:
   ArcGIS could not find that layer: …". **FAIL (pre-fix):** a layer called "Unknown Layer" gets
   added.

> Note: because C-32 is deferred, several of these still surface in the console rather than on the
> row. That is expected at this commit.

---

### Test 5 — C-34: spatial reference

**What was wrong.** `outSR=4326` was *requested* but the response's `spatialReference` was never
verified. Some MapServer and older services ignore `outSR` and return Web Mercator (metres).
Those metre values went straight into `lat`/`lon` — every feature at a nonsense location.

**Steps.**
1. Download a layer from an older MapServer if you have one.

**PASS:** either the features land in the right place, or the download is **refused** with
`service returned spatial reference 102100 instead of the requested 4326 …`.

**FAIL (pre-fix):** features silently plotted near 0,0 / off the coast of Africa, or scattered
nonsensically.

2. General sanity for every layer you download: features must land where ArcGIS Online shows them.

---

### Test 6 — C-21 / C-33: token handling

**What was wrong.** The token was a `?token=` query parameter (leaking into ArcGIS/proxy/CDN access
logs, `Referer` headers and any HAR export), and an imported config could mark **any foreign host**
`private`, which caused the plugin to send the operator's ArcGIS token to that host.

**Steps.**
1. Devtools → Network. Download a **private** layer.
2. Inspect every request URL.

**PASS:** no `token=` anywhere in any URL. The request carries request header
`X-Esri-Authorization: Bearer …`.

**FAIL (pre-fix):** `…/query?where=1%3D1&…&token=AAPK…` in plain sight in the URL bar/Network tab.

3. Craft a hostile config and import it via **Add Layer → paste**:

```json
{"v":2,"url":"https://services1.arcgis.com/…/FeatureServer/0","private":true,
 "layer":{"name":"Test"}}
```

then a second one pointing `url` at a host that is **not** your ArcGIS org (e.g.
`https://example.com/arcgis/rest/services/X/FeatureServer/0`).

**PASS:** the console logs
`refusing to send the ArcGIS token to an untrusted host: … — portal is …` and the request to that
host carries **no** Authorization header.

**FAIL (pre-fix):** your ArcGIS OAuth access token is transmitted to that host as a query parameter.

> ⚠️ The *rest* of that attack chain is still open — see §5. This step verifies only the token
> guard, not the consent gate.

---

### Test 7 — C-24: uniqueValue renderer with a `defaultSymbol`

**What was wrong.** `resolveShapeStyle` checked `singleShapeStyle` **first** and returned it, so
per-value styling was consulted only when a uniqueValue renderer had *no* `defaultSymbol`. Every
uniqueValue renderer that declares a default — the common case — painted every feature the default
colour.

**Preconditions.** A **polygon or polyline** layer with a uniqueValue renderer that has both
per-value symbols and a default symbol ("all other values" checked in ArcGIS).

**Steps.**
1. Download it.

**PASS:** polygons/lines are coloured **per attribute value**, matching ArcGIS Online. Features
whose value matches no rule get the default colour.

**FAIL (pre-fix):** every polygon is the single default colour — usually grey — with the per-value
colours nowhere on the map.

---

### Test 8 — C-26: re-entrancy, and the schedulers actually stopping

**What was wrong.** Both loops were `setInterval` around work that can take minutes, with no guard,
so executions stacked without bound; overlapping syncs overwrote each other's marker-UID lists,
leaving markers on the map with **no record of their UIDs** — unremovable by Hide, Delete or Clear
All Layers.

**Steps.**
1. Set a layer's refresh interval to the minimum (30 s) and let it run for ~5 minutes.
2. While it is running, mash the **↻** button and toggle visibility repeatedly.
3. Then click 🗑 to delete the layer.

**PASS:** every marker for that layer disappears. Network shows requests serialised, not stacked.

**FAIL (pre-fix):** orphaned markers remain on the map permanently after delete, and the Network
tab shows overlapping bursts of the same query.

4. Note: intervals below 30 s are now clamped to 30 s and the input reflects the clamped value back.
   Typing letters into the interval box no longer silently disables auto-refresh (it previously
   persisted `NaN` as `null`, and `null <= 0` turned refresh permanently off).

---

### Test 9 — C-20: token storage (behaviour change you will notice)

**What changed.** The access token is now held **in memory only** and never written to storage. The
refresh token moved from `localStorage` to `sessionStorage`.

**Consequence you must expect: closing the browser tab ends the ArcGIS session.** You will be asked
to sign in again in a new tab. That is deliberate — the refresh token grants long-lived offline
access to the whole ArcGIS account, and any XSS anywhere in the CloudTAK origin could read it from
`localStorage`. See `QUESTIONS-FOR-OWNER.md` (wp2) if you want this traded back.

**Steps.**
1. Sign in. In the console:

```js
localStorage['cloudtak-featurelink:auth']    // -> undefined
sessionStorage['cloudtak-featurelink:auth']  // -> {"username":"…","refreshToken":"…"}  (no access token)
```

**PASS:** as above.
**FAIL (pre-fix):** `localStorage['cloudtak-featurelink:auth']` contained **both** tokens in plaintext.

2. Reload the page (F5) — you stay signed in (same tab session).
3. Close the tab and open CloudTAK fresh — you are signed out.
4. Trigger several concurrent operations right as a token expires (refresh several layers at once
   after ~1 hour). **PASS:** no spurious sign-out. Before, concurrent refreshes each rotated the
   refresh token and the loser signed you out.

---

### Test 10 — PLI (touched as a side effect; verify it still works end to end)

`addPliFeature`/`updatePliFeature`/`createPliFeatureService` all changed shape, so this needs a
regression pass even though it is not a C-item WP2 targeted.

1. PLI tab → **Create** a new PLI layer. **PASS:** created, and the message names the layer. A
   double-click on Create must **not** create two services (busy is now set before the first await).
2. Check the created service in ArcGIS — the `SCHEMA_TEMPLATE` / "EXAMPLE" row is now deleted after
   publish; previously a phantom EXAMPLE contact appeared at 34.05,-117.31 in every PLI layer.
3. Enable **Auto-Send**. Watch the status line.
   **PASS:** either it reports success, or it states plainly *why* it cannot send — e.g.
   *"Your own position is not available from CloudTAK — PLI is not being transmitted."*
   **FAIL (pre-fix):** the UI said "Auto-Send: On" while the feed was completely dead, with the only
   evidence a single `console.warn`.
4. **Send to Layer** on a map item. **PASS:** success, or a message carrying the real ArcGIS error
   ("Invalid field: tak_callsign"). **FAIL (pre-fix):** the literal "Send failed" for successes,
   because `-1` meant both "failed" and "succeeded without echoing an objectId".

---

## 4. Test these hardest — changed but not verifiable here

### 4.1 The `X-Esri-Authorization` preflight (§0.2) — **highest risk**
No live ArcGIS was available to WP2. Every authenticated call now triggers a CORS preflight that
previously did not exist. If private layers fail while public ones work, this is the cause. Capture
the failing `OPTIONS` request and I will fall back to a POST form field.

### 4.2 Sublayer enumeration cost on a large account
`searchUserLayers` now issues one extra `?f=json` per owned service (5 at a time) to enumerate
sublayers. On an account with 100+ services, watch how long **Refresh** takes and whether ArcGIS
starts returning 429s.

### 4.3 `orderByFields` on the paged query
Pagination now sends `orderByFields=<objectIdField>`. Correct and necessary for page stability, but
a service with an unusual OID field or a view without a sortable OID could reject it. Symptom: a
layer that used to download now errors immediately with an ArcGIS message about ordering.

### 4.4 MultiPolygon emission
Esri multi-part polygons are now split by winding order and emitted as GeoJSON `MultiPolygon`,
rewound to RFC 7946. Test with a polygon that has genuine holes **and** one with disjoint parts
(a county with islands). Previously all rings after the first were treated as holes, so islands were
punched out of the mainland. This path is unit-tested only for its ring maths, never against
node-cot's real normalizer.

### 4.5 The default CoT type changed
Unclassified features now default to `a-u-G` (**unknown**) instead of `a-f-G` (**friendly**). This
is the correct behaviour for a tactical display, but it changes what you see: features that used to
render as blue friendly icons will render as yellow unknowns. Confirm this is what you want.

### 4.6 Stable feature UIDs
Synthesized UIDs are now derived from the ObjectID (`FL-oid-42`), not `FL-${index}-${Date.now()}`.
Existing markers placed by an older build have the old UIDs and will be orphaned once. **Clear All
Layers, then re-download** on first run after this deploy to avoid duplicate markers.

---

## 5. Deliberately NOT fixed — do not spend test time here

| Item | State | Consequence while testing |
|---|---|---|
| **C-02 / C-19** — untrusted-import consent gate, ZIP bomb caps | **Not started.** `importConfig.ts`, `importIngest.ts`, `zipReader.ts` are unchanged. | Auto-ingest still applies third-party configs with no consent prompt, and can still redirect the PLI endpoint. **Do not test the auto-import path against a shared TAK server you don't control.** The C-33 token guard (Test 6) does block the token-exfiltration end of the chain. |
| **C-32** — empty catches in `LayersTab.vue` | Not fixed. `@action='downloadLayer(l).catch(() => {})'` is still there. | Per-row errors are computed and stored (`layerErrors`) but not rendered. **Watch the console** for failures during every test above. |
| **C-30** — lockfile gitignored | Not fixed. | `npm ci` may fail; use `npm install`. |
| **C-31** — `install.sh` destroys before copying, ships `node_modules` | Not fixed. | See §0.3. A failed `cp` leaves the plugin directory absent and the script still rebuilds. |
| Appendix B §1–§5 Vue work (a11y, `SendToLayerPicker`'s frozen marker list, `AddLayerView` URL validation, …) | Not fixed. | `SendToLayerPicker`'s item list is still a `computed` over a non-reactive MapLibre call — it is a frozen snapshot from when the overlay opened. Reopen the overlay to refresh it. |

---

## 6. Fast rollback

```bash
cd CloudTAK && ./install.sh --remove /path/to/CloudTAK   # then rebuild
# or, to go back to pre-remediation code:
git checkout dev -- CloudTAK/ && cd CloudTAK && ./install.sh /path/to/CloudTAK
```

Then **Settings → Refresh App** again (§1.3).
