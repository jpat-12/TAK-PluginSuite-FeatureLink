using System;
using System.Globalization;
using System.IO;

namespace FeatureLink.Services
{
    /// <summary>
    /// Installs a generated iconset into WinTAK's icon database.
    ///
    /// <para>This is the only file in the auto-iconset feature that touches the WinTAK SDK, and it
    /// is deliberately thin. Everything with a rule attached — canonicalization, the UID hash,
    /// group and file naming, the zip and its manifest — lives in <see cref="AutoIconset"/>, which
    /// is SDK-free and therefore unit-tested against the cross-platform golden vectors. What
    /// remains here is a file write and one host call, neither of which can be asserted without a
    /// running WinTAK.</para>
    ///
    /// <para><b>The host API.</b> ATAK installs an iconset by writing a zip and broadcasting an
    /// intent. WinTAK has no intents; the equivalent is
    /// <c>WinTak.CursorOnTarget.Icons.IconsetDatabaseManager</c>, which is public, lives in
    /// <c>WinTak.CursorOnTarget.dll</c> (already referenced by this project), and is the same API
    /// WinTAK's own Iconset Manager is built on. It imports the zip into
    /// <c>%AppData%\WinTAK\Databases\iconsets.sqlite</c> and populates the render cache.</para>
    ///
    /// <para><b>Why the UID check is load-bearing.</b> WinTAK reads the UID declared inside
    /// <c>iconset.xml</c> and stores it verbatim — confirmed against a real installed set, whose
    /// database row carried the XML's UID rather than the zip's hash. If the XML is ever rejected
    /// (a malformed manifest, a stray attribute — see <see cref="AutoIconset.BuildIconsetXml"/>),
    /// the host falls back to hashing the zip bytes instead, and the resulting UID is one no other
    /// platform can reproduce. Icons then resolve locally and nowhere else, which is the single
    /// worst outcome available here because everything still looks correct on this workstation.
    /// So the returned UID is compared against the computed one and a mismatch is logged loudly.</para>
    /// </summary>
    public static class IconsetInstaller
    {
        /// <summary>Where generated zips are kept. Under the plugin's own folder — the host owns
        /// <c>%AppData%\WinTAK\icons\</c> and <c>Databases\</c>, and the import populates those.</summary>
        private static string IconsetDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinTAK", "FeatureLink", "iconsets");

        /// <summary>
        /// Writes the zip and imports it, returning true when the host holds an iconset under the
        /// expected UID afterwards.
        ///
        /// <para>Never throws: symbology is an enhancement, and a layer download must not fail
        /// because an icon could not be installed. Every failure path logs and returns false, and
        /// the caller falls back to unstyled markers.</para>
        /// </summary>
        /// <param name="uid">The §4 digest. Also the zip's file name, so two layers that share a
        /// display name cannot collide — ATAK names the zip after the group and has exactly that
        /// open problem.</param>
        public static bool Install(string uid, string group, byte[] zipBytes, out string zipPath)
        {
            zipPath = null;
            if (string.IsNullOrEmpty(uid) || zipBytes == null || zipBytes.Length == 0) return false;

            try
            {
                var manager = WinTak.CursorOnTarget.Icons.IconsetDatabaseManager.GetReference();
                if (manager == null)
                {
                    Log.Warn("WinTAK's iconset database is unavailable; layer icons will not be installed.");
                    return false;
                }

                // The UID is a hash of the layer URL and its driving field, so it IS the content
                // identity: if the host already has this set, re-importing it would do nothing but
                // cost time on every sync. Re-generation only matters when the published renderer
                // changes, which changes the icons but not the UID — handled by ForceReinstall.
                if (manager.ContainsIconset(uid)) return true;

                Directory.CreateDirectory(IconsetDirectory);
                zipPath = Path.Combine(IconsetDirectory, uid + ".zip");

                // Write via a temp file and move, so an interrupted write cannot leave a truncated
                // zip that the host would then try to import.
                string tempPath = zipPath + ".tmp";
                File.WriteAllBytes(tempPath, zipBytes);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(tempPath, zipPath);

                var installed = manager.ImportIconsetZip(zipPath);
                if (installed == null)
                {
                    Log.Warn($"WinTAK refused the iconset for group \"{group}\"; markers will use default icons.");
                    return false;
                }

                if (!string.Equals(installed.Uid, uid, StringComparison.Ordinal))
                {
                    // See the class remarks: this means the manifest was rejected and the host
                    // hashed the zip instead, so this device's icon paths no longer match any
                    // other platform's for the same layer.
                    Log.Error(string.Format(CultureInfo.InvariantCulture,
                        "Iconset UID mismatch for \"{0}\": expected {1}, WinTAK stored {2}. Icons will "
                        + "resolve on this machine only and will NOT match other TAK devices.",
                        group, uid, installed.Uid));
                    return false;
                }

                Log.Info($"Installed iconset \"{group}\" ({installed.IconCount} icons, uid {uid}).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not install the iconset for \"{group}\".", ex);
                return false;
            }
        }

        /// <summary>Drops a previously installed set so the next download regenerates it. For a
        /// layer whose published renderer changed: the UID is derived from the URL and field, so
        /// it does not change when the symbols do, and <see cref="Install"/>'s idempotence would
        /// otherwise keep serving the stale icons forever.</summary>
        public static bool ForceReinstall(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            try
            {
                var manager = WinTak.CursorOnTarget.Icons.IconsetDatabaseManager.GetReference();
                if (manager == null || !manager.ContainsIconset(uid)) return false;
                manager.RemoveIconset(uid, false);
                Log.Info("Removed iconset " + uid + " so it can be regenerated.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not remove iconset " + uid + ": " + ex.Message);
                return false;
            }
        }
    }
}
