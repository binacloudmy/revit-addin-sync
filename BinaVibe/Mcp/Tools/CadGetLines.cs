// CadGetLines — cad_get_lines MCP tool. Extracts line/arc geometry from an
// attached DWG/DXF via ACadSharp for the CAD classifier and viewer.
//
// Like CadLoad.cs, uses full ACadSharp.IO.* names to avoid collision with
// BinaVibe.Mcp.Tools.DwgReader.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACadSharp;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadGetLines
    {
        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var path = DwgScratchCache.GetPath(dwgRef);
            if (path == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"attachment '{dwgRef}' not found" };

            var layerFilter = ArgsHelp.GetString(args, "layer_filter");

            try
            {
                return Extract(path, layerFilter);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private static Dictionary<string, object?> Extract(string path, string? layerFilter)
        {
            CadDocument doc;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dwg")
            {
                using var reader = new ACadSharp.IO.DwgReader(path);
                doc = reader.Read();
            }
            else if (ext == ".dxf")
            {
                using var reader = new ACadSharp.IO.DxfReader(path);
                doc = reader.Read();
            }
            else
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"unsupported format: {ext}" };
            }

            var entities = doc.ModelSpace.Entities.ToList();
            if (!string.IsNullOrEmpty(layerFilter))
            {
                entities = entities.Where(e =>
                    e.Layer.Name.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            var lines = new List<Dictionary<string, object?>>();
            var arcs = new List<Dictionary<string, object?>>();

            foreach (var entity in entities)
            {
                switch (entity)
                {
                    case ACadSharp.Entities.Line line:
                        lines.Add(new Dictionary<string, object?>
                        {
                            ["x1"] = Math.Round(line.StartPoint.X, 1),
                            ["y1"] = Math.Round(line.StartPoint.Y, 1),
                            ["z1"] = Math.Round(line.StartPoint.Z, 1),
                            ["x2"] = Math.Round(line.EndPoint.X, 1),
                            ["y2"] = Math.Round(line.EndPoint.Y, 1),
                            ["z2"] = Math.Round(line.EndPoint.Z, 1),
                            ["layer"] = line.Layer.Name,
                        });
                        break;
                    case ACadSharp.Entities.Arc arc:
                        arcs.Add(new Dictionary<string, object?>
                        {
                            ["cx"] = Math.Round(arc.Center.X, 1),
                            ["cy"] = Math.Round(arc.Center.Y, 1),
                            ["r"] = Math.Round(arc.Radius, 1),
                            ["start_deg"] = Math.Round(arc.StartAngle * 180 / Math.PI, 1),
                            ["end_deg"] = Math.Round(arc.EndAngle * 180 / Math.PI, 1),
                            ["layer"] = arc.Layer.Name,
                        });
                        break;
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["lines"] = lines,
                ["arcs"] = arcs,
                ["line_count"] = lines.Count,
                ["arc_count"] = arcs.Count,
            };
        }
    }
}
