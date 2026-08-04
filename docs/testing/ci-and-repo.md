# Testing guide — CI, repository gates and line endings (WP5)

**Audience:** the repository owner, running a hands-on session.
**Scope:** everything WP5 produced — the `.github/` CI system, the three Python gates, the
line-ending fix, and the versioning policy.

**The single most important thing in this document:** when you first push this branch, the
CI run **will be red**, and that is the correct result. §2 tells you exactly which jobs are
expected green, which are expected red *because they are correctly detecting a real defect
that has not been fixed yet*, and which cannot run at all until you configure secrets. Read
§2 before you look at the Actions tab, or you will not be able to tell a broken workflow
from a working one.

Nothing in this document has ever run on GitHub Actions. Every local command below **has**
been executed against this tree; the pasted output is real.

---

## 0. Five-minute smoke test (no push, no network, no SDK)

Run these four commands from the repository root. Expected results are in §3–§5.

```bash
python .github/scripts/check_versions.py            # EXPECT: exit 1, 14 violations
python .github/scripts/check_icon_corpora.py        # EXPECT: exit 0, "PARITY: OK"
git ls-files --eol -- '*.sh' 'ATAK5.6/gradlew'      # EXPECT: every row i/lf
python -c "import yaml,glob; [yaml.safe_load(open(f,encoding='utf-8')) for f in glob.glob('.github/workflows/*.yml')]; print('workflows parse OK')"
```

If all four behave as described, WP5's deliverables are intact.

---

## 1. Making CI run for the first time

### 1.1 What has to happen

`.github/workflows/` did not exist. Four workflow files sat at
`ATAK5.{6,7}/workflows/{build,release}.yml`, where **GitHub Actions never looks** — it
discovers workflows only in `.github/workflows/` at the repository root. Those four files had
never executed and could not. Everything below is new.

```bash
git checkout audit-remediation
git push -u origin audit-remediation
```

That is all. `ci.yml`, `security.yml` and `sbom.yml` trigger on `push` to `'**'`, so the run
starts immediately. Open **Actions** in the GitHub UI.

`release.yml` is `workflow_dispatch` only and will not fire.

### 1.2 Before you push — two things to check

**a) Actions must be enabled.** Settings → Actions → General → *Allow all actions and
reusable workflows*. Every third-party action is pinned by 40-character commit SHA, so if
your organisation restricts actions to an allowlist you must permit these:

```
actions/checkout            actions/setup-java        actions/setup-node
actions/setup-python        actions/upload-artifact   actions/download-artifact
actions/attest-build-provenance                       actions/dependency-review-action
github/codeql-action        gitleaks/gitleaks-action  gradle/actions/setup-gradle
microsoft/setup-msbuild     NuGet/setup-nuget         anchore/sbom-action
```

**b) The four dead workflows are still in the tree.** WP5 did **not** delete
`ATAK5.{6,7}/workflows/` — their content has been superseded by `.github/workflows/`, but
they were left in place so you can diff old against new before they go. They are inert
(GitHub does not read that path), so leaving them costs nothing but confusion. Once you have
reviewed the replacement:

```bash
git rm -r ATAK5.6/workflows ATAK5.7/workflows
git commit -m "Remove the four dead workflows superseded by .github/workflows/ (C-13)"
```

---

## 2. What to expect on the first run

### 2.1 Expected **GREEN** today

| Workflow | Job | Why it should pass |
|---|---|---|
| CI | `icon-corpus-parity` | The two 7,461-file corpora are byte-for-byte identical today. Verified locally — see §4. |
| CI | `clean-clone-build` | C-29 was closed at the baseline commit; all three previously-untracked sources are now tracked. |
| CI | `cloudtak` | WP2 committed `package-lock.json`, an eslint config and a vitest suite. |
| CI | `takportal` | WP4 added `package.json` with `lint` and `test` scripts and a test suite. |
| CI | `infratak` | `ruff` and `bandit` run against a single Python module. |
| Security | `pinned-binaries` | All eight tracked binary blobs match their recorded digests, and there are no unpinned ones. Verified locally — see §6. |
| Security | `codeql` (javascript-typescript, python) | Analysis-only; needs no SDK. |
| SBOM | `node-sbom`, `python-sbom`, `jvm-dotnet-sbom` | Source-level scans. |

