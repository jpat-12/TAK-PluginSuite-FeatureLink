# WinTAK 5.6 — Hand-Testing Guide (WP3 audit remediation)

**Read this first.**

> **Nothing in `WinTAK5.6/` was compiled during this remediation.** This machine has `dotnet`
> 10.0.301 but **no `msbuild`** and **no WinTAK SDK**, so a .NET Framework 4.8 WPF/MEF plugin
> could not be built here at any point. Correctness was established by reading, by symbol-level
> cross-checking, and by a test suite that compiles and runs the *SDK-independent* logic only.
>
> **Assume the first thing you will hit is a compile error, not a behaviour bug.** Budget a build
> pass before you start testing behaviour. §1 tells you what to do about it.

The one thing that *was* executed here:

```
Passed!  - Failed: 0, Passed: 216, Skipped: 0, Total: 216 - FeatureLink.Tests.dll (net8.0)
```

That covers the symbology resolvers, the ArcGIS client's pagination and error handling, the UID
formula, the URL guard, the JSON caps, and the config round-trip. It does **not** cover the dock
pane, CoT emission, the WPF converters, or anything that touches a WinTAK type.

---

## Risk ranking — test these hardest

| Rank | Item | Why |
|---|---|---|
| 1 | **C-18 `.wpk` packaging** | If `Newtonsoft.Json.dll` is not in the package the plugin does not load *at all*. Every other test is blocked behind this. |
| 2 | **C-07 symbology** | **Built from scratch.** WinTAK had no symbology of any kind — no renderer extraction, no shape styling, no `ResolveShapeStyle`. ~700 new lines, never compiled against the SDK. Highest defect probability in the package. |
| 3 | **C-16 status strip** | New XAML in the root grid. If the resource keys or the row index are wrong, the whole view fails to load. Also: everything else is far harder to diagnose until this works. |
| 4 | **Dock pane rewrite** (C-26, C-27, C-02, C-19) | The 1455-line file was rewritten to ~2160 lines. Every SDK call was preserved verbatim, but "preserved verbatim" is a claim I could not verify with a compiler. |
| 5 | **PLI CE/LE** | `selfEvent?.Point?.CE90` — inferred by analogy with the send-item path, which reads `CE90`/`LE90` off a `CotPoint`. **If `ILocationService.GetSelfCotEvent()` returns something whose `Point` lacks these, this will not compile.** Easy fix: hardcode `double.NaN`. |
| 6 | **C-09 OAuth `state`** | Cannot be exercised without a live ArcGIS app registration whose redirect allowlist includes the loopback URI — which the code's own comments admit was never confirmed. |

Everything marked "could not verify" below means exactly that: read carefully, never executed.

---

## 1. Build prerequisites

What you need that this machine did not have:

| Requirement | Notes |
|---|---|
| **Visual Studio 2022 (17.x)** or **Build Tools for VS 2022** | Must include the *.NET desktop build tools* workload. `dotnet build` **cannot** build this project — it is a legacy (non-SDK-style) `.csproj` with WPF `Page` items. |
| **.NET Framework 4.8 targeting pack** | Bundled with the workload above. |
| **WinTAK 5.6.0.151 installed** | The nine SDK assemblies come from your install. |
| **NuGet CLI** (`nuget.exe`) or VS's restore | `packages.config` is the legacy model; **nothing in the build restores it automatically.** |
| **Python 3** on `PATH` | The Release build shells out to `build-wpk.py`. The csproj now prefers `py -3`; override with `/p:PythonExe=python`. |

### 1.1 Populate `libs/`

