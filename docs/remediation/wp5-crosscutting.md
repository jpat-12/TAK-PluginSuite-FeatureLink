# WP5 — Cross-cutting: CI/CD, versioning, governance, licensing, repo hygiene

**Branch:** `audit-remediation` · **Baseline:** `a8f6f4a`
**Scope note:** this package was **narrowed mid-execution** by owner decision. The original
brief covered CI, governance, licensing, versioning, the icon corpus, accessibility/VPAT,
supply-chain attestation, and the whole of Appendix E §3 documentation reconciliation. The
revised instruction was to finish what was in flight, produce test instructions, and stop —
explicitly **not** to start `NOTICE`/`THIRD-PARTY-NOTICES.md`, icon provenance, the VPAT, or
any §3 doc reconciliation, because the other four packages are still changing the code those
documents must describe. §4 lists everything deferred, honestly and with estimates.

> **The single most important line in this report:** **no workflow in this repository has
> ever executed on GitHub Actions.** Everything below marked FIXED was validated locally —
> YAML parsing, script execution, digest verification, index-blob inspection. Nothing was
> validated by a green CI run, because there has never been one. `docs/testing/ci-and-repo.md`
> tells the owner exactly which jobs are expected to pass, which are expected to *correctly*
> fail, and how to tell those apart.

---

## 1. Per-C-ID status

