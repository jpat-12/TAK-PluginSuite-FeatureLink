# FeatureLink Data Packages

**Status:** implemented in WinTAK 5.6 as of 2.10.0. Not yet implemented in ATAK, CloudTAK or
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

The smoke sequence to close (1) and (2): select two layers with different renderers → send to one
contact → on the receiving machine confirm both layers appear, then confirm markers render with
the *correct per-value icons* rather than defaults, **with the network to ArcGIS blocked**. The
last condition is the whole test; with the portal reachable the recipient regenerates the iconsets
itself and a broken package looks like a working one.
