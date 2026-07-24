# Infra-TAK Module — FeatureLink

**Current version: 1.4.0** — `MODULE_VERSION` in `featurelink_displayconfig.py`
is the single source of truth; `install.sh` reads it back out with `grep`
after every sync. No separate CHANGELOG, the commit log is the changelog.

Adds a **FeatureLink** link to the sidebar (sorted last) and a module card
to the home page of an [infra-TAK](https://github.com/jpat-12/infra-TAK)
console, both using FeatureLink's own icon (the same layered-map mark as the
ATAK plugin's launcher icon). The link opens the **`/featurelink` hub** —
the saved dataset configs list plus an entry point into the Display
Configurator at `/featurelink/featurelink-display-config`. The configurator
itself is the same tool that used to live at
`jpat-12.github.io/FeatureLink-DisplayConfig` (now retired — see below),
served from the console instead of GitHub Pages, plus a save/hub layer on
top so configs don't just live in a downloaded file.

`install.sh` migrates any pre-v1.2.0 install automatically (old route was
`/featurelink-display-config` directly, old icon was a tak.gov logo
placeholder) — just re-run it, no manual cleanup needed. The old routes also
redirect to the new ones, so previously shared links/QR codes keep working.

The `/featurelink` hub uses infra-TAK's actual design system (same CSS
variables/classes as `/esri`, `/takserver`, etc., plus the real console
sidebar) rather than a bespoke look, so it reads as part of the console. The
configurator itself (`/featurelink/featurelink-display-config`) keeps its
own distinct look — that's the original standalone app's UI, unchanged.

## What it does

A client-side web app for configuring display settings for FeatureLayer
exports — symbology, labels, popups, and layer properties — with a live QR
code export, plus a save/admin layer:

- **Upload data** — CSV, TSV, JSON (ArcGIS FeatureLayer query response),
  GeoJSON, Excel (.xlsx), or a live ArcGIS FeatureLayer URL
- **Symbology** — Simple marker, Unique Values, Class Breaks (Graduated),
  Icon Symbol (10 bundled iconsets), Rule-Based (If/Then conditions)
- **Labels** — Field, font size, color, bold/italic, halo
- **Popups** — Title field, visible fields & aliases, custom HTML template
- **Layer properties** — Name, opacity, scale visibility
- **Export** — Download a display config JSON ready for use with the
  FeatureLink ATAK plugin
- **QR Code** — Share your config via QR scan: compact/full payload for
  another instance of the configurator to import, URL+Config to also encode
  a FeatureLayer source, or (once saved) a **Saved Dataset Link** — a plain
  URL that opens this exact dataset + config in a browser with a single
  camera scan, no app needed
- **Save** — click **💾 Save** (top right, once data is loaded) to persist
  the dataset (the uploaded file, or the FeatureLayer URL) and its display
  config to the console. Saving again with the same session updates the
  same entry; the name prompt lets you rename it.
- **`/featurelink` hub** (linked from the top of the configurator, and from
  the sidebar/home page) — lists every saved dataset config with its
  source, field count, and last-updated time, plus a button to start a new
  one. **Open** reloads a saved entry back into the configurator (dataset
  re-fetched/re-parsed, config re-applied exactly — no lossy round-trip
  through the export format). **QR** pops up the same Saved Dataset Link QR
  right on the hub page — no need to open the configurator first. **Copy
  link** copies that same URL. **Delete** removes it.

Uploaded files and saved configs are stored under
`CONFIG_DIR/featurelink_displayconfig/datasets/` on the console (one
directory per saved entry: `record.json` for the config + metadata, plus
`data.bin` for file-based sources). FeatureLayer-URL-based entries store just
the URL and re-fetch live data on open.

## Payload formats (for the ATAK plugin's QR scanner / config importer)

There are **five distinct payload shapes** the tool produces. The first two
are JSON meant to be parsed; the QR modes exist to fit that JSON in a QR
code and use shortened keys accordingly. The last one is not JSON at all.

### 1. Exported Config JSON (the "⬇ Export Config JSON" download)

```jsonc
{
  "_version": "1.1",
  "_tool": "FeatureLink Display Configurator",
  "_generated": "2026-01-01T00:00:00.000Z",
  "_source": "my-data.csv",              // uploaded filename, or null if loaded from a URL
  "featureLayerUrl": "https://.../FeatureServer/0",  // or null if loaded from a file
  "_rowCount": 1234,
  "fields": [ { "name": "STATUS", "type": "str" }, ... ],  // type: str|num|dat|geo
  "layer": { "name": "...", "opacity": 1.0, "minScale": 0, "maxScale": 0, "visible": true },
  "symbology": { /* Esri renderer JSON — see "Symbology object" below */ },
  "labels": { "enabled": true, "field": "...", "fontSize": 12, "color": "#ffffff", "haloColor": "#000000", "haloSize": 1, "bold": false, "italic": false },
  "popup": {
    "enabled": true, "titleField": "...", "customHtml": false, "htmlTemplate": null,
    "fields": [ { "field": "STATUS", "alias": "Status" }, ... ]
  }
}
```

