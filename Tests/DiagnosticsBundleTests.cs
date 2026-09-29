using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>F5 "Send diagnostics": what the zip carries and its 2 MB ceiling.</summary>
    public class DiagnosticsBundleTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bina-diag-" + Guid.NewGuid().ToString("N"));

        public DiagnosticsBundleTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static ZipArchive Open(byte[] zip) => new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);

        private static string Read(ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name);
            Assert.True(e != null, "missing entry " + name);
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }

        [Fact]
        public void Carries_the_logs_state_files_and_a_versions_listing()
        {
            File.WriteAllText(Path.Combine(_root, "loader.log"), "loader line");
            File.WriteAllText(Path.Combine(_root, "updater.log"), "updater line");
            File.WriteAllText(Path.Combine(_root, "update-gate.json"), "{\"version\":\"0.0.80\"}");
            File.WriteAllText(Path.Combine(_root, "reinstall.json"), "{\"attempts\":1}");
            var v = Path.Combine(_root, "versions", "0.0.80");
            Directory.CreateDirectory(Path.Combine(v, "net8.0"));
            File.WriteAllText(Path.Combine(v, ".complete"), "0.0.80");
            File.WriteAllText(Path.Combine(v, ".bad"), "crashed during 2 launches");
            File.WriteAllText(Path.Combine(v, "net8.0", "RevitWebAppSync.dll"), "MZ");

            using var zip = Open(DiagnosticsBundle.Build(_root, null));

            Assert.Equal("loader line", Read(zip, "loader.log"));
            Assert.Equal("updater line", Read(zip, "updater.log"));
            Assert.Contains("0.0.80", Read(zip, "update-gate.json"));
            Assert.Contains("attempts", Read(zip, "reinstall.json"));
            var listing = Read(zip, "versions.txt");
            Assert.Contains("0.0.80", listing);
            Assert.Contains(".complete", listing);
            Assert.Contains(".bad: crashed during 2 launches", listing);
            Assert.DoesNotContain("RevitWebAppSync.dll", listing);
            Assert.DoesNotContain("net8.0", listing);
        }

        [Fact]
        public void Oversized_logs_are_cut_to_their_tail_and_the_zip_stays_under_2mb()
        {
            var rng = new Random(1);
            var sb = new StringBuilder();
            sb.AppendLine("HEAD-MARKER");
            // Random hex barely compresses, so the byte ceiling is really exercised.
            while (sb.Length < 5 * 1024 * 1024)
                sb.Append(rng.Next().ToString("x8"));
            sb.AppendLine();
            sb.Append("TAIL-MARKER");
            File.WriteAllText(Path.Combine(_root, "updater.log"), sb.ToString());
            File.WriteAllText(Path.Combine(_root, "loader.log"), sb.ToString());

            var bytes = DiagnosticsBundle.Build(_root, null);

            Assert.True(bytes.Length <= DiagnosticsBundle.MaxBytes, $"zip is {bytes.Length} bytes");
            using var zip = Open(bytes);
            var log = Read(zip, "updater.log");
            Assert.EndsWith("TAIL-MARKER", log);
            Assert.DoesNotContain("HEAD-MARKER", log);
        }

        [Fact]
        public void Extra_plugin_logs_are_included_by_file_name()
        {
            var extra = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(extra);
            var log = Path.Combine(extra, "copilot.log");
            File.WriteAllText(log, "copilot line");

            using var zip = Open(DiagnosticsBundle.Build(_root, new[] { log, Path.Combine(extra, "missing.log") }));

            Assert.Equal("copilot line", Read(zip, "logs/copilot.log"));
        }

        [Fact]
        public void A_missing_root_still_yields_a_valid_zip()
        {
            using var zip = Open(DiagnosticsBundle.Build(Path.Combine(_root, "nope"), null));
            Assert.Contains("no versions", Read(zip, "versions.txt"));
        }
    }
}
