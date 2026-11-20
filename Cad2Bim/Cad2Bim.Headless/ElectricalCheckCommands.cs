using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using BinaVibe.Mcp.Tools.Electrical;

namespace Cad2Bim.Headless {
    /// <summary>
    /// The phase 1 exit check without Revit, on files dump_electrical_reference and
    /// plan_cad_electrical wrote:
    ///
    /// `--compare-devices ref.json placed.json [--plan=plan.json] [--ref-types=text] [--ref-link=text] [--role=socket] [--across=50] [--along=500] [--dz=150] [--json]`
    /// matches the reference devices to the placed ones (PlacementCompare) and reports the
    /// issue's rule first: the share of reference devices with a placed device within 50 mm, one
    /// to one, against 90%, and a code for every miss (the plan explains more of them).
    /// `--ref-types` keeps the devices of both dumps whose family type contains the text;
    /// `--ref-link` carries the reference through the placed dump's link whose name contains the
    /// text. Diagnostic: each match is split across and along the wall; `--across`, `--along` and
    /// `--dz` (mm) set when it passes (50, 500, 150). Those shares are of the plan's points
    /// (`--role` keeps one role's), else of the placed devices.
    ///
    /// `file.dwg --fit=walls.json [--cluster=N] [--min-wall=50]` fits the drawing's walls to a
    /// dump's walls (CadModelFit): one level's walls, or every level's when the dump has
    /// walls_by_level. Model walls thinner than --min-wall (mm) are left out, as
    /// plan_cad_electrical leaves them out. Each cluster's size, wall count and largest empty
    /// bands are printed first: a band wider than the 5 m plan gap inside one cluster means
    /// plans the one-pass clustering did not split.
    /// </summary>
    internal static class ElectricalCheckCommands {
        internal static int Compare(string[] args) {
            int at = Array.IndexOf(args, "--compare-devices");
            if (at < 0 || at + 2 >= args.Length) {
                Console.Error.WriteLine("usage: --compare-devices <ref.json> <placed.json> [--plan=plan.json] [--ref-types=text] [--ref-link=text] [--role=socket] [--across=50] [--along=500] [--dz=150] [--json]");
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

            var limits = new PassLimits();
            if (!TryMm(args, "--across=", ref limits.AcrossMm) || !TryMm(args, "--along=", ref limits.AlongMm) ||
                !TryMm(args, "--dz=", ref limits.DzMm))
                return 2;

            var points = new List<PlannedPoint>();
            var dropped = new List<DroppedInstance>();
            bool census = false;
            if (!string.IsNullOrEmpty(planPath)) {
                using JsonDocument planDoc = Load(planPath);
                census = PlacementCompare.ReadPlan(planDoc.RootElement, points, dropped);
            }

            // The issue's rule: within 50 mm, one to one; every miss gets a code.
            CompareResult result = PlacementCompare.Compare(reference, placed, points, dropped, 50, link, limits, census);
            result.PlanGiven = !string.IsNullOrEmpty(planPath);
            // The denominator is the drawn sockets: every plan point (placed or excluded), of
            // --role when given. With no plan, the placed devices stand in.
            string? role = Value(args, "--role=");
            if (!string.IsNullOrEmpty(planPath))
                result.Drawn = points.Count(p => string.IsNullOrEmpty(role) || string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));
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

        /// <summary>Reads a non-negative mm value; false (after saying why) when it is not one.</summary>
        private static bool TryMm(string[] args, string prefix, ref double value) {
            string? text = Value(args, prefix);
            if (text == null) return true;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) && mm >= 0) {
                value = mm;
                return true;
            }
            Console.Error.WriteLine($"{prefix}{text} is not a distance in mm");
            return false;
        }

