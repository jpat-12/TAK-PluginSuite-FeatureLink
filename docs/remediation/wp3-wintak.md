# WP3 — WinTAK 5.6 / 5.7 — Remediation Report

**Branch:** `audit-remediation` · **Baseline:** `a8f6f4a`
**Scope:** `WinTAK5.6/**`, `WinTAK5.7/**`, this report, `docs/testing/wintak.md`,
`docs/remediation/wp3-wintak-57-parity.md`, append-only additions to `QUESTIONS-FOR-OWNER.md`.

---

## 0. The honest headline

**No WinTAK plugin code was compiled during this work.** This machine has `dotnet` 10.0.301 but
**no `msbuild` and no WinTAK SDK**, so a .NET Framework 4.8 WPF/MEF assembly could not be built at
any point. Correctness for the plugin proper was established by reading, by verifying that every
type and method called exists with the signature assumed, and by keeping every SDK call site
byte-identical to the code it replaced.

What *was* executed:

- **`dotnet test` — 216 tests, all passing**, against the SDK-independent logic compiled by source
  link into a net8.0 project (`WinTAK5.6/Tests/FeatureLink.Tests/`). This is a real compiler +
  runtime check of `AutoSymbology`, `DisplayStyleResolver`, `ArcGisFeatureService`, `ArcGisHttp`,
  `SafeJson`, `UrlGuard`, `Log`, `ArcGisLayer`, `DownloadedFeature`.
  ```
  Passed!  - Failed: 0, Passed: 216, Skipped: 0, Total: 216, Duration: 337 ms
  ```
- **`build-wpk.py` under Python 3.13**, for both trees, including the failure path.

Everything else is `TEST-NOT-EXECUTED-LOCALLY`. `docs/testing/wintak.md` is the hand-test plan,
ordered by risk, and it says plainly that the first thing the owner will probably hit is a compile
error rather than a behaviour bug.

**Two real defects were found by the new tests and fixed** — both in code written from scratch
during this remediation, which is the least-reviewed code in the package. See §3.

---

## 1. Per-C-ID status

