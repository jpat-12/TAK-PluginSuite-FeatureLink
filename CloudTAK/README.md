# FeatureLink — CloudTAK Plugin

A CloudTAK (browser) plugin that bridges CloudTAK to ArcGIS Feature Services — the same
capability as the [ATAK5.6 FeatureLink plugin](../ATAK5.6/README.md), ported to CloudTAK's
Vue3/TypeScript plugin architecture. Sign in to an ArcGIS Portal, browse and download hosted
Feature Layers onto the map, stream a PLI-style position feed to a shared layer, and push
individual map items to a layer — all from inside CloudTAK.

---

## Why this isn't a 1:1 port

CloudTAK plugins are browser modules that run inside the CloudTAK web UI, not Android APKs —
several ATAK-specific mechanisms don't have a direct equivalent and were adapted:

| ATAK plugin | CloudTAK plugin |
|---|---|
| Device GPS → PLI layer (`MapView.getSelfMarker()`) | CloudTAK's own CoT self-marker position (best-effort; see **Known limitation** below) |
| Radial (long-press) menu → "Send to Feature Layer" | **Send Item to Feature Layer** picker in the plugin panel — pick an on-screen item, pick a layer, Send |
| QR code scan/generate (ZXing camera) | Dropped. Layer/PLI-endpoint sharing is download-`.featurelinkshare` / paste-JSON / upload-`.json` instead |
| ATAK Mission Package send-to-contact | The share button downloads a `<name>.featurelinkshare` file to hand off by any channel. An in-app send to a picked TAK contact (ATAK/WinTAK/CloudTAK) is fully designed against CloudTAK's `/api/marti/package` + contacts API but not yet built — see [`docs/CLOUDTAK-SHARE-DESIGN.md`](../docs/CLOUDTAK-SHARE-DESIGN.md) |
| ATAK Mission Package **received** by a CloudTAK session | Auto-ingested from CloudTAK's Import Manager — see **Receiving ATAK/WinTAK layer shares** below |
| `featurelink://import` deep link, native OAuth WebView | Dropped |
| OAuth PKCE sign-in, `featurelink://auth` custom-scheme redirect | Same OAuth2 PKCE flow and same ArcGIS OAuth app/client ID, redirecting to ArcGIS's hosted login page in a popup — but the redirect_uri is a fixed relay page instead of a custom URI scheme, so it works unmodified on every CloudTAK deployment (see **Authentication** below) |

## Known limitation — PLI auto-send

CloudTAK's public `PluginAPI` doesn't (yet) expose a confirmed way to read the operator's own
CoT self-marker position — the same gap `CloudTAK-Plugin_StatusBoard_CAP` flags for its
`pullCotPosition()` hook. `lib/cot.ts`'s `getSelfPosition()` attempts a best-effort reach-in
and degrades to a silent no-op (logged once to the console) if that shape isn't what's
actually there on your CloudTAK build. Re-verify `getSelfPosition()` against your CloudTAK
version, or replace it with a confirmed API once CloudTAK exposes one.

## Receiving ATAK/WinTAK layer shares (auto-import)

When an ATAK/WinTAK user hits **Share** on a FeatureLink layer, ATAK only knows how to send an
ATAK Mission Package — there's no CloudTAK-aware send path. Sent to a CloudTAK session, that
package lands in CloudTAK's own generic **Import Manager**, which tries to build a map tileset
from it and fails ("No features found… Cannot create tileset") since it's a small JSON config,
not spatial data. The uploaded `.zip` survives that failure, and inside it is a
`<name>.featurelinkshare` file.

`lib/importIngest.ts` polls CloudTAK's `/api/import` every 60s for packages named like ATAK's
share convention (`FeatureLink - <layer>`), downloads the raw zip, extracts that file
(`lib/zipReader.ts` parses the ZIP by hand + `DecompressionStream`, no bundled zip dep), and
applies it the same way **Add Layer → Import Config** does. Success/failure surfaces as an
**Auto-Import** row on the Home tab.

