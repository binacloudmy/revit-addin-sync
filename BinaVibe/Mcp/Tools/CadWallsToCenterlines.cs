using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>
    /// cad_walls_to_centerlines — turn CAD double-lines into Revit wall centerlines.
    ///
    /// Reuses extract_cad_geometry verbatim for the geometry read (same import
    /// selection, same layer filter, same *_ft segment shape), then runs the pure
    /// CadCenterlineSolver: parallel-pair the two faces of each wall, take the
    /// midline, snap corners.
    ///
    /// Two modes on one tool (one Ya/Tidak gate either way):
    ///   - create=false (default) → returns proposed create_wall args, no Transaction.
    ///   - create=true → builds every accepted wall itself in ONE Transaction and
    ///     returns a summary. This is the anti-loop path: the model sets a type rule
    ///     once instead of issuing hundreds of per-wall create_wall calls.
    ///
    /// v1 = straight walls only (arcs never reach CadExtract's `segments`).
    /// </summary>
    internal static class CadWallsToCenterlines
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            // 1. Reuse CadExtract for the read. It handles import_id / name_filter /
            //    layer_filter and the ambiguous/not-found "ask the user" responses —
            //    pass those straight through.
            var ext = CadExtract.Run(uidoc, args);
            if (!(ext.TryGetValue("ok", out var okv) && okv is true))
                return ext;
            if (ext.TryGetValue("ambiguous", out var amb) && amb is true)
                return ext;
            if (ext.TryGetValue("segments", out var segObj) && segObj is List<Dictionary<string, object>> segDicts)
            {
                var segs = ToWallSegs(segDicts);

                var opt = SolveOptions.FromMm(
                    minThickMm: ArgsHelp.GetDouble(args, "min_thickness_mm") ?? 50,
                    maxThickMm: ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500,
                    angleTolDeg: ArgsHelp.GetDouble(args, "angle_tol_deg") ?? 1.5,
                    overlapMinRatio: ArgsHelp.GetDouble(args, "overlap_min_ratio") ?? 0.5,
                    minSegLenMm: ArgsHelp.GetDouble(args, "min_wall_length_mm") ?? 300,
                    // snap_mm default = max thickness (spec).
                    snapMm: ArgsHelp.GetDouble(args, "snap_mm") ?? ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500,
                    cornerReachMm: ArgsHelp.GetDouble(args, "corner_reach_mm") ?? 500);

                var solved = CadCenterlineSolver.Solve(segs, opt);

                string level = ArgsHelp.GetString(args, "level")
                    ?? (ext.TryGetValue("import_level", out var lv) ? lv as string : null)
                    ?? "";
                double? heightMm = ArgsHelp.GetDouble(args, "height_mm");
                string? typeName = ArgsHelp.GetString(args, "type_name");
                string? layerFilter = ArgsHelp.GetString(args, "layer_filter");

                // 2a. CREATE MODE — build the walls here in one gated transaction.
                if (ArgsHelp.GetBool(args, "create") == true)
                    return CreateWalls(uidoc.Document, solved, ext, level, heightMm, typeName,
                        ParseBands(args),
                        ArgsHelp.GetDouble(args, "create_min_thickness_mm"),
                        ArgsHelp.GetDouble(args, "create_max_thickness_mm"),
                        layerFilter);

                // 2b. PROPOSAL MODE — emit exact create_wall args. Convert feet -> mm HERE.
                var proposed = solved.Walls.Select(c =>
                {
                    var wall = new Dictionary<string, object?>
                    {
                        ["start_mm"] = new[] { Math.Round(c.Ax * MmPerFoot, 1), Math.Round(c.Ay * MmPerFoot, 1), 0.0 },
                        ["end_mm"] = new[] { Math.Round(c.Bx * MmPerFoot, 1), Math.Round(c.By * MmPerFoot, 1), 0.0 },
                        ["level"] = level,
                        ["thickness_mm"] = Math.Round(c.ThicknessFt * MmPerFoot, 1),
                        ["source_layer"] = c.Layer,
                    };
                    if (heightMm.HasValue) wall["height_mm"] = heightMm.Value;
                    if (!string.IsNullOrEmpty(typeName)) wall["type_name"] = typeName;
                    return (object)wall;
                }).ToList();

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["import_id"] = ext.TryGetValue("import_id", out var iid) ? iid : null,
                    ["import_name"] = ext.TryGetValue("import_name", out var inm) ? inm : null,
                    ["level"] = level,
                    ["layer_filter"] = ArgsHelp.GetString(args, "layer_filter"),
                    ["proposed_walls"] = proposed,
                    ["wall_count"] = proposed.Count,
                    ["segments_in"] = segDicts.Count,
                    ["unpaired_segments"] = solved.UnpairedSegments,
                    ["junctions_snapped"] = solved.JunctionsSnapped,
                    ["note"] = "pass each proposed_walls entry to create_wall (mutate — behind the Ya/Tidak gate). "
                        + "start_mm/end_mm are ready mm triplets; do NOT convert. Provide a `level` arg or the CAD "
                        + "import level is used. type_name/height_mm fall through to create_wall defaults when omitted. "
                        + "unpaired_segments are single-face/leftover lines not turned into walls.",
                };
            }

            // No segments key (e.g. name-filter miss listing imports_present) — pass through.
            return ext;
        }

        // Create every accepted wall in ONE transaction. Coords are used directly in
        // FEET from the solver (no mm round-trip). Per-wall try/catch so one bad wall
        // (degenerate/rejected by Revit) doesn't roll back the whole batch.
        private static Dictionary<string, object?> CreateWalls(
            Document doc, SolveResult solved, Dictionary<string, object?> ext,
            string level, double? heightMm, string? typeName, List<ThicknessBand> bands,
            double? createMinMm, double? createMaxMm, string? layerFilter)
        {
            var levelEl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, level, StringComparison.OrdinalIgnoreCase));
            if (levelEl == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"level '{level}' not found" };

            double heightFt = (heightMm ?? 3000.0) / MmPerFoot;

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

            using var tx = new Transaction(doc, "BinaVibe: cad_walls_to_centerlines");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var c in solved.Walls)
                {
                    double th = c.ThicknessFt * MmPerFoot;
                    if (!CadWallBanding.InWindow(th, createMinMm, createMaxMm)) { skippedWindow++; continue; }

                    string? tName = CadWallBanding.PickType(th, bands, typeName);
                    // band mode with no match and no fallback type → leave it for the user.
                    if (bands.Count > 0 && tName == null) { skippedNoType++; continue; }

                    // Degenerate after snapping (collapsed to a point) → Revit would throw.
                    if (Dist(c.Ax, c.Ay, c.Bx, c.By) < 1e-3) { skippedDegenerate++; continue; }

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
                ["level"] = level,
                ["layer_filter"] = layerFilter,
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
                ["note"] = "walls created in one transaction. by_type shows the count per resolved "
                    + "wall type ('<default>' = Revit default type when none resolved). Re-run with "
                    + "create=false to preview proposed_walls without building.",
            };
        }

        // Parse the optional thickness_to_type band map: [{min_mm, max_mm, type_name}].
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

        private static double Dist(double ax, double ay, double bx, double by)
            => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        // CadExtract stored each coordinate as a boxed double (Math.Round) and layer
        // as a string. Read them back into the solver's plain-double input.
        private static List<WallSeg> ToWallSegs(List<Dictionary<string, object>> segDicts)
        {
            var list = new List<WallSeg>(segDicts.Count);
            foreach (var s in segDicts)
            {
                if (!(TryD(s, "x1_ft", out var x1) && TryD(s, "y1_ft", out var y1)
                      && TryD(s, "x2_ft", out var x2) && TryD(s, "y2_ft", out var y2)))
                    continue;
                var layer = s.TryGetValue("layer", out var l) ? l as string ?? "" : "";
                list.Add(new WallSeg(x1, y1, x2, y2, layer));
            }
            return list;
        }

        private static bool TryD(Dictionary<string, object> d, string key, out double v)
        {
            v = 0;
            if (!d.TryGetValue(key, out var o) || o == null) return false;
            switch (o)
            {
                case double dd: v = dd; return true;
                case int ii: v = ii; return true;
                case long ll: v = ll; return true;
                default: return double.TryParse(o.ToString(), out v);
            }
        }
    }
}
