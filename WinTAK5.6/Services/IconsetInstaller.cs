using System;
using System.Globalization;
using System.IO;
using TAKEngine.Core;

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
        public static bool Install(string uid, string group, byte[] zipBytes, out string zipPath,
            out bool replacedExisting)
        {
            zipPath = null;
            replacedExisting = false;
            if (string.IsNullOrEmpty(uid) || zipBytes == null || zipBytes.Length == 0) return false;

            try
            {
                var manager = WinTak.CursorOnTarget.Icons.IconsetDatabaseManager.GetReference();
                if (manager == null)
                {
                    Log.Warn("WinTAK's iconset database is unavailable; layer icons will not be installed.");
                    return false;
                }

                Directory.CreateDirectory(IconsetDirectory);
                zipPath = Path.Combine(IconsetDirectory, uid + ".zip");

                // An existing set under this UID is ALWAYS removed and replaced, never reused.
                //
                // The UID hashes the layer URL and driving field, NOT the symbols, so it is a
                // stable identity for "this layer's icons" and says nothing about whether those
                // icons are current. Anything that reused a matching UID therefore risked serving
                // stale icons indefinitely: a layer whose renderer an earlier build mis-read kept
                // its wrong iconset even after the reader was fixed, because the UID still
                // matched. The freshly generated zip is the authority; what the host already
                // holds is not.
                //
                // Note this makes an install do real work on every sync of a picture-marker
                // layer, including each recurrence tick.
                if (manager.ContainsIconset(uid))
                {
                    replacedExisting = true;
                    Log.Info($"Replacing the installed iconset \"{group}\" (uid {uid}).");
                    try { manager.RemoveIconset(uid, false); }
                    catch (Exception ex) { Log.Warn("Could not remove the existing iconset: " + ex.Message); }

                    // Removing the iconset drops the DATABASE record; it does not touch WinTAK's
                    // render cache, and the cache is what actually draws. That matters here more
                    // than it looks: an operator who re-styles a layer in ArcGIS usually keeps the
                    // same category labels, so the icon's path — uid/group/filename — is
                    // byte-identical and only the PNG bytes changed. The cache therefore hits and
                    // serves the previous image forever. Observed in the field: a freshly
                    // regenerated zip alongside cached PNGs a day older, with the map still
                    // drawing the old symbols.
                    if (manager.ContainsIconset(uid))
                    {
                        // Importing over a set the host still holds is how a UID mismatch gets
                        // manufactured — WinTAK may keep the old record and hash the new zip.
                        // Better to keep the existing icons than to silently break cross-platform
                        // resolution for this layer.
                        Log.Error($"WinTAK would not remove the existing iconset \"{group}\" (uid {uid}); "
                                  + "keeping the installed one rather than risking a UID mismatch.");
                        return false;
                    }

                    // Only once the record is actually gone: purging the cache for a set the host
                    // still holds would strip its renderings while leaving it installed.
                    PurgeIconCache(uid, group);
                }

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

                // Only on a replacement: the textures for this set are already on the GPU and
                // would otherwise keep being drawn in place of the new images.
                if (replacedExisting) PurgeRendererTextures();

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not install the iconset for \"{group}\".", ex);
                return false;
            }
        }

        /// <summary>
        /// Asks the map renderer to drop its cached GPU textures, so a replaced icon is re-read
        /// from disk instead of redrawn from the copy already uploaded.
        ///
        /// <para><b>Why this is needed on top of everything else.</b> Replacing an iconset has
        /// four layers of staleness, and the first three are not enough on their own: the
        /// database record (fixed by removing before importing), the on-disk render cache (fixed
        /// by <see cref="PurgeIconCache"/>), the marker's own resolved icon (fixed by recreating
        /// the markers), and finally the renderer's texture cache. The evidence for this last one
        /// was unambiguous: the Marker Details panel and the Point Dropper both showed the NEW
        /// icon while the map still drew the old one — same data, two renderers, one of them
        /// holding a texture.</para>
        ///
        /// <para>Called by reflection rather than against a compile-time reference.
        /// <c>Spyglass.Graphics</c> is a rendering assembly, not part of the SDK surface a plugin
        /// is meant to bind to, and this is a best-effort cache hint rather than functionality —
        /// if a future WinTAK renames or drops it, the icons stay stale for a session instead of
        /// the plugin failing to load. The type is resolved from assemblies already in the
        /// process, so nothing new is loaded.</para>
        ///
        /// <para>This purges every texture, not just ours. That is a visible cost — the map
        /// re-uploads what it needs over the next frames — so it runs ONLY when an iconset was
        /// actually replaced, which is rare.</para>
        /// </summary>
        /// <summary>
        /// The host's map view controller, supplied by the dock pane. Used ONLY to reach the
        /// render thread — see <see cref="PurgeRendererTextures"/>. Held as <c>object</c> so this
        /// file gains no new SDK reference; the cast to <c>RenderContext</c> is against
        /// TAK.Engine, which is already referenced.
        /// </summary>
        public static object RenderHost { get; set; }

        private static void PurgeRendererTextures()
        {
            try
            {
                Type cacheType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    cacheType = asm.GetType("Spyglass.Graphics.TextureCache", throwOnError: false);
                    if (cacheType != null) break;
                }
                if (cacheType == null) return;

                var singleton = cacheType.GetProperty("Singleton",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                object instance = singleton?.GetValue(null);
                if (instance == null) return;

                var purge = cacheType.GetMethod("Purge", Type.EmptyTypes);
                if (purge == null) return;

                // MUST run on the render thread. This is called from the layer-download worker,
                // and purging the GPU texture cache off-thread tears textures out from under
                // markers the renderer is still drawing — the host then dereferences a texture
                // that is gone. The observed symptom was a NullReferenceException inside
                // WinTak.CursorOnTarget.Graphics.CotMapMarker.OnHoverChanged when the operator
                // moved the mouse over a marker after a re-sync. RenderContext documents the
                // affinity ("graphics resources may be shared ... only in a thread-safe manner")
                // and offers both the test and the marshal.
                var context = RenderHost as RenderContext;
                if (context == null)
                {
                    // Without a way onto the render thread, DO NOT purge. Stale icons for a
                    // session are a cosmetic defect; crashing the host is not.
                    Log.Info("Skipping the texture-cache purge: no render context is available, "
                             + "and purging off the render thread can crash the map. The new icons "
                             + "will be drawn after a restart.");
                    return;
                }

                if (context.IsRenderThread)
                {
                    purge.Invoke(instance, null);
                }
                else
                {
                    context.QueueEvent(_ =>
                    {
                        try { purge.Invoke(instance, null); }
                        catch (Exception ex)
                        {
                            // On the render thread: swallowing beats propagating into the host's
                            // frame loop, which would take the map down rather than one icon.
                            Log.Info("Texture-cache purge failed on the render thread: " + ex.Message);
                        }
                    }, null);
                }

                Log.Info("Asked the map renderer to drop its cached textures so the new icons are drawn.");
            }
            catch (Exception ex)
            {
                // Best effort by design — see the remarks. Stale icons for a session, nothing worse.
                Log.Info("Could not purge the renderer's texture cache: " + ex.Message);
            }
        }

        /// <summary>
        /// Deletes WinTAK's cached renderings for an iconset, so a replaced set is actually drawn
        /// with its new images.
        ///
        /// <para>The path is derived from the host's own
        /// <c>IconsetDatabaseManager.GetIconCachePath</c> rather than assembled from
        /// <c>%AppData%</c>, so it follows the host if it ever moves. It is then checked to
        /// contain the uid before anything is deleted — this method removes a directory tree, and
        /// a wrong path would delete somebody else's icons.</para>
        /// </summary>
        private static void PurgeIconCache(string uid, string group)
        {
            try
            {
                string probe = WinTak.CursorOnTarget.Icons.IconsetDatabaseManager
                    .GetIconCachePath(uid, group, "probe.png");
                if (string.IsNullOrEmpty(probe)) return;

                // .../iconcache/{uid}/{group}/probe.png -> .../iconcache/{uid}
                string groupDir = Path.GetDirectoryName(probe);
                string uidDir = groupDir == null ? null : Path.GetDirectoryName(groupDir);
                if (string.IsNullOrEmpty(uidDir)) return;

                // Refuse to delete anything whose path does not name this uid.
                string leaf = Path.GetFileName(uidDir.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.Equals(leaf, uid, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"Not purging the icon cache: \"{uidDir}\" does not end in the iconset uid.");
                    return;
                }

                if (!Directory.Exists(uidDir)) return;
                Directory.Delete(uidDir, recursive: true);
                Log.Info($"Purged the cached renderings for iconset \"{group}\" so the new images are drawn.");
            }
            catch (Exception ex)
            {
                // A locked file leaves the old images cached — the icons stay stale, but nothing
                // else breaks, and the install still proceeds.
                Log.Warn("Could not purge the icon cache for " + uid + ": " + ex.Message);
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