| C-ID | Status | Files touched | Test / evidence |
|---|---|---|---|
| **C-13** — CI has never executed and would fail if it did; two Actions script injections | **FIXED (unrun)** | `.github/workflows/{ci,security,sbom,release}.yml`, `.github/{dependabot.yml,gitleaks.toml,pinned-binaries.sha256,CODEOWNERS,pull_request_template.md}`, `.github/scripts/*.py` | All 4 workflows + `dependabot.yml` parse under PyYAML. 9+5+3+3 = 20 jobs, **every one with explicit `permissions:`**. **45** third-party action refs, **all** pinned by 40-char commit SHA (verified by regex sweep). Property-key bug fixed: CI now writes `atak.sdk.path` (what `build.gradle:44` reads), not `sdk.path`. All paths rebased onto the monorepo with `defaults.run.working-directory` + workspace-relative `upload-artifact` `path:`. Both injections eliminated — `release_notes` passes via `env:` as `"$RELEASE_NOTES"`; the version is read from the tracked `VERSION` file instead of `sed`-ed from dispatch input, so no untrusted value reaches the executed Gradle script. |
| **C-15** — versionCode frozen; both ATAK trees share one package identity | **PARTIAL** (policy + gate FIXED; source fixes are cross-package) | `VERSIONING.md`, `VERSION`, `.github/scripts/check_versions.py`, `ci.yml:version-policy` | Policy written and **enforced**. Gate run under python 3.13.14 → **exit 1, 14 violations**, correctly detecting `versionCode 5` as a hand-typed literal in both trees, the shared `applicationId`, `versionName` not embedding the ATAK target, and the shared WinTAK `<id>`. **The gate reported 17 violations when written and 14 now** — the three that cleared are WinTAK5.6's `MANIFEST.xml` and two `AssemblyInfo.cs` versions, fixed by WP3 in the interim. That is proof the gate tracks reality. Full remediation table in `docs/testing/ci-and-repo.md` §3.2. |
| **C-18** — `.wpk` omits `Newtonsoft.Json.dll` | **FIXED** (CI gate) | `.github/scripts/check_wpk.py`, `ci.yml:wintak` | Both paths verified with synthetic fixtures: package **without** Newtonsoft → **exit 1** naming the missing assembly; **with** → **exit 0**. A missing `.wpk` exits **2**, never 0. |
| **C-29** — untracked load-bearing source | **FIXED** (regression gate) | `ci.yml:clean-clone-build` | Closed at baseline `a8f6f4a`; this adds the gate preventing recurrence. Exports via `git archive HEAD` (tracked content only), then resolves every relative TypeScript import and every first-party Java import against the pristine tree, and `py_compile`s the Infra-TAK entry point. |
| **C-30** — non-reproducible builds, lockfile gitignored | **PARTIAL** (CI half FIXED) | `ci.yml`, `sbom.yml`, `.gitignore`, `TAKPortal/.gitignore` | CI uses `npm ci` and **hard-fails** if `CloudTAK/plugin/package-lock.json` is absent. SBOM job emits CycloneDX per component. Root and component ignores state explicitly that lockfiles are not ignored. `CloudTAK/.gitignore` is WP2's file and was **not touched**. |
| **C-35** — no privacy/security/governance | **PARTIAL** | `PRIVACY.md`, `SECURITY.md`, `.github/pinned-binaries.sha256`, `sbom.yml` | `PRIVACY.md` and `SECURITY.md` written from source, not from docs. `NOTICE` and `THIRD-PARTY-NOTICES.md` **not written** (descoped) — see §4. |
| **C-36** — licensing undeclared in 5 of 7 components | **PARTIAL / mostly cross-package** | (verification only) | Verified during this pass: `CloudTAK/plugin/package.json` now carries `"license": "Apache-2.0"` (WP2) and `TAKPortal/package.json` carries it too (WP4). Remaining: both `MANIFEST.xml`, both `.csproj`, `AssemblyInfo.cs`, `plugin.xml`, and the root `README.md` License section. The third-party inventory was **not** produced. |
| **C-37** — `lintCivRelease` never invoked | **FIXED** (CI half) | `ci.yml:atak` | `abortOnError` was already `true` and nothing ever ran lint. The `atak` matrix now runs `./gradlew lintCivRelease` as a hard gate. Cannot execute here (no SDK). |
| **C-40** — committed dev keystore | **PARTIAL — deliberate** | `.gitignore` ×3, `.github/pinned-binaries.sha256`, `SECURITY.md` | Both blobs pinned by SHA-256 (`5a5ec442…30a15`, byte-identical across trees) so a *substitution* is detectable. Signing posture documented in `SECURITY.md`. **The `!…featurelink.keystore` negations were deliberately left in place** — removing an ignore rule does not untrack an already-tracked file, so doing only my half would change nothing except requiring `-f` to re-add, while risking another agent's in-flight build. Annotated in-file as DEPRECATED-pending-C-40. |
| **Appendix E §2.1** — missing/redundant ignore & attribute files | **FIXED** | `.gitattributes`, 6 `.gitignore` files | Deleted 3 redundant per-component `.gitattributes` (byte-identical to root). Created `TAKPortal/.gitignore` and `Infra-TAK/.gitignore` (neither existed). Collapsed the four duplicated component ignores to genuine deltas. |
| **Appendix E §2.2** — line-ending handling actively wrong | **FIXED** | `.gitattributes` | See §2 below — verified at the index-blob level. |
| **Appendix E §2.3** — duplicated icon corpus | **QUANTIFIED, not fixed** | `.github/scripts/check_icon_corpora.py` | Full numbers in §3. Parity gate added; deduplication deferred. |
| **Appendix E §2.4** — ignore rule-level defects | **FIXED** | `.gitignore` | All seven filed defects fixed; verified zero tracked files newly ignored. |
| **Appendix E §6.3 / §7** — governance gaps | **FIXED** | `CONTRIBUTING.md`, `.github/CODEOWNERS`, `.github/pull_request_template.md`, `.github/dependabot.yml` | Branch-protection recommendation, stash flagged (not applied), stale branches recorded. |
| **Appendix E §3.6** — backup-file rot | **CONFIRMED CLEAN** | — | `README.md.bak` and `README2.md.bak` are deleted. `grep -rn "README2\?\.md\.bak"` across tracked files returns **only** the `.gitignore` rule that now matches both (`*.bak`, replacing the exact literal `README.md.bak` that missed `README2.md.bak`). Nothing references them. |
| **Appendix E §3** — documentation accuracy | **NOT STARTED — descoped** | — | See §4. |
| **Appendix F §2.4** — icon corpus "silent drift" | **NOT-A-DEFECT (confirmed struck)** | — | Independently re-verified: both corpora hold **7,461 files**, name-sets identical, **0 byte-differing files**, `manifest.json` md5 `f22c42c8955245ee3bbe44fc58bde1b4` in both. Not re-filed. The parity gate addresses only the forward-looking risk. |

