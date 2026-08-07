# WP2 — CloudTAK (TypeScript / Vue 3) — remediation report

**Branch:** `audit-remediation` · **Baseline:** `a8f6f4a`
**Commits:** `f9e138c` (scaffold) · `4addb5b` (the C-item fixes) · `40dee26` (Vue call sites, auth) ·
`2d0a881` (test suite green) · `8b14c65` (test guide)

**Scope was narrowed mid-package by the owner** after two session-budget interruptions: finish what
was in flight, get the toolchain actually running, write test instructions, stop. Items explicitly
descoped are listed under *Deferred* with severity and estimate.

## Toolchain — actually executed, not asserted

This is the one package with a real toolchain, and all three gates now pass on a clean tree:

```
$ npm run check    # vue-tsc --noEmit
                   -> exit 0, no output
$ npm run lint     # eslint .
                   -> exit 0, no output
$ npm test         # vitest run
 ✓ test/arcgisRest.test.ts (15 tests) 407ms
 ✓ test/symbology.test.ts   (8 tests) 1710ms
 Test Files  2 passed (2)
      Tests  23 passed (23)
                   -> exit 0
```

At `a8f6f4a`: `check` emitted 2 × TS2307 permanently (and `README.md:141` documented that as
expected, so a real type error was indistinguishable from noise); `lint` could not execute at all
("ESLint couldn't find an eslint.config file") despite five ESLint packages being installed; there
was no test runner, no `test` script and zero test files.

Making `check` pass also required enabling the tsconfig strictness Appendix B §0.2 asked for —
`noUncheckedIndexedAccess`, `noImplicitReturns`, `noFallthroughCasesInSwitch`, `noUnusedLocals`,
`noUnusedParameters`, `verbatimModuleSyntax`, pinned `lib`. That surfaced **19 real index-access
faults**, every one of them on a path that parses untrusted ArcGIS JSON (`arcgisRest.ts:126-128`
`paths[0][0][0]`, `cot.ts:190-191` `ring[0]`/`ring[len-1]`, `cot.ts:262` `ALPHA_TABLE[i]`,
`displayConfig.ts:53` and `autoSymbology.ts:72` colour destructuring, `autoIconset.ts:61-62`). All
were fixed with explicit guards, none suppressed.

## Per C-ID

