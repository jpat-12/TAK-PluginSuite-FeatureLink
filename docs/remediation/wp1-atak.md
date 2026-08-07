# WP1 — ATAK 5.6 / 5.7 (Java / Android) — remediation report

**Branch:** `audit-remediation`
**Baseline:** `a8f6f4a`
**Commits:** `9f8f40a` (arcgis/config layer), `12af14c` (receiver, WIP), `b3c74f5` (receiver
close-out + C-28 deadlock fix), `92cf3b9` (ATAK5.7 mirror)

> ### Honest statement of verification
> **No build was run and no test was executed.** This machine has `javac` 17 but **no `gradle` and
> no ATAK SDK**, so the plugin cannot be compiled here. I did not claim a build passed at any
> point and am not claiming one now.
>
> What I *did* do:
> - Read every cited `file:line` before changing it.
> - Cross-checked every symbol I call for existence and signature.
> - Ran `javac -proc:none` over all 22 sources in **both** trees. It parses and type-checks
>   everything: **zero syntax errors, zero non-symbol semantic errors.** All 1,515 remaining
>   diagnostics are `cannot find symbol` / `package does not exist` against the absent Android and
>   ATAK SDKs, plus the `@Override` and method-reference cascades those produce. This proves the
>   files are syntactically valid Java and internally consistent; it proves **nothing** about
>   behaviour against the real SDK.
>
> Hands-on test instructions are in **`docs/testing/atak.md`**, including a ranked "high risk,
> test this hardest" table.

---

## Scope note

Mid-package the owner narrowed the scope to: finish what was in flight, leave nothing
mid-surgery, mirror to 5.7, and write test instructions. C-23 (round-trip test), C-25, C-37,
C-40, C-15 and the C-14 test source set were explicitly descoped. They are listed as DEFERRED
below with what, if anything, partially landed.

---

## Per-C-ID status

