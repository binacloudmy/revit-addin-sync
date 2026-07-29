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
    /// midline, snap corners. Returns proposed create_wall args — creation stays a
    /// separate mutate step so the Ya/Tidak gate and idempotency keys are unchanged.
    ///
    /// Read-only (no Transaction). v1 = straight walls only (arcs never reach
    /// CadExtract's `segments`).
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
                    snapMm: ArgsHelp.GetDouble(args, "snap_mm") ?? ArgsHelp.GetDouble(args, "max_thickness_mm") ?? 500);

                var solved = CadCenterlineSolver.Solve(segs, opt);

                // 2. Emit exact create_wall args. Convert feet -> mm HERE, once.
                string level = ArgsHelp.GetString(args, "level")
                    ?? (ext.TryGetValue("import_level", out var lv) ? lv as string : null)
                    ?? "";
                double? heightMm = ArgsHelp.GetDouble(args, "height_mm");
                string? typeName = ArgsHelp.GetString(args, "type_name");

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