| C-ID | Status | Files | Test | Evidence |
|---|---|---|---|---|
| **C-07** (+RC-3, FIX-1/2/6/7) | **FIXED** | `lib/symbology.ts` (new), `layerActions.ts`, `arcgisRest.ts`, `autoIconset.ts`, `cot.ts`, `types.ts` | `test/symbology.test.ts` — 5 cases | `ensureLayerSymbology()` is called from `downloadLayer()`, so all six entry routes resolve symbology; `addPublicLayer`'s duplicate block deleted. Guard is content-based (`hasMeaningfulStyling`), not truthiness. `fetchLayerMeta()` caches one `?f=json` and retains `drawingInfo.renderer`. Test asserts a browse-download and a scheduler tick both yield `{uid}/{group}/{file}` paths. |
| **C-08** | **FIXED** | `lib/arcgisUrl.ts` (new), `arcgisRest.ts`, `types.ts`, `layerActions.ts` | `arcgisRest.test.ts` ×3, `symbology.test.ts` ×1 | `canonicalizeLayerUrl` replaces `ensureLayerIndex`'s hard `/0`; `fetchServiceLayers()` enumerates `layers[]`; `ArcGISLayer.layerId` added; `searchUserLayers`/`addPublicLayer` emit one row per sublayer. Group layers skipped. |
| **C-06** | **FIXED** | `arcgisRest.ts`, `types.ts` | `arcgisRest.test.ts` ×2 | `resultOffset`/`resultRecordCount` loop on `exceededTransferLimit`, page size from `maxRecordCount`, `orderByFields` for page stability, 50k hard cap, `truncated` returned and surfaced. Test drives a 3-page fixture and asserts 3 requests + 5 features. |
| **C-22** | **FIXED** | `lib/arcgisHttp.ts` (new), all of `arcgisRest.ts`, `oauth.ts` | `arcgisRest.test.ts` ×3 | One `arcgisJson()` guard checks `res.ok` **and** `json.error`; typed `ArcGISError` with `kind` ∈ network/http/auth/notfound/service/malformed and a `userMessage`. No `res.json()` on an ArcGIS response survives outside it. |
| **C-21** | **FIXED** | `arcgisHttp.ts`, `arcgisUrl.ts`, `arcgisRest.ts`, `autoIconset.ts` | `arcgisRest.test.ts` ×1 | Token in `X-Esri-Authorization`/`Authorization` headers; `redactUrl()` applied to logged URLs. Test asserts the token string appears in no request URL. |
| **C-33** | **FIXED** | `arcgisUrl.ts`, `arcgisHttp.ts`, `layerActions.ts` | `arcgisRest.test.ts` ×1 | `isTokenTrustedHost()` gates every attachment to the signed-in portal's deployment (AGOL sibling hosts, or an exact Enterprise host). Enforced twice — at `tokenForLayer` and again at the request. |
| **C-34** | **FIXED** | `arcgisRest.ts` | `arcgisRest.test.ts` ×2 | `assertWgs84()` rejects any `wkid`/`latestWkid` ≠ 4326; lat/lon range-checked; `Number.isFinite` replaces the `Number.isNaN` check that let `undefined` through (`Number.isNaN(undefined) === false`). |
| **C-24** | **FIXED** | `displayConfig.ts` | `symbology.test.ts` ×2 | `resolveShapeStyle` consults `shapeStyleByValue` before `singleShapeStyle`. Test uses a uniqueValue polygon renderer **with** a `defaultSymbol` — the exact dead-code case — and asserts per-value colours win while an unmatched value still falls back. |
| **C-26** | **FIXED** | `lib/asyncLock.ts` (new), `scheduler.ts`, `layerActions.ts`, `index.ts` | not directly unit-tested | Per-layer mutex around download+sync; both loops are fixed-delay `setTimeout` chains with in-flight guards, per-op timeouts and exponential backoff; `pliState` surfaces PLI health; `unwatchPliAutoSend()` gives `disable()` a stop handle. **Note:** the sibling claim that `stopPliScheduler` is never called was STRUCK as false and was not "fixed". |
| **C-20** | **FIXED** | `arcgisAuth.ts` | manual (test guide §3.9) | Access token in a module variable, never persisted. Refresh token moved `localStorage` → `sessionStorage`. Plus single-flight refresh (`singleFlight`) and a 60 s expiry skew — the two causes of spurious sign-outs. Behaviour change: sessions no longer survive closing the tab. |
| **C-14** | **PARTIAL** | `vitest.config.ts`, `eslint.config.js`, `types/cloudtak.d.ts`, `test/**` | 23 tests | Runner stood up and green. Covers the C-items above. Does **not** yet cover the suites Appendix B §13 lists for `zipReader`/`importConfig`/`arcgisAuth`/`store`/components — those modules were descoped. |
| **§0.2 tsconfig** | **PARTIAL** | `tsconfig.json` | the typecheck itself | All requested flags enabled except `skipLibCheck: false` and `exactOptionalPropertyTypes` — see *Deferred*. |
| **C-02 / C-19** | **NOT STARTED** | — | — | `importConfig.ts`, `importIngest.ts`, `zipReader.ts` are byte-identical to `a8f6f4a`. Work began and was lost to a session interruption before any of it was committed. |
| **C-30** | **NOT STARTED** | — | — | Descoped by the owner. |
| **C-31** | **NOT STARTED** | — | — | Descoped by the owner. |
| **C-32** | **NOT STARTED** | — | — | Descoped by the owner. Groundwork exists: `layerErrors`/`layerBusy` reactive maps and `downloadLayerReporting()` are implemented in `layerActions.ts`; only the template wiring is missing. |

### Opportunistic fixes landed alongside the above

Made because the file was already open and the change was small, all covered by the typecheck:

* **§10.1** `hexToArgb` expanded CSS shorthand wrongly (`#abc` → `abcabc`, not `aabbcc`); marker and
  shape paths disagreed about 8-digit `#rrggbbaa` (one accepted it, the other silently replaced it
  with default blue). Now one `parseHexColor` for both.
