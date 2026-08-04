# Questions for the Owner — FeatureLink Audit Remediation

The owner was unavailable during this remediation pass. **Nothing here blocked work.** Every item
below was implemented using the defensible default stated in the "Default taken" column. Each is
reversible; review at your convenience and tell us where you want a different call.

Branch: `audit-remediation`. Baseline: `a8f6f4a`.

---

## Decisions taken up front by the integrator

| # | Question | Default taken | How to reverse |
|---|---|---|---|
| I-1 | Should remediation land on `dev` directly? | No — a new branch `audit-remediation` was cut from `dev` @ `d5a7ad9` and nothing was pushed. | `git merge audit-remediation` from `dev` when you're satisfied. |
| I-2 | C-40 keystore: rotate the signing key now? | No key was rotated and **no git history was rewritten** (Appendix F §2.1 adjudicates this MEDIUM and explicitly says no rewrite is required). The build config was changed to read the release password from an environment variable that **fails closed**, so a real key can be substituted without editing tracked source. | Generate a 4096-bit/SHA-256 project key and supply it via CI secrets. |
| I-3 | `README.md.bak` / `README2.md.bak` (backup-file rot, Appendix E §3.6) | Deleted. Both are recoverable from git history. | `git checkout d5a7ad9 -- README.md.bak` |
| I-4 | Infra-TAK is labelled "Deprecated & not supported" yet ships live XSS and a root-privileged installer (Appendix F §5). Archive it out of the repo, or patch it to the same standard? | **Patched, not archived** — deleting a component is a product decision, not an audit fix. The security defects (C-05, C-12, C-38) were fixed in place. The 7,450 duplicate icons were left alone pending your call on I-5. | Deleting `Infra-TAK/` remains a one-commit operation. |

---

## Raised by the work packages

<!-- Work-package agents append here, one bullet each, prefixed with their package ID. -->

- **WP1 (ATAK) — token-at-rest mechanism.** C-20 asked for `EncryptedSharedPreferences`, but
  `androidx.security:security-crypto` transitively pulls `androidx.core`, which `app/build.gradle`
  deliberately excludes to keep the plugin's classloader clean inside ATAK. **Default taken:** a
  hand-rolled `SecureTokenStore` encrypting the refresh token with AES-256/GCM under a
  non-exportable Android Keystore key — same protection, zero new dependencies. Reverse by
  relaxing the AndroidX exclusions and swapping in `EncryptedSharedPreferences`.
- **WP1 (ATAK) — forced re-sign-in on upgrade.** `SecureTokenStore` **discards** any plaintext
  refresh token left by an older build rather than migrating it, so every operator signs in once
  more after this update. **Default taken:** discard. Migrating would require reading the
  plaintext secret one last time, which is the wrong trade for a token granting full portal
  access. Reverse by adding a one-shot read-then-re-encrypt migration.
- **WP1 (ATAK) — 8-digit `#AARRGGBB` colours.** The new C-23 `shp` block carries alpha (ArcGIS
  translucent fills are common and the 6-digit form masks alpha off). The existing `sym` schema
  still emits 6-digit `#RRGGBB` so TAK Portal / CloudTAK / WinTAK are unaffected. **Default
  taken:** alpha in the new wire surface only. Carrying alpha everywhere is a coordinated
  four-way schema change and needs your call.
- **WP1 (ATAK) — 50,000-feature ceiling per layer.** C-06 pagination now stops there and raises a
  visible "Layer download incomplete" dialog. **Default taken:** 50,000. Real tactical hardware
  may not tolerate anywhere near that many map items; tell us if it should be far lower.
- **WP1 (ATAK) — no certificate pinning for `arcgis.com`.** Appendix A §4 raises it as a MEDIUM.
  **Default taken:** not pinned — Esri rotates its chain without notice and a stale pin bricks
  sign-in in the field with no remote remedy. A `network_security_config.xml` denying cleartext
  and trusting system anchors was added instead.
