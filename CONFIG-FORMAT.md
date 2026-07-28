# FeatureLink Display Config Format

This is the payload contract between the **Display Configurator** (the same
tool running at `Infra-TAK/featurelink_displayconfig_assets/index.html` and
`TAKPortal/assets/featurelink-configurator/index.html` — identical code,
ported) and the **FeatureLink ATAK/WinTAK plugin** (`ATAK5.6/`). If you change
what one side produces, check this document against the other side's parser
before shipping — the two are hand-synchronized, nothing enforces it at
build time.

**Producer** (the configurator, `index.html`): `buildQRPayload()`,
`buildUrlConfigExport()`, `buildExportPayload()`, `buildSymbologyExport()`.

**Consumer** (the plugin): `QrHelper.parse()` / `QrHelper.parseDisplayConfig()`
in `QrHelper.java`, `DisplayConfig.fromJson()` / `DisplayConfig.fromJsonV3()`
in `DisplayConfig.java`, applied in `FeatureLinkDropDownReceiver.
applyScannedPayload()` → `applyScannedDisplayConfig()`, and (for CoT field
mapping) `ArcGISRestClient.downloadLayerAsCoT()`.

There are four distinct payload shapes, plus one cross-cutting piece (CoT
field mapping) that only two of them carry.

## Mode 1 — compact, display-only (`v:1`)

Produced by the QR panel's **Compact** option. No `url` field — it's meant to
be **applied to a layer already open in ATAK**, not to add one.

```jsonc
{
  "v": 1,
  "src": "my-data.csv",       // original filename, informational only
  "layer": { "name": "...", "opacity": 1.0, "visible": true },
  "sym": { /* compact symbology — see "Compact sym schema" below */ },
  "lbl": { /* compact labels, or omitted if disabled */ },
  "popup": { "t": "titleField", "flds": [...] }
}
```

**How the plugin applies it** (`DisplayConfig.fromJson()`, then
`applyScannedDisplayConfig()`'s `else` branch): since there's no `url`, the
plugin can't know which layer this belongs to. It lists every layer currently
in ATAK and shows an **"Apply display config to:"** picker — whichever the
user taps gets `layerDisplayConfigs.put(layer.url, config)` and is
re-downloaded with the new styling. If no layers exist yet, the user sees
"Add a layer first, then scan the display config again."

## Mode 2 — compact, url + config (`v:2`)

Produced by the QR panel's **URL + Config** option (`buildUrlConfigExport()`
in `index.html`) — only available once data's been loaded from a live
FeatureLayer URL (`state.featureLayerUrl` set, not a file upload). This is
also what TAK Portal's `featurelinkDatasets.service.js` stores as
`exported_config` and serves for field-user Download / **Open in ATAK** — see
"Which mode TAK Portal uses" below for why.

```jsonc
{
  "v": 2,
  "url": "https://.../FeatureServer/0",
  "layer": { "name": "...", "opacity": 1.0, "visible": true },
  "sym": { /* compact symbology — see below */ },
  "lbl": { /* compact labels, or omitted */ },
  "popup": { "t": "titleField", "flds": [["field","alias"], "plainField", ...] },
  "cm": { "uf": ["AssetID"], "tf": ["SymbolCode","Type"], "cf": ["UnitName"], "rf": ["Notes"] },
  // "cm" (CoT field mapping) is entirely optional — omitted whenever every
  // mapping is left on "(default)" in the CoT Mapping tab. Each key is a
  // candidate-column array, not a single field — see below.
  "freq": { "iv": 180, "u": "s" }
  // "freq" (auto-refresh interval) is entirely optional — omitted when the
  // Update Frequency tab is set to 0. "iv" is a plain seconds count (default
  // 180), "u" is always "s" from the configurator now — see "Update
  // frequency" below for why the field still exists.
}
```

**How the plugin applies it** (`DisplayConfig.fromJson()`, `url` non-empty
branch): fetches the layer info, adds it to the layer list if not already
present, stores `layerDisplayConfigs.put(config.url, config)`, and downloads
it immediately — layer + styling in one shot, no picker.

### Compact `sym` schema (used by Mode 1 and Mode 2)

