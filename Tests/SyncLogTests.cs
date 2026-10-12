using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>
    /// AI-51: the sync services must log to
    /// %LOCALAPPDATA%\Bina\RevitSync\logs\ (capped, rotated) — never to the
    /// user's Desktop, and never with tokens, token prefixes, Authorization
    /// headers, signed/presigned URLs, e-mails, URNs or request/response
    /// bodies. Log lines keep only endpoint/operation name, HTTP status,
    /// elapsed ms and byte count.
    /// </summary>
    public class SyncLogTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string NewTempDir() =>
            Path.Combine(Path.GetTempPath(), "bina-synclog-test-" + Guid.NewGuid().ToString("N"));

        // --- cap / rotation -------------------------------------------------

        [Fact]
        public void Rotation_keeps_one_backup_once_the_cap_is_exceeded()
        {
            var dir = NewTempDir();
            var previous = RevitWebAppSync.Services.SyncLog.DirectoryOverride;
            try
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = dir;

                // One write larger than the cap, then a small one to trigger the roll.
                RevitWebAppSync.Services.SyncLog.Write(
                    new string('x', (int)RevitWebAppSync.Services.SyncLog.MaxBytes + 64));
                RevitWebAppSync.Services.SyncLog.Write("after rotation");

                Assert.True(File.Exists(RevitWebAppSync.Services.SyncLog.LogPath), "current log missing");
                Assert.True(File.Exists(RevitWebAppSync.Services.SyncLog.BackupPath), "rotated backup missing");
                Assert.True(
                    new FileInfo(RevitWebAppSync.Services.SyncLog.LogPath).Length
                        < RevitWebAppSync.Services.SyncLog.MaxBytes,
                    "current log should start fresh after rotation");
                Assert.True(
                    new FileInfo(RevitWebAppSync.Services.SyncLog.BackupPath).Length
                        >= RevitWebAppSync.Services.SyncLog.MaxBytes,
                    "backup should hold the oversized file");
            }
            finally
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Repeated_rotation_keeps_only_one_backup_and_replaces_it()
        {
            var dir = NewTempDir();
            var previous = RevitWebAppSync.Services.SyncLog.DirectoryOverride;
            try
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = dir;
                var oversize = (int)RevitWebAppSync.Services.SyncLog.MaxBytes + 64;

                RevitWebAppSync.Services.SyncLog.Write(new string('A', oversize)); // over cap
                RevitWebAppSync.Services.SyncLog.Write("roll-1");                  // rolls A -> backup
                RevitWebAppSync.Services.SyncLog.Write(new string('B', oversize)); // over cap again
                RevitWebAppSync.Services.SyncLog.Write("roll-2");                  // replaces backup

                var files = Directory.GetFiles(dir)
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(new[] { "sync.1.log", "sync.log" }, files);

                var backup = File.ReadAllText(RevitWebAppSync.Services.SyncLog.BackupPath);
                Assert.Contains("B", backup);        // newest rolled file
                Assert.DoesNotContain("A", backup);  // oldest rolled file is gone
            }
            finally
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Default_path_is_under_LocalAppData_Bina_RevitSync_logs()
        {
            var previous = RevitWebAppSync.Services.SyncLog.DirectoryOverride;
            try
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = null;

                var expectedDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bina", "RevitSync", "logs");

                Assert.Equal(expectedDir, RevitWebAppSync.Services.SyncLog.LogDirectory);
                Assert.Equal(Path.Combine(expectedDir, "sync.log"), RevitWebAppSync.Services.SyncLog.LogPath);
                Assert.Equal(Path.Combine(expectedDir, "sync.1.log"), RevitWebAppSync.Services.SyncLog.BackupPath);
            }
            finally
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = previous;
            }
        }

        [Fact]
        public void Write_never_throws_when_the_log_path_cannot_be_created()
        {
            // A FILE where the log DIRECTORY should be makes Directory.CreateDirectory throw.
            var file = Path.Combine(Path.GetTempPath(), "bina-synclog-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "not a directory");
            var previous = RevitWebAppSync.Services.SyncLog.DirectoryOverride;
            try
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = file;
                var ex = Record.Exception(() => RevitWebAppSync.Services.SyncLog.Write("boom"));
                Assert.Null(ex);
            }
            finally
            {
                RevitWebAppSync.Services.SyncLog.DirectoryOverride = previous;
                try { File.Delete(file); } catch { }
            }
        }

        // --- sensitive-field / Desktop source scan ---------------------------

        [Fact]
        public void Sync_services_log_only_permitted_fields()
        {
            var root = RepoRoot();

            // Fields whose mere presence in a log line is off-limits: token
            // values/prefixes, headers, URLs, bodies, e-mails, URNs, paths, and
            // the ticket's disallowed extras (project id, version, MIME type).
            // NOTE: the words inside operation names ("…: token received",
            // "oss signed-url: …") are fine; only the *values* are forbidden,
            // which is why this scans the variable/URL tokens rather than the
            // prose.
            var forbidden = new[]
            {
                "specialfolder.desktop",
                "autodesk_upload_log",
                "bina_upload_log",
                "substring",
                "accesstoken",
                "bearer",
                "authorization",
                "responsebody",
                "jsoncontent",
                "request body",
                "response body",
                "http://",
                "https://",
                "x-amz",
                "sig=",
                "signature=",
                "expires=",
                "urninbase64",
                "urn:",
                "email",
                "@",
                "_email",
                "signedurl=",
                "uploadurl=",
                "filepath",
                "temppath",
                "password",
                "project",
                "version",
                "mimetype",
                "type=",
            };

            foreach (var name in new[] { "AutodeskApiService.cs", "BinaApiService.cs" })
            {
                var text = File.ReadAllText(Path.Combine(root, name));

                // No Desktop destination anywhere in a logging service.
                Assert.DoesNotContain("SpecialFolder.Desktop", text);
                Assert.Contains("SyncLog.Write", text);

                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (!line.Contains("LogToFile(") && !line.Contains("SyncLog.Write(")) continue;

                    // The only body-derived value we keep is its byte count.
                    var scrub = line
                        .Replace("responseBody.Length", "")
                        .ToLowerInvariant();

                    foreach (var bad in forbidden)
                        Assert.True(
                            scrub.IndexOf(bad, StringComparison.Ordinal) < 0,
                            $"{name}: log line should not contain '{bad}': {line}");
                }
            }
        }

        [Fact]
        public void No_user_message_points_at_a_desktop_log()
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "BimDisciplineCommand.cs"));
            Assert.DoesNotContain("log file on Desktop", text);
        }
    }
}