* **FIX-6** `cot.ts:117` forced `#FFFFFF` whenever an icon path resolved, so a 404'd icon rendered
  **white** — worse than the blue default and indistinguishable from "no symbology". The resolved
  colour is now retained.
* **§10.1** Multi-part Esri polygons were emitted as a single GeoJSON `Polygon`, treating every ring
  after the first as a hole (a county with islands rendered punched out). Now classified by signed
  area, emitted as `MultiPolygon`, rewound to RFC 7946.
* **§10.1** O(n²) `nextUids.includes(uid)` in the removal loop → `Set`.
* **§9.5** Synthesized UIDs were `FL-${i}-${Date.now()}` — changing every download, so every marker
  was torn down and re-added on every 180 s tick. Now derived from the ObjectID.
* **§9.5** Unclassified features defaulted to `a-f-G` (**friendly**). Now `a-u-G` (unknown).
* **§9.1** Username interpolated raw into the portal search DSL → quoted and escaped. Search now
  pages on `nextStart` instead of taking an arbitrary first 100.
* **§9.6** PLI service creation: orphaned CSV item deleted on publish failure, publish timeout is
  now a hard failure instead of "Created" over a broken service, the `SCHEMA_TEMPLATE`/"EXAMPLE"
  phantom row is deleted, and the published layer id is read rather than assumed to be 0.
* **§9.3** `fetchObjectIdField` was called on every PLI update (every 30 s) with **no token**, so on
  a secured layer it always failed and fell back to the literal `'OBJECTID'`; on a service with a
  different OID field every update failed and the scheduler added a new row every 30 s forever. Now
  tokenized and cached, and a success-without-objectId is recovered by querying back on `uid`.
* **§7.2/§7.3** `store.ts`: validating loader with per-layer migration (a persisted
  `{"privateLayers":"x"}` used to brick the plugin unrecoverably; legacy layers missing `visible`
  loaded as hidden), null-prototype maps against `__proto__` keys from imported configs, debounced
  save, `±Infinity` sentinels that survive a JSON round trip (class-break symbology used to stop
  working after any reload), quota failures surfaced instead of swallowed, `resetStore()`.
* **§10.2** `parseDisplayConfig` validates instead of `as unknown as DisplayConfig`, rejects
  prototype-pollution keys, and checks a discriminating `type` **first** — the PLI-endpoint payload
  `{v:1,type:'pli_endpoint',url}` used to match the DisplayConfig branch and get added as a *layer*,
  breaking the entire "Share PLI Endpoint" round trip. Attribute values are sanitized and capped
  before reaching `callsign`/`remarks`. Last class break is now inclusive of its maximum.
* **§10.7** `oauth.ts`: `postForTokens` checked no status and no content type, so a 400 with an HTML
  body surfaced to the operator as `SyntaxError: Unexpected token <` on the single most important
  failure path; a missing `expires_in` fabricated a 1-hour lifetime. Portal URLs are validated
  against an allowlist shape — an attacker-writable `store.portalUrl` could otherwise direct the
  code exchange, carrying the authorization code and PKCE verifier, to any host.
* **§5.3/§4.2** `SendToLayerPicker` reports a real ArcGIS error instead of "Send failed" on success;
  `PliTab` sets `busy` at function entry (a double-click used to create two billable hosted Feature
  Services) and shows the actual creation failure instead of the causeless "Failed to create
  service".
* **§3.2/§5.2** Recurrence intervals clamped to [30 s, 24 h] with `NaN` rejected and the accepted
  value reflected back (`NaN` persisted as `null`, and `null <= 0` turned auto-refresh permanently
  off while the UI kept showing the bogus number).
* **§8.2** Deletions are recorded in `excludedPrivateUrls`, so the 60 s ingest poll can no longer
  resurrect a layer the operator deliberately removed. `unexcludeLayer()` provides the way back
  that never existed.
* **§10.5** Browse→on-device move happens only **after** a successful download (a failed download
  used to permanently relocate the layer); `lastSync`/`featureCount` set only after a successful
  sync; `toggleLayerVisibility` reverts the flag on failure so the eye icon cannot lie.

