using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

using BinaVibe.Mcp.Tools.Electrical;

namespace Cad2Bim.Headless {
    /// <summary>
    /// `--census`: the electrical census of one drawing (phase 0 of the CAD electrical to BIM
    /// work). The summary goes to stdout and the timings to stderr, so the summary can be
    /// saved on its own; `--json` prints the census JSON instead, and `--census-out=` writes it
    /// to a file as well.
    ///
    /// A census of a real drawing carries its project names and texts, so the JSON file is
    /// refused anywhere inside a git working tree: it must never end up committed.
    /// </summary>
    internal static class CensusCommand {
        internal static int Run(string path, string[] args) {
            bool jsonOnly = args.Contains("--json");
            string? outPath = args.FirstOrDefault(a => a.StartsWith("--census-out=", StringComparison.Ordinal))
                ?.Substring("--census-out=".Length).Trim('"');

            if (!string.IsNullOrEmpty(outPath) && InsideGitWorkTree(outPath)) {
                Console.Error.WriteLine($"refusing to write the census inside a git working tree: {outPath}");
                return 2;
            }

            var clock = Stopwatch.StartNew();
            ElectricalCensusResult census = ElectricalCensus.Read(path);
            clock.Stop();

            string json = JsonSerializer.Serialize(census.ToWire(), new JsonSerializerOptions {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            if (!string.IsNullOrEmpty(outPath)) {
                File.WriteAllText(outPath, json);
            }

            if (jsonOnly) {
                Console.WriteLine(json);
            } else {
                Console.Write(ElectricalCensusSummary.Format(census, Path.GetFileName(path)));
            }

            Console.Error.WriteLine("timings  " + string.Join("  ", census.TimingsSeconds
                .Select(t => $"{t.Key} {t.Value:0.00}s")) + $"  total {clock.Elapsed.TotalSeconds:0.00}s");
            if (!string.IsNullOrEmpty(outPath)) Console.Error.WriteLine($"census json written to {outPath}");

            return 0;
        }

        private static bool InsideGitWorkTree(string file) {
            DirectoryInfo? dir = new FileInfo(Path.GetFullPath(file)).Directory;
            while (dir != null) {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return true;
                dir = dir.Parent;
            }
            return false;
        }
    }
}
