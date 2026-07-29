# FeatureLink (WinTAK)

A WinTAK 5.6 port of the ATAK **FeatureLink** plugin (`com.atakmap.android.featurelink.plugin`,
Android side currently at v2.6.22). Signs in to ArcGIS Online/Portal via OAuth2 PKCE, browses and
syncs ArcGIS Feature Layers to the map as CoT markers, and sends this device's Position Location
Information (PLI) to a designated ArcGIS Feature Layer on a timer.

This is a **scaffold + core sync** port — see "What's deferred" below for the ATAK features
intentionally left out of this pass, and "Where the WinTAK UI diverges from ATAK" for the visual
differences from the Android layout.

---

## What this port includes

- **OAuth2 PKCE sign-in** (`Services/ArcGisAuthService.cs`) — ported from `ArcGISAuthManager` +
  `OAuthHelper`. Redirect capture uses a local loopback `HttpListener` and the system browser
  instead of ATAK's in-app WebView (see "OAuth redirect mechanism" below).
- **Layer browse/download** (`Services/ArcGisFeatureService.cs`) — ported from
  `ArcGISRestClient`: `SearchUserLayersAsync`, `FetchLayerInfoAsync`, `QueryFeatureCountAsync`,
  `DownloadLayerAsCotAsync`, each downloaded feature converted to a `CotEvent` and posted via
  `ICotMessageSender.Process()`.
- **PLI auto-send** — a 30-second timer builds a CoT-style feature from `ILocationService` and
  posts/updates it on the configured ArcGIS Feature Layer via `applyEdits`
  (`AddPliFeatureAsync`/`UpdatePliFeatureAsync`), and `CreatePliFeatureServiceAsync` ports the
  ATAK plugin's "Create New Layer" CSV-upload-then-publish flow.
- **Layer auto-refresh** — each layer's `RecurrenceInterval`/`RecurrenceUnit` is honored by a
  15-second polling timer, mirroring `checkLayerRecurrence()`.
- **Persistence** (`Services/SettingsStore.cs`) — layer list, PLI config, and excluded-private-URL
  list as XML at `%AppData%\WinTAK\FeatureLink\settings.xml`; ArcGIS refresh-token state as a
  separate DPAPI-encrypted blob at `%AppData%\WinTAK\FeatureLink\tokens.bin` (never plaintext).
- **3-tab UI** (`Views/FeatureLinkView.xaml`) — Home / Layers / PLI, laid out to match the ATAK
  plugin's own `main_layout.xml` / `page_home.xml` / `page_layers.xml` / `page_pli.xml` /
  `page_account.xml` / `page_add_layer.xml` / `item_layer.xml`, including the same color palette,
  card/collapsible-section structure, and "pushed full-panel overlay" pattern for the Account and
  Add Layer pages. See the divergence list below for what couldn't be reproduced exactly.

## What's deferred

Each of these is visible in the UI (where the ATAK layout would show it) but disabled, so the
layout stays recognizable without pretending the feature works:

- **QR code sharing/scanning** (`QrHelper`/`QrScanDialog` on the ATAK side) — the "Share Config
  QR" / "Scan Config QR" buttons on the PLI tab, and "Scan Config QR" on the Add Layer page, are
  present but disabled. To implement: add a QR encode/decode library (ZXing.Net is the .NET
  equivalent of the ATAK plugin's `com.google.zxing:core` dependency), reuse
  `ArcGisFeatureService`'s URL/JSON shapes, and reuse `SettingsStore.FeatureLinkSettings` for the
  round-trip payload — most of the plumbing (`applyScannedConfig`, `buildPliPayload` in
  `QrHelper.java`) is a fairly direct port once QR encode/decode exists on the .NET side.
- **Radial-menu "send to layer"** (`FeatureLinkMenuFactory`, `SEND_TO_LAYER` intent,
  `handleSendToLayer()`) — WinTAK's map-item context-menu extension point wasn't present in any
  of the three SDK samples reviewed (ImageFolderSync, OpenAtlas, VideoStream), so there's no
  confirmed pattern to port this against yet. Start by searching the full WinTAK SDK docs for a
  `IMapItemContextMenu`/`IMapMenuFactory`-shaped extension point before attempting this.
- **Deep-link import from TAK Portal** (`OAuthCallbackActivity`, `ImportConfigActivity`,
  `IMPORT_CONFIG` intent) — ATAK's version relies on Android's `featurelink://` intent-filter
  activation, which has no WinTAK equivalent. The OAuth piece of this is superseded by the
  loopback-listener flow already implemented; the "import a shared display config by tapping a
  link" piece would need its own investigation into whether WinTAK supports any URI-scheme
  registration at all.
- **`DisplayConfig` symbology mapping** (`DisplayConfig.java` — sym/lbl/popup/cotMapping per
  layer) — out of scope per the porting brief. `Models/DownloadedFeature.cs` already carries the
  full raw ArcGIS `attributes` dictionary per feature specifically so this can be layered in later
  without re-touching `ArcGisFeatureService.DownloadLayerAsCotAsync`.
- **Layer share via Mission Package** (`LayerShareHelper`, `sendLayerShare()`) — the per-row
  "share" icon button in the Layers tab is present (mirroring `item_layer.xml`'s
  `layer_share_btn`) but disabled. ATAK's version packages a layer's config as a `.featurelink.json`
  Mission Package sent to a contact; WinTAK's contact/Mission Package APIs weren't exercised in
  the reviewed SDK samples, so this needs the same investigation as radial-menu send-to-layer.
- **"Upload Display Prefs (JSON)"** on the Add Layer page — depends on `DisplayConfig` (above),
  so it's disabled for the same reason.

## Where the WinTAK UI diverges from ATAK

`Views/FeatureLinkView.xaml` mirrors the ATAK plugin's actual layout XML (under
`ATAK5.6/app/src/main/res/layout/` and `res/values/colors.xml` / `dimens.xml` / `styles.xml`) as
closely as WPF allows — same tab order (Home/Layers/PLI), same header (icon + title + account
button), same card groupings per page, same collapsible-section chevrons, the same "pushed
full-panel overlay" navigation for Account and Add Layer (rather than popup dialogs), and the same
`fl_*` color values (background `#0A0A0A`/`#161616`, accent `#0099CC`, success `#4CAF50`, error
`#FF5722`, badge colors, etc.) reproduced as WPF `SolidColorBrush` resources. Specific places it
still diverges:

1. **No bundled icon set.** ATAK's layout references `ic_eye`, `ic_chevron_up/down`, `ic_share`,
   `ic_qr_scan`, `ic_back`, `ic_add`, `ic_account`, and `ic_menu_delete` drawables. This port has
   no equivalent vector/PNG icon set yet, so every icon button uses a Unicode glyph
   (`◉`/`○`, `▲`/`▼`, `⇗`, `‹`, `🗑`, a colored dot for account status) instead. Swapping these
   for real icons is a pure XAML change once icon assets exist — see "Assets" below.
2. **No in-panel OAuth WebView.** ATAK hosts the ArcGIS sign-in page in an in-app WebView overlay
   (`oauth_webview_container` in `main_layout.xml`). WinTAK has no first-party embedded browser
   control available under this project's constraints (.NET Framework 4.8, no extra NuGet
   dependencies beyond what OpenAtlas already uses), so sign-in opens the system default browser
   instead (see `Services/ArcGisAuthService.cs`). There is consequently no WPF equivalent of that
   overlay — the Account page just shows a "Sign in with ArcGIS" button and status text.
3. **`TabControl`-free tab bar.** Rather than WPF's built-in `TabControl`, the tab bar is three
   plain `Button`s bound to `NavigateHomeCommand`/`NavigateLayersCommand`/`NavigatePliCommand`
   with a `CurrentTabIndex`-driven `DataTrigger` per button for the underline/highlight — this
   was necessary to reproduce the exact `main_layout.xml` visual (flat text buttons with a
   2px accent underline on the selected tab) rather than a themed `TabItem` chrome.
4. **Layer row action icons are simplified relative to `item_layer.xml`.** ATAK's row swaps the
   action icon's meaning per layer type (download/refresh for private, delete for public) and
   only shows a separate delete icon for private layers. This port instead always shows both a
   "sync" and a "remove" icon per row (for both private and public layers) since the underlying
   `DownloadLayerCommand`/`RemoveLayerCommand` behavior is identical either way and showing both
   consistently is less surprising in a first WinTAK pass — functionally a superset of the ATAK
   behavior, not a smaller one.
