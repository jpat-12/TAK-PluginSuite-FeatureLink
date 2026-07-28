<div align="center">

# <img src="assets/featurelink-icon.svg" width="72" height="72" align="center" alt=""> TAK-PluginSuite-FeatureLink <img src="assets/try-harder-badge.png" width="72" height="72" align="center" alt="Try Harder">

</div>

FeatureLink bridges TAK (ATAK, WinTAK) and Esri ArcGIS Feature Services -
pushing map items to hosted feature layers, tracking PLI history, and
managing private/public feature layers - plus an infra-TAK console module
for configuring how those layers display. This repo collects every
FeatureLink component in one place.

<div align="center">

<a href="#supported-modules"><img src="https://img.shields.io/badge/Supported%20Modules-14B8A6?style=flat&logoColor=000000" height="80" alt="Supported Modules"></a>&nbsp;&nbsp;<a href="#configuration--install"><img src="https://img.shields.io/badge/Configuration%20%26%20Install-7C3AED?style=flat&logoColor=000000" height="80" alt="Configuration & Install"></a>&nbsp;&nbsp;<a href="https://buymeacoffee.com/jpat"><img src="https://img.shields.io/badge/%E2%98%95_Buy_me_a_coffee-FFD166?style=flat&logoColor=000000" height="80" alt="Buy Me A Coffee"></a>

</div>

<!-- TODO: diagram/visual explaining how the FeatureLink pieces (config sources + plugins) fit together -->
<p align="center"><em>(visual - how the whole FeatureLink suite fits together - coming soon)</em></p>

## Supported Modules

| Folder | What it is | Status |
|---|---|---|
| [`ATAK5.6/`](ATAK5.6/) | FeatureLink ATAK plugin, built against ATAK-CIV 5.6.0 | Available - see [`ATAK5.6/README.md`](ATAK5.6/README.md) |
| `ATAK5.7/` | FeatureLink ATAK plugin, ATAK-CIV 5.7.x port | Planned |
| [`CloudTAK/`](CloudTAK/) | FeatureLink CloudTAK plugin | In development - see [`CloudTAK/README.md`](CloudTAK/README.md) |
| [`Infra-TAK/`](Infra-TAK/) | Display Configurator - infra-TAK console module for building FeatureLink display configs (symbology/labels/popups) with QR export | **Deprecated & not supported** - see [`Infra-TAK/README.md`](Infra-TAK/README.md) |
| [`TAKPortal/`](TAKPortal/) | FeatureLink Configs - TAK Portal module: full Display Configurator (ported from Infra-TAK) for admins, any logged-in field user browses/downloads the results | Available - see [`TAKPortal/README.md`](TAKPortal/README.md) |
| [`WinTAK5.6/FeatureLink/`](WinTAK5.6/FeatureLink/) | FeatureLink WinTAK plugin, WinTAK 5.6.x port | In development - see [`WinTAK5.6/FeatureLink/README.md`](WinTAK5.6/FeatureLink/README.md) |
| `WinTAK5.7/` | FeatureLink WinTAK plugin, WinTAK 5.7.x port | Planned |

## Configuration & Install

FeatureLink has two halves: a **config source** (where display configs, symbology,
and layer setup get built) and the **plugins** that consume them in the field. Start
with whichever config source your team already runs, then set up the plugin for
your platform below.

### Choose Your Configuration Type

There are several ways to get a feature layer onto your EUD:
Personal recommendation - TAK Portal Link & Map-Based Config are the easiest, especially for orgs that use Esri a lot.

| # | Method | How it works | Notes |
|---|---|---|---|
| 1 | **Sign into ArcGIS** | Sign into your ArcGIS Portal account in the plugin - every layer you own, public or private, shows up automatically on the **Layers** page. | Simplest option - no config building required. |
| 2 | **QR Code Config** | Build a styled config in TAK Portal or an Infra-TAK module, then scan the QR from **Layers > + Add Layer**. | Doesn't work for layers needing a large styling palette - the QR payload has a size limit. |
| 3 | **TAK Portal Link** | From TAK Portal's sidebar: **Onboarding > FeatureLink > Open in ATAK**. | Needs an admin to set up the FeatureLayer config once - after that it's the same for every field user. |
| 4 | **Map-Based Config** | Styling is read directly from a saved Web Map. | |
| 5 | **Single FeatureLayer Import** | Styling is read directly from the feature layer, or the user is prompted for it. | |