| C-ID | Status | Files touched | Test | Evidence |
|---|---|---|---|---|
| **C-16** StatusText unbound | **FIXED** | `Views/FeatureLinkView.xaml`, `ViewModels/FeatureLinkDockPane.cs` | `XamlBindingSmokeTests` (6 tests, passing) | Confirmed at `:113` — the only `StatusText` in the XAML was a `Style` resource key. Now bound to a status strip in root-grid `Grid.Row="2"`, outside the scrolling page container, visible on all three tabs. All status writes routed through a marshalling `SetStatus()` that also logs. The smoke test asserts every public `string` and `ICommand` on the VM is referenced by a binding, and that every binding path resolves. |
| **C-17** 5.7 regression cluster | **PARTIAL (bounded by owner)** | `WinTAK5.7/MANIFEST.xml`, `Properties/AssemblyInfo.cs`, `FeatureLink.csproj`, `build-wpk.py` | packaging verified by execution | Full re-fork descoped. Identity collision broken: `FeatureLink`/`2.6.9`/`1.0.0.0` on **both** trees → 5.7 is now `FeatureLink.WinTAK57`/`0.9.0-pre`/`0.9.0.7` and self-describes as an incomplete port; Release builds emit an MSBuild warning. Full file-by-file gap report + 8-phase port order (~6–7 engineer-days) in **`wp3-wintak-57-parity.md`**. |
| **C-18** `.wpk` omits `Newtonsoft.Json.dll` | **FIXED** | `build-wpk.py` (both), `FeatureLink.csproj` (both), `MANIFEST.xml` (both) | executed under Python 3.13 | Package now contains `FeatureLink.dll`, `FeatureLink.dll.config` (binding redirects), the icon and every non-SDK dependency; SDK assemblies explicitly excluded; missing dependency fails the build with exit 2 and a clear message. Manifest templated from the checked-in `MANIFEST.xml` instead of duplicated inline. |
| **C-07** Symbology never implemented | **FIXED (with a documented partial)** | `Services/AutoSymbology.cs` **(new)**, `Services/DisplayStyleResolver.cs`, `Services/ArcGisFeatureService.cs`, `Models/ArcGisLayer.cs`, `Models/DownloadedFeature.cs`, `ViewModels/FeatureLinkDockPane.cs` | `AutoSymbologyTests` (30), `DisplayStyleResolverTests` (60+), `ConfigRoundTripTests` (5) — all passing | Confirmed: `grep -E 'drawingInfo|renderer|AutoSymbology|Iconset' WinTAK5.6/Services/` returned zero hits at baseline. Built from scratch: renderer extraction ported from `AutoSymbology.java` / `autoSymbology.ts`, `ShapeStyle` + `ResolveShapeStyle` + `BuildShapeConfigJson` + `BuildMarkerSymConfigJson`, and `ResolveSymbologyAsync` on the download path. **C-24 honoured**: per-value `bv` lookup first, single style as fallback — WinTAK does **not** inherit the inversion ATAK/CloudTAK have. **Partial:** WinTAK's SDK exposes no verified polyline/polygon map-item API, so line/area features are still plotted as a first-vertex marker; the resolved stroke colour is applied to it and the fact is logged, counted and surfaced in the status line. That closes Appendix F §5's "silently and completely dropped, no warning, no log line". |
| **C-06** Zero pagination | **FIXED** | `Services/ArcGisFeatureService.cs` | `ArcGisFeatureServiceTests` — 4 paging tests + 2 search-paging, passing | `resultOffset`/`resultRecordCount` loop driven by the service's own `maxRecordCount`, honouring `exceededTransferLimit`, with a `Truncated` flag surfaced as `— TRUNCATED, not all features were downloaded.` Portal search now pages on `start`/`nextStart` instead of taking the first 100. |
| **C-22** ArcGIS error bodies with HTTP 200 | **FIXED** | `Services/ArcGisHttp.cs` **(new)**, `ArcGisFeatureService.cs`, `ArcGisAuthService.cs` | 6 error-body tests, passing | One central guard checking transport status **and** `json.error`, raising a typed `ArcGisServiceException` with the ArcGIS code, message and `details`. All parse sites routed through it. Typed errors reach the now-visible status channel. |
| **C-09** No OAuth `state` | **FIXED** | `Services/ArcGisAuthService.cs` | `TEST-NOT-EXECUTED-LOCALLY` (needs a live app registration) | Confirmed absent at `:191-200`. CSPRNG 256-bit nonce, in memory only, cleared on completion, compared without short-circuiting; non-matching or absent state is rejected. Also: listener loops past probe requests, refresh serialised behind a `SemaphoreSlim` with a 60 s expiry skew, https asserted before `Process.Start`, refresh token revoked at the portal on logout, probe listeners `Close()`d. CloudTAK used as the reference. |
| **C-27** `async void` / fire-and-forget | **FIXED** | `ViewModels/FeatureLinkDockPane.cs` | `TEST-NOT-EXECUTED-LOCALLY` | Both timer-driven `async void` methods → `async Task` launched through `RunGuarded`, which observes faults and reports them. All six `DelegateCommand`-wrapped async lambdas likewise. `ToDictionary(l => l.Url, …)` → `GroupBy().ToDictionary(g => g.Key, g => g.First())` (threw `ArgumentException` on a duplicate service URL, inside a `Dispatcher.Invoke` on an unobserved task). The `await` that sat outside its `try` is inside it. |
| **C-26** No re-entrancy guards | **FIXED** | `ViewModels/FeatureLinkDockPane.cs` | `TEST-NOT-EXECUTED-LOCALLY` | Per-layer in-flight `HashSet` under a lock; `Interlocked.CompareExchange` guards on both timers; per-operation timeouts (5 min download, 60 s PLI) via linked `CancellationTokenSource`s cancelled on `Dispose`. `_layerMarkerUids` is now lock-guarded with a flattened uid `HashSet` alongside, so the per-broadcast membership test is O(1) rather than O(total markers) on WinTAK's CoT thread. |
| **C-19** Unbounded peer JSON | **FIXED** | `Services/SafeJson.cs` **(new)**, `ViewModels/FeatureLinkDockPane.cs` | `SafeJsonTests` (9), passing — including a 100,000-level document | `JsonTextReader { MaxDepth = 32 }`, a 1 MB byte cap applied **before** the file is read, non-object roots and trailing content rejected, explicit `JsonSerializerSettings` pinning `TypeNameHandling.None` (all WinTAK plugins share one AppDomain). C-18 is also fixed, so the Newtonsoft-13 version guarantee is real. |
| **C-21** Token in the URL | **FIXED** | `Services/ArcGisHttp.cs`, `ArcGisFeatureService.cs`, `Services/Log.cs` **(new)** | 2 token-placement tests + 3 redaction tests, passing | `X-Esri-Authorization: Bearer` (and `Authorization: Bearer`); no `token=` anywhere in a URL. `Log.Redact` strips `token`/`access_token`/`refresh_token`/`code`/`code_verifier`/`client_secret` values and `Bearer <value>` from every line before it can be written. |
| **C-02** Unauthenticated config injection | **FIXED** | `Services/DialogService.cs` **(new)**, `Services/UrlGuard.cs` **(new)**, `ViewModels/FeatureLinkDockPane.cs` | `UrlGuardTests` (20+), passing; dialog path `TEST-NOT-EXECUTED-LOCALLY` | Confirmed at `:702-712` — auto-add + auto-download with no prompt. Now: the peer-supplied URL is validated **before** the operator is asked (https only, no private/loopback/link-local/CGNAT after DNS resolution, ArcGIS service path), then a mandatory accept dialog naming the source file **and** the target URL, defaulting to **No**. Schema version checked. `freq.iv` clamped to ≥30 s. No `pli_endpoint` handling exists on WinTAK, so there is nothing to auto-apply. |
| **C-36** Licensing undeclared | **FIXED** | `Properties/AssemblyInfo.cs` (both), `MANIFEST.xml` (both) | manifest verified by execution | `AssemblyCompany` populated, copyright attributed, `AssemblyMetadata("License","Apache-2.0")`, `<license>`/`<licenseUrl>`/`<copyright>` in both manifests. Assembly version aligned with the package version (was `1.0.0.0` against a package claiming `2.6.9`). **Cross-package:** WP5 owns the repo-level `NOTICE` / third-party inventory. |
| **C-14** Zero tests | **FIXED** | `Tests/FeatureLink.Tests/**` **(new, 9 files)** | **216 passing** | xUnit, justified in §4. Covers pagination, error bodies, the shape-style resolver, the config round-trip against CONFIG-FORMAT fixtures, the C-39 UID golden vectors, and the C-16 binding smoke test — exactly the list in the brief. |
| **C-39** UID case folding | **FIXED** | `Services/ArcGisFeatureService.cs` | `UidStabilityTests` (14), passing | `ToLowerInvariant()`, asserted under `en-US`/`de-DE`/`tr-TR`/`az-Latn-AZ`/`ar-SA`. Golden vectors were generated **independently by Python 3.13 and Node 24 on this machine**, both agreeing, and include the dotted/dotless-I case, CJK and an astral-plane emoji. Also fixed the unrelated half: uids were `FL-{index}-{timestamp}` and therefore unstable across downloads. |

