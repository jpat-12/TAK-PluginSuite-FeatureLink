# FeatureLink Plugin — Work Status & Handoff

> **For a new agent:** Read this top-to-bottom before touching any file.
> All paths are relative to the repo root (`samples/FeatureLink/`).

---

## 1. What This Plugin Does

**FeatureLink** is an ATAK 5.7.0 plugin that bridges ATAK map items to ArcGIS
Online / Enterprise Feature Layers.  Key capabilities:

| Feature | Status |
|---------|--------|
| Sign in to ArcGIS (token auth) | ✅ Working |
| Create hosted Feature Layer from CSV schema | ✅ Working |
| Join an existing Feature Layer by URL | ✅ Working |
| Auto-send self PLI every 30 s | ✅ Working |
| Send any map item to the layer from the radial menu | ✅ Working |
| Share layer config as a QR code | ✅ Working |
| Scan QR code with device camera to auto-connect | ✅ Working |
| Browse & download private/public layers into ATAK | ✅ Working |
| Upload display prefs JSON | ✅ Wired (file picker only) |
| PLI history breadcrumb overlay | ✅ Working |

---

## 2. File Map

```
app/src/main/java/com/atakmap/android/featurelink/
├── FeatureLinkDropDownReceiver.java   — All UI logic (3 tabs, QR, PLI, radial handler)
├── FeatureLinkMapComponent.java       — Plugin entry point; registers receivers & menu factory
├── QrHelper.java                      — ZXing QR generation + JSON payload encode/decode
├── QrScanActivity.java                — Full-screen camera Activity using Camera2 + ZXing
├── PliHistoryOverlay.java             — MapOverlay drawing PLI breadcrumb trail
├── LayerListAdapter.java              — RecyclerView-style ListView adapter for layer lists
├── arcgis/
│   ├── ArcGISAuthManager.java         — Token generation, credential storage, session state
│   ├── ArcGISRestClient.java          — ALL ArcGIS REST API calls (blocking, background-thread)
│   └── ArcGISLayer.java               — Simple POJO: name, url, type, featureCount, prefs
└── radial/
    └── FeatureLinkMenuFactory.java    — Injects "Send to Feature Layer" button into ATAK radial menu

app/src/main/res/
├── layout/
│   ├── main_layout.xml       — App header (icon/title/account btn) + tab bar (Home/Layers/PLI)
│   │                           + page container + overlay_container (pushed pages) + OAuth overlay
│   ├── page_home.xml         — Feature statistics card only (collapsible); account moved out
│   ├── page_account.xml      — Pushed page: ArcGIS sign-in/out, opened via header account button
│   ├── page_layers.xml       — "My ArcGIS Layers" + "Public Layers" cards, no sub-tabs
│   ├── page_add_layer.xml    — Pushed page: scan-QR (primary) or paste-URL (fallback) + upload prefs
│   ├── page_pli.xml          — PLI feature layer / auto-send / QR config cards
│   └── item_layer.xml        — Single layer row (eye icon, name, interval spinners, action btn)
├── values/
│   ├── colors.xml            — fl_* design-system colors (surfaces, text, accent, status)
│   ├── dimens.xml            — fl_* spacing/radius/text-size scale
│   └── styles.xml            — FL.Text.*, FL.Button.*, FL.Input, FL.Card styles
└── drawable/
    ├── bg_card.xml           — Rounded card surface (used by FL.Card)
    ├── bg_input.xml          — Rounded input bg w/ focused-state border
    ├── bg_button_primary.xml / bg_button_secondary.xml — Filled/outline button states
    ├── ic_account.xml / ic_back.xml / ic_add.xml — Header/overlay-page icons
    └── ic_qr_scan.xml        — Vector drawable (4 corner brackets + centre square)

app/src/main/AndroidManifest.xml      — Permissions (INTERNET, CAMERA) + QrScanActivity
app/build.gradle                      — Dependencies: ZXing 3.5.2; compileSdk 36, targetSdk 34
buildtools/                           — atak-gradle-takdev.jar must be committed here (see §7)
.github/workflows/build.yml           — GitHub Actions CI (see §7)
```

---

## 3. ArcGIS Integration Details

