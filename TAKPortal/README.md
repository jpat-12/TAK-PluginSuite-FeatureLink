# TAK Portal Module — FeatureLink Configs

Adds a **FeatureLink** entry under **Onboarding** and a **FeatureLink Configs**
entry under **Administration** to an existing
[TAK Portal](https://github.com/AdventureSeeker423/TAK-Portal) install (as
deployed by [infra-TAK](https://github.com/jpat-12/infra-TAK), default
`~/TAK-Portal`, Docker Compose).

Lets an admin prep FeatureLink display configs (upload a file, or point at a
live source URL — e.g. an ArcGIS FeatureLayer) for field users to browse and
download, without needing the infra-TAK console or its admin password.
**Anyone already logged into TAK Portal** can open **Onboarding → FeatureLink**
and download a config — that page is just another download link inside the
user's normal, already-Authentik-gated portal session. There's no separate
login or token for the plugin: the user downloads the file in their browser,
then opens/imports it into the FeatureLink ATAK/WinTAK plugin the same way as
any other exported FeatureLink display config (see Infra-TAK's "Payload
formats" section).

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

- **`routes/featurelinkConfigsAdmin.routes.js`** — admin CRUD API for the
  config catalog, mounted at `/api/featurelink/admin/configs` behind
  `requirePermission("page.featurelink_configs")` (same convention as Plugin
  Manager's `page.plugin_manager`). Multipart upload for file-backed configs,
  same pattern as the existing Plugin Manager's APK upload.
- **`routes/featurelinkBrowse.routes.js`** — the list/download API behind the
  Onboarding page, mounted at `/api/featurelink/configs*`. Requires nothing
  beyond an ordinary logged-in session — by the time these handlers run,
  `portalAuth.middleware.js` has already verified the request and set
  `req.authentikUser`.
- **`services/featurelinkConfigs.service.js`** — catalog storage
  (`data/featurelink-configs/catalog.json` + `data/featurelink-configs/files/`,
  inside the `tak_portal_data` volume so it survives rebuilds).
- **`views/featurelink-configs.ejs`** — the Administration page (list, add,
  delete configs).
- **`views/featurelink.ejs`** — the Onboarding page (list configs, download
  buttons).
- Patches (idempotent) to:
  - **`services/permissions.registry.js`** — new `page.featurelink_configs`
    permission for the admin page/API, plus carve-outs so `/featurelink` and
    its API require no specific permission.
  - **`services/portalAuth.middleware.js`** — adds `/featurelink` and its API
    to `isAllowedNonAdminPath`, the same list `/setup-my-device` and
    `/plugins` are already on, so any logged-in user reaches it even if
    they're not in an admin/agency-admin/bridge-member group.
  - **`server.js`** — mounts the two route files, adds the two page routes.
  - **`views/partials/sidebar.ejs`** — the two nav links.

## Install

```bash
git clone https://github.com/jpat-12/TAK-PluginSuite-FeatureLink.git
cd TAK-PluginSuite-FeatureLink/TAKPortal
bash install.sh                    # auto-detects ~/TAK-Portal, /opt/TAK-Portal, /root/TAK-Portal
# or: bash install.sh /path/to/TAK-Portal
```

This copies the module's files in, patches `server.js` /
`permissions.registry.js` / `portalAuth.middleware.js` / `sidebar.ejs`, and
runs `docker compose up -d --build` to bake the changes into a rebuilt image.

Give **`page.featurelink_configs`** to whichever admin role should manage the
catalog, from TAK Portal's **Access Control** page (global admins have every
permission by default).

## Updating

Re-run `bash install.sh` — it's idempotent (each patch checks for its own
marker before applying) and safe to run repeatedly, including after `git
pull`ing a newer TAK Portal `main` from upstream. If the module's own repo
has a git remote, `install.sh` pulls it first automatically.

## Uninstall

```bash
bash uninstall.sh                  # or: bash uninstall.sh /path/to/TAK-Portal
```

Reverses the patches, deletes the copied files, and rebuilds. Leaves
`data/featurelink-configs/` in place — delete it by hand if you also want the
saved configs gone.

## Known assumption / drift risk

The patches in `install.sh` anchor on exact strings from TAK Portal's
`server.js`, `permissions.registry.js`, `portalAuth.middleware.js`, and
`views/partials/sidebar.ejs` as of the version this module was written
against (verified against the live upstream source, including a round-trip
install/uninstall check that reproduces the original files byte-for-byte). If
upstream restructures those files significantly, a patch step will print an
`ERROR: could not find ... anchor` and exit rather than silently corrupt the
file — re-check the anchor against the new upstream source if that happens.