        internal static int Fit(string path, string[] args) {
            string wallsPath = Value(args, "--fit=") ?? "";
            string? clusterText = Value(args, "--cluster=");
            string? minWallText = Value(args, "--min-wall=");
            double minWall = minWallText != null
                ? double.Parse(minWallText, CultureInfo.InvariantCulture)
                : CadSymbolResolve.MinStructuralWallMm;

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

            // Structural walls only, as plan_cad_electrical takes them (fewer than four left on a
            // level keeps them all).
            var structural = new Dictionary<string, List<(FitSegment Centreline, double ThicknessMm)>>(StringComparer.Ordinal);
            foreach (var pair in byLevel) {
                List<ModelWall> kept = CadSymbolResolve.Structural(pair.Value.Select(w => new ModelWall {
                    X1 = w.Centreline.X1, Y1 = w.Centreline.Y1, X2 = w.Centreline.X2, Y2 = w.Centreline.Y2, ThicknessMm = w.ThicknessMm,
                }).ToList(), minWall);
                structural[pair.Key] = kept.Select(w => (new FitSegment(w.X1, w.Y1, w.X2, w.Y2), w.ThicknessMm)).ToList();
                Console.WriteLine($"model {pair.Key}: {pair.Value.Count} walls, {pair.Value.Count - kept.Count} under {minWall:0} mm ignored");
            }
            var model = structural.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<FitSegment>)pair.Value.Select(w => w.Centreline).ToList(), StringComparer.Ordinal);

            List<int> chosen = clusters.ToList();
            foreach (int cluster in chosen) {
                if (cluster < 0 || cluster >= scan.Clusters.Count) {
                    Console.Error.WriteLine($"no cluster {cluster}: the drawing has {scan.Clusters.Count}");
                    return 2;
                }
            }

            // Every cluster's shape first, so a cluster holding stacked plans shows before the fits.
            Console.WriteLine($"drawing: {scan.Clusters.Count} clusters on {background.Count} wall-like layers");
            var walls = new Dictionary<int, List<FitSegment>>();
            foreach (int cluster in chosen) {
                walls[cluster] = scan.WallCentrelines(cluster, background, false);
                Console.WriteLine(ClusterLine(scan, cluster, walls[cluster]));
            }

            foreach (int cluster in chosen) {
                List<FitSegment> cad = walls[cluster];
                List<LevelFit> fits = CadModelFit.FitLevels(cad, model, o, out bool tie, out List<LevelFit> elsewhere);
                string others = elsewhere.Count > 0 ? "  (also fits elsewhere: " + string.Join(", ", elsewhere.Select(e => e.Level)) + ")" : "";
                Console.WriteLine($"cluster {cluster}: {cad.Count} drawing walls{(tie ? "  (levels tie)" : "")}{others}");
                foreach (LevelFit fit in fits) {
                    Console.WriteLine("  " + Line(fit.Level, "fit", fit.Fit));
                    Rivals(fit.Fit);
                    if (fit.Fit.Outcome != "failed") continue;
                    FitResult faces = CadModelFit.Fit(Faces(scan, cluster, background, o), CadModelFit.ModelFaces(structural[fit.Level]), o);
                    Console.WriteLine("  " + Line(fit.Level, "fit_faces", faces));
                    Rivals(faces);
                }
            }
            return 0;
        }

        private static string Line(string level, string method, FitResult f) =>
            $"{level,-24} {method,-9} score {f.Score:0.000}  uncropped {f.ScoreUncropped:0.000}  inside {f.InsideShare:0.000}  " +
            $"{f.Outcome,-11} rot {f.Transform.RotationRad * 180 / Math.PI:0.00}  " +
            $"tx {f.Transform.TxMm:0}  ty {f.Transform.TyMm:0}  voters {f.Voters}  model cover {f.ModelCoverage:0.000}  " +
            $"rivals {f.Rivals.Count}" +
            (f.FailReason != null ? "  " + f.FailReason : "");

        /// <summary>Each rival on its own line, in pick order (top to bottom of the drawing).</summary>
        private static void Rivals(FitResult f) {
            if (f.Rivals.Count == 0) return;
            List<FitResult> places = CadModelFit.Places(f);
            for (int i = 0; i < places.Count; i++) {
                FitResult p = places[i];
                Console.WriteLine($"      pick {i}{(ReferenceEquals(p, f) ? " (best)" : "")}  score {p.Score:0.000}  " +
                                  $"covered {p.CoveredMm / 1000:0.0} m  tx {p.Transform.TxMm:0}  ty {p.Transform.TyMm:0}");
            }
        }

        /// <summary>
        /// `cluster N: W x H m, C census walls, D drawing walls, largest empty y band B m at A-Z m,
        /// largest empty x band ...`: band positions from the cluster's bottom (y) or left (x)
        /// edge. Bands are read off the drawing's wall centrelines in the cluster, each wall
        /// spanning its full extent, as the plan clustering reads them.
        /// </summary>
        private static string ClusterLine(ElectricalScan scan, int cluster, List<FitSegment> walls) {
            ScanCluster box = scan.Clusters[cluster];
            CensusPlan? plan = cluster < scan.Census.Plans.Count ? scan.Census.Plans[cluster] : null;
            (double yGap, double yAt) = LargestGap(walls.Select(w => (Math.Min(w.Y1, w.Y2), Math.Max(w.Y1, w.Y2))), box.MinY, box.MaxY);
            (double xGap, double xAt) = LargestGap(walls.Select(w => (Math.Min(w.X1, w.X2), Math.Max(w.X1, w.X2))), box.MinX, box.MaxX);
            string split = Math.Max(yGap, xGap) > CadClassifier.PlanGapMm ? "  (gap over the 5 m plan gap)" : "";
            return $"cluster {cluster}: {(box.MaxX - box.MinX) / 1000:0.0} x {(box.MaxY - box.MinY) / 1000:0.0} m, " +
                   $"{plan?.Walls ?? 0} census walls, {walls.Count} drawing walls, " +
                   $"largest empty y band {yGap / 1000:0.0} m at {(yAt - box.MinY) / 1000:0.0}-{(yAt + yGap - box.MinY) / 1000:0.0} m, " +
                   $"largest empty x band {xGap / 1000:0.0} m at {(xAt - box.MinX) / 1000:0.0}-{(xAt + xGap - box.MinX) / 1000:0.0} m" +
                   split;
        }

        /// <summary>The widest stretch of [lo, hi] no span covers: its width and where it starts.</summary>
        internal static (double Width, double At) LargestGap(IEnumerable<(double Lo, double Hi)> spans, double lo, double hi) {
            double reach = lo, best = 0, at = lo;
            foreach ((double a, double b) in spans.OrderBy(s => s.Lo)) {
                if (a > reach && a - reach > best) {
                    best = Math.Min(a, hi) - reach;
                    at = reach;
                }
                reach = Math.Max(reach, b);
            }
            if (hi - reach > best) {
                best = hi - reach;
                at = reach;
            }
            return (Math.Max(0, best), at);
        }

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
