# WinTAK — Auto-Iconset Verification

Hand-test sequence for the ArcGIS renderer → iconset pipeline (`Services/AutoIconset.cs`,
`Services/IconsetInstaller.cs`, `FeatureLinkDockPane.TryResolveIconset`).

**Why this file exists.** The generator's rules are covered by 62 unit tests driven off the shared
cross-platform golden vectors, so the *strings* are verified. What no test on this machine can
verify is the half that needs a running host: whether WinTAK accepts our zip, whether it honours
the UID we declare, and whether a marker actually renders the icon. Those three are the whole
feature from an operator's point of view, and all three are checked below.

---

## 0. What was broken before

WinTAK could read an ArcGIS renderer and could emit `<usericon iconsetpath="…">` on a marker, but
nothing ever *created* the iconset that path named. Every picture-marker layer therefore fell back
to a default icon, and said so once per sync:

```
Layer "ILWG-TAKSERVER" has a renderer this build cannot translate
  (picture-marker-only renderers need an installed iconset).
```

If you still see that line, the pipeline did not run. That is the single most useful signal in
this document.

---

## 1. Risk ranking — test these hardest

| Rank | Item | Why |
|---|---|---|
| 1 | **UID match** (§3 below) | If WinTAK rejects our `iconset.xml` it silently falls back to hashing the zip. Icons then resolve on this machine and **nowhere else** — everything looks correct locally while the federated contract is broken. This is the only failure mode that is invisible without deliberately checking. |
| 2 | **Icons actually render** (§4) | Install can succeed while the marker still shows a default icon, if the emitted `iconsetpath` and the installed set disagree. |
| 3 | **Cross-device resolution** (§6) | The entire reason the UID is a hash of the layer URL rather than a random id. |
| 4 | Collision / naming (§5) | Covered by unit tests; verify once against real data. |

---

## 2. Preconditions

- WinTAK **5.6.0.151** with the plugin installed.
- An ArcGIS layer whose renderer uses **picture markers** (`esriPMS`). The layers already in use
  for this work all qualify: `ILWG-TAKSERVER`, `ILWG-TAKSERVER-PLI`, `TEST-NC`.
- Confirm the renderer type first, so a negative result is not mistaken for a bug:

  ```
  GET {layerUrl}/0?f=json
  ```

  Look for `drawingInfo.renderer` and symbols with `"type":"esriPMS"` and a non-empty
  `imageData`. A renderer made only of `esriSMS` symbols is correctly handled by the colour path,
  not this one, and will produce no iconset by design.

---

## 3. Install and UID match — the load-bearing check

Sync the layer, then:

```powershell
# 1. The zip we generated, named by UID
dir "$env:APPDATA\WinTAK\FeatureLink\iconsets\"

# 2. What WinTAK actually stored
$db = "$env:APPDATA\WinTAK\Databases\iconsets.sqlite"
# open with any sqlite client:
#   SELECT name, uid FROM iconsets ORDER BY id DESC LIMIT 5;
#   SELECT iconset_uid, groupName, COUNT(*) FROM icons GROUP BY iconset_uid, groupName;

# 3. The render cache WinTAK fills after a successful import
dir "$env:APPDATA\WinTAK\Databases\iconcache\"
```

**PASS:** the zip's file name (minus `.zip`) equals the `uid` column in `iconsets`, and
`iconcache\{uid}\{group}\` contains one PNG per symbol.

**FAIL — and the one to look for:** the `uid` column holds a value that is *not* our file name.
That means the manifest was rejected and WinTAK hashed the zip instead. The plugin detects this
itself and logs it as an error, so check the log first:

```
Iconset UID mismatch for "<group>": expected <a>, WinTAK stored <b>.
Icons will resolve on this machine only and will NOT match other TAK devices.
```

If that appears, the cause is almost always `iconset.xml` — see `AUTO-ICONSET-SPEC.md` §7. A stray
attribute on `<icon>` aborts the strict parse of the whole document.

Expected success log lines:

```
Installed iconset "<Layer Name> Icons" (<n> icons, uid <64-hex>).
Icons ready for "<Layer Name>" — <n> symbol(s).
```

---

## 4. Icons render on the map

1. Sync the layer.
2. Confirm the layer's badge reads **Config**, not *No Config*.
3. Confirm markers show the ArcGIS symbology rather than the default TAK icon.
4. For a `uniqueValue` renderer, confirm **different field values get different icons** — a single
   icon applied to every feature means the config collapsed to the single-icon (`"t":"ic"`) shape
   when it should have been per-value (`"t":"adv"`).

Cross-check the emitted path against the installed set — they are built independently and must
agree:

```
iconsetpath = {uid}/{group}/{filename}
```

---

## 5. Naming and collisions

Against a layer whose renderer labels include punctuation or duplicates:

- Group is the **FeatureServer layer's own `name`** plus `" Icons"`, not your local label for it.
- Characters outside `[A-Za-z0-9 _-]` become `_`.
- Duplicate labels produce `X.png`, `X_2.png`, `X_3.png` in **renderer array order**.
- A default symbol with no label is `Other.png`, inside `{group}/`, never at the zip root.

---

## 6. Cross-device resolution — the actual point

This is what the whole UID design exists for and cannot be checked on one machine.

1. Sync the layer on WinTAK; note the `uid` from the log.
2. Add the **same layer URL** on an ATAK device running the FeatureLink plugin.
3. Confirm ATAK independently generates an iconset with the **same uid** (ATAK logs it too).
4. Share a marker from WinTAK to ATAK and confirm the icon resolves there.

**PASS:** both platforms derived the same `{uid}/{group}/{filename}` from the same layer without
exchanging anything. The PNG bytes need not match — only the strings.

---

## 7. Known limits (not bugs)

- **Web Map links are rejected**, deliberately, with a message telling you to use the layer's own
  `/FeatureServer/{id}` URL. Hashing a Web Map item id would mint a UID no other platform can
  reproduce, which is worse than refusing (`AUTO-ICONSET-SPEC.md` §2.3).
- **`CIMSymbolReference` renderers yield no icons** on any TAK platform. The count is logged so a
  thin result is explainable rather than mysterious.
- **A changed renderer does not regenerate the set.** The UID hashes the layer URL and field, not
  the symbols, so new icons on an existing layer keep the old set. `IconsetInstaller.ForceReinstall`
  exists for this; it is not yet wired to a UI action.
- **Non-ASCII characters in the URL *path*** are outside the golden vectors' coverage, and .NET
  percent-encodes them. Real ArcGIS paths are ASCII. Do not "fix" this without adding a shared
  vector first — a unilateral change here desynchronises this platform from the other three.