---

## 2. Line endings — verified end state

The audit recorded 8 `LF will be replaced by CRLF` warnings from a bare `* text=auto`.

**Correct end state, now implemented:** LF in the index and LF on checkout everywhere, with a
short CRLF allowlist (`*.bat`, `*.cmd`, `*.ps1`, `*.sln`, `*.vcxproj`, `*.props`, `*.targets`)
for the genuinely CRLF-sensitive Windows set. `.NET`/WPF sources are CRLF-*tolerant*, so
`.cs`/`.xaml` stay LF.

**`*.sh` and `gradlew` are pinned to LF as a correctness requirement, not a preference** — a
CRLF shell script fails on Linux with `bad interpreter: /bin/bash^M`, and **both TAK Portal
and Infra-TAK install via shell script**.

Evidence (raw index blobs, via `git cat-file`, not the `ls-files --eol` summary):

```
TAKPortal/install.sh         CRLF=0  first line=b'#!/bin/bash'
TAKPortal/uninstall.sh       CRLF=0  first line=b'#!/bin/bash'
Infra-TAK/install.sh         CRLF=0  first line=b'#!/bin/bash'
Infra-TAK/uninstall.sh       CRLF=0  first line=b'#!/bin/bash'
CloudTAK/install.sh          CRLF=0  first line=b'#!/usr/bin/env bash'
ATAK5.6/gradlew              CRLF=0  first line=b'#!/bin/sh'
ATAK5.7/gradlew              CRLF=0  first line=b'#!/bin/sh'
```

Zero CRLF, no shebang carrying a trailing `\r`. A Linux clone gets LF; every installer runs.
`bash -n` passes on all five install/uninstall scripts.

An index-wide scan found **20** blobs containing CRLF byte sequences — **all binary** (PNGs,
the two takdev jars, the two gradle wrappers, the keystores). No *text* blob is stored with
CRLF, so **no `git add --renormalize` content churn was needed**. Those 20 are now explicitly
`binary` in `.gitattributes`, closing the `text=auto` misdetection risk.

Some working-tree files still show `w/crlf` — they were checked out before the fix and git
does not retroactively rewrite the working tree. Harmless (the index is LF, and `eol=lf` now
normalises on the way in). Refresh procedure in `docs/testing/ci-and-repo.md` §6.3.

---

## 3. Icon corpus — the orphan analysis nobody had run

Per corpus (`TAKPortal/assets/featurelink-configurator/icons/`, duplicated byte-for-byte at
`Infra-TAK/featurelink_displayconfig_assets/icons/`):

| Measure | Value |
|---|---|
| Files per corpus | 7,461 (7,450 PNG + `manifest.json` + 10 `iconset.xml`) |
| Iconsets declared | 11 |
| **PNG paths referenced by `manifest.json`** | **3,771** |
| Referenced but absent (broken references) | **0** |
| **On disk but unreferenced** | **3,679** |
| …explained as the `<set>/<group>/<file>` second copy of a referenced icon | **3,676** |
| …**genuinely unexplained** | **3** |
| **Unique image contents** | **3,659** |
| Intra-corpus duplicate PNG files | 3,791 |
| Bytes, one corpus | 6.95 MB |
| **Bytes, both corpora as committed** | **13.90 MB** |
| Bytes, unique images only | **3.45 MB** |

**The headline is not "3,679 orphans".** 3,676 of them are a deliberate dual layout — each
icon stored both flat at the iconset root and again under its group subdirectory. Reporting
them as deletable orphans would have been wrong. Two genuine findings fall out instead:

**(a) 3 unexplained files** — `Responder Icons/NIMS Positions/BLANK.png`,
`Responder Icons/Natural Hazards/Hazard--Other.png`, `Responder Icons/PrePlan/Foam.png`.

**(b) 2 name collisions — a latent interop bug.** Two icon names resolve to **different
bytes** depending on the lookup path, so two consumers using different path conventions
render **different images for the same manifest entry**:

- `Responder Icons/Human Caused Hazards/Hazard--Other.png` (836 b) ≠ `Responder Icons/Hazard--Other.png` (870 b)
- `Responder Icons/Incident/Foam.png` (1424 b) ≠ `Responder Icons/Foam.png` (1010 b)

**Deduplication opportunity:** 14,900 tracked PNGs represent **3,659 unique images / 3.45 MB**
— roughly **75%** reducible by collapsing to one shared corpus consumed by both components.
Git LFS is **not** indicated: `.git` is 7.0 MB and these are small, well-compressing,
rarely-changing files.

**Provenance and licensing: NOT established** (descoped). 11 third-party iconsets redistributed
under a repo declaring Apache-2.0 with no attribution and no licence record. Several names
(Google, OSM, FEMA, FalconView) strongly imply terms Apache-2.0 does not grant. Filed as an
owner question; no default was takeable.

---

## 4. Deferred — complete and honest

Ordered by risk. Estimates assume one engineer familiar with the tree.

### 4.1 Descoped by owner decision (not started)

| Item | Severity | Est. | Notes |
|---|---|---|---|
| `NOTICE` + `THIRD-PARTY-NOTICES.md` (C-36) | **HIGH** | 1–1.5 d | Apache-2.0 §4(d) requires propagating an upstream NOTICE. Known items: Newtonsoft.Json 13.0.3 (MIT — notice must be reproduced in distributions, and is not), the WinTAK SDK DLLs (terms undocumented), `atak-gradle-takdev.jar` (licence unstated), gradle-wrapper (Apache-2.0, unattributed), the npm dev-dependency trees. Blocked on the icon-provenance answer for completeness, but the software half can be written now. |
| Icon provenance & licensing | **HIGH** | 2–4 d, mostly external | Needs the original source of each of 11 iconsets established **in writing**. Cannot be resolved by reading the repository. Disqualifying at a licensing review if left open. |
| Appendix E §3 doc reconciliation — `README.md`, `capabilities.md`, `CONFIG-FORMAT.md`, `AUTO-ICONSET-SPEC.md`, 3× `WORKSTATUS.md`, per-component READMEs, `LICENSE` refs | **MEDIUM** (adjudicated down from CRITICAL — costs evaluator confidence, no operational impact) | 3–4 d | **Correctly deferred:** the other four packages were still rewriting the code these docs describe, so the work would have been stale on arrival. Must be done *after* they land. Includes the two highest-value pieces: the **4-mode × 5-implementation read/write conformance matrix** for `CONFIG-FORMAT.md` (whose absence made "Mode 3 shape styling is silently dropped on WinTAK" unfalsifiable from the docs), and the **MAJOR/MINOR forward-compatibility rule** — normative skew semantics for the `shp` block C-23 adds, treating `toCompactJson()` as the versioned wire format it actually is. |
| Section 508 / VPAT | **MEDIUM** (compliance) | 2–3 d | A VPAT is typically a mandatory deliverable on a federal award and **no auditor named 508**. The underlying findings exist and were rated LOW as code quality: 0 `@string/` references against 43 hardcoded strings, 0 RTL-capable layouts, no `values-night/`. They should be reframed as compliance blockers. |
| `CHANGELOG.md` | **MEDIUM** | 0.5 d | Nine releases, no record of any. Must record the unexplained `2.6.19` gap explicitly rather than skipping it. |
| Icon corpus deduplication | **MEDIUM** | 1–2 d | ~75% reduction; design decision required (submodule vs build-time copy vs package). The parity gate holds the line meanwhile. |
| Resolve the 2 icon name collisions | **LOW-MED** | 0.5 d | Needs an owner call on which of each pair is correct. |
| `.editorconfig`, `docs/adr/` | **LOW** | 0.5 d | Four languages, no formatting enforcement; substantive decisions recorded only in commit messages. |

### 4.2 Blocked on things outside this package

