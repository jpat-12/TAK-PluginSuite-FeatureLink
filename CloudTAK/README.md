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
| QR code scan/generate (ZXing camera) | Dropped. Layer/PLI-endpoint sharing is copy-to-clipboard / paste-JSON / upload-`.json` instead |
| ATAK Mission Package send-to-contact | Copy-to-clipboard / download-as-file (no CloudTAK contact-send API used here) |
| `featurelink://import` deep link, native OAuth WebView | Dropped |
| OAuth PKCE sign-in, `featurelink://auth` custom-scheme redirect | Same OAuth2 PKCE flow and same ArcGIS OAuth app/client ID, redirecting to ArcGIS's hosted login page in a popup — but the redirect_uri is a fixed relay page instead of a custom URI scheme, so it works unmodified on every CloudTAK deployment (see **Authentication** below) |

## Known limitation — PLI auto-send

CloudTAK's public `PluginAPI` doesn't (yet) expose a confirmed way to read the operator's own
CoT self-marker position — the same gap `CloudTAK-Plugin_StatusBoard_CAP` flags for its
`pullCotPosition()` hook. `lib/cot.ts`'s `getSelfPosition()` attempts a best-effort reach-in
and degrades to a silent no-op (logged once to the console) if that shape isn't what's
actually there on your CloudTAK build. Re-verify `getSelfPosition()` against your CloudTAK
version, or replace it with a confirmed API once CloudTAK exposes one.

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
- Nothing else — sign-in works out of the box (see **Authentication** above).

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
      HomeTab.vue                 feature-count stats, quick-glance status card
      LayersTab.vue                private (signed-in) + public layer lists
      PliTab.vue                   create/join PLI layer, auto-send toggle, share endpoint
  lib/
    types.ts                    ArcGISLayer, DisplayConfig, PLI payload types (ported schema)
    config.ts                   deployment constants (default portal URL, ArcGIS OAuth client ID)
    plugin-api.ts                holds the PluginAPI reference
    store.ts                     reactive state + localStorage persistence
    oauth.ts                     ArcGIS OAuth2 PKCE flow: auth URL, code exchange, refresh
    arcgisAuth.ts                session manager: sign in/out via OAuth popup, token persistence + refresh
    arcgisRest.ts                fetch-based ArcGIS REST client (search, download, applyEdits, publish)
    displayConfig.ts             styling-rule JSON parse + per-feature color/icon/label resolution
    layerActions.ts              shared layer CRUD/download logic
    layerShare.ts                share-config JSON builder + clipboard/file helpers
    importConfig.ts              paste/upload config JSON → apply (QR-scan replacement)
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
