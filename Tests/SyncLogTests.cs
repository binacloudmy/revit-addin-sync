using System;
using System.Collections.Generic;
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

        private static string LogFileIn(string dir) =>
            Path.Combine(dir, RevitWebAppSync.Services.SyncLog.FileName);

        private static string BackupFileIn(string dir) =>
            Path.Combine(dir, RevitWebAppSync.Services.SyncLog.BackupFileName);

        // --- cap / rotation -------------------------------------------------
        // Writes target an explicit directory (SyncLog.WriteTo), so these tests
        // never mutate shared static state and can run in any order/parallel.

        [Fact]
        public void Rotation_keeps_one_backup_once_the_cap_is_exceeded()
        {
            var dir = NewTempDir();
            try
            {
                RevitWebAppSync.Services.SyncLog.WriteTo(
                    dir, new string('x', (int)RevitWebAppSync.Services.SyncLog.MaxBytes + 64));
                RevitWebAppSync.Services.SyncLog.WriteTo(dir, "after rotation");

                Assert.True(File.Exists(LogFileIn(dir)), "current log missing");
                Assert.True(File.Exists(BackupFileIn(dir)), "rotated backup missing");
                Assert.True(
                    new FileInfo(LogFileIn(dir)).Length < RevitWebAppSync.Services.SyncLog.MaxBytes,
                    "current log should start fresh after rotation");
                Assert.True(
                    new FileInfo(BackupFileIn(dir)).Length >= RevitWebAppSync.Services.SyncLog.MaxBytes,
                    "backup should hold the oversized file");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Repeated_rotation_keeps_only_one_backup_and_replaces_it()
        {
            var dir = NewTempDir();
            try
            {
                var oversize = (int)RevitWebAppSync.Services.SyncLog.MaxBytes + 64;
                RevitWebAppSync.Services.SyncLog.WriteTo(dir, new string('A', oversize)); // over cap
                RevitWebAppSync.Services.SyncLog.WriteTo(dir, "roll-1");                  // rolls A -> backup
                RevitWebAppSync.Services.SyncLog.WriteTo(dir, new string('B', oversize)); // over cap again
                RevitWebAppSync.Services.SyncLog.WriteTo(dir, "roll-2");                  // replaces backup

                var files = Directory.GetFiles(dir)
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(new[] { "sync.1.log", "sync.log" }, files);

                var backup = File.ReadAllText(BackupFileIn(dir));
                Assert.Contains("B", backup);        // newest rolled file
                Assert.DoesNotContain("A", backup);  // oldest rolled file is gone
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Default_path_is_under_LocalAppData_Bina_RevitSync_logs()
        {
            var expectedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bina", "RevitSync", "logs");

            Assert.Equal(expectedDir, RevitWebAppSync.Services.SyncLog.LogDirectory);
            Assert.Equal(Path.Combine(expectedDir, "sync.log"), RevitWebAppSync.Services.SyncLog.LogPath);
            Assert.Equal(Path.Combine(expectedDir, "sync.1.log"), RevitWebAppSync.Services.SyncLog.BackupPath);
        }

        [Fact]
        public void Write_never_throws_when_the_log_path_cannot_be_created()
        {
            // A FILE where the log DIRECTORY should be makes Directory.CreateDirectory throw.
            var file = Path.Combine(Path.GetTempPath(), "bina-synclog-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "not a directory");
            try
            {
                var ex = Record.Exception(() => RevitWebAppSync.Services.SyncLog.WriteTo(file, "boom"));
                Assert.Null(ex);
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        // --- legacy Desktop log cleanup (review: old files still hold tokens) --

        [Fact]
        public void Legacy_cleanup_removes_the_two_known_desktop_logs_only()
        {
            var dir = NewTempDir();
            Directory.CreateDirectory(dir);
            var autodesk = Path.Combine(dir, "autodesk_upload_log.txt");
            var bina = Path.Combine(dir, "bina_upload_log.txt");
            var keep = Path.Combine(dir, "important.rvt");
            File.WriteAllText(autodesk, "old token");
            File.WriteAllText(bina, "old token");
            File.WriteAllText(keep, "keep me");
            try
            {
                RevitWebAppSync.Services.LegacySyncLogCleanup.Purge(new[] { dir });

                Assert.False(File.Exists(autodesk), "autodesk legacy log should be deleted");
                Assert.False(File.Exists(bina), "bina legacy log should be deleted");
                Assert.True(File.Exists(keep), "unrelated Desktop files must be left alone");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // --- sensitive-field / Desktop source scan ---------------------------

        [Fact]
        public void Every_logging_service_logs_only_permitted_fields()
        {
            var root = RepoRoot();
            var skipDirs = new[] { "bin", "obj", ".git", "Tests", "UiHarness" };

            var loggingFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                    .Any(part => skipDirs.Contains(part)))
                .Where(f =>
                {
                    var t = File.ReadAllText(f);
                    return t.Contains("LogToFile(")
                        || t.Contains("SyncLog.Write(")
                        || t.Contains("SyncLog.WriteTo(");
                })
                .Select(f => Path.GetRelativePath(root, f))
                .ToList();

            // Discovery guard: a new logging service is picked up automatically,
            // and these two must always be covered.
            Assert.Contains("AutodeskApiService.cs", loggingFiles);
            Assert.Contains("BinaApiService.cs", loggingFiles);

            // Fields whose presence inside a log call is off-limits. NOTE: prose
            // inside operation names ("…: token received", "oss signed-url: …")
            // is fine — only the *values* are forbidden, so this scans the call
            // argument span (comments/other code are never inside it).
            var forbidden = new[]
            {
                "specialfolder.desktop",
                "desktop",
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

            foreach (var rel in loggingFiles)
            {
                var text = File.ReadAllText(Path.Combine(root, rel));
                foreach (var span in LogCallSpans(text))
                {
                    // The only body-derived value we keep is its byte count.
                    var scrub = span.Replace("responseBody.Length", "").ToLowerInvariant();

                    foreach (var bad in forbidden)
                        Assert.True(
                            scrub.IndexOf(bad, StringComparison.Ordinal) < 0,
                            $"{rel}: log call should not contain '{bad}': {span.Trim()}");
                }
            }
        }

        /// <summary>
        /// Extracts the argument span of every <c>LogToFile(...)</c> /
        /// <c>SyncLog.Write(...)</c> / <c>SyncLog.WriteTo(...)</c> call, newlines
        /// included, by balanced parenthesis matching — so multi-line calls are
        /// covered and comments outside the call can never cause a false
        /// positive. Parentheses inside string literals are ignored, so an
        /// unbalanced "(" in a log message cannot cut the span short.
        /// (Interpolated-string braces are treated as string content; their
        /// expressions are balanced in the calls this guards.)
        /// </summary>
        private static IEnumerable<string> LogCallSpans(string text)
        {
            foreach (var marker in new[] { "LogToFile(", "SyncLog.Write(", "SyncLog.WriteTo(" })
            {
                int idx = 0;
                while (idx < text.Length &&
                       (idx = text.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
                {
                    int open = idx + marker.Length - 1; // the '('
                    int depth = 0;
                    int i = open;
                    bool inString = false, verbatim = false;

                    for (; i < text.Length; i++)
                    {
                        char ch = text[i];

                        if (inString)
                        {
                            if (verbatim)
                            {
                                // "" is an escaped quote in a verbatim string.
                                if (ch == '"')
                                {
                                    if (i + 1 < text.Length && text[i + 1] == '"') i++;
                                    else inString = false;
                                }
                            }
                            else
                            {
                                if (ch == '\\') i++;          // skip the escaped char
                                else if (ch == '"') inString = false;
                            }
                            continue;
                        }

                        if (ch == '@' && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            inString = true;
                            verbatim = true;
                            i++;
                            continue;
                        }
                        if (ch == '"')
                        {
                            inString = true;
                            verbatim = false;
                            continue;
                        }

                        if (ch == '(') depth++;
                        else if (ch == ')')
                        {
                            depth--;
                            if (depth == 0) { i++; break; }
                        }
                    }
                    yield return text.Substring(idx, i - idx);
                    idx = i;
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