- **WP1 (ATAK) — `ImportResolver` in-place semantics on ATAK 5.7 remain unverified.**
  `ImportInPlaceResolver` (5.6) explicitly does not copy the file; plain `ImportResolver` (5.7)
  may. If it copies, 5.7 leaves an orphan `.featurelinkshare` in `atak/tools/datapackage/` after
  every accepted share. Needs a device check — no ATAK SDK on the remediation machine.
- **WP1 (ATAK) — iconset zip filename collisions left in place.** Two layers whose display names
  sanitise to the same string still overwrite each other's `{group}.zip`. Fixing it requires
  adding a uid discriminator to the filename **and** changing `sendLayerShare()`'s lookup in the
  same commit; under the narrowed scope that was judged too risky to half-apply. **Default
  taken:** leave as-is, documented in `docs/remediation/wp1-atak.md`.
- **WP1 (ATAK) — C-37 resolution recommendation.** The API-24/26 calls were replaced with
  plain-Java equivalents, but `build.gradle` was descoped so `minSdkVersion 21` still stands.
  When resumed we recommend **raising `minSdk` to 26** rather than enabling
  `coreLibraryDesugaring`: ATAK 5.x already requires Android 8 so no real device is lost, and
  desugared library classes interact badly with the plugin classloader and the
  `-repackageclasses` ProGuard step.
- **WP4 (server) — unowned dataset records are now ADMIN-ONLY, and this is breaking.**
  `isOwnedBy()` returned `true` when `created_by` was unset, so every record written before
  ownership tracking existed — and **everything the Infra-TAK Flask module writes, which has no
  ownership model at all** — was readable, editable and deletable by any logged-in user. That is
  fail-open authorization on exactly the legacy data most likely to matter. **Default taken:**
  fail closed; an unowned record is manageable by admins only. Operators may report "my old
  configs vanished"; an admin re-saving each one stamps an owner. If you would rather have a
  one-time migration that assigns ownership up front, say so — the counting command is in
  `docs/testing/server.md` §11.5.
- **WP4 (server) — the OAuth relay ships with an EMPTY origin allowlist and will refuse all
  sign-ins until you edit it.** C-01's fix replaces `state.split('::')[0]` with a
  deployment-controlled `REGISTERED_ORIGINS` list in `docs/featurelink-oauth-relay.html`.
  **Default taken:** ship empty and fail closed, rather than guessing your hostnames and shipping
  a list that might be wrong. You must add every CloudTAK/TAK Portal origin before OAuth works —
  `docs/testing/server.md` §0.3.
- **WP4 (server) — outbound ArcGIS traffic is now allowlisted to Esri's public cloud only.**
  C-04's SSRF guard defaults to `*.arcgis.com, *.arcgisonline.com, *.esri.com`. **Default taken:**
  an ArcGIS **Enterprise** deployment must add its own portal host via
  `FEATURELINK_ARCGIS_ALLOWED_HOSTS`, because the default must not be able to reach anything on
  your own network. If icon generation stops working after this upgrade, that is why; the error
  names the refused host.
- **WP4 (server) — the host portal's session cookie flags are outside this module's control.**
  `SameSite=Lax` is set on our own `fl_csrf` cookie, but the Authentik session cookie belongs to
  TAK Portal upstream. **Default taken:** secure our own cookie, add an independent Origin/Referer
  assertion so we do not depend on the host's flags, and document the gap. Please confirm with the
  TAK Portal maintainer that the session cookie is `SameSite=Lax; Secure; HttpOnly`.
- **WP4 (server) — `GET /api/featurelink/configs/:id/download` is still open to every logged-in
  user.** There is no `published`/`visibility` flag, so a half-finished config is downloadable the
  moment it is saved. **Default taken:** unchanged, because this is the advertised field-user
  browse surface and adding a visibility flag is a schema change affecting all four clients.
