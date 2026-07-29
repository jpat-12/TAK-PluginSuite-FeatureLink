# Native ArcGIS-Renderer-to-Iconset Integration Across FeatureLink

## Context

The `ArcGIS-WebMap` POC (`arcgis_to_cot.py`) proved out a specific capability: given an ArcGIS Web Map or FeatureServer, read its renderer's picture-marker (`esriPMS`) symbols, decode their embedded `imageData`, resize them, and package them into an ATAK-compatible iconset zip (`iconset.xml` + PNGs, `{uid}/{group}/{filename}` addressing) — with zero manual icon work.

FeatureLink (`TAK-PluginSuite-FeatureLink`) already needs exactly this. `CONFIG-FORMAT.md`'s "Custom icon sets" section names the gap directly:

> **Known limitation:** custom sets have no ATAK iconset UID... a config referencing a custom icon has nothing... to resolve on the plugin side, and ATAK falls back to its default marker styling... making them actually render as ATAK markers would need the plugin to fetch and cache arbitrary bitmaps by URL, which hasn't been built yet.

**The requirement driving this rewrite:** a user must be able to take one Web Map/FeatureServer link and paste it into *any* of the four surfaces — ATAK, WinTAK, CloudTAK, or TAK Portal — and get the same result. Concretely: if user 1 on WinTAK and user 2 on ATAK are both looking at data from the same source layer, their `<usericon iconsetpath="...">` references must resolve to the **same iconset UID, group, and filename** on both devices, so a CoT event one of them creates/shares renders identically on the other's screen. This is a stronger requirement than "each platform can generate *an* iconset" — it requires every platform's generator to be deterministic and mutually consistent, not four independent implementations that happen to look similar.

## Design decision: fully federated, everything on-device

**Every platform ingests the link and does the entire pipeline locally — no server round-trip is ever required.** Pasting a Web Map/FeatureServer link into any of the four surfaces pulls the layer, applies its styling, and generates + installs the matching iconset on that device, using only that device's own code. TAK Portal is *one of four equal generators*, not a central authority the others depend on.

This is a deliberate narrowing of an earlier two-mode design (centralized-vs-federated). We are committing to the federated model as *the* model because it is offline-capable, has no single point of failure, and needs no cross-service plumbing. The centralized "fetch-by-UID from TAK Portal" capability is retained only as an **optional convenience** (see "Optional: TAK Portal as a convenience cache"), never as a prerequisite for any platform to function.

## Why federated works at all: the shareability guarantee

`<usericon iconsetpath="{uid}/{group}/{filename}">` is resolved **by string**, not by content hash — ATAK (and by extension anything mirroring its model) doesn't care whether two icon files are byte-identical, only whether a locally-installed iconset with that exact UID/group/filename exists. That means two independently-generated iconsets are already "the same" for sharing purposes as long as:

1. Every platform derives the **UID** from the same canonical input using the same formula, and
2. Every platform derives the **group name** and **per-value filenames** from the same fields using the same sanitization rules.

Neither depends on the actual image bytes. The POC already does this correctly today — `uid = sha256(f"{layer_url}/{layer_id}/{field}")` — it's metadata-derived, not pixel-derived. A Java resize and a Python resize won't produce the same PNG bytes, but they produce the **same `iconsetpath` strings**, so a WinTAK-generated icon and an ATAK-generated icon for the same source layer resolve identically — a shared CoT event referencing it renders on both, even though the two devices built their zips independently and never talked to each other.

**Because there is no server to reconcile a mismatch, the frozen spec (Phase 0) is the *only* thing holding the four implementations together.** In the earlier centralized design a UID disagreement could be masked by everyone fetching the same physical file; here it cannot. Getting Phase 0 exactly right, and porting it faithfully to each language, is therefore the whole ballgame.

## The one cost of going server-free (name it, don't paper over it)

If device B receives a shared CoT event referencing an auto-generated icon whose **source layer B has never ingested**, B has no way to conjure that icon from the bare reference — it falls back to a default marker, same as today. In the retired centralized design, TAK Portal's by-UID repository was the *only* thing that closed this gap; pure federated mode cannot close it.

The practical consequence, stated plainly for the spec and README:

- ✅ Share the **link** → every device that ingests it renders the icons identically.
- ⚠️ Share only a **bare CoT event** to a device that never ingested the source link → that device shows a default marker until it loads the source.

For the normal FeatureLink workflow (everyone is handed the same layer link) this is a non-issue. It is documented here so no one later mistakes it for a bug.

## Phase 0 — Freeze the shared spec (prerequisite to everything else)

New doc, `AUTO-ICONSET-SPEC.md`, sibling to `CONFIG-FORMAT.md`, versioned the same way. Defines, precisely enough that a Python/Java/C#/TypeScript port all produce identical strings:

- **Canonicalization of the source URL**: how a pasted Web Map vs. FeatureServer vs. `.../FeatureServer/N` link is normalized to one canonical form before hashing (scheme lowercased, no trailing slash, resolved to the literal `FeatureServer/{layerId}` form even if the user pasted a web map link) — this matters because two users pasting *equivalent but textually different* links must still converge on the same UID.
- **UID formula**: `sha256(canonical_layer_url + "/" + field_name)`, hex digest — exactly what the POC does today, just pinned as a contract instead of an implementation detail.
- **Group name derivation**: the service's own `name` property (from `FeatureServer/{id}?f=json`, *before* any web-map-level renderer override — the override can change the renderer but the group-naming source stays fixed) + `" Icons"`, sanitized.
- **Filename derivation per renderer entry**: label-or-value → strip any embedded path prefix → strip `.png` → sanitize forbidden filesystem characters → dedupe collisions with a fixed `_2`, `_3`... suffix rule. (This is the exact logic already validated in the POC's `safe_filename()`/`write_icon()` — the spec just needs to write down the algorithm, not invent a new one.)
- **Default/fallback icon naming**: fixed name (`Other.png`) when no `defaultLabel` is present.
- A version marker in `iconset.xml` (`version="1"`) and an explicit note that changing any of the above is a breaking change to shared/cached iconsets — same "hand-synchronized, nothing enforces it at build time" warning `CONFIG-FORMAT.md` already carries for its own contract.

Every platform below implements this spec; none of them invent their own hashing/naming logic. **This phase must land before any port begins** — with no server to reconcile drift, a spec change after two platforms have generated iconsets under an old formula breaks the exact cross-platform matching this plan exists to guarantee.

## Phase A — ATAK (on-device)

Everything happens locally as part of adding the layer; there is no server-backed variant required.

- Java port of the Phase 0 spec directly in `ArcGISRestClient.java` / `DisplayConfig.java`:
  - fetch renderer JSON, decode base64 `esriPMS.imageData`,
  - `Bitmap.createScaledBitmap` for the 32×32 resize,
  - `ZipOutputStream` for packaging `iconset.xml` + PNGs,
  - install straight into `atak/iconsets/`.
- Runs synchronously as part of the existing **Add Layer > paste a Feature Service URL** flow (method 5) — no server round trip, no dialog, matching the POC's one-link experience. (The heavier decode/resize/zip work runs off the UI thread with a non-blocking progress toast; the `ATAK-Plugin-QuickCapture` iconset packaging code is the closest existing analog to mirror.)
- Because it follows the same spec as every other platform, its UID/group/filenames match what WinTAK/CloudTAK/TAK Portal would independently produce for the same source layer.
- `FeatureLinkDropDownReceiver`'s existing `getMissingIconsetUids()` check is repurposed: instead of prompting the user, a missing UID whose source layer is already known triggers a local regenerate. (A missing UID with *no* known source layer is the documented offline limitation — default marker, no dialog spam.)
- **Self-render on the paste path:** after generating the set, ATAK synthesizes a display config from the layer's own renderer (`DisplayConfig.forAutoIcons`) mapping each field value → the generated `iconsetpath`, so the *pasting* device shows the icons on its own map — not only devices that later receive its CoT. Covers `uniqueValue` (per-value) and single-symbol renderers; `classBreaks` (range-matched) icons are generated for sharing but not self-rendered (documented gap). An existing config (from a QR/TAK Portal scan) is never overwritten.

## Phase B — CloudTAK (on-device)

- `lib/displayConfig.ts`'s `resolveIconsetPath()` builds a `data:` URI straight from the renderer's `imageData` via `lib/arcgisRest.ts` — simplest of all four platforms since browsers render inline base64 natively, **no zip packaging at all**.
- Still computes the same spec UID even though the browser doesn't need it for its own rendering — it must advertise the same UID if it ever writes a `<usericon>` reference into a shared CoT event, so other platforms can resolve it.
- Resolves automatically as part of loading the link — no separate step.

## Phase C — WinTAK (on-device)

Two prerequisites not yet built (per `WinTAK5.6/README.md` and the earlier survey — no symbology mapper, no icon/custom-marker mechanism at all):

1. Port `SymConfig.fromEsriRenderer()` to C# (`SymConfig.cs`, mirroring `DisplayConfig.java`) — needed regardless of iconsets.
2. Research spike: find (or confirm the absence of) a WinTAK SDK equivalent to ATAK's `UserIconDatabase`/custom-marker-by-path system, since this determines whether WinTAK can even *render* a custom bitmap icon the same way ATAK does, independent of whether the UID matches.
3. Once scoped: implement the Phase 0 spec in C# (same canonicalization/UID/naming rules — this is what lets a WinTAK-generated icon reference match an ATAK one for the same source layer), generating and installing the iconset on-device, wired into `FeatureLinkDockPane.PostFeatureAsCot()`'s CoT detail construction.

**If the spike finds no viable per-icon bitmap mechanism in WinTAK**, the fallback is documented, not silently dropped: WinTAK renders the renderer's resolved *color/shape* (which `SymConfig.fromEsriRenderer()` already gives it) instead of the custom icon, while still emitting the correct `iconsetpath` in its own CoT output so that an ATAK/CloudTAK device receiving that CoT event *does* render the real icon — WinTAK just wouldn't show it locally. This keeps sharing correct even if WinTAK's own rendering is temporarily degraded.

## Phase D — TAK Portal (on-device, and an equal peer)

TAK Portal generates iconsets the same way every other platform does — it is not privileged.

- **New service**: `TAKPortal/services/featurelinkArcgisIconset.service.js` — Node implementation of the Phase 0 spec (decode base64 `esriPMS.imageData`, resize to 32×32 via `sharp`, build `iconset.xml`, zip, using `crypto.createHash('sha256')` for the UID exactly as specified).
- **Reuse, don't duplicate ingestion**: feed the generated zip through the *existing* `services/featurelinkCustomIcons.service.js` ingest path (already parses an uploaded zip's `iconset.xml` for `uid`/`defaultGroup`, registers it in the manifest).
- **New route**: `POST /api/featurelink/admin/custom-icons/from-arcgis`, taking a Web Map/FeatureServer URL (+ optional field override), canonicalizing per the spec, calling the new service, then the existing ingest function.
- **Automatic, not a button**: fires the moment a layer is loaded from a URL in Map-Based Config / Single FeatureLayer Import — no separate click (see "One-link" section below).
- **Docs**: update `CONFIG-FORMAT.md`'s "Custom icon sets" "Known limitation" paragraph once this closes it, and cross-reference the new `AUTO-ICONSET-SPEC.md`.