| C-ID | Status | Files touched | Test | Evidence |
|---|---|---|---|---|
| **C-07** | **FIXED** | `FeatureLinkDropDownReceiver.java` (both trees) | `docs/testing/atak.md` §1.1–1.7 — **TEST-NOT-EXECUTED-LOCALLY** | Symbology resolution hoisted out of `addPublicLayer()` into `resolveSymbology()`/`ensureSymbology()`, called from `downloadLayer()`. Guard is a **content** test (`needsSymbologyResolution`: no `sym` AND no `singleShapeStyle` AND empty `shapeStyleByValue`), not a truthiness test on the config object. Renderer fetch now passes the **token** (the old add path hardcoded `null`, so private layers never resolved). |
| **C-08** | **FIXED** | `ArcGISRestClient.java`, `ArcGISLayer.java`, `FeatureLinkDropDownReceiver.java` | §2.1–2.4 — **TEST-NOT-EXECUTED-LOCALLY** | `listSubLayers()` enumerates the service root's `layers[]`, skipping group layers. Each sublayer becomes its own `ArcGISLayer` whose `url` is fully qualified (`…/FeatureServer/<id>`), so every existing URL-keyed structure keeps working; `layerId` recorded on the object. Multi-select picker when >1. `ensureLayerIndex` is now case-insensitive and takes an explicit id; `/0` is a documented last-resort fallback only. |
| **C-06** | **FIXED** | `ArcGISRestClient.java`, `FeatureLinkDropDownReceiver.java`, `ArcGISLayer.java` | §3.1–3.3 — **TEST-NOT-EXECUTED-LOCALLY** | `downloadLayerFeatures()` loops on `resultOffset`/`resultRecordCount`, honours `exceededTransferLimit` (top level **and** under `properties`), reads the service's own `maxRecordCount` as the page size, caps at 50,000 features / 200 pages, and returns a `DownloadResult` carrying `truncated`, `skipped` and `maxRecordCount`. Truncation raises a **modal dialog**, not a toast. Portal search paginates via `start`/`nextStart` (was a silent 100-item cap). |
| **C-22** | **FIXED** | `ArcGISRestClient.java`, `FeatureLinkDropDownReceiver.java` | §4.1–4.4 — **TEST-NOT-EXECUTED-LOCALLY** | Single guard `parseChecked()` on **every** response body in the class; typed `ArcGisException` with Esri's code and `isAuthFailure()` for 403/498/499. `describeFailure()` in the receiver renders distinct operator-actionable messages. Also fixed the `-1` sentinel being summed into the Home tab's "Total Features" total. |
| **C-09** | **FIXED** | `OAuthHelper.java`, `ArcGISAuthManager.java`, `OAuthCallbackActivity.java`, `FeatureLinkDropDownReceiver.java`, `AndroidManifest.xml` | §6.1–6.5 — **TEST-NOT-EXECUTED-LOCALLY** | CSPRNG `state` nonce (32 bytes, `SecureRandom`), **in memory only**, one-shot, constant-time compared, rejected when no flow is pending. PKCE verifier also moved in-memory and cleared on cancel/back/failure; any legacy on-disk verifier is purged at construction. `sendBroadcast` replaced with `setPackage(ATAK) + sendBroadcast(intent, signature-permission)`. Callbacks carrying neither `code` nor `error` are dropped. Modelled on CloudTAK's `oauth.ts`/`arcgisAuth.ts` as the brief directed (read-only; CloudTAK not edited). |
| **C-02** | **FIXED** | `FeatureLinkMapComponent.java`, `ImportConfigActivity.java`, `AndroidManifest.xml`, `strings.xml`, `FeatureLinkDropDownReceiver.java` | §7.1–7.5 — **TEST-NOT-EXECUTED-LOCALLY** | New `signature`-level permission `…permission.INTERNAL_BROADCAST`; `registerReceiver` now passes it. Sender uses `setPackage()` + the permission. Mandatory non-cancellable **consent dialog** showing source and every target URL before applying. `pli_endpoint` payloads get an explicit warning and are never auto-applied. Payload capped at 512 KB and shape-checked. 5-second duplicate-delivery suppression fixes the double-apply from the two parallel registrations. |
| **C-21** | **FIXED** | `ArcGISRestClient.java` | §5.1–5.2 — **TEST-NOT-EXECUTED-LOCALLY** | All 11 `&token=` query-string sites removed. `applyAuth()` sets `Authorization: Bearer` (plus `X-Esri-Authorization`). `redact()` scrubs `token`/`access_token`/`refresh_token`/`code`/`code_verifier` from every logged URL, including the two `Log.e` sites that leaked live tokens. **Flagged #1 high-risk** — see §9 of the test guide. |
| **C-20** | **FIXED** | new `SecureTokenStore.java`, `ArcGISAuthManager.java` | §8.1 — **TEST-NOT-EXECUTED-LOCALLY** | Refresh token wrapped in AES-256/GCM under a non-exportable **Android Keystore** key. Legacy plaintext values are **discarded, not read** (one forced re-sign-in). Fails closed: never falls back to plaintext. Chose the Keystore directly over `EncryptedSharedPreferences` because `androidx.security-crypto` pulls `androidx.core`, which this build explicitly excludes — recorded as an owner decision. |
| **C-39** | **FIXED** | `AutoIconset.java` | §8.2 — **TEST-NOT-EXECUTED-LOCALLY** | `toLowerCase(Locale.ROOT)` at both `canonicalize` sites; SHA-256 hex now hand-rolled against a `char[]` alphabet instead of `String.format("%02x")`, removing the non-Latin-numbering-system failure too. `DisplayConfig.toHexColor` likewise pinned to `Locale.ROOT`. **The 60-char `cleanSetName` cap is already applied once, to the base name, before the `" Icons"` suffix** (`AutoIconset:362`) — the double-truncation half of C-39 is a TAK Portal defect, not an ATAK one. |
| **C-24** | **FIXED** | `DisplayConfig.java` | §1.7 — **TEST-NOT-EXECUTED-LOCALLY** | `resolveShapeStyle()` now does the per-value lookup first and falls back to `singleShapeStyle`. Landed with C-23's `shp` work in `9f8f40a` before the descope. |
| **C-28** | **PARTIAL** | `FeatureLinkDropDownReceiver.java` | §8.6, §9 row 6 — **TEST-NOT-EXECUTED-LOCALLY** | Pools split into interactive (4), background (3) and a dedicated single-thread stats coordinator. Nested same-pool submit in `checkLayerRecurrence` removed (scan is a cheap main-thread pass dispatching to the background pool). Home-tab counts batched via `invokeAll` fan-out and coalesced by generation. PLI publish moved to the background pool. **Not done:** `waitForPublishJob` is still a blocking `Thread.sleep` loop rather than a scheduled poll — it no longer occupies an interactive thread, but the brief asked for a scheduled poll and that restructuring was not attempted. |
| **C-23** | **PARTIAL / DEFERRED** | `DisplayConfig.java` | none — **NO TEST WRITTEN** | The versioned `shp` block (`sv`, `f`, `s`, `byv`, `#AARRGGBB` colours) **was** added to `toCompactJson`/`fromJson`, along with the previously-dropped `url`/`layer`/`freq`/`private`/`rendererOverride` fields. The required `fromJson(toCompactJson(x)) == x` round-trip test was **not** written — that depends on the C-14 test source set, which was descoped. |
| **C-25** | **PARTIAL / DEFERRED** | `FeatureLinkDropDownReceiver.java` | none | A `ScheduledExecutorService` with `scheduleWithFixedDelay` (30 s tick) was added and is started from `loadSavedData()`. Descoped before verification; treat as unproven. |
| **C-37** | **DEFERRED (code half done)** | `DisplayConfig.java`, `FeatureLinkDropDownReceiver.java` | none | All 9 `Map.getOrDefault` sites and the `String.join` site were replaced with plain-Java equivalents, so the specific `NoSuchMethodError` crash paths the audit named are gone. **`build.gradle` was not touched** — `minSdkVersion` is still `21` and desugaring is still off, so the underlying policy gap stands. **Recommendation when resumed: raise `minSdk` to 26** rather than enable `coreLibraryDesugaring` — ATAK 5.x already requires Android 8, so no real device is lost, and desugared library classes interact badly with the plugin classloader and the `-repackageclasses` ProGuard step. |
| **C-15** | **DEFERRED** | none | none | `versionCode 5`, identical `applicationId`/`versionName` across both trees, unchanged. Field devices still cannot upgrade in place; the 5.6 and 5.7 APKs still mutually overwrite. Testers must `adb uninstall` first — called out in the test guide. |
| **C-40** | **DEFERRED** | none | none | Keystore and plaintext passwords unchanged in `build.gradle` and `.gitignore`. Per the brief, no key rotation and no history rewrite were ever in scope; the config-side change (env-var passwords that fail closed, drop the `.gitignore` negations) was descoped before it was made. |
| **C-14 / C-29** | **DEFERRED** | none | none | No JVM test source set was created. There are still zero automated tests in either tree. C-29 (tracking the untracked sources) was already closed by the baseline commit `a8f6f4a`. |

