using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WinTak.MissionPackages;

namespace FeatureLink.Services
{
    /// <summary>
    /// Puts a package FeatureLink created into WinTAK's own Data Packages list.
    ///
    /// <para>Without this a created package is only a file. It sends correctly and a recipient can
    /// import it, but the operator who built it cannot find it again anywhere in WinTAK — which
    /// made "create a package" feel like it had not done anything.</para>
    ///
    /// <para><b>This reverses an earlier decision, deliberately.</b> <c>DataPackageWriter</c>
    /// documents why the SDK's package builder is not used, and that reasoning still holds for
    /// <i>writing</i>: the manifest is the part every other TAK client must agree with, so it stays
    /// SDK-free and fixture-tested. Registering a finished zip with the host is a different job
    /// that only the host can do, so this file does that and nothing else. The split is the point
    /// — the testable part stayed testable and the untestable part is twenty lines.</para>
    /// </summary>
    public static class MissionPackageRegistrar
    {
        /// <summary>WinTAK's Data Packages folder, used as the default destination so a created
        /// package lands where every other package on the machine lives.</summary>
        public static string PackagesFolder
        {
            get
            {
                // Prefer the host's own answer, which follows WinTAK if it ever moves.
                try
                {
                    string declared = PackagesDirectory.DirectoryFullName;
                    if (!string.IsNullOrWhiteSpace(declared)) return declared;
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not read WinTAK's packages directory from the SDK: " + ex.Message);
                }

                return DefaultPackagesFolder();
            }
        }

        /// <summary>The 5.6 location, used when the host does not answer. Kept separate so the
        /// fallback is one obvious literal rather than buried in a catch block.</summary>
        internal static string DefaultPackagesFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WinTAK", "Data Packages");
        }

        /// <summary>
        /// Registers an already-written package so it appears in WinTAK's Data Packages list.
        /// </summary>
        /// <param name="service">The host service. Null is tolerated — the import that supplies it
        /// allows default, so the package is still written and still sends, it just is not
        /// listed.</param>
        /// <param name="packagePath">The zip on disk. Must still exist when this is called.</param>
        /// <param name="packageName">Operator-visible name, matching the manifest.</param>
        /// <param name="packageUid">The manifest's package UID, so the host's record and the
        /// manifest agree on identity.</param>
        /// <param name="callsign">Who created it — this machine's callsign.</param>
        /// <returns>True when the host accepted it.</returns>
        public static bool Register(IMissionPackageService service, string packagePath,
            string packageName, string packageUid, string callsign)
        {
            if (service == null)
            {
                Log.Warn("Not listing the data package: WinTAK did not export a mission package "
                         + "service. The package was still written and can be opened from disk.");
                return false;
            }

            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
            {
                Log.Warn("Not listing the data package: " + (packagePath ?? "(no path)")
                         + " does not exist.");
                return false;
            }

            try
            {
                // The host stores the hash alongside the record and transfers quote it, so it is
                // computed from the finished file rather than left null.
                string sha256 = Sha256Of(packagePath);

                var package = new MissionPackage(packagePath, callsign ?? string.Empty, sha256)
                {
                    Name = packageName,
                    Uid = packageUid,
                };

                service.AddPackage(package);
                Log.Info($"Listed data package \"{packageName}\" (uid {packageUid}) in WinTAK.");
                return true;
            }
            catch (Exception ex)
            {
                // Listing is an enhancement on top of a package that already exists and already
                // sent. Failing here must not turn a successful send into a reported failure.
                Log.Error($"Could not list the data package \"{packageName}\" in WinTAK.", ex);
                return false;
            }
        }

        /// <summary>Lower-case hex SHA-256 of a file.</summary>
        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] digest = sha.ComputeHash(stream);
                var sb = new StringBuilder(64);
                foreach (byte b in digest) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
