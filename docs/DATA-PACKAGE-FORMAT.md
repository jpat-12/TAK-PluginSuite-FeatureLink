# FeatureLink Data Packages

**Status:** implemented in WinTAK 5.6 as of 2.11.0. Not yet implemented in ATAK, CloudTAK or
TAK Portal — this document is the contract those ports should follow.

---

## What it is and why

The single-layer **Share** button sends one `.featurelinkshare` JSON file to one contact. That
file names a layer URL and its display config, and the recipient's plugin downloads the features
from ArcGIS itself.

A data package does the same for **many layers at once**, and adds the part the share cannot
carry: the **iconset zips this machine generated** for those layers. That is the whole reason the
feature exists. A peer derives the same iconset UID from the same layer URL — the UID is
`sha256(canonicalUrl + "/" + fieldName)`, identical across all four implementations — so the
reference in a marker's `usericon` resolves correctly. But it only resolves once the iconset
*exists* on their machine, and it only gets there after they have downloaded that layer from
ArcGIS themselves. Bundling the zips is what makes markers render for a peer who cannot reach the
portal at all.

---

## Package layout

A package is an ordinary zip:

```
FeatureLink-Ground_Teams-20260920-1234.zip
├── MANIFEST/manifest.xml
├── featurelink/
│   ├── Ground_Teams-a1b2c3d4e5f6.featurelinkshare
│   └── ICP-9f8e7d6c5b4a.featurelinkshare
├── cot/
│   ├── <feature-uid>.cot
│   └── <feature-uid>.cot
└── iconsets/
    ├── <64-hex-uid>.zip
    └── <64-hex-uid>.zip
```

- **`featurelink/`** — one `.featurelinkshare` per selected layer, byte-identical to what the
  Share button sends. The file stem is the layer name sanitized to `[A-Za-z0-9_-]`, capped at 48
  characters, plus a 12-hex-character hash of the layer URL. The hash is not decoration: two
  layers can carry the same display name (the same service published twice, or two long names
  truncated to the same 48 characters) and a duplicate entry path would mean one silently
  overwriting the other inside the zip. A counter suffix (`_2`) disambiguates even a hash
  collision.
- **`cot/`** — one `.cot` file per **individually selected feature**. Present only when the
  operator picked features rather than whole layers. A layer config names a *service*, and a
  service cannot express "these nineteen of its markers" — so a subset can only travel as the
  points themselves. Each is built through the same `BuildFeatureCotEvent` call the map is drawn
  from, using the style captured when the feature was plotted, so the recipient gets the marker
  the sender is looking at rather than an approximation of it. A layer with no `cot/` entries
  means "the whole layer": the recipient downloads it from ArcGIS, which is the original Share
  behaviour.
- **`iconsets/`** — the generated zips, named by their UID, exactly as
  `IconsetInstaller` wrote them to `%AppData%\WinTAK\FeatureLink\iconsets\`. An iconset referenced
  by two selected layers is included once.

## Manifest

Standard TAK manifest, format version 2, at the fixed path `MANIFEST/manifest.xml`:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<MissionPackageManifest version="2">
  <Configuration>
    <Parameter name="uid" value="featurelink-pkg-a1b2c3d4e5f6" />
    <Parameter name="name" value="FeatureLink-Ground_Teams-20260920-1234" />
    <Parameter name="onReceiveImport" value="true" />
    <Parameter name="onReceiveDelete" value="false" />
  </Configuration>
  <Contents>
    <Content ignore="false" zipEntry="featurelink/Ground_Teams-a1b2c3d4e5f6.featurelinkshare">
      <Parameter name="uid" value="featurelink-cfg-a1b2c3d4e5f6" />
    </Content>
    <Content ignore="false" zipEntry="iconsets/&lt;uid&gt;.zip">
      <Parameter name="uid" value="featurelink-iconset-&lt;uid&gt;" />
    </Content>
  </Contents>
</MissionPackageManifest>
```

Two values are load-bearing:

- **`onReceiveImport=true`** — the recipient's client imports the contents rather than merely
  filing the zip.
- **`onReceiveDelete=false`** — the zip must survive import. The iconsets inside are referenced by
  markers for as long as the layer is on the recipient's map, so deleting the package would strip
  symbology from exactly the offline peer the package exists for.

