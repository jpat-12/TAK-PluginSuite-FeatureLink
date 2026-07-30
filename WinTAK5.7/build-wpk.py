"""
Packages the Release build into a WinTAK plugin (.wpk) file
compatible with WinTAK's Plugin Manager import.

Structure mirrors TAK Product Center plugins:
  manifest.xml                     - wintakPluginPackage metadata
  icon.png                         - plugin icon (optional, uses Assets/Large.png)
  plugin/x64/FeatureLink/
      FeatureLink.dll               - the compiled plugin

Usage: python build-wpk.py <version> <sdk_version> <dll_path> <assets_dir> <out_path>
Called automatically by the PackageWpk MSBuild target.
"""
import sys
import os
import zipfile

def main():
    if len(sys.argv) != 6:
        print("Usage: build-wpk.py <version> <sdk_version> <dll_path> <assets_dir> <out_path>")
        sys.exit(1)

    version, sdk_version, dll_path, assets_dir, out_path = sys.argv[1:]

    manifest = f"""﻿<?xml version="1.0" encoding="utf-8"?>
<wintakPluginPackage>
  <metadata>
    <id>FeatureLink</id>
    <version>{version}</version>
    <sdkVersion>{sdk_version}</sdkVersion>
    <takVariants>
    </takVariants>
    <description>FeatureLink: browse and sync ArcGIS Feature Layers to the map, and send this device's Position Location Information (PLI) to an ArcGIS Feature Layer via OAuth2 sign-in. Ported from the ATAK FeatureLink plugin.</description>
    <dependencies>
    </dependencies>
  </metadata>
</wintakPluginPackage>"""

    if os.path.exists(out_path):
        os.remove(out_path)

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        # manifest at root
        z.writestr("manifest.xml", manifest.encode("utf-8"))

        # icon at root (use Large.png if present)
        icon_path = os.path.join(assets_dir, "Large.png")
        if os.path.exists(icon_path):
            z.write(icon_path, "icon.png")

        # DLL under plugin/x64/<PluginName>/
        z.write(dll_path, "plugin/x64/FeatureLink/FeatureLink.dll")

    print(f"WPK written: {out_path}")

if __name__ == "__main__":
    main()
