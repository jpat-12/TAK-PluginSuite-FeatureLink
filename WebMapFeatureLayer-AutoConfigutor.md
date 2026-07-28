# Native ArcGIS-Renderer-to-Iconset Integration Across FeatureLink

## Context

The `ArcGIS-WebMap` POC (`arcgis_to_cot.py`) proved out a specific capability: given an ArcGIS Web Map or FeatureServer, read its renderer's picture-marker (`esriPMS`) symbols, decode their embedded `imageData`, resize them, and package them into an ATAK-compatible iconset zip (`iconset.xml` + PNGs, `{uid}/{group}/{filename}` addressing) — with zero manual icon work.

FeatureLink (`TAK-PluginSuite-FeatureLink`) already needs exactly this. `CONFIG-FORMAT.md`'s "Custom icon sets" section names the gap directly:

> **Known limitation:** custom sets have no ATAK iconset UID... a config referencing a custom icon has nothing... to resolve on the plugin side, and ATAK falls back to its default marker styling... making them actually render as ATAK markers would need the plugin to fetch and cache arbitrary bitmaps by URL, which hasn't been built yet.

**The requirement driving this rewrite:** a user must be able to take one Web Map/FeatureServer link and paste it into *any* of the four surfaces — ATAK, WinTAK, CloudTAK, or TAK Portal — and get the same result. Concretely: if user 1 on WinTAK and user 2 on ATAK are both looking at data from the same source layer, their `<usericon iconsetpath="...">` references must resolve to the **same iconset UID, group, and filename** on both devices, so a CoT event one of them creates/shares renders identically on the other's screen. This is a stronger requirement than "each platform can generate *an* iconset" — it requires every platform's generator to be deterministic and mutually consistent, not four independent implementations that happen to look similar.

## The shareability guarantee, and why it's achievable cheaply

`<usericon iconsetpath="{uid}/{group}/{filename}">` is resolved **by string**, not by content hash — ATAK (and by extension anything mirroring its model) doesn't care whether two icon files are byte-identical, only whether a locally-installed iconset with that exact UID/group/filename exists. That means two independently-generated iconsets are already "the same" for sharing purposes as long as:

1. Every platform derives the **UID** from the same canonical input using the same formula, and
2. Every platform derives the **group name** and **per-value filenames** from the same fields using the same sanitization rules.

Neither depends on the actual image bytes. The POC already does this correctly today — `uid = sha256(f"{layer_url}/{layer_id}/{field}")` — it's metadata-derived, not pixel-derived. So the fix isn't a new synchronization mechanism, it's **writing that formula down once, precisely, as a frozen contract**, the same way `CONFIG-FORMAT.md` already documents the display-config payload as a "hand-synchronized, nothing enforces it at build time" contract between producer and consumers.

Two complementary paths follow from this, and the plan uses both:

- **Centralized (preferred whenever TAK Portal is reachable)**: TAK Portal generates once, persists the zip keyed by UID, and every platform fetches the identical physical file. Byte-identical, simplest, no drift possible.
- **Federated (required for the no-server paths — pasting a link directly into ATAK/WinTAK/CloudTAK with no TAK Portal involved)**: each platform independently computes the same UID/group/filenames from the same source layer, per the frozen spec, and generates its own zip locally. Not byte-identical (a Java resize and a Python resize won't produce the same PNG bytes), but **string-identical at every level that matters for rendering and sharing** — a WinTAK-generated icon and an ATAK-generated icon for the same source layer resolve to the same `iconsetpath`, so a shared CoT event referencing it renders on both, even though the two devices built their zips independently and never talked to each other.

**Named limitation to document, not paper over:** if device B has never loaded the source layer (so it's never generated or fetched that UID) and has no TAK Portal to ask, it has no way to conjure the icon from a bare CoT reference — it falls back to a default marker, same as today. Centralized mode closes this (TAK Portal becomes a lookup-by-UID repository, described below); pure federated/offline mode cannot close it and shouldn't pretend to.

## Phase 0 — Freeze the shared spec (prerequisite to everything else)

New doc, `AUTO-ICONSET-SPEC.md`, sibling to `CONFIG-FORMAT.md`, versioned the same way. Defines, precisely enough that a Python/Java/C#/TypeScript port all produce identical strings:

- **Canonicalization of the source URL**: how a pasted Web Map vs. FeatureServer vs. `.../FeatureServer/N` link is normalized to one canonical form before hashing (scheme lowercased, no trailing slash, resolved to the literal `FeatureServer/{layerId}` form even if the user pasted a web map link) — this matters because two users pasting *equivalent but textually different* links must still converge on the same UID.
- **UID formula**: `sha256(canonical_layer_url + "/" + field_name)`, hex digest — exactly what the POC does today, just pinned as a contract instead of an implementation detail.
- **Group name derivation**: the service's own `name` property (from `FeatureServer/{id}?f=json`, *before* any web-map-level renderer override — the override can change the renderer but the group-naming source stays fixed) + `" Icons"`, sanitized.
- **Filename derivation per renderer entry**: label-or-value → strip any embedded path prefix → strip `.png` → sanitize forbidden filesystem characters → dedupe collisions with a fixed `_2`, `_3`... suffix rule. (This is the exact logic already validated in the POC's `safe_filename()`/`write_icon()` — the spec just needs to write down the algorithm, not invent a new one.)
- **Default/fallback icon naming**: fixed name (`Other.png`) when no `defaultLabel` is present.
- A version marker in `iconset.xml` (`version="1"`) and an explicit note that changing any of the above is a breaking change to shared/cached iconsets — same "hand-synchronized, nothing enforces it at build time" warning `CONFIG-FORMAT.md` already carries for its own contract.

Every phase below implements this spec; none of them invent their own hashing/naming logic.

## Phase A — TAK Portal (canonical authority + shared repository)

- **New service**: `TAKPortal/services/featurelinkArcgisIconset.service.js` — Node implementation of the Phase 0 spec (decode base64 `esriPMS.imageData`, resize to 32×32 via `sharp`, build `iconset.xml`, zip, using `crypto.createHash('sha256')` for the UID exactly as specified).
- **Reuse, don't duplicate ingestion**: feed the generated zip through the *existing* `services/featurelinkCustomIcons.service.js` ingest path (already parses an uploaded zip's `iconset.xml` for `uid`/`defaultGroup`, registers it in the manifest).
- **Persistent, lookup-by-UID storage**: store generated sets keyed by UID permanently (not just per-session), and add `GET /api/featurelink/custom-icons/by-uid/:uid` — this is what makes TAK Portal a **shared repository**: any platform that encounters an unfamiliar `iconsetpath` UID in an incoming CoT event, and has a TAK Portal session, can resolve and install it on demand, not just at config-apply time.
- **New route**: `POST /api/featurelink/admin/custom-icons/from-arcgis`, taking a Web Map/FeatureServer URL (+ optional field override), canonicalizing per the spec, calling the new service, then the existing ingest function.
- **Automatic, not a button**: fires the moment a layer is loaded from a URL in Map-Based Config / Single FeatureLayer Import — no separate click (see "One-link" section below).
- **Docs**: update `CONFIG-FORMAT.md`'s "Custom icon sets" "Known limitation" paragraph once this closes it, and cross-reference the new `AUTO-ICONSET-SPEC.md`.

## Phase B — ATAK

1. **Server-backed (TAK Portal Link / QR, Modes 2/3)**: `IconsetInstaller`-style class (mirroring `ATAK-Plugin-QuickCapture/.../icons/IconsetInstaller.java`), fetching by UID from TAK Portal's `by-uid` route — including for UIDs discovered from *incoming CoT traffic*, not just from an applied display config, so a device can backfill an icon it's missing when it sees one referenced. Wired into `FeatureLinkDropDownReceiver`'s existing `getMissingIconsetUids()` check, firing automatically with a progress toast; the existing blocking `AlertDialog` becomes the fallback only if the fetch itself fails (no network, no session).
2. **No-server (Add Layer > paste a Feature Service URL directly, method 5)**: Java port of the Phase 0 spec directly in `ArcGISRestClient.java`/`DisplayConfig.java` (`Bitmap.createScaledBitmap` for resize, `ZipOutputStream` for packaging), run synchronously as part of adding the layer, straight into `atak/iconsets/` — no server round trip, no dialog, matching the POC's one-link experience. Because it follows the same spec as every other platform, its UID/group/filenames match what WinTAK/CloudTAK/TAK Portal would independently produce for the same source layer.

## Phase C — CloudTAK

- `lib/displayConfig.ts`'s `resolveIconsetPath()` extended two ways: TAK-Portal-backed configs resolve via the `by-uid` route (reusing whatever asset-caching `mapStore` already has — confirm the exact mechanism during implementation, don't assume); Map-Based/Single-FeatureLayer-Import (no TAK Portal, direct via `lib/arcgisRest.ts`) builds a `data:` URI straight from the renderer's `imageData` — simplest of all four platforms since browsers render inline base64 natively, no zip packaging at all. Still computes the same spec UID (even though a browser doesn't need it for its own rendering, it needs to advertise the same UID if it ever writes a `<usericon>` reference into a shared CoT event, so other platforms can resolve it).
- Both paths resolve automatically as part of loading the link — no separate step.