### Auth Flow (`ArcGISAuthManager`)
1. User enters portal URL + username + password on the **Private** tab.
2. `authenticate()` calls `ArcGISRestClient.generateToken()` → stores token in
   `AtakAuthenticationDatabase` (ATAK's encrypted credential store).
3. Token is refreshed automatically on expiry — stored with key `"featurelink_arcgis"`.
4. `getPassword()` reads back from `AtakAuthenticationDatabase` for QR payload building.

### Layer Creation (`ArcGISRestClient.createPliFeatureService`)
Three-step CSV upload workflow:
1. **Upload** — POSTs `SCHEMA_CSV` (22-column header + 1 sample row) to
   `/sharing/rest/content/users/{user}/addItem` as `multipart/form-data`.
2. **Publish** — POSTs to `/sharing/rest/content/users/{user}/publish` with
   `publishParameters.layerInfo.fields` that **explicitly** sets
   `esriFieldTypeDate` / `esriFieldTypeDouble` / `esriFieldTypeString` for all
   22 columns (ArcGIS would otherwise guess wrong types from the CSV data).
3. **Enable editing** — POSTs `updateDefinition` with
   `capabilities: "Create,Delete,Query,Update,Editing"`.
   
Returns `{featureServerUrl}/0` which is persisted in SharedPreferences
`"pli_layer_url"`.

### Feature Write (`ArcGISRestClient.addPliFeature`)
Called for both PLI auto-send and radial-menu "Send to Layer".  Writes all 22
fields via `applyEdits`.

**Critical**: `GeoPoint.getCE()`, `getLE()`, `getAltitude()` return
`Double.NaN` when GPS data is unavailable. `JSONObject.put()` **throws** on NaN.
Fixed via `safeDouble(v)` helper which substitutes `JSONObject.NULL`.

### 22-Column Schema
```
uid, source_system, source_layer, source_objectid,
cot_type, tak_callsign, tak_icon, tak_remarks,
latitude (Double), longitude (Double), hae (Double), ce (Double), le (Double),
time (Date), start_time (Date), stale_time (Date), sent_toFL_time (Date),
sent_by_user, how, sync_status, last_synced (Date), raw_cot_xml
```
Date fields store **Unix epoch milliseconds** (the ArcGIS Feature Service
`applyEdits` REST API expects ms, not ISO strings).

---

## 4. QR Code System

### Payload format (JSON, version 1)
```json
{ "v": 1, "portal": "https://www.arcgis.com", "user": "alice",
  "pass": "secret", "url": "https://.../FeatureServer/0", "name": "Alpha PLI" }
```
`QrHelper.buildPayload()` → `QrHelper.generateBitmap()` (ZXing `QRCodeWriter`).
`QrHelper.parse()` validates `v==1`, non-empty `user` and `url`.

### Share QR (Private tab → "Share Config QR")
Button only enabled when `pliLayerUrl != null && isAuthenticated()`.
Generates bitmap on executor thread, shows in `AlertDialog` via
`getMapView().getContext()` (ATAKActivity — **not** `pluginContext`).

### Scan QR (Home camera icon + Private tab "Scan QR")
Both call `startQrScan()` → `QrScanActivity.launch(pluginContext, callback)`.

**`pluginContext` is mandatory here** — `new Intent(context, QrScanActivity.class)`
resolves the Activity class against `context.getPackageName()`.  Passing
ATAKActivity context causes `ActivityNotFoundException` because Android looks for
the Activity inside `com.atakmap.app.civ` instead of the plugin APK.

`QrScanActivity` is a standalone Activity (programmatic layout, no XML):
- Camera2 API, rear camera preferred, `ImageReader(1280×720, YUV_420_888)`
- ZXing `QRCodeReader` decodes each frame on background thread
- On decode: calls static `Callback`, then `finish()`
- Static callback is cleared in `FeatureLinkDropDownReceiver.disposeImpl()`

---

## 5. Radial Menu ("Send to Feature Layer")

`FeatureLinkMenuFactory` implements `MapMenuFactory`. Registered in
`FeatureLinkMapComponent.onCreate()` via `MapMenuReceiver.getInstance()`.

When ATAK shows a radial menu for any map item, `create(MapItem)` is called.
The factory adds a `MapMenuButtonWidget` using the sizing pattern from the
`radialmenudemo` SDK sample (critical — wrong sizing = invisible or
mis-sized button):
```java
btn.setOrientation(btn.getOrientationAngle(), menu.getInnerRadius());
btn.setButtonSize(btn.getButtonSpan(), menu.getButtonWidth());
// weight = average of existing children
```
Button click fires `Intent(SEND_TO_LAYER)` with `uid` extra →
`FeatureLinkDropDownReceiver.handleSendToLayer(uid)`.

Icon: `"asset:///icons/incomplete.png"` (confirmed present in ATAK 5.7.0).
Custom PNG at `app/src/main/assets/icons/ic_send_to_layer.png` exists but may
not load reliably across devices — fall back to the built-in icon if needed.

---

## 6. Fixed Bugs (Do Not Re-introduce)

| Bug | Root cause | Fix location |
|-----|-----------|--------------|
| `BadTokenException` on all dialogs | `AlertDialog.Builder(pluginContext)` — plugin context has no window token | All dialogs now use `getMapView().getContext()` (ATAKActivity) |
| `ActivityNotFoundException: QrScanActivity` | `new Intent(atakCtx, QrScanActivity.class)` resolves to ATAK's package | `QrScanActivity.launch()` takes `pluginContext` |
| `JSONException: Forbidden numeric value: NaN` | `GeoPoint.getCE/LE/getAltitude()` return `NaN` when GPS unavailable | `ArcGISRestClient.safeDouble()` replaces NaN/Inf with `JSONObject.NULL` |
| `onActivityResult` does not exist | `MapComponent` in ATAK 5.7.0 has no `onActivityResult` | Removed entirely; QR scan uses Camera2 + static callback |
| `<rect>` in vector drawable | Android vector drawables do not support `<rect>` element | `ic_qr_scan.xml` uses `<path android:pathData="M13,13 L19,13 L19,19 L13,19 Z">` |
| `addWidget` vs `addChildWidget` | Wrong method name on `IMapMenuWidget` | `FeatureLinkMenuFactory` uses `menu.addChildWidget(btn)` |

---

## 7. Build Setup

### Local build
```
ATAK-CIV-5.7.0.7-SDK/
  atak-gradle-takdev.jar   ← Gradle plugin (151 KB)
  main.jar                 ← ATAK SDK compile classes (32 MB)
  atak.apk                 ← Full ATAK app (378 MB, needed by takdev)
  android_keystore         ← Debug keystore (password: tnttnt)
  samples/FeatureLink/     ← This repo
```
`local.properties` (gitignored) must contain `sdk.dir=<path to Android SDK>`.
The takdev plugin finds `main.jar` and `atak.apk` via the `../../` relative path.

### GitHub Actions CI (`.github/workflows/build.yml`)

**One-time setup** before the first CI run:

1. **Commit the build plugin**
   ```bash
   mkdir buildtools
   cp ../../atak-gradle-takdev.jar buildtools/
   git add buildtools/atak-gradle-takdev.jar
   git commit -m "add TAK build plugin"
   ```

2. **Add GitHub Secrets** (Settings → Secrets → Actions):
   | Secret | Value |
   |--------|-------|
   | `TAKREPO_URL` | TAK developer maven repo URL (from your TAK account) |
   | `TAKREPO_USER` | TAK repo username |
   | `TAKREPO_PASSWORD` | TAK repo password |
   
   Without these, the build falls back to the local jar only — it will **not**
   have `main.jar` and will fail with unresolved ATAK imports.
   If you don't have TAK repo access, upload `main.jar` as a GitHub Release
   asset and add a download step before `assembleCivDebug`.

3. **Push** — the workflow triggers on push to `main`, `master`, or `develop`.
   The APK appears under **Actions → (run) → Artifacts**.

---

## 8. Key ATAK API Gotchas

| Situation | Right approach |
|-----------|---------------|
| Show any dialog / AlertDialog | `getMapView().getContext()` (ATAKActivity), never `pluginContext` |
| Inflate plugin layouts | `PluginLayoutInflater.inflate(pluginContext, ...)` |
| Start a plugin Activity | `new Intent(pluginContext, MyActivity.class)` + `FLAG_ACTIVITY_NEW_TASK` |
| Register intents for plugin | `DocumentedIntentFilter` + `registerDropDownReceiver()` |
| Radial menu button sizing | Copy `radialmenudemo` pattern exactly (orientation + buttonSize + layoutWeight) |
| Background networking | Always use `ExecutorService`; methods in `ArcGISRestClient` are blocking |
| ATAK credential storage | `AtakAuthenticationDatabase.getCredentials(key, "")` |
| `onActivityResult` | Does NOT exist on `MapComponent`/`DropDownReceiver` in ATAK 5.7.0 |
| System back button in a drop-down | Override `protected boolean onBackButtonPressed()` on `DropDownReceiver`; return `true` to consume (e.g. close an overlay page instead of the whole drop-down), `false` to fall through to default (closes the drop-down) |

---

## 9. TODO / Known Gaps

- [ ] **PLI stale time UX** — currently hardcoded (30 s PLI, 7 days sent items); expose as settings
- [ ] **Token refresh** — token is 60-min expiry; `ArcGISAuthManager` does not auto-refresh mid-session
- [ ] **Raw CoT XML** — `raw_cot_xml` field is always written as `""` empty string; wire up actual CoT serialisation
- [ ] **Layer 0 assumption** — `ensureLayerIndex()` always appends `/0`; multi-layer services not supported
- [ ] **Schema template row** — the `SCHEMA_TEMPLATE` row inserted during CSV publish is never deleted; add a cleanup call after layer creation
- [ ] **`source_layer` / `source_objectid`** — always written as empty; intended to track which ATAK layer / objectid the feature originated from
- [ ] **Upload display prefs** — `showPrefFileDialog()` opens a file picker but does not actually parse or upload the JSON; implementation is a stub
- [ ] **Release build signing** — `proguard-gradle.txt` / repackage config exists but release keystore management is not documented
- [ ] **Mode 3 Esri renderer mapping unverified** — `SymConfig.fromEsriRenderer()` (`DisplayConfig.java`) translates `_v`-schema `symbology` JSON assuming the *standard* Esri REST renderer/symbol format (simple/uniqueValue/classBreaks over esriSMS/esriPMS, `color` as `[r,g,b,a]` 0-255 arrays). This was never checked against a real payload from the FeatureLink Display Configurator console — if it emits a custom shape instead, get a sample payload and correct the mapping.
