# FeatureLink Suite — Versioning Policy

**Status:** normative. CI enforces every MUST in this document via
`.github/scripts/check_versions.py`, run by the `version-policy` job in
`.github/workflows/ci.yml`. A pull request that violates a MUST does not merge.

**Audit provenance:** this document exists to close **C-15** (adjudicated CRITICAL) and the
version half of **C-17**. Appendix E §4.1 carries the full pre-remediation inventory of 29
declared version strings; §4.2 carries the defects. The headline defect it fixes:

> `ATAK5.6/app/build.gradle` and `ATAK5.7/app/build.gradle` both declared
> `PLUGIN_VERSION = "2.7.24"`, `versionCode 5`, the same `namespace`, and the same signing
> identity. Two separately-built, separately-shipped artifacts with **one package identity**.
> They mutually overwrite on a device. And because `versionCode` reached only `4` across the
> eight releases `2.6.16 … 2.6.24` — it was not incremented per release — Android's sole
> upgrade discriminator was frozen, so **every fielded device has silently failed to upgrade
> across all nine releases**. For side-loaded public-safety software that is an operational
> failure, not a hygiene issue.

---

## 1. One suite version

The suite has **one** version number, in SemVer 2.0.0 form `MAJOR.MINOR.PATCH`. Every
component declares that same number. There is no per-component version line.

| Component | Where the suite version MUST appear |
|---|---|
| ATAK 5.6 | `ATAK5.6/app/build.gradle` → `ext.PLUGIN_VERSION` |
| ATAK 5.7 | `ATAK5.7/app/build.gradle` → `ext.PLUGIN_VERSION` |
| WinTAK 5.6 | `WinTAK5.6/MANIFEST.xml` → `<version>`, and `Properties/AssemblyInfo.cs` → `AssemblyVersion`/`AssemblyFileVersion` |
| WinTAK 5.7 | `WinTAK5.7/MANIFEST.xml` → `<version>`, and `Properties/AssemblyInfo.cs` → `AssemblyVersion`/`AssemblyFileVersion` |
| CloudTAK | `CloudTAK/plugin/package.json` → `version` |
| TAK Portal | `TAKPortal/package.json` → `version` |
| Infra-TAK | `Infra-TAK/featurelink_displayconfig.py` → `__version__` |

The canonical source is `VERSION` at the repository root: a single line containing the
suite version and nothing else. Every other declaration is checked against it.

**MUST:** all seven declarations equal the contents of `VERSION`.
**MUST NOT:** any component carry an independent version line. `CloudTAK/plugin/package.json`
was at `0.1.0` — unrelated to the suite's `2.x` — so nothing mapped a CloudTAK build to the
ATAK/WinTAK release it was interop-compatible with, despite all three sharing the
`CONFIG-FORMAT.md` contract. `AssemblyVersion("1.0.0.0")` against `MANIFEST.xml` `2.6.9`
meant Windows file properties, crash telemetry and support triage all reported a version the
package never advertised.

### 1.1 What increments what

Because the suite's components interoperate through `CONFIG-FORMAT.md`, the version
increment is chosen by **wire-format impact**, not by how much code changed:

| Change | Increment | Example |
|---|---|---|
| A `CONFIG-FORMAT.md` MAJOR change — an older client can no longer safely read a new config | **MAJOR** | removing a required key; changing the meaning of `v` |
| A `CONFIG-FORMAT.md` MINOR change — new optional keys, older clients ignore them and stay correct | **MINOR** | adding the `shp` block (C-23) |
| A new user-visible capability with no wire-format change | **MINOR** | auto-symbology |
| Bug fix, security fix, doc fix, refactor | **PATCH** | pagination fix (C-06) |

**MUST:** a release that adds a key to the persisted schema increments at least MINOR, and
the corresponding `CONFIG-FORMAT.md` `_version` MINOR is incremented in the same commit.