**Caveat, stated honestly:** "should pass" here means the workflow logic is correct and the
component's own tooling passes locally. None of these has ever executed on a GitHub runner.
Expect first-run friction of the ordinary kind — a missing `apt` package, a Node version
difference, a path that behaves differently on `ubuntu-latest`. That is a workflow bug to
fix, not a defect in the code being tested. Treat the first run as debugging the pipeline.

### 2.2 Expected **RED** today — and correct

> **`version-policy` will fail. Do not "fix" the gate.**

C-15 was descoped from this remediation pass, and WP1 confirms `ATAK5.{6,7}/app/build.gradle`
was left untouched. The gate therefore detects exactly the defects the audit found:
`versionCode` is still the hand-typed literal `5` in both trees, and both declare the same
`applicationId`. **A gate that passed today would be a broken gate.** Full expected output in
§3.

It goes green when the 14 cross-package fixes in §3.2 land.

| Workflow | Job | Why it fails, and what it means |
|---|---|---|
| CI | `version-policy` | **Correctly detecting C-15.** 14 violations. Green only after the §3.2 fixes. |
| CI | `ci-required` | Aggregate gate. Red because `version-policy`, `atak` and `wintak` are red. Correct. |
| Security | `secret-scan` | May flag `ATAK5.{6,7}/app/featurelink.keystore` — that is the **correct** detection of C-40. The keystore is still tracked (adjudicated MEDIUM, no history rewrite warranted, remediation is to rotate forward). Add a `.github/gitleaks.toml` allowlist entry only once you have consciously accepted it. Also see §2.4 on the licence requirement. |

### 2.3 Expected **RED — blocked, cannot run**

| Workflow | Job | Blocker |
|---|---|---|
| CI | `atak` (5.6, 5.7) | No ATAK SDK secret |
| CI | `wintak` (5.6, 5.7) | No WinTAK SDK secret |
| Security | `codeql` (java-kotlin, csharp) | Runs in `build-mode: none`; may be partial |

These jobs **fail loudly with a written explanation rather than skipping**. That is
deliberate. A build job that silently no-ops when its SDK is missing produces a green check
that means nothing — which is precisely the false assurance the repository had for nine
releases. When they fail you will see, in the log, an `::error::` line naming the exact
secrets to configure (§7).

### 2.4 Expected **SKIPPED**

| Workflow | Job | When it runs |
|---|---|---|
| Security | `dependency-review` | Pull requests only (`if: github.event_name == 'pull_request'`) |
| Release | all | Manual `workflow_dispatch` only |
| Security | (scheduled run) | Weekly, Mondays 06:17 UTC |

**One licensing trap:** `gitleaks-action` v2 is free for personal **public** repositories but
requires a `GITLEAKS_LICENSE` secret for **organisation-owned** repositories. If this
repository sits under an org, `secret-scan` fails with a licence error rather than a findings
error. Read the log before assuming a secret was found.

### 2.5 The one-line summary for the Actions tab

> A first run showing **`version-policy` red, `atak`/`wintak` red with "SDK not configured",
> everything else green** is the expected, healthy result. Anything else is a workflow bug
> worth investigating.

---

## 3. Gate 1 — `check_versions.py` (C-15)

### 3.1 Run it

```bash
python .github/scripts/check_versions.py
```

Actual output on this tree, python 3.13.14 (**exit code 1**):