### 2. QR "Full config" mode (`_v` schema — similar to #1 but NOT identical keys)

```jsonc
{
  "_v": "1.1",
  "_tool": "FeatureLink Display Configurator",
  "_src": "my-data.csv",                 // or null
  "featureLayerUrl": "https://.../FeatureServer/0",  // or null
  "fields": [ { "n": "STATUS", "t": "str" }, ... ],   // note: n/t, not name/type
  "layer": { ... same shape as #1 ... },
  "symbology": { /* identical shape to #1 — same generator function */ },
  "labels": { ... same shape as #1, including haloColor/haloSize ... },
  "popup": {
    "enabled": true, "title": "...", "html": null,     // note: "title" not "titleField"
    "fields": [ { "f": "STATUS", "a": "Status" }, ... ] // note: f/a, not field/alias
  }
}
```

### 3. QR "Compact" mode (`v:1` schema — smallest payload, no raw field list)

```jsonc
{
  "v": 1,
  "src": "my-data.csv",                  // or null
  "layer": { "name": "...", "opacity": 1.0, "visible": true },  // no minScale/maxScale
  "sym": { /* discriminated union by "t" — see "sym object" below */ },
  "lbl": { "f": "...", "sz": 12, "c": "#ffffff", "b": false, "i": false } /* or null if labels disabled — no halo */,
  "popup": {
    "t": "TITLE_FIELD",
    "flds": [ "STATUS", ["NAME", "Display Name"], ... ]  // string = same alias; [field, alias] = renamed
  }
}
```

### 4. QR "URL + Config" mode (`v:2` schema — compact + the layer URL)

Identical to #3 (compact), plus a top-level `"url"` field with the
FeatureLayer URL, and `v: 2` instead of `v: 1`. Only generated when the
dataset was loaded from a URL (falls back to compact/`v:1` otherwise).

```jsonc
{ "v": 2, "url": "https://.../FeatureServer/0", "layer": {...}, "sym": {...}, "lbl": {...}, "popup": {...} }
```

### 5. QR "Saved Dataset Link" mode — **not JSON**

A plain URL string (no wrapping object, no `v` field):

```
https://<console-host>/featurelink/featurelink-display-config?load=<dataset-id>
```

Scanning it should open that URL in a browser (or an in-app WebView) —
there's nothing to parse. It only works for datasets that have been
**Saved** first (the QR option is disabled otherwise), and `<dataset-id>`
is a 32-char lowercase hex string.

### `sym` object (used in payloads #3 and #4)

Discriminated by `t`:

| `t` | Meaning | Fields |
|---|---|---|
| `s` | Simple marker | `c` color hex, `oc` outline color hex, `sz` size, `sh` shape (`circle`\|`square`\|`diamond`\|`triangle`\|`cross`\|`x`\|`star`), `op` opacity 0–1 |
| `adv` | Rule-based + per-value (the "advanced" editor) | `f` field name, `vs`: `[{v, c, sh, m, is, ic, up}]` (up to 20; `m` is `shape`\|`icon`, `is`/`ic` = iconset/icon filename when `m:"icon"`), `r`: `[{f, o, v, c, sh, m, is, ic, up}]` (rules; `o` is one of `= ≠ contains "starts with" > < >= <= "is empty" "is not empty"`) |
| `ic` | Single icon symbol | `is` iconset name, `ic` icon filename, `up` **usericonPath — use this, not `is`/`ic`** (see below), `sz` size (default 24), `op` opacity |
| `rb` | Rules-based (shape/color only, no icons) | `rules`: `[{f, o, v, c, sh}]`, `dc` default color hex — **note:** default shape/size/opacity are NOT preserved in this compact form |
| `uv` | Unique values | `f` field, `uv`: `[{v, c}]` (up to 30) |
| `cb` | Class breaks | `f` field, `cb`: `[{mn, mx, c}]` — **note:** this tool's own importer does not currently re-apply `cb` payloads (known gap); if your parser handles it directly this isn't a concern |

### Symbology object (Esri renderer JSON, used in payloads #1 and #2)

Same shape either way — one function builds it for both:

- `{"type":"simple", "symbol": {"type":"simple-marker"|"picture-marker", ...}}`
- `{"type":"rule-based", "field", "rules":[{"where","label","symbol"}], "defaultSymbol"}`
- `{"type":"unique-value", "field", "uniqueValueInfos":[{"value","label","symbol"}]}`
- `{"type":"class-breaks", "field", "classBreakInfos":[{"minValue","maxValue","label","symbol"}]}`

`symbol` is either:
- `{"type":"simple-marker", "color":[r,g,b,a], "size", "style", "outline":{"color","width"}}` (`color` is `[0-255,0-255,0-255,0-255]`, alpha included)
- `{"type":"picture-marker", "url":"icons/<iconset>/<icon>.png", "usericonPath":"<uid>/<group>/<icon>.png", "width", "height", "opacity"}` — `url` is relative to the console (`/featurelink/featurelink-display-config/`), for browser preview only. **`usericonPath` is what ATAK needs** (see below) — `null` if the iconset has no known ATAK iconset UID (currently only `TAK-UserIcons`, ATAK's built-in team-role icons, isn't a custom-iconset package).

