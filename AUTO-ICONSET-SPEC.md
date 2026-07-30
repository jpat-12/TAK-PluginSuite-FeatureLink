# AUTO-ICONSET-SPEC.md — the frozen cross-platform iconset contract

**Version: 1** &nbsp;·&nbsp; Sibling to `CONFIG-FORMAT.md`, versioned the same way.

This document is the single source of truth for how every FeatureLink surface (ATAK, WinTAK,
CloudTAK, TAK Portal) turns one ArcGIS Web Map / FeatureServer link into an ATAK-compatible
iconset. It exists so that four independent implementations — in Java, C#, TypeScript, and
Node — produce **string-identical** iconset references (`{uid}/{group}/{filename}`) for the
same source layer, without ever talking to each other.

> **Hand-synchronized, nothing enforces it at build time.** Exactly like the display-config
> contract in `CONFIG-FORMAT.md`, this is a contract between independent producers/consumers.
> Changing *any* rule below is a **breaking change** to every already-generated or already-shared
> iconset — bump the version marker (`version="1"` in `iconset.xml`, and the header above) and
> treat old and new as different sets. See §8.

---

## 0. Why string-identity is the whole game (read this first)

`<usericon iconsetpath="{uid}/{group}/{filename}">` is resolved **by string**, not by image
content. This was confirmed by decompiling ATAK 5.6's `com.atakmap.android.icons.IconsMapAdapter.addIconset()`
(see `TAKPortal/services/featurelinkCustomIcons.service.js` for the annotated findings). The two
facts that make federated generation work:

