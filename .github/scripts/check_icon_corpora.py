#!/usr/bin/env python3
"""
Assert that the two duplicated icon corpora remain byte-for-byte identical, and report the
corpus's internal duplication and orphan statistics.

WHY THIS EXISTS
---------------
`TAKPortal/assets/featurelink-configurator/icons/` and
`Infra-TAK/featurelink_displayconfig_assets/icons/` are two complete copies of the same
7,461-file icon corpus (7,450 PNGs + manifest.json + 10 iconset.xml).

Two auditors asserted the copies had "silently drifted". **That assertion is FALSE and was
struck** (checklist Appendix F section 2.4): `diff -r` returns 0 differences and both
manifest.json files share md5 f22c42c8955245ee3bbe44fc58bde1b4. This script does NOT re-file
that finding.

What IS true is the forward-looking half: nothing prevents future divergence. An icon added
to the TAK Portal corpus and not to the Infra-TAK one would break the cross-platform iconset
contract that AUTO-ICONSET-SPEC.md declares frozen, and nothing anywhere would notice. This
script is that missing mechanism. It runs in CI on every push.

Run:
    python .github/scripts/check_icon_corpora.py            # parity gate (CI)
    python .github/scripts/check_icon_corpora.py --report   # + duplication/orphan analysis
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

CORPORA = {
    "TAKPortal": "TAKPortal/assets/featurelink-configurator/icons",
    "Infra-TAK": "Infra-TAK/featurelink_displayconfig_assets/icons",
}


def walk(base: str) -> dict[str, str]:
    """relative path -> sha256"""
    out: dict[str, str] = {}
    for root, _dirs, files in os.walk(base):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, base).replace(os.sep, "/")
            with open(full, "rb") as fh:
                out[rel] = hashlib.sha256(fh.read()).hexdigest()
    return out


def parity() -> int:
    trees: dict[str, dict[str, str]] = {}
    for label, rel in CORPORA.items():
        base = os.path.join(ROOT, rel)
        if not os.path.isdir(base):
            print(f"FATAL: corpus missing: {rel}", file=sys.stderr)
            return 2
        trees[label] = walk(base)

    (a_label, a), (b_label, b) = trees.items()
    print(f"{a_label}: {len(a)} files")
    print(f"{b_label}: {len(b)} files")

    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    differing = sorted(p for p in set(a) & set(b) if a[p] != b[p])

    if not (only_a or only_b or differing):
        print("\nICON CORPUS PARITY: OK -- the two corpora are byte-for-byte identical.")
        return 0

    print(f"\nICON CORPUS PARITY: FAILED -- the corpora have diverged.\n")
    if only_a:
        print(f"  Present only in {a_label} ({len(only_a)}):")
        for p in only_a[:25]:
            print(f"    + {p}")
        if len(only_a) > 25:
            print(f"    ... and {len(only_a) - 25} more")
    if only_b:
        print(f"  Present only in {b_label} ({len(only_b)}):")
        for p in only_b[:25]:
            print(f"    + {p}")
        if len(only_b) > 25:
            print(f"    ... and {len(only_b) - 25} more")
    if differing:
        print(f"  Same path, different content ({len(differing)}):")
        for p in differing[:25]:
            print(f"    ~ {p}")
        if len(differing) > 25:
            print(f"    ... and {len(differing) - 25} more")

    print(
        "\n  AUTO-ICONSET-SPEC.md declares the iconset corpus a FROZEN cross-platform\n"
        "  contract, and the UID formula's entire value proposition is that the same layer\n"
        "  yields a byte-identical UID on every platform. A corpus that differs between the\n"
        "  two components that ship it breaks that guarantee silently -- the icon simply\n"
        "  fails to resolve on one platform.\n"
        "\n  FIX: apply the change to BOTH corpora in the same commit, or land the shared-corpus\n"
        "  deduplication described in docs/remediation/wp5-crosscutting.md so this class of\n"
        "  divergence becomes impossible rather than merely detected."
    )
    return 1


def report() -> None:
    base = os.path.join(ROOT, CORPORA["TAKPortal"])
    manifest_path = os.path.join(base, "manifest.json")
    with open(manifest_path, encoding="utf-8") as fh:
        manifest = json.load(fh)

    referenced: set[str] = set()
    group_expected: set[str] = set()
    for entry in manifest["iconsets"]:
        name = entry["name"]
        groups = entry.get("groups") or {}
        default_group = entry.get("defaultGroup")
        for icon in entry.get("icons", []):
            referenced.add(f"{name}/{icon}")
            group = groups.get(icon, default_group)
            if group:
                group_expected.add(f"{name}/{group}/{icon}")

    on_disk: set[str] = set()
    non_png: list[str] = []
    sizes: dict[str, int] = {}
    digests: dict[str, str] = {}
    for root, _dirs, files in os.walk(base):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, base).replace(os.sep, "/")
            if name.lower().endswith(".png"):
                on_disk.add(rel)
                data = open(full, "rb").read()
                sizes[rel] = len(data)
                digests[rel] = hashlib.sha256(data).hexdigest()
            else:
                non_png.append(rel)

    orphans = on_disk - referenced
    missing = referenced - on_disk
    unexplained = orphans - group_expected

    unique = {}
    for rel, dig in digests.items():
        unique.setdefault(dig, sizes[rel])

    print("=" * 78)
    print("ICON CORPUS ANALYSIS  (per corpus; the corpus is duplicated across 2 components)")
    print("=" * 78)
    print(f"iconsets declared in manifest.json : {len(manifest['iconsets'])}")
    print(f"non-PNG files                      : {len(non_png)}  {sorted(non_png)}")
    print(f"PNG files on disk                  : {len(on_disk)}")
    print(f"PNG paths referenced by manifest   : {len(referenced)}")
    print(f"referenced but ABSENT (broken refs): {len(missing)}")
    print(f"on disk but UNREFERENCED           : {len(orphans)}")
    print(f"  ...of which are the <set>/<group>/<file> second copy of a referenced icon:"
          f" {len(orphans & group_expected)}")
    print(f"  ...genuinely unexplained         : {len(unexplained)}")
    for p in sorted(unexplained):
        print(f"      ? {p}")
    print()
    print(f"unique image contents              : {len(unique)}")
    print(f"intra-corpus duplicate PNG files   : {len(on_disk) - len(unique)}")
    print(f"bytes, all files, one corpus       : {sum(sizes.values()) / 1048576:.2f} MB")
    print(f"bytes, unique images only          : {sum(unique.values()) / 1048576:.2f} MB")
    print(f"bytes, both corpora as committed   : {sum(sizes.values()) * 2 / 1048576:.2f} MB")
    print()

    by_set = collections.Counter(p.split("/")[0] for p in orphans)
    print("unreferenced PNGs by iconset:")
    for k, v in sorted(by_set.items(), key=lambda kv: -kv[1]):
        print(f"  {k:<32} {v}")
    print()

    # The genuinely dangerous case: a name that resolves to DIFFERENT bytes depending on
    # whether the consumer uses the flat path or the grouped path.
    conflicts = []
    for grouped in sorted(orphans & group_expected):
        parts = grouped.split("/")
        flat = f"{parts[0]}/{parts[-1]}"
        if flat in digests and digests[flat] != digests[grouped]:
            conflicts.append((grouped, sizes[grouped], flat, sizes[flat]))
    print(f"NAME COLLISIONS -- same icon name, different bytes by lookup path: {len(conflicts)}")
    for grouped, gsize, flat, fsize in conflicts:
        print(f"  {grouped} ({gsize} b)  !=  {flat} ({fsize} b)")
    if conflicts:
        print("  These are latent interop bugs: two consumers resolving the same manifest entry\n"
              "  by different path conventions render DIFFERENT images for the same icon.")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--report", action="store_true",
                    help="also print the duplication / orphan / collision analysis")
    args = ap.parse_args()
    rc = parity()
    if args.report:
        print()
        report()
    return rc


if __name__ == "__main__":
    sys.exit(main())
