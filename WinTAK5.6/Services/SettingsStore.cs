using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using FeatureLink.Models;
using Newtonsoft.Json;

namespace FeatureLink.Services
{
    /// <summary>
    /// Persists FeatureLink's layer list and PLI configuration to
    /// <c>%AppData%\WinTAK\FeatureLink\settings.xml</c>, and the ArcGIS refresh-token state to a
    /// separate DPAPI-encrypted blob (<c>tokens.bin</c>) in the same folder.
    ///
    /// This replaces the ATAK plugin's Android SharedPreferences (featurelink_prefs) —
    /// PREF_LAYERS_JSON / PREF_PUBLIC_LAYERS_JSON / PREF_PLI_* — with a single readable XML file
    /// for the non-sensitive state, matching the constraint that plaintext tokens must never be
    /// written to disk. Tokens are DPAPI-protected with <see cref="DataProtectionScope.CurrentUser"/>,
    /// so the blob is only decryptable by the same Windows user account that created it (and only
    /// on the same machine) — there is no cross-machine roaming of a signed-in session.
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string RootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinTAK", "FeatureLink");

        private static readonly string SettingsPath = Path.Combine(RootDir, "settings.xml");
        private static readonly string TokensPath = Path.Combine(RootDir, "tokens.bin");

        // Binds the encrypted token blob to this app so a copied tokens.bin from another DPAPI
        // consumer on the same account can't be fed back in and silently decrypt.
        private static readonly byte[] Entropy =
            System.Text.Encoding.UTF8.GetBytes("FeatureLink.ArcGIS.TokenStore.v1");

        public sealed class FeatureLinkSettings
        {
            public List<ArcGisLayer> PrivateLayers { get; set; } = new List<ArcGisLayer>();
            public List<ArcGisLayer> PublicLayers { get; set; } = new List<ArcGisLayer>();
            public PliSettings Pli { get; set; } = new PliSettings();
            public string PortalUrl { get; set; } = "https://www.arcgis.com";

            /// <summary>Private-layer URLs the user has explicitly removed on this device — kept
            /// out of the next sign-in's auto-populated list, mirroring
            /// PREF_EXCLUDED_PRIVATE_URLS.</summary>
            public List<string> ExcludedPrivateLayerUrls { get; set; } = new List<string>();
        }

        public static FeatureLinkSettings Load()
        {
            var settings = new FeatureLinkSettings();
            if (!File.Exists(SettingsPath)) return settings;

            try
            {
                var doc = XDocument.Load(SettingsPath);
                var root = doc.Root;
                if (root == null) return settings;

                settings.PortalUrl = (string)root.Element("PortalUrl") ?? "https://www.arcgis.com";

                var privateEl = root.Element("PrivateLayers");
                if (privateEl != null)
                    settings.PrivateLayers = privateEl.Elements("Layer")
                        .Select(ArcGisLayer.FromXElement).ToList();

                var publicEl = root.Element("PublicLayers");
                if (publicEl != null)
                    settings.PublicLayers = publicEl.Elements("Layer")
                        .Select(ArcGisLayer.FromXElement).ToList();

                settings.Pli = PliSettings.FromXElement(root.Element("Pli"));

                var excludedEl = root.Element("ExcludedPrivateLayerUrls");
                if (excludedEl != null)
                    settings.ExcludedPrivateLayerUrls = excludedEl.Elements("Url")
                        .Select(e => e.Value).ToList();
            }
            catch
            {
                // Corrupt/unreadable settings file — start clean rather than crash the plugin.
                return new FeatureLinkSettings();
            }
            return settings;
        }

        public static void Save(FeatureLinkSettings settings)
        {
            Directory.CreateDirectory(RootDir);

            var root = new XElement("FeatureLinkSettings",
                new XElement("PortalUrl", settings.PortalUrl ?? "https://www.arcgis.com"),
                new XElement("PrivateLayers", settings.PrivateLayers.Select(l => l.ToXElement())),
                new XElement("PublicLayers", settings.PublicLayers.Select(l => l.ToXElement())),
                settings.Pli.ToXElement(),
                new XElement("ExcludedPrivateLayerUrls",
                    settings.ExcludedPrivateLayerUrls.Select(u => new XElement("Url", u))));

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

            // Write to a temp file then move — avoids a half-written settings.xml if WinTAK is
            // killed mid-save (e.g. force-close while a PLI tick is writing LastSyncTicks).
            var tempPath = SettingsPath + ".tmp";
            doc.Save(tempPath);
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            File.Move(tempPath, SettingsPath);
        }

        // -------------------------------------------------------------------------
        // DPAPI-protected token storage
        // -------------------------------------------------------------------------

        public static void SaveTokenState(StoredTokenState state)
        {
            Directory.CreateDirectory(RootDir);
            if (state == null)
            {
                if (File.Exists(TokensPath)) File.Delete(TokensPath);
                return;
            }

            var json = JsonConvert.SerializeObject(state);
            var plainBytes = System.Text.Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokensPath, protectedBytes);
        }

        public static StoredTokenState LoadTokenState()
        {
            if (!File.Exists(TokensPath)) return null;
            try
            {
                var protectedBytes = File.ReadAllBytes(TokensPath);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var json = System.Text.Encoding.UTF8.GetString(plainBytes);
                return JsonConvert.DeserializeObject<StoredTokenState>(json);
            }
            catch
            {
                // Blob unreadable (different user account, corrupted, or DPAPI key rotated) —
                // treat as logged out rather than throw; user just signs in again.
                return null;
            }
        }

        public static void ClearTokenState()
        {
            if (File.Exists(TokensPath)) File.Delete(TokensPath);
        }
    }
}
