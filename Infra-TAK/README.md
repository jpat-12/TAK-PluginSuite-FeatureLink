# Infra-TAK Module — FeatureLink

**Current version: 1.2.0** — `MODULE_VERSION` in `featurelink_displayconfig.py`
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
  through the export format). **Copy link** copies the same URL the QR's
  Saved Dataset Link mode encodes. **Delete** removes it.

Uploaded files and saved configs are stored under
`CONFIG_DIR/featurelink_displayconfig/datasets/` on the console (one
directory per saved entry: `record.json` for the config + metadata, plus
`data.bin` for file-based sources). FeatureLayer-URL-based entries store just
the URL and re-fetch live data on open.

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