### NOT-A-DEFECT

* **"`stopPliScheduler` is never called"** — already STRUCK in the checklist; confirmed it *is*
  called at `scheduler.ts:106`. Not touched.
* **"The committed tree does not compile"** — STRUCK; confirmed. `autoSymbology.ts` is tracked at
  `a8f6f4a` and the tree compiles.
* **Appendix B §5.2 `LayerRow.vue:33`** (`defineEmits` kebab mapping) — filed as "verified, no
  action". Re-confirmed correct.

## CROSS-PACKAGE REQUESTS

1. **CI (WP5 / `.github/workflows`)** — please add a CloudTAK job running
   `npm ci && npm run check && npm run lint && npm test` in `CloudTAK/plugin`, and make it required.
   All three now pass, so this can be a hard gate immediately. Blocked on C-30 for `npm ci`
   specifically (see below).
2. **C-30 lockfile (whoever picks it up)** — delete `CloudTAK/.gitignore:4`
   (`plugin/package-lock.json`) and commit the lockfile. WP2 regenerated it locally when adding
   vitest/eslint-plugin-vue/happy-dom, so the file on disk is current and correct; it is simply
   still ignored. One-line change, unblocks `npm ci` in CI.
3. **WP6 (server-side iconset hot-load)** — `lib/symbology.ts` is the seam. Replace the body of the
   extraction call inside `ensureLayerSymbology()` with the `by-uid` probe → `from-arcgis` flow.
   **Do not** re-plumb the call site: the one-resolution-point-on-the-download-path shape, the
   content-based guard, `stylingStatus`/`stylingMessage`, and the `rendererHash` already computed
   from the retained `drawingInfo` are exactly what Steps 4–6 need. `autoIconset.generateAutoIconset`
   now accepts `rendererOverride`/`uidOverride`/`groupOverride` (FIX-4), so a TAK Portal Web Map
   export's embedded renderer is honoured rather than re-derived into a non-matching UID. The
   client-side extraction in `autoIconset.ts` is the part your Step 4 supersedes.
4. **TAK Portal (WP4)** — the `cleanSetName` double-truncation at
   `featurelinkArcgisIconset.service.js:255` vs `:303-307` is still the cross-platform blocker.
   CloudTAK applies the 60-char cap **once**, to the base, before the `" Icons"` suffix, and the new
   test asserts the exact strings (`<64-hex>/Roads Icons/Fire_Station.png`). Portal must match.

## OWNER DECISIONS NEEDED

Also appended to `QUESTIONS-FOR-OWNER.md`. Each has a defensible default already implemented —
nothing blocked.

1. **ArcGIS session no longer survives closing the tab** (C-20). Default chosen: access token in
   memory only, refresh token in `sessionStorage`. Alternative if that is too disruptive in the
   field: a CloudTAK-server-side token broker (removes the refresh token from the browser entirely,
   more work, strictly better) or reverting the refresh token to `localStorage` (restores the
   convenience and the XSS exposure).
2. **Unclassified features now default to `a-u-G` (unknown), not `a-f-G` (friendly).** Correct for a
   tactical display, but it visibly changes existing layers. Confirm.
3. **`https:` only.** `canonicalizeLayerUrl` rejects `http://` service URLs with an explanatory
   message rather than letting the browser fail opaquely on mixed content. Confirm no fielded
   deployment relies on plain-HTTP ArcGIS Enterprise on a trusted LAN.
4. **Minimum auto-refresh is 30 s.** Chosen to stop an imported config installing a 1 s refresh loop
   against the org's service. Confirm 30 s is not too coarse for any operational layer.
5. **Enterprise portal support.** `AccountView` still has no portal field, so auth always targets
   `arcgis.com` while layer search targets `store.portalUrl` — the two halves can point at different
   portals and Enterprise is effectively unsupported despite the README implying otherwise. Not
   fixed; needs a product decision (add the field, or document Enterprise as unsupported).

## Deferred, with severity and estimate

