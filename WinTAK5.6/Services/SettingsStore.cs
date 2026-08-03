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
            /// <summary>On-device layers not shared to Everyone — either received via a share
            /// from another user, or moved here from the "My ArcGIS Layers" browse list on first
            /// download because their portal item's Access wasn't "public".</summary>
            public List<ArcGisLayer> SharedPrivateLayers { get; set; } = new List<ArcGisLayer>();
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

                var sharedPrivateEl = root.Element("SharedPrivateLayers");
                if (sharedPrivateEl != null)
                    settings.SharedPrivateLayers = sharedPrivateEl.Elements("Layer")
                        .Select(ArcGisLayer.FromXElement).ToList();

                settings.Pli = PliSettings.FromXElement(root.Element("Pli"));

                var excludedEl = root.Element("ExcludedPrivateLayerUrls");
                if (excludedEl != null)
                    settings.ExcludedPrivateLayerUrls = excludedEl.Elements("Url")
                        .Select(e => e.Value).ToList();
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable settings file. This used to discard the operator's entire
                // configuration silently, with no backup, no log and no message. Now the bad file
                // is preserved so it can be recovered or diagnosed, and the failure is reported.
                Log.Error("settings.xml could not be read — starting with an empty configuration.", ex);
                TryBackupCorruptSettings();
                return new FeatureLinkSettings();
            }
            return settings;
        }

        private static void TryBackupCorruptSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string backup = SettingsPath + ".corrupt-"
                    + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                File.Copy(SettingsPath, backup, true);
                Log.Warn("Unreadable settings backed up to " + backup);
            }
            catch (Exception ex) { Log.Warn("Could not back up the unreadable settings file: " + ex.Message); }
        }

        /// <summary>Saves without throwing. <see cref="Save"/> had no try/catch at all and is
        /// called from WPF property setters, command handlers and the background PLI timer, so a
        /// read-only profile, a full disk or an AV lock on the temp file produced either a WinTAK
        /// crash dialog or an unobserved timer exception.</summary>
        public static bool TrySave(FeatureLinkSettings settings, out Exception error)
        {
            try
            {
                Save(settings);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        public static void Save(FeatureLinkSettings settings)
        {
            Directory.CreateDirectory(RootDir);

            var root = new XElement("FeatureLinkSettings",
                new XElement("PortalUrl", settings.PortalUrl ?? "https://www.arcgis.com"),
                new XElement("PrivateLayers", settings.PrivateLayers.Select(l => l.ToXElement())),
                new XElement("PublicLayers", settings.PublicLayers.Select(l => l.ToXElement())),
                new XElement("SharedPrivateLayers", settings.SharedPrivateLayers.Select(l => l.ToXElement())),
                settings.Pli.ToXElement(),
                new XElement("ExcludedPrivateLayerUrls",
                    settings.ExcludedPrivateLayerUrls.Select(u => new XElement("Url", u))));

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

            // Write to a temp file then replace atomically. The previous Delete-then-Move was NOT
            // atomic: a crash or power loss in the window between the two left NO settings file at
            // all — total loss of every layer, exclusion and PLI setting. File.Replace is atomic
            // on NTFS and keeps a backup copy of the previous file.
            var tempPath = SettingsPath + ".tmp";
            var backupPath = SettingsPath + ".bak";
            try
            {
                doc.Save(tempPath);
                if (File.Exists(SettingsPath))
                    File.Replace(tempPath, SettingsPath, backupPath, true);
                else
                    File.Move(tempPath, SettingsPath);
            }
            catch
            {
                // Never leave a stale .tmp accumulating on a failed save.
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        // -------------------------------------------------------------------------
        // DPAPI-protected token storage
        // -------------------------------------------------------------------------

        /// <summary>Returns false (rather than throwing out of <c>ApplyTokens</c> → <c>SignInAsync</c>)
        /// when the blob cannot be written: <c>ProtectedData.Protect</c> throws
        /// <c>CryptographicException</c> on some domain/profile configurations, and a read-only
        /// profile fails the write. The caller stays signed in for this session and reports
        /// "signed in but not persisted" rather than "sign-in failed".</summary>
        public static bool SaveTokenState(StoredTokenState state)
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                if (state == null)
                {
                    if (File.Exists(TokensPath)) File.Delete(TokensPath);
                    return true;
                }

                var json = JsonConvert.SerializeObject(state);
                var plainBytes = System.Text.Encoding.UTF8.GetBytes(json);
                var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

                // Atomic replace for the token blob too.
                var tempPath = TokensPath + ".tmp";
                File.WriteAllBytes(tempPath, protectedBytes);
                if (File.Exists(TokensPath)) File.Replace(tempPath, TokensPath, null, true);
                else File.Move(tempPath, TokensPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not persist the ArcGIS session — you will need to sign in again next launch.", ex);
                return false;
            }
        }

        public static StoredTokenState LoadTokenState()
        {
            if (!File.Exists(TokensPath)) return null;
            try
            {
                var protectedBytes = File.ReadAllBytes(TokensPath);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var json = System.Text.Encoding.UTF8.GetString(plainBytes);
                // Explicit settings pin TypeNameHandling.None: JsonConvert.DefaultSettings is
                // process-wide and every WinTAK plugin shares one AppDomain, so another plugin
                // enabling TypeNameHandling would otherwise turn this into a deserialization sink.
                return SafeJson.Deserialize<StoredTokenState>(json);
            }
            catch (Exception ex)
            {
                // Blob unreadable (different user account, corrupted, or DPAPI key rotated) —
                // treat as logged out rather than throw; user just signs in again.
                Log.Warn("Stored ArcGIS session could not be read: " + ex.Message);
                return null;
            }
        }

        public static void ClearTokenState()
        {
            try
            {
                if (File.Exists(TokensPath)) File.Delete(TokensPath);
            }
            catch (Exception ex)
            {
                Log.Error("Could not delete the stored ArcGIS session on sign-out.", ex);
            }
        }
    }
}