### Also fixed (Appendix C items encountered while working the C-list)

| Finding | What changed |
|---|---|
| §1.1 zero logging in either tree | `Services/Log.cs` — `TraceSource` + a rolling file log at `%AppData%\WinTAK\FeatureLink\featurelink.log`, redacted. |
| §3.7 spatial reference never verified | `wkid != 4326/4269` now rejects the response instead of plotting Web Mercator metres as degrees. Coordinates range-checked; out-of-range and malformed features counted and reported. |
| §3.7 culture-sensitive attribute stringification | `JValue.ToString(InvariantCulture)` / `ToString(Formatting.None)`; `de-DE` covered by test. |
| §3.7 `FetchObjectIdFieldAsync` on every PLI tick | Cached per layer URL. |
| §3.7 username interpolated into an ArcGIS search query | Quoted and escaped; test with `a" OR owner:*`. |
| §3.7 `WaitForPublishJobAsync` result ignored; `EnableEditingAsync` swallowed | Both now fail loudly; publish window 60 s → 5 min. |
| §3.5 empty catch on the whole PLI path | Consecutive-failure counter, last-success timestamp, `IsPliConnected` driven by real send results, every failure logged and surfaced. |
| §3.5 PLI staleness == send interval; hardcoded `a-f-G-U-C`; CE/LE hardcoded 0 | 3× interval; real self CoT type; CE/LE from the self CoT point (`NaN` → JSON null). |
| §3.8 unvalidated CoT type; 365-day staleness | Validated against the CoT grammar, fallback `a-u-G` (**unknown**, not *friendly*); staleness 3× the refresh interval. |
| §3.9 file-read retry 1.5 s; watcher `Error` unsubscribed; 8 KB buffer | Exponential backoff to 30 s; `Error` handled; `InternalBufferSize` 64 KB. |
| §3.10 temp share files never deleted; reserved device names; name collisions | Deleted after 2 min; `CON`/`PRN`/… rejected; hash suffix for uniqueness; `visible` no longer hardcoded `true`. |
| §3.11 culture-sensitive `TryParse`/`StartsWith`; `≠` only; 6-digit hex only; unclamped opacity; `flds:[[]]` crash | All fixed; `!=`/`<>` accepted; 3/6/8-digit hex; opacity clamped incl. `NaN`; remarks capped at 4 KB. |
| §3.12 non-atomic save; no try/catch; corrupt file discards everything | `File.Replace`, `TrySave`, corrupt-file backup, per-element defensive parsing. |
| §3.13 `RecurrenceUnit` had **three** different defaults | One `DefaultRecurrenceUnit` constant; interval clamped. |
| §3.13 `LastSync` treats `Unspecified` as local | Treated as UTC. |
| §7 hardcoded `D:\Apps\Plugins\` failed every Debug build elsewhere | Conditional + `FEATURELINK_PLUGIN_DIR`. |
| §7 no `libs/` existence check → ~200 CS0246 | `VerifySdkLibs` target, one clear message. |
| §7 `python` assumed on PATH; SDK version a literal in 4 places | `py -3` (overridable); single `$(TakSdkVersion)`. |
| §7 no `<LangVersion>` | Pinned `7.3`. |
| §4 `SignInCommand.RaiseCanExecuteChanged` never called | Called from `RaiseAuthDependentPropertiesChanged`. |
| §4 `RunOnUi` falls back to the calling thread when `Application.Current` is null | Dispatcher captured at construction. |
| §5 `Dispose()` not idempotent, no `GC.SuppressFinalize`, no cancellation | Full dispose pattern + `CancellationTokenSource`. |
| §10 stale class doc claiming symbology/share/send-to-PLI are "out of scope" | Corrected; duplicate `<summary>` block removed. |

### NOT-A-DEFECT

| Finding | Evidence |
|---|---|
| §2.3 "`libs/`, `packages/`, `FeatureLink.csproj.user` are vendored into git" | Already recorded as a correction in the appendix itself; re-confirmed — `git ls-files WinTAK5.6` returns no path under `libs/`, `packages/`, `bin/`, `obj/`, `.vs/`. No action; kept that way. |
| §6 "cert-validation bypass" | Confirmed absent. `ServerCertificateValidationCallback` has zero hits in both trees. **Not introduced.** |
| §3.7 "`GetStringAsync` never checks HTTP status" (as literally worded) | `GetStringAsync` *does* throw on non-success. The real defect is the adjacent one — ArcGIS returns errors with HTTP **200** — which is what C-22 fixes. Recorded so the distinction is not lost. |

---

## 2. Deferred, with severity and estimate

| Item | Severity | Est. | Why deferred |
|---|---|---|---|
| **WinTAK 5.7 full re-fork** | **CRITICAL** | 6–7 d | Descoped by the owner. Full plan in `wp3-wintak-57-parity.md`. **Do not ship a 5.7 package.** |
| DI refactor (`IArcGisAuthService`/`IArcGisFeatureService`/`ISettingsStore`) + `FeatureLinkDockPaneTests` | HIGH | 2 d | The VM `new`s its services in field initialisers, so it is untestable without live network + a browser OAuth round trip. Blocks §9.6 of the appendix. |
| `CotEmissionTests` — **is `CotItem.SetAttribute` XML-escaping its input?** | **CRITICAL** | 0.5 d + SDK | Cannot be answered without the SDK. Peer-controlled callsign/remarks/iconsetpath reach broadcast CoT. Mitigated at our boundary (control chars stripped, length capped, iconset path format validated) but **not proven**. Manual capture in `docs/testing/wintak.md` §11.2. **Must be settled before delivery.** |
| Canonical `LayerKey(url)` normalisation | HIGH | 1 d | Systemic identity bug across ~8 sites (trailing slash, `/0` suffix, host case). Touches persistence, so it needs a migration story; too broad to land safely without a compiler. |
| `SplitViewToXaml` — break up the 1124-line `FeatureLinkView.xaml` | HIGH | 1.5 d | Unreviewable/unmergeable at this size, but any restructure is unverifiable without a XAML compiler. |
| Virtualization on the 5 `ItemsControl`s | HIGH | 0.5 d | 300-item accounts freeze the UI. Low risk but unverifiable. |
| `AutomationProperties` / keyboard nav / focus visuals / `.resx` localization | HIGH (508 compliance) | 3 d | Only 2 `AutomationProperties` added (on the new status strip and the PLI URL). Section 508 remains a stated compliance gap. |
| `MessageBox`/`OpenFileDialog`/`ContactPickerWindow` fully behind `IDialogService` | MEDIUM | 0.5 d | Partially done — `DialogService` covers confirms and share consent; the file dialog and contact picker are still direct. |
| Two-process settings coordination | HIGH | 0.5 d | Last writer still wins. |
| `.wpk` Authenticode signing | MEDIUM | 0.5 d | Needs a code-signing certificate — an owner decision, not a code change. |
| `libs/PROVENANCE.md` with SHA-256 of the 9 SDK DLLs | MEDIUM | 0.25 d | Needs the actual DLLs, which are not on this machine. |
| `PackageReference` migration from `packages.config` | HIGH | 0.5 d | A clean clone + `msbuild` still fails at the Newtonsoft reference unless `nuget restore` is run first. Unverifiable without msbuild; documented in the testing guide instead. |
| Startup scan for shares that arrived while WinTAK was closed | MEDIUM | 0.25 d | Watcher-only today. |
| `FeatureLinkModule.Initialize()` is a no-op → PLI/auto-refresh do not run until the panel is opened once | MEDIUM | 0.5 d | Real "set and forget" break after every WinTAK restart. Fixing it means moving construction out of the dock pane, which interacts with the DI refactor above. |

---

## 3. Defects found by the new tests (both in from-scratch code)

1. **`DisplayStyleResolver` class breaks.** A break whose `mn`/`mx` was **present but unparseable**
   fell back to ±infinity, silently turning a typo into a catch-all break that swallowed every
   feature and coloured the whole layer wrong. `TryBound` now distinguishes "absent, therefore
   unbounded" from "present but malformed, therefore skip", and logs the skip.
2. **`PliLayerUrl` was bound by nothing** in `FeatureLinkView.xaml` — the same class of defect as
   C-16, caught by the very smoke test C-16 asked for. An operator could not see which layer their
   position was being sent to. Now displayed in the PLI Feature Layer card.

A third, in the tooling: `build-wpk.py`'s `replace_element` used a naive `str.find`, so the 5.7
manifest's comment *quoting* the old `<id>`/`<version>` got rewritten instead of the live elements
— the first generated 5.7 package declared exactly the identity the comment was warning about.
Caught by running the script against the real manifest rather than a fixture; fixed in both trees.

---

## 4. Why xUnit, and why a source-linked net8.0 project

**xUnit over NUnit/MSTest:** `dotnet test` runs it with no extra runner or adapter configuration,
which matters because the only .NET toolchain guaranteed on a build box here is the SDK itself; its
fresh-instance-per-test model suits the static state these classes carry (`Log`, `DialogService`
hooks); and `Theory`/`InlineData` maps directly onto the table-driven suites the audit asked for
(6 sym types × 10 operators, 8 URL shapes, the culture matrix).

**Source link rather than a project reference:** `FeatureLink.csproj` is a legacy .NET Framework
4.8 WPF/MEF project that can only build against the WinTAK SDK assemblies in `WinTAK5.6\libs\`,
which are correctly git-ignored. If the test project referenced it, the tests could never run in
CI — which is precisely how this codebase ended up with zero tests. Linking the pure sources into
an SDK-style net8.0 project means the highest-value suites run with a bare `dotnet test` anywhere,
**and** gives the only compiler check any WinTAK code got during this work.

The deliberate cost: `FeatureLinkDockPane`, CoT emission and the WPF converters are not covered.

---

## 5. CROSS-PACKAGE REQUESTS

1. **WP5 (cross-cutting) — `NOTICE` / third-party inventory.** WinTAK's only non-SDK dependency is
   **Newtonsoft.Json 13.0.3 (MIT)**, now shipped inside the `.wpk` (C-18). It needs an entry.
   The 9 redistributed WinTAK SDK assemblies (`TAK.Engine`, `WinTak.*`, `Prism*`, ~32 MB) are
   `<Private>False</Private>` and **not** packaged, but their redistribution rights still need
   confirming in writing before any `.wpk` ships (C-36's own caveat).
2. **WP5 — CI.** Please add to the component matrix:
   `dotnet test WinTAK5.6/Tests/FeatureLink.Tests -f net8.0`. It needs no msbuild and no WinTAK
   SDK, so it can run on any runner today. Also worth a gate asserting the `.wpk` contains
   `Newtonsoft.Json.dll` (the C-18 regression) and that `WinTAK5.6/MANIFEST.xml` and
   `WinTAK5.7/MANIFEST.xml` never again share an `<id>` **or** a `<version>` (C-17).
3. **WP1 (ATAK) — C-23 / C-24, wire-format coordination.** WinTAK now reads **and writes** a
   compact `shp` block (`{"f":field,"s":{…},"bv":{value:{…}}}`, style keys
   `sc`/`sw`/`sd`/`fc`/`fs`, colours as `#aarrggbb`). This is the shape the WinTAK side needs for
   `DisplayConfig.toCompactJson()` to stop stripping shape styling in transit. If ATAK picks
   different key names, they must be reconciled — WinTAK's is the only implementation that exists
   today. **Also:** WinTAK deliberately implements the C-24 precedence **correctly** (per-value
   first, single as fallback), which is the *opposite* of ATAK's `DisplayConfig.java:277` and
   CloudTAK's `displayConfig.ts:306` as they stand. Until those are fixed, the same config renders
   differently on WinTAK than on ATAK/CloudTAK — a divergence, not a WinTAK bug.