### `usericonPath` / `up` — applying the icon on the ATAK side

Every icon reference in every payload (`up` in the `sym` object, `usericonPath` in
the Esri-style `symbol` object) is a ready-to-use value for CoT's
`<usericon iconsetpath="...">` attribute — exactly the string ATAK's icon
resolver needs, already fully resolved server-side (iconset UID + exact
group folder + filename) so nothing on the plugin side has to know about
iconsets, UIDs, or groups at all:

```
<uid>/<group>/<filename>
```

e.g. `34ae1613-9645-4222-a9d2-e5f243dea2865/Military/A10.png`. Applying it
to a marker (see `IconDropper.java` in `CivilAirPatrol-Field-PluginV2` for
a working reference) is just:

```java
CotDetail userIcon = new CotDetail("usericon");
userIcon.setAttribute("iconsetpath", up);   // or symbol.usericonPath
detail.addChild(userIcon);
```

The UID-per-iconset and filename-per-group data this is resolved from
lives in `featurelink_displayconfig_assets/icons/manifest.json`, in each
iconset's `uid`, `defaultGroup`, and `groups` (`{filename: group}`) keys —
generated from the actual `iconset.xml` + folder layout of each bundled
iconset. `null` means either the iconset field/icon wasn't set, or (for
`TAK-UserIcons`) there's no ATAK iconset UID to resolve against — those
must still be handled as ATAK's built-in team-role icons, not a custom
iconset.

### External dependencies

The configurator loads two libraries from public CDNs at runtime: SheetJS
(`cdn.sheetjs.com`, for `.xlsx` uploads) and the `qrcode` npm package
(`cdn.jsdelivr.net`, for QR generation — pinned to **1.4.4**, not latest:
1.5.x's `build/qrcode.min.js` is an unbundled CommonJS shim that throws
`QRCode is not defined` when loaded directly via `<script>`; 1.4.4's is a
real standalone UMD bundle). Both need the browser viewing this page to
have outbound internet access — there's no offline/vendored fallback yet.

### Iconsets

Ten iconsets are bundled under `featurelink_displayconfig_assets/icons/`:
Default, Generic Icons, Responder Icons, FalconView, OSM, Google, Public
Safety Air, GeoOps, FEMA Icons, Incident Management Icons, and
TAK-UserIcons.

## Install

On the infra-TAK console host, as root, from a checkout of this module
directory (e.g. the `TAK-PluginSuite-FeatureLink/Infra-TAK` folder):

```bash
sudo bash install.sh
```

This:
1. If this checkout tracks a git remote, pulls the latest first (harmless
   no-op when run from inside the monorepo checkout).
2. Copies `featurelink_displayconfig.py` and
   `featurelink_displayconfig_assets/` (the page + bundled iconsets) into
   the detected infra-TAK install directory.
3. Patches `app.py` (idempotently — safe to re-run; migrates any pre-v1.2.0
   patches to the current route/icon first) to:
   - register the module's routes at startup, following the same
     `register_routes(app, login_required)` convention infra-TAK already
     uses for its other modules
   - add the **FeatureLink** link (sorted last) to the sidebar
   - add a module card for it to the console home page (`detect_modules()`),
     also sorted last
4. Restarts `takwerx-console` so the link and card show up immediately.

## Updating

Re-run `install.sh` — it re-syncs the page and iconsets, re-patches `app.py`
if needed (idempotent), and restarts the console:

```bash
sudo bash install.sh
```

## Uninstall

```bash
sudo bash uninstall.sh
```

Removes the sidebar link, home page module card, and this module's route
registration from `app.py`, deletes `featurelink_displayconfig.py` and
`featurelink_displayconfig_assets/` from the console, and restarts
`takwerx-console`. Safe to run even if the module isn't installed. Saved
dataset configs under `CONFIG_DIR/featurelink_displayconfig/datasets/` are
left in place (they're user data, not console integration) — delete that
directory by hand if you want them gone too.

## Files

- `featurelink_displayconfig.py` — Flask routes serving the `/featurelink`
  hub, the configurator page and its API, and the icon assets; registered
  into infra-TAK's `app.py` at startup.
- `featurelink_displayconfig_assets/index.html` — the configurator itself
  (adds Save + saved-dataset loading + the Saved Dataset Link QR mode on
  top of the original standalone app).
- `featurelink_displayconfig_assets/icons/` — the ten bundled iconsets.
- `install.sh` — install/update entry point (see above).
- `uninstall.sh` — removes the module's console integration (see above).

## Migrating from the standalone GitHub Pages app

The old standalone repo (`FeatureLink-DisplayConfig`,
`jpat-12.github.io/FeatureLink-DisplayConfig`) is retired in favor of this
module — this is now the canonical home for the tool. Display configs
exported from either version are interchangeable (same JSON format); there's
nothing to migrate besides where you access the tool from.