This reaches past `PluginAPI`'s documented surface to call `/api/import` directly with a token
read out of `localStorage` (`lib/cloudtakInternals.ts`), so it's inherently fragile against
future CloudTAK changes — every failure path is caught and shown rather than failing silently.
Note the match is done **client-side** on the import name; CloudTAK's `/api/import` `filter`
query param is a plain substring match, not a regex (an anchored `^FeatureLink` matches nothing).

## Authentication

Sign-in opens ArcGIS's own hosted OAuth2 login page (PKCE flow, ported from the ATAK plugin's
`ArcGISAuthManager`/`OAuthHelper`) in a popup — clicking **Sign in with ArcGIS** never navigates
CloudTAK itself away, and the panel resolves once the user has authenticated with ArcGIS
(including SSO/enterprise/social logins, unlike the old username/password approach). CloudTAK
never sees the password.

**Works out of the box on any CloudTAK deployment — no per-install ArcGIS setup needed.** The
tricky part of web OAuth is normally that ArcGIS only accepts a pre-registered, exact
`redirect_uri` per app (no wildcards), so a naive port would need every CloudTAK hostname
individually registered. Instead, the OAuth app's one and only registered `redirect_uri` is a
small static relay page — [`docs/featurelink-oauth-relay.html`](../docs/featurelink-oauth-relay.html)
in this repo, published via GitHub Pages — that the popup lands on after ArcGIS auth completes.
The relay forwards the result back to whichever CloudTAK origin actually opened the popup (via
`window.opener.postMessage`, targeted using the origin embedded in the OAuth `state` param), so
the plugin's own origin is never involved in ArcGIS's redirect_uri allowlist at all. See
`ARCGIS_OAUTH_CLIENT_ID` / `ARCGIS_OAUTH_RELAY_URL` in `plugin/lib/config.ts` — if you fork this
plugin under your own ArcGIS OAuth app, update both.

The OAuth exchange itself lives in `lib/oauth.ts`; the popup lifecycle, `postMessage` handshake,
and session/token persistence + silent refresh are in `lib/arcgisAuth.ts`'s `beginSignIn()`.

## Required server config — Content-Security-Policy

CloudTAK's nginx config sends a `Content-Security-Policy` header whose `connect-src` directive,
by default, only allows the browser to `fetch()` the CloudTAK server itself — which blocks
**every** ArcGIS REST call this plugin makes (OAuth token exchange, layer search/download, PLI
publish, all of it) with a generic `TypeError: Failed to fetch`. This isn't a plugin bug or
something `install.sh` can fix — it's CloudTAK's own server-side CSP header, controlled by an
env var CloudTAK already supports (`api/nginx.conf.js`). Add this to CloudTAK's
`docker-compose.yml` (the `api` service's `environment:`), or its `.env` file:

```
NGINX_CSP_CONNECT_SRC=https://www.arcgis.com,https://*.arcgis.com
```

Then recreate the container so nginx regenerates its config (a plain `restart` won't pick up a
changed env var):

```sh
docker compose up -d api
```

Verify it took effect:

```sh
curl -sI https://<your-cloudtak-host>/ | grep -i content-security-policy
# should include: connect-src ... https://www.arcgis.com https://*.arcgis.com
```

If you're pointing this at an ArcGIS **Enterprise** portal instead of ArcGIS Online, add that
portal's own domain to the same comma-separated list too.

## UI parity with the ATAK version

Outside of the platform-forced adaptations in the table above, this plugin intentionally
mirrors the ATAK version's layout rather than redesigning it: the same Home / Layers / PLI
tabs, the same section groupings and titles ("Feature Statistics", "My ArcGIS Layers",
"Public Layers", "PLI Feature Layer"), the same collapsible-section behavior — including the
PLI Feature Layer section auto-collapsing the first time it's found connected, ported from
`FeatureLinkDropDownReceiver.maybeAutoCollapsePliLayerSection` — and the same green/red/gray
status-color conventions for connected/disconnected/off. If you're extending this plugin,
keep new UI in that spirit rather than introducing a different pattern.

---

## Requirements

- A CloudTAK checkout (for `install.sh`) or `@tak-ps/cloudtak` types (for dev typecheck).
- `NGINX_CSP_CONNECT_SRC` set on CloudTAK's `api` service to allow ArcGIS domains (see
  **Required server config** below) — without it, every ArcGIS REST call fails with
  `Failed to fetch`.