### Additional Appendix A items fixed opportunistically

Not C-list items, but cheap and on the same lines. All **TEST-NOT-EXECUTED-LOCALLY**.

- **§7** `httpPostMultipart` now `disconnect()`s in a `finally` (was the one helper that leaked a
  connection on every PLI-layer creation). `OAuthHelper.postForTokens` likewise, and it now reads
  `getErrorStream()` so Esri's `invalid_grant` message is actually surfaced instead of a bare
  `IOException`. Response bodies capped at 24 MB.
- **§8** `DisplayConfig.fromJson`/`fromJsonV3` now log the exception and a 200-char payload
  preview instead of `catch { return null; }` — this was the product's most undiagnosable field
  failure. `enc()` no longer forces `throws Exception` up through five signatures, and
  `OAuthHelper.enc` no longer silently returns an unencoded string.
- **§9** `resolveField` no longer returns the literal string `"null"` for a JSON null. Feature
  UIDs are derived from OBJECTID rather than `System.currentTimeMillis()`, so re-downloads update
  in place instead of churning the map. Lat/lon range-validated. `matchesRule` accepts `!=` and
  `<>` alongside `≠`, and compares numerically so `5` matches `5.0`. Class-breaks top break is
  inclusive. `buildIconsetPath` returns null instead of a guaranteed-invalid 2-segment path.
  `addIconsetGroup` requires exactly 3 segments. `ArcGISLayer` has `equals`/`hashCode` on a
  canonical URL, ending the reference-equality data-corruption path in `saveLayerOfSection`.
  `lastSync` is set only inside the success branch. `safeServiceName` no longer collapses every
  non-Latin callsign to one name. Publish no longer returns a `/0` URL that may not exist.