4. **Docs owner — `CONFIG-FORMAT.md`.** It has no `shp` block documented and no producer/consumer
   matrix, which is what made Appendix F §5's break unfalsifiable from the documentation. The block
   as implemented is specified in this report and in `AutoSymbology.cs`'s header. Outside my
   boundary; not edited.
5. **WP4 (TAK Portal).** `TAKPortal/services/featurelinkArcgisIconset.service.js:236-244` was read
   as the C-07 reference and **not modified**.

---

## 6. OWNER DECISIONS NEEDED

Each was defaulted rather than blocked on; the default taken is stated. All are also appended to
`QUESTIONS-FOR-OWNER.md`.

1. **Ship WinTAK 5.7 at all?** *Default taken:* keep the tree, give it a distinct identity, mark it
   "INCOMPLETE PORT — not for release" in the manifest, the assembly and an MSBuild warning. It
   cannot compile as delivered (`libs/` is empty), so it is not usable today regardless.
2. **`shp` wire-format key names.** *Default taken:* the compact shape above, matching the existing
   `sym`/`lbl`/`popup` style. Needs ratifying in `CONFIG-FORMAT.md` before anything else produces it.
3. **Shape styling on WinTAK renders as a first-vertex marker.** *Default taken:* resolve the
   style, apply the stroke colour to that marker, log and count it. Genuine polyline/polygon
   rendering needs a WinTAK map-item API I could not verify exists.
