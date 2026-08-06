# Contributing to FeatureLink

FeatureLink is public-safety software. Please read `PRIVACY.md` and `SECURITY.md` before
your first change.

This document closes the "no `CONTRIBUTING.md`" gap in Appendix E §7: there was no single
place recording build prerequisites, branch conventions or review requirements.

---

## 1. The shape of the repository

Seven components, four languages, two fork pairs:

| Directory | Component | Toolchain |
|---|---|---|
| `ATAK5.6/`, `ATAK5.7/` | ATAK-CIV plugin | Java 17 / Gradle / Android |
| `WinTAK5.6/`, `WinTAK5.7/` | WinTAK plugin | C# / .NET Framework 4.8 / WPF |
| `CloudTAK/` | CloudTAK plugin | TypeScript / Vue 3 / Vite |
| `TAKPortal/` | TAK Portal module | Node / Express / EJS |
| `Infra-TAK/` | Display Configurator | Python / Flask |

**`ATAK5.7/` is a fork of `ATAK5.6/`, and `WinTAK5.7/` is a fork of `WinTAK5.6/`.** Any fix
to one MUST be applied to the other unless the divergence is deliberate and documented.
`WinTAK5.7/` is a *regressed* fork — for that pair, `WinTAK5.6/` is the reference.

This is the single most common way a change goes wrong here. The pull-request template
carries a fork-parity checklist for exactly this reason.

---

## 2. Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| Git | 2.40+ | everything |
| Node | 20.11+ (22 recommended) | CloudTAK, TAK Portal |
| npm | 10+ | CloudTAK, TAK Portal |
| Python | 3.12+ | Infra-TAK, the `.github/scripts/` gates, `build-wpk.py` |
| JDK | 17 (Temurin) | ATAK |
| Android SDK | platform 36, build-tools 34 | ATAK |
| ATAK-CIV SDK | 5.6 / 5.7 `main.jar` | ATAK — **licensed, not in this repo** |
| Visual Studio / MSBuild | 2022, .NET Framework 4.8 targeting pack | WinTAK |
| WinTAK SDK | assemblies for `WinTAK5.x/libs/` | WinTAK — **licensed, not in this repo** |

Neither TAK SDK may be committed. Both are fetched in CI from repository secrets, verified
by SHA-256 before use, and the corresponding jobs **fail loudly** when those secrets are
absent rather than skipping. A build gate that silently no-ops is worse than none, because
it produces a green check that means nothing.

### Local setup

```bash
git clone <repo> && cd TAK-PluginSuite-FeatureLink

# CloudTAK
cd CloudTAK/plugin && npm ci && npm run verify && cd ../..

# TAK Portal
cd TAKPortal && npm ci && npm run lint && npm test && cd ..

# Infra-TAK
pip install ruff pytest && ruff check Infra-TAK/

# ATAK (needs the SDK)
cd ATAK5.6
cat > local.properties <<'EOF'
sdk.dir=/path/to/Android/Sdk
takdev.plugin=<repo>/ATAK5.6/buildtools/atak-gradle-takdev.jar
atak.sdk.path=/path/to/ATAK-CIV-5.6.0-SDK
EOF
./gradlew assembleCivDebug
```

Note the property name: **`atak.sdk.path`**, which is what `app/build.gradle` reads. The
old CI wrote `sdk.path`, so `main.jar` never reached the compile classpath. If your ATAK
build cannot resolve ATAK API symbols, check this line first.

`local.properties` is gitignored and must stay that way — it carries absolute developer
paths and the keystore password.

---

## 3. Before you open a pull request

Run the same gates CI runs. All four work with no SDK and no network:

```bash
python .github/scripts/check_versions.py        # VERSIONING.md conformance
python .github/scripts/check_icon_corpora.py    # the two icon corpora must stay identical
cd CloudTAK/plugin && npm run verify            # vue-tsc + eslint + vitest
cd TAKPortal && npm run lint && npm test
ruff check Infra-TAK/
```

See `docs/testing/ci-and-repo.md` for expected output on the current tree, including which
gates are **expected to fail today** and why that is correct.

---

## 4. Branches, commits and review

- Branch from `dev`. Name branches `fix/<c-id>-short-description` or
  `feat/<component>-short-description`.
