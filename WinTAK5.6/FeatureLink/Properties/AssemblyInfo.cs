using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: AssemblyTitle("FeatureLink")]
[assembly: AssemblyDescription("Sync ArcGIS Feature Layers and send Position Location Information (PLI) from WinTAK")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("FeatureLink")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// ── WinTAK plugin identity attributes ────────────────────────────────────────
// These are what WinTAK reads to validate and display the plugin.
// TakSdkVersion MUST match the installed WinTAK version or the plugin
// will be rejected as "Not a valid Plugin".
[assembly: WinTak.Framework.TakSdkVersion("5.6.0.151")]
[assembly: WinTak.Framework.PluginName("FeatureLink")]
[assembly: WinTak.Framework.PluginDescription("Browse, sync, and share ArcGIS Feature Layers on the map, and send this device's Position Location Information (PLI) to an ArcGIS Feature Layer. Ported from the ATAK FeatureLink plugin (com.atakmap.android.featurelink.plugin).")]