**Prohibited pattern — carrying the patch number across a minor bump.** The working tree at
audit time went `2.6.24 → 2.7.24`, incrementing MINOR while *preserving* the PATCH. That is
semantically incoherent and makes the two lines indistinguishable in a sorted release list.
A minor bump resets PATCH to `0`. **MUST:** if MINOR increases, PATCH becomes `0`.

---

## 2. Per-ATAK-target identity (the fix for the mutual-overwrite defect)

ATAK 5.6 and ATAK 5.7 are **separate products** built from separate trees against separate
SDKs. They MUST be independently installable and independently upgradable on the same device.

**MUST:** each ATAK target declares a distinct `applicationId` / `namespace`:

```
ATAK 5.6 →  com.atakmap.android.featurelink.atak56.plugin
ATAK 5.7 →  com.atakmap.android.featurelink.atak57.plugin
```

**MUST:** `versionName` embeds the ATAK target using SemVer build metadata:

```
versionName = "${PLUGIN_VERSION}+atak5.6"      e.g. 2.7.0+atak5.6
versionName = "${PLUGIN_VERSION}+atak5.7"      e.g. 2.7.0+atak5.7
```

Prior to this, disambiguation relied entirely on the `archivesBaseName` filename suffix. The
filename is not carried into the installed package: inside the APK, `versionName`,
`packageName` and the `plugin-api` metadata were identical between the two builds. An
operator holding a device could not answer "which build is this?", and a support engineer
reading a crash report could not either.

The same rule applies to the WinTAK pair, whose `MANIFEST.xml` files both declared
`<id>FeatureLink</id>` and `<version>2.6.9</version>` while shipping materially different
codebases (C-17):

```
WinTAK 5.6 →  <id>FeatureLink</id>          <version>{suite}</version>   <sdkVersion>5.6.0.151</sdkVersion>
WinTAK 5.7 →  <id>FeatureLink-WinTAK57</id> <version>{suite}</version>   <sdkVersion>5.7.0.144</sdkVersion>
```

**MUST:** the two WinTAK `MANIFEST.xml` files declare distinct `<id>` values. WinTAK's plugin
loader identifies packages by id+version; installing one over the other with the same id and
the same version is undefined behaviour.

---

## 3. `versionCode` — derived, never hand-edited

Android uses `versionCode` as the **sole** upgrade discriminator. It is an integer, it must
increase on every release, and it must never be typed by a human.

**MUST:** `versionCode` is computed from the suite version and the ATAK target ordinal:

```
versionCode = MAJOR * 1_000_000
            + MINOR *    10_000
            + PATCH *       100
            + ATAK_TARGET_ORDINAL          # 6 for ATAK 5.6, 7 for ATAK 5.7
```

Worked example — suite `2.7.0`:

| Target | versionCode |
|---|---|
| ATAK 5.6 | `2*1000000 + 7*10000 + 0*100 + 6` = **2 070 006** |
| ATAK 5.7 | `2*1000000 + 7*10000 + 0*100 + 7` = **2 070 007** |

Properties this gives you:

- **Strictly monotonic by construction.** Any version increment raises `versionCode`, so the
  frozen-at-4 defect is structurally unreachable.
- **Reversible.** Given a `versionCode` from a field device you can recover the exact suite
  version and ATAK target — which is what makes "which build is this?" answerable.
- **Headroom.** The last two digits reserve 100 target ordinals; PATCH has 100 slots per
  MINOR; the ceiling is `MAJOR ≤ 2147`, comfortably inside Android's 2 100 000 000 limit.
- **No build-number input.** Deriving `versionCode` from a CI run number (the other common
  approach) makes it non-reproducible: rebuilding the same tag would produce a different
  code. This formula is a pure function of tracked source.

**MUST NOT:** `versionCode` appear as a literal in `build.gradle`. It is computed in the
Gradle script from `PLUGIN_VERSION` and `ATAK_VERSION`.

### 3.1 The CI monotonicity gate