- **WP4 (server) — upload limits reduced and SVG dropped.** Per-file 25 MB → 8 MB, per-request 200
  files → 25, plus a 200 MB/user/day quota (the old ceiling was 5 GB of disk per request with no
  quota and no rate limit). SVG was removed from the accepted icon types because it is served as
  `image/svg+xml` from the portal origin and executes scripts, and ATAK cannot render it as a
  marker anyway. **Default taken:** both. Confirm against your operators' largest real iconsets.
- **WP4 (server) — C-12 install/uninstall remains UNFIXED and is the highest-severity item still
  open in this scope.** `uninstall.sh` still leaves behind `featurelinkCustomIcons.routes.js`,
  `featurelinkCustomIcons.service.js` and `featurelinkArcgisIconset.service.js` — **exactly the
  SSRF and zip-bomb sinks** — and its `server.js` unpatcher prints "removed …" unconditionally
  whether or not anything matched, so a mismatch can leave a `require` pointing at a deleted file
  and **the portal permanently down after an uninstall, with a success message printed.** Neither
  script backs anything up. Also note `install.sh` still does not install `multer`/`unzipper`;
  until it does, run `npm install --save multer@^2 unzipper@^0.12` in the portal directory by hand
  before every install or upgrade. **Test only on a disposable instance.**
- **WP4 (server) — Infra-TAK still carries three unfixed CRITICALs.** Its Jinja XSS
  (`featurelink_displayconfig.py:248`, which also means the QR button is functionally broken for
  every dataset), no CSRF on any of its 8 Flask routes, and no ownership model at all. Its
  installer still enforces root and self-updates from a remote with no verification. Per item I-4
  the standing decision is patch-not-archive, but the patching was descoped from this session.
  **Default taken:** the DOM XSS in its configurator and its CDN scripts were fixed; the rest is
  documented as deferred and the console is flagged trusted-users-only in the test plan.
- **WP2 (CloudTAK) — your ArcGIS session no longer survives closing the browser tab.** C-20 rated
  the plaintext `localStorage` storage of BOTH the access and the refresh token CRITICAL for
  CloudTAK specifically, because any XSS in the origin reads both and the refresh token grants
  long-lived offline access to the whole ArcGIS account. **Default taken:** access token held in
  memory only and never persisted; refresh token moved to `sessionStorage` (per-tab, cleared on
  close). The cost is one extra sign-in per browser session. Alternatives if that is too disruptive
  in the field: a CloudTAK-server-side token broker (removes the refresh token from the browser
  entirely — strictly better, more work), or reverting the refresh token to `localStorage` (restores
  the convenience and the exposure).
- **WP2 (CloudTAK) — unclassified features now render as UNKNOWN, not FRIENDLY.** Features whose
  layer supplies no CoT type defaulted to `a-f-G` (friendly ground), i.e. an unclassified feature
  was presented to the operator as a friendly unit on a tactical display. **Default taken:** `a-u-G`
  (unknown). This visibly changes every existing unclassified layer — confirm before fielding.
- **WP2 (CloudTAK) — plain-HTTP ArcGIS service URLs are now rejected outright.** `http://` is mixed
  content inside CloudTAK and previously failed with an opaque "Failed to fetch". **Default taken:**
  refuse with an explanatory message. Confirm no fielded deployment relies on a plain-HTTP ArcGIS
  Enterprise on a trusted LAN.
- **WP2 (CloudTAK) — minimum auto-refresh interval clamped to 30 s (max 24 h).** An imported config
  could previously install a 1-second refresh loop against the org's ArcGIS service — a
  self-inflicted DoS and a fast route to API-credit exhaustion. **Default taken:** clamp to
  [30 s, 24 h], reject `NaN`, and reflect the accepted value back into the input. Confirm 30 s is
  not too coarse for any operational layer.