## Phase D — WinTAK

Two prerequisites not yet built (per `WinTAK5.6/README.md` and the earlier survey — no symbology mapper, no icon/custom-marker mechanism at all):

1. Port `SymConfig.fromEsriRenderer()` to C# (`SymConfig.cs`, mirroring `DisplayConfig.java`) — needed regardless of iconsets.
2. Research spike: find (or confirm the absence of) a WinTAK SDK equivalent to ATAK's `UserIconDatabase`/custom-marker-by-path system, since this determines whether WinTAK can even *render* a custom bitmap icon the same way ATAK does, independent of whether the UID matches.
3. Once scoped: implement the Phase 0 spec in C# (same canonicalization/UID/naming rules — this is what lets a WinTAK-generated icon reference match an ATAK one for the same source layer), wired into `FeatureLinkDockPane.PostFeatureAsCot()`'s CoT detail construction, same server-backed/no-server duality as Phase B.

**If the spike finds no viable per-icon bitmap mechanism in WinTAK**, the fallback is documented, not silently dropped: WinTAK renders the renderer's resolved *color/shape* (which `SymConfig.fromEsriRenderer()` already gives it) instead of the custom icon, while still emitting the correct `iconsetpath` in its own CoT output so that an ATAK/CloudTAK device receiving that CoT event *does* render the real icon — WinTAK just wouldn't show it locally. This keeps sharing correct even if WinTAK's own rendering is temporarily degraded.

## One-link, zero-extra-steps UX (applies to every phase above)

Every platform already has a "paste a link" entry point — TAK Portal's Map-Based Config/Single FeatureLayer Import, ATAK's Add Layer paste-URL, WinTAK's/CloudTAK's equivalents. Pasting a link into any of them should pull the layer, apply its styling, and generate/fetch/install the matching iconset in one action:

- No-server paths (method 5 on each platform): generation happens synchronously, on-device, as part of adding the layer.
- Server-backed paths: fetch-by-UID happens automatically in the background with a progress toast, not a dialog the user must dismiss/acknowledge before anything works.

## Suggested sequencing

**0 → A → B → C → D.** Phase 0 must land first — every other phase implements it, and retrofitting a UID formula change after multiple platforms have generated/cached iconsets under an old formula would break the exact cross-platform matching this plan exists to guarantee. After that: A (fastest payoff, becomes the shared repository), then B, C, D as before.

## Verification

- Reuse the same 4 real layers already validated against the POC (FieldTAK CoT map, Damage Assessment source + dashboard-view layers, ILWG Ops map) as fixtures.
- **Cross-platform matching (the core new requirement)**: for each fixture layer, generate its iconset independently on at least two platforms (e.g. TAK Portal's Phase A service and ATAK's no-server Phase B path) and diff the resulting UID/group/filenames — they must be identical strings even though the actual PNG bytes may differ.
- **Shared-CoT rendering**: simulate user 1 (WinTAK or ATAK) creating a CoT event referencing an auto-generated icon, and confirm user 2 on a different platform, who has independently loaded the same source layer (or fetches it from TAK Portal's `by-uid` route), renders the same icon — not a fallback default.
- Phase A: confirm `POST .../from-arcgis` produces a set discoverable via `GET .../by-uid/:uid`, and that it fires automatically on URL load.
- Phase B: no-server path renders correctly with zero extra taps; server-backed path auto-fetches missing icons with only a toast, falling back to the old dialog only on fetch failure.
- Phase C: same link, loaded in a CloudTAK browser session, renders the same icon with no manual re-upload.
- Phase D: once the SDK spike lands, confirm WinTAK either renders the real icon or (documented fallback) the correct color/shape while still emitting a spec-compliant `iconsetpath` for others to resolve.
- Re-run `arcgis_to_cot.py` against the same 4 fixtures as a regression check that the frozen Phase 0 spec still matches the POC's original output.