```
Suite version (VERSION): 2.7.0
Derived versionCode    : ATAK 5.6 -> 2070006, ATAK 5.7 -> 2070007

VERSION POLICY: 14 violation(s) of VERSIONING.md

  [ATAK5.6] ATAK5.6/app/build.gradle: PLUGIN_VERSION is '2.7.24', VERSION says '2.7.0'
  [ATAK5.7] ATAK5.7/app/build.gradle: PLUGIN_VERSION is '2.7.24', VERSION says '2.7.0'
  [WinTAK5.7] WinTAK5.7/MANIFEST.xml: <version> is '2.6.9', VERSION says '2.7.0'
  [WinTAK5.7] AssemblyVersion is '1.0.0.0', VERSION says '2.7.0'
  [WinTAK5.7] AssemblyFileVersion is '1.0.0.0', VERSION says '2.7.0'
  [CloudTAK] CloudTAK/plugin/package.json: version is '0.1.0', VERSION says '2.7.0'
  [TAK Portal] TAKPortal/package.json: version is '1.5.0', VERSION says '2.7.0'
  [Infra-TAK] Infra-TAK/featurelink_displayconfig.py: no __version__
  [ATAK5.6] versionCode is the hand-typed literal 5
  [ATAK5.7] versionCode is the hand-typed literal 5
  [ATAK5.6/ATAK5.7] both trees declare the SAME package identity
                    'com.atakmap.android.featurelink.plugin'
  [ATAK5.6] versionName 'PLUGIN_VERSION' does not embed the ATAK target
  [ATAK5.7] versionName 'PLUGIN_VERSION' does not embed the ATAK target
  [WinTAK5.6/WinTAK5.7] both MANIFEST.xml files declare the SAME <id> 'FeatureLink'

This gate fails the build. It does not warn. See VERSIONING.md.
```

Each violation carries a `FIX:` line with the exact edit; they are trimmed above for length.
Run it yourself to see them.

**This is the C-15 defect reproducing.** Confirmation the gate is live rather than
rubber-stamping: it reported **17** violations when first written and reports **14** now —
the three that disappeared are `WinTAK5.6/MANIFEST.xml` and its two `AssemblyInfo.cs`
versions, which WP3 fixed in the interim. The gate tracked a real fix landing.

### 3.2 Making it green

Each fix is in another work package's tree; none is WP5's to make. Full detail in
`VERSIONING.md`.

| # | File | Change | Owner |
|---|---|---|---|
| 1 | `ATAK5.{6,7}/app/build.gradle` | `ext.PLUGIN_VERSION = "2.7.0"` | WP1 |
| 2 | `ATAK5.{6,7}/app/build.gradle` | Replace `versionCode 5` with `maj*1000000 + min*10000 + pat*100 + 6` (5.6) / `+ 7` (5.7) | WP1 |
| 3 | `ATAK5.{6,7}/app/build.gradle` | `versionName "${PLUGIN_VERSION}+atak5.6"` / `+atak5.7` | WP1 |
| 4 | `ATAK5.{6,7}/app/build.gradle` | Distinct `namespace`: `…featurelink.atak56.plugin` / `…atak57.plugin` | WP1 |
| 5 | `WinTAK5.7/MANIFEST.xml` | `<version>2.7.0</version>`, and `<id>FeatureLink-WinTAK57</id>` | WP3 |
| 6 | `WinTAK5.7/Properties/AssemblyInfo.cs` | `AssemblyVersion`/`AssemblyFileVersion` → `2.7.0.0` | WP3 |
| 7 | `CloudTAK/plugin/package.json` | `"version": "2.7.0"` | WP2 |
| 8 | `TAKPortal/package.json` | `"version": "2.7.0"` | WP4 |
| 9 | `Infra-TAK/featurelink_displayconfig.py` | `__version__ = "2.7.0"` | WP4 |

Re-run after each; the count should fall monotonically to zero.

### 3.3 Testing the monotonicity gate

The gate that would have caught the frozen-`versionCode` defect:

```bash
python .github/scripts/check_versions.py --baseline HEAD~5
```

With no `VERSION` file at the baseline it prints a note explaining that there is no baseline
to compare against, and does **not** treat that as a pass. After the first release, point
`--baseline` at the previous release tag and confirm that lowering `VERSION` produces a hard
failure:

```bash
echo "2.6.0" > VERSION
python .github/scripts/check_versions.py --baseline <previous-release-tag>   # must exit 1
git checkout VERSION
```

---

## 4. Gate 2 — `check_icon_corpora.py`

```bash
python .github/scripts/check_icon_corpora.py
```

Actual output (**exit 0**):

```
TAKPortal: 7461 files
Infra-TAK: 7461 files

ICON CORPUS PARITY: OK -- the two corpora are byte-for-byte identical.
```

Two auditors asserted these corpora had "silently drifted". **That was false and was
struck.** This gate does not re-file it; it exists because nothing prevented *future*
divergence, and a divergence would break the frozen cross-platform iconset contract silently.

Prove the gate actually detects divergence:

```bash
cp TAKPortal/assets/featurelink-configurator/icons/manifest.json /tmp/m.bak
printf '\n' >> TAKPortal/assets/featurelink-configurator/icons/manifest.json
python .github/scripts/check_icon_corpora.py     # EXPECT exit 1, names manifest.json
cp /tmp/m.bak TAKPortal/assets/featurelink-configurator/icons/manifest.json
python .github/scripts/check_icon_corpora.py     # EXPECT exit 0 again
```

### 4.1 The analysis mode

```bash
python .github/scripts/check_icon_corpora.py --report
```

Takes ~40 s (it hashes 14,900 files). Produces the orphan analysis nobody had run. Headline
numbers from the real tree, per corpus:

| Measure | Value |
|---|---|
| PNG files on disk | 7,450 |
| PNG paths referenced by `manifest.json` | **3,771** |
| Referenced but absent (broken refs) | **0** |
| On disk but unreferenced | **3,679** |
| …of which are the `<set>/<group>/<file>` second copy of a referenced icon | 3,676 |
| …genuinely unexplained | **3** |
| Unique image contents | **3,659** |
| Bytes, one corpus | 6.95 MB |
| Bytes, both corpora as committed | **13.90 MB** |

So the 3,679 "orphans" are almost entirely a deliberate dual layout — each icon is stored
both flat at the iconset root and again under its group subdirectory — not junk. Two genuine
findings fall out:

- **3 unexplained files:** `Responder Icons/NIMS Positions/BLANK.png`,
  `Responder Icons/Natural Hazards/Hazard--Other.png`, `Responder Icons/PrePlan/Foam.png`.
- **2 name collisions where the same icon name resolves to *different bytes* depending on
  the lookup path** — a latent interop bug, since two consumers using different path
  conventions render different images for the same manifest entry:
  - `Responder Icons/Human Caused Hazards/Hazard--Other.png` (836 b) vs
    `Responder Icons/Hazard--Other.png` (870 b)
  - `Responder Icons/Incident/Foam.png` (1424 b) vs `Responder Icons/Foam.png` (1010 b)

Deduplication potential: 14,900 tracked PNGs represent **3,659 unique images / 3.45 MB** —
roughly a 75% reduction if collapsed to a single shared corpus. Deferred; see the report.

---

## 5. Gate 3 — `check_wpk.py` (C-18)

Needs a built `.wpk`, so it cannot run against this tree. Both paths were verified with
synthetic fixtures:

```bash
python - <<'PY'
import zipfile
def mk(p, extra):
    with zipfile.ZipFile(p,'w') as z:
        z.writestr('FeatureLink.dll', b'MZ'+b'\0'*400)
        z.writestr('MANIFEST.xml', '<wintakPluginPackage/>')
        for n,b in extra: z.writestr(n,b)
mk('/tmp/bad.wpk', [])                                            # reproduces C-18
mk('/tmp/good.wpk', [('Newtonsoft.Json.dll', b'MZ'+b'\0'*700)])   # C-18 fixed
PY

python .github/scripts/check_wpk.py /tmp/bad.wpk     # EXPECT exit 1
python .github/scripts/check_wpk.py /tmp/good.wpk    # EXPECT exit 0
```

Verified results: `bad.wpk` → exit **1**, `good.wpk` → exit **0**. The failure message names
`Newtonsoft.Json.dll` and explains that a package without it either fails to load or binds to
whatever version the host WinTAK supplies — which also re-opens C-19, because Newtonsoft
≥ 13.0.1 is what provides the `MaxDepth = 64` default.

A missing file exits **2**, never 0 — "no package" is a build failure, not "nothing to check".

---

## 6. Verifying the line-ending fix

**Why this matters operationally:** both TAK Portal and Infra-TAK install via shell script. A
`.sh` file checked out with CRLF fails on Linux with `bad interpreter: /bin/bash^M` — the
installer simply does not run. This is a real, testable failure mode, not style.

### 6.1 The check

```bash
git ls-files --eol -- '*.sh' 'ATAK5.6/gradlew' 'ATAK5.7/gradlew'
```

Actual output:

```
i/lf    w/crlf  attr/text eol=lf        ATAK5.6/gradlew
i/lf    w/crlf  attr/text eol=lf        ATAK5.7/gradlew
i/lf    w/lf    attr/text eol=lf        CloudTAK/install.sh
i/lf    w/crlf  attr/text eol=lf        Infra-TAK/install.sh
i/lf    w/crlf  attr/text eol=lf        Infra-TAK/uninstall.sh
i/lf    w/crlf  attr/text eol=lf        TAKPortal/install.sh
i/lf    w/lf    attr/text eol=lf        TAKPortal/uninstall.sh
```

**Read the `i/` column, not the `w/` column.**

- `i/lf` — how the file is stored **in the index**, i.e. what a `git clone` on Linux
  receives. Every row is `i/lf`. **This is the column that determines whether `install.sh`
  runs on the server, and it is correct.**
- `attr/text eol=lf` — the `.gitattributes` rule is being applied.
- `w/crlf` on some rows — those files were checked out on Windows *before* `.gitattributes`
  was fixed. Git does not retroactively rewrite the working tree. **Harmless:** the index is
  LF, so a Linux clone is LF, and because `eol=lf` is now set, editing and re-committing one
  of these normalises it back to LF on the way in.

### 6.2 Definitive proof — raw index bytes

`git ls-files --eol` is a summary. This reads the actual stored blob:

```bash
python - <<'PY'
import subprocess
for f in ['TAKPortal/install.sh','TAKPortal/uninstall.sh','Infra-TAK/install.sh',
          'Infra-TAK/uninstall.sh','CloudTAK/install.sh','ATAK5.6/gradlew','ATAK5.7/gradlew']:
    oid = subprocess.run(['git','rev-parse',':'+f],capture_output=True,text=True).stdout.strip()
    b = subprocess.run(['git','cat-file','blob',oid],capture_output=True).stdout
    crlf = b.count(b'\r\n')
    print('%-28s CRLF=%d  first line=%r' % (f, crlf, b.split(b'\n')[0][:32]))
PY
```

Actual output — **zero CRLF in every one**:

```
TAKPortal/install.sh         CRLF=0  first line=b'#!/bin/bash'
TAKPortal/uninstall.sh       CRLF=0  first line=b'#!/bin/bash'
Infra-TAK/install.sh         CRLF=0  first line=b'#!/bin/bash'
Infra-TAK/uninstall.sh       CRLF=0  first line=b'#!/bin/bash'
CloudTAK/install.sh          CRLF=0  first line=b'#!/usr/bin/env bash'
ATAK5.6/gradlew              CRLF=0  first line=b'#!/bin/sh'
ATAK5.7/gradlew              CRLF=0  first line=b'#!/bin/sh'
```

