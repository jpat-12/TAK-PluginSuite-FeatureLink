using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows;

// C-17 / C-36: this assembly and the 5.6 one both reported version 1.0.0.0 and an identical
// PluginName, so a support engineer holding a crash dump or a file-properties dialog could not
// tell the two builds apart at all. The version now says what this tree actually is — a stale
// fork that is NOT at parity with 5.6 — and the fourth field carries the WinTAK SDK minor.
// See docs/remediation/wp3-wintak-57-parity.md.
[assembly: AssemblyTitle("FeatureLink (WinTAK 5.7 port — incomplete)")]
[assembly: AssemblyDescription("Sync ArcGIS Feature Layers and send Position Location Information (PLI) from WinTAK 5.7. INCOMPLETE PORT — not at parity with the WinTAK 5.6 build.")]
[assembly: AssemblyCompany("Civil Air Patrol — FeatureLink project")]
[assembly: AssemblyProduct("FeatureLink for WinTAK 5.7 (pre-release)")]
[assembly: AssemblyCopyright("Copyright © 2026 Civil Air Patrol — FeatureLink project. Licensed under the Apache License, Version 2.0.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyMetadata("License", "Apache-2.0")]
[assembly: AssemblyMetadata("LicenseUrl", "https://www.apache.org/licenses/LICENSE-2.0")]
[assembly: NeutralResourcesLanguage("en-US")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("0.9.0.7")]
[assembly: AssemblyFileVersion("0.9.0.7")]
[assembly: AssemblyInformationalVersion("0.9.0-pre+wintak5.7.0.144")]

// ── WinTAK plugin identity attributes ────────────────────────────────────────
// These are what WinTAK reads to validate and display the plugin.
// TakSdkVersion MUST match the installed WinTAK version or the plugin
// will be rejected as "Not a valid Plugin".
[assembly: WinTak.Framework.TakSdkVersion("5.7.0.144")]
[assembly: WinTak.Framework.PluginName("FeatureLink (5.7 pre-release)")]
[assembly: WinTak.Framework.PluginDescription("INCOMPLETE 5.7 PORT — not for release. Browse and sync ArcGIS Feature Layers on the map, and send this device's Position Location Information (PLI) to an ArcGIS Feature Layer. Missing versus the WinTAK 5.6 build: Mission Package layer share, send-item-to-PLI, live marker visibility, orphan-marker cleanup, and display-style resolution. Licensed under Apache-2.0.")]
