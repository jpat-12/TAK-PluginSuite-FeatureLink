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