## Develop

The plugin compiles **inside** CloudTAK, which supplies the real `@tak-ps/cloudtak` types.
To typecheck in isolation:

```sh
cd plugin
npm install
npm run check   # vue-tsc --noEmit
```

> A `@tak-ps/cloudtak` "Cannot find module" error is expected in isolation — that dependency
> only resolves at build time inside CloudTAK.

## Install

```sh
./install.sh                 # install into ~/CloudTAK
./install.sh --pull          # git pull, reinstall + rebuild
./install.sh --remove        # uninstall
```

After install: **CloudTAK → Settings → Refresh App** (a hard refresh won't work — the service
worker intercepts requests). Appears in the right-side menu as **FeatureLink**.

Before first use, set `NGINX_CSP_CONNECT_SRC` per **Required server config** below — otherwise
sign-in and every ArcGIS REST call will fail with `Failed to fetch`.

## Architecture

```
plugin/
  index.ts                     class-based entry — install/enable/disable, registers routes
  components/
    FeatureLinkMain.vue         shell: header (title, account button), Home/Layers/PLI tabs
    PluginIcon.vue               menu icon
    AccountView.vue             pushed view: sign in with ArcGIS (OAuth redirect) / sign out
    AddLayerView.vue            pushed view: add public layer by URL, paste/upload config JSON
    SendToLayerPicker.vue       pushed view: pick a map item + layer, send (radial-menu replacement)
    LayerRow.vue                shared layer-list row (visibility, interval, actions)
    tabs/
      HomeTab.vue                 feature-count stats, quick-glance status card, Auto-Import status row
      LayersTab.vue                private (signed-in) + public layer lists
      PliTab.vue                   create/join PLI layer, auto-send toggle, share endpoint
  lib/
    types.ts                    ArcGISLayer, DisplayConfig, PLI payload types (ported schema)
    config.ts                   deployment constants (default portal URL, ArcGIS OAuth client ID)
    plugin-api.ts                holds the PluginAPI reference
    store.ts                     reactive state + localStorage persistence
    oauth.ts                     ArcGIS OAuth2 PKCE flow: auth URL, code exchange, refresh
    arcgisAuth.ts                session manager: sign in/out via OAuth popup, token persistence + refresh
    arcgisRest.ts                fetch-based ArcGIS REST client (search, download, applyEdits, publish, count-only query)
    displayConfig.ts             styling-rule JSON parse + per-feature color/icon/label resolution
    layerActions.ts              shared layer CRUD/download; feature counts on sign-in (count-only, no download)
    layerShare.ts                share-config JSON builder + clipboard/file-download helpers
    importConfig.ts              paste/upload/auto-ingested config JSON → apply (QR-scan replacement)
    importIngest.ts              polls /api/import for ATAK-shared packages, applies them (see Receiving section)
    zipReader.ts                 hand-rolled ZIP central-directory parse + DecompressionStream (no zip dep)
    cloudtakInternals.ts         reach-in: reads CloudTAK's session token from localStorage for /api/import
    cot.ts                       map marker reach-in, PLI breadcrumbs, on-screen marker listing
    scheduler.ts                 layer auto-refresh + PLI auto-send background loops
```

## QR Code Schemas — dropped

The ATAK plugin's four QR code types (`credentials`, `pli_endpoint`, `layer_config`,
`pli_config`) are not carried forward as QR codes. `layer_config` and `pli_endpoint` survive
as plain JSON, importable via **Add Layer → Import Config** (paste or upload `.json`).
`credentials`/`pli_config` (which carried a plaintext ArcGIS password inside a scannable QR
code) are dropped entirely — sign-in now happens once, directly in the Account view, rather
than being something worth encoding into a shareable payload.

## License

Licensed under the Apache License, Version 2.0. See the [LICENSE](../LICENSE) file for details.
