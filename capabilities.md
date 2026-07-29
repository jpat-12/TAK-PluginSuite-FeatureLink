# FeatureLink Suite — Capabilities Matrix

Snapshot of where every platform stands, current as of `dev` branch (2026-07-29). Two parts:
**Part 1** compares the underlying platform/SDK capabilities that shape what's possible on
each surface. **Part 2** tracks FeatureLink's actual feature parity across them.

---

## Part 1 — Platform/SDK Capability Comparison

| Capability | ATAK 5.6 | ATAK 5.7 | WinTAK 5.6 | WinTAK 5.7 | CloudTAK | TAK Portal | Infra-TAK |
|---|---|---|---|---|---|---|---|
| Runtime | Native Android (Java/Kotlin) | Native Android (Java/Kotlin) | .NET Framework 4.8 / WPF / MEF (C#) | Unconfirmed — assume same as 5.6 until SDK reviewed | Browser (Vue3/TypeScript), runs inside CloudTAK web UI | Server-side Node.js (Express/EJS) behind Authentik | Server-side Python/Flask console |
| Plugin packaging | `.apk` (Gradle, ATAK takdev plugin) | `.apk` (same toolchain, newer SDK) | `.wpk` (MSBuild) | Unconfirmed | JS module installed into CloudTAK's plugin registry | Idempotent source patch to a stock TAK Portal checkout (not a real plugin API) | Idempotent source patch to infra-TAK's `app.py` (not a real plugin API) |
| Radial/context menu on map items | Yes (`MapMenuFactory`, `MapMenuButtonWidget`) — used for "Send to Feature Layer" | Presumed yes (unverified against 5.7 SDK) | **No confirmed extension point** — not present in any of the 3 reviewed SDK samples (ImageFolderSync, OpenAtlas, VideoStream) | Unknown | N/A (browser has no long-press radial) — replaced by an in-panel "Send to Feature Layer" picker | N/A | N/A |
| In-app camera / QR scan | Yes (Camera2 + ZXing) | Presumed yes | No confirmed equivalent; deferred | Unknown | No camera access model for a plugin panel — QR dropped in favor of JSON paste/upload/download | N/A (server renders QR images via `qrcode` npm pkg for display, not scanning) | Same — QR *generation* only, via CDN `qrcode` lib |
| In-app OAuth WebView | Yes (`oauth_webview_container`) | Presumed yes | **No** — no first-party embedded browser control available under the .NET 4.8 constraint; opens system browser + loopback `HttpListener` instead | Unknown | Opens ArcGIS's hosted login in a **popup**, relayed back via `postMessage` — works on any deployment with zero per-install OAuth config | N/A (Authentik `forward_single` proxy handles all auth; app never sees ArcGIS creds) | N/A |
| Encrypted credential storage | `AtakAuthenticationDatabase` (built-in) | Same | DPAPI-encrypted blob (`tokens.bin`), hand-rolled | Unknown | Browser `localStorage` (session/refresh tokens only — password never touches CloudTAK) | N/A | N/A |
| Mission-Package send/receive (contact sharing) | Yes, native | Presumed yes | Not exercised in reviewed SDK samples — needs investigation | Unknown | Receive: auto-ingested via CloudTAK's generic Import Manager + `/api/import` polling (reach-in, undocumented API). Send-to-contact: **designed, not built** (`docs/CLOUDTAK-SHARE-DESIGN.md`) — CloudTAK's package route never sets `onReceiveImport`, so recipients must tap "Import" manually | N/A | N/A |
| Self-position / PLI source | `MapView.getSelfMarker()` — reliable | Same | `ILocationService` — shape **inferred**, not documented; no team/group-color or CoT `how` exposed | Unknown | **No confirmed public API** to read the operator's own CoT self-marker — best-effort reach-in, degrades to silent no-op if unavailable | N/A | N/A |
| Per-item map visibility toggle | `Marker.setVisible()` | Same | **No equivalent found** — `Visible` flag only gates next download, not live marker visibility | Unknown | Full control (own Leaflet/MapLibre-style marker layer) | N/A | N/A |
| Deep-link / custom URI-scheme activation | Yes (`featurelink://import`, Android intent-filter) | Presumed yes | **No WinTAK equivalent confirmed** | Unknown | N/A — CloudTAK is same-origin web, links resolve as normal URLs | Native (`/featurelink?load=<id>` is a plain URL, works anywhere) | Native (`/featurelink/featurelink-display-config?load=<id>`) |
| Network HTTP client | `HttpURLConnection` (stock) | Same | `HttpClient`/`WebRequest` (stock .NET) | Unknown | Browser `fetch()` — **subject to CloudTAK server's CSP `connect-src`**, which blocks external calls (ArcGIS) unless explicitly allowlisted | Node `fetch` (Node 18+) | Python `requests` |
| Auth model for outbound calls | Direct HTTP from device, no proxy | Same | Direct HTTP from device | Unknown | Direct HTTP from browser, gated by CSP | N/A (server-to-server where needed) | N/A |
| Distribution/build maturity | Stable, shipped releases (v2.6.x) | Not started | Builds locally, no shipped release yet | Not started | Installs into live CloudTAK, actively iterated | Installs into live TAK Portal, actively iterated | Deprecated — superseded by TAK Portal module |

**Key platform-level takeaways:**
- **WinTAK's biggest open unknowns are extension points**, not the ArcGIS logic itself: no confirmed radial-menu/context-menu API, no confirmed Mission-Package API, no confirmed per-item visibility API, and an inferred (undocumented) self-location API. These block feature parity more than anything ArcGIS-side.
- **CloudTAK's constraints are platform-inherent, not fixable in-plugin**: no self-position API (yet), no camera access model, and a server-side CSP header that must be configured by whoever runs CloudTAK (`NGINX_CSP_CONNECT_SRC`) or every ArcGIS call fails.
- **ATAK 5.7 and WinTAK 5.7 have zero code** — both folders exist in the repo but are empty. Nothing to compare yet; see Part 2 gap table.
- **TAK Portal and Infra-TAK aren't "plugins" in the SDK sense** — they're idempotent source patches to someone else's codebase. That trades a real extension API for update-safety (a plain `git pull` + re-run `install.sh` survives upstream changes), at the cost of "drift risk" if upstream restructures the anchor strings `install.sh` patches against.

---

## Part 2 — FeatureLink Feature Parity by Platform

Status legend: ✅ done · 🟡 partial/stubbed · 🚧 designed but not built · ❌ not present · N/A not applicable · — not started

| Feature | ATAK 5.6 | ATAK 5.7 | WinTAK 5.6 | WinTAK 5.7 | CloudTAK | TAK Portal | Infra-TAK |
|---|---|---|---|---|---|---|---|
| **Overall status** | ✅ Available (shipped, v2.6.22) | — Planned, no code yet | 🟡 In development (scaffold + core sync) | — Planned, after WinTAK 5.6 stabilizes | 🟡 In development | ✅ Available | ⚠️ Deprecated, not supported |
| ArcGIS sign-in | ✅ OAuth2 (hosted login page) | — | ✅ OAuth2 PKCE (loopback listener + system browser) | — | ✅ OAuth2 PKCE (popup + relay page, zero per-deployment setup) | N/A (Authentik gate; plugin never authenticates to ArcGIS on TAK Portal's behalf) | N/A |
| Browse & download feature layers | ✅ | — | ✅ (`SearchUserLayersAsync`, `DownloadLayerAsCotAsync`) | — | ✅ (search + count-only query, download on demand) | N/A (serves saved configs, not live layer browsing) | N/A |
| Create new hosted layer (CSV publish flow) | ✅ | — | ✅ (`CreatePliFeatureServiceAsync`) | — | ✅ (PLI tab create/join) | N/A | N/A |
| Layer auto-refresh (recurrence interval) | ✅ | — | ✅ (15s polling timer) | — | ✅ (`scheduler.ts`) | N/A | N/A |
| PLI auto-send | ✅ (30s) | — | ✅ (30s timer, ported) | — | 🟡 built, but **blocked** — no confirmed self-position API on CloudTAK; degrades to silent no-op | N/A | N/A |
| PLI history breadcrumb overlay | ✅ | — | ❌ not ported | — | 🟡 partial (`cot.ts` breadcrumbs, tied to same self-position gap) | N/A | N/A |
| Send map item to layer (radial menu) | ✅ (native radial menu) | — | ❌ deferred — no confirmed WinTAK context-menu extension point | — | ✅ **adapted** — in-panel "Send to Feature Layer" picker replaces long-press radial | N/A | N/A |
| QR code share/scan | ✅ (4 payload types: credentials, pli_endpoint, layer_config, pli_config) | — | ❌ deferred (disabled in UI; needs ZXing.Net) | — | ❌ **dropped by design** — replaced with download/paste/upload JSON; `credentials`/`pli_config` (plaintext password in QR) dropped entirely | ✅ generates QR (Saved Dataset Link + configurator's compact/full/URL modes) | ✅ generates QR (same configurator) |
| Layer share via Mission Package / contact send | ✅ (native, `.featurelink.json` MP) | — | 🚧 not exercised in reviewed SDK samples | — | 🟡 **receive**: auto-ingest via Import Manager polling (works, fragile reach-in). **Send**: designed only, not built (`docs/CLOUDTAK-SHARE-DESIGN.md`) — recipient must tap Import manually | N/A | N/A |
| Deep-link import (`featurelink://import`) | ✅ | — | ❌ no WinTAK URI-scheme equivalent found | — | N/A (plain URLs work natively) | ✅ (`/featurelink?load=<id>` — plain URL, works via any browser) | ✅ (`/featurelink/featurelink-display-config?load=<id>`) |
| Display Configurator (symbology/labels/popups) | 🟡 consumes configs; own in-plugin editor not built | — | ❌ out of scope for this porting pass; `DownloadedFeature.cs` carries raw attributes so it can be layered in later | — | 🟡 `displayConfig.ts` applies configs; not the authoring tool itself | ✅ **full ported copy** — the canonical admin-facing configurator (symbology/labels/popups/CoT mapping, live preview) | ✅ original standalone tool (now deprecated in favor of TAK Portal's copy) |
| Auto-iconset (renderer → ATAK iconset, on-device, no server) | ✅ built, wired into paste-URL flow; class-break range icons don't self-render yet | — | ❌ not built | — | ❌ not built | ✅ built (`featurelinkArcgisIconset.service.js`, `from-arcgis` + `by-uid` endpoints) | ❌ not built (module deprecated before this feature landed) |
| Feature-count-on-signin (no geometry download) | ❌ not present | — | ❌ not present | — | ✅ built | N/A | N/A |
| Upload/apply display prefs JSON | 🟡 stub — file picker only, doesn't parse/upload | — | ❌ disabled (depends on DisplayConfig, out of scope) | — | ✅ (Add Layer → Import Config: paste/upload/auto-ingested) | ✅ (this **is** the configurator) | ✅ (this **is** the configurator) |
| CoT field mapping (per-column → CoT UID/type/callsign/remarks) | 🟡 `raw_cot_xml` always empty — not wired | — | Unknown/not audited | — | Unknown/not audited | ✅ multi-candidate-column mapping, first-non-blank-wins | Presumed same base tool as TAK Portal's (pre-fork) |
| Encrypted local token storage | ✅ (`AtakAuthenticationDatabase`) | — | ✅ (DPAPI blob, never plaintext) | — | 🟡 `localStorage` (browser-standard, not app-level encryption) | N/A | N/A |
| Known critical bug backlog | Documented (§9 of `ATAK5.6/WORKSTATUS.md`): layer-0 assumption, no token auto-refresh, empty `source_layer`/`source_objectid`, schema-template row never cleaned up | — | Documented (README "Known limitations"): inferred `ILocationService` shape, no marker removal on shrinking feature sets | — | Fixed this session: CSP-driven "Failed to fetch" (needs `NGINX_CSP_CONNECT_SRC` documented), substring-vs-regex import filter bug, hide-layer-leaves-markers bug | Drift risk: `install.sh` patches anchor on exact upstream string matches; breaks loudly (not silently) if upstream restructures | Deprecated — no active bug triage |

**Key parity takeaways:**
- **ATAK 5.6 is the reference implementation** — every other platform is either a direct port of it (WinTAK, CloudTAK) or consumes/produces the same config format (TAK Portal, Infra-TAK).
- **ATAK 5.7 and WinTAK 5.7 are both empty folders** — not started, no code to assess. Treat these as "port ATAK 5.6 → 5.7" and "stabilize WinTAK 5.6, then port to 5.7" respectively once prioritized.
- **WinTAK 5.6's gap list is entirely platform-capability-gated**, not effort-gated: QR, radial send-to-layer, Mission Package share, and DisplayConfig mapping are all "deferred until an extension point is confirmed" rather than "deferred, low priority."
- **CloudTAK's gap list is a mix of by-design adaptations** (QR → JSON paste/download, radial → in-panel picker — these are *done*, not gaps) **and real blockers** (self-position API, contact-send auto-import).
- **Auto-iconset is the newest cross-cutting feature** (not yet field-tested anywhere) — live on ATAK + TAK Portal, explicitly not yet on CloudTAK/WinTAK. It's the one feature actively rolling out suite-wide right now.
- **TAK Portal has effectively superseded Infra-TAK** as the config-authoring surface — Infra-TAK is flagged deprecated/unsupported in the root README, kept only for teams not running TAK Portal.

---

## What Needs Further Work (prioritized by what unblocks the most)

1. **Confirm WinTAK extension points** (context-menu/radial, Mission Package API, per-item visibility, self-location API shape) — this single investigation unblocks four separate WinTAK gaps at once (radial send-to-layer, MP share, PLI breadcrumbs, live marker hide/show).
2. **CloudTAK self-position API** — blocks real PLI auto-send and breadcrumbs; currently silently degrades. Worth revisiting each CloudTAK release for a newly exposed `PluginAPI` surface.
3. **CloudTAK send-to-contact** — design is already complete (`docs/CLOUDTAK-SHARE-DESIGN.md`); needs the profile-asset staging step probed against a live console, then implementation.
4. **Auto-iconset rollout to CloudTAK + WinTAK**, plus Web Map link resolution (currently FeatureServer/layer URLs only) and class-break self-render — tracked in `WebMapFeatureLayer-AutoConfigurator.md`.
5. **ATAK 5.6 known-gaps cleanup** (`WORKSTATUS.md` §9) — token auto-refresh, layer-0-only assumption, empty `source_layer`/`source_objectid` fields, orphaned CSV schema-template row. Low platform-risk, just unfinished.
6. **Field-test everything flagged "not yet field-tested"** — auto-iconset (ATAK + TAK Portal) has never run against real hardware/live layers.
7. **ATAK 5.7 / WinTAK 5.7 ports** — not started; lowest urgency until 5.6.x lines stabilize (explicit sequencing already stated in the root README).
8. **WinTAK icon assets** — `Assets/Large.png`/`Small.png` are missing entirely; the project won't even build in Release without them being added.
9. **Infra-TAK**: no further investment planned (deprecated) — only relevant to teams not yet migrated to TAK Portal.
