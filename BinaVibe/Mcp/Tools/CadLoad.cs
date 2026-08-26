// CadLoad — cad_load MCP tool. Reads a standalone attached DWG/DXF straight
// off disk via ACadSharp (no Revit link needed) and hands back layers, entity
// counts, and bounds for the CAD-to-BIM viewer's cad-to-bim-viewer pipeline.
//
// ACadSharp.IO also defines a DwgReader class; this file lives in the same
// namespace as BinaVibe.Mcp.Tools.DwgReader (the Revit-API-based one used by
// get_dwg_summary etc.), so the ACadSharp readers are referenced with their
// full names below to avoid resolving to the wrong type.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACadSharp;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadLoad
    {
        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var path = DwgScratchCache.GetPath(dwgRef);
            if (path == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"attachment '{dwgRef}' not found" };

            try
            {
                return Extract(path);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private static Dictionary<string, object?> Extract(string path)
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

            var layers = doc.Layers.Select(l => l.Name).ToList();
            var entities = doc.ModelSpace.Entities.ToList();
            var entityCounts = entities
                .GroupBy(e => e.ObjectName)
                .ToDictionary(g => g.Key, g => g.Count());

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var entity in entities)
            {
                if (entity is ACadSharp.Entities.Line line)
                {
                    UpdateBounds(ref minX, ref minY, ref maxX, ref maxY, line.StartPoint.X, line.StartPoint.Y);
                    UpdateBounds(ref minX, ref minY, ref maxX, ref maxY, line.EndPoint.X, line.EndPoint.Y);
                }
            }

            var boundsValid = minX < double.MaxValue;
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["layers"] = layers,
                ["entity_counts"] = entityCounts,
                ["bounds_mm"] = boundsValid
                    ? new Dictionary<string, object?> { ["min"] = new[] { minX, minY }, ["max"] = new[] { maxX, maxY } }
                    : null,
                ["source_app"] = DetectSource(doc),
            };
        }

        private static void UpdateBounds(ref double minX, ref double minY, ref double maxX, ref double maxY, double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        private static string DetectSource(CadDocument doc)
        {
            try
            {
                foreach (var cls in doc.Classes)
                {
                    if (cls.DxfName.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase))
                        return "civil3d";
                    if (cls.DxfName.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase))
                        return "autocad_architecture";
                }
            }
            catch { /* unreadable class table — fall through to plain */ }
            return "plain_autocad";
        }
    }
}