- **§3** `AutoSymbology` — class-breaks no longer silently styles a whole layer as bucket 0;
  hatch fills downgrade to a 25 % wash with a log line instead of an opaque block over the map;
  `esriSLSNull` suppresses the line instead of drawing it solid; empty-string uniqueValue keys are
  honoured; point→dp unit conversion; channel clamping; `Result` maps defensively copied and
  unmodifiable; unhandled symbol types logged once each.
- **§6** WebView hardening (file access off, Safe Browsing on) and actual `destroy()` on teardown;
  `hideOAuthWebView()` called from `disposeImpl()`; `FeatureLinkImporter.receiver` nulled in both
  teardown paths; `disposed` flag + `postToUi()` guard on every posted lambda; receiver
  unregistration wrapped so a partial-init teardown can no longer strand markers on the map.
- **§10** `objectIdField` cached per layer URL (was a full metadata GET on every 30 s PLI send).
  `AutoIconset.generate` no longer fetches layer metadata twice.
- **§4/transport** `network_security_config.xml` with `cleartextTrafficPermitted="false"`;
  `usesCleartextTraffic="false"`; explicit `supportsRtl="false"`; unused `ACCESS_NETWORK_STATE`
  permission removed; `http://` layer URLs rejected at the UI.
- **§3** Dead `recurrence_options` string-array deleted.
- **Appendix A §1** The 4-line unported debug `Log.d` in ATAK5.7's `refreshLayersList()` is gone —
  the two receivers are now byte-identical.

### NOT-A-DEFECT

- **C-39, double-truncation half.** The checklist's `cleanSetName` double-truncation applies to
  TAK Portal. ATAK already applies the 60-char cap once, to the base name, before appending
  `" Icons"` (`AutoIconset.sanitizeGroupBase` at `:169`, used at `:362`). No change needed.

---

## ATAK5.6 ↔ ATAK5.7 fork parity

Every change was applied to both trees. Proof:

```
$ git diff --no-index --stat ATAK5.6/app/src ATAK5.7/app/src
 .../featurelink/FeatureLinkMapComponent.java | 6 +++---
 1 file changed, 3 insertions(+), 3 deletions(-)
```

The **only** residual difference is the legitimate SDK-version divergence documented in Appendix A
§1: ATAK 5.7 renamed `ImportInPlaceResolver` to `ImportResolver`, so 5.7 keeps its own import
statement, its own comment, and its own `fromMarshal` call. Three lines, one file.

Before this work the trees also differed by an unported 4-line debug `Log.d` in 5.7's
`refreshLayersList()`; that is now removed and the two receivers are byte-identical. File counts
match (22 Java sources each), and `javac` parses both trees cleanly.

---

## CROSS-PACKAGE REQUESTS

1. **WP5 (CI/workflows).** `ATAK5.{6,7}/workflows/*.yml` are mine to depend on but not to edit.
   Two things I need from CI to make this work durable:
   - A **fork-parity gate**: fail the build when
     `git diff --no-index ATAK5.6/app/src ATAK5.7/app/src` returns anything outside an allowlist
     containing exactly `FeatureLinkMapComponent.java`. There is no shared source module and no
     sync script; the 5.7 debug-log drift proves the process has already failed silently once.
   - The `sdk.path` → **`atak.sdk.path`** key fix (`build.yml:66`, `release.yml:66` vs
     `build.gradle:44`). Until that lands, CI cannot compile ATAK at all, and none of my work is
     machine-verified anywhere.
2. **WP5 (versioning).** C-15 is deferred on my side but was listed in WP5's commit
   `51af2d3 WIP(WP5): versioning policy (C-15)`. If WP5 owns the policy, the `build.gradle`
   edits in both ATAK trees still need to be made by someone — please confirm ownership so it
   does not fall between us.
3. **No requests for WP2/WP3/WP4.** I read `CloudTAK/plugin/lib/` for the C-09 reference pattern
   and edited nothing outside `ATAK5.6/**`, `ATAK5.7/**` and my own docs.

---

## OWNER DECISIONS NEEDED

Also appended to `QUESTIONS-FOR-OWNER.md`. In every case I picked a defensible default and
implemented it rather than blocking.

1. **Token-at-rest mechanism.** Used the Android Keystore directly (AES-256/GCM) instead of
   `EncryptedSharedPreferences`, because `androidx.security:security-crypto` transitively pulls
   `androidx.core`, which this build deliberately excludes to keep the plugin classloader clean.
   *Default chosen: hand-rolled Keystore wrapper (`SecureTokenStore`), zero new dependencies.*
   Confirm you would rather not relax the AndroidX exclusions.
