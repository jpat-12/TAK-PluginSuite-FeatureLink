# PR: `dev` → `main` — FeatureLink 2.9.0

**68 commits · 216 files · +43,430 / −3,836**

Two bodies of work: the audit remediation already merged into `dev` (PR #3 and the CI follow-ups),
and a field-driven WinTAK session on 18–19 Sep 2026 that fixed the symbology defect and several
things found alongside it. This describes both, but the second in more detail because it is the
part that has not been reviewed before.

---

## The short version

A WinTAK operator reported three things: layers would not add, symbology never came over, and
large layers nagged on every refresh. All three are fixed. Chasing the symbology defect took
**four attempts**, because three plausible causes were each real bugs that were not *the* bug. All
four fixes are in — the first three matter for other layers even though they did not explain this
one.

The headline: **WinTAK 5.6 now builds.** It had never been compiled before this session.

---

## Part 1 — WinTAK now builds and is tested

| | Before | After |
|---|---|---|
| Compiles | never attempted — no SDK on the authoring machine | clean, **0 errors / 0 warnings** |
| Tests | 216, none running in CI | **469**, running in CI |
| Packaging | unverified | `.wpk` passes the repo's own `check_wpk.py` |

`docs/testing/wintak.md` predicted *"the first thing you will hit is a compile error"*. It built
clean on the first attempt — roughly 900 lines of C-07 symbology written without a compiler turned
out to be correct.

**CI:** a new `wintak-tests` job runs the SDK-free suite with no secrets. Those 216 tests existed
but never executed, because they were wired into a job that hard-fails without the licensed WinTAK
SDK — a suite gated on an SDK it does not use. That job also needed removing from public CI, where
it could only ever fail; splitting the two is what made the removal safe.

---

## Part 2 — the symbology defect, and the three wrong turns

Worth reading in order, because each attempt narrowed the next.

### Attempt 1 — the missing iconset pipeline *(real bug, not the cause)*

WinTAK could emit `<usericon iconsetpath>` on a marker but nothing ever **created** the iconset
that path named. ATAK had `AutoIconset.java`; WinTAK had no counterpart, so every picture-marker
layer fell back to default icons and said so once per sync.

Ported as `Services/AutoIconset.cs` (spec-conformant: canonicalization, UID hash, group/file
naming, collisions, `iconset.xml`, zip) plus a thin `Services/IconsetInstaller.cs` calling
`IconsetDatabaseManager.ImportIconsetZip`.

**Verified in the field:** WinTAK honours the UID declared in our `iconset.xml` rather than hashing
the zip, so icons resolve cross-platform. That was the highest-risk unknown in the whole feature.

62 tests, most driven off `TAKPortal/test/fixtures/auto-iconset-golden-vectors.json` — the same
fixture ATAK and TAK Portal conform to. These functions must be **byte-identical across four
implementations**; a locally sensible divergence would silently stop this device's icons resolving
against everyone else's with nothing looking wrong locally.

### Attempt 2 — icons and colours treated as alternatives *(real bug, not the cause)*

Finding any picture symbol suppressed the colour config entirely, so a renderer mixing badges and
coloured circles produced one icon stamped on everything and no colours. Precedence is now **per
value, not per layer** — the config schema always supported it.

### Attempt 3 — the modern renderer layout *(real bug, not the cause)*

Both extractors read only the classic flat `uniqueValueInfos`. Modern ArcGIS Online publishes
`uniqueValueGroups[].classes[]`. A twelve-category renderer therefore looked like a renderer with
**none** — both extractors fell through to `defaultSymbol` and one icon went on every feature.
Nothing threw and nothing logged, because for a renderer with no categories the code behaved
exactly right.

**This one opened a cross-platform divergence** and closing it is why ATAK changed too. The
iconset UID is identical on both platforms, so WinTAK was building uid `X` with twelve icons while
ATAK built the **same** uid `X` with one. `AUTO-ICONSET-SPEC.md` §0 requires one UID to mean one
resolvable set of strings. `enumerateValueEntries` is now in both.

### Attempt 4 — the actual cause: item-level renderer overrides

The diagnostic added in attempt 3 found it in one line:

```
Layer "TEST-CoT-Visualization100" renderer 'simple': declared=0 extracted=1
```

A `simple` renderer has exactly one symbol by definition. Styling a hosted layer on its portal
item's **Visualization** tab does not modify the feature service — ArcGIS saves it to the *item*,
at `/sharing/rest/content/items/{id}/data`. The service keeps reporting `simple`.

ATAK had solved this already (`ArcGISRestClient.fetchItemRenderer`), and its own comment describes
the exact symptom. Ported; `ArcGisLayer` gained a persisted `ItemId`, captured from the portal
search that was already receiving it and discarding it.

> **The lesson worth keeping:** attempts 1–3 were found by reading and were each wrong about this
> layer. Attempt 4 was found by a log line, in seconds. The diagnostic was the highest-value change
> in the whole sequence, and it was ported from ATAK, which had it all along.

---

## Part 3 — everything else in this PR

### Fixed

- **Add Layer passed a null token unconditionally**, so adding a layer from your own org could
  never work while signed in. Now tries anonymously first (that is what determines "public"),
  retries with the token on an auth failure, and files the layer as private so recurring syncs keep
  authenticating.
- **Large layers prompted on every 15-second recurrence tick.** Answer is remembered per layer and
  persisted; a background refresh raises the dialog **once per session**, an explicit Sync always
  asks. (First attempt dropped to a status-bar line instead — invisible, and worse than the nag.)
- **`FeatureCount` was written in one place only** — the Home-tab stats refresh, which skips the
  browse list. A layer could plot 29 markers and read "0 features" forever. Also `0` was doing
  double duty for *empty* and *never counted*; uncounted now reads `— features`.
- **Markers were named `Feature-0…N`** — a position in the result set, unstable across queries.
  Now uses the layer's own `displayField`, which is what ArcGIS labels features with. Skips a
  `displayField` pointing at the object id, and skips blank/`<Null>` values.
- **classBreaks fell back to `classBreakInfos[0]`** — the *lowest* bucket — styling the whole layer
  as though every feature sat in it. ATAK refuses deliberately; WinTAK now matches. *(A
  pre-existing test asserted the old behaviour and was rewritten.)*
- **Log sink did open/write/close per line under a global lock**, so a large sync paid tens of
  thousands of file-handle cycles on the download thread. Held-open writer; per-feature warnings
  coalesce into one line per layer.
- **Iconsets were reused on a UID match.** The UID hashes the layer URL and field, *not* the
  symbols, so a set installed by a build that mis-read the renderer survived two subsequent fixes
  to the reader. Always removed and re-imported now.

### Added

- **Zoom to layer** — clicking the title frames the layer's plotted features. Extent computed from
  downloaded features (already WGS84; describes what is on the map, not the whole source layer),
  with a `cos(latitude)` correction and a resolution floor.
- **Feature list** — a chevron expands the layer's features; clicking one centres the map.
  Virtualized, capped at 500, not persisted.
- **`ErrorText`** — every failure now says what happened *and* what to do. The message that
  motivated it appeared eleven times in ninety seconds while the operator retried one URL in five
  shapes: `Could not load layer from URL: Token Required — Token Required`. It named no cause and
  suggested no action, and the old code appended `(sign in again)` to every auth failure — actively
  wrong advice when you already are.
- **Multi-account foundation** (no UI yet): `ArcGisOAuth` extraction, `ArcGisAccountKey`
  (`portalUrl|username` — the same username exists on every Enterprise portal), `TokenBlobFormat`
  writing a **superset** document so an older build still finds the legacy fields and keeps working
  instead of silently signing the user out, and `OwnerAccountKey` on each layer.

---

## Risk and what is not verified

**Read this before merging.**

1. **The ATAK change is uncompiled.** No ATAK SDK on the machine, no ATAK test suite in the repo,
   and CI no longer builds ATAK. Reviewed by reading — `org.json` + `java.util` only, null-guarded,
   no streams or `var` — and both trees verified byte-identical. **Needs a Gradle build before it
   ships.** `docs/ATAK-TODO.md` records this and the rest.

2. **Zoom-to-layer is unverified in the host.** It needed a seventh SDK reference,
   `WinTak.Display`, for `IMapViewController` — none of the six the port already referenced exposes
   any route to the camera. It is imported as a **property with `AllowDefault`**, not through the
   constructor, precisely so an unsatisfied import cannot fail MEF composition and silently prevent
   the whole dock pane from loading. Unknown: whether WinTAK exports the contract at all, and
   whether `LookAt`'s second argument is metres-per-pixel as assumed. Both fail safe.

3. **Compiling is not running.** Nothing in WinTAK5.6 has been exercised against a live map beyond
   the operator's own testing during the session. `docs/testing/wintak-iconset.md` has the smoke
   sequence; its §3 (UID match) is the check that matters, because a mismatch looks correct locally
   and breaks every peer.

4. **Known and unfixed: WinTAK reads only sublayer 0.** `EnsureLayerIndex` appends `/0` and nothing
   enumerates a service's sublayers, so a multi-layer service downloads one layer. ATAK fixed this
   under C-08 and has `listSubLayers`. **This is the largest open defect on the WinTAK side** and
   explains both "most layers aren't downloading" and `Layer_Line` returning 0 features.

5. **Not investigated:** whether CloudTAK and TAKPortal still read only the classic renderer
   layout. They implement the same spec and probably have the same gap.

---

## Versioning

**2.9.0.** `VERSIONING.md` §1.1 makes a new user-visible capability and a new persisted schema key
MINOR, and a minor bump resets PATCH. All ten declarations agree; `WinTAK5.7` stays `0.9.0-pre`
under the tracked C-17 exemption.

One deliberate deviation: §1.1 also couples a persisted-schema key to a `CONFIG-FORMAT.md`
`_version` bump. Not done, on purpose — `CONFIG-FORMAT.md` governs the configurator→plugin wire
payload, and the new keys (`LargeDownloadAccepted`, `OwnerAccountKey`, `ItemId`, `Extent`,
`FeatureCountKnown`) are local `settings.xml` state that never appears in a share. Bumping it would
falsely signal a wire change to ATAK, CloudTAK and Portal.

---

## Verification performed

- WinTAK 5.6 Release x64 — **0 errors / 0 warnings**, clean rebuild
- **469/469** tests
- `check_wpk.py` — OK, `Newtonsoft.Json.dll` present (C-18)
- `check_versions.py` — OK, four documented exemptions
- Iconset install confirmed against a live WinTAK: **no UID mismatch**