The **manifest lists only what was actually written**. If an iconset's source file could not be
read, the entry is dropped from both the zip and the manifest, rather than telling a recipient to
import something absent.

## Naming

Automatic, and always timestamped `yyyyMMdd-HHmm`:

| Selection | Name |
|---|---|
| one layer | `FeatureLink-<LayerName>-<stamp>` |
| several layers | `FeatureLink-<N>-layers-<stamp>` |
| nothing nameable | `FeatureLink-layers-<stamp>` |

Beyond one layer, listing them makes an unreadably long name; the count is what the recipient can
use, and the manifest carries the full list. The timestamp is always present because packages are
identified by name in the recipient's package list — two sends of the same layers an hour apart
must not look like one item.

The operator may overwrite the name. Once they type in the box, the automatic name stops
overwriting their text, so checking one more layer cannot silently discard what they just typed.

## Package UID

`featurelink-pkg-` + a 12-hex-character hash of the package name and the sorted entry paths. Same
name and same contents produce the same UID, so re-sending an edited package replaces rather than
duplicates in the recipient's list; different contents produce a different UID.

---

## The selection workflow

The **PACKAGE** tab (between LAYERS and PLI) runs a four-step workflow. It is also reachable from
a button on each layer row — which pre-selects that layer's features — and from the package button
beside the account icon in the header.

1. **Name** — pre-filled automatically, and the automatic name stops overwriting the box the
   moment the operator types in it.
2. **Select** — three ways, which compose rather than replace each other:
   - *Click points* — click features on the map to add or remove them individually.
   - *Draw area* — drag a rectangle; everything inside is added.
   - *Draw radius* — drag out from a centre; everything inside is added.
   - *Add a whole layer* — for when the map is not the convenient way to say it.
3. **Review** — the selection, with Find (centres the map on it) and Remove per row. **Back**
   returns to step 2 with everything still selected: the selection is keyed by feature UID, so
   re-drawing over ground already covered adds only what is new rather than toggling existing
   picks back off. That is the "add" half of add-and-remove, and it is tested.
4. **Send or save** — two peers, not a primary and a fallback:
   - *Send to selected contacts* — needs at least one contact ticked.
   - *Create package without sending* — needs only a selection. **Choosing contacts is
     optional.**

### Contacts are optional

Creating the package without picking anybody is a supported outcome, not a consolation prize for
when there is nobody to send to. On a disconnected network, handing over a file is often the only
way to move data at all — so the save path needs only a selection, the button is always live once
something is selected, and a hint under the contact list says so whether the list is empty or
merely unticked.

Both outcomes close the workflow, because both are completions. A **failure** leaves the selection
intact so the operator can retry without rebuilding it — that is the case that matters, since a
hand-picked selection of points is the expensive thing to recreate. Backing out of the file dialog
reports nothing and changes nothing.

### Why the steps are cards in a tab, not a wizard dialog

Selecting features *on the map* is the point of the workflow, and a modal window over the map
would defeat it. The steps are cards in the existing panel so the map stays visible and clickable
throughout.

### The map is made modal while a selection mode is active

`PushMapEvents`/`PopMapEvents` suppress everything the mode does not need — otherwise a drag to
draw a box also pans the map underneath, and a click to pick a marker also opens WinTAK's wheel
menu.

The matching Pop is the dangerous half. Miss it and the operator's map stays unresponsive with
nothing on screen explaining why, which is worse than the feature not existing. Every exit path
runs through `MapAreaSelector.Stop()`, handlers are detached independently of the Pop so a throw
in one cannot strand it, the class is `IDisposable`, and a failed Pop logs at error level naming
the consequence.

### Geometry

Selection arithmetic is SDK-free and tested (`FeatureSelectionTests`), because a feature wrongly
excluded from a drawn area does not announce itself: the package sends, the status line is green,
and a searcher's marker simply is not in it.

- **Distance is haversine**, not the cheaper equirectangular approximation. At the
  hundred-kilometre scale a search area reaches, the flat approximation is wrong by enough to drop
  or add features near the edge.
- **Boundaries are inclusive.** A feature exactly on the edge of the box or radius is in — the
  operator drew the line through it, and silently dropping it is the invisible failure.
