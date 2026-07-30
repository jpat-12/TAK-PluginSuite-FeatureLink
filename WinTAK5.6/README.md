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
- **Radial-menu "send to layer" — built via a different entry point.** No native context-menu
  extension point or scene-wide "item selected" service exists anywhere in the reviewed WinTAK
  SDK assemblies (checked `WinTak.Framework.dll`/`WinTak.Common.dll`/`WinTak.Graphics.dll`/
  `WinTak.CursorOnTarget.dll` — the `[Button]`/`[MenuButton]` attributes only cover the ribbon,
  not per-item map actions, and `MapMarker.IsSelected`/`IsSelectedChanged` only fire for items
  this plugin itself creates). Rather than right-clicking a map item, the PLI tab's new "Send
  Item to PLI Layer" card lists everything recently observed going out over the network — via
  `ICommunicationService.PreviewCotBroadcast`, which fires for every outgoing CoT message
  regardless of origin (`Models/RecentCotItem.cs`) — and sends the picked one to the configured
  PLI layer via the same `AddPliFeatureAsync()` call `handleSendToLayer()` uses on the ATAK side
  (always a plain add, matching ATAK's one-time-snapshot semantics, never tracked for update the
  way this device's own PLI position is).
- **Deep-link import from TAK Portal** (`OAuthCallbackActivity`, `ImportConfigActivity`,
  `IMPORT_CONFIG` intent) — **investigated and blocked.** Reflected `WinTAK.exe` itself (not
  just the plugin SDK) looking for a single-instance/URI-activation mechanism a registered
  `featurelink://` protocol handler could hand off to; found `MainWindow.HandleStartupArguments()`
  but no named-pipe/mutex/second-instance-forwarding logic anywhere in the executable. Registering
  the protocol without that would just launch a second, separate `WinTAK.exe` process per click
  rather than delivering the link to an already-running session — not worth building as-is.
- **`DisplayConfig` symbology mapping — icon/color/label/popup now ported.** `Services/
  DisplayStyleResolver.cs` mirrors `DisplayConfig.resolveIconsetPath()`/`resolveColor()`/
  `resolveLabel()`/`buildRemarks()` against the compact `sym`/`lbl`/`popup` JSON a received share
  carries (`ArcGisLayer.SymJson`/`LblJson`/`PopupJson`). Icon resolution posts a `<usericon
  iconsetpath="...">` CoT detail (matching ATAK's wire shape exactly); color has no CoT-detail
  equivalent so it's applied directly via `WinTak.Graphics.MapMarker.Color` post-creation (found
  reflecting the SDK, same technique as the marker-removal/visibility fixes below).
  `cotMapping` (custom uid/type/callsign/remarks field mapping) is still not ported.
- **Layer share via Mission Package — now built both ways.** `ICommunicationService.
  SendMissionPackage(List&lt;string&gt; contactUids, FileInfo, string name, bool)` (found
  reflecting `WinTak.Common.dll`, no reviewed sample exercises it) sends a layer's
  `.featurelinkshare` JSON — built by `BuildShareConfigJson()`, the same compact shape
  `LayerShareHelper.java` produces, reusing this layer's own `SymJson`/`LblJson`/`PopupJson`
  verbatim — to a contact picked via `WinTak.Net.Contacts.IContactService.AllContacts` and a new
  `Views/ContactPickerWindow.xaml` (WinTAK has no built-in list-selection dialog equivalent to
  `AlertDialog.setItems()`). Receiving was already built earlier (the Mission Package folder
  watcher in `FeatureLinkDockPane`).
- **"Upload Display Prefs (JSON)"** on the Add Layer page — built. Opens a
  `System.Windows.Forms.OpenFileDialog` and runs the picked file through the same
  `ImportFeatureLinkShareAsync()` the receive-side folder watcher uses — the manual counterpart
  to that automatic path.

## Where the WinTAK UI diverges from ATAK

`Views/FeatureLinkView.xaml` mirrors the ATAK plugin's actual layout XML (under
`ATAK5.6/app/src/main/res/layout/` and `res/values/colors.xml` / `dimens.xml` / `styles.xml`) as
closely as WPF allows — same tab order (Home/Layers/PLI), same header (icon + title + account
button), same card groupings per page, same collapsible-section chevrons, the same "pushed
full-panel overlay" navigation for Account and Add Layer (rather than popup dialogs), and the same
`fl_*` color values (background `#0A0A0A`/`#161616`, accent `#0099CC`, success `#4CAF50`, error
`#FF5722`, badge colors, etc.) reproduced as WPF `SolidColorBrush` resources. Specific places it
still diverges:

1. **Bundled icon set — mostly done.** `Assets\` now has real PNGs (rasterized from ATAK's vector
   drawables via `System.Drawing`) for `ic_eye_open`/`ic_eye_closed`, `ic_chevron_up`/`down`,
   `ic_share`, `ic_back`, `ic_delete`, `ic_account`, and the header logo. Still glyph-only:
   `ic_qr_scan`, `ic_add`, and the sync action's ⬇/↻ glyph — those are a pure XAML change once/if
   they're needed, same pattern as the ones already done.
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

- **`ILocationService` shape is inferred, not fully documented.** Its use here (`PositionChanged`
  event, `GetGpsPosition()`, `HasConnections`, `GetPositionDocument()`) is copied from the one SDK
  sample that touches self-location (`VideoStream/VideoStreamDockPane.cs`). It exposes no
  team/group-color or CoT `how` directly, but `GetSelfCotEvent()` returns WinTAK's own actual self
  CoT event — its `<__group>` detail (`GetDetailAttribute("__group","name"/"role")`) and `How`
  carry the same information ATAK's self-marker meta strings do, so `SendPliUpdate()` now pulls
  `group_name`/`group_role`/`how` from there instead of sending blanks/a hardcoded `"m-g"`.

### Resolved this pass (previously listed here)

Per-item map visibility, marker removal on shrinking feature sets, and marker color/icon/label/
popup styling were all previously blocked on "no confirmed WinTAK API." Reflecting the SDK
assemblies (`WinTak.Graphics.dll` in particular) turned up real, usable APIs for all of them:
`IMapItemFinderService.GetMapItem(uid)` returns a `WinTak.Graphics.MapItem`/`MapMarker`, whose
`.Visible`, `.Dispose()`, and `.Color` properties are exactly what ATAK's `Marker.setVisible()`/
`removeItem()`/`setColor()` do. No reviewed SDK sample exercises any of these three — same caveat
as the Mission Package APIs above — but the type shapes are unambiguous.

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
WinTak.Graphics.dll
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