| Item | Sev | Est. | Note |
|---|---|---|---|
| **C-02 / C-33 consent gate + C-19 caps** (`importConfig`, `importIngest`, `zipReader`) | **CRITICAL** | 1–1.5 days | The highest-impact CloudTAK finding. Auto-ingest still applies third-party config with no consent and can still redirect the PLI feed. The token-exfiltration *end* of the chain is closed by C-33, but the consent gate, the `pli_endpoint` auto-apply block, the decompressed-byte budget and the entry/size caps are not written. Design was settled before the interruption: a reactive review queue both the manual and ingest paths post into, `private` ignored from any ingested config, `pli_endpoint` never applied from ingest at all. |
| **C-32** UI error surfacing | HIGH | 3–4 h | `layerErrors`/`layerBusy`/`downloadLayerReporting` exist; only `LayersTab.vue`/`LayerRow.vue` wiring remains. The eslint config already makes `no-empty` (allowEmptyCatch: false) an error, so the empty catches cannot come back once removed. |
| **C-30** lockfile | HIGH | 15 min | One `.gitignore` line. See CROSS-PACKAGE #2. |
| **C-31** `install.sh` | HIGH | 2–3 h | Temp dir + validate + atomic rename, exclude `node_modules`, verify docker up front, fix the `set -u` empty-array hazard on bash 3.2, and correct the factually wrong post-install message claiming username/password sign-in. |
| Appendix B §1–§5 Vue work | HIGH→LOW | 2–3 days | `SendToLayerPicker`'s frozen `computed` over `queryRenderedFeatures` (a core capability that does not work), `AddLayerView` URL validation and success-message visibility, PLI join-endpoint validation, the a11y sweep, i18n. |
| `skipLibCheck: false` | HIGH | unknown | Cannot be evaluated outside a CloudTAK checkout — with the local type stub it is meaningless, and against the real package it may surface unrelated upstream errors. Recommend a separate `check:strict` CI job inside CloudTAK. |
| `exactOptionalPropertyTypes` | MEDIUM | 2–4 h | Not enabled; `fromMode3` assigns `undefined` to ~20 optional fields deliberately and would need auditing first. |
| `autoSymbology.ts` ≈30-case matrix (§10.4) | HIGH | 4–6 h | The module is the sole source of all polyline/polygon styling and has only the two C-24 cases covering it. |
| Cross-platform iconset conformance corpus | HIGH | 1 day | Needs Java + Node + TS runners agreeing on ≥20 fixtures including 55/60/70-char names. WP2 asserts the TS side exactly; the shared corpus is a suite-wide deliverable. |

## Honest limitations

* **No live ArcGIS, no live CloudTAK, no live TAK server.** Everything is verified against fixtures
  that model real ArcGIS response shapes. Five specific changes cannot be verified without live
  services and are called out as "test this hardest" in `docs/testing/cloudtak.md` §4: the
  `X-Esri-Authorization` **CORS preflight** (highest risk — it did not exist before and an Enterprise
  reverse proxy may reject it), sublayer-enumeration cost on a large account, `orderByFields` on
  services with unusual OID fields, MultiPolygon emission through node-cot's real normalizer, and
  the `a-u-G` default.
* **`lib/cot.ts` is only exercised through a stub.** Its two dynamic imports resolve only inside a
  CloudTAK checkout, so tests alias them to `test/stubs/cloudtakHost.ts`. The ring maths and colour
  handling are covered; the actual `normalize_geojson` contract is not.
* **`initCot()` failure is still silent.** §6's finding stands: if CloudTAK moves either internal
  module the plugin appears fully functional — layers "download", counts update, rows say "synced" —
  and not a single marker appears. Not fixed (it is Vue-layer work).
* **Marker UIDs changed.** Existing markers from an older build will be orphaned exactly once. The
  test guide instructs a Clear All Layers + re-download on first run after deploy.
* **The type stub is a deployment risk I could not test.** `types/cloudtak.d.ts` gets copied into
  CloudTAK by `install.sh`, where the real `@tak-ps/cloudtak` exists. esbuild does not typecheck so
  the build cannot break, but CloudTAK's own `vue-tsc` might report a duplicate declaration.
  Mitigation documented in the test guide §1.4.