- **WP2 (CloudTAK) — ArcGIS Enterprise is still effectively unsupported and that is now explicit.**
  `AccountView` has no portal field, so sign-in always targets `arcgis.com` while layer search
  targets `store.portalUrl` — the two halves can point at different portals, despite the README
  implying Enterprise works. **Default taken:** not fixed; it needs a product decision (add a portal
  field, or document Enterprise as unsupported). Flagged in `docs/testing/cloudtak.md`.
- **WP2 (CloudTAK) — the highest-impact CloudTAK finding is still OPEN.** The untrusted-import
  consent gate (C-02), the ZIP-bomb and size caps (C-19) and the review queue were descoped when
  this package was narrowed. `importConfig.ts`, `importIngest.ts` and `zipReader.ts` are unchanged
  from baseline: auto-ingest still applies third-party config with **no consent prompt** and can
  still silently redirect your PLI position feed to an attacker's Feature Service, triggered by
  anyone who can drop a package named `FeatureLink…` into your Import Manager. The token-exfiltration
  end of that chain IS closed (C-33 — a token is never sent to a host off your signed-in portal).
  **Do not test the auto-import path against a shared TAK server you do not control.** Estimate to
  close: 1–1.5 days.
- **WP2 (CloudTAK) — NEW risk introduced by the C-21 fix: a CORS preflight that did not exist
  before.** Moving the token out of `?token=` and into `X-Esri-Authorization: Bearer` makes the
  browser send an `OPTIONS` preflight before every authenticated ArcGIS request. ArcGIS Online
  answers these correctly; an ArcGIS **Enterprise** deployment behind a locked-down reverse proxy
  might not. Symptom would be: public layers fine, every private layer fails. This is the single
  change most in need of live testing — see `docs/testing/cloudtak.md` §4.1. Fallback if it bites:
  move the token to a POST form field instead.
- **WP5 (cross-cutting) — the suite version number.** `VERSIONING.md` requires one SemVer number
  shared by all seven components, anchored in a new root `VERSION` file. The working tree carried
  `2.7.24`, which is incoherent (a MINOR bump that preserved the PATCH, making `2.6.x` and `2.7.x`
  indistinguishable in a sorted release list). **Default taken: `2.7.0`**, because the policy
  requires a MINOR bump to reset PATCH to 0. If you would rather the auto-symbology work be a
  patch on the 2.6 line, set `VERSION` to `2.6.25` instead — the CI gate follows the file.
- **WP5 — `CODEOWNERS` owner handles are placeholders and the file is currently inert.**
  `@featurelink-maintainers`, `@featurelink-security`, `@featurelink-atak`, `@featurelink-wintak`,
  `@featurelink-cloudtak`, `@featurelink-server` and `@featurelink-contract-owners` do not exist.
  **GitHub silently ignores an unresolvable code owner**, so the file enforces nothing until real
  accounts or teams are substituted — and "require review from Code Owners" branch protection will
  appear satisfied while reviewing nobody. **Default taken:** placeholders committed with a
  prominent in-file warning, so the intent is recorded and substitution is a one-line edit.
- **WP5 — `SECURITY.md` has no monitored security contact address.** The document currently routes
  reporters to GitHub private vulnerability reporting only. A public-safety project should publish
  a monitored address. **Default taken:** GitHub PVR as the primary channel, with the gap flagged
  in-file. Register an address and replace the PLACEHOLDER block.
- **WP5 — PLI retention is unbounded in the implementation, not merely undocumented, and this
  needs a decision before any operational deployment.** Verified across all three clients: there
  is **no purge command, no TTL, no delete path and no operator-visible control** over PLI data
  already written to the hosted ArcGIS layer. `PliHistoryOverlay` caps the *on-device* breadcrumb
  at 5 markers, which bounds nothing already sent. Normal operation updates one row in place per
  device, but three things defeat that: ArcGIS editor-tracking/archiving (a per-layer setting
  FeatureLink neither reads nor sets) retains **every 30-second edit as history**; changing the
  PLI layer orphans the previous row forever; and an update failure orphans a row and adds a new
  one. Only an ArcGIS administrator can erase — a responder cannot remove their own data, and a
  team lead cannot clear an incident. For CAP/SAR this is a records-retention and personal-safety
  exposure. **Default taken:** documented truthfully in `PRIVACY.md` §6 with interim mitigations
  (one dedicated PLI layer per incident, delete the layer at incident close, do not enable editor
  tracking) and the purge capability specified but **not implemented** — it lives in three other
  work packages' trees. **Decide:** is a purge capability a release blocker for 2.7.0?