1. **UID comes from `iconset.xml`, verbatim.** When a zip's `iconset.xml` parses with a
   non-empty `name` **and** `uid` (ATAK's `UserIconSet.isValid()`), ATAK trusts that `uid`
   string exactly as written. Only when `iconset.xml` is missing/empty/invalid does ATAK fall
   back to `sha256(raw zip bytes)`.

   → **Consequence:** if every platform writes the *same computed UID* into `iconset.xml`, every
   platform's iconset resolves to the same UID **even though the PNG bytes and zip bytes differ**.
   A Java resize and a Node passthrough never produce identical bytes; they don't need to. **The
   deterministic UID MUST be written into `iconset.xml` — never rely on the zip-hash fallback,
   which would make federated generation diverge.**

2. **group and filename come from the zip entry path, verbatim.** For every image entry, ATAK
   derives `group` = the entry's **first path segment** (original case) — or the literal string
   `Other` if the entry has no folder — and `filename` = the entry's **last path segment**
   (original case, spaces and all, **not** sanitized by ATAK). Neither value comes from
   `iconset.xml`.

   → **Consequence:** the iconset path is controlled entirely by *how we name the zip entries*.
   Every platform MUST lay out entries as `{group}/{filename}` using the identical `{group}` and
   `{filename}` strings computed by §4–§5. ATAK does no sanitization of its own, so the strings we
   write ARE the strings that get matched. Any sanitization below exists to make our four
   implementations agree and to stay filesystem-safe — not to mirror an ATAK pass.

Everything else in this spec is just pinning down exactly what those computed strings are.

---

## 1. Inputs

A generator is invoked with:

- **`sourceUrl`** (required) — a user-pasted link. One of:
  - a FeatureServer service root: `https://…/FeatureServer`
  - a specific layer: `https://…/FeatureServer/{layerId}`
  - a Web Map item link (portal item id) — see §2.3.
- **`fieldOverride`** (optional) — the attribute field whose values drive the renderer. When
  absent, the field is taken from the renderer JSON (§3). For a single-symbol renderer there is
  no field; use the empty string `""`.
- **`token`** (optional) — ArcGIS token for non-public layers.

---

## 2. Canonicalization of the source URL

Two users who paste **equivalent but textually different** links MUST converge on the same
canonical URL, because the UID (§4) hashes it. Apply these steps in order:

### 2.1 Normalize the raw URL
1. Trim surrounding whitespace.
2. Lowercase the **scheme** and **host** only. (Path case is preserved — ArcGIS service and
   folder names are case-sensitive in the REST path.)
3. Remove any `?query` and `#fragment`.
4. Remove a trailing `/`.
5. Collapse any `//` in the path (other than after the scheme) to `/`.

### 2.2 Resolve to the canonical layer form
The canonical form is exactly:

```
{scheme}://{host}{/path-to}/FeatureServer/{layerId}
```

- If the URL already ends in `/FeatureServer/{layerId}` (numeric `layerId`) → use as-is.
- If the URL ends in `/FeatureServer` (service root, no layer) → append `/0`. Layer `0` is the
  canonical default; a source with data on a non-zero layer MUST be pasted with its explicit
  `/{layerId}`.
- `MapServer` URLs follow the same rule with `MapServer` in place of `FeatureServer`.

### 2.3 Web Map links
A Web Map item (`…/home/item.html?id={itemId}` or `…/sharing/rest/content/items/{itemId}`) is
resolved by fetching the item's `…/data` (its operational-layers JSON) and, for the target
operational layer, reading its `url` (a FeatureServer/{id} URL) — then canonicalizing **that**
per §2.1–§2.2. **The group name (§4) is always derived from the FeatureServer layer's own
`name`, never from the web-map-level title or any web-map renderer override.** The override may
change the *renderer* used for symbol extraction, but the naming source stays the FeatureServer
layer. (Web-map resolution MAY be implemented incrementally; a generator that does not yet
resolve web maps MUST reject a web map link with a clear error rather than hashing it raw.)

The output of this section is **`canonicalUrl`**.

---

## 3. Fetching the renderer and the naming source

1. `GET {canonicalUrl}?f=json` → the layer metadata object.
2. **Naming source** = metadata `name` (a FeatureServer layer always carries its own `name`).
3. **Renderer** = metadata `drawingInfo.renderer`.
4. **Driving field**:
   - `uniqueValue` / `uniqueValueRenderer` → `renderer.field1` (fallback `renderer.field`).
   - single symbol (`simple`, or a bare `renderer.symbol`) → `""` (no field).
   - `classBreaks` → `renderer.field`. *(class-break icons are uncommon; extract only the
     `esriPMS` symbols that are present.)*
   - `fieldOverride`, when supplied, replaces the above.

Only **`esriPMS`** (picture-marker) symbols carry embedded bitmaps and are eligible for iconset
generation. `esriSMS` (simple-marker) symbols are handled by the display-config color/shape path
(`SymConfig.fromEsriRenderer`) and are **skipped** here.

---

## 4. UID formula (load-bearing)

```
uid = lowercase_hex( SHA-256( utf8( canonicalUrl + "/" + fieldName ) ) )
```

- `canonicalUrl` is the §2 output (which already contains `/FeatureServer/{layerId}`, so the
  layer id is part of the hash input).
- `fieldName` is the §3 driving field, or `""` for a single-symbol renderer.
- The separator is a literal forward slash `/`. ArcGIS field names never contain `/`.
- Output is the 64-character lowercase hex digest, used **verbatim** as the `uid` attribute of
  `iconset.xml`. ATAK accepts any non-empty string as a UID (§0); a hex digest is valid.

> This is exactly what the `arcgis_to_cot.py` POC computed (`sha256(f"{layer_url}/{layer_id}/{field}")`),
> now pinned as a contract. The POC's `{layer_url}/{layer_id}` corresponds to this spec's
> `canonicalUrl` (which ends in `FeatureServer/{layerId}`).

**Test vector** (compute once, must match on every platform):

```
canonicalUrl = https://services1.arcgis.com/abc/arcgis/rest/services/Damage/FeatureServer/0
fieldName    = damage_level
uid          = sha256("https://services1.arcgis.com/abc/arcgis/rest/services/Damage/FeatureServer/0/damage_level")
             = 9aa866980d8078ab2a4cbb84b5ad4de9b4c6af6d3192957e15b8ce459dfd2ad9
```

Each implementation MUST include a unit test asserting its `uid()` returns this exact hex string
for this input. (Digest computed from the frozen formula above; all ports copy it verbatim.)

---

## 5. Group name and filename derivation

### 5.1 Group name
```
group = sanitizeGroup( namingSourceName ) + " Icons"
```
- `namingSourceName` is the §3 layer `name`.
- `sanitizeGroup(s)`: replace every character **not** in `[A-Za-z0-9 _-]` with `_`; collapse runs
  of whitespace to a single space; trim; cap at 60 chars. (Matches `NAME_RE`/`cleanSetName` in
  `featurelinkCustomIcons.service.js`.)
- The literal ` Icons` suffix is appended **after** sanitization.
- ATAK uses this as the zip entry's first path segment, verbatim (§0.2).

### 5.2 Filename per renderer entry
For each `esriPMS` symbol, derive its file's base name from the renderer entry's label or value:

1. `raw` = the entry's `label` if non-empty, else its `value` (uniqueValue) / a stable
   description; for the single-symbol case use `defaultLabel` if present, else the fallback in §6.
2. Strip any embedded path prefix: keep only the segment after the last `/` or `\`.
3. Strip a trailing `.png` (case-insensitive) if present.
4. `sanitizeFile(base)`: replace every character **not** in `[A-Za-z0-9._-]` with `_`. (Matches
   `FILE_RE`.) Do **not** lowercase; do **not** strip spaces beyond the char-class replacement
   (a space becomes `_`).
5. Append `.png`.

### 5.3 Collision rule
Within one set, if a produced filename already exists, append `_2`, then `_3`, … before the
`.png` extension, incrementing until unique. (Matches `uniqueFileName()` — same `_N` scheme.)
Ordering is the renderer entry order, so the suffix assignment is deterministic across platforms
**provided every platform iterates renderer entries in the JSON's array order.** Implementations
MUST preserve renderer array order.

### 5.4 Resulting iconset path
```
iconsetpath = uid + "/" + group + "/" + filename
```
This is the string written into a display config's `usericonPath` / `<usericon iconsetpath>`, and
the string ATAK independently reconstructs from the installed zip. They match by construction.

---

## 6. Default / fallback icon

When a renderer has a default symbol (an `esriPMS` `defaultSymbol`, or a single-symbol renderer
with no label), its file is named:

```
Other.png   (when no defaultLabel is present)
```

If a `defaultLabel` **is** present, it is run through §5.2 like any other entry. `Other.png` sits
in the same `{group}/` folder as the rest (it is **not** placed at the zip root — that would make
ATAK assign it the group `Other` per §0.2; we want it in the layer's group).

---

## 7. iconset.xml

Every generated zip MUST contain an `iconset.xml` at the zip root:

```xml
<iconset name="{group}" uid="{uid}" defaultGroup="{group}" version="1">
  <icon name="{filename}"/>
  <!-- one <icon> per generated PNG -->
</iconset>
```

- `name` and `uid` MUST both be non-empty (or ATAK ignores the file and hashes the zip — §0.1).
- `uid` is the §4 digest, verbatim.
- **The `<icon>` element MUST carry only `name` — no `group` attribute.** ATAK's real parser
  (`org.simpleframework.xml`, strict mode) deserializes each `<icon>` into
  `com.atakmap.android.icons.UserIcon`, whose only `@Attribute`-annotated fields are `name` and
  `type2525b`; `UserIcon.group` is a plain unannotated Java field, not part of the XML schema. A
  stray `group` attribute with no matching annotation throws `AttributeException` and fails the
  **entire document parse** — confirmed live against a real device
  (`org.simpleframework.xml.core.AttributeException: Attribute 'group' does not have a match in
  class com.atakmap.android.icons.UserIcon`), at which point ATAK silently discards the whole
  file's `uid` and falls back to hashing the raw zip bytes (§0.1's fallback), breaking the
  cross-platform UID-matching guarantee this whole spec exists for. This was a real bug in the
  ATAK-side generator (`AutoIconset.java`) — fixed; earlier revisions of this doc incorrectly
  showed `group="{group}"` here. ATAK does **not** read group/filename from `<icon>` regardless
  (it uses the zip paths — §0.2) — there was never a reason for a consumer to need it there.
- `version="1"` mirrors this spec's version. Bumping the spec bumps this.

### Zip layout
```
iconset.xml
{group}/{filename}          ← one entry per PNG, group as the single folder level
{group}/{filename}
...
```
No nested folders beyond the single `{group}/` level (ATAK's group = first segment only).

---

## 8. Versioning and breaking changes

- The version marker lives in three coupled places: this document's header, the `version="1"`
  attribute in `iconset.xml`, and each implementation's `SPEC_VERSION` constant.
- A change to canonicalization (§2), the UID formula (§4), or group/filename derivation (§5)
  changes the resolved `iconsetpath` for existing layers → **breaking.** Bump the version; old
  and new iconsets are distinct sets and will not cross-resolve. Because there is no central
  authority reconciling drift in the federated model, a version skew between two devices means
  they simply generate two different (non-matching) sets — no silent corruption, but no sharing
  either until both are on the same spec version.

---

## 9. Conformance checklist (every implementation)

- [ ] `canonicalize(sourceUrl)` passes the §2 cases (service root → `/0`; explicit layer
      preserved; scheme/host lowercased; trailing slash & query stripped).
- [ ] `uid(canonicalUrl, field)` matches the §4 test vector byte-for-byte against the other
      platforms.
- [ ] `group` = sanitized layer name + `" Icons"`.
- [ ] filenames match §5.2 including the `.png` strip and `_N` collision suffixes, in renderer
      array order.
- [ ] `iconset.xml` written with non-empty `name` + `uid`, `uid` = the §4 digest verbatim.
- [ ] zip entries laid out as `{group}/{filename}`; `iconset.xml` at root.
- [ ] single-symbol default → `Other.png` inside `{group}/`.
- [ ] `esriSMS` symbols skipped (handled by the color/shape display-config path).

---

## 10. Known limitation (documented, not a bug)

A device that has **never ingested** a source layer, and has no optional TAK Portal cache to
query, cannot conjure an icon from a bare incoming CoT reference — it renders a default marker.
Federated/offline mode cannot close this; only ingesting the same link (or an optional
`GET …/custom-icons/by-uid/:uid` fetch from a reachable Portal) installs the set. This is by
design — see `WebMapFeatureLayer-AutoConfigurator.md` §"The one cost of going server-free."
