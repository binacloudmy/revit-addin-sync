// CadCreateWalls — cad_create_walls MCP tool. Creates Revit walls from
// centerline data produced by the CAD-to-BIM viewer's preview step.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadCreateWalls
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;

            // Parse centerlines
            if (!args.TryGetProperty("centerlines", out var centerlinesEl))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "centerlines required" };

            var centerlines = new List<(double ax, double ay, double bx, double by, double thickness)>();
            foreach (var cl in centerlinesEl.EnumerateArray())
            {
                centerlines.Add((
                    cl.GetProperty("ax").GetDouble() / MmPerFoot,
                    cl.GetProperty("ay").GetDouble() / MmPerFoot,
                    cl.GetProperty("bx").GetDouble() / MmPerFoot,
                    cl.GetProperty("by").GetDouble() / MmPerFoot,
                    cl.TryGetProperty("thickness_mm", out var t) ? t.GetDouble() : 200
                ));
            }

            // Get level
            var levelName = ArgsHelp.GetString(args, "level");
            Level? level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            }
            level ??= new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).First();

            // Get wall type
            var wallTypeName = ArgsHelp.GetString(args, "wall_type");
            WallType? wallType = null;
            if (!string.IsNullOrEmpty(wallTypeName))
            {
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name.Contains(wallTypeName, StringComparison.OrdinalIgnoreCase));
            }
            wallType ??= new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .First();

            var wallIds = new List<long>();
            var errors = new List<string>();

            using (var txn = new Transaction(doc, "Create Walls from CAD"))
            {
                txn.Start();

                foreach (var (ax, ay, bx, by, thickness) in centerlines)
                {
                    try
                    {
                        var start = new XYZ(ax, ay, level.Elevation);
                        var end = new XYZ(bx, by, level.Elevation);
                        var line = Line.CreateBound(start, end);

                        var wall = Wall.Create(doc, line, wallType.Id, level.Id, 10.0, 0, false, false);
                        wallIds.Add(wall.Id.Value);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.Message);
                    }
                }

                txn.Commit();
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["wall_ids"] = wallIds,
                ["count"] = wallIds.Count,
                ["errors"] = errors.Count > 0 ? errors : null,
            };
        }
    }
}
