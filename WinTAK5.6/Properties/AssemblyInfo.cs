using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;

// C-36: AssemblyCompany was empty and the copyright had no holder, so the shipped binary was
// unattributable and failed any software-provenance/SBOM check. The licence is declared here as
// well as in MANIFEST.xml and the project README. WP5 owns the repo-level NOTICE and the
// third-party dependency inventory — Newtonsoft.Json 13.0.3 (MIT) is this assembly's only non-SDK
// dependency; see CROSS-PACKAGE REQUESTS in docs/remediation/wp3-wintak.md.
[assembly: AssemblyTitle("FeatureLink")]
[assembly: AssemblyDescription("Sync ArcGIS Feature Layers and send Position Location Information (PLI) from WinTAK")]
[assembly: AssemblyCompany("Civil Air Patrol — FeatureLink project")]
[assembly: AssemblyProduct("FeatureLink for WinTAK")]
[assembly: AssemblyCopyright("Copyright © 2026 Civil Air Patrol — FeatureLink project. Licensed under the Apache License, Version 2.0.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyMetadata("License", "Apache-2.0")]
[assembly: AssemblyMetadata("LicenseUrl", "https://www.apache.org/licenses/LICENSE-2.0")]
[assembly: NeutralResourcesLanguage("en-US")]
[assembly: ComVisible(false)]

// Version: kept in step with MANIFEST.xml's <version> and the csproj's $(WpkVersion). These were
// hardcoded 1.0.0.0 while the package claimed 2.6.9, so the binary on disk could not be correlated
// to a release and every crash dump reported 1.0.0.0 forever.
// The fourth field carries the WinTAK SDK minor (6 = 5.6.x) so a support engineer can tell the 5.6
// and 5.7 builds apart from file properties alone — C-17.
[assembly: AssemblyVersion("2.7.0.6")]
[assembly: AssemblyFileVersion("2.7.0.6")]
[assembly: AssemblyInformationalVersion("2.7.0+wintak5.6.0.151")]

// ── WinTAK plugin identity attributes ────────────────────────────────────────
// These are what WinTAK reads to validate and display the plugin.
// TakSdkVersion MUST match the installed WinTAK version or the plugin
// will be rejected as "Not a valid Plugin".
[assembly: WinTak.Framework.TakSdkVersion("5.6.0.151")]
[assembly: WinTak.Framework.PluginName("FeatureLink")]
[assembly: WinTak.Framework.PluginDescription("Browse, sync, and share ArcGIS Feature Layers on the map, and send this device's Position Location Information (PLI) to an ArcGIS Feature Layer. Ported from the ATAK FeatureLink plugin (com.atakmap.android.featurelink.plugin). Licensed under Apache-2.0.")]
