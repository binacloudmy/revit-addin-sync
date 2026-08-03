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

            // 1. Get attachment path
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var path = DwgScratchCache.GetPath(dwgRef);
            if (path == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"attachment '{dwgRef}' not found — attach a DWG first" };

            // 2. Extract lines from ACadSharp
            var layerFilter = ArgsHelp.GetString(args, "layer_filter");
            var cadLines = CadFileReader.GetLinesForLayer(path, layerFilter);
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

            // 3. Convert to WallSeg (solver expects feet)
            var segs = cadLines.Select(l => new WallSeg(
                l.X1 / MmPerFoot, l.Y1 / MmPerFoot,
                l.X2 / MmPerFoot, l.Y2 / MmPerFoot,
                l.Layer
            )).ToList();

            // 4. Solve centerlines
            var opt = SolveOptions.FromMm(
                minThickMm: ArgsHelp.GetDouble(args, "min_thickness_mm") ?? 50,
                maxThickMm: ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500,
                angleTolDeg: ArgsHelp.GetDouble(args, "angle_tol_deg") ?? 1.5,
                overlapMinRatio: ArgsHelp.GetDouble(args, "overlap_min_ratio") ?? 0.5,
                minSegLenMm: ArgsHelp.GetDouble(args, "min_wall_length_mm") ?? 300,
                snapMm: ArgsHelp.GetDouble(args, "snap_mm") ?? ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500,
                cornerReachMm: ArgsHelp.GetDouble(args, "corner_reach_mm") ?? 500);

            var solved = CadCenterlineSolver.Solve(segs, opt);

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

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["dwg_ref"] = dwgRef,
                    ["layer_filter"] = layerFilter,
                    ["level"] = levelName,
                    ["proposed_walls"] = proposed,
                    ["wall_count"] = proposed.Count,
                    ["segments_in"] = cadLines.Count,
                    ["unpaired_segments"] = solved.UnpairedSegments,
                    ["junctions_snapped"] = solved.JunctionsSnapped,
                    ["note"] = "preview only — pass create=true to build walls. No Revit link needed.",
                };
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
            string? firstError = null;
            var byType = new Dictionary<string, int>();

            using var tx = new Transaction(doc, "BinaVibe: cad_walls_from_attachment");
            TxGuard.StartSwallowing(tx);
            try
            {
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
                ["by_type"] = byType,
                ["skipped_out_of_window"] = skippedWindow,
                ["skipped_no_type_match"] = skippedNoType,
                ["skipped_degenerate"] = skippedDegenerate,
                ["failed"] = failed,
                ["first_error"] = firstError,
                ["unpaired_segments"] = solved.UnpairedSegments,
                ["junctions_snapped"] = solved.JunctionsSnapped,
                ["note"] = "walls created from attachment — no Revit link needed.",
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
    }
}