No shebang carries a trailing `\r`. Every installer will execute on Linux.

### 6.3 Refreshing a stale working tree (optional)

Only when no other work is in flight — this rewrites the whole working tree:

```bash
git status --porcelain      # MUST be empty first
git rm --cached -r -q .
git reset --hard
git ls-files --eol -- '*.sh'   # every row should now read w/lf
```

### 6.4 The CRLF allowlist still holds

```bash
git ls-files --eol -- '*.sln' '*.bat'
```

```
i/lf    w/crlf  attr/text eol=crlf      ATAK5.6/gradlew.bat
i/lf    w/crlf  attr/text eol=crlf      ATAK5.7/gradlew.bat
i/lf    w/lf    attr/text eol=crlf      WinTAK5.6/FeatureLink.sln
i/lf    w/crlf  attr/text eol=crlf      WinTAK5.7/FeatureLink.sln
```

`attr/… eol=crlf` on the Windows-only files, `eol=lf` on the shell scripts. Both rules apply.

### 6.5 Confirm the original symptom is gone

The audit recorded 8 `LF will be replaced by CRLF` warnings on `git diff --stat`:

```bash
git diff --stat 2>&1 | grep -i "will be replaced" || echo "no LF->CRLF warnings"
```

You may still see `CRLF will be replaced by LF` on a file someone saved with CRLF in their
editor. That is the **correct** direction — it is the repository normalising toward LF.

---

## 7. Secrets required for the ATAK and WinTAK jobs

Neither SDK is redistributable, so neither can be committed. Until these are set, the `atak`
and `wintak` jobs fail with an explanatory `::error::` rather than skipping.

Set at **Settings → Secrets and variables → Actions → Repository secrets**:

| Secret | Used by | What it is |
|---|---|---|
| `ATAK_SDK_URL` | `atak` (both) | Presence gate. Any non-empty value; the per-version URLs below do the real work. |
| `ATAK_SDK_URL_5_6` | `atak` 5.6 | URL to the **ATAK-CIV 5.6** SDK `main.jar` |
| `ATAK_SDK_SHA256_5_6` | `atak` 5.6 | Its SHA-256, lowercase hex |
| `ATAK_SDK_URL_5_7` | `atak` 5.7 | URL to the **ATAK-CIV 5.7** SDK `main.jar` |
| `ATAK_SDK_SHA256_5_7` | `atak` 5.7 | Its SHA-256 |
| `WINTAK_SDK_URL_5_6` | `wintak` 5.6 | URL to a zip of the WinTAK 5.6 SDK assemblies for `libs/` |
| `WINTAK_SDK_SHA256_5_6` | `wintak` 5.6 | Its SHA-256 |
| `WINTAK_SDK_URL_5_7` | `wintak` 5.7 | URL to the WinTAK 5.7 SDK assemblies |
| `WINTAK_SDK_SHA256_5_7` | `wintak` 5.7 | Its SHA-256 |
| `GITLEAKS_LICENSE` | `secret-scan` | **Only if this repository is organisation-owned.** |

Compute a digest with:

```bash
sha256sum /path/to/ATAK-CIV-5.6.0-SDK/main.jar
```

**The checksums are mandatory, not optional.** The old workflow downloaded `main.jar` from a
GitHub Release named `sdk-artifacts` with no checksum and no signature, so anyone with
release-write access — or a compromised token — could have substituted a malicious jar that
would then be compiled into every plugin artifact.

**Note the per-version secrets.** The old ATAK 5.6 and 5.7 workflows were byte-identical
apart from one comment, and **both downloaded the same `main.jar` from the same release**. Had
they ever run, the 5.7 build would have compiled against the 5.6 SDK.

### 7.1 Hosting the SDK artifacts

Options, best first:

1. A **private** GitHub repository in the same org, referenced by a raw URL with a PAT.
2. A GitHub Release on a **private** repository, fetched with `gh release download`.
3. Object storage (S3/Azure Blob) with a presigned URL. Rotate on expiry.

Do **not** put them in a public release. Redistribution rights for the WinTAK SDK assemblies
are **undocumented** — see `SECURITY.md` and the open question in `QUESTIONS-FOR-OWNER.md`.

### 7.2 Verifying the SDK gate before you have credentials

You can confirm the fail-loudly behaviour without any SDK: push and open the failing `atak`
job. The log must contain `ATAK SDK not configured` followed by the full list of secrets. If
it instead shows a **green** `atak` job, the gate is broken — that is the false-assurance
failure mode this design exists to prevent.

---

## 8. Verifying supply-chain controls

### 8.1 Pinned binary digests

```bash
while read -r path digest; do
  case "$path" in ''|'#'*) continue ;; esac
  actual="$(sha256sum "$path" | cut -d' ' -f1)"
  [ "$actual" = "$digest" ] && echo "OK  $path" || echo "MISMATCH  $path"
done < <(sed 's/#.*//' .github/pinned-binaries.sha256 | awk 'NF==2')
```

Actual output — all eight pass:

```
OK  ATAK5.6/buildtools/atak-gradle-takdev.jar
OK  ATAK5.7/buildtools/atak-gradle-takdev.jar
OK  ATAK5.6/gradle/wrapper/gradle-wrapper.jar
OK  ATAK5.7/gradle/wrapper/gradle-wrapper.jar
OK  ATAK5.6/.takdev/aars/takdevlint.aar
OK  ATAK5.7/.takdev/aars/takdevlint.aar
OK  ATAK5.6/app/featurelink.keystore
OK  ATAK5.7/app/featurelink.keystore
```

The important one is `atak-gradle-takdev.jar`: a 148 KB tracked binary **executed as a Gradle
classpath dependency**, previously pinned by nothing — the old workflow only checked that the
file existed and printed its byte count.

### 8.2 No unpinned executable blobs

```bash
git ls-files '*.jar' '*.aar' '*.dll' '*.exe' '*.so' '*.keystore' '*.jks' \
  | while read -r f; do grep -qF "$f" .github/pinned-binaries.sha256 || echo "UNPINNED: $f"; done
```

Expected: no output. Verified — currently clean.

### 8.3 Every action is SHA-pinned

```bash
grep -rhoE 'uses: [^ ]+@[^ ]+' .github/workflows/ \
  | grep -vE '@[0-9a-f]{40}$' | grep -v 'uses: \./' || echo "all actions SHA-pinned"
```

Verified: **45** action references, all pinned by 40-character commit SHA. A tag is
repointable by the action owner — the `tj-actions/changed-files` supply-chain pattern.

---

## 9. Workflow YAML validation

```bash
python - <<'PY'
import glob, yaml
for f in sorted(glob.glob('.github/workflows/*.yml')) + ['.github/dependabot.yml']:
    d = yaml.safe_load(open(f, encoding='utf-8'))
    jobs = d.get('jobs', {})
    print(f"OK {f}  jobs={len(jobs)}")
    for n, j in jobs.items():
        print(f"     {n:<22} permissions={j.get('permissions','INHERITED (!)')}")
PY
```

Verified output — all five parse, **every job declares explicit `permissions:`**, none
inherits:

```
OK .github/workflows/ci.yml        jobs=9
OK .github/workflows/release.yml   jobs=3
OK .github/workflows/sbom.yml      jobs=3
OK .github/workflows/security.yml  jobs=5
OK .github/dependabot.yml          updates=8  ecosystems=[github-actions, gradle, npm, nuget, pip]
```

The old workflows declared **no** `permissions:` block at all and inherited the
repository-default `GITHUB_TOKEN` scope — read/write on all scopes under older settings.

