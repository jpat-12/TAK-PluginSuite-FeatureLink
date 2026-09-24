# PR: `feature/wintak-datapackage-workflow` → `dev`

**13 commits · 38 files · +7,115 / −96 · 469 → 611 tests**

Adds a data-package workflow to the WinTAK plugin: pick features off the map, bundle them with
the iconsets this machine generated, send to contacts or save to a file. Along the way it fixes
six defects, four of which were already shipping and three of which failed **silently**.

---

## The short version

An operator can now select individual features — by clicking them, dragging a box, or drawing a
radius — review the selection, and send it to contacts as a TAK data package that carries the
CoT *and* the icons. Packages land in WinTAK's own Data Packages folder and are listed in a new
**PACKAGES** tab.

The feature took four field iterations to get right. Two of the fixes below were found only
because a test on a real device contradicted what the code and the SDK binaries implied.

---

## Part 1 — the workflow

**PACKAGES tab**, between LAYERS and PLI, plus a button on each layer row and one beside the
account icon. Four steps:

1. **Name** — filled automatically from the selection and the time; stops auto-filling the moment
   the operator types.
2. **Select** — click individual features, drag an area, drag a radius, or add a whole layer.
   The methods compose rather than replace.
3. **Review** — every feature listed with its source layer and position, with *Find* and *Remove*.
   **Back** returns to selection with everything still chosen, so re-drawing only adds.
4. **Send or save** — two peers. Contacts are **optional**; creating the package without sending
   is a supported outcome, not a fallback.

Under it, a **Built packages** listing: name, contents, size, time, with Send / Show / Delete.
Read back off disk each refresh rather than remembered, so a package deleted elsewhere, moved, or
left from a previous run all read correctly.

### Design notes

- **Selection geometry is SDK-free and tested.** A feature wrongly excluded from a drawn area
  never announces itself — the send succeeds, the status line is green, and a marker simply is not
  there. Haversine not equirectangular; inclusive boundaries; a box drawn across the antimeridian
  selects the strip drawn rather than the rest of the world.
- **The map goes modal during selection** (`PushMapEvents`/`PopMapEvents`), or a drag to draw a box
  also pans the map. The matching Pop is the dangerous half: every exit runs through `Stop()`,
  handlers detach independently so a throw cannot strand it, and a failed Pop logs at error naming
  the consequence.
- **Packaged CoT is built by the same call the map is drawn from.** `BuildFeatureCotEvent` was
  extracted from the posting path for exactly this — a second implementation of the styling rules
  would have drifted.
- **Three view models, not one.** `DataPackageWorkflow` and `PackageLibraryViewModel` are separate
  from `FeatureLinkDockPane`, which is already the largest file in the port.

---

## Part 2 — the defects fixed

### Iconsets never reached the recipient *(found in the field, got wrong twice)*

The headline. Three shapes were tried; **two fail silently** — the package imports, the markers
appear, and only the symbols are missing:

| Shape | Result |
|---|---|
| zipped **+ a `uid` Parameter** | filed under `atak/attachments/<uid>/`; no importer ever sees it |
| **expanded** into the package | files extract, then ATAK treats each PNG as a standalone image and prompts the operator to place them one by one |
| **zipped, no `uid`** | **works** |

Two rules, both needed:

1. **Leave the iconset zipped.** Unpacking it ourselves robs ATAK's iconset importer of the `.zip`
   it recognises.
2. **No `uid` Parameter on non-CoT content.** In ATAK a `uid` means *"this content is an attachment
   of the CoT item with that uid"*. Ours matched no item, so the iconset **and** the layer config
   became orphan folders.

> **The lesson worth keeping.** The on-device layout of a working iconset —
> `files/<guid>/iconset.xml` beside `<Group>/*.png` — was read as the shape the *package* needs.
> It is the importer's **output**, not its input. Two iterations were spent making the package
> look like the answer instead of the question.

### A manifest that declared the wrong encoding

Manifests went out saying `encoding="utf-16"` while the bytes were UTF-8, because
`XDocument.Save(TextWriter)` takes its declaration from the writer and a plain `StringWriter`
reports UTF-16. **A round-trip test passed over it** — parsing a *string* ignores the declaration;
a recipient reads the manifest out of the zip as a *stream*, where it is fatal. Packages built
before this should be rebuilt.

### A GPU texture purge on the wrong thread *(host crash)*

