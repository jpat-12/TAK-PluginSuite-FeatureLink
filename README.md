# TAK-PluginSuite-FeatureLink

FeatureLink bridges TAK (ATAK, WinTAK) and Esri ArcGIS Feature Services —
pushing map items to hosted feature layers, tracking PLI history, and
managing private/public feature layers — plus an infra-TAK console module
for configuring how those layers display. This repo collects every
FeatureLink component in one place.

## Components

| Folder | What it is | Status |
|---|---|---|
| [`ATAK5.6/`](ATAK5.6/) | FeatureLink ATAK plugin, built against ATAK-CIV 5.6.0 | Available — see [`ATAK5.6/README.md`](ATAK5.6/README.md) |
| `ATAK5.7/` | FeatureLink ATAK plugin, ATAK-CIV 5.7.x port | Planned |
| [`Infra-TAK/`](Infra-TAK/) | Display Configurator — infra-TAK console module for building FeatureLink display configs (symbology/labels/popups) with QR export | Available — see [`Infra-TAK/README.md`](Infra-TAK/README.md) |
| [`TAKPortal/`](TAKPortal/) | FeatureLink Configs — TAK Portal module: admin preps display configs, any logged-in field user browses/downloads them | Available — see [`TAKPortal/README.md`](TAKPortal/README.md) |
| `WinTAK5.6/` | FeatureLink WinTAK plugin, WinTAK 5.6.x port | Planned |
| `WinTAK5.7/` | FeatureLink WinTAK plugin, WinTAK 5.7.x port | Planned |

## Install

### ATAK plugin (`ATAK5.6/`)

Build and sideload the plugin following [`ATAK5.6/README.md`](ATAK5.6/README.md#build)
— requires Android Studio, the ATAK-CIV 5.6.0 SDK, and the
`atak-gradle-takdev` plugin.

### Infra-TAK module (`Infra-TAK/`)

Adds a **FeatureLink Display Config** link to an existing
[infra-TAK](https://github.com/jpat-12/infra-TAK) console — a browser-based
tool for building display configs (symbology, labels, popups, layer
properties) for FeatureLink exports, with QR code sharing to the plugin.
On the infra-TAK console host, as root — this uses a sparse, partial clone
so the console only pulls down `Infra-TAK/`, not the ATAK/WinTAK plugin
folders (Android SDKs, gradle caches, keystores — several hundred MB of
stuff a console box has no use for):

```bash
git clone --no-checkout --depth 1 --filter=blob:none \
  https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
cd TAK-PluginSuite-FeatureLink
git sparse-checkout set Infra-TAK
git checkout
cd Infra-TAK
sudo bash install.sh
```

This copies the module into the console's install directory, patches its
`app.py` to register the module's routes and sidebar link, and restarts the
console service. See [`Infra-TAK/README.md`](Infra-TAK/README.md) for
details, updating, and uninstalling.

To update later, `git pull` from `TAK-PluginSuite-FeatureLink/` (the sparse
checkout is sticky — it won't suddenly pull in ATAK/WinTAK) and re-run
`install.sh`, or just let `install.sh` do the pull for you (see below).

This module replaces the old standalone `FeatureLink-DisplayConfig`
GitHub Pages app, which is now deprecated in favor of running the same
tool from the console.

### TAK Portal module (`TAKPortal/`)

Adds a **FeatureLink Configs** admin page (Administration) and a **FeatureLink**
browse/download page (Onboarding) to an existing
[TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) install (as
deployed by infra-TAK, default `~/TAK-Portal`). Lets an admin prep display
configs for field users to download, without needing the infra-TAK console's
admin password — any logged-in TAK Portal user can download a config through
their own browser session, then open it in the FeatureLink plugin:

```bash
git clone https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
cd TAK-PluginSuite-FeatureLink/TAKPortal
bash install.sh
```

Patches TAK Portal's `server.js`, `permissions.registry.js`,
`portalAuth.middleware.js`, and sidebar idempotently (safe to re-run,
including after pulling a newer TAK Portal `main` from upstream) rather than
forking it — see [`TAKPortal/README.md`](TAKPortal/README.md) for why there's
no separate login/token for the plugin, plus updating and uninstalling.

### WinTAK plugins (`WinTAK5.6/`, `WinTAK5.7/`)

Not yet published — placeholders for upcoming ports.

## Related

- [ATAK-Plugin_FeatureLink](https://github.com/jpat-12/ATAK-Plugin_FeatureLink) — original standalone repo for the ATAK plugin
- [infra-TAK](https://github.com/jpat-12/infra-TAK) — the console the `Infra-TAK/` module installs into
- [TAK-Portal](https://github.com/AdventureSeeker423/TAK-Portal) — the portal the `TAKPortal/` module installs into

## Support

If this project is useful to you, consider [buying me a coffee](https://buymeacoffee.com/jpat).
