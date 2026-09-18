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
        private static StreamWriter _writer;
        private static long _bytesWritten;

        /// <summary>Set to false in tests (or by an operator preference) to keep everything in
        /// the TraceSource only.</summary>
        public static bool FileLoggingEnabled { get; set; } = true;

        /// <summary>Directory the rolling log is written to. Null means the default,
        /// <c>%AppData%\WinTAK\FeatureLink</c>.
        ///
        /// <para>Settable for two reasons: an operator on a locked-down or roaming profile may
        /// need the log somewhere else, and the roll behaviour is otherwise untestable — it is
        /// the part of this class most likely to break, and it handles a file handle across a
        /// rename. Assigning closes the current writer, so the next line opens under the new
        /// path.</para></summary>
        public static string LogDirectory
        {
            get => _logDirectory;
            set
            {
                lock (FileLock)
                {
                    _logDirectory = value;
                    _logPath = null;
                    _fileLoggingFailed = false;
                    CloseWriterQuietly();
                }
            }
        }

        private static string _logDirectory;

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

        /// <summary>
        /// Appends one line to the rolling log file.
        ///
        /// <para>This used to be <c>File.AppendAllText</c> — an open, write, flush and close for
        /// every single line — preceded by a <c>FileInfo</c> stat to check the roll threshold, all
        /// under a process-wide lock. That is fine at the rate a UI produces messages, and badly
        /// wrong on the download path, where a systematic per-feature warning turns a
        /// 50,000-feature sync into 50,000 handle open/close cycles and 50,000 directory stats on
        /// the thread doing the work. The scenario where diagnostics matter most — a large layer
        /// misbehaving — was exactly the scenario where the log sink became the bottleneck.</para>
        ///
        /// <para>The writer is now held open with <see cref="StreamWriter.AutoFlush"/> on, so a
        /// line still reaches disk immediately (a crash log is worthless if the tail is buffered
        /// away) while the handle and the path resolution are paid once. The roll threshold is
        /// tracked with a counter rather than re-stated per line.</para>
        /// </summary>
        private static void AppendToFile(string line)
        {
            if (!FileLoggingEnabled || _fileLoggingFailed) return;
            try
            {
                lock (FileLock)
                {
                    if (_writer == null && !OpenWriter()) return;

                    _writer.WriteLine(line);

                    // +2 covers the newline; exactness does not matter for a roll threshold.
                    _bytesWritten += line.Length + 2;
                    if (_bytesWritten > MaxLogBytes) Roll();
                }
            }
            catch
            {
                // Read-only profile, AV lock, roaming-profile hiccup — stop trying rather than
                // throwing out of a logging call on every subsequent operation.
                _fileLoggingFailed = true;
                CloseWriterQuietly();
            }
        }

        /// <summary>Opens the log for append. Returns false (and disables file logging) when the
        /// location is not writable at all.</summary>
        private static bool OpenWriter()
        {
            try
            {
                if (_logPath == null)
                {
                    string dir = _logDirectory ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WinTAK", "FeatureLink");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, "featurelink.log");
                }

                var info = new FileInfo(_logPath);
                _bytesWritten = info.Exists ? info.Length : 0;

                _writer = new StreamWriter(
                    new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
                return true;
            }
            catch
            {
                _fileLoggingFailed = true;
                return false;
            }
        }

        /// <summary>Rotates featurelink.log to featurelink.log.1 and starts a fresh file. Called
        /// with <see cref="FileLock"/> held.</summary>
        private static void Roll()
        {
            CloseWriterQuietly();
            try
            {
                string rolled = _logPath + ".1";
                if (File.Exists(rolled)) File.Delete(rolled);
                File.Move(_logPath, rolled);
            }
            catch { /* another process holds it — keep appending to the current file instead */ }

            _bytesWritten = 0;
            OpenWriter();
        }

        private static void CloseWriterQuietly()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }

        /// <summary>Releases the log handle. Call from the plugin's dispose path so the file is
        /// not held open by a pane that has gone away.</summary>
        public static void Shutdown()
        {
            lock (FileLock) CloseWriterQuietly();
        }
    }
}