> <details>
> <summary><strong>TAK Portal vs. Infra-TAK - which config source should you use?</strong></summary>
>
> | | [TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) | [infra-TAK](https://github.com/jpat-12/infra-TAK) |
> |---|---|---|
> | **Who builds configs** | Any admin | Console admin only |
> | **Who can grab a config** | Any logged-in TAK Portal user, via their own session | Whoever has the console admin password |
> | **Login needed by field users** | Just their existing TAK Portal account | None - configs are pulled via QR/link, no separate account |
> | **Best for** | Teams already running TAK Portal - field users self-serve configs without bugging an admin | Teams running infra-TAK without TAK Portal, or who want config-building kept behind the console's admin gate |
>
> Both run the same Display Configurator under the hood - the only difference is
> who can reach it and how a field user gets the result onto their device. See
> [CONFIG-FORMAT.md](CONFIG-FORMAT.md) if you need the payload details.
>
> </details>

> <details>
> <summary><strong>TAK Portal Setup</strong></summary>
>
> Adds the full **FeatureLink Configs** Display Configurator (Administration) and
> a **FeatureLink** browse/download page (Onboarding) to an existing
> [TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) install (as
> deployed by infra-TAK, default `~/TAK-Portal`):
>
> ```bash
> git clone https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
> cd TAK-PluginSuite-FeatureLink/TAKPortal
> bash install.sh
> ```
>
> Patches TAK Portal's `server.js`, `permissions.registry.js`,
> `portalAuth.middleware.js`, and sidebar idempotently (safe to re-run,
> including after pulling a newer TAK Portal `main` from upstream) rather than
> forking it - see [`TAKPortal/README.md`](TAKPortal/README.md) for details,
> updating, and uninstalling.
>
> </details>

> <details>
> <summary><strong>InfraTAK Setup</strong></summary>
>
> Adds a **FeatureLink Display Config** link to an existing
> [infra-TAK](https://github.com/jpat-12/infra-TAK) console. On the infra-TAK
> console host, as root - this uses a sparse, partial clone so the console only
> pulls down `Infra-TAK/`, not the ATAK/WinTAK plugin folders (Android SDKs,
> gradle caches, keystores - several hundred MB of stuff a console box has no
> use for):
>
> ```bash
> git clone --no-checkout --depth 1 --filter=blob:none \
>   https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
> cd TAK-PluginSuite-FeatureLink
> git sparse-checkout set Infra-TAK
> git checkout
> cd Infra-TAK
> sudo bash install.sh
> ```
>
> This copies the module into the console's install directory, patches its
> `app.py` to register the module's routes and sidebar link, and restarts the
> console service. See [`Infra-TAK/README.md`](Infra-TAK/README.md) for
> details, updating, and uninstalling. To update later, `git pull` from
> `TAK-PluginSuite-FeatureLink/` (the sparse checkout is sticky) and re-run
> `install.sh`.
>
> This module replaces the old standalone `FeatureLink-DisplayConfig`
> GitHub Pages app, which is now deprecated in favor of running the same
> tool from the console.
>
> </details>

<details>
<summary><h3 style="display:inline">ATAK Plugin</h3></summary>

**[Download the latest release](https://github.com/jpat-12/TAK-PluginSuite-FeatureLink/releases)**

1. Side-load the APK onto your Android device running ATAK-CIV 5.6.0.
2. In ATAK, open **Settings > Manage Plugins** and enable **FeatureLink**.
3. **Sign into ArcGIS** - tap the account button in the FeatureLink header, enter
   your ArcGIS Portal URL, username, and password, and tap **Sign in with ArcGIS**.
   Your hosted feature layers populate the Layers tab automatically.
4. **Set up PLI** - on the PLI tab, use **Create New Layer** or **Join Existing
   Layer** (via QR scan) to set up a shared position layer, then enable
   **Auto-Send PLI** to start streaming your position to it.
5. **Import a config** - on the Layers tab, tap **Add Layer**, then either
   **Scan Config QR** or paste a Feature Service URL directly.
6. **Scan a QR code** - any scan entry point (Add Layer page, PLI layer URL
   field, PLI's Scan Config QR button) accepts all FeatureLink QR types and
   routes automatically to the right tab.

See [`ATAK5.6/README.md`](ATAK5.6/README.md) for the full QR code schema
reference and building from source.

</details>

<br>

<details>
<summary><h3 style="display:inline">CloudTAK Plugin</h3></summary>

In development. A browser-based Vue3/TypeScript port that runs inside the CloudTAK web UI
itself rather than as a native app - install/enable/disable lifecycle, layer browse/download
with display-config styling, PLI create/join/auto-send, and a "Send to Feature Layer" picker
(replacing ATAK's radial menu) are implemented. Sign-in uses ArcGIS username/password token
auth instead of OAuth (no app registration required), and QR scanning is replaced by
paste/upload config JSON, since a desktop browser has no camera-scan equivalent. UI mirrors
the ATAK plugin's tabs, section layout, and collapse behavior rather than being redesigned.
See [`CloudTAK/README.md`](CloudTAK/README.md) for the full architecture, known limitations,
and install instructions.

</details>

<br>

<details>
<summary><h3 style="display:inline">WinTAK Plugin</h3></summary>

In development. The WinTAK 5.6.x port (C#/.NET/WPF/MEF, `.wpk` package) is a scaffold + core
sync pass - ArcGIS OAuth2 PKCE sign-in, layer browse/download-as-CoT, and PLI auto-send are
implemented, with the 3-tab UI laid out to match the ATAK plugin. QR sharing, radial-menu
"send to layer", deep-link import, and display-config symbology mapping are deferred - see
[`WinTAK5.6/FeatureLink/README.md`](WinTAK5.6/FeatureLink/README.md) for what's included, what's
deferred, and build/deploy instructions. WinTAK 5.7.x port planned after 5.6.x stabilizes.

</details>

## Related

- [CONFIG-FORMAT.md](CONFIG-FORMAT.md) - the display-config/QR payload contract between the Display Configurator and the ATAK/WinTAK plugin
- [ATAK-Plugin_FeatureLink](https://github.com/jpat-12/ATAK-Plugin_FeatureLink) - original standalone repo for the ATAK plugin, **deprecated**
- [infra-TAK](https://github.com/jpat-12/infra-TAK) - the console the `Infra-TAK/` module installs into
- [TAK-Portal](https://github.com/AdventureSeeker423/TAK-Portal) - the portal the `TAKPortal/` module installs into

## Support

If this project is useful to you, consider [buying me a coffee](https://buymeacoffee.com/jpat).
