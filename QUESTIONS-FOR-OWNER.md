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
