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

            if (!string.IsNullOrEmpty(outPath)) {
                if (InsideGitWorkTree(outPath)) {
                    Console.Error.WriteLine($"refusing to write the census inside a git working tree: {outPath}");
                    return 2;
                }

                // A census of a big drawing takes a while: say now, not after it, that the
                // file cannot be written.
                if (!CanWrite(outPath, out string? why)) {
                    Console.Error.WriteLine($"cannot write the census to {outPath}: {why}");
                    return 2;
                }
            }

            var clock = Stopwatch.StartNew();
            ElectricalCensusResult census = ElectricalCensus.Read(path);
            clock.Stop();

            string json = JsonSerializer.Serialize(census.ToWire(), new JsonSerializerOptions {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // The summary is printed whatever happens to the file.
            string? writeError = null;
            if (!string.IsNullOrEmpty(outPath)) {
                try {
                    File.WriteAllText(outPath, json);
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                             ex is System.Security.SecurityException) {
                    writeError = ex.Message;
                }
            }

            if (jsonOnly) {
                Console.WriteLine(json);
            } else {
                Console.Write(ElectricalCensusSummary.Format(census, Path.GetFileName(path)));
            }

            Console.Error.WriteLine("timings  " + string.Join("  ", census.TimingsSeconds
                .Select(t => $"{t.Key} {t.Value:0.00}s")) + $"  total {clock.Elapsed.TotalSeconds:0.00}s");
            if (writeError != null) {
                Console.Error.WriteLine($"census json NOT written to {outPath}: {writeError}");
                return 3;
            }
            if (!string.IsNullOrEmpty(outPath)) Console.Error.WriteLine($"census json written to {outPath}");

            return 0;
        }

        /// <summary>The folder exists, the path is not a folder, and a file can be created
        /// there: checked by creating and deleting a probe beside the target.</summary>
        private static bool CanWrite(string file, out string? why) {
            why = null;
            try {
                string full = Path.GetFullPath(file);
                if (Directory.Exists(full)) { why = "it is a folder"; return false; }

                string? dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { why = $"no such folder: {dir}"; return false; }

                string probe = Path.Combine(dir, ".census-probe-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                         ex is System.Security.SecurityException || ex is ArgumentException ||
                                         ex is NotSupportedException) {
                why = ex.Message;
                return false;
            }
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
