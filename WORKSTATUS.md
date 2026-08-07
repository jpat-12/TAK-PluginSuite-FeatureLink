# Work Status

**Branch:** `audit-remediation` (forked from `dev` @ `d5a7ad9`).
**As of:** 2026-08-06.

Audit remediation against `FEATURELINK-AUDIT-CHECKLIST.md`. The authoritative work list is
Appendix F §4, the deduplicated critical list `C-01…C-40`; per-work-package reports live in
`docs/remediation/`. The pre-remediation session summary that used to fill this file (CloudTAK
share/ingest fixes, auto-iconset, repo housekeeping) is all merged into `dev` and is described
in that branch's history.

> **Nothing in this branch has been runtime-tested.** Every remediation report row is tagged
> `TEST-NOT-EXECUTED-LOCALLY`, and the checklist's own §0 SEV-HIGH coverage gap says no
> component was reviewed at runtime. Per-component test instructions are written and unexecuted
> in `docs/testing/`. No delivery claim should be made against this branch until a live ArcGIS
> org and a live TAK server have been exercised.

## ATAK (WP1) — see `docs/remediation/wp1-atak.md`

**Fixed and committed:** C-07 (symbology resolved on the download path, the owner's field
defect), C-08 (all sublayers addressable, not just `/0`), C-06 (pagination + truncation
surfaced), C-22 (ArcGIS error bodies checked at every parse site), C-09 (OAuth `state` nonce,
in-memory PKCE, explicit broadcast), C-02 (signature-permission receiver + mandatory consent
dialog), C-21 (`Authorization: Bearer`, every logged URL redacted), C-20 (refresh token under
an Android Keystore AES-256/GCM key), C-39 (`Locale.ROOT` canonicalisation), C-24 (per-value
shape styling precedence).

**Fixed after those reports, this session:**
- **Manual Add Layer never sent the token.** `commitAddedLayers()` stamps every added layer
  `"public"` and `fetchLayerInfoChecked()` hardcodes the same, so `downloadLayer()`'s
  `"private".equals(layer.type)` gate withheld the token from a secured layer: 499 "Token
  Required", reported to the operator as an expired session that signing in again never fixed.
  The gate now asks `ArcGISAuthManager.holdsCredentialsFor(url)`, which matches the signed-in
  portal's host (treating the `arcgis.com` family as one, exact-host for Enterprise) and
  refuses unrecognised hosts so an ingested config cannot harvest the token. The add path and
  the QR path are scoped the same way.
- **`sendBroadcast(intent, permission)` dropped every broadcast.** The two-arg form requires the
  *receiver* to hold the permission, but the receiver lives inside ATAK, which never declares
  it. C-02's protection is the `registerReceiver(..., INTERNAL_PERMISSION, ...)` side, which is
  what gates senders. "Open in ATAK" and the whole OAuth callback silently did nothing until
  this was reverted to the one-arg form.
- **New bulk "Update" button** on the Layers page: re-downloads `publicLayers` +
  `sharedPrivateLayers` (deliberately not the browse lists, which would pull the operator's
  whole account onto the map) on the background pool, with a re-entrancy guard and one summary
  instead of N toasts and stacked dialogs.

**Partial / deferred, with the gap stated:**
- **C-28 partial** — pools split, nested same-pool submit removed, counts batched.
  `waitForPublishJob` is still a blocking sleep loop, not the scheduled poll the brief asked for.
- **C-23 partial** — the versioned `shp` block landed; the required
  `fromJson(toCompactJson(x)) == x` round-trip test was not written (depends on C-14).
- **C-25 unproven** — a `scheduleWithFixedDelay` tick exists but was descoped before
  verification. Do not claim auto-refresh works.
- **C-37 half** — all `Map.getOrDefault`/`String.join` crash paths replaced, but
  `minSdkVersion` is still 21 with desugaring off. Recommendation when resumed: raise to 26.
- **C-40 deferred** — keystore and plaintext signing passwords unchanged. No history rewrite
  was ever in scope.
- **C-14 / C-29** — still zero automated tests in either tree. C-29 itself was closed by the
  baseline commit `a8f6f4a`.

Seven owner decisions are waiting in `wp1-atak.md` (token-at-rest mechanism, forced re-sign-in
on upgrade, 8-digit colours, the 50,000-feature ceiling, no certificate pinning, 5.7
`ImportResolver` semantics, iconset zip filename collisions) and in `QUESTIONS-FOR-OWNER.md`.

## Other work packages

`docs/remediation/` carries the reports: `wp2-cloudtak.md`, `wp3-wintak.md`,
`wp3-wintak-57-parity.md`, `wp4-server.md`, `wp5-crosscutting.md`. `WP6-HOTLOAD-BRIEF.md` is
the one **new capability** rather than defect remediation: server-side iconset hot-load for
CloudTAK feature layers, where TAK Portal generates, CloudTAK's server proxies and caches, and
the browser never generates. It is gated on WP2 and WP4 landing first.

## Versioning (C-15)

The suite version is **2.7.0**, canonical in `VERSION`, policy in `VERSIONING.md`.

- Both ATAK trees now declare `PLUGIN_VERSION = "2.7.0"` (was `2.7.24`, a MINOR bump that
  carried the old PATCH across, which §1.1 prohibits).
- `versionCode` is **derived**, no longer the literal `5` that froze Android's only upgrade
  discriminator across nine releases: 2070006 for ATAK 5.6, 2070007 for ATAK 5.7.
- `versionName` embeds the target: `2.7.0+atak5.6` / `2.7.0+atak5.7`, so an installed build can
  be identified without the filename.
- CloudTAK (`0.1.0`), TAK Portal (`1.5.0`) and Infra-TAK (`1.4.0`) are synced to `2.7.0`.

**Still open on versioning:**
- **Distinct `applicationId` per ATAK target (§2) is NOT done.** Both trees still declare
  `com.atakmap.android.featurelink.plugin`, so 5.6 and 5.7 continue to overwrite each other on
  a device. Deliberately deferred: §7 notes the new package installs *alongside* the old one,
  and uninstalling the old one destroys all saved layers, display configs and PLI settings,
  with no export capability anywhere in the suite. §7's own rule is that no version-scheme
  change should reach a fielded device before config export/import ships.
- **WinTAK 5.7 is at `0.9.0-pre` / `AssemblyVersion 0.9.0.7`**, not the suite version. Left
  alone on purpose: C-17 records 5.7 as a regressed fork and blocks release until parity is
  proven by diff, so stamping it `2.7.0` would advertise parity it does not have. This
  knowingly violates the §1 MUST and will fail `check_versions.py` gate item 1 until either
  5.7 reaches parity or the policy grants pre-release forks an exemption.
- `VERSIONING.md:35` says Infra-TAK declares `__version__`; the actual symbol is
  `MODULE_VERSION`. The doc, or the code, needs to move.

## Immediate next steps

1. Restart ATAK and walk the demo path on a device. Nothing here has been run.
2. Expect a forced ArcGIS re-sign-in on first launch after the C-20 change (legacy plaintext
   tokens are discarded, not migrated).
3. Then WP6, gated on WP2/WP4.
