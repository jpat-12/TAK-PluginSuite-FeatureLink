# TAK Portal Module — FeatureLink Configs

Adds a **FeatureLink** entry under **Onboarding** and a **FeatureLink Configs**
entry under **Administration** to an existing
[TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) install (as
deployed by [infra-TAK](https://github.com/jpat-12/infra-TAK), default
`~/TAK-Portal`, Docker Compose).

**Administration → FeatureLink Configs** is the full FeatureLink Display
Configurator — the same tool infra-TAK runs at `Infra-TAK/`
(`featurelink_displayconfig_assets/index.html`), ported into TAK Portal as its
own copy rather than a smaller hand-built form. Upload a dataset or point at a
live ArcGIS FeatureLayer URL, then set up symbology, labels, popups, and layer
properties with a live preview, and Save.

**Onboarding → FeatureLink** is where field users go: anyone already logged
into TAK Portal can browse the saved configs and download or **Open in ATAK**
— no separate login or token for the plugin. The user downloads the config
through their own already-Authentik-gated browser session (same as clicking
any other download link in the portal), then the plugin either opens it via
the `featurelink://import` deep link or the user imports the downloaded file
manually, same as any other exported FeatureLink display config.

## Why a module instead of a fork

TAK Portal is deployed by cloning straight from upstream
(`AdventureSeeker423/TAK-Portal`) and rebuilt in place with
`docker compose up -d --build` / `./takportal update`. A fork would need a
manual merge against upstream on every update; this module instead patches a
stock checkout idempotently (same convention as
[`InfraTAK-Module-MigrateAuthentik`](https://github.com/jpat-12/InfraTAK-Module-MigrateAuthentik))
so a plain `git pull` from upstream + re-running `install.sh` keeps both the
module and upstream's own updates intact.

## Why there's no plugin-side token

TAK Portal sits entirely behind an Authentik `forward_single` proxy provider
on `takportal.<fqdn>` (same pattern as Node-RED) — Caddy calls out to
Authentik's outpost and only forwards a request once it carries a valid
session cookie. That's a browser mechanism: a non-interactive HTTP client
(the plugin polling an API on its own) has no cookie jar and can't complete
Authentik's login/MFA redirect, so it would never even reach TAK Portal's
Node app, regardless of any auth scheme built into the app itself. Rather
than carve a hole in that Authentik gate for a machine client, this module
sidesteps the problem: the **person** downloads the config through their
own browser session (which already works fine — that's what forward_auth is
for), and the plugin only ever deals with a file the user handed it, not a
network call it has to authenticate on its own.

## What it adds

- **`assets/featurelink-configurator/`** — the ported Display Configurator:
  `index.html` (framework-free, same as infra-TAK's copy) + the bundled
  iconsets. Only three hardcoded URL references were changed (`ICONS_BASE`,
  the QR/copy-link builder, and the "Saved Configs" back-link) to match where
  this module mounts it — no other logic touched.
- **`services/featurelinkDatasets.service.js`** — dataset persistence, ported
  1:1 from `Infra-TAK/featurelink_displayconfig.py` (same record shape,
  same `datasets/<id>/record.json` + `data.bin` storage layout — under
  `data/featurelink-configs/`, inside the `tak_portal_data` volume so it
  survives rebuilds). Adds one field beyond the original: `exported_config`,
  the Mode 3 plugin-ready JSON the configurator's "Export Config JSON" button
  already builds client-side — saving now sends that alongside the internal
  state, so TAK Portal never has to re-derive Esri-renderer-to-plugin-schema
  logic on the server side.
- **`routes/featurelinkDatasetsAdmin.routes.js`** — admin CRUD backing the
  configurator (list/save/load/delete/raw-file), mirroring
  `featurelink_displayconfig.py`'s Flask routes 1:1. Mounted at
  `/api/featurelink/admin/datasets` behind
  `requirePermission("page.featurelink_configs")`.
- **`routes/featurelinkConfigurator.routes.js`** — serves the ported
  `index.html` + iconsets at `/featurelink-configs/configurator`, same
  permission gate.
- **`routes/featurelinkBrowse.routes.js`** — the field-user list/download API
  behind the Onboarding page, mounted at `/api/featurelink/configs*`. Requires
  nothing beyond an ordinary logged-in session — by the time these handlers
  run, `portalAuth.middleware.js` has already verified the request and set
  `req.authentikUser`. Serves each dataset's `exported_config` verbatim.
- **`views/featurelink-configs.ejs`** — the Administration hub (list saved
  dataset configs, Open/QR/Copy-link/Delete, "+ New Dataset Config").
- **`views/featurelink.ejs`** — the Onboarding page (list configs; Download +
  Open in ATAK for any dataset with a live FeatureLayer configured).
- Patches (idempotent) to:
  - **`services/permissions.registry.js`** — `page.featurelink_configs`
    permission for the admin page/API, plus carve-outs so `/featurelink` and
    its API require no specific permission.
  - **`services/portalAuth.middleware.js`** — adds `/featurelink` and its API
    to `isAllowedNonAdminPath`, the same list `/setup-my-device` and
    `/plugins` are already on, so any logged-in user reaches it even if
    they're not in an admin/agency-admin/bridge-member group.
  - **`server.js`** — mounts the route files, adds the two page routes.
  - **`views/partials/sidebar.ejs`** — the two nav links.

## Install

```bash
git clone https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
cd TAK-PluginSuite-FeatureLink/TAKPortal
bash install.sh                    # auto-detects ~/TAK-Portal, /opt/TAK-Portal, /root/TAK-Portal
# or: bash install.sh /path/to/TAK-Portal
```

This copies the module's files in (including the configurator's ~7,500
iconset files, so first install/update takes a little longer than a typical
`git pull`), patches `server.js` / `permissions.registry.js` /
`portalAuth.middleware.js` / `sidebar.ejs`, and runs
`docker compose up -d --build` to bake the changes into a rebuilt image.

Give **`page.featurelink_configs`** to whichever admin role should manage the
catalog, from TAK Portal's **Access Control** page (global admins have every
permission by default).

## Updating

Re-run `bash install.sh` — it's idempotent (each patch checks for its own
marker before applying) and safe to run repeatedly, including after `git
pull`ing a newer TAK Portal `main` from upstream. If the module's own repo
has a git remote, `install.sh` pulls it first automatically. If you're
updating from the pre-configurator-port version of this module, `install.sh`
migrates the old mount away automatically — verified with a round-trip test
(old state → migrated → uninstalled) that reproduces upstream byte-for-byte.

## Uninstall

```bash
bash uninstall.sh                  # or: bash uninstall.sh /path/to/TAK-Portal
```

Reverses the patches, deletes the copied files (including the configurator
assets), and rebuilds. Leaves `data/featurelink-configs/` in place — delete
it by hand if you also want the saved dataset configs gone.

## Known assumption / drift risk

The patches in `install.sh` anchor on exact strings from TAK Portal's
`server.js`, `permissions.registry.js`, `portalAuth.middleware.js`, and
`views/partials/sidebar.ejs` as of the version this module was written
against (verified against the live upstream source, including a round-trip
install/uninstall check that reproduces the original files byte-for-byte). If
upstream restructures those files significantly, a patch step will print an
`ERROR: could not find ... anchor` and exit rather than silently corrupt the
file — re-check the anchor against the new upstream source if that happens.

The ported `index.html` itself is a snapshot of infra-TAK's configurator as
of when this module was built — it does not auto-update if infra-TAK's own
tool gains new features later. Re-copy
`Infra-TAK/featurelink_displayconfig_assets/` and reapply the same three URL
edits (see the comments in `assets/featurelink-configurator/index.html`) to
pull in upstream improvements.