| Item | Blocker | Est. once unblocked |
|---|---|---|
| Turning `version-policy` green | 9 cross-package edits (§5) | 0.5 d |
| Any green CI run | Push to GitHub + first-run debugging | 0.5–1 d |
| ATAK / WinTAK CI jobs | SDK secrets not configured | 0.5 d |
| Complete SBOMs for ATAK/WinTAK | Same | included above |
| PLI purge capability | Lives in three other trees | 2–3 d |
| Config export/import | Lives in three other trees | 3–5 d |

---

## 5. CROSS-PACKAGE REQUESTS

**To WP1 (ATAK)** — `ATAK5.{6,7}/app/build.gradle`:
1. `ext.PLUGIN_VERSION = "2.7.0"` (match root `VERSION`).
2. Replace the literal `versionCode 5` with the derived expression — `maj*1000000 + min*10000 + pat*100 + 6` for 5.6, `+ 7` for 5.7 (→ 2 070 006 / 2 070 007).
3. `versionName "${PLUGIN_VERSION}+atak5.6"` / `+atak5.7`.
4. Distinct `namespace`: `com.atakmap.android.featurelink.atak56.plugin` / `…atak57.plugin`. **Without this the two builds mutually overwrite on a device.**
5. C-40: replace the four `tnttnt` literals with `System.getenv("TAK_KEYSTORE_PASSWORD") ?: project.findProperty('takReleaseKeyPassword')`, **failing the build when absent**; then `git rm --cached` both keystores in the same commit as the `.gitignore` negation removal.
6. `git rm --cached ATAK5.{6,7}/.takdev/plugin.properties` and commit the two `api.hash` values as an explicit non-generated input (the ignore rule is in place but cannot untrack).
7. Confirm `.github/pinned-binaries.sha256` digests still match after any `buildtools/` change.

**To WP2 (CloudTAK):** `"version": "2.7.0"` in `plugin/package.json`; remove `plugin/package-lock.json` from `CloudTAK/.gitignore` and commit the lockfile — the `cloudtak` CI job hard-fails without it.

**To WP3 (WinTAK):** `WinTAK5.7/MANIFEST.xml` → `<version>2.7.0</version>` **and a distinct `<id>FeatureLink-WinTAK57</id>`**; `AssemblyVersion`/`AssemblyFileVersion` → `2.7.0.0` in 5.7; populate `AssemblyCompany`/`AssemblyCopyright` with the real holder (both currently empty/unattributed); add `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>` to both `.csproj`; ensure `build-wpk.py` packages `Newtonsoft.Json.dll` — `check_wpk.py` gates it.

**To WP4 (server):** `"version": "2.7.0"` in `TAKPortal/package.json`; add `__version__ = "2.7.0"` to `Infra-TAK/featurelink_displayconfig.py`; **add a pinned `Infra-TAK/requirements.txt`** — without it the component's dependency set is undeclared, Dependabot cannot scan it and its SBOM records only itself; keep `install.sh`/`uninstall.sh` LF-only.

**To all packages — specified, not implemented:**
- **PLI purge** — a `purgePliFeatures()` in each REST client (`applyEdits` with `deletes`), exposed as an explicit operator action plus an end-of-session prompt. Until it exists, `PRIVACY.md` §6 must keep saying there is no way to delete PLI data.
- **Config export/import** — serialise saved layers, display configs and PLI settings to a portable file. Required before the C-15 `applicationId` change reaches a fielded device, because the manual uninstall it forces destroys all of that with no backup path.

---

## 6. Checklist §1 process item — line ranges for the integrator

**The standalone `audit-*.md` files do not exist.** `git ls-files | grep -i audit` returns only
four icon PNGs named `location-auditorium.png`. The content survives solely as Appendices A–D
inside `FEATURELINK-AUDIT-CHECKLIST.md`, which the brief forbids me to edit.

Each appendix reproduces its original file starting 6 lines after the appendix heading, so
`checklist_line = original_line + (appendix_start + 5)`. **All four ranges spot-verified** —
every one lands on `[SEV-CRITICAL]` items that are recommended *test suites*, not defects:

