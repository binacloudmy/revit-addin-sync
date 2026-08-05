// cad_walls_from_attachment — create Revit walls from an ATTACHED DWG/DXF.
//
// Unlike cad_walls_to_centerlines (which requires a Revit ImportInstance link),
// this tool reads geometry directly via ACadSharp. No Revit link needed — just
// attach the DWG in the Copilot pane.
//
// Flow:
//   1. DwgScratchCache.GetPath(dwg_ref) → file path
//   2. CadFileReader.GetLinesForLayer(path, layer_filter) → line segments
//   3. CadCenterlineSolver.Solve() → wall centerlines
//   4. Wall.Create() in one Transaction

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadWallsFromAttachment
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var timings = new Dictionary<string, long>();

            // 1. Get attachment path
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var path = DwgScratchCache.GetPath(dwgRef);
            if (path == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"attachment '{dwgRef}' not found — attach a DWG first" };

            // 2. Extract lines from ACadSharp
            var layerFilter = ArgsHelp.GetString(args, "layer_filter");
            var extractStart = sw.ElapsedMilliseconds;
            var linesResult = CadFileReader.GetLinesForLayerWithTimings(path, layerFilter);
            var cadLines = linesResult.Lines;
            // Capture ACadSharp timings
            foreach (var kv in linesResult.Timings)
                timings[$"acadsharp_{kv.Key}"] = kv.Value;
            if (cadLines.Count == 0)
            {
                var allLines = CadFileReader.Extract(path);
                var layers = allLines.Lines.GroupBy(l => l.Layer).Select(g => g.Key).Take(20).ToList();
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"no lines found for layer_filter '{layerFilter}'",
                    ["available_layers"] = layers,
                    ["total_lines"] = allLines.LineCount,
                };
            }

            // 2b. Filter by Z range (for multi-floor DWGs)
            var zMinMm = ArgsHelp.GetDouble(args, "z_min_mm");
            var zMaxMm = ArgsHelp.GetDouble(args, "z_max_mm");
            if (zMinMm.HasValue || zMaxMm.HasValue)
            {
                cadLines = FilterByZ(cadLines, zMinMm, zMaxMm);
                if (cadLines.Count == 0)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = $"no lines in Z range {zMinMm ?? 0}mm to {zMaxMm ?? double.MaxValue}mm",
                    };
            }

            timings["extract_ms"] = sw.ElapsedMilliseconds - extractStart;

            // 3. Convert to WallSeg (solver expects feet)
            var segsRaw = cadLines.Select(l => new WallSeg(
                l.X1 / MmPerFoot, l.Y1 / MmPerFoot,
                l.X2 / MmPerFoot, l.Y2 / MmPerFoot,
                l.Layer
            )).ToList();

            // 3b. Stitch collinear segments across door gaps + filter column pads
            var stitchStart = sw.ElapsedMilliseconds;
            var stitchOpt = StitchOptions.FromMm(
                maxGapMm: ArgsHelp.GetDouble(args, "max_stitch_gap_mm") ?? 1500,
                collinearTolMm: ArgsHelp.GetDouble(args, "collinear_tol_mm") ?? 50,
                maxColumnSizeMm: ArgsHelp.GetDouble(args, "max_column_size_mm") ?? 800);
            var stitched = CadSegmentStitcher.Stitch(segsRaw, stitchOpt);
            var segs = stitched.Segments;
            timings["stitch_ms"] = sw.ElapsedMilliseconds - stitchStart;

            // 4. Solve centerlines
            var solveStart = sw.ElapsedMilliseconds;
            var opt = SolveOptions.FromMm(
                minThickMm: ArgsHelp.GetDouble(args, "min_thickness_mm") ?? 50,
                maxThickMm: ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500,
                angleTolDeg: ArgsHelp.GetDouble(args, "angle_tol_deg") ?? 1.5,
                overlapMinRatio: ArgsHelp.GetDouble(args, "overlap_min_ratio") ?? 0.5,
                minSegLenMm: ArgsHelp.GetDouble(args, "min_wall_length_mm") ?? 300,
                snapMm: ArgsHelp.GetDouble(args, "snap_mm") ?? ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500,
                cornerReachMm: ArgsHelp.GetDouble(args, "corner_reach_mm") ?? 500);

            var solved = CadCenterlineSolver.Solve(segs, opt);
            timings["solve_ms"] = sw.ElapsedMilliseconds - solveStart;

            // 5. Get level
            var levelName = ArgsHelp.GetString(args, "level");
            Level? levelEl = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                levelEl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (levelEl == null)
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"level '{levelName}' not found" };
            }
            else
            {
                // Default to lowest level
                levelEl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).FirstOrDefault();
                if (levelEl == null)
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no levels in model" };
                levelName = levelEl.Name;
            }

            // 6a. PROPOSAL MODE (default) — return proposed walls without creating
            if (ArgsHelp.GetBool(args, "create") != true)
            {
                var proposed = solved.Walls.Select(c => new Dictionary<string, object?>
                {
                    ["start_mm"] = new[] { Math.Round(c.Ax * MmPerFoot, 1), Math.Round(c.Ay * MmPerFoot, 1), 0.0 },
                    ["end_mm"] = new[] { Math.Round(c.Bx * MmPerFoot, 1), Math.Round(c.By * MmPerFoot, 1), 0.0 },
                    ["level"] = levelName,
                    ["thickness_mm"] = Math.Round(c.ThicknessFt * MmPerFoot, 1),
                    ["source_layer"] = c.Layer,
                }).ToList();

                // Build thickness histogram for type mapping guidance
                var thicknesses = solved.Walls.Select(c => Math.Round(c.ThicknessFt * MmPerFoot, 0)).ToList();
                var histogram = BuildThicknessHistogram(thicknesses);
                var stats = new Dictionary<string, object?>
                {
                    ["min_mm"] = thicknesses.Count > 0 ? thicknesses.Min() : 0,
                    ["max_mm"] = thicknesses.Count > 0 ? thicknesses.Max() : 0,
                    ["median_mm"] = thicknesses.Count > 0 ? Median(thicknesses) : 0,
                };

                // Detect floor clusters from Z coordinates
                var floorClusters = DetectFloorClusters(cadLines);

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["dwg_ref"] = dwgRef,
                    ["layer_filter"] = layerFilter,
                    ["level"] = levelName,
                    ["proposed_walls"] = proposed,
                    ["wall_count"] = proposed.Count,
                    ["segments_in"] = cadLines.Count,
                    ["segments_after_stitch"] = segs.Count,
                    ["segments_merged"] = stitched.MergedCount,
                    ["column_pads_filtered"] = stitched.FilteredRectangles,
                    ["unpaired_segments"] = solved.UnpairedSegments,
                    ["junctions_snapped"] = solved.JunctionsSnapped,
                    ["thickness_histogram"] = histogram,
                    ["thickness_stats"] = stats,
                    ["note"] = "Preview only. Use thickness_histogram to suggest thickness_to_type mapping. "
                        + "Pass create=true with thickness_to_type:[{min_mm, max_mm, type_name}] to build walls. "
                        + "To include single-line walls, pass include_unpaired=true with unpaired_type_name.",
                    ["_diagnostics"] = new Dictionary<string, object?>
                    {
                        ["timings_ms"] = timings,
                        ["total_ms"] = sw.ElapsedMilliseconds,
                    },
                };

                // Add unpaired segment info if any
                if (solved.UnpairedSegments > 0)
                {
                    result["unpaired_note"] = $"{solved.UnpairedSegments} single-line segments (no parallel pair). "
                        + "These may be walls drawn as single lines. To include them, pass include_unpaired=true "
                        + "and unpaired_type_name (e.g. 'Generic - 150mm').";
                }

                // Add floor cluster info if multiple floors detected
                if (floorClusters.Count > 1)
                {
                    result["floor_clusters"] = floorClusters;
                    result["multi_floor_warning"] = $"Detected {floorClusters.Count} floor levels in DWG (Z offsets). "
                        + "Consider filtering by z_min_mm/z_max_mm to process one floor at a time, or creating "
                        + "separate Revit levels for each floor.";
                }

                return result;
            }

            // 6b. CREATE MODE — build walls in one Transaction
            double heightMm = ArgsHelp.GetDouble(args, "height_mm") ?? 3000.0;
            double heightFt = heightMm / MmPerFoot;
            string? typeName = ArgsHelp.GetString(args, "type_name");

            var bands = ParseBands(args);
            double? createMinMm = ArgsHelp.GetDouble(args, "create_min_thickness_mm");
            double? createMaxMm = ArgsHelp.GetDouble(args, "create_max_thickness_mm");

            var typeCache = new Dictionary<string, WallType?>(StringComparer.OrdinalIgnoreCase);
            WallType? ResolveType(string? name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (typeCache.TryGetValue(name, out var cached)) return cached;
                var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                typeCache[name] = wt;
                return wt;
            }

            int created = 0, skippedWindow = 0, skippedNoType = 0, skippedDegenerate = 0, failed = 0;
            int unpairedCreated = 0;
            string? firstError = null;
            var byType = new Dictionary<string, int>();

            // Check if user wants unpaired segments included
            bool includeUnpaired = ArgsHelp.GetBool(args, "include_unpaired") == true;
            string? unpairedTypeName = ArgsHelp.GetString(args, "unpaired_type_name");

            var createStart = sw.ElapsedMilliseconds;
            using var tx = new Transaction(doc, "BinaVibe: cad_walls_from_attachment");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Create walls from paired segments (detected thickness)
                foreach (var c in solved.Walls)
                {
                    double th = c.ThicknessFt * MmPerFoot;
                    if (!CadWallBanding.InWindow(th, createMinMm, createMaxMm)) { skippedWindow++; continue; }

                    string? tName = CadWallBanding.PickType(th, bands, typeName);
                    if (bands.Count > 0 && tName == null) { skippedNoType++; continue; }

                    // Degenerate wall (zero length) → skip
                    double len = Math.Sqrt(
                        (c.Bx - c.Ax) * (c.Bx - c.Ax) +
                        (c.By - c.Ay) * (c.By - c.Ay));
                    if (len < 1e-3) { skippedDegenerate++; continue; }

                    try
                    {
                        var line = Line.CreateBound(new XYZ(c.Ax, c.Ay, 0), new XYZ(c.Bx, c.By, 0));
                        var wt = ResolveType(tName);
                        var wall = wt != null
                            ? Wall.Create(doc, line, wt.Id, levelEl.Id, heightFt, 0, false, false)
                            : Wall.Create(doc, line, levelEl.Id, false);
                        created++;
                        var key = wt?.Name ?? "<default>";
                        byType[key] = byType.TryGetValue(key, out var n) ? n + 1 : 1;
                    }
                    catch (Exception ex) { failed++; firstError ??= ex.Message; }
                }

                // Create walls from unpaired segments (single-line walls)
                if (includeUnpaired && !string.IsNullOrEmpty(unpairedTypeName))
                {
                    var unpairedWt = ResolveType(unpairedTypeName);
                    if (unpairedWt != null)
                    {
                        foreach (var seg in solved.UnpairedSegs)
                        {
                            double len = seg.Len;
                            if (len < 1e-3) { skippedDegenerate++; continue; }

                            try
                            {
                                var line = Line.CreateBound(
                                    new XYZ(seg.Ax, seg.Ay, 0),
                                    new XYZ(seg.Bx, seg.By, 0));
                                Wall.Create(doc, line, unpairedWt.Id, levelEl.Id, heightFt, 0, false, false);
                                unpairedCreated++;
                                byType[unpairedTypeName] = byType.TryGetValue(unpairedTypeName, out var n) ? n + 1 : 1;
                            }
                            catch (Exception ex) { failed++; firstError ??= ex.Message; }
                        }
                    }
                }

                TxGuard.CommitOrThrow(tx);
            }
            catch (Exception) { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created"] = true,
                ["dwg_ref"] = dwgRef,
                ["layer_filter"] = layerFilter,
                ["level"] = levelName,
                ["proposed_count"] = solved.Walls.Count,
                ["created_count"] = created,
                ["unpaired_created"] = unpairedCreated,
                ["total_created"] = created + unpairedCreated,
                ["by_type"] = byType,
                ["skipped_out_of_window"] = skippedWindow,
                ["skipped_no_type_match"] = skippedNoType,
                ["skipped_degenerate"] = skippedDegenerate,
                ["failed"] = failed,
                ["first_error"] = firstError,
                ["unpaired_segments_remaining"] = solved.UnpairedSegments - unpairedCreated,
                ["junctions_snapped"] = solved.JunctionsSnapped,
                ["note"] = unpairedCreated > 0
                    ? $"walls created from attachment — {unpairedCreated} single-line walls included."
                    : "walls created from attachment — no Revit link needed.",
                ["_diagnostics"] = new Dictionary<string, object?>
                {
                    ["timings_ms"] = new Dictionary<string, long>(timings)
                    {
                        ["create_ms"] = sw.ElapsedMilliseconds - createStart,
                    },
                    ["total_ms"] = sw.ElapsedMilliseconds,
                },
            };
        }

        private static List<ThicknessBand> ParseBands(JsonElement args)
        {
            var bands = new List<ThicknessBand>();
            if (args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("thickness_to_type", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    double min = GetD(el, "min_mm") ?? 0;
                    double max = GetD(el, "max_mm") ?? double.MaxValue;
                    string? tn = el.TryGetProperty("type_name", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null;
                    if (!string.IsNullOrEmpty(tn)) bands.Add(new ThicknessBand(min, max, tn!));
                }
            }
            return bands;
        }

        private static double? GetD(JsonElement o, string key)
            => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
                ? d : (double?)null;

        /// <summary>
        /// Build thickness histogram with suggested bands for type mapping.
        /// Groups into 50mm buckets to reveal natural clusters.
        /// </summary>
        private static List<Dictionary<string, object>> BuildThicknessHistogram(List<double> thicknesses)
        {
            if (thicknesses.Count == 0) return new List<Dictionary<string, object>>();

            // Group into 50mm buckets
            var buckets = thicknesses
                .GroupBy(t => Math.Floor(t / 50) * 50)
                .OrderBy(g => g.Key)
                .Select(g => new Dictionary<string, object>
                {
                    ["range_mm"] = $"{g.Key:0}-{g.Key + 50:0}",
                    ["min_mm"] = g.Key,
                    ["max_mm"] = g.Key + 50,
                    ["count"] = g.Count(),
                    ["percent"] = Math.Round(100.0 * g.Count() / thicknesses.Count, 1),
                })
                .ToList();

            return buckets;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
        }

        /// <summary>
        /// Detect floor clusters from Z coordinates of lines.
        /// Groups Z values into clusters (floors typically differ by 3000-4500mm).
        /// </summary>
        private static List<Dictionary<string, object>> DetectFloorClusters(List<CadLine> lines)
        {
            // Get all Z values (use average of Z1 and Z2 for each line)
            var zValues = lines
                .Select(l => Math.Round((l.Z1 + l.Z2) / 2, 0))
                .ToList();

            if (zValues.Count == 0) return new List<Dictionary<string, object>>();

            // Group into clusters (tolerance ~100mm)
            const double clusterTolerance = 100;
            var clusters = new List<(double z, int count)>();

            foreach (var z in zValues.OrderBy(z => z))
            {
                var existing = clusters.FindIndex(c => Math.Abs(c.z - z) < clusterTolerance);
                if (existing >= 0)
                {
                    var (oldZ, oldCount) = clusters[existing];
                    // Update centroid
                    clusters[existing] = ((oldZ * oldCount + z) / (oldCount + 1), oldCount + 1);
                }
                else
                {
                    clusters.Add((z, 1));
                }
            }

            // Filter out tiny clusters (noise)
            var significant = clusters
                .Where(c => c.count >= lines.Count * 0.05) // At least 5% of lines
                .OrderBy(c => c.z)
                .Select((c, i) => new Dictionary<string, object>
                {
                    ["floor_index"] = i,
                    ["z_mm"] = Math.Round(c.z, 0),
                    ["line_count"] = c.count,
                    ["percent"] = Math.Round(100.0 * c.count / lines.Count, 1),
                })
                .ToList();

            return significant;
        }

        /// <summary>
        /// Filter lines by Z range (for processing one floor at a time).
        /// </summary>
        public static List<CadLine> FilterByZ(List<CadLine> lines, double? zMinMm, double? zMaxMm)
        {
            if (!zMinMm.HasValue && !zMaxMm.HasValue) return lines;

            return lines.Where(l =>
            {
                double avgZ = (l.Z1 + l.Z2) / 2;
                if (zMinMm.HasValue && avgZ < zMinMm.Value) return false;
                if (zMaxMm.HasValue && avgZ > zMaxMm.Value) return false;
                return true;
            }).ToList();
        }
    }
}