| Key | Meaning |
|---|---|
| `t` | Symbology type: `s` (simple), `uv` (unique value), `rb` (rule-based), `cb` (class breaks), `ic` (single icon), `adv` (advanced: per-value + rules, shape or icon) |
| `c` | Fill/default color, `#rrggbb` |
| `oc` | Outline color |
| `sz` | Size, px |
| `sh` | Shape: `circle`\|`square`\|`diamond`\|`triangle`\|`cross`\|`x` |
| `op` | Opacity, 0–1 |
| `f` | Field name driving `uv`/`rb`/`cb`/`adv` |
| `uv` | (type `uv`) `[{"v":value,"c":color}, ...]` |
| `rules` / `r` | (type `rb` uses `rules`, `adv` uses `r`) `[{"f":field,"o":op,"v":value,"c":color,"sh":shape}, ...]`. `o` is one of `= ≠ contains "starts with" "is empty" "is not empty" > < >= <=` |
| `dc` | (type `rb`) default color when no rule matches |
| `cb` | (type `cb`) `[{"mn":min,"mx":max,"c":color}, ...]` |
| `is`, `ic` | (type `ic`/`adv` icon entries) iconset name, icon filename. `is` may name a custom uploaded icon set (see "Custom icon sets" below), not just a bundled one — the plugin can't tell the difference from this field alone, only from whether `up` is present. |
| `up` | Server-resolved `"<iconset-uid>/<group>/<filename>"` — the exact ATAK `IconsetPath` value, resolved by the configurator against `icons/manifest.json` (`resolveUsericonPath()` in `index.html`). Prefer this over `is`/`ic` — see `DisplayConfig.resolvedOrLegacy()`. |
| `vs` | (type `adv`) per-value entries: `[{"v":value,"c":color,"sh":shape,"m":"shape"\|"icon","is":iconset,"ic":icon,"up":usericonPath}, ...]` |

Compact `lbl`: `{"f":field, "sz":sizeSp, "c":color, "b":bold, "i":italic}`.

Compact `popup`: `{"t":titleField, "flds":[fieldName_or_[name,alias], ...]}`.

### CoT field mapping (`cm` / `cotMapping`)

By default `ArcGISRestClient.downloadLayerAsCoT()` reads fixed attribute
column names off each downloaded feature:

| CoT part | Default column | Fallback if missing/blank |
|---|---|---|
| UID | `uid` | `FL-<index>-<timestamp>` |
| CoT type | `cot_type` | `a-f-G` |
| Callsign | `tak_callsign` | `Feature-<index>` |
| Remarks | `tak_remarks` | (empty) |

A FeatureLayer whose schema doesn't happen to use those exact names — a
pre-existing agency layer you don't want to rename columns on — can map its
own columns instead, via the configurator's **CoT Mapping** tab. Each of the
four parts (UID, CoT type, callsign, remarks) accepts **multiple** candidate
columns via checkboxes, tried in the order checked — first non-blank value
on a feature wins, and if none of the checked columns have a value (or none
are checked) that part falls back to its default column/value from the table
above. This is a fallback chain, not a merge — only one candidate's value is
ever used per feature.

Compact key names (Mode 2, `cm`): `uf` (UID fields), `tf` (type fields), `cf`
(callsign fields), `rf` (remarks fields) — each an array, omitted entirely if
empty. Mode 3 (`cotMapping`) spells the same four out as
`uidFields`/`typeFields`/`callsignFields`/`remarksFields`, also arrays. Both
are parsed by the same `DisplayConfig.CotMapping` class in
`DisplayConfig.java` (`fromJson()` for `cm`, `fromJsonV3()` for `cotMapping`),
whose `fieldList()` helper also accepts a bare string for backward
compatibility with configs saved before multi-select existed. Mode 1 has no
`url` to redownload from, so field mapping doesn't apply there.

### Update frequency (`freq` / `updateFrequency`)

Set in the configurator's **4. Update Frequency** tab, and defaults to 180
seconds for a new config rather than off. This is only the **initial**
auto-refresh interval for a device adding the layer for the first time —
see `ArcGISLayer.recurrenceInterval`/`recurrenceUnit` (which likewise
default to 180/`"s"` for any brand-new layer, portal-sourced or not) and
`FeatureLinkDropDownReceiver.applyScannedDisplayConfig()`'s url branch,
which sets it on the freshly-created `ArcGISLayer` only inside the
`!alreadyAdded` guard. Once a layer exists on a device, the end user can
change the interval themselves via the "Refresh every ___ seconds" field
next to that layer in the plugin's Layers tab (`LayerListAdapter`) — that's
a per-device preference persisted to this device's own SharedPreferences,
and re-scanning or re-opening this same config on that device never
overwrites it again.