- **Never commit directly to `main` or `dev`.** All 33 commits in this repository's history
  are linear, direct-to-branch, with no merge commits and no pull requests — there is no
  evidence of code review on any change ever made here. That stops now.
- One logical change per commit. A 1,808-line commit mixing a cross-platform feature, a
  version bump and a marketing section of the README is unreviewable, and that has happened.
- Commit messages: imperative subject under 72 characters, then a body explaining *why*.
  Name the audit C-ID if the commit closes one.
- Every PR needs at least one approving review from a code owner (`.github/CODEOWNERS`).

### Branch protection — recommended settings

`origin/main` currently sits at the initial commit while `dev` is 33 commits ahead, so
anyone cloning the default branch gets an essentially empty project. Fix that first
(merge `dev` into `main`, or repoint the default branch), then protect both:

**On `main` and `dev`:**

- [x] Require a pull request before merging
  - [x] Require at least 1 approval
  - [x] Dismiss stale approvals when new commits are pushed
  - [x] **Require review from Code Owners**
- [x] Require status checks to pass before merging
  - [x] Require branches to be up to date before merging
  - Required checks: **`CI required checks`** (the aggregate job in `ci.yml` — requiring
    this one rather than each individual job means a newly-added component job becomes
    required automatically, with no branch-settings edit), plus `Secret scan (gitleaks)`
- [x] Require conversation resolution before merging
- [x] Require signed commits
- [x] Do not allow bypassing the above settings (including for administrators)
- [x] Restrict force pushes
- [x] Restrict deletions

**Repository-level:**

- [x] Enable secret scanning **and push protection** — push protection is what would have
      stopped the keystore reaching the repository in the first place
- [x] Enable Dependabot alerts and security updates (`.github/dependabot.yml` is present)
- [x] Enable private vulnerability reporting (`SECURITY.md` points contributors at it)

### Housekeeping

Two branches need a decision: `jpat-laptop` is a machine-named personal workspace published
to the shared remote (a note to delete it was written and never acted on), and `atak5.6/ui`
sits at a commit that is not an ancestor of `dev`, carrying unmerged work of unknown status.
Delete the first; merge or delete the second.

There is also a stash — `stash@{0}`, *"wip: featurelink-configurator index.html"*. **Do not
apply it blindly.** It predates the remediation work and will conflict. Inspect it with
`git stash show -p stash@{0}`, salvage anything still wanted as a fresh commit, then drop it.

---

## 5. Line endings

`.gitattributes` pins the whole repository to **LF in the index and LF on checkout**, with a
short CRLF allowlist for `*.bat`, `*.cmd`, `*.ps1` and `*.sln`.

`*.sh` and `gradlew` are pinned to LF as a **correctness requirement, not a preference**: a
shell script checked out with CRLF fails on Linux with `bad interpreter: /bin/bash^M`, and
both TAK Portal and Infra-TAK are installed by shell script. Verify with:

```bash
git ls-files --eol -- '*.sh' 'ATAK5.6/gradlew' 'ATAK5.7/gradlew'
```

Every row must read `w/lf`. If yours says `w/crlf`, your checkout predates the fix — run
`git rm --cached -r . && git reset --hard` on a clean tree to re-checkout.

---

## 6. Adding a dependency

1. Add it to the component's manifest (`package.json`, `packages.config`, `build.gradle`,
   `requirements.txt`) — never vendor a binary into the tree.
2. Confirm the licence is compatible with Apache-2.0. `dependency-review` blocks
   GPL/AGPL/LGPL-3.0/SSPL at the PR.
3. Record it in `THIRD-PARTY-NOTICES.md`.
4. Commit the updated lockfile. Lockfiles are **not** gitignored; discarding one means no
   reproducible resolution and no dependency-integrity guarantee.

If you must add a tracked binary blob that the build *executes*, record its SHA-256 in
`.github/pinned-binaries.sha256` along with its provenance. The `pinned-binaries` CI job
fails on any tracked `.jar`/`.aar`/`.dll`/`.exe`/`.so` that is not pinned.

---

## 7. Reporting a vulnerability

Do **not** open a public issue. See `SECURITY.md`.