### Optional: TAK Portal as a convenience cache (not a dependency)

Independent of the on-device model, TAK Portal *may* additionally persist generated sets keyed by UID and expose `GET /api/featurelink/custom-icons/by-uid/:uid`. A platform that has a Portal session *and* encounters an unfamiliar `iconsetpath` UID could optionally resolve it from there instead of re-ingesting the source link — this is the only thing that can soften the offline limitation described above. It is explicitly optional: no platform requires it to function, and the federated on-device path remains the default and the fallback everywhere. Build it only after the four on-device paths work.

## One-link, zero-extra-steps UX (applies to every platform above)

Every platform already has a "paste a link" entry point — TAK Portal's Map-Based Config/Single FeatureLayer Import, ATAK's Add Layer paste-URL, WinTAK's/CloudTAK's equivalents. Pasting a link into any of them pulls the layer, applies its styling, and generates + installs the matching iconset in one action:

- Generation happens **on-device**, synchronously (or with a non-blocking progress toast for the heavier Java/C# resize+zip work) as part of adding the layer — never a dialog the user must dismiss before anything works.

## Suggested sequencing

**0 → then A / B / D in any order → C last.**

- **Phase 0 first, non-negotiable** — every platform implements it, and with no server to reconcile drift, a UID-formula change after multiple platforms have generated iconsets under an old formula breaks the cross-platform matching this plan exists to guarantee.
- **A (ATAK)** and **D (TAK Portal)** are the fastest payoff — both already have the ingestion seams, and ATAK has the closest analog code in `ATAK-Plugin-QuickCapture`'s iconset packaging.
- **B (CloudTAK)** is the simplest implementation (inline `data:` URIs, no zip) and can slot in any time after Phase 0.
- **C (WinTAK)** last, gated on its SDK research spike.

## Verification

- Reuse the same 4 real layers already validated against the POC (FieldTAK CoT map, Damage Assessment source + dashboard-view layers, ILWG Ops map) as fixtures.
- **Cross-platform matching (the core requirement)**: for each fixture layer, generate its iconset independently on at least two platforms (e.g. TAK Portal's Phase D service and ATAK's Phase A path) and diff the resulting UID/group/filenames — they must be identical strings even though the actual PNG bytes may differ. **This is the primary gate** — in a server-free design nothing else catches a spec-port mismatch.
- **Shared-CoT rendering (positive case)**: simulate user 1 (WinTAK or ATAK) creating a CoT event referencing an auto-generated icon, and confirm user 2 on a different platform *who has independently ingested the same source link* renders the same icon — not a fallback default.
- **Shared-CoT rendering (documented-limitation case)**: confirm that user 2 who has *not* ingested the source link, with no optional Portal cache reachable, falls back to a default marker cleanly (no crash, no hang) — verifying the known limitation behaves as documented.
- **Phase A (ATAK)**: no-server path renders correctly with zero extra taps; a missing-but-known UID regenerates locally rather than prompting.
- **Phase B (CloudTAK)**: same link, loaded in a CloudTAK browser session, renders the same icon with no manual re-upload.
- **Phase D (TAK Portal)**: confirm `POST .../from-arcgis` produces the correctly-keyed set and that it fires automatically on URL load; if the optional cache is built, confirm `GET .../by-uid/:uid` returns it.
- **Phase C (WinTAK)**: once the SDK spike lands, confirm WinTAK either renders the real icon or (documented fallback) the correct color/shape while still emitting a spec-compliant `iconsetpath` for others to resolve.
- Re-run `arcgis_to_cot.py` against the same 4 fixtures as a regression check that the frozen Phase 0 spec still matches the POC's original output.
