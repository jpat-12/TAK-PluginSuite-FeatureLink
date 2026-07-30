# FeatureLink

An ATAK plugin that bridges ATAK and ArcGIS Feature Services, enabling operators to push map items to hosted feature layers, track PLI history on the map, and manage both private (authenticated) and public ArcGIS layers — all without leaving ATAK.

---

## Features

- **ArcGIS Integration** — Authenticate to an ArcGIS Portal, browse hosted feature layers, and push point items directly from the ATAK map.
- **PLI Auto-Send** — Continuously stream your device's position to a designated ArcGIS Feature Layer on a configurable interval.
- **PLI History Overlay** — Renders up to 5 fading breadcrumb markers on the map showing recent PLI positions (color-matched to your team).
- **Public Layer Support** — Add and manage public ArcGIS Feature Service URLs without authentication.
- **Auto-Iconset Generation** *(new; not yet field-tested)* — Pasting a Feature Service URL reads the layer's own picture-marker (`esriPMS`) renderer symbols and builds a matching ATAK iconset **on-device, no server round-trip**, so the layer's custom marker icons render locally *and* on any other device that ingests the same link. The UID/group/filenames are computed from a frozen cross-platform contract ([`../AUTO-ICONSET-SPEC.md`](../AUTO-ICONSET-SPEC.md)), so a marker shared to another platform resolves to the same icon. A display config that references a missing iconset whose source layer is known is regenerated locally instead of prompting.
- **Radial Menu Integration** — "Send to Feature Layer" action appears on the radial menu of any point map item.
- **QR Code Share** — Generate and scan QR codes to share portal credentials or layer URLs between devices.
- **3-Tab UI** — Home (stats), Layers (authenticated + public), and PLI, with Account and Add Layer as dedicated pushed pages.

---

## Requirements

| Item | Version |
|---|---|
| ATAK-CIV | 5.7.0 |
| Android Min SDK | 21 (Android 5.0) |
| Android Target SDK | 34 |
| Java | 17 |
| Gradle | 8.13.0 |

---

## Build

### Prerequisites

1. Install Android Studio (Hedgehog or later recommended).
2. Place the ATAK SDK JARs in the SDK's expected location, or configure `takrepo.url`, `takrepo.user`, and `takrepo.password` in `local.properties` if you have access to the TAK Maven repo.
3. The `atak-gradle-takdev.jar` plugin must be present at `../../atak-gradle-takdev.jar` relative to the `app/` directory, or override the path in `local.properties`:

```properties
takdev.plugin=/absolute/path/to/atak-gradle-takdev.jar
```

### Build commands

```bash
# Debug APK (CIV flavor)
./gradlew assembleCivDebug

# Release APK
./gradlew assembleCivRelease
```

The output APK will be in `app/build/outputs/apk/civ/debug/` or `.../release/`.

### Flavors

| Flavor | Description |
|---|---|
| `civ` | ATAK-CIV (default) |
| `mil` | ATAK-MIL |
| `gov` | ATAK-GOV (appends `.gov` suffix) |

---

## Installation

1. Side-load the signed APK onto your Android device running ATAK-CIV 5.7.0.
2. In ATAK, open **Settings > Manage Plugins** and enable FeatureLink.
3. The FeatureLink toolbar button will appear in the ATAK toolbar.

---

## Usage

The plugin header (icon, title, and an account button) sits above the Home / Layers / PLI
tab bar and is visible on every tab. Tapping the account button pushes a full-screen
**Account** page (with a back button) for ArcGIS sign-in/out — it no longer lives inline
on the Home tab.

### Home Tab

Displays a collapsible **Feature Statistics** card: total feature count and per-layer
stats fetched live from ArcGIS REST. Tap the chevron to collapse/expand it.

### Account Page (via header button)

1. Enter your ArcGIS Portal URL, username, and password, then tap **Sign in with ArcGIS**.
2. Once signed in, your hosted feature layers populate the Layers tab automatically.
3. Tap the back button (or the system back button) to return to whatever tab you were on.

### Layers Tab

- **My ArcGIS Layers** — populated automatically once signed in. Use the checkboxes to
  select layers for download; set a recurrence interval with the spinner.
- **Public Layers** — tap **Add Layer** to push the **Add Layer** page, which offers
  **Scan Config QR** (primary) or pasting a Feature Service URL directly (fallback).
  The **Upload Display Prefs (JSON)** action also lives on this page. Pasting a URL also
  auto-generates the layer's custom marker icons on-device (see **Auto-Iconset Generation**
  above) — a "Icons ready: …" toast confirms it, and the layer's own renderer styling is
  applied so the icons show on your map with no extra steps.

### PLI Tab

1. Use **Create New Layer** or **Join Existing Layer** (via QR scan) to set up a shared
   position layer.
2. Enable **Auto-Send PLI** to stream your position to the configured PLI layer automatically.

### Send to Feature Layer (Radial Menu)

Long-press any point map item to open its radial menu. Tap **Send to Feature Layer** to push that item's coordinates and metadata to the selected ArcGIS layer.

### QR Codes

FeatureLink supports four distinct QR code types. Any scanner entry point (Add Layer page,
PLI layer URL field, PLI "Scan Config QR" button) accepts all four types and routes
automatically — including pushing you to the Home or Layers tab as needed.

| Where to scan | What it does |
|---|---|
| Layers tab — Add Layer page's "Scan Config QR" | Adds a layer to the list |
| PLI tab — QR icon on the layer URL field | Fills the PLI destination URL |
| PLI tab — Scan Config QR button | Full setup: sign in + set PLI endpoint |

---

