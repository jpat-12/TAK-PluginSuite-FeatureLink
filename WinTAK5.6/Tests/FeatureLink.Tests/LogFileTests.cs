using System;
using System.IO;
using System.Text;
using FeatureLink.Services;
using Xunit;

namespace FeatureLink.Tests
{
    /// <summary>
    /// <see cref="Log"/>'s configuration is process-wide static state, and these tests switch file
    /// logging on and repoint the log directory. xUnit runs test classes in parallel by default,
    /// so without this any other class that logged — the ArcGIS client logs on every failure path
    /// — would write into this class's temp directory mid-assertion and make the file-count and
    /// file-size checks flaky. Disabling parallelisation for this one collection is cheaper and
    /// more honest than making the assertions tolerant of foreign lines.
    /// </summary>
    [CollectionDefinition(LogFileCollection.Name, DisableParallelization = true)]
    public sealed class LogFileCollection
    {
        public const string Name = "Log file sink";
    }

    /// <summary>
    /// Covers the rolling file sink in <see cref="Log"/>.
    ///
    /// The sink was rewritten from a per-line <c>File.AppendAllText</c> (open, write, close, plus
    /// a <c>FileInfo</c> stat, for every line, under a global lock) to a held-open
    /// <see cref="StreamWriter"/> with a counter-tracked roll threshold. That change trades a
    /// trivially-correct implementation for a much faster one that now owns a file handle across
    /// a rename — so the roll path, the reopen-after-roll path and handle release are exactly
    /// what needs asserting.
    ///
    /// These tests redirect <see cref="Log.LogDirectory"/> into a temp folder. The whole suite
    /// otherwise runs with <see cref="Log.FileLoggingEnabled"/> false (see
    /// <see cref="TestBootstrap"/>), so each test re-enables it and restores the global state
    /// afterwards — xUnit gives one instance per test, but these are process-wide statics.
    /// </summary>
    [Collection(LogFileCollection.Name)]
    public sealed class LogFileTests : IDisposable
    {
        private readonly string _dir;

        public LogFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "featurelink-logtests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Log.LogDirectory = _dir;
            Log.FileLoggingEnabled = true;
        }

        public void Dispose()
        {
            Log.Shutdown();
            Log.FileLoggingEnabled = false;
            Log.LogDirectory = null;
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; leave it */ }
        }

        private string LogPath => Path.Combine(_dir, "featurelink.log");
        private string RolledPath => Path.Combine(_dir, "featurelink.log.1");

        /// <summary>The whole point of AutoFlush: a crash log whose tail is still buffered is
        /// worthless, so a line must be on disk before the next call returns.</summary>
        [Fact]
        public void A_written_line_is_readable_immediately_without_shutdown()
        {
            Log.Info("first line");

            Assert.True(File.Exists(LogPath));
            Assert.Contains("first line", ReadShared(LogPath), StringComparison.Ordinal);
        }

        [Fact]
        public void Writes_append_rather_than_truncate_across_reopen()
        {
            Log.Info("before shutdown");
            Log.Shutdown();
            Log.Info("after shutdown");

            string text = ReadShared(LogPath);
            Assert.Contains("before shutdown", text, StringComparison.Ordinal);
            Assert.Contains("after shutdown", text, StringComparison.Ordinal);
        }

        /// <summary>Exceeding the 2 MB cap must move the current file aside and start a fresh one,
        /// with the handle surviving the rename.</summary>
        [Fact]
        public void Exceeding_the_cap_rolls_to_dot_one_and_keeps_writing()
        {
            var filler = new string('x', 1024);
            for (int i = 0; i < 2200; i++) Log.Info(filler);   // > 2 MB

            Assert.True(File.Exists(RolledPath), "the previous log should have been rolled to .1");

            Log.Info("written after the roll");
            Assert.Contains("written after the roll", ReadShared(LogPath), StringComparison.Ordinal);

            // The fresh file must be a fresh file, not the old one still growing.
            Assert.True(new FileInfo(LogPath).Length < new FileInfo(RolledPath).Length);
        }

        /// <summary>A second roll must overwrite the previous .1 rather than failing on an
        /// existing file — two rolling files is the whole retention policy.</summary>
        [Fact]
        public void A_second_roll_replaces_the_previous_rolled_file()
        {
            var filler = new string('x', 1024);
            for (int i = 0; i < 2200; i++) Log.Info(filler);
            Assert.True(File.Exists(RolledPath));

            Log.Info("marker-in-second-generation");
            for (int i = 0; i < 2200; i++) Log.Info(filler);

            // The .1 file is now the generation that carried the marker.
            Assert.Contains("marker-in-second-generation", ReadShared(RolledPath), StringComparison.Ordinal);
            Assert.Equal(2, Directory.GetFiles(_dir, "featurelink.log*").Length);
        }

        /// <summary>C-21 again, but at the sink rather than at the formatter: a credential must
        /// not reach the file even when the caller passes one straight in.</summary>
        [Fact]
        public void Secrets_are_redacted_on_the_way_to_disk()
        {
            Log.Info("GET https://example.com/rest?token=SECRETVALUE&f=json");
            Log.Error("auth header was Bearer ANOTHERSECRET");

            string text = ReadShared(LogPath);
            Assert.DoesNotContain("SECRETVALUE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ANOTHERSECRET", text, StringComparison.Ordinal);
            Assert.Contains("<redacted>", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Shutdown_releases_the_handle_so_the_file_can_be_replaced()
        {
            Log.Info("held open");
            Log.Shutdown();

            // Would throw IOException if the writer were still holding the file.
            File.Delete(LogPath);
            Assert.False(File.Exists(LogPath));
        }

        [Fact]
        public void Disabling_file_logging_stops_writing_without_throwing()
        {
            Log.Info("written while enabled");
            Log.Shutdown();
            Log.FileLoggingEnabled = false;

            Log.Info("must not be written");

            Assert.DoesNotContain("must not be written", ReadShared(LogPath), StringComparison.Ordinal);
        }

        /// <summary>An unwritable directory must degrade to TraceSource-only, not throw out of a
        /// diagnostic call into whatever operation happened to be logging.</summary>
        [Fact]
        public void An_unwritable_directory_does_not_throw_out_of_the_logging_call()
        {
            Log.Shutdown();
            Log.LogDirectory = "\0:\\definitely\\not\\a\\valid\\path";

            var ex = Record.Exception(() => Log.Error("this must not propagate", new InvalidOperationException("inner")));

            Assert.Null(ex);
        }

        /// <summary>The sink holds the file open, so the assertions must read it the same way a
        /// support engineer tailing the log would — shared, without taking it away.</summary>
        private static string ReadShared(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
