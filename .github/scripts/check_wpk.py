#!/usr/bin/env python3
"""
Assert that a built WinTAK .wpk package contains every assembly it needs to load.

Closes the CI half of C-18 (adjudicated CRITICAL).

THE DEFECT
----------
`WinTAK5.6/build-wpk.py` packaged only FeatureLink.dll, the icon and MANIFEST.xml, while
`WinTAK5.6/packages.config` pins `Newtonsoft.Json 13.0.3` and the plugin calls into it. The
assembly actually loaded at runtime was therefore whatever the host WinTAK happened to
supply -- or nothing, in which case the plugin does not load at all.

This also re-opens C-19: Newtonsoft >= 13.0.1 defaults JsonReader.MaxDepth to 64, which is
what downgrades the peer-JSON-parsing DoS from CRITICAL to HIGH. If the host supplies an
older Newtonsoft, that default is absent and the original claim re-applies. So packaging the
pinned assembly is load-bearing for two findings, not one.

A build that produces a .wpk missing a required assembly is indistinguishable, from the
build log, from a good one. That is precisely why this must be a gate rather than a review
item.

Run:
    python .github/scripts/check_wpk.py path/to/FeatureLink.wpk
"""

from __future__ import annotations

import os
import sys
import zipfile

# Assemblies that MUST be inside the package. FeatureLink.dll is the plugin itself;
# Newtonsoft.Json.dll is the pinned dependency from packages.config.
REQUIRED = ("FeatureLink.dll", "Newtonsoft.Json.dll")

# Files WinTAK's loader needs in order to identify the package at all.
REQUIRED_METADATA = ("MANIFEST.xml",)


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    path = sys.argv[1]

    if not os.path.isfile(path):
        print(f"FATAL: no .wpk at {path}. The WinTAK build did not produce a package -- treat "
              f"this as a build failure, not as 'nothing to check'.", file=sys.stderr)
        return 2

    with zipfile.ZipFile(path) as zf:
        names = zf.namelist()
        sizes = {os.path.basename(n).lower(): zf.getinfo(n).file_size for n in names}

    print(f"Package : {path} ({os.path.getsize(path)} bytes)")
    print(f"Entries : {len(names)}")
    for n in sorted(names):
        print(f"    {n}")
    print()

    failures: list[str] = []

    for required in REQUIRED:
        if required.lower() not in sizes:
            failures.append(
                f"MISSING ASSEMBLY: {required} is not in the package.\n"
                f"    A .wpk without it either fails to load or silently binds to whatever\n"
                f"    version the host WinTAK supplies. Fix build-wpk.py to copy every\n"
                f"    assembly resolved by packages.config into the package root."
            )
        elif sizes[required.lower()] == 0:
            failures.append(f"ZERO-LENGTH ASSEMBLY: {required} is present but empty.")
        else:
            print(f"  OK  {required} ({sizes[required.lower()]} bytes)")

    for required in REQUIRED_METADATA:
        if required.lower() not in sizes:
            failures.append(f"MISSING METADATA: {required} is not in the package.")
        else:
            print(f"  OK  {required} ({sizes[required.lower()]} bytes)")

    if failures:
        print(f"\nWPK PACKAGING: FAILED ({len(failures)} problem(s))\n", file=sys.stderr)
        for f in failures:
            print(f"  {f}\n", file=sys.stderr)
        return 1

    print("\nWPK PACKAGING: OK -- every required assembly is present.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
