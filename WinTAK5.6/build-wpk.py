"""
Packages the Release build into a WinTAK plugin (.wpk) file
compatible with WinTAK's Plugin Manager import.

Structure mirrors TAK Product Center plugins:
  manifest.xml                     - wintakPluginPackage metadata
  icon.png                         - plugin icon (optional, uses Assets/Large.png)
  plugin/x64/FeatureLink/
      FeatureLink.dll               - the compiled plugin
      FeatureLink.dll.config        - binding redirects, when MSBuild generated one
      Newtonsoft.Json.dll           - and every other non-SDK managed dependency

Usage: python build-wpk.py <version> <sdk_version> <dll_path> <assets_dir> <out_path>
                           [--manifest <MANIFEST.xml>]
Called automatically by the PackageWpk MSBuild target.

C-18 (audit): this script previously packaged ONLY FeatureLink.dll, the icon and an
inline copy of the manifest. `packages.config` pins Newtonsoft.Json 13.0.3 and the
plugin uses JObject on every code path, so the shipped .wpk threw
FileNotFoundException on a clean WinTAK that does not happen to expose a compatible
Newtonsoft.Json in its probing path -- i.e. the plugin could not load at all. Every
non-SDK managed dependency found next to the built DLL is now packaged, the set of
required dependencies is asserted before the archive is written, and the manifest is
templated from the checked-in MANIFEST.xml rather than duplicated inline (the two had
already begun to diverge).
"""
import os
import sys
import zipfile

# Assemblies that MUST be inside the .wpk for the plugin to load. Keep in sync with
# packages.config; the build fails loudly rather than shipping an unloadable package.
REQUIRED_DEPENDENCIES = ("Newtonsoft.Json.dll",)

# Assemblies that are provided by the WinTAK host itself and must never be packaged --
# shipping our own copy risks loading a second, conflicting instance into the shared
# AppDomain. These are referenced with <Private>False</Private> so MSBuild does not copy
# them to bin\, but the filter is explicit so an accidental <Private>True</Private> in the
# csproj cannot leak an SDK assembly into a release package.
SDK_ASSEMBLY_PREFIXES = ("WinTak.", "TAK.Engine", "Prism")

PLUGIN_DIR_IN_WPK = "plugin/x64/FeatureLink/"


def fail(message):
    print("build-wpk.py: error: " + message, file=sys.stderr)
    sys.exit(2)


def read_manifest(manifest_path, version, sdk_version):
    """Renders manifest.xml from the checked-in MANIFEST.xml, substituting the version and
    sdkVersion MSBuild owns. Falls back to a minimal generated manifest only if the file is
    missing, so the description/structure has exactly one source of truth."""
    if manifest_path and os.path.isfile(manifest_path):
        with open(manifest_path, "r", encoding="utf-8-sig") as fh:
            text = fh.read()
        text = replace_element(text, "version", version)
        text = replace_element(text, "sdkVersion", sdk_version)
        # WinTAK's manifest parser has always been fed a UTF-8 BOM by this script; keep it.
        return "﻿" + text.lstrip("﻿")
    fail("MANIFEST.xml not found at %r -- it is the single source of manifest truth" % manifest_path)


def replace_element(xml_text, tag, value):
    open_tag = "<%s>" % tag
    close_tag = "</%s>" % tag
    start = xml_text.find(open_tag)
    end = xml_text.find(close_tag)
    if start < 0 or end < 0 or end < start:
        fail("MANIFEST.xml has no <%s> element to substitute" % tag)
    return xml_text[: start + len(open_tag)] + value + xml_text[end:]


def collect_dependencies(build_dir, primary_dll_name):
    """Every managed assembly MSBuild copied next to the plugin DLL, minus the plugin itself
    and minus host-provided SDK assemblies."""
    deps = []
    for entry in sorted(os.listdir(build_dir)):
        if not entry.lower().endswith(".dll"):
            continue
        if entry == primary_dll_name:
            continue
        if any(entry.startswith(p) for p in SDK_ASSEMBLY_PREFIXES):
            continue
        deps.append(entry)
    return deps


def main():
    argv = sys.argv[1:]
    manifest_path = None
    if "--manifest" in argv:
        idx = argv.index("--manifest")
        try:
            manifest_path = argv[idx + 1]
        except IndexError:
            fail("--manifest requires a path")
        del argv[idx : idx + 2]

    if len(argv) != 5:
        print(
            "Usage: build-wpk.py <version> <sdk_version> <dll_path> <assets_dir> <out_path> "
            "[--manifest <MANIFEST.xml>]",
            file=sys.stderr,
        )
        sys.exit(1)

    version, sdk_version, dll_path, assets_dir, out_path = argv

    # ---- validate every input up front (C-18/build-wpk hardening) -------------------
    if not os.path.isfile(dll_path):
        fail("plugin assembly not found: %r (was the Release build run?)" % dll_path)
    build_dir = os.path.dirname(os.path.abspath(dll_path))
    primary_dll_name = os.path.basename(dll_path)

    if manifest_path is None:
        manifest_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "MANIFEST.xml")

    dependencies = collect_dependencies(build_dir, primary_dll_name)
    missing = [d for d in REQUIRED_DEPENDENCIES if d not in dependencies]
    if missing:
        fail(
            "required dependencies missing from %r: %s. Run `nuget restore` / rebuild so "
            "MSBuild copies them next to %s." % (build_dir, ", ".join(missing), primary_dll_name)
        )

    manifest = read_manifest(manifest_path, version, sdk_version)

    out_dir = os.path.dirname(os.path.abspath(out_path))
    if out_dir and not os.path.isdir(out_dir):
        try:
            os.makedirs(out_dir)
        except OSError as exc:
            fail("cannot create output directory %r: %s" % (out_dir, exc))

    # zipfile mode "w" truncates; the previous explicit os.remove() only added a failure
    # mode when a shell/preview handler held a lock on the old artifact.
    try:
        with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("manifest.xml", manifest.encode("utf-8"))

            icon_path = os.path.join(assets_dir, "Large.png")
            if os.path.exists(icon_path):
                z.write(icon_path, "icon.png")

            z.write(dll_path, PLUGIN_DIR_IN_WPK + primary_dll_name)

            # AutoGenerateBindingRedirects emits <dll>.config; without it a host that ships a
            # different Newtonsoft.Json major version cannot bind ours.
            config_path = dll_path + ".config"
            if os.path.isfile(config_path):
                z.write(config_path, PLUGIN_DIR_IN_WPK + primary_dll_name + ".config")

            for dep in dependencies:
                z.write(os.path.join(build_dir, dep), PLUGIN_DIR_IN_WPK + dep)
    except (OSError, zipfile.BadZipFile) as exc:
        fail("failed writing %r: %s" % (out_path, exc))

    print("WPK written: %s" % out_path)
    print("  plugin:       %s" % primary_dll_name)
    print("  dependencies: %s" % (", ".join(dependencies) or "(none)"))


if __name__ == "__main__":
    main()