Compact key names (Mode 2, `freq`): `iv` (interval value in seconds; `0` =
off) and `u` (interval unit — always `"s"` from the configurator and the
plugin's own UI now, though `"min"`/`"hr"` still parse correctly for a layer
or config saved before this changed to seconds-only — see
`ArcGISLayer.recurrenceMillis()`). Omitted entirely when off. Mode 3
(`updateFrequency`) spells it out as `{"enabled":bool, "intervalValue":n,
"intervalUnit":"..."}`. Both are parsed in
`DisplayConfig.fromJson()`/`fromJsonV3()` into the same
`freqInterval`/`freqUnit` fields. Mode 1 has no `url` to apply a default to
(same reasoning as `cm`), so it doesn't carry `freq`.

### Custom icon sets

Uploaded via the Symbology tab's icon picker (Display Configurator only, not
in Infra-TAK's copy), stored server-side by `featurelinkCustomIcons.service.js`
at `data/featurelink-configs/custom-icons/<setName>/`, listed/served by
`featurelinkCustomIcons.routes.js` at `/api/featurelink/admin/custom-icons`.
They merge into the same `iconManifest.iconsets` array the bundled sets use
(marked `custom: true`), so they show up in both the iconset dropdown and
global icon search identically. `iconUrl()` in `index.html` routes a custom
set's images to that API route instead of the bundled `ICONS_BASE` static
path.

**Known limitation:** custom sets have no ATAK iconset UID, so
`resolveUsericonPath()` can't produce an `up` value for one — a config
referencing a custom icon (`is`/`ic` only, no `up`) has nothing for
`DisplayConfig.resolvedOrLegacy()` to resolve on the plugin side, and ATAK
falls back to its default marker styling instead of rendering the uploaded
image. Custom icons are fine for previewing/organizing in the configurator
today; making them actually render as ATAK markers would need the plugin to
fetch and cache arbitrary bitmaps by URL, which hasn't been built yet.

When an icon symbol *is* resolvable (bundled set, `up` present), the marker's
color is left white rather than tinted with the symbol's configured color —
`FeatureLinkDropDownReceiver`'s CoT-application code only applies
`resolveColor()`'s tint when no iconset path was set on the marker, so a
picked icon always renders in its own native colors instead of behind a
colored circle.

## Mode 3 — full export (`_version`/`_v`)