`WinTAK5.6/libs/` is git-ignored by design. Copy these nine from your WinTAK install (default
`C:\Program Files\WinTAK\` or wherever you installed it) into `WinTAK5.6\libs\`:

```
TAK.Engine.dll
WinTak.Common.dll
WinTak.CursorOnTarget.dll
WinTak.Framework.dll
WinTak.Graphics.dll
WinTak.Net.dll
Prism.dll
Prism.Mef.Wpf.dll
Prism.Wpf.dll
```

A new `VerifySdkLibs` target fails with one clear message if `WinTak.Framework.dll` is missing,
instead of ~200 cryptic `CS0246` errors.

### 1.2 Restore and build

```powershell
cd "<repo>\WinTAK5.6"

# 1. NuGet restore (nothing does this for you)
nuget restore FeatureLink.sln
#    or: msbuild FeatureLink.sln /t:Restore

# 2. Debug build
msbuild FeatureLink.sln /p:Configuration=Debug /p:Platform=x64

# 3. Release build — also produces the .wpk
msbuild FeatureLink.sln /p:Configuration=Release /p:Platform=x64
```

**Expected artefact:** `bin\Release\FeatureLink-2.7.0-5.6.0.151.wpk`

If the Debug build previously failed because `D:\Apps\Plugins\` did not exist, that is fixed —
`DeployPlugin` is now conditional. To re-enable live deployment:

```powershell
$env:FEATURELINK_PLUGIN_DIR = "C:\Program Files\WinTAK\Plugins\"
msbuild FeatureLink.sln /p:Configuration=Debug /p:Platform=x64
```

### 1.3 If it does not compile

Most likely causes, in order:

1. **`selfEvent?.Point?.CE90` / `.LE90`** in `SendPliUpdateAsync` (`FeatureLinkDockPane.cs`).
   If `CotEvent.Point` has no `CE90`/`LE90`, replace both with `double.NaN`. Behaviour is
   unchanged from the pre-remediation build (which sent hardcoded `0`), just honest.
2. **`item.IsDisposed` / `item.Visible` / `MapMarker.Color`** — these were all in the original
   code and were preserved verbatim; if one fails, the original had the same problem.
3. **`Dispatcher.InvokeAsync(Func<T>).Task`** in `RunOnUiAsync` — available since .NET 4.5;
   if your Prism/WPF combination objects, replace with
   `Task.FromResult(dispatcher.Invoke(func))`.
4. **`AutomationProperties.LiveSetting`** on the status strip (`FeatureLinkView.xaml`) — .NET 4.5+.
   Delete the attribute if it is rejected; it is a screen-reader nicety, not functional.

Record anything you have to change — those are places where reading was not enough.

---

## 2. Test 1 (BLOCKING) — C-18: is `Newtonsoft.Json.dll` actually in the `.wpk`?

**This is the first test and everything else is blocked behind it.** Before remediation the `.wpk`
contained only `FeatureLink.dll`, the icon and the manifest, while `packages.config` pinned
`Newtonsoft.Json 13.0.3`. On a clean WinTAK that does not happen to expose a compatible
Newtonsoft in its probing path, the plugin threw `FileNotFoundException` on its first `JObject`
use — i.e. **it could not load at all**. It worked on the developer's machine only.

### 2.1 Verify the package contents

```powershell
cd "<repo>\WinTAK5.6\bin\Release"

# A .wpk is a plain ZIP.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("$PWD\FeatureLink-2.7.0-5.6.0.151.wpk")
$zip.Entries | Select-Object FullName, Length
$zip.Dispose()
```

**Expected — all five, exactly:**

```
manifest.xml
icon.png
plugin/x64/FeatureLink/FeatureLink.dll
plugin/x64/FeatureLink/FeatureLink.dll.config      <- binding redirects
plugin/x64/FeatureLink/Newtonsoft.Json.dll         <- THE FIX
```

**FAIL if `Newtonsoft.Json.dll` is absent.** There must be **no** `WinTak.*.dll`, `TAK.Engine.dll`
or `Prism*.dll` — those are host-provided and shipping our own copy risks loading a second,
conflicting instance into the shared AppDomain.

### 2.2 Verify the manifest

```powershell
$zip = [System.IO.Compression.ZipFile]::OpenRead("$PWD\FeatureLink-2.7.0-5.6.0.151.wpk")
$e = $zip.GetEntry("manifest.xml"); $r = New-Object System.IO.StreamReader($e.Open())
$r.ReadToEnd(); $r.Dispose(); $zip.Dispose()
```

**Expected:** `<version>2.7.0</version>`, `<sdkVersion>5.6.0.151</sdkVersion>`, and a
`<license>Apache-2.0</license>` element (C-36). The manifest is now templated from the
checked-in `MANIFEST.xml` rather than duplicated inside the Python script, so the two can no
longer drift.

### 2.3 Verify the build fails loudly when the dependency is missing

```powershell
cd "<repo>\WinTAK5.6"
Move-Item bin\Release\Newtonsoft.Json.dll bin\Release\Newtonsoft.Json.dll.bak
py -3 build-wpk.py 2.7.0 5.6.0.151 bin\Release\FeatureLink.dll Assets bin\Release\probe.wpk --manifest MANIFEST.xml
echo "exit code: $LASTEXITCODE"
Move-Item bin\Release\Newtonsoft.Json.dll.bak bin\Release\Newtonsoft.Json.dll
```

**Expected:** exit code `2` and
`build-wpk.py: error: required dependencies missing from '...': Newtonsoft.Json.dll`.
*(This exact case was executed on the remediation machine under Python 3.13 and passed.)*

### 2.4 Clean-machine install smoke test

**This is the test that actually proves C-18**, and it needs a machine or VM that has **never**
had the plugin or a Visual Studio install on it.

1. Install WinTAK 5.6.0.151 on a clean Windows VM.
2. Copy only the `.wpk` across.
3. WinTAK → **Plugins** → **Import** → select the `.wpk` → restart WinTAK.
4. Confirm the **FeatureLink** button appears on the Home ribbon tab, Tools group.
5. Click it. **Expected:** the dock pane opens.
6. **Expected:** `%AppData%\WinTAK\FeatureLink\featurelink.log` exists and contains
   `FeatureLink dock pane constructed.`

**Before the fix:** the button appeared but the pane threw `FileNotFoundException` on first use,
with — because of C-16 — no visible message anywhere.

> If step 4 shows nothing at all, the plugin failed MEF composition. Check WinTAK's own log.
> Known pre-existing issue (not fixed, out of scope): `FeatureLinkButton.cs:38` uses
> `GetDockPane(...)?.Activate()`, so a composition failure presents as a silently dead button.

---

## 3. Test 2 (do this early) — C-16: the status channel is visible

`StatusText` is the plugin's **only** user-facing error and progress channel. It was assigned in
20+ places and **bound by nothing**. The only other `StatusText` in the XAML was a `Style`
resource key of the same name — a coincidence that made the omission invisible on review. Every
network, auth, import, share and download failure was silently swallowed.

**Do this before anything else in §4–§10: every other test is far more diagnosable once you can
see the status line.**

### 3.1 The strip exists

1. Open the FeatureLink pane.
2. **Expected:** a thin bar across the **bottom** of the pane, present on all three tabs
   (Home / Layers / PLI), reading `Ready.`
3. Switch tabs. **Expected:** the bar stays put and does not scroll away.

### 3.2 Force an error and confirm you see it

Easiest reproduction, no network needed:

1. Layers tab → **+ Add Layer**.
2. Paste `https://192.168.99.99/rest/services/Nope/FeatureServer/0` → **Add**.
3. **Expected:** the strip shows
   `Cannot add layer: "192.168.99.99" resolves to a private, loopback or link-local address, which this plugin will not fetch.`
   **Before:** absolutely nothing happened, visibly.

Second reproduction, exercising a real network failure:

1. Paste `https://services.arcgis.com/thishostdoesnotexist/ArcGIS/rest/services/X/FeatureServer/0`.
2. **Expected:** `Could not load layer from URL: Could not reach the ArcGIS service: …`
   **Before:** silence.

Third — the one the audit specifically asked for:

1. Add a **valid public** layer and confirm it downloads.
2. Disconnect the network.
3. Click that layer's sync/refresh button.
4. **Expected:** the strip shows `Download failed for <name>: …`, **and**
   `%AppData%\WinTAK\FeatureLink\featurelink.log` gains a matching `[Error]` line with a stack
   trace. **Before:** neither. The audit's exact wording was *"Currently fails on both counts."*

### 3.3 Log hygiene (C-21)

```powershell
Select-String -Path "$env:APPDATA\WinTAK\FeatureLink\featurelink.log" -Pattern "token=|Bearer\s+ey"
```

**Expected: no matches.** Any `token=` should appear as `token=<redacted>`. **FAIL** if a real
token value is in the log.

### 3.4 PLI layer URL is displayed

Found by the C-16 regression test itself: `PliLayerUrl` was another public property bound by
nothing.

1. PLI tab → join or create a layer.
2. **Expected:** the configured layer URL is shown in the *PLI Feature Layer* card.
   **Before:** nowhere in the UI — you could not confirm where your position was being sent.

---

## 4. Test 3 (HIGHEST RISK) — C-07: symbology

**This is a from-scratch implementation, not a patch.** WinTAK had *no* symbology resolution on
the download path at any point: `ArcGisFeatureService` read the layer metadata and threw
`drawingInfo` away, and `DisplayStyleResolver` had no `ResolveShapeStyle` whatsoever. A
Portal-authored config carrying polyline/polygon styling was **silently and completely dropped**,
with no warning and no log line (Appendix F §5's concrete interop break).

New code: `Services/AutoSymbology.cs` (renderer → style structs, ported from
`AutoSymbology.java`), `ShapeStyle` + `ResolveShapeStyle` + `BuildShapeConfigJson` +
`BuildMarkerSymConfigJson` in `DisplayStyleResolver.cs`, and `ResolveSymbologyAsync` in the dock
pane. ~700 lines. **Test this hardest.**

### 4.1 Auto-symbology from a layer's own renderer — point markers

**Precondition:** an ArcGIS Feature Service **point** layer whose published renderer is a
*unique value* renderer on some text field, with clearly different colours per value (e.g.
`STATUS`: `OPEN` = bright green, `CLOSED` = bright red). No FeatureLink display config attached.

1. Add the layer by URL and let it download.
2. **Expected:** markers are coloured **per feature by their `STATUS` value** — green ones and red
   ones, matching what ArcGIS Online shows for the same layer.
3. **Expected in the log:** no `carries polyline/polygon shape styling` line (this is a point layer).
4. **Before:** every marker was WinTAK's default. This is the owner's original field defect.

### 4.2 Auto-symbology — a single-symbol renderer

**Precondition:** a point layer with a *simple* renderer, one `esriSMS` symbol, a distinctive
colour (e.g. magenta) and a non-circle shape.

1. Add and download.
2. **Expected:** every marker takes the renderer's colour.
3. **Known limitation to confirm, not a bug:** the *shape* (`square`/`diamond`/…) is extracted and
   carried in the config but WinTAK's marker API exposes only colour, so shape is not rendered.
   Note what you observe.

### 4.3 Shape styling — the Appendix F §5 interop break

**Precondition:** an ArcGIS **polygon** or **polyline** layer whose renderer uses `esriSFS`/`esriSLS`
— ideally a unique-value renderer so per-value styling is exercised (e.g. `ZONE`: `HOT` = red
outline, translucent red fill; default = grey thin outline).

1. Add the layer and download.
2. **Expected in `featurelink.log`:**
   `Layer "<name>" carries polyline/polygon shape styling (esriSLS/esriSFS); applying it to first-vertex markers.`
3. **Expected in the status strip:** the download summary includes a shape-styled count, e.g.
   `Downloaded: Zones (37 features, 37 shape-styled)`.
4. **Expected on the map:** one marker per feature at its **first vertex**, coloured by the
   resolved **stroke** colour — red for `HOT`, grey otherwise.
5. **Before:** markers appeared in the default colour and **nothing** was logged or shown. That
   silence is the defect; the visible acknowledgement is the fix.

> **Documented partial, please confirm and report.** WinTAK's plugin SDK exposes no *verified*
> polyline/polygon map-item API, so line and area features are still plotted as a marker at their
> first vertex — the same simplification the ATAK downloader makes. The styling is now
> **resolved, applied to that marker, logged and counted** instead of discarded. If your WinTAK
> build does expose a polyline/polygon item type, say so and it becomes a small follow-up.

### 4.4 Portal-authored config carrying shape styling (`shp`)

**Precondition:** a `.featurelinkshare` file with a `shp` block. Save this as
`test-shape-config.featurelinkshare` (substitute a polygon layer URL you can reach):

```json
{
  "v": 2,
  "url": "https://services.example.com/arcgis/rest/services/Zones/FeatureServer/0",
  "layer": { "name": "Zones (shape-styled)", "opacity": 1.0, "visible": true },
  "sym": { "t":"uv", "c":"#3388ff", "f":"ZONE", "op":1.0,
           "uv":[{"v":"HOT","c":"#ff0000"},{"v":"COLD","c":"#0000ff"}] },
  "lbl": { "f":"NAME" },
  "popup": { "t":"NAME", "flds":[["NAME","Name"],"ZONE"] },
  "shp": {
    "f": "ZONE",
    "s":  {"sc":"#FF808080","sw":1,"sd":"solid","fc":"#00000000","fs":"none"},
    "bv": {
      "HOT":  {"sc":"#FF00FF00","sw":4,"sd":"dash","fc":"#4000FF00","fs":"solid"},
      "COLD": {"sc":"#FFFF0000","sw":2,"sd":"dot","fc":"#40FF0000","fs":"solid"}
    }
  },
  "freq": { "iv": 180, "u": "s" },
  "private": false
}
```

1. Layers tab → **+ Add Layer** → **Upload Display Prefs (JSON)** → pick the file.
2. **Expected:** the layer is added and downloaded (no consent prompt — you picked the file
   yourself; see §5 for the peer path, which *does* prompt).
3. **Expected:** a `HOT` feature's marker is **green** (`#00FF00`), a `COLD` one **red**.
   **This is the C-24 precedence check**: the per-value `bv` entry must win over the `s`
   fallback. If everything comes out **grey** (`#808080`), the precedence is inverted and this is
   a **FAIL** — that is exactly the bug ATAK and CloudTAK still have and WinTAK was written not to
   inherit.
4. A feature with a `ZONE` value that is neither → **grey**.
5. Labels come from `NAME`; tap a marker and confirm remarks read `Name: …` then `ZONE: …`.
6. Restart WinTAK. Re-open the pane. **Expected:** the styling survives — it is persisted to
   `settings.xml` as `<ShpJson>`. Verify:
   ```powershell
   Select-String -Path "$env:APPDATA\WinTAK\FeatureLink\settings.xml" -Pattern "ShpJson"
   ```
   **Before:** there was no such element and no such capability.

### 4.5 Explicit config beats auto-derived

1. Take a layer that has its own renderer (§4.1) **and** apply a display config with a different
   colour scheme.
2. **Expected:** the display config wins. The auto-derived symbology is only used when the layer
   has no explicit config, and it is re-derived from live metadata on every download (never
   persisted), so it cannot go stale against a renderer the layer owner edits.

### 4.6 Symbology unit tests (already green here)

```powershell
cd "<repo>\WinTAK5.6\Tests\FeatureLink.Tests"
dotnet test -f net8.0
```

Covers all six sym types, the ten rule operators (including the ASCII `!=` spelling that used to
fall through and silently never match), 3/6/8-digit hex, opacity `NaN`/negative/overflow, empty
`flds` entries, colour-break edge cases, and the `de-DE`/`tr-TR` culture matrix.

---

## 5. Test 4 — C-02: consent before a peer's layer is added

Before: a `.featurelinkshare` arriving in a Mission Package was **auto-added and auto-downloaded
with no prompt at all**. Any peer who could send you a package could inject arbitrary markers onto
your COP without interaction — and make your workstation issue an HTTP request to a URL of their
choosing.

1. From a second TAK client, send this WinTAK instance a Mission Package containing a
   `.featurelinkshare` (use the §4.4 file).
2. **Expected:** a modal **"Accept shared FeatureLink layer?"** dialog naming
   the received file, the layer name, **the exact URL that will be contacted**, the sharing mode,
   and whether styling is included. The default button is **No**.
3. Click **No**. **Expected:** status strip reads `Declined the shared layer.`; no layer added; no
   HTTP request made (confirm in the log — there should be no fetch for that URL).
4. Repeat, click **Yes**. **Expected:** the layer is added and downloaded.
5. **Hostile-URL case:** edit the share so `"url"` is `http://169.254.169.254/latest/meta-data/`.
   **Expected:** **no prompt at all** and the strip reads
   `Rejected a shared layer: … private, loopback or link-local …`. The URL is validated *before*
   the operator is even asked. **Before:** the plugin fetched it.
6. **Oversized/deep JSON (C-19):** send a `.featurelinkshare` that is 5 MB, and one nested ~1000
   levels deep. **Expected:** `Ignored an oversized FeatureLink share file.` / an import-failure
   message — and **WinTAK stays running**. Before, a deeply nested document risked
   `StackOverflowException`, which is uncatchable on .NET Framework and takes the whole process
   down.
7. **Hostile refresh interval:** set `"freq":{"iv":1}`. **Expected:** the layer's refresh interval
   is clamped to **30 s**, not 1 s. Before, a peer could drive a full layer re-download every
   second, forever — resource exhaustion and ArcGIS credit burn on your account.

---

## 6. Test 5 — C-06: pagination

Before: the query had no `resultOffset`, no `resultRecordCount` and no `exceededTransferLimit`
check, so **any layer over the service `maxRecordCount` (typically 1000–2000) silently synced only
its first page** and the UI reported it as a complete success.

**Precondition:** a Feature Service layer with **more than 2000** features. 25,000 is ideal.

1. Add and download it.
2. **Expected:** the status strip reports the **full** feature count, matching what the ArcGIS
   REST `?returnCountOnly=true` endpoint says.
3. **Expected in the log:** multiple `/query?` requests with `resultOffset=0`, `1000`, `2000`, …
4. Cross-check the Home tab's *Total Features* against the ArcGIS item page.
5. **Before:** exactly `maxRecordCount` features, reported as success.

Also confirm the **truncation warning** exists: if a download ever stops early, the status line
must end with `— TRUNCATED, not all features were downloaded.` (Hard to force in the field; the
unit tests cover the mechanism.)

**Search paging:** if your ArcGIS account owns **more than 100** Feature Services, sign in and
refresh. **Expected:** all of them appear in *My ArcGIS Layers*. **Before:** exactly 100, with no
indication that there were more.

---

## 7. Test 6 — C-22 / C-21: ArcGIS error bodies and token placement

ArcGIS returns `{"error":{"code":498,…}}` with **HTTP 200**. Seven parse sites read `count` or
`features` straight off such a body, so a 401, a deleted layer and a genuinely empty layer were
all reported as **"0 features"**.

1. Add a public layer, download it successfully.
2. In ArcGIS Online, **unshare** that layer (make it private) *without* signing out of the plugin.
3. Force a sync.
4. **Expected:** a specific message —
   `Download failed for <name>: You do not have permissions to access this resource…`
   or similar, carrying the real ArcGIS reason. **Before:** `Downloaded: <name> (0 features)`.
5. Delete a layer in ArcGIS and sync again. **Expected:** a distinct 404-flavoured message, clearly
   different from the permissions one.
6. Sign in, wait for the access token to expire (or revoke it in ArcGIS), then sync.
   **Expected:** the message mentions signing in again.

**Token placement (C-21):** capture traffic with Fiddler or `mitmproxy`.

- **Expected:** no request URL contains `token=`.
- **Expected:** requests carry `X-Esri-Authorization: Bearer <token>` (and `Authorization: Bearer`).
- **Before:** the bearer token was in the query string of every request, and therefore in every
  proxy log, server log and `Referer`.

---

## 8. Test 7 — C-26 / C-27: re-entrancy, timers, crashes

These were the process-crash findings: two `async void` methods driven by `System.Threading.Timer`
with no re-entrancy guard, and a `ToDictionary` that throws on a duplicate key inside a
`Dispatcher.Invoke` on an unobserved task.

1. **Duplicate service URL (C-27).** In ArcGIS, publish the same Feature Service twice, or create a
   hosted **view** so two portal items share a service URL. Sign in and press **Refresh**.
   **Expected:** the refresh completes, both items visible or de-duplicated, no crash.
   **Before:** `ArgumentException: An item with the same key has already been added`, thrown inside
   a dispatcher lambda from a fire-and-forget task — silent total failure of layer refresh, with
   process-crash risk on some .NET Framework configurations.
2. **Double-click sync (C-26).** Click a layer's sync button twice rapidly.
   **Expected:** the second click is ignored; the log shows
   `Skipping a download for … — one is already running.` Exactly one set of markers.
   **Before:** two concurrent downloads racing the same marker-uid map.
3. **Slow layer vs. the 15 s recurrence tick.** Set a big layer's refresh interval to 30 s on a
   deliberately slow link (throttle to ~1 Mbps).
   **Expected:** no overlapping downloads, no duplicate markers, WinTAK stays up. Leave it 30 min.
4. **PLI overlap (C-26).** Enable PLI auto-send on a slow/black-holed endpoint.
   **Expected:** the log shows `Skipping a PLI tick — the previous send is still in flight.`, and
   the ArcGIS layer accumulates **one** row for this device, not a duplicate per stalled tick.
5. **Double-click Sign In.** **Expected:** the button disables immediately after sign-in succeeds
   (`RaiseCanExecuteChanged` is now called); you cannot start two browser tabs and two loopback
   listeners.
6. **Marker sweep.** Download a layer, delete some features server-side, sync again.
   **Expected:** the vanished features' markers disappear from the map.
7. **Uid stability.** Sync a layer whose features have **no** `uid` attribute, twice.
   **Expected:** markers update in place — no flash, no duplicates. **Before:** uids were
   `FL-{index}-{timestamp}`, so every refresh destroyed and recreated every marker.

---

## 9. Test 8 — C-09: OAuth `state`

**Cannot be verified here at all** — it needs a live ArcGIS app registration whose redirect
allowlist includes the loopback URI. The code's own comments admit that was never confirmed, so
**sign-in may simply not work against the production app registration**. Establish that first.

1. Sign in normally. **Expected:** the browser opens, you authenticate, the tab says
   *"Signed in to FeatureLink"*, and the pane shows `Signed in as <user>`.
2. **Inspect the authorize URL** (copy it from the browser address bar).
   **Expected:** it carries `&state=<43-ish base64url characters>`. **Before:** no `state` at all —
   textbook OAuth authorization-code injection.
3. **Injection attempt.** While sign-in is pending (browser open, not yet authenticated), from a
   *different* browser tab or `curl`, hit the loopback with a forged code:
   ```
   curl "http://localhost:51000/callback/?code=ATTACKER_CODE&state=wrong"
   ```
   (find the real port in the authorize URL's `redirect_uri`)
   **Expected:** the plugin **rejects** it —
   `The sign-in response did not match this sign-in request and was rejected.`
   **Before:** the plugin would have exchanged the attacker's code and bound to their identity.
4. **Probe tolerance.** `curl "http://localhost:51000/callback/favicon.ico"` before authenticating,
   then complete sign-in normally. **Expected:** sign-in still succeeds. **Before:** the stray
   request consumed the single-shot listener and the real callback failed.
5. **Sign out.** **Expected:** the log records `Refresh-token revocation returned HTTP 200.`
   **Before:** the refresh token stayed valid at the portal after you believed you had signed out.
6. **Non-admin image.** Run WinTAK as a **standard user** on a locked-down build and sign in.
   **Expected (best case):** it works. **If it fails**, the message now names the URL-reservation
   problem instead of an opaque `HttpListenerException: Access is denied`. Report either way —
   this is a total-loss failure mode on a hardened enterprise image and it is undocumented.

---

## 10. Test 9 — settings durability and the share path

1. **Corrupt settings.** With WinTAK closed, truncate `%AppData%\WinTAK\FeatureLink\settings.xml`
   mid-element. Start WinTAK, open the pane.
   **Expected:** the plugin starts, the log records the failure, and a
   `settings.xml.corrupt-<timestamp>` backup exists. **Before:** the entire configuration was
   discarded silently, with no backup, no log and no message.
2. **One bad element.** Restore a good file, then change one layer's
   `<RecurrenceInterval>` to `abc`. Start WinTAK.
   **Expected:** **all** layers still load; only that one field falls back to its default, with a
   log line. **Before:** total configuration loss.
3. **Atomic save.** During a save-heavy moment (many layers, rapid eye-icon toggling), kill WinTAK
   with Task Manager. Restart. **Expected:** `settings.xml` exists and is valid. **Before:** the
   `File.Delete` → `File.Move` pair had a window in which **no** settings file existed at all.
4. **Read-only profile.** Mark the FeatureLink folder read-only, toggle a layer's visibility.
   **Expected:** the status strip reads `Could not save settings: …` and WinTAK keeps running.
   **Before:** an unhandled exception on the UI thread (a WinTAK crash dialog) or an unobserved
   exception from the PLI timer.
5. **Share a layer.** Layers tab → share icon → pick a contact → send.
   **Expected:** the receiving client gets it; the status strip confirms; and about two minutes
   later the temp file under `%TEMP%\FeatureLinkShare\` is **gone**. **Before:** those files —
   containing the layer URL and full display config — accumulated forever on a shared workstation.
6. **Share a hidden layer.** Hide a layer, then share it. **Expected:** it arrives **hidden**.
   **Before:** `"visible"` was hardcoded `true`.
7. **Share a styled layer.** Share a layer that has shape styling. **Expected:** the receiver's
   share file contains a `shp` block. *(This is the WinTAK half of C-23; ATAK's `toCompactJson`
   still strips it — WP1's item.)*
8. **Reserved filename.** Rename a layer to `CON`, then share it. **Expected:** it works; the
   temp file is not literally `CON.featurelinkshare`.
9. **Version skew.** Hand-edit a share to `"v": 99`.
   **Expected:** `This FeatureLink share uses schema v99, which this build cannot read.`
   **Before:** it was parsed as v2 and silently mis-imported.

---

## 11. Test 10 — CoT hygiene

1. **Invalid CoT type.** Add a layer whose `cot_type` column contains junk (`hello`, or a 4 KB
   blob). Download and capture the broadcast CoT.
   **Expected:** the event type is `a-u-G` (**unknown**). **Before:** the junk was emitted onto the
   network, and an empty value fell back to `a-f-G` — asserting a **friendly** affiliation the
   source data never claimed, which is an operational-safety problem in its own right.
2. **XML escaping — resolve this before delivery.** Set a feature's `tak_callsign` to:
   ```
   A" type="a-h-G" x="
   ```
   Download and capture the raw CoT on the wire.
   **Expected:** well-formed XML with the callsign escaped. FeatureLink now strips control
   characters and caps length at its own boundary, **but whether `CotItem.SetAttribute`
   XML-escapes its input is undocumented and unverified in the SDK.** If the captured XML is
   malformed, that is a **CRITICAL** finding: peer-controlled data reaching a federated message
   bus. Report the captured bytes either way.
3. **Staleness.** Capture a synced feature's CoT. **Expected:** stale time is ~3× the layer's
   refresh interval (or 24 h if auto-refresh is off). **Before:** a flat **365 days** — markers
   persisted on every peer's map for a year after you uninstalled the plugin.
4. **PLI CE/LE.** Enable auto-send, inspect a row in the ArcGIS layer.
   **Expected:** `ce`/`le` carry the real GPS accuracy, or JSON `null`. **Before:** hardcoded `0`,
   a fabricated zero-metre error that is worse than a null for any downstream consumer.
5. **PLI CoT type.** **Expected:** the row's `cot_type` matches your actual self-marker type.
   **Before:** hardcoded `a-f-G-U-C`, so an aircraft or vehicle reported as ground-unit-combat.
6. **PLI failure visibility.** Point PLI at a layer, then delete that layer in ArcGIS.
   **Expected:** the PLI card shows
   `Position not sent (N consecutive failures): …` and the Home tab's PLI badge stops saying
   "Connected". **Before:** the entire PLI send path ended in a completely empty catch — the plugin
   stopped reporting position and the operator was never told. For a PLI feature that is a
   mission-safety defect.
7. **PLI "Connected" honesty.** Join a **garbage-but-well-formed** layer URL that you have no
   access to. **Expected:** the badge does **not** say "Connected" — it should read
   `Not Yet Sent`, then `Failing (N)`. **Before:** a green "Connected" indefinitely, purely because
   a URL had been typed and you were signed in.

---

## 12. Running the automated tests

```powershell
cd "<repo>\WinTAK5.6\Tests\FeatureLink.Tests"
dotnet test -f net8.0
```

Requires only the .NET SDK — **no msbuild, no WinTAK SDK**. It compiles the pure logic
(`AutoSymbology`, `DisplayStyleResolver`, `ArcGisFeatureService`, `ArcGisHttp`, `SafeJson`,
`UrlGuard`, `Log`, `ArcGisLayer`, `DownloadedFeature`) by **source link** rather than by
project reference, precisely so it can run without the SDK.

Result on the remediation machine:

```
Passed!  - Failed: 0, Passed: 216, Skipped: 0, Total: 216, Duration: 309 ms
```

`XamlBindingSmokeTests` reads `FeatureLinkView.xaml` and `FeatureLinkDockPane.cs` from the repo,
so **it will fail if you move or rename those files** — that is intentional.

---

## 13. What is NOT covered and NOT fixed

| Item | Status |
|---|---|
| **WinTAK 5.7** | Still a regressed fork. See `docs/remediation/wp3-wintak-57-parity.md`. Only the version/`<id>` collision and the `.wpk` packaging were fixed. **Do not ship 5.7.** |
| `FeatureLinkDockPane` unit tests | Impossible without the SDK, and blocked on the DI refactor (`new ArcGisAuthService()` in a field initialiser). |
| CoT emission tests | Same. §11.2 is the manual substitute and it is the one that must be settled before delivery. |
| WPF converters, virtualization, accessibility, localization | Not addressed. `AutomationProperties` count is still ~2 (the two added here). Section 508 remains a compliance gap. |
| URL normalisation (`LayerKey`) | Not addressed. Trailing slashes and `/0` suffix variants of the same layer still compare unequal in several places. |
| `.wpk` Authenticode signing | Not addressed. |
| `libs/` provenance manifest | Not addressed. |
| Two-instance settings coordination | Not addressed; last writer still wins. |

---

## 14. Reporting back

For each numbered test, record **pass / fail / not-run** plus what you actually saw. Please
prioritise reporting:

1. **Anything that did not compile** (§1.3) — that is where reading was not enough.
2. **§11.2, the CoT XML-escaping capture** — this must be settled before any delivery.
3. **§4.3/§4.4 shape styling** — the newest and least-reviewed code in the package.
4. **§9.1, whether OAuth sign-in works at all** against the production app registration.
