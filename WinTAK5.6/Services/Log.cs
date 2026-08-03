using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FeatureLink.Services
{
    /// <summary>
    /// Minimal diagnostic sink for the plugin.
    ///
    /// The audit measured <b>zero</b> logging calls anywhere in either WinTAK tree: with
    /// <c>StatusText</c> also unbound (C-16) a failed sync produced no artifact anywhere at all,
    /// making fielded failures undiagnosable. This routes everything through a
    /// <see cref="TraceSource"/> (so a host/operator can attach any standard .NET listener) and,
    /// by default, also appends to a rolling file under
    /// <c>%AppData%\WinTAK\FeatureLink\featurelink.log</c>.
    ///
    /// Every message passes through <see cref="Redact"/> first — C-21 requires that no
    /// <c>token=</c> value ever reaches a log line, and ArcGIS URLs historically carried one.
    /// </summary>
    public static class Log
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;

        private static readonly TraceSource Source = new TraceSource("FeatureLink", SourceLevels.Information);
        private static readonly object FileLock = new object();
        private static string _logPath;
        private static bool _fileLoggingFailed;

        /// <summary>Set to false in tests (or by an operator preference) to keep everything in
        /// the TraceSource only.</summary>
        public static bool FileLoggingEnabled { get; set; } = true;

        public static void Info(string message) => Write(TraceEventType.Information, message, null);

        public static void Warn(string message) => Write(TraceEventType.Warning, message, null);

        public static void Error(string message, Exception ex = null) => Write(TraceEventType.Error, message, ex);

        private static void Write(TraceEventType level, string message, Exception ex)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-ddTHH:mm:ss.fffZ} [{1}] {2}{3}",
                DateTime.UtcNow, level, Redact(message),
                ex == null ? string.Empty : Environment.NewLine + Redact(ex.ToString()));

            try { Source.TraceEvent(level, 0, line); } catch { /* a listener threw — never let logging break the caller */ }
            AppendToFile(line);
        }

        /// <summary>Strips the value of any <c>token</c>/<c>code</c>/<c>refresh_token</c>/
        /// <c>code_verifier</c> query or form parameter, and any <c>Bearer &lt;value&gt;</c>, so a
        /// credential can never be persisted to disk by a diagnostic call.</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = SecretParam.Replace(text, "$1=<redacted>");
            text = BearerValue.Replace(text, "Bearer <redacted>");
            return text;
        }

        private static readonly Regex SecretParam = new Regex(
            @"\b(token|access_token|refresh_token|code|code_verifier|client_secret)=[^&\s""']*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BearerValue = new Regex(
            @"Bearer\s+[A-Za-z0-9\-\._~\+/=]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void AppendToFile(string line)
        {
            if (!FileLoggingEnabled || _fileLoggingFailed) return;
            try
            {
                lock (FileLock)
                {
                    if (_logPath == null)
                    {
                        string dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "WinTAK", "FeatureLink");
                        Directory.CreateDirectory(dir);
                        _logPath = Path.Combine(dir, "featurelink.log");
                    }

                    var info = new FileInfo(_logPath);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        string rolled = _logPath + ".1";
                        try { if (File.Exists(rolled)) File.Delete(rolled); } catch { }
                        try { File.Move(_logPath, rolled); } catch { }
                    }

                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Read-only profile, AV lock, roaming-profile hiccup — stop trying rather than
                // throwing out of a logging call on every subsequent operation.
                _fileLoggingFailed = true;
            }
        }
    }
}
