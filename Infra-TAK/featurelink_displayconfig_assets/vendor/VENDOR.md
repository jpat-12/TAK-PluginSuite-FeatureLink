# Vendored browser libraries (C-38)

These two libraries used to be loaded from public CDNs with **no Subresource Integrity and no
`crossorigin` attribute**, inside authenticated admin pages:

| Was | Used by |
|---|---|
| `https://cdn.sheetjs.com/xlsx-0.20.3/package/dist/xlsx.full.min.js` | configurator `index.html` (Excel import) |
| `https://cdn.jsdelivr.net/npm/qrcode@1.4.4/build/qrcode.min.js` | configurator `index.html`, `views/featurelink-configs.ejs`, Infra-TAK hub |

Two separate defects:

1. **Supply chain.** A CDN compromise or DNS hijack executes attacker JavaScript inside an
   authenticated TAK Portal admin session, with no integrity check to stop it.
2. **Functional.** FeatureLink is explicitly targeted at DDIL (Denied, Degraded, Intermittent and
   Limited bandwidth) environments and at air-gapped TAK deployments. On any such network the
   Excel import and **every QR code** simply stop working, with no fallback and no error message —
   `QRCode is not defined` is thrown from `featurelink-configs.ejs`.

Both are now served from the portal's own origin. There is no remote `script-src` left on either
page, so a CSP restricted to `'self'` is achievable (tracked separately — the pages still carry
inline handlers in places).

## Provenance

| File | Upstream | Version | SHA-256 |
|---|---|---|---|
| `qrcode-1.4.4.min.js` | https://cdn.jsdelivr.net/npm/qrcode@1.4.4/build/qrcode.min.js | 1.4.4 | `0e1769a0feb8c5c87f16bcfc0a2050135d9e9f9e4d5fe46194f19183a2969b9b` |
| `xlsx-0.20.3.full.min.js` | https://cdn.sheetjs.com/xlsx-0.20.3/package/dist/xlsx.full.min.js | 0.20.3 | `cc015130aa8521e7f088f88898eba949ccdcbfb38df0bd129b44b7273c3a6f41` |

Retrieved 2026-08-03. Verify with `sha256sum` before any upgrade, and record the new digest here.

`qrcode` is pinned to 1.4.4 deliberately: 1.5.x removed the standalone browser bundle from
`build/qrcode.min.js` (it became an unbundled CommonJS shim requiring a bundler, which throws
`QRCode is not defined` when loaded directly via `<script>`).

## Licences

| Library | Licence |
|---|---|
| node-qrcode (`qrcode`) | MIT — Copyright (c) 2012 Ryan Day / soldair and contributors |
| SheetJS Community Edition (`xlsx`) | Apache-2.0 — Copyright (C) 2012-present SheetJS LLC |

Both licences permit redistribution. They must be reflected in the repo-level `NOTICE` /
`THIRD-PARTY-NOTICES.md` that C-36 requires (owned by the cross-cutting work package).

An identical pair of files is vendored at `TAKPortal/assets/featurelink-configurator/vendor/`;
the two components install independently, so each carries its own copy.