4. **ArcGIS OAuth client ID.** Still the ATAK app's `RXtGmClVuYd1Sp7d`, and its own comment admits
   the loopback redirect was never confirmed allowlisted — **sign-in may not work at all against
   production.** *Default taken:* left in place but overridable via `FEATURELINK_ARCGIS_CLIENT_ID`.
   A dedicated WinTAK registration should be minted.
5. **Package version.** *Default taken:* 5.6 → `2.7.0` (was `2.6.9`; the README claimed it tracks
   the ATAK line, which was 15 patch versions ahead), 5.7 → `0.9.0-pre`.
6. **Company / copyright holder.** *Default taken:* `Civil Air Patrol — FeatureLink project`,
   Apache-2.0 per the repo `LICENSE`. Replace if the correct legal entity differs.
7. **Minimum auto-refresh interval, 30 s.** *Default taken:* clamps a peer-supplied `freq.iv`.
   Tighten or loosen to taste; the point is that `iv:1` must not be honoured.
8. **PLI CE/LE source.** *Default taken:* `selfEvent?.Point?.CE90/LE90`, inferred by analogy with
   the send-item path. **If those members do not exist, this will not compile** — replace with
   `double.NaN`.
9. **`.wpk` code signing.** *Default taken:* not done; needs a certificate.
10. **Deprecated/disabled UI cards** ("QR Configuration", etc.) still ship visibly dead. *Default
    taken:* left as-is — a product decision, not a code one.

