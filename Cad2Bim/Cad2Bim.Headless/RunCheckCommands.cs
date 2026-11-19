using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using BinaVibe.Mcp.Tools.Electrical;

namespace Cad2Bim.Headless {
    /// <summary>
    /// The phase 2 (conduit) checks without Revit.
    ///
    /// `file.dwg --runs --layers=E-CONDUIT[,E-TRUNK] [--cluster=N] [--points-out=runs.json]`
    /// chains each layer's pieces into runs (CadRunChain) and prints, per layer: pieces, runs,
    /// total length, open ends, junction ends, bends, free arcs and short runs dropped. No
    /// coordinates are printed; `--points-out` writes the runs' points (drawing mm) to a file,
    /// refused inside a git working tree. `--cluster` keeps the pieces in that census plan's box.
    ///
    /// `--compare-runs ref.json placed.json [--kind=conduit|cable_tray] [--tol=100] [--3d] [--json]`
    /// compares two dump_electrical_reference dumps made with include_runs (RunCompare): the share
    /// of the reference's length within the tolerance of a created segment, and the reverse.
    /// </summary>
    internal static class RunCheckCommands {
        internal static int Runs(string path, string[] args) {
            string? layersText = Value(args, "--layers=");
            if (string.IsNullOrWhiteSpace(layersText)) {
                Console.Error.WriteLine("usage: <file.dwg> --runs --layers=E-CONDUIT[,E-TRUNK] [--cluster=N] [--points-out=runs.json]");
                return 2;
            }
            string[] layers = layersText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
            string? clusterText = Value(args, "--cluster=");
            string? pointsOut = Value(args, "--points-out=");
            if (pointsOut != null && InsideGitWorkTree(Path.GetFullPath(pointsOut))) {
                Console.Error.WriteLine("refusing to write run points inside a git working tree: " + Path.GetFullPath(pointsOut));
                return 2;
            }

            ElectricalScan scan = ElectricalCensus.ScanFile(path);
            Func<double, double, bool> keep = (x, y) => true;
            if (clusterText != null) {
                int cluster = int.Parse(clusterText, CultureInfo.InvariantCulture);
                if (cluster < 0 || cluster >= scan.Clusters.Count) {
                    Console.Error.WriteLine($"cluster {cluster} is not a plan of this drawing (0 to {scan.Clusters.Count - 1})");
                    return 2;
                }
                keep = scan.InClusterBox(cluster);
            }

            var options = new ChainOptions();
            var dump = new List<object?>();
            Console.WriteLine($"join {options.JoinMm:0} mm, min run {options.MinRunMm:0} mm" +
                              (clusterText != null ? $", cluster {clusterText}" : ", whole sheet"));
            foreach (string layer in layers) {
                if (!scan.LayerNames.Contains(layer)) {
                    Console.WriteLine($"{layer}: not in the drawing");
                    continue;
                }
                List<Segment> pieces = scan.RunPieces(new[] { layer }, keep, out int left);
                List<ArcSpan> arcs = scan.RunArcs(new[] { layer }, keep, out _);
                ChainResult chain = CadRunChain.Chain(pieces, arcs, options);
                if (chain.Error != null) {
                    Console.WriteLine($"{layer}: {pieces.Count} pieces, {chain.Error}");
                    continue;
                }
                int open = chain.Runs.Count(r => !r.Closed && r.StartKind == "open") + chain.Runs.Count(r => !r.Closed && r.EndKind == "open");
                int junctionEnds = chain.Runs.Count(r => r.StartKind == "junction") + chain.Runs.Count(r => r.EndKind == "junction");
                double length = chain.Runs.Sum(r => r.LengthMm);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}: pieces {1} (+{2} outside), arcs {3}, runs {4}, length {5:0.0} m, open ends {6}, junctions {7} " +
                    "(junction ends {8}), bends {9}, free arcs {10}, short runs dropped {11}, closed loops {12}, short legs {13}",
                    layer, pieces.Count, left, arcs.Count, chain.Runs.Count, length / 1000, open, chain.Junctions, junctionEnds,
                    chain.Bends, chain.FreeArcs, chain.TooShort, chain.Runs.Count(r => r.Closed),
                    chain.Runs.Count(r => r.Flags.Contains("short_leg"))));

                foreach (ChainedRun run in chain.Runs) {
                    dump.Add(new Dictionary<string, object?> {
                        ["layer"] = layer,
                        ["start"] = run.StartKind,
                        ["end"] = run.EndKind,
                        ["closed"] = run.Closed,
                        ["length_mm"] = Math.Round(run.LengthMm, 1),
                        ["flags"] = run.Flags,
                        ["points_mm"] = run.Points.Select(p => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) }).ToList(),
                    });
                }
            }

            if (pointsOut != null) {
                File.WriteAllText(pointsOut, JsonSerializer.Serialize(new Dictionary<string, object?> {
                    ["schema"] = "cad.electrical.runs/1",
                    ["units"] = "drawing mm",
                    ["runs"] = dump,
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"run points written to {Path.GetFullPath(pointsOut)}");
            }
            return 0;
        }

        internal static int CompareRuns(string[] args) {
            int at = Array.IndexOf(args, "--compare-runs");
            if (at < 0 || at + 2 >= args.Length) {
                Console.Error.WriteLine("usage: --compare-runs <ref.json> <placed.json> [--kind=conduit|cable_tray] [--tol=100] [--3d] [--json]");
                return 2;
            }
            string? kind = Value(args, "--kind=");
            double tol = 100;
            string? tolText = Value(args, "--tol=");
            if (tolText != null &&
                (!double.TryParse(tolText, NumberStyles.Float, CultureInfo.InvariantCulture, out tol) || tol < 0)) {
                Console.Error.WriteLine($"--tol={tolText} is not a distance in mm");
                return 2;
            }

            using JsonDocument refDoc = JsonDocument.Parse(File.ReadAllText(args[at + 1]));
            using JsonDocument placedDoc = JsonDocument.Parse(File.ReadAllText(args[at + 2]));
            List<RunSegment> reference = RunCompare.Read(refDoc.RootElement, kind);
            List<RunSegment> placed = RunCompare.Read(placedDoc.RootElement, kind);
            if (reference.Count == 0) {
                Console.Error.WriteLine($"{args[at + 1]} has no runs: dump it with include_runs=true");
                return 2;
            }

            RunCompareResult result = RunCompare.Compare(reference, placed, tol, args.Contains("--3d"));
            if (args.Contains("--json")) {
                Console.WriteLine(JsonSerializer.Serialize(result.ToWire(), new JsonSerializerOptions {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            } else {
                Console.Write(RunCompare.Summary(result));
            }
            return 0;
        }

        private static bool InsideGitWorkTree(string file) {
            DirectoryInfo? dir = new FileInfo(file).Directory;
            while (dir != null) {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return true;
                dir = dir.Parent;
            }
            return false;
        }

        private static string? Value(string[] args, string prefix) =>
            args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length).Trim('"');
    }
}