5. **No collapsible-section auto-collapse-on-first-connect animation.** ATAK's PLI Feature Layer
   card auto-collapses the first time a PLI destination becomes connected
   (`maybeAutoCollapsePliLayerSection()`); this port reproduces the same *logic*
   (`FeatureLinkDockPane.MaybeAutoCollapsePliLayerSection()`) but WPF's `Visibility` toggle is an
   instant collapse rather than ATAK's (also instant, no ATAK-side animation either) — so this one
   is actually not a divergence, just called out for completeness since it's easy to miss.

## Known limitations

- **No per-item map visibility toggle.** ATAK's `Marker.setVisible()` lets a layer's markers be
  hidden without removing them. No equivalent per-CoT-item visibility API turned up in any of the
  three reviewed WinTAK SDK samples — `ArcGisLayer.Visible` currently only gates whether a feature
  is (re-)posted on the *next* download, not live visibility of already-posted markers. Revisit if
  a WinTAK item-visibility API is confirmed to exist.
- **No marker removal on shrinking feature sets.** Re-downloading a layer that has fewer features
  than before does not remove the map items for features that disappeared server-side — there is
  no documented "remove CoT item by uid" call in the reviewed samples either.
- **`ILocationService` shape is inferred, not fully documented.** Its use here (`PositionChanged`
  event, `GetGpsPosition()`, `HasConnections`, `GetPositionDocument()`) is copied from the one SDK
  sample that touches self-location (`VideoStream/VideoStreamDockPane.cs`). It exposes no
  team/group-color or CoT `how` the way ATAK's self-marker meta strings do, so the PLI payload
  sends empty strings for `group_name`/`group_role` and a hardcoded `"m-g"` for `how` — see the
  `TODO` comment in `SendPliUpdate()`.

---

## Requirements

- WinTAK 5.6.0.151 or later
- An ArcGIS Online or ArcGIS Enterprise Portal account with permission to create hosted feature
  layers (only needed for "Create New Layer" on the PLI tab — "Join Existing Layer" only needs an
  existing layer URL with edit access)

---

## Developer Setup

### Prerequisites

- Visual Studio 2022
- .NET Framework 4.8
- WinTAK 5.6.0.151 installed at `D:\Apps\`
- Newtonsoft.Json 13.0.3 (NuGet — restore via `packages.config`)

### SDK DLLs

The `libs\` folder is git-ignored. Copy the following files from your WinTAK installation
(`D:\Apps\`) into `libs\`:

```
TAK.Engine.dll
WinTak.Framework.dll
WinTak.Common.dll
WinTak.CursorOnTarget.dll
WinTak.Net.dll
Prism.dll
Prism.Mef.Wpf.dll
Prism.Wpf.dll
```

### Assets

`Assets\Large.png` and `Assets\Small.png` are **not** included in this port (per the porting
brief — binary icon assets weren't generated). The `.csproj`'s `Resource` items reference these
paths and MSBuild will fail with a missing-file error until real PNGs are added:

- `Assets\Large.png` — 256×256, used for the ribbon button's large icon and the `.wpk` package icon
- `Assets\Small.png` — 32×32, used for the ribbon button's small icon

A reasonable starting point is exporting the ATAK plugin's own `ic_launcher` at those two sizes.

### ArcGIS OAuth application

The OAuth client ID reused from the ATAK build (`RXtGmClVuYd1Sp7d`) will only work for sign-in
once its registered redirect-URI allowlist includes a loopback URI (e.g.
`http://localhost:51000/callback/` through `http://localhost:51050/callback/`, matching the port
range `ArcGisAuthService` scans) — see the design note at the top of
`Services/ArcGisAuthService.cs`. Coordinate with whoever administers that ArcGIS OAuth application
before shipping a build that expects sign-in to work; alternatively, register a separate
"FeatureLink for WinTAK" OAuth application with its own client ID.

### Build

| Configuration | Output |
|---|---|
| `Debug` | Compiles and copies DLL to `D:\Apps\Plugins\` for live testing |
| `Release` | Compiles and produces `bin\Release\FeatureLink-<version>-5.6.0.151.wpk` |

### Releasing a new version

1. Bump `<WpkVersion>` in `FeatureLink.csproj` (now tracking the Android side's version numbering,
   starting from `2.6.1`, rather than its own independent line)
2. Build in Release configuration
3. Create a GitHub release, tag it (e.g. `v2.6.1`), and attach the `.wpk` from `bin\Release\`

---

## License

Copyright 2026 Jonathan V. Pattara

Licensed under the [Apache License, Version 2.0](LICENSE).