`.github/scripts/check_versions.py`, run on every push and pull request, asserts:

1. Every one of the seven component declarations equals `VERSION`.
2. `VERSION` is well-formed SemVer, and PATCH is `0` whenever MINOR changed relative to the
   merge base.
3. Each ATAK tree's `versionCode` equals the formula applied to `VERSION` and its own
   `ATAK_VERSION`.
4. The two ATAK trees declare **different** `namespace` / `applicationId` values.
5. The two WinTAK trees declare **different** `MANIFEST.xml` `<id>` values.
6. `MANIFEST.xml` `<version>` equals `AssemblyVersion` equals `AssemblyFileVersion` equals
   `VERSION`, in both WinTAK trees.
7. **Monotonicity:** for each ATAK target, the computed `versionCode` is **strictly greater**
   than the `versionCode` of the same target at the merge base (on a PR) or at the previous
   release tag (on `main`). This is the assertion whose absence let nine releases ship with a
   frozen upgrade discriminator.

The gate fails the build. It does not warn.

---

## 4. Release tags and artifact names

Tags are namespaced per target, because a single `v{version}` tag cannot describe two
artifacts. The pre-existing `release.yml` ran `gh release create "v${version}"` from
**byte-identical** workflows in both ATAK trees, so releasing 5.7 at a version already
released for 5.6 would have collided on the tag or overwritten the 5.6 release.

```
v{version}                    suite-wide release; the umbrella tag
v{version}-atak5.6            ATAK 5.6 artifact
v{version}-atak5.7            ATAK 5.7 artifact
v{version}-wintak5.6          WinTAK 5.6 artifact
v{version}-wintak5.7          WinTAK 5.7 artifact
v{version}-cloudtak
v{version}-takportal
v{version}-infratak
```

**MUST:** every published artifact is accompanied by a `SHA256SUMS` file and a CycloneDX
SBOM, and is covered by an `actions/attest-build-provenance` attestation (C-35, EO 14028 /
NIST SSDF). `.github/workflows/release.yml` produces all three; a release built any other way
is not a release.

---

## 5. Compatibility matrix

Published in `README.md` and maintained with every release. It states, for each suite
version, which host versions it supports and which `CONFIG-FORMAT.md` schema versions it can
read and write. Nothing previously stated which FeatureLink version supported which
ATAK/WinTAK/CloudTAK version, or the EOL policy — `capabilities.md` gestured at this and was
materially false.

The supported-version window is defined in `SECURITY.md`.

---

## 6. Known historical gaps (recorded, not fixed)

- **`2.6.19` has no release artifact.** The sequence is `2.6.16, .17, .18, [19 missing], .20,
  .21, .22, .23, .24`. Either a pulled release with no record of why, or an untracked build.
  With no `CHANGELOG.md` there is no way to determine which. Recorded in `CHANGELOG.md` as an
  explicit unexplained gap rather than silently skipped.
- **Nine releases were built on a developer workstation**, not by CI — the four workflows that
  existed were in `<component>/workflows/`, where GitHub never looks, so they had never
  executed (C-13). No released artifact prior to this remediation has provenance.

---

## 7. Migration for devices already carrying a broken build

Raising `versionCode` from the frozen `5` to the derived value fixes upgrades **going
forward**. It does not repair a device already carrying a build whose `versionCode` is `5`
under the *old* `applicationId`: because §2 changes the `applicationId` per target, the new
package is a **different application** to Android, so it installs alongside rather than over
the old one, and the old one must be uninstalled manually.

**Uninstalling destroys all locally saved layers, display configs and PLI settings, and there
is no export or backup capability in any client.** That is a real, unmitigated operational
consequence of this fix, tracked as an open gap in
`docs/remediation/wp5-crosscutting.md` and `QUESTIONS-FOR-OWNER.md`. The required capability
is specified there. **No version-scheme change should reach a fielded device before config
export/import ships.**
