using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using BinaVibe.Mcp.Tools.Electrical;

namespace Cad2Bim.Headless {
    /// <summary>
    /// The phase 1 exit check without Revit, on files dump_electrical_reference and
    /// plan_cad_electrical wrote:
    ///
    /// `--compare-devices ref.json placed.json [--plan=plan.json] [--ref-types=text] [--ref-link=text] [--json]`
    /// matches the reference devices to the placed ones (PlacementCompare). `--ref-types` keeps
    /// the devices of both dumps whose family type contains the text; `--ref-link` carries the
    /// reference through the placed dump's link whose name contains the text.
    ///
    /// `file.dwg --fit=walls.json [--cluster=N]` fits the drawing's walls to a dump's walls
    /// (CadModelFit): one level's walls, or every level's when the dump has walls_by_level.
    /// </summary>
    internal static class ElectricalCheckCommands {
        internal static int Compare(string[] args) {
            int at = Array.IndexOf(args, "--compare-devices");
            if (at < 0 || at + 2 >= args.Length) {
                Console.Error.WriteLine("usage: --compare-devices <ref.json> <placed.json> [--plan=plan.json] [--ref-types=text] [--ref-link=text] [--json]");
                return 2;
            }

            string? types = Value(args, "--ref-types=");
            string? linkText = Value(args, "--ref-link=");
            string? planPath = Value(args, "--plan=");

            using JsonDocument refDoc = Load(args[at + 1]);
            using JsonDocument placedDoc = Load(args[at + 2]);
            List<RefDevice> reference = PlacementCompare.ReadDevices(refDoc.RootElement, types);
            List<RefDevice> placed = PlacementCompare.ReadDevices(placedDoc.RootElement, types);

            LinkTransform? link = null;
            if (!string.IsNullOrEmpty(linkText)) {
                List<LinkTransform> links = PlacementCompare.ReadLinks(placedDoc.RootElement)
                    .Where(l => l.Name.IndexOf(linkText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (links.Count != 1) {
                    Console.Error.WriteLine($"--ref-link={linkText} matches {links.Count} links in {args[at + 2]}; it must match one");
                    return 2;
                }
                link = links[0];
            }

            var points = new List<PlannedPoint>();
            var dropped = new List<DroppedInstance>();
            if (!string.IsNullOrEmpty(planPath)) {
                using JsonDocument planDoc = Load(planPath);
                PlacementCompare.ReadPlan(planDoc.RootElement, points, dropped);
            }

            CompareResult result = PlacementCompare.Compare(reference, placed, points, dropped, 50, link);
            if (args.Contains("--json")) {
                Console.WriteLine(JsonSerializer.Serialize(result.ToWire(), new JsonSerializerOptions {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            } else {
                Console.Write(PlacementCompare.Summary(result));
            }
            return 0;
        }

        internal static int Fit(string path, string[] args) {
            string wallsPath = Value(args, "--fit=") ?? "";
            string? clusterText = Value(args, "--cluster=");

            using JsonDocument wallsDoc = Load(wallsPath);
            var byLevel = new Dictionary<string, List<(FitSegment Centreline, double ThicknessMm)>>(StringComparer.Ordinal);
            JsonElement root = wallsDoc.RootElement;
            if (root.TryGetProperty("walls", out JsonElement one) && one.ValueKind == JsonValueKind.Array) {
                string level = root.TryGetProperty("level", out JsonElement l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? "" : "";
                byLevel[level] = Walls(one);
            } else if (root.TryGetProperty("walls_by_level", out JsonElement all) && all.ValueKind == JsonValueKind.Object) {
                foreach (JsonProperty level in all.EnumerateObject()) byLevel[level.Name] = Walls(level.Value);
            } else {
                Console.Error.WriteLine($"{wallsPath} has no walls: dump it with include_walls=true");
                return 2;
            }

            ElectricalScan scan = ElectricalCensus.ScanFile(path);
            List<string> background = scan.WallLikeLayers.ToList();
            if (background.Count == 0) {
                Console.Error.WriteLine("no wall-like layer in the drawing");
                return 2;
            }

            IEnumerable<int> clusters = clusterText != null
                ? new[] { int.Parse(clusterText, CultureInfo.InvariantCulture) }
                : Enumerable.Range(0, scan.Clusters.Count);
            var o = new FitOptions();
            var model = byLevel.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<FitSegment>)pair.Value.Select(w => w.Centreline).ToList(), StringComparer.Ordinal);

            foreach (int cluster in clusters) {
                if (cluster < 0 || cluster >= scan.Clusters.Count) {
                    Console.Error.WriteLine($"no cluster {cluster}: the drawing has {scan.Clusters.Count}");
                    return 2;
                }
                List<FitSegment> cad = scan.WallCentrelines(cluster, background, false);
                List<LevelFit> fits = CadModelFit.FitLevels(cad, model, o, out bool tie);
                Console.WriteLine($"cluster {cluster}: {cad.Count} drawing walls{(tie ? "  (levels tie)" : "")}");
                foreach (LevelFit fit in fits) {
                    Console.WriteLine("  " + Line(fit.Level, "fit", fit.Fit));
                    if (fit.Fit.Outcome != "failed") continue;
                    FitResult faces = CadModelFit.Fit(Faces(scan, cluster, background, o), CadModelFit.ModelFaces(byLevel[fit.Level]), o);
                    Console.WriteLine("  " + Line(fit.Level, "fit_faces", faces));
                }
            }
            return 0;
        }

        private static string Line(string level, string method, FitResult f) =>
            $"{level,-24} {method,-9} score {f.Score:0.000}  {f.Outcome,-11} rot {f.Transform.RotationRad * 180 / Math.PI:0.00}  " +
            $"tx {f.Transform.TxMm:0}  ty {f.Transform.TyMm:0}  voters {f.Voters}  model cover {f.ModelCoverage:0.000}" +
            (f.FailReason != null ? "  " + f.FailReason : "");

        /// <summary>The drawing's wall faces in one plan, as plan_cad_electrical's fallback takes them.</summary>
        private static List<FitSegment> Faces(ElectricalScan scan, int cluster, List<string> layers, FitOptions o) {
            ScanCluster box = scan.Clusters[cluster];
            double margin = new CensusOptions().NearWallMm;
            return ElectricalCensus.MergeByDirection(scan.FacePieces(layers))
                .Where(s => s.Length >= o.MinVoteLengthMm)
                .Select(s => new FitSegment(s.P1.x, s.P1.y, s.P2.x, s.P2.y))
                .Where(s => {
                    double mx = (s.X1 + s.X2) / 2, my = (s.Y1 + s.Y2) / 2;
                    return mx >= box.MinX - margin && mx <= box.MaxX + margin && my >= box.MinY - margin && my <= box.MaxY + margin;
                })
                .ToList();
        }

        private static List<(FitSegment Centreline, double ThicknessMm)> Walls(JsonElement list) {
            var walls = new List<(FitSegment, double)>();
            foreach (JsonElement w in list.EnumerateArray()) {
                walls.Add((new FitSegment(Number(w, "x1"), Number(w, "y1"), Number(w, "x2"), Number(w, "y2")), Number(w, "thickness_mm")));
            }
            return walls;
        }

        private static double Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        private static JsonDocument Load(string file) => JsonDocument.Parse(File.ReadAllText(file));

        private static string? Value(string[] args, string prefix) =>
            args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length).Trim('"');
    }
}