2. **Forced re-sign-in on upgrade.** `SecureTokenStore` discards legacy plaintext tokens rather
   than migrating them, so every operator signs in once more after this update.
   *Default chosen: discard.* Migrating would mean reading the plaintext secret one last time,
   which I judged the wrong trade for a token that grants full portal access.
3. **8-digit `#AARRGGBB` colours.** The new `shp` block emits alpha; the existing `sym` schema
   still emits 6-digit `#RRGGBB` so the other four platform implementations are unaffected.
   *Default chosen: alpha in the new wire surface only.* If TAK Portal / CloudTAK / WinTAK should
   also carry alpha, that is a coordinated four-way schema change.
4. **Feature ceiling of 50,000 per layer.** Above that the download stops and warns.
   *Default chosen: 50,000, with a visible modal.* Real hardware may not tolerate anywhere near
   that many map items; you may want it far lower.
5. **Certificate pinning for `arcgis.com`.** Deliberately **not** applied — Esri rotates its chain
   without notice and a stale pin bricks sign-in in the field with no remote remedy.
   *Default chosen: system trust anchors, cleartext denied.*
6. **`ImportResolver` in-place semantics on ATAK 5.7.** Still unverified either way.
   `ImportInPlaceResolver` explicitly does not copy the file; plain `ImportResolver` may. If it
   copies, 5.7 leaves an orphan `.featurelinkshare` in `atak/tools/datapackage/` after every
   accepted share. Needs a device check (test guide §9 row 7).
7. **Iconset zip filename collision.** Two layers whose display names sanitise to the same string
   still overwrite each other's `{group}.zip`. Fixing it means adding a uid discriminator to the
   filename **and** changing `sendLayerShare()`'s lookup in the same commit; I did not want to
   half-apply that under the narrowed scope. *Default chosen: leave as-is, documented.*

---

## Honest limitations

- **Nothing was compiled, built, packaged, installed or run.** No `gradle`, no ATAK SDK, no
  device. Every claim above rests on reading plus a `javac` parse/typecheck pass.
- **Zero automated tests exist.** C-14 was descoped, so no regression test fails against `a8f6f4a`
  and passes after these changes. The definition of done in the brief is therefore **not** met for
  any C-item; `docs/testing/atak.md` is the manual substitute.
- **The `Authorization: Bearer` migration is the single largest untested risk.** It changes the
  shape of every authenticated ArcGIS call at once, and I could not confirm against a live portal
  that Esri accepts the header on `applyEdits`, multipart `addItem`, `publish` and
  `updateDefinition` — or that ArcGIS Enterprise behaves like ArcGIS Online here.
- **`SecureTokenStore` has never executed.** Keystore key generation, the IV framing, and the
  key-invalidated recovery path are all unexercised.
- **The C-02 signature-permission registration involves a genuine cross-app subtlety**: the
  permission is declared by the plugin APK while `registerReceiver` runs inside ATAK's process. I
  reasoned it holds because the permission check is on the sender, but this is the kind of thing
  that reads correctly and behaves otherwise.
- **I introduced and then fixed a self-deadlock in my own C-28 change** (`invokeAll` on the pool
  the calling task ran in). That is a fair signal about the concurrency work generally; the
  current shape is a dedicated `statsExecutor` coordinator fanning out to `bgExecutor`.
- **UI/a11y (Appendix A §11) was almost entirely untouched.** 43 hardcoded strings, 127 hardcoded
  `dp` literals, 37 hardcoded colour literals, no `values-night/`, no RTL, no runtime `CAMERA`
  permission handling. The `CAMERA` gap is worth flagging: it is tagged SEV-CRITICAL and means
  every QR capability fails silently when the permission is not already granted. Estimate: ~2 days
  for string extraction + lint gating, ~0.5 day for the camera permission check.
- **`QrScanDialog`'s `rowStride` decode bug (Appendix A §12.8, SEV-HIGH) was not touched.** It is
  the most likely cause of "the scanner just doesn't work on my phone" and needs a device with a
  padded stride to verify. Estimate: 0.5 day plus device time.
- **Appendix A §12 capability inventory is not discharged.** It defines roughly 200 test
  obligations; `docs/testing/atak.md` covers the subset that maps to what I changed.
