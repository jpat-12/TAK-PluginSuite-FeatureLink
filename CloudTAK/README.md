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
| OAuth PKCE sign-in | Username/password token auth against ArcGIS's `generateToken` endpoint (see **Authentication** below) |

## Known limitation — PLI auto-send

CloudTAK's public `PluginAPI` doesn't (yet) expose a confirmed way to read the operator's own
CoT self-marker position — the same gap `CloudTAK-Plugin_StatusBoard_CAP` flags for its
`pullCotPosition()` hook. `lib/cot.ts`'s `getSelfPosition()` attempts a best-effort reach-in
and degrades to a silent no-op (logged once to the console) if that shape isn't what's
actually there on your CloudTAK build. Re-verify `getSelfPosition()` against your CloudTAK
version, or replace it with a confirmed API once CloudTAK exposes one.

## Authentication

Sign-in uses ArcGIS's legacy but still-supported **username/password token auth**
(`POST /sharing/rest/generateToken`) instead of OAuth — no app registration, no redirect URI,
works immediately from any origin. Trade-offs, by design:

- The password is sent directly to the ArcGIS portal to obtain a token and is never stored —
  only the resulting token (and its expiry) persists in `localStorage`, in
  `lib/arcgisAuth.ts`/`lib/tokenAuth.ts`.
- Only works for ArcGIS **"built-in"** accounts — not SSO, enterprise logins, or social logins.
- There is no refresh token with this flow. The plugin requests the longest-lived token the
  org's token-expiration policy allows (`REQUESTED_EXPIRATION_MINUTES` in `tokenAuth.ts`), but
  once it expires the user must re-enter their password — `getToken()` clears the session
  automatically when that happens rather than failing silently.

If your organization requires SSO/enterprise login, or you'd rather not have the plugin handle
passwords at all, this is the piece to swap out — `lib/tokenAuth.ts` and the `signIn`/`getToken`
surface in `lib/arcgisAuth.ts` are the only places that would need to change.

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
- ArcGIS "built-in" account credentials (see **Authentication** above).

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
    AccountView.vue             pushed view: sign in (username/password) / sign out
    AddLayerView.vue            pushed view: add public layer by URL, paste/upload config JSON
    SendToLayerPicker.vue       pushed view: pick a map item + layer, send (radial-menu replacement)
    LayerRow.vue                shared layer-list row (visibility, interval, actions)
    tabs/
      HomeTab.vue                 feature-count stats, quick-glance status card
      LayersTab.vue                private (signed-in) + public layer lists
      PliTab.vue                   create/join PLI layer, auto-send toggle, share endpoint
  lib/
    types.ts                    ArcGISLayer, DisplayConfig, PLI payload types (ported schema)
    config.ts                   deployment constants (default portal URL)
    plugin-api.ts                holds the PluginAPI reference
    store.ts                     reactive state + localStorage persistence
    tokenAuth.ts                  generateToken (username/password) REST call
    arcgisAuth.ts                session manager: sign in/out, token persistence + expiry handling
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