---

## 7. Honest limitations

- **Nothing in `WinTAK5.6/` or `WinTAK5.7/` was compiled.** No msbuild, no WinTAK SDK. The dock
  pane went from 1455 to ~2160 lines without a compiler ever seeing it.
- **Unverified SDK assumptions**, in descending risk order: `selfEvent?.Point?.CE90/LE90`;
  `Dispatcher.InvokeAsync(Func<T>).Task`; `AutomationProperties.LiveSetting` in this XAML dialect;
  `MapItem.IsDisposed`/`Visible`/`Dispose()` and `MapMarker.Color` (all pre-existing, preserved
  verbatim); whether `Process()` has materialised a map item by the time `GetMapItem` is called
  (now logged when it has not, instead of silently skipping).
- **C-09 cannot be exercised** without a live ArcGIS app registration whose redirect allowlist
  includes the loopback URI.
- **The XAML binding smoke test is text analysis, not reflection**, because the VM cannot be loaded
  without the SDK. It catches the exact defect that shipped and it runs anywhere; it would not
  catch a binding whose *type* is wrong.
- **The C-39 golden vectors prove Python↔Node↔C# agreement.** They do **not** prove the Java side
  agrees — that needs a Java runner in the same test corpus, which is WP1's tree.
- **Only 2 `AutomationProperties`** were added. The Section 508 finding stands essentially
  untouched.
- Appendix C lists 275 actionable items. This package closes the 14 assigned C-IDs plus roughly 40
  of the supporting findings. **The remainder is in §2 with severities and estimates, not silently
  dropped.**