| Original citation | Appendix (starts) | Offset | **Line range in `FEATURELINK-AUDIT-CHECKLIST.md`** | Re-tag to |
|---|---|---|---|---|
| `audit-atak.md:535-537` | A (216) | +221 | **756–758** | `TEST` |
| `audit-cloudtak.md:474-477` | B (827) | +832 | **1306–1309** | `TEST` |
| `audit-cloudtak.md:635-637` | B (827) | +832 | **1467–1469** | `TEST` / `REMEDIATION` |
| `audit-wintak.md:491-494` | C (1481) | +1486 | **1977–1980** | `TEST` |
| `audit-server.md:346-417` | D (2027) | +2032 | **2378–2449** | `TEST` |

Spot-check evidence — checklist line 756 reads
`- [ ] **[SEV-CRITICAL]** \`DisplayConfigTest\` (JVM, \`org.json\`…) — round-trip every schema mode…`,
and 1977 reads
`- [ ] **[SEV-CRITICAL]** Create \`FeatureLink.Tests\` (net48, xUnit, Moq, FluentAssertions)…`.
Both are clearly recommended test suites mis-filed as defects. The §1 note that this inflates
the CRITICAL headline by 46% is correct.

Note the `audit-server.md:346-417` block (**2378–2449**, 72 lines) is mixed — it contains
`SEV-HIGH` entries interleaved. Re-tag per line, not wholesale.

---

## 7. Honest limitations

1. **No CI run has ever happened.** Every "FIXED" for C-13 means *the workflow is syntactically
   valid, its logic was reasoned through against the documented defects, and its non-SDK parts
   were exercised locally*. Expect ordinary first-run friction. Treat the first push as
   debugging the pipeline, not as validating the code.
2. **ATAK and WinTAK were never compiled.** No `gradle`, no `msbuild`, neither SDK. The `atak`
   and `wintak` jobs, the `lintCivRelease` gate (C-37) and the real `.wpk` check (C-18) are all
   `TEST-NOT-EXECUTED-LOCALLY`. `check_wpk.py` was proven with synthetic fixtures only.
3. **The version gate is red and that is the deliverable.** It is not a broken gate; it is a
   working gate against unfixed source. Do not weaken it to get a green tick.
4. **`CODEOWNERS` is currently inert.** GitHub silently ignores unresolvable owners, so
   "require review from Code Owners" would appear satisfied while reviewing nobody. Committed
   deliberately with an in-file warning; substituting real handles is a one-line edit.
5. **Action SHAs were resolved live from the GitHub API during this session** and are real, not
   invented. They will age; Dependabot is configured to bump them.
6. **`gitleaks` may flag the keystore on the first run.** That is the correct detection of C-40,
   not a false positive. Do not allowlist it until the rotation in `SECURITY.md` is done.
7. **Concurrency.** Four other agents were editing the tree throughout. Line numbers cited in
   the audit had shifted; I re-derived every one I depended on. Two of my files were swept into
   other packages' commits by the integrator (`docs/testing/ci-and-repo.md` into `e647400`) —
   content intact, attribution blurred.
8. **`PRIVACY.md` §6 is a statement about current behaviour, not a solved problem.** I could not
   implement the purge; it lives in three other trees. The document says so plainly rather than
   implying a control that does not exist — a privacy notice that omitted it would be false.

---

## 8. Commits

| Commit | Content |
|---|---|
| `abf2ed4` | Line-ending policy repo-wide (Appendix E §2.1, §2.2) |
| `e87865a` | `.gitignore` rule-level defects and coverage gaps (§2.1, §2.4; supports C-30) |
| `51af2d3` | `VERSIONING.md` (C-15 policy) |
| *(WIP, integrator)* | `.github/` tree — 4 workflows, dependabot, gitleaks, pinned digests, 3 gate scripts, root `VERSION` |
| `4158c59` | `CODEOWNERS`, PR template, `CONTRIBUTING.md` (§6.3, §7) |
| `6d5ff92` | `PRIVACY.md`, `SECURITY.md` (C-35) |
| *(in `e647400`)* | `docs/testing/ci-and-repo.md` |
| *(this)* | This report + `QUESTIONS-FOR-OWNER.md` additions |