- **A box drawn across the antimeridian selects the strip drawn**, not the rest of the world. A
  naive min/max on the two corners would select everything *except* the strip.

---

## Implementation notes

### Why this does not use `WinTak.MissionPackages`

The SDK exposes a package builder — `MissionPackage` + `FilePackageContent` + `Save()` — and using
it would be the obvious route. It is deliberately not used:

1. It would add an **eighth SDK assembly reference** to a project CI already cannot compile,
   widening the surface that only builds on a licensed workstation.
2. The manifest is the one part of this feature every other TAK client must agree with, which
   makes it exactly the part worth having under test. The format above is asserted against
   fixtures in `DataPackageTests`; a call into a host type could only be verified by running
   WinTAK.

Only `ICommunicationService.SendMissionPackage(List<string>, FileInfo, string, bool)` touches the
host — already referenced, and already used by the single-layer Share button. It takes a **list**
of contact UIDs, so multi-recipient send needed no new host surface.

### The persisted `IconsetUids` key

`ArcGisLayer.AutoSymJson` is deliberately **not** persisted: it is re-derived from live metadata on
every download so it can never go stale against a renderer the layer owner edited. But that means
a layer's icon references only exist in memory, for layers synced in the current session.

A package built after a restart would therefore bundle icons only for layers already re-synced —
which is precisely when an operator packaging data for an offline peer is least likely to have
re-synced anything. So `ArcGisLayer` gained a persisted `IconsetUids` key (semicolon-separated),
written when `IconsetInstaller` succeeds. It is a record of what is on **disk**, and the zips
outlive the session.

The planner reads both sources and unions them, so neither alone can lose icons.

### The retained per-feature style

`LayerFeature` carries the icon path, colour, label and remarks **resolved at plot time**, so a
package can rebuild each feature's CoT exactly as the map drew it.

The alternative was keeping every feature's full ArcGIS attribute dictionary and re-resolving at
package time. That is the same answer at many times the memory — a 50,000-feature layer would hold
50,000 dictionaries — and it would re-resolve against a display config that may have been
refreshed since, so a package could disagree with the markers the operator is looking at. These
fields *are* the resolution result, captured when it was applied.

`ArcGisLayer.AllFeatures` holds the uncapped pool the selection draws on, separate from
`Features`, which stays capped at 500 because it is realized into a scrolling panel. The cap would
buy nothing here and would cost correctness: an operator dragging a box round the south end of an
8,000-feature layer must select what is inside it, not whichever of the first 500 happen to fall
there.

### What is excluded

Layers that have never been downloaded (`LastSyncTicks == 0`) do not appear in the picker. They
have no symbology derived and no icons generated, so packaging one would ship a bare URL — and a
recipient who cannot reach ArcGIS would get nothing at all.

---

## Not yet verified

**Read this before relying on the feature in the field.**

1. **No package has been received by a peer.** The format is tested; the round trip is not. The
   single-layer Share button it builds on has itself never been exercised against a live contact.
2. **`onReceiveImport` behaviour on the recipient side is assumed, not observed** — specifically,
   whether ATAK and WinTAK both route a bundled iconset zip into the icon database on import, or
   whether the `.featurelinkshare` import path needs to install it explicitly.
3. **Cross-platform:** ATAK, CloudTAK and TAK Portal neither produce nor consume these packages
   yet.

4. **The map selection modes have not been exercised against a live map.** `PushMapEvents`,
   `PopMapEvents` and `MapMouseEventArgs.WorldLocation` are all **undocumented** in the 5.6 SDK
   reference — they appear in the API listing with no prose — so their exact semantics are
   inferred. The specific unknowns: whether `PushMapEvents(toKeep)` suppresses panning as
   intended, and whether `MapMouseUp` fires with a usable `WorldLocation` at the end of a drag.
   Both fail visibly rather than silently, and `Stop()` is defensive, but **the first thing to
   check in the field is that the map still pans normally after leaving a selection mode.**

The smoke sequence to close (1) and (2): select two layers with different renderers → send to one
contact → on the receiving machine confirm both layers appear, then confirm markers render with
the *correct per-value icons* rather than defaults, **with the network to ArcGIS blocked**. The
last condition is the whole test; with the portal reachable the recipient regenerates the iconsets
itself and a broken package looks like a working one.