- **WP5 — there is no config export/backup in any client, and the C-15 fix makes that bite.**
  Giving each ATAK target a distinct `applicationId` (required, or the two builds mutually
  overwrite) means the new package is a *different application* to Android: it installs alongside
  the old one rather than over it, and the old one must be uninstalled by hand. **Uninstalling
  destroys all locally saved layers, display configs and PLI settings**, and no client can export
  or restore them. **Default taken:** documented in `VERSIONING.md` §7 with the recommendation
  that no version-scheme change reach a fielded device before export/import ships. **Decide:**
  ship export/import first, or accept a one-time data loss on upgrade with a written operator
  procedure to re-enter settings?
- **WP5 — icon corpus: 3 unexplained files and 2 genuine name collisions, plus a ~75% dedup
  opportunity.** The orphan analysis nobody had run is now done. Per corpus: 7,450 PNGs, of which
  only **3,771 are referenced by `manifest.json`**; of the 3,679 unreferenced, **3,676 are a
  deliberate second copy** of a referenced icon under its group subdirectory (not junk). Genuinely
  unexplained: `Responder Icons/NIMS Positions/BLANK.png`,
  `Responder Icons/Natural Hazards/Hazard--Other.png`, `Responder Icons/PrePlan/Foam.png`. **Two
  real latent bugs:** `Hazard--Other.png` and `Foam.png` each resolve to **different bytes**
  depending on whether a consumer uses the flat or the grouped path. Overall the 14,900 tracked
  PNGs represent only **3,659 unique images / 3.45 MB** — a shared corpus would cut ~75%.
  **Decide:** deduplicate to a single shared corpus, and which of each colliding pair is correct?
- **WP5 — icon provenance and licensing is UNRESOLVED and was not attempted (descoped).** 11
  third-party iconsets (Default, FalconView, FEMA Icons, Generic Icons, GeoOps, Google, Incident
  Management Icons, OSM, Public Safety Air, Responder Icons, TAK-UserIcons) totalling 7,461 files
  per corpus are redistributed under a repository declaring Apache-2.0, with **no attribution, no
  licence record and no provenance**. Several names (Google, OSM, FEMA, FalconView) strongly imply
  third-party terms that Apache-2.0 does not grant. **No default could be taken** — this needs the
  original source of each set established in writing before award.
- **WP5 — WinTAK SDK redistribution rights are undocumented.** `WinTAK5.{6,7}/libs/` holds SDK
  assemblies (`TAK.Engine.dll` and friends). They are correctly untracked, but whether the built
  `.wpk` may legally embed them is unaddressed anywhere. Obtain written confirmation before
  shipping any `.wpk`.
- **WP5 — `gitleaks-action` needs a licence key if this repository is organisation-owned.** Free
  for personal public repositories. If the repo sits under an org and `GITLEAKS_LICENSE` is not
  set, the `secret-scan` job fails with a *licence* error rather than a findings error — easily
  misread as a detected secret. **Default taken:** the workflow passes the secret through and the
  trap is documented in `docs/testing/ci-and-repo.md` §2.4.
- **WP5 — the keystore `.gitignore` negations were deliberately LEFT IN PLACE.** Appendix F §2.1's
  remediation calls for deleting `!…featurelink.keystore` from all four ignore files and
  `git rm --cached`ing both blobs. Removing an ignore rule does **not** untrack an already-tracked
  file, so doing only my half would have changed nothing except to require `-f` to re-add — while
  risking another agent's in-flight build. **Default taken:** negations retained, annotated in-file
  as DEPRECATED-pending-C-40, and both blobs pinned by SHA-256 in `.github/pinned-binaries.sha256`
  so a substitution is detectable. The `git rm --cached` plus env-var signing passwords in
  `build.gradle` remain to be done together, in one commit.