## QR Code Schemas

All FeatureLink QR codes are UTF-8 JSON. Every payload includes `"v":1` (schema version) and a `"type"` discriminator. Only the fields listed for each type are required; unknown fields are ignored.

---

### 1 — Credentials (`type: "credentials"`)

Use this when you only want to share an ArcGIS sign-in. Scanning it fills the portal URL, username, and password fields on the Home tab and signs in automatically. No layer data is touched.

```json
{
  "v": 1,
  "type": "credentials",
  "portal": "https://www.arcgis.com",
  "user": "jsmith",
  "pass": "hunter2"
}
```

| Field | Required | Notes |
|---|---|---|
| `portal` | No | Defaults to `https://www.arcgis.com` if omitted |
| `user` | **Yes** | ArcGIS username |
| `pass` | **Yes** | ArcGIS password |

---

### 2 — PLI Endpoint (`type: "pli_endpoint"`)

Use this when you want to share only the Feature Layer URL that devices should stream their PLI to. The device must already be signed in. Scanning it sets the PLI destination immediately — no credentials required.

```json
{
  "v": 1,
  "type": "pli_endpoint",
  "url": "https://services.arcgis.com/ORG/arcgis/rest/services/TeamPLI/FeatureServer/0"
}
```

| Field | Required | Notes |
|---|---|---|
| `url` | **Yes** | ArcGIS Feature Layer REST endpoint |

---

### 3 — Layer Download (`type: "layer_config"`)

Use this when you want to share a Feature Layer URL that should be added to the Layers list and downloaded. Set `"private": true` for layers that require authentication; the device must already be signed in to download those.

```json
{
  "v": 1,
  "type": "layer_config",
  "url": "https://services.arcgis.com/ORG/arcgis/rest/services/Roads/FeatureServer/0",
  "name": "Roads",
  "private": false
}
```

| Field | Required | Notes |
|---|---|---|
| `url` | **Yes** | ArcGIS Feature Layer REST endpoint |
| `name` | No | Display name; falls back to the URL if omitted |
| `private` | No | `true` = authenticated layer; defaults to `false` |

---

### 4 — Full PLI Bundle (`type: "pli_config"`)

Use this to onboard a new device in one scan: it signs the device into ArcGIS **and** sets the PLI destination. This is what the **Share Config QR** button on the PLI tab generates.

```json
{
  "v": 1,
  "type": "pli_config",
  "portal": "https://www.arcgis.com",
  "user": "jsmith",
  "pass": "hunter2",
  "url": "https://services.arcgis.com/ORG/arcgis/rest/services/TeamPLI/FeatureServer/0",
  "name": "Alpha Team PLI"
}
```

| Field | Required | Notes |
|---|---|---|
| `portal` | No | Defaults to `https://www.arcgis.com` if omitted |
| `user` | **Yes** | ArcGIS username |
| `pass` | **Yes** | ArcGIS password |
| `url` | **Yes** | PLI Feature Layer REST endpoint |
| `name` | No | Layer display name; informational only |

---

### Generating QR codes outside the app

Any QR code generator (Python, web tool, etc.) can produce FeatureLink-compatible codes. Encode the JSON as UTF-8 plain text at any standard size (512 × 512 px recommended). Example with Python:

```python
import json, qrcode

payload = {
    "v": 1,
    "type": "credentials",
    "portal": "https://www.arcgis.com",
    "user": "jsmith",
    "pass": "hunter2"
}

img = qrcode.make(json.dumps(payload))
img.save("featurelink_creds.png")
```

---

## Architecture

```
com.atakmap.android.featurelink
├── plugin/
│   ├── FeatureLinkLifecycle.java   # AbstractPlugin entry point
│   └── FeatureLinkTool.java        # Toolbar button, fires SHOW_PLUGIN intent
├── arcgis/
│   ├── ArcGISAuthManager.java      # Token lifecycle, uses AtakAuthenticationDatabase
│   ├── ArcGISRestClient.java       # All HTTP calls (generateToken, query, applyEdits, fetchJson, …)
│   ├── AutoIconset.java            # ArcGIS renderer → on-device ATAK iconset (AUTO-ICONSET-SPEC.md)
│   └── ArcGISLayer.java            # Data model with JSON serialization
├── radial/
│   └── FeatureLinkMenuFactory.java # Injects "Send to Feature Layer" into radial menu
├── FeatureLinkMapComponent.java    # DropDownMapComponent, registers receivers + menu factory
├── FeatureLinkDropDownReceiver.java# 3-tab UI controller
├── LayerListAdapter.java           # Custom list adapter for private/public layers
├── PliHistoryOverlay.java          # Fading breadcrumb markers on the ATAK map
├── QrHelper.java                   # QR code generation (ZXing)
├── QrScanActivity.java             # Camera-based QR scanning activity
└── QrScanDialog.java               # In-plugin QR scan dialog wrapper
```

### Key intents

| Intent action | Purpose |
|---|---|
| `com.atakmap.android.featurelink.SHOW_PLUGIN` | Opens the FeatureLink drop-down |
| `com.atakmap.android.featurelink.SEND_TO_LAYER` | Sends a selected map item to the active layer |

---

## Dependencies

| Library | Version | Purpose |
|---|---|---|
| ATAK Plugin SDK | 5.7.0 | Core ATAK APIs |
| ZXing Core | 3.5.2 | QR code generation / scanning |

All HTTP calls to ArcGIS REST use plain `HttpURLConnection` — no third-party HTTP client required.

---

## License

Licensed under the Apache License, Version 2.0. See the [LICENSE](../LICENSE) file for details.
