<!--
  FeatureLink Suite pull-request template.

  The checklists below are not ceremony. Each one exists because its absence produced a
  real, recorded defect in this repository:

    * "Platforms updated" exists because an auto-symbology change landed in 3 of 7
      components. The four untouched components render the same layer differently, silently.
    * "Fork parity" exists because the same in-flight change landed as +432 lines in ATAK5.6
      and +436 in ATAK5.7, and because WinTAK5.7 was forked before a feature commit and
      never caught up — shipping under the same plugin id and version as WinTAK5.6.
    * "New files tracked" exists because three load-bearing sources were untracked while
      tracked files imported them.
    * "Version policy" exists because versionCode was frozen at 4 across nine releases.
-->

## What this changes

<!-- One paragraph. What behaviour differs after this merges, from an operator's point of view? -->

## Why

<!-- Link the issue, or the audit C-ID (e.g. C-06, C-22). If this closes an audit item, say which. -->

Closes:

---

## Platforms updated

Tick every component this change reaches. If a box is unticked, state below why that
component legitimately does not need it — "not applicable" is an acceptable answer, but it
must be written down, because a silent partial rollout is the failure mode this catches.

- [ ] ATAK 5.6
- [ ] ATAK 5.7
- [ ] WinTAK 5.6
- [ ] WinTAK 5.7
- [ ] CloudTAK
- [ ] TAK Portal
- [ ] Infra-TAK
- [ ] Not applicable to any component (docs / CI / tooling only)

**Components deliberately not updated, and why:**

<!-- e.g. "TAK Portal and Infra-TAK author configs, they do not render markers." -->

## Fork parity

ATAK 5.6/5.7 and WinTAK 5.6/5.7 are forks of one another. WinTAK 5.7 is a *regressed* fork —
for that pair, 5.6 is the reference.

- [ ] Change applied identically to both trees of any fork pair it touches, **or** the
      divergence is intentional and explained below
- [ ] I diffed the two trees after the change and the only differences are the
      known-legitimate ones (SDK version strings, plugin id)

**Intentional divergence:**

## Interop contract

- [ ] This change does **not** alter the `CONFIG-FORMAT.md` wire format
- [ ] It does — and `CONFIG-FORMAT.md` is updated in this PR, the schema `_version` is
      bumped, and the forward-compatibility behaviour for older readers is specified
- [ ] It changes the auto-iconset UID formula — `AUTO-ICONSET-SPEC.md` updated and the
      cross-language golden vectors still match byte-for-byte in all four languages

## Version policy

- [ ] No version change needed
- [ ] `VERSION` bumped, and the increment matches the wire-format impact per `VERSIONING.md`
      (MINOR resets PATCH to 0)
- [ ] `python .github/scripts/check_versions.py` passes locally

## Tests

- [ ] A regression test exists that **fails before this change and passes after it**
- [ ] No test is possible, and the reason is stated below

**If untested, why:**

## Security and privacy

- [ ] No new network destination, no new persisted field, no new permission
- [ ] This changes what data leaves the device, or where it goes — `PRIVACY.md` is updated
      in this PR
- [ ] No secret, token, keystore, password or production hostname is added to the tree
- [ ] Any new untrusted input is validated before use, and never interpolated into a shell
      command, a `sed` expression, or a `run:` block

## Repository hygiene

- [ ] Every new source file is `git add`ed — a tracked file importing an untracked one
      builds locally and breaks the branch for everyone else
- [ ] No build output, `node_modules`, `bin/`, `obj/`, `.gradle/` or `local.properties`
      is staged
- [ ] Shell scripts are LF-only (`git ls-files --eol -- '*.sh'` shows `w/lf`) — a CRLF
      `install.sh` fails on Linux with `bad interpreter: /bin/bash^M`
- [ ] Icon corpora, if touched, are updated in **both** components identically

## Reviewer notes

<!-- Anything the reviewer should look at first, or anything you are unsure about. -->