- **WP5 — `origin/main` is still at the initial commit while `dev` is 33 commits ahead.** Anyone
  cloning the default branch, and every "view on GitHub" landing, gets an essentially empty
  project. Merge `dev` into `main` or repoint the default branch, then apply the branch protection
  in `CONTRIBUTING.md`. Also: `jpat-laptop` is a machine-named personal branch published to the
  shared remote, and `atak5.6/ui` is not an ancestor of `dev` and carries unmerged work of unknown
  status. **And `stash@{0}` ("wip: featurelink-configurator index.html") exists — inspect it with
  `git stash show -p`, do NOT apply it blindly; it predates the remediation and will conflict.**
- **WP5 — the checklist's own §1 process item could not be actioned as written.** It cites
  mis-tagged `SEV-CRITICAL` items at `audit-server.md:346-417`, `audit-cloudtak.md:474-477,635-637`,
  `audit-wintak.md:491-494` and `audit-atak.md:535-537`. **Those standalone files do not exist** —
  `git ls-files | grep -i audit` returns only four icon PNGs named `location-auditorium.png`. The
  content survives only as Appendices A–D inside `FEATURELINK-AUDIT-CHECKLIST.md`, which the brief
  forbids me to edit. Line ranges are reported to the integrator in
  `docs/remediation/wp5-crosscutting.md` for re-tagging to `TEST` / `REMEDIATION`.
- **WP3 — ship WinTAK 5.7 at all?** A full re-fork of 5.7 from current 5.6 (C-17) was descoped as
  too large; the gap is ~6–7 engineer-days, laid out phase by phase in
  `docs/remediation/wp3-wintak-57-parity.md`. The tree **cannot compile as delivered** (`libs/` is
  empty on disk while the csproj references nine assemblies by `HintPath`), so it is not usable
  today regardless. **Default taken:** kept, given a distinct identity
  (`FeatureLink.WinTAK57` / `0.9.0-pre`, was `FeatureLink` / `2.6.9` — *byte-identical to 5.6*),
  and marked "INCOMPLETE PORT — not for release" in the manifest, the assembly description and an
  MSBuild warning on every Release build. **Decide:** fund the port, or state in writing that
  WinTAK 5.7 is out of scope for this delivery.
- **WP3 — the `shp` wire-format key names need ratifying in `CONFIG-FORMAT.md`.** WinTAK now reads
  and writes a compact shape-styling block — `{"f":field,"s":{…},"bv":{value:{…}}}` with style keys
  `sc`/`sw`/`sd`/`fc`/`fs` and colours as `#aarrggbb`. It is the **only** implementation that
  exists today, so it is the de facto format, but `CONFIG-FORMAT.md` documents no `shp` block at
  all and has no producer/consumer matrix — which is exactly what made Appendix F §5's interop
  break unfalsifiable from the documentation. **Default taken:** the shape above, mirroring the
  existing `sym`/`lbl`/`popup` conventions. Needs agreeing with WP1 before ATAK's
  `toCompactJson()` starts emitting it.
- **WP3 — WinTAK deliberately implements the C-24 precedence the *opposite* way to ATAK and
  CloudTAK.** Per the brief, `ResolveShapeStyle` tries the per-value `shapeStyleByValue` lookup
  first and falls back to `singleShapeStyle`. ATAK (`DisplayConfig.java:277`) and CloudTAK
  (`displayConfig.ts:306`) still return the single style first, which makes every per-value entry
  dead code whenever a `defaultSymbol` exists. **Until those are fixed, the same config renders
  differently on WinTAK than on the other two platforms.** That is a real, visible cross-platform
  divergence — intentional and correct on WinTAK's side, but it should not be discovered in the
  field. Confirm the fix lands on ATAK and CloudTAK.
