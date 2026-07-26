<div align="center">

# <img src="assets/featurelink-icon.svg" width="72" height="72" align="center" alt=""> TAK-PluginSuite-FeatureLink <img src="assets/try-harder-badge.png" width="72" height="72" align="center" alt="Try Harder">

</div>

FeatureLink bridges TAK (ATAK, WinTAK) and Esri ArcGIS Feature Services —
pushing map items to hosted feature layers, tracking PLI history, and
managing private/public feature layers — plus an infra-TAK console module
for configuring how those layers display. This repo collects every
FeatureLink component in one place.

<a href="https://buymeacoffee.com/jpat"><img src="https://img.buymeacoffee.com/button-api/?text=Buy%20me%20a%20coffee&emoji=&slug=jpat&button_colour=FFDD00&font_colour=000000&font_family=Cookie&outline_colour=000000&coffee_colour=ffffff" width="150" alt="Buy Me A Coffee"></a>

<!-- TODO: diagram/visual explaining how the FeatureLink pieces (config sources + plugins) fit together -->
<p align="center"><em>(visual — how the whole FeatureLink suite fits together — coming soon)</em></p>

## Supported Modules

| Folder | What it is | Status |
|---|---|---|
| [`ATAK5.6/`](ATAK5.6/) | FeatureLink ATAK plugin, built against ATAK-CIV 5.6.0 | Available — see [`ATAK5.6/README.md`](ATAK5.6/README.md) |
| `ATAK5.7/` | FeatureLink ATAK plugin, ATAK-CIV 5.7.x port | Planned |
| `CloudTAK/` | FeatureLink CloudTAK plugin | Planned |
| [`Infra-TAK/`](Infra-TAK/) | Display Configurator — infra-TAK console module for building FeatureLink display configs (symbology/labels/popups) with QR export | **Deprecated & not supported** — see [`Infra-TAK/README.md`](Infra-TAK/README.md) |
| [`TAKPortal/`](TAKPortal/) | FeatureLink Configs — TAK Portal module: full Display Configurator (ported from Infra-TAK) for admins, any logged-in field user browses/downloads the results | Available — see [`TAKPortal/README.md`](TAKPortal/README.md) |
| `WinTAK5.6/` | FeatureLink WinTAK plugin, WinTAK 5.6.x port | Planned |
| `WinTAK5.7/` | FeatureLink WinTAK plugin, WinTAK 5.7.x port | Planned |

## Configuration & Install

FeatureLink has two halves: a **config source** (where display configs, symbology,
and layer setup get built) and the **plugins** that consume them in the field. Start
with whichever config source your team already runs, then set up the plugin for
your platform below.

### Choose Your Configuration Type

<details>
<summary><strong>TAK Portal vs. Infra-TAK — which config source should you use?</strong></summary>

| | [TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) | [infra-TAK](https://github.com/jpat-12/infra-TAK) |
|---|---|---|
| **Who builds configs** | Any admin | Console admin only |
| **Who can grab a config** | Any logged-in TAK Portal user, via their own session | Whoever has the console admin password |
| **Login needed by field users** | Just their existing TAK Portal account | None — configs are pulled via QR/link, no separate account |
| **Best for** | Teams already running TAK Portal — field users self-serve configs without bugging an admin | Teams running infra-TAK without TAK Portal, or who want config-building kept behind the console's admin gate |

Both run the same Display Configurator under the hood — the only difference is
who can reach it and how a field user gets the result onto their device. See
[CONFIG-FORMAT.md](CONFIG-FORMAT.md) if you need the payload details.

</details>

<details>
<summary><strong>TAK Portal Setup</strong></summary>

Adds the full **FeatureLink Configs** Display Configurator (Administration) and
a **FeatureLink** browse/download page (Onboarding) to an existing
[TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) install (as
deployed by infra-TAK, default `~/TAK-Portal`):

```bash
git clone https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
cd TAK-PluginSuite-FeatureLink/TAKPortal
bash install.sh
```

Patches TAK Portal's `server.js`, `permissions.registry.js`,
`portalAuth.middleware.js`, and sidebar idempotently (safe to re-run,
including after pulling a newer TAK Portal `main` from upstream) rather than
forking it — see [`TAKPortal/README.md`](TAKPortal/README.md) for details,
updating, and uninstalling.

</details>

<details>
<summary><strong>InfraTAK Setup</strong></summary>

Adds a **FeatureLink Display Config** link to an existing
[infra-TAK](https://github.com/jpat-12/infra-TAK) console. On the infra-TAK
console host, as root — this uses a sparse, partial clone so the console only
pulls down `Infra-TAK/`, not the ATAK/WinTAK plugin folders (Android SDKs,
gradle caches, keystores — several hundred MB of stuff a console box has no
use for):

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
details, updating, and uninstalling. To update later, `git pull` from
`TAK-PluginSuite-FeatureLink/` (the sparse checkout is sticky) and re-run
`install.sh`.

This module replaces the old standalone `FeatureLink-DisplayConfig`
GitHub Pages app, which is now deprecated in favor of running the same
tool from the console.

</details>

### ATAK Plugin

**[Download the latest release](https://github.com/jpat-12/TAK-PluginSuite-FeatureLink/releases)**

1. Side-load the APK onto your Android device running ATAK-CIV 5.6.0.
2. In ATAK, open **Settings > Manage Plugins** and enable **FeatureLink**.
3. **Sign into ArcGIS** — tap the account button in the FeatureLink header, enter
   your ArcGIS Portal URL, username, and password, and tap **Sign in with ArcGIS**.
   Your hosted feature layers populate the Layers tab automatically.
4. **Set up PLI** — on the PLI tab, use **Create New Layer** or **Join Existing
   Layer** (via QR scan) to set up a shared position layer, then enable
   **Auto-Send PLI** to start streaming your position to it.
5. **Import a config** — on the Layers tab, tap **Add Layer**, then either
   **Scan Config QR** or paste a Feature Service URL directly.
6. **Scan a QR code** — any scan entry point (Add Layer page, PLI layer URL
   field, PLI's Scan Config QR button) accepts all FeatureLink QR types and
   routes automatically to the right tab.

See [`ATAK5.6/README.md`](ATAK5.6/README.md) for the full QR code schema
reference and building from source.

### CloudTAK Plugin

In development.

### WinTAK Plugin

In development — planned ports for WinTAK 5.6.x and 5.7.x.

## Related

- [CONFIG-FORMAT.md](CONFIG-FORMAT.md) — the display-config/QR payload contract between the Display Configurator and the ATAK/WinTAK plugin
- [ATAK-Plugin_FeatureLink](https://github.com/jpat-12/ATAK-Plugin_FeatureLink) — original standalone repo for the ATAK plugin
- [infra-TAK](https://github.com/jpat-12/infra-TAK) — the console the `Infra-TAK/` module installs into
- [TAK-Portal](https://github.com/AdventureSeeker423/TAK-Portal) — the portal the `TAKPortal/` module installs into

## Support

If this project is useful to you, consider [buying me a coffee](https://buymeacoffee.com/jpat).
