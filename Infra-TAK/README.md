# Infra-TAK Module — FeatureLink Display Configurator

**Current version: 1.0.0** — `MODULE_VERSION` in `featurelink_displayconfig.py`
is the single source of truth; `install.sh` reads it back out with `grep`
after every sync. No separate CHANGELOG, the commit log is the changelog.

Adds a **FeatureLink Display Config** link to the sidebar of an
[infra-TAK](https://github.com/jpat-12/infra-TAK) console. It's the same
display configurator that used to live at
`jpat-12.github.io/FeatureLink-DisplayConfig` (now retired — see below),
served from the console instead of GitHub Pages.

## What it does

A self-contained, client-side web app for configuring display settings for
FeatureLayer exports — symbology, labels, popups, and layer properties —
with a live QR code export. Everything runs in the browser; this module just
serves the static page and its bundled iconsets from the console, gated
behind the console login like every other module page. There is no
server-side processing, no settings key, and nothing to configure at
install time.

- **Upload data** — CSV, TSV, JSON (ArcGIS FeatureLayer query response),
  GeoJSON, Excel (.xlsx)
- **Symbology** — Simple marker, Unique Values, Class Breaks (Graduated),
  Icon Symbol (10 bundled iconsets), Rule-Based (If/Then conditions)
- **Labels** — Field, font size, color, bold/italic, halo
- **Popups** — Title field, visible fields & aliases, custom HTML template
- **Layer properties** — Name, opacity, scale visibility
- **Export** — Download a display config JSON ready for use with the
  FeatureLink ATAK plugin
- **QR Code** — Share your config instantly via QR scan (compact or full
  payload)

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
3. Patches `app.py` (idempotently — safe to re-run) to register the
   module's routes at startup and add the **FeatureLink Display Config**
   link to the sidebar, following the same `register_routes(app,
   login_required)` convention infra-TAK already uses for its other
   modules.
4. Restarts `takwerx-console` so the link shows up immediately.

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

Removes the sidebar link and this module's route registration from
`app.py`, deletes `featurelink_displayconfig.py` and
`featurelink_displayconfig_assets/` from the console, and restarts
`takwerx-console`. Safe to run even if the module isn't installed.

## Files

- `featurelink_displayconfig.py` — Flask routes serving the configurator
  page and its icon assets, registered into infra-TAK's `app.py` at
  startup.
- `featurelink_displayconfig_assets/index.html` — the configurator itself
  (unchanged from the standalone app).
- `featurelink_displayconfig_assets/icons/` — the ten bundled iconsets.
- `install.sh` — install/update entry point (see above).
- `uninstall.sh` — removes the module's console integration (see above).

## Migrating from the standalone GitHub Pages app

The old standalone repo (`FeatureLink-DisplayConfig`,
`jpat-12.github.io/FeatureLink-DisplayConfig`) is retired in favor of this
module — this is now the canonical home for the tool. Display configs
exported from either version are interchangeable (same JSON format); there's
nothing to migrate besides where you access the tool from.