- **WP3 — shape styling on WinTAK renders as a first-vertex marker, not a real line or polygon.**
  WinTAK's plugin SDK exposes no polyline/polygon map-item API that I could verify exists, so a
  line/area feature is still plotted as a marker at its first vertex (the same simplification the
  ATAK downloader makes). **Default taken:** resolve the style, apply the resolved stroke colour to
  that marker, and log/count/surface the fact. That closes the "silently and completely dropped,
  with no warning and no log line" half of Appendix F §5, but it is a *partial*. If a WinTAK
  polyline item type does exist, this becomes a small follow-up — please confirm either way.
- **WP3 — the ArcGIS OAuth client ID is still the ATAK app's, and sign-in may not work at all.**
  `ArcGisAuthService.cs` uses `RXtGmClVuYd1Sp7d`, and the code's own comment admits it was never
  confirmed that the app registration allowlists the loopback redirect URI this desktop flow
  requires. **This is untested against production and could be a total sign-in failure.**
  **Default taken:** left in place but overridable via the `FEATURELINK_ARCGIS_CLIENT_ID`
  environment variable. A dedicated "FeatureLink for WinTAK" registration should be minted, and the
  loopback redirect verified, **before** any delivery.
- **WP3 — is `CotItem.SetAttribute` XML-escaping its input? This must be settled before delivery.**
  Peer-controlled data (a feature callsign, remarks, an `iconsetpath` from a received share)
  reaches broadcast CoT. Whether the WinTAK SDK escapes it is undocumented and could not be tested
  without the SDK. **Default taken:** FeatureLink now strips control characters, caps lengths and
  validates the iconset path format at its own boundary — but that is mitigation, not proof. A
  manual capture procedure is in `docs/testing/wintak.md` §11.2. If the captured XML is malformed,
  it is a CRITICAL finding: XML injection into a federated message bus.
- **WP3 — package version and copyright holder were chosen, not given.** **Defaults taken:**
  WinTAK 5.6 → `2.7.0` (was `2.6.9`, while its README claimed to track the ATAK line, which was 15
  patch versions ahead); WinTAK 5.7 → `0.9.0-pre`; `AssemblyCompany` /
  copyright → `Civil Air Patrol — FeatureLink project`; licence → Apache-2.0 per the repo
  `LICENSE`. Replace if the correct legal entity differs — the binary was previously unattributed
  (`AssemblyCompany("")`, `Copyright © 2026` with no holder), which fails software-provenance
  requirements.
- **WP3 — a peer-supplied auto-refresh interval is now clamped to a 30-second minimum.** A received
  share carrying `{"freq":{"iv":1}}` previously drove a full layer re-download every second,
  forever, on a network peer's say-so — resource exhaustion and ArcGIS credit burn on the
  operator's own account. **Default taken:** 30 s floor. Adjust to taste; the point is that `iv:1`
  must not be honoured.
- **WP3 — the `.wpk` is still unsigned.** No Authenticode signature on `FeatureLink.dll` and the
  package is a plain ZIP, so there is no tamper evidence and it will likely fail plugin-store
  submission. **Default taken:** not done — it needs a code-signing certificate, which is an owner
  decision rather than a code change.
- **WP3 — nothing in `WinTAK5.6/` or `WinTAK5.7/` was ever compiled.** This machine has no
  `msbuild` and no WinTAK SDK. `FeatureLinkDockPane.cs` went from 1455 to ~2160 lines without a
  compiler seeing it. The 216 passing tests cover only the SDK-independent logic. **Expect a build
  pass before behaviour testing**, and treat `selfEvent?.Point?.CE90`/`.LE90` in `SendPliUpdateAsync`
  as the single most likely compile failure (replace both with `double.NaN` if those members do not
  exist). Risk-ordered hand-test plan: `docs/testing/wintak.md`.