`NullReferenceException` in `WinTak.CursorOnTarget.Graphics.CotMapMarker.OnHoverChanged`, reported
during iconset testing. `IconsetInstaller.PurgeRendererTextures` ran on the layer-download worker
and purged the **global** texture cache, tearing textures out from under markers the renderer was
still drawing. The 5.6 SDK's `RenderContext` documents the affinity and supplies both the test
(`IsRenderThread`) and the marshal (`QueueEvent`). With no render context reachable it now does not
purge at all — stale icons for a session are cosmetic; crashing the host is not.

### Marker uids churned on refresh, invalidating packages

A packaged `.cot` names each feature by uid, so a uid that changes on refresh means rebuilding the
package every sync. Two of three derivation tiers were already stable; the third hashed
**attributes + geometry**, so editing any attribute — including the symbology field an operator
edits precisely to change how a marker looks — produced a new uid. Now anchored to position alone,
rounded to ~11 mm.

*Honest limit:* moving a feature still renames it on that tier. Position **is** the identity when
nothing better exists; the fix for a layer you control is to add a `uid` column.

### The panel claimed sends that had not happened

`SendMissionPackage` returns as soon as the transfer is **queued** and fails asynchronously, so
reporting success on return was claiming an unobserved outcome — the panel said "Sent" while
WinTAK said "Failed to send Data Package". Now subscribes to `MissionPackageSent` /
`MissionPackageTransferFailed`, says "Sending…" until one fires, and translates the raw Commo code
into something actionable.

### Contacts with no route were offered as recipients

The list came from `AllContacts` — every contact ever seen. Commo refuses the **whole** transfer
when it cannot resolve an endpoint for **any** destination, so one stale contact failed the send.
Now filtered to the live set with a `CurrentConnector`.

### A tenth version declaration CI did not police

`WpkVersion` was hard-coded in the csproj, so the `.wpk` filename could disagree with `VERSION` —
the artifact an operator installs announcing a version the build was not. It now reads `VERSION` at
build time. *(Caught when a rebuild after a bump still produced the old filename.)*

---

## Tests

**469 → 611.** New suites: `DataPackageTests`, `FeatureSelectionTests`, `PackageLibraryTests`, plus
additions to `ErrorTextTests` and `UidStabilityTests`.

`XamlBindingSmokeTests` now parses all three view models rather than allowlisting ~40 member names,
which would have switched the guard off for the largest page in the panel. It caught unbound
bindings three times during this work.

**Three pre-existing tests asserted behaviour that turned out to be the defect** and were inverted,
each with the reasoning recorded on it:

- every content element carries a `uid` — that routing *was* the bug
- editing an attribute yields a different uid — that churn *was* the bug
- an iconset travels expanded — superseded by the field result

---

## Risk and what is not verified

1. **The end-to-end send still fails in this environment, for reasons outside the plugin.** Commo
   reports `MP Send init failed to find usable endpoints for any of the given destination contacts`.
   Decompilation confirmed the plugin calls the only API, with the correct identifiers, and that
   `SendableFile.Send` — WinTAK's own path — compiles to the identical call. The test machine's
   server had negotiated down to XML-only, a second server was unreachable, and a client
   certificate had expired. **Nothing plugin-side fixes this.**
2. **Iconset delivery is verified; the whole-package round trip is not.** A hand-built package in
   the final shape imported into ATAK with its icons. A package built by *this code* has not yet
   been round-tripped — the shape is asserted by tests, but confirming the two agree is the next
   field step.
3. **Map selection modes are built on undocumented SDK surface.** `PushMapEvents`, `PopMapEvents`
   and `MapMouseEventArgs.WorldLocation` appear in the 5.6 reference with no prose. Failure is
   visible rather than silent and `Stop()` is defensive, but **the first field check is that the
   map still pans normally after leaving a selection mode**.
4. **`WinTak.MissionPackages` is an eighth SDK reference**, imported with `AllowDefault` so an
   unsatisfied import costs only the host listing. CI still cannot compile WinTAK.
5. **ATAK, CloudTAK and TAK Portal neither produce nor consume these packages**, and
   `docs/DATA-PACKAGE-FORMAT.md` is the contract for when they do.

---

## Versioning

**2.12.7.** Every bump on this branch is PATCH per Jon's standing preference, which overrides
`VERSIONING.md` §1.1 (a new user-visible capability would otherwise be MINOR). Worth resolving
deliberately: §1.1 and current practice now disagree, and the doc is normative and CI-enforced.

---

## Verification performed

- WinTAK 5.6 Release x64 — **0 errors / 0 warnings**
- **611/611** tests
- `check_versions.py` — OK, documented exemptions only
- `check_wpk.py` — OK
- Field: iconset delivery into ATAK confirmed on 24 Sep 2026
