using System;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Shared, best-effort, size-capped text log for the sync services
    /// (AutodeskApiService / BinaApiService). Replaces the old per-service
    /// writers that appended ten to forty lines per upload to a file on the
    /// user's Desktop — which OneDrive often syncs off the machine.
    ///
    /// AI-51: everything written here is assumed to be readable by anyone.
    /// Callers pass NON-SENSITIVE facts only: an operation/endpoint name plus
    /// HTTP status, elapsed milliseconds and byte count. Never a token, an
    /// Authorization header, a signed URL, an e-mail address, or a full
    /// request/response body.
    ///
    /// Lives next to the engine and CAD-to-BIM logs:
    /// %LOCALAPPDATA%\Bina\RevitSync\logs\sync.log. Capped at 1 MB; on
    /// overflow the previous file is kept once as sync.1.log.
    ///
    /// The cap is not a hard guarantee: it is enforced on the NEXT write, so a
    /// write that itself crosses 1 MB leaves the file slightly over until the
    /// following write rolls it. Rotation is best-effort — if the roll fails
    /// (e.g. an antivirus/OneDrive lock holds the file), the catch keeps
    /// appending and the file can exceed 1 MB. Both are accepted so that
    /// logging can never break a sync.
    ///
    /// Thread-safe. Never throws — logging must never break a sync.
    /// </summary>
    internal static class SyncLog
    {
        internal const long MaxBytes = 1L * 1024 * 1024; // 1 MB
        internal const string FileName = "sync.log";
        internal const string BackupFileName = "sync.1.log";

        private static readonly object _gate = new object();

        internal static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync", "logs");

        internal static string LogPath => Path.Combine(LogDirectory, FileName);
        internal static string BackupPath => Path.Combine(LogDirectory, BackupFileName);

        /// <summary>Writes to the production log directory.</summary>
        internal static void Write(string message) => WriteTo(LogDirectory, message);

        /// <summary>
        /// Writes to an explicit directory. This is the test seam: tests pass a
        /// temp directory instead of mutating shared static state, so parallel
        /// test classes can never race on a log destination.
        /// </summary>
        internal static void WriteTo(string directory, string message)
        {
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(directory);
                    RotateIfNeeded(directory);
                    File.AppendAllText(
                        Path.Combine(directory, FileName),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { /* logging must never break a sync */ }
        }

        private static void RotateIfNeeded(string directory)
        {
            try
            {
                var current = Path.Combine(directory, FileName);
                var info = new FileInfo(current);
                if (!info.Exists || info.Length < MaxBytes) return;

                var backup = Path.Combine(directory, BackupFileName);
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(current, backup);
            }
            catch { /* rotation is best-effort; keep appending if it fails */ }
        }
    }
}
