#!/usr/bin/env python3
"""
Enforce VERSIONING.md across the seven FeatureLink components.

Closes the enforcement half of C-15 (adjudicated CRITICAL).

The defect this exists to make structurally unreachable:

    ATAK5.6/app/build.gradle and ATAK5.7/app/build.gradle both declared
    PLUGIN_VERSION = "2.7.24", versionCode 5, the same namespace and the same signing
    identity -- two separately-shipped artifacts with one package identity, which mutually
    overwrite on a device. And versionCode reached only 4 across the eight releases
    2.6.16 .. 2.6.24, i.e. it was not incremented per release, so Android's sole upgrade
    discriminator was frozen and every fielded device silently failed to upgrade across
    all nine releases.

Run:
    python .github/scripts/check_versions.py                 # full check
    python .github/scripts/check_versions.py --baseline REF  # also check monotonicity vs REF

Exit codes:
    0  all MUSTs satisfied
    1  at least one MUST violated (each printed with the exact remediation)
    2  the script could not read something it needs (treated as failure, never as a pass)

This script is deliberately dependency-free (stdlib only) so it runs identically on a
GitHub runner, in a pre-commit hook, and on a developer workstation with no venv.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# ATAK target ordinal -> the last two digits of versionCode. See VERSIONING.md section 3.
ATAK_TARGET_ORDINAL = {"5.6": 6, "5.7": 7}

SEMVER_RE = re.compile(r"^(?P<major>\d+)\.(?P<minor>\d+)\.(?P<patch>\d+)$")

failures: list[str] = []
notes: list[str] = []


def fail(item: str, detail: str, remedy: str) -> None:
    failures.append(f"  [{item}] {detail}\n        FIX: {remedy}")


def note(msg: str) -> None:
    notes.append(f"  - {msg}")


def read(path: str) -> str | None:
    full = os.path.join(ROOT, path)
    if not os.path.isfile(full):
        return None
    with open(full, encoding="utf-8", errors="replace") as fh:
        return fh.read()


def version_code(suite: str, atak_target: str) -> int:
    """versionCode = MAJOR*1_000_000 + MINOR*10_000 + PATCH*100 + ATAK_TARGET_ORDINAL"""
    m = SEMVER_RE.match(suite)
    if not m:
        raise ValueError(f"not SemVer: {suite!r}")
    return (
        int(m["major"]) * 1_000_000
        + int(m["minor"]) * 10_000
        + int(m["patch"]) * 100
        + ATAK_TARGET_ORDINAL[atak_target]
    )


def first_group(pattern: str, text: str | None, flags: int = 0) -> str | None:
    if text is None:
        return None
    m = re.search(pattern, text, flags)
    return m.group(1) if m else None


# ---------------------------------------------------------------------------
# 1. The canonical suite version
# ---------------------------------------------------------------------------

def load_suite_version() -> str:
    raw = read("VERSION")
    if raw is None:
        print("FATAL: VERSION is missing at the repository root. It is the single source of "
              "truth for the suite version (VERSIONING.md section 1).", file=sys.stderr)
        sys.exit(2)
    suite = raw.strip()
    if not SEMVER_RE.match(suite):
        print(f"FATAL: VERSION contains {suite!r}, which is not SemVer MAJOR.MINOR.PATCH.",
              file=sys.stderr)
        sys.exit(2)
    return suite


# ---------------------------------------------------------------------------
# 2. Per-component declared versions
# ---------------------------------------------------------------------------

def check_declarations(suite: str) -> None:
    """VERSIONING.md section 1: all seven declarations equal VERSION."""

    for tree in ("ATAK5.6", "ATAK5.7"):
        path = f"{tree}/app/build.gradle"
        text = read(path)
        if text is None:
            fail(tree, f"{path} not found", "restore the file")
            continue
        declared = first_group(r"""ext\.PLUGIN_VERSION\s*=\s*["']([^"']+)["']""", text)
        if declared is None:
            fail(tree, f"{path}: no ext.PLUGIN_VERSION declaration",
                 'add: ext.PLUGIN_VERSION = "<suite version>"')
        elif declared != suite:
            fail(tree, f"{path}: PLUGIN_VERSION is {declared!r}, VERSION says {suite!r}",
                 f'set ext.PLUGIN_VERSION = "{suite}"')

    for tree in ("WinTAK5.6", "WinTAK5.7"):
        man = read(f"{tree}/MANIFEST.xml")
        declared = first_group(r"<version>\s*([^<\s]+)\s*</version>", man)
        if declared is None:
            fail(tree, f"{tree}/MANIFEST.xml: no <version> element", "add one")
        elif declared != suite:
            fail(tree, f"{tree}/MANIFEST.xml: <version> is {declared!r}, VERSION says {suite!r}",
                 f"set <version>{suite}</version>")

        asm = read(f"{tree}/Properties/AssemblyInfo.cs")
        for attr in ("AssemblyVersion", "AssemblyFileVersion"):
            got = first_group(rf'{attr}\("([^"]+)"\)', asm)
            if got is None:
                fail(tree, f"{tree}/Properties/AssemblyInfo.cs: no {attr}", f"add [assembly: {attr}(...)]")
                continue
            # .NET assembly versions are 4-part; compare the first three components.
            if ".".join(got.split(".")[:3]) != suite:
                fail(tree,
                     f"{tree}/Properties/AssemblyInfo.cs: {attr} is {got!r}, VERSION says {suite!r}",
                     f'set [assembly: {attr}("{suite}.0")] -- the package manifest and the '
                     f"compiled assembly disagreeing by a whole major line means Windows file "
                     f"properties, crash telemetry and support triage all report the wrong build")

    for path, key in (("CloudTAK/plugin/package.json", "CloudTAK"),
                      ("TAKPortal/package.json", "TAK Portal")):
        raw = read(path)
        if raw is None:
            fail(key, f"{path} not found", "restore the file")
            continue
        try:
            declared = json.loads(raw).get("version")
        except json.JSONDecodeError as exc:
            fail(key, f"{path} is not valid JSON: {exc}", "fix the JSON")
            continue
        if declared != suite:
            fail(key, f"{path}: version is {declared!r}, VERSION says {suite!r}",
                 f'set "version": "{suite}" -- an independent version line means nothing maps '
                 f"this build to the ATAK/WinTAK release it is interop-compatible with, "
                 f"despite all three sharing the CONFIG-FORMAT.md contract")

    infra = read("Infra-TAK/featurelink_displayconfig.py")
    declared = first_group(r"""^__version__\s*=\s*["']([^"']+)["']""", infra, re.M)
    if declared is None:
        fail("Infra-TAK", "Infra-TAK/featurelink_displayconfig.py: no __version__",
             f'add: __version__ = "{suite}" -- an operator cannot otherwise state which '
             f"configurator build generated a config")
    elif declared != suite:
        fail("Infra-TAK", f"__version__ is {declared!r}, VERSION says {suite!r}",
             f'set __version__ = "{suite}"')


# ---------------------------------------------------------------------------
# 3. versionCode derivation, and distinct package identity per target
# ---------------------------------------------------------------------------

def check_atak_identity(suite: str) -> None:
    namespaces: dict[str, str] = {}

    for tree, target in (("ATAK5.6", "5.6"), ("ATAK5.7", "5.7")):
        path = f"{tree}/app/build.gradle"
        text = read(path)
        if text is None:
            continue

        atak_version = first_group(r"""ext\.ATAK_VERSION\s*=\s*["']([^"']+)["']""", text) or ""
        if not atak_version.startswith(target):
            fail(tree, f"{path}: ATAK_VERSION is {atak_version!r}, expected the {target}.x line",
                 f"set ext.ATAK_VERSION = '{target}.0'")

        expected = version_code(suite, target)
        literal = first_group(r"^\s*versionCode\s+(\d+)\s*$", text, re.M)
        if literal is not None:
            fail(tree,
                 f"{path}: versionCode is the hand-typed literal {literal}. "
                 f"VERSIONING.md section 3 forbids a literal -- a hand-edited upgrade "
                 f"discriminator is exactly how it stayed frozen at 4 across nine releases.",
                 f"compute it in the Gradle script:\n"
                 f"             def (maj, min, pat) = PLUGIN_VERSION.tokenize('.')*.toInteger()\n"
                 f"             versionCode maj*1000000 + min*10000 + pat*100 + "
                 f"{ATAK_TARGET_ORDINAL[target]}\n"
                 f"             // -> {expected} for suite {suite}, ATAK {target}")
        else:
            derived = first_group(r"^\s*versionCode\s+(.+)$", text, re.M)
            if derived is None:
                fail(tree, f"{path}: no versionCode at all", f"add the derived expression (-> {expected})")
            elif str(ATAK_TARGET_ORDINAL[target]) not in derived:
                fail(tree,
                     f"{path}: versionCode expression {derived.strip()!r} does not include the "
                     f"ATAK target ordinal {ATAK_TARGET_ORDINAL[target]}",
                     "the two ATAK targets must produce different versionCodes")
            else:
                note(f"{tree}: versionCode is derived; expected value for {suite} is {expected}")

        # Accepts both Gradle spellings: `namespace 'x'` and `namespace = 'x'`.
        ns = first_group(r"""^\s*namespace\s*=?\s*["']([^"']+)["']""", text, re.M) or \
             first_group(r"""^\s*applicationId\s*=?\s*["']([^"']+)["']""", text, re.M)
        if ns:
            namespaces[tree] = ns

    if len(namespaces) == 2 and len(set(namespaces.values())) == 1:
        shared = next(iter(namespaces.values()))
        fail("ATAK5.6/ATAK5.7",
             f"both trees declare the SAME package identity {shared!r}. Two separately-built, "
             f"separately-shipped artifacts with one applicationId mutually overwrite on a "
             f"device -- an operator cannot hold both, and cannot tell which one is installed.",
             "give each target a distinct applicationId per VERSIONING.md section 2:\n"
             "             ATAK 5.6 -> com.atakmap.android.featurelink.atak56.plugin\n"
             "             ATAK 5.7 -> com.atakmap.android.featurelink.atak57.plugin")

    for tree, target in (("ATAK5.6", "5.6"), ("ATAK5.7", "5.7")):
        text = read(f"{tree}/app/build.gradle")
        vn = first_group(r"^\s*versionName\s+(.+)$", text, re.M) if text else None
        if vn is not None and f"atak{target}" not in vn:
            fail(tree,
                 f"versionName {vn.strip()!r} does not embed the ATAK target. Inside the APK, "
                 f"versionName/packageName/plugin-api were identical between the two builds; "
                 f"disambiguation relied entirely on the archivesBaseName FILENAME, which is "
                 f"not carried into the installed package.",
                 f'set versionName "${{PLUGIN_VERSION}}+atak{target}"')


def check_wintak_identity() -> None:
    ids: dict[str, str] = {}
    for tree in ("WinTAK5.6", "WinTAK5.7"):
        pid = first_group(r"<id>\s*([^<\s]+)\s*</id>", read(f"{tree}/MANIFEST.xml"))
        if pid:
            ids[tree] = pid
    if len(ids) == 2 and len(set(ids.values())) == 1:
        fail("WinTAK5.6/WinTAK5.7",
             f"both MANIFEST.xml files declare the SAME <id> {next(iter(ids.values()))!r}. "
             f"WinTAK's plugin loader identifies packages by id+version; with the same id and "
             f"the same version, installing one over the other is undefined behaviour -- and "
             f"the two trees ship materially different codebases (C-17).",
             "give 5.7 a distinct id, e.g. <id>FeatureLink-WinTAK57</id>")


# ---------------------------------------------------------------------------
# 4. The monotonicity gate -- the assertion whose absence froze versionCode at 4
# ---------------------------------------------------------------------------

def git_show(ref: str, path: str) -> str | None:
    try:
        out = subprocess.run(["git", "show", f"{ref}:{path}"], cwd=ROOT,
                             capture_output=True, check=False)
        return out.stdout.decode("utf-8", "replace") if out.returncode == 0 else None
    except OSError:
        return None


def check_monotonic(suite: str, baseline: str) -> None:
    base_ver_raw = git_show(baseline, "VERSION")
    base_suite = base_ver_raw.strip() if base_ver_raw else None

    if base_suite is None:
        note(f"monotonicity: {baseline} predates the VERSION file, so there is no baseline "
             f"versionCode to compare against. This is expected for the first release after "
             f"the C-15 remediation and is NOT a pass for subsequent releases.")
        return

    if not SEMVER_RE.match(base_suite):
        note(f"monotonicity: baseline VERSION {base_suite!r} is not SemVer; skipping.")
        return

    for tree, target in (("ATAK5.6", "5.6"), ("ATAK5.7", "5.7")):
        try:
            new_code = version_code(suite, target)
            old_code = version_code(base_suite, target)
        except ValueError:
            continue
        if suite != base_suite and new_code <= old_code:
            fail(tree,
                 f"versionCode would go BACKWARDS or stay equal: {old_code} at {baseline} "
                 f"(suite {base_suite}) -> {new_code} now (suite {suite}). Android uses "
                 f"versionCode as the SOLE upgrade discriminator; a non-increasing code means "
                 f"side-loaded upgrades over an existing install are rejected or silently "
                 f"skipped, so field devices cannot upgrade.",
                 "increment VERSION")
        elif suite != base_suite:
            note(f"{tree}: versionCode {old_code} -> {new_code} (strictly increasing) OK")

    # SemVer discipline: a MINOR bump resets PATCH.
    new = SEMVER_RE.match(suite)
    old = SEMVER_RE.match(base_suite)
    if new and old and int(new["minor"]) > int(old["minor"]) and int(new["patch"]) != 0:
        fail("VERSION",
             f"{base_suite} -> {suite} increments MINOR while preserving PATCH "
             f"({new['patch']}). That is the exact pattern the audit flagged (2.6.24 -> "
             f"2.7.24): semantically incoherent, and it makes the two lines "
             f"indistinguishable in a sorted release list.",
             f"use {new['major']}.{new['minor']}.0")


# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--baseline", default=None,
                    help="git ref to compare versionCode against (e.g. the PR merge base)")
    args = ap.parse_args()

    suite = load_suite_version()
    print(f"Suite version (VERSION): {suite}")
    print(f"Derived versionCode    : ATAK 5.6 -> {version_code(suite, '5.6')}, "
          f"ATAK 5.7 -> {version_code(suite, '5.7')}")
    print()

    check_declarations(suite)
    check_atak_identity(suite)
    check_wintak_identity()
    if args.baseline:
        check_monotonic(suite, args.baseline)

    if notes:
        print("Notes:")
        print("\n".join(notes))
        print()

    if failures:
        print(f"VERSION POLICY: {len(failures)} violation(s) of VERSIONING.md\n")
        print("\n\n".join(failures))
        print("\nThis gate fails the build. It does not warn. See VERSIONING.md.")
        return 1

    print("VERSION POLICY: OK -- all MUSTs in VERSIONING.md are satisfied.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
