#nullable enable
// W5 fix round 1 (spec §13): "a test ensures no validation override exists".
// The add-in never pins certificates (Cloudflare rotates, government networks
// inspect TLS), so the only safe stance is the OS's own validation: no code
// may install a certificate-validation callback or accept-any validator.
// Scans every C# source in the repo (bin/obj excluded). Runs anywhere.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotTransportSecurityTests
    {
        // Assembled at runtime so this file never matches itself.
        private static readonly string[] Forbidden =
        {
            "ServerCertificate" + "CustomValidationCallback",
            "ServerCertificate" + "ValidationCallback",
            "RemoteCertificate" + "ValidationCallback",
            "DangerousAcceptAny" + "ServerCertificateValidator",
        };

        private static string RepoRoot([CallerFilePath] string thisFile = "")
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? "" })
                for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir != null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                        return dir.FullName;
            throw new InvalidOperationException("could not locate the repo root");
        }

        [Fact]
        public void NoTlsValidationOverride_AnywhereInTheSource()
        {
            var root = RepoRoot();
            var sep = Path.DirectorySeparatorChar;
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep))
                .ToList();
            Assert.True(files.Count > 100, "expected to scan the add-in sources, found " + files.Count);

            var hits = files
                .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, n: i + 1)))
                .Where(x => Forbidden.Any(p => x.line.IndexOf(p, StringComparison.Ordinal) >= 0))
                .Select(x => x.f.Substring(root.Length) + ":" + x.n + ": " + x.line.Trim())
                .ToList();

            Assert.True(hits.Count == 0, "certificate validation must not be overridden:\n" + string.Join("\n", hits));
        }
    }
}
