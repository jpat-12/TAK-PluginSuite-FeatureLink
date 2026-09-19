# ATAK — outstanding work

Carried forward from the WinTAK symbology work of 2026-09-19. Everything here is ATAK-side
only; the WinTAK equivalents are done unless stated.

**Read this first:** nothing in `ATAK5.6/` or `ATAK5.7/` has been compiled during this work.
There is no ATAK SDK on the development machine, no ATAK test suite in the repo, and CI no
longer builds ATAK (`.github/workflows/ci.yml` — the jobs were removed because a public runner
can never hold the licensed SDK). Every item below is written but unverified. **Budget a Gradle
build before any of it ships.**

The two trees' parser files are currently byte-identical. Keep them that way — make each change
once and copy it across, then `diff` to confirm.

---

## 1. Iconset replacement — always remove before re-adding

**Status:** done on WinTAK, **not done on ATAK.**

WinTAK's `IconsetInstaller.Install` now unconditionally removes an existing set under the same
UID and re-imports, rather than reusing it.

The reason is structural and applies identically to ATAK: the UID hashes the layer URL and the
driving field, **not the symbols**. It is a stable identity for *"this layer's icons"* and says
nothing about whether those icons are current. Anything that reuses a set on a UID match will
serve stale icons indefinitely — a layer whose renderer an earlier build mis-read keeps its wrong
iconset forever, because the UID still matches. That is not hypothetical: it happened on WinTAK,
and it survived two separate fixes to the renderer reader before being noticed.

ATAK writes its zip to `atak/iconsets/` and broadcasts `com.atakmap.app.REFRESH_ICONSET`
(`AutoIconset.java`, class comment ~line 42). Determine what ATAK's importer does when a set
with that UID is already present — overwrite, ignore, or duplicate — and make the
remove-then-add explicit rather than relying on the importer's behaviour.

Note the cost, which WinTAK also carries: this makes an install do real work on every sync of a
picture-marker layer, including each recurrence tick. If that proves too expensive on a handheld,
gate it on a content fingerprint rather than on UID presence — hash the entry names and bytes,
**not** the zip bytes, because zip entries carry timestamps and identical content produces
different bytes every build.

---

## 2. Confirm the `uniqueValueGroups` port compiles and behaves

**Status:** written 2026-09-19, **uncompiled.**

`AutoSymbology.enumerateValueEntries` was added and is consumed by `AutoIconset`,
`AutoSymbology` and `DisplayConfig`. It reads both the classic flat `uniqueValueInfos` array and
the modern `uniqueValueGroups[].classes[]` that ArcGIS Online now publishes.

This closed a real cross-platform divergence rather than a cosmetic gap: the iconset UID is
identical on both platforms, so for a modern renderer WinTAK was building uid `X` holding twelve
icons while ATAK built the **same** uid `X` holding only `Other.png`. `AUTO-ICONSET-SPEC.md` §0
requires one UID to mean one resolvable set of `{uid}/{group}/{filename}` strings.

Verify on a build:
- a modern-layout renderer yields its categories rather than falling through to `defaultSymbol`
- renderer order is preserved (§5.3 assigns `_2`/`_3` collision suffixes by it)
- a class covering several `values` expands to one entry per value
- multi-field keys join on `fieldDelimiter`
- the generated UID matches WinTAK's for the same layer

**One deliberate behaviour drift to confirm is acceptable:** `ex.declared` in `AutoIconset`
previously counted all `uniqueValueInfos` including symbol-less ones; it now counts only
symbol-bearing categories, because the enumerator drops the others. Side effect — the
`"(absent)"` entry in the `skipped` diagnostic set is no longer reachable from the uniqueValue
branch. Harmless, but it is a change in a diagnostic, so it should be a decision rather than a
surprise.

---

## 3. Check CloudTAK and TAK Portal for the same renderer gap

**Status:** not investigated.

Both implement `AUTO-ICONSET-SPEC.md` and both likely read only the classic `uniqueValueInfos`
layout. If so they have the same divergence item 2 just closed between ATAK and WinTAK, and the
same silent failure mode: a modern renderer looks like a renderer with no categories, and every
feature gets one symbol with nothing logged.

Start at `TAKPortal/services/featurelinkArcgisIconset.service.js` and
`CloudTAK/plugin/lib/autoSymbology.ts`. The shared golden vectors at
`TAKPortal/test/fixtures/auto-iconset-golden-vectors.json` are the conformance harness; consider
adding modern-layout cases to them so all four platforms are pinned by one fixture.

---

## Already done on ATAK — do NOT re-port from WinTAK

Recorded because the WinTAK work made it easy to assume otherwise. ATAK is **ahead** here:

| | ATAK | WinTAK |
|---|---|---|
| Sublayer enumeration (C-08) | ✅ `ArcGISRestClient.listSubLayers`; `ArcGISLayer.layerId` persisted; URLs always fully qualified | ❌ appends `/0`, nothing enumerates |
| Item-level renderer override | ✅ `ArcGISRestClient.fetchItemRenderer` | ✅ ported 2026-09-19 |
| Extraction diagnostics | ✅ `AutoIconset.logExtraction` | ✅ ported 2026-09-19 |
| classBreaks: refuse rather than mislead | ✅ | ✅ aligned 2026-09-19 |

The sublayer row is the notable one: **WinTAK still downloads only sublayer 0 of any service**,
which ATAK fixed under C-08. That is a WinTAK task, not an ATAK one, and it is the largest known
open defect on that platform.
