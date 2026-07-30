# CloudTAK In-App Share — Design & API Discovery

Status: **design captured, not yet built.** The share button today downloads a
`<name>.featurelinkshare` file ([LayersTab.vue] `shareLayer()`); this document
records the discovered contract for the follow-up *in-app send to a TAK contact*
(so a CloudTAK user can push a layer config to an ATAK / WinTAK / CloudTAK
recipient over the TAK network, the way ATAK's Mission Package share does).

Everything below was verified against CloudTAK `main` + `@tak-ps/node-cot` source
and probed live against the prod server (`map.prod.ilwg.us`, CloudTAK 13.31.0) in
July 2026. All of it reaches past PluginAPI's documented surface, so it is
inherently fragile against CloudTAK changes — same caveat as `cloudtakInternals.ts`
and `importIngest.ts`.

## The receive side (already built, ATAK/WinTAK)

- `FeatureLinkMarshal` routes any received file whose name ends `.featurelinkshare`
  to `FeatureLinkImporter` — **by filename suffix only**.
- `FeatureLinkImporter` auto-applies it **only if** the delivered Mission Package's
  MANIFEST carries `onReceiveImport=true` (ATAK's `setImportInstructions(true,…)`).
  Without that flag the file lands on disk and the user must import it manually.
- CloudTAK recipients: a package that lands in their Import Manager is picked up by
  `importIngest.ts` (the auto-ingest we already ship).

## Confirmed CloudTAK endpoints

| Purpose | Endpoint | Notes |
|---|---|---|
| Session token | `localStorage['CapacitorStorage.token']` | Bearer; see `cloudtakInternals.ts`. Confirmed working. |
| List contacts | `GET /api/marti/api/contacts/all` | Returns `{ uid, callsign, team, role, takv }[]`. `takv` (`ATAK-CIV:…`, `CloudTAK:…`, `WinTAK:…`) lets us label recipient platform. **This is the contact picker source.** |
| Self identity | `GET /api/profile` | `username` → our contact uid is `ANDROID-CloudTAK-<username>` (filter self out of the picker). |
| Package list | `GET /api/marti/package` | `{ uid, hash, name, size }[]`. |
| Simple file upload | `POST /api/marti/package` | multipart/form-data (Busboy, first file). Wraps input in a `DataPackage` (or `DataPackage.parse` if it's already one). Returns `{ Hash, Name, UID, … }`. **No `destinations` → does not deliver to anyone.** |
| Build + deliver package | `PUT /api/marti/package` | Body `{ type:'FeatureCollection', name, destinations:[{uid}|{group}|{mission}], features:[…], assets:[{type:'profile',id}], basemaps:[…] }`. Builds a package server-side, uploads it, and for non-mission destinations sends a `FileShare` CoT (server fills in `senderUrl = https://<takserver>:8443/Marti/sync/content?hash=…` via DNS lookup). **This is the only path that constructs `senderUrl` correctly.** |
| Stage a profile asset | `/api/import` upload → async Events Task → S3 → `POST /api/profile/asset` | Multi-step/async. `PUT`'s `assets:[{type:'profile',id}]` requires the file to already exist as a `ProfileFile`. |

## The core problem

To deliver **our raw `.featurelinkshare`** (so the marshal fires) using CloudTAK's
correct server-side `senderUrl`, the file must be inside the package `PUT` builds.
`PUT` only takes `features` (→ CoT XML, wrong shape) or `assets` (pre-staged
`ProfileFile`s). So either:

- **Path A — profile-asset staging (recommended).** Upload `<name>.featurelinkshare`
  through CloudTAK's file pipeline until it's a `ProfileFile`, then
  `PUT /api/marti/package` with `assets:[{type:'profile', id}]` +
  `destinations:[{uid}]`. Server builds the package (file added verbatim via
  `pkg.addFile(..., {name})`, [marti-package.ts] ~L389) and sends the FileShare.
  Cost: the async upload pipeline.
- **Path B — POST + hand-sent FileShare.** `POST /api/marti/package` (simple
  multipart) → `Hash`, then build a `FileShare` CoT (`@tak-ps/node-cot`
  `lib/builders/fileshare.ts`) with `marti.dest=[{uid}]` and send via
  `mapStore.worker.conn.sendCOT(cot)` (the reach-in `cot.ts` already uses for the
  worker). Cost: must construct `senderUrl` ourselves → needs the TAK server's
  Marti address, which isn't cleanly exposed to a plugin. More fragile.

## Known limitation either way: no auto-import on the recipient

CloudTAK's `PUT` route does `new DataPackage(...)` and **never calls
`setEphemeral()`/`setPermanent()`**, so the MANIFEST never gets `onReceiveImport`
(verified in node-cot `lib/data-package.ts` — the manifest only emits params that
are set). Result: ATAK/WinTAK recipients get the native "X wants to send you a
file" accept prompt, but must tap **Import** once — it does not auto-apply like an
ATAK→ATAK share. (User accepted this tradeoff.) True parity would need `onReceiveImport`
set, which means either a CloudTAK-side change or a fully hand-built package +
hand-sent FileShare (Path B).

## Recommended build order

1. **Contact picker** — `GET /api/marti/api/contacts/all`, drop self
   (`ANDROID-CloudTAK-<username>` from `/api/profile`), show `callsign` + platform
   from `takv`, multi-select → collect `uid`s.
2. **Path A staging** — resolve the exact profile-asset upload sequence against a
   live server (the `/api/import`→ProfileFile step is the one piece still not fully
   pinned down; verify what `id` `PUT.assets` wants).
3. **Deliver** — `PUT /api/marti/package` with `assets` + `destinations`.
4. **On-device test matrix** — send to an ATAK, a WinTAK, and a CloudTAK recipient;
   confirm the file arrives and imports (one tap on ATAK/WinTAK; auto-ingest on
   CloudTAK).

[LayersTab.vue]: ../CloudTAK/plugin/components/tabs/LayersTab.vue
[marti-package.ts]: https://github.com/dfpc-coe/CloudTAK/blob/main/api/stateless/routes/marti-package.ts