Produced by the **"⬇ Export Config JSON"** button (`buildExportPayload()` →
`exportConfig()`) — a standalone manual download, independent of TAK Portal's
save flow. Uses real [Esri REST renderer JSON](https://developers.arcgis.com/web-map-specification/objects/renderer/)
for symbology instead of the compact `sym` shape, for interop with tools that
expect genuine Esri renderer objects.

```jsonc
{
  "_version": "1.1",
  "_tool": "FeatureLink Display Configurator",
  "_generated": "2026-...",
  "_source": "my-data.csv",
  "featureLayerUrl": "https://.../FeatureServer/0",
  "_rowCount": 1234,
  "fields": [{"name":"...", "type":"..."}],
  "layer": { "name": "...", "opacity": 1.0, "visible": true },
  "symbology": { /* Esri renderer JSON — see below */ },
  "labels": { "enabled": true, "field": "...", "fontSize": 12, "color": "#fff", "haloColor": "#000", "haloSize": 1, "bold": false, "italic": false },
  "popup": { "enabled": true, "titleField": "...", "fields": [{"field":"...","alias":"..."}], "customHtml": false, "htmlTemplate": null },
  "cotMapping": { "uidFields": [], "typeFields": [], "callsignFields": [], "remarksFields": [] },
  "updateFrequency": { "enabled": true, "intervalValue": 180, "intervalUnit": "s" }
}
```

Parsed by `DisplayConfig.fromJsonV3()` / `SymConfig.fromEsriRenderer()`.
`symbology.type` must be one of:

- `"simple"` / `"simpleRenderer"` (also the default/fallback case) — reads
  `symbology.symbol`. A picture marker (`symbol.type === "esriPMS"`) becomes
  an icon symbol (uses `symbol.usericonPath`, falling back to `symbol.url`);
  anything else is read as an SMS-style symbol (`symbol.color` as a Esri
  `[r,g,b,a]` array, `symbol.outline.color`, `symbol.size`, and
  `symbol.style` — **must** be a real Esri SMS constant:
  `esriSMSCircle`/`esriSMSSquare`/`esriSMSDiamond`/`esriSMSTriangle`/
  `esriSMSCross`/`esriSMSX`; anything else silently falls back to circle).
- `"uniqueValue"` / `"uniqueValueRenderer"` — `field` (or legacy `field1`),
  `uniqueValueInfos: [{value, symbol}]`, `defaultSymbol`.
- `"classBreaks"` / `"classBreaksRenderer"` — `field`,
  `classBreakInfos: [{classMinValue, classMaxValue, symbol}]` (note the
  `class` prefix — a bare `minValue`/`maxValue` is silently ignored),
  `defaultSymbol`.
- `"rule-based"` — **not a real Esri renderer type.** Esri's REST API has no
  native concept of arbitrary field/operator/value rules, so this is
  FeatureLink's own extension for Rule-Based and Advanced-with-rules
  symbology, recognized by a matching case in `SymConfig.fromEsriRenderer()`.
  `rules: [{field, op, value, symbol, where, label}]` — the plugin reads
  `field`/`op`/`value`/`symbol` directly and ignores `where`/`label` (those
  exist only so a *different*, genuinely Esri-REST-compliant consumer could
  still make sense of the payload via `where`). `defaultSymbol` for when no
  rule matches.

Colors throughout `symbology` are Esri-style `[r, g, b, a]` arrays (0–255
per channel, alpha dropped — the plugin renders opaque RGB only), not CSS
hex/rgba strings.

## Mode 4 — Saved Dataset Link

A bare `https://` URL, e.g. `https://portal.example.com/featurelink-configs/
configurator?load=<id>` — produced by the QR panel's **Saved Dataset Link**
option (only enabled after Save) and the hub page's **QR**/**Copy link**
buttons.

This is **for a browser, not the plugin**: scanning it with an ordinary
camera app opens the configurator with that dataset preloaded, so a person
can review or keep tweaking it. It is not meant to be fetched directly by
`QrHelper.fetchSavedDatasetLink()` — TAK Portal's `/featurelink-configs/
configurator` page sits behind Authentik forward_auth and TAK Portal's own
admin permission gate, so a raw HTTP GET from the plugin (no session cookie)
would hit a login redirect, not JSON, even though the plugin's QR-scan code
path does attempt exactly that fetch for any bare URL it scans. In practice:
don't scan a Saved Dataset Link with ATAK's in-app QR scanner — only with a
phone's regular camera/browser.

## Which mode TAK Portal actually uses

`featurelinkDatasets.service.js`'s `exported_config` (served for field-user
Download and the **Open in ATAK** deep link) is built from
**`buildUrlConfigExport()` — Mode 2**, not Mode 3's `buildExportPayload()`,
deliberately. Mode 2 maps directly onto `DisplayConfig.fromJson()` with no
Esri-renderer translation layer, so it round-trips correctly for every
symbology type without exception. Mode 3's translation (`buildSymbologyExport()`)
has real subtlety in it (see "Mode 3" above) — it's fixed as of this writing,
but it's still one more translation step than Mode 2, kept alive only because
the standalone "Export Config JSON" button is a useful interchange format for
tools that actually want genuine Esri renderer JSON.

## Keeping the two sides aligned

When adding a new symbology/label/popup/mapping feature to the configurator:

1. Decide which mode(s) need it. Compact (`sym`/`lbl`/`popup`/`cm`) is the
   one that matters for TAK Portal's actual downloads — Mode 3 is optional
   polish for the standalone export button.
2. Update the JS producer (`buildUrlConfigExport()` and/or
   `buildSymbologyExport()`), using the exact key names in the tables above.
3. Update the matching Java parser (`SymConfig.fromJson()` / `fromEsriRenderer()`,
   `LabelConfig`, `PopupConfig`, `CotMapping` in `DisplayConfig.java`) to read
   those same keys.
4. Update this document.