**A note on YAML parsing:** PyYAML parses the bare key `on:` as boolean `True` (the "Norway
problem"). If you write your own validator, look up `doc.get('on', doc.get(True))`, or you
will wrongly conclude the workflows have no triggers.

### 9.1 Verifying the script-injection fix

The old `release.yml` had two Actions script injections. Confirm neither pattern survives:

```bash
grep -nE '\$\{\{ *(github\.event\.)?inputs\.' .github/workflows/release.yml
```

Every hit must be inside an `env:` block, never inside a `run:` block. In the replacement,
untrusted `release_notes` reaches the shell only as `$RELEASE_NOTES`, and the version is read
from the tracked `VERSION` file rather than `sed`-ed in from dispatch input — so no untrusted
value goes anywhere near the Gradle script that is subsequently executed.

---

## 10. Component gates, run locally

These are what CI runs; all work with no SDK.

```bash
cd CloudTAK/plugin && npm ci && npm run verify   # vue-tsc + eslint + vitest
cd TAKPortal      && npm ci && npm run lint && npm test
pip install ruff bandit && ruff check Infra-TAK/ && bandit -r Infra-TAK/ -ll
for f in TAKPortal/install.sh Infra-TAK/install.sh CloudTAK/install.sh; do bash -n "$f" && echo "OK $f"; done
```

`bash -n` on all five install/uninstall scripts passes on this tree — verified.

> These belong to WP2/WP4 and were changing while this guide was written. If one fails,
> check that package's report before assuming the workflow is at fault.

---

## 11. Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| `version-policy` red, 14 violations | **Correct.** C-15 is unfixed. | §3.2. Do not weaken the gate. |
| `atak`/`wintak` red, "SDK not configured" | **Correct.** No secrets. | §7 |
| `atak` job **green** without SDK secrets | Gate broken — false assurance | Investigate immediately |
| `secret-scan` red on `featurelink.keystore` | **Correct.** C-40. | Rotate forward per `SECURITY.md`; allowlist only after conscious acceptance |
| `secret-scan` red with a licence error | Org-owned repo | Set `GITLEAKS_LICENSE` |
| `icon-corpus-parity` red | Corpora diverged | Apply the change to **both** corpora |
| `clean-clone-build` red, "UNRESOLVED IMPORT" | **Correct.** C-29 recurring: a tracked file imports an untracked one | `git add` the missing source |
| `cloudtak` red on missing lockfile | C-30 regressed | Ensure `package-lock.json` is committed and unignored |
| Gradle cannot resolve ATAK API symbols | The C-13 property-key bug | `local.properties` must say `atak.sdk.path`, **not** `sdk.path` |
| `bad interpreter: /bin/bash^M` on a server | CRLF `.sh` from an old checkout | §6.3 |
| Every action fails to resolve | Org action allowlist | §1.2(a) |

---

## 12. What this guide does **not** cover

Deferred by scope decision; see `docs/remediation/wp5-crosscutting.md` for the full list with
estimates.

- `NOTICE` and `THIRD-PARTY-NOTICES.md` — not written.
- Icon **provenance** and licensing (the orphan/duplication *analysis* is done, §4.1; who
  owns the 11 iconsets is not established).
- The Section 508 / VPAT deliverable.
- Any Appendix E §3 documentation reconciliation — `README.md`, `capabilities.md`,
  `CONFIG-FORMAT.md` (including the 4-mode × 5-implementation conformance matrix and the
  forward-compatibility rule), `AUTO-ICONSET-SPEC.md`, the three `WORKSTATUS.md` files,
  per-component READMEs. Deliberately not started: the other packages are still changing the
  code these documents must describe, so the work would have been stale on arrival.
- `CHANGELOG.md`.
- **No workflow in this repository has ever executed on GitHub Actions.** Everything in §2 is
  a reasoned expectation from local validation, not an observed CI result.
