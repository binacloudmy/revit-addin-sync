// DWG/DXF reading via ACadSharp — extracts block names, text content,
// attributes that Revit's ImportInstance API cannot access.
//
// Used for CAD attachments in the Copilot pane. The agent gets richer data
// than Revit's geometry-only extraction: block names for door/window type
// mapping, text content for room names/grid labels, block attributes for
// metadata (width, height, fire rating).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>
    /// Source application detection result.
    /// </summary>
    public class CadSourceInfo
    {
        public string Source { get; set; } = "plain_autocad";
        public bool Supported { get; set; } = true;
        public string? Warning { get; set; }
        public List<string> DetectedClasses { get; set; } = new();
    }

    /// <summary>
    /// Block instance extracted from CAD.
    /// </summary>
    public class CadBlock
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Rotation { get; set; }
        public string Layer { get; set; } = "";
        public double XScale { get; set; } = 1;
        public double YScale { get; set; } = 1;
        public Dictionary<string, string> Attributes { get; set; } = new();
    }

    /// <summary>
    /// Text entity extracted from CAD.
    /// </summary>
    public class CadText
    {
        public string Type { get; set; } = "TEXT";
        public string Content { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public string Layer { get; set; } = "";
        public double? Height { get; set; }
    }

    /// <summary>
    /// A line segment extracted from CAD (in CAD units, typically mm).
    /// </summary>
    public class CadLine
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double Z1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double Z2 { get; set; }
        public string Layer { get; set; } = "";
    }

    /// <summary>
    /// Full extraction result from a CAD file.
    /// </summary>
    public class CadExtractionResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public CadSourceInfo? SourceInfo { get; set; }
        public List<string> Layers { get; set; } = new();
        public List<CadBlock> Blocks { get; set; } = new();
        public List<CadText> Texts { get; set; } = new();
        public List<CadLine> Lines { get; set; } = new();
        public int LineCount { get; set; }
        public int CircleCount { get; set; }
        public int ArcCount { get; set; }
        public int DimensionCount { get; set; }
        public int HatchCount { get; set; }
    }

    public static class CadFileReader
    {
        /// <summary>
        /// Detect source application from DWG/DXF classes.
        /// Returns warning for unsupported vertical apps (Architecture, Civil 3D, MEP).
        /// </summary>
        public static CadSourceInfo DetectSource(CadDocument doc)
        {
            var result = new CadSourceInfo();
            var classes = new List<string>();

            try
            {
                foreach (var cls in doc.Classes)
                {
                    classes.Add(cls.DxfName);
                }
            }
            catch { }

            result.DetectedClasses = classes.Take(20).ToList();

            var aecObjects = classes.Where(c => c.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase)).ToList();
            var aeccObjects = classes.Where(c => c.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase)).ToList();
            var aecbObjects = classes.Where(c => c.StartsWith("AECB_", StringComparison.OrdinalIgnoreCase)).ToList();

            if (aeccObjects.Any())
            {
                result.Source = "civil3d";
                result.Supported = false;
                result.Warning = "Civil 3D file detected. Pipes, surfaces, alignments will be lost. " +
                    "Export to plain DWG first using EXPORTTOAUTOCAD command in Civil 3D.";
            }
            else if (aecObjects.Any())
            {
                result.Source = "autocad_architecture";
                result.Supported = false;
                result.Warning = "AutoCAD Architecture file detected. AEC walls, doors, and spaces will lose " +
                    "semantic data. Export to plain DWG first using EXPORTTOAUTOCAD command.";
            }
            else if (aecbObjects.Any())
            {
                result.Source = "mep";
                result.Supported = false;
                result.Warning = "AutoCAD MEP file detected. Ducts and pipes will be lost. " +
                    "Export to plain DWG first using EXPORTTOAUTOCAD command.";
            }

            return result;
        }

        /// <summary>
        /// Extract full CAD data from a DWG or DXF file.
        /// </summary>
        public static CadExtractionResult Extract(string filePath)
        {
            var result = new CadExtractionResult();

            // Validate the extension BEFORE existence: an unsupported type is
            // "unsupported" regardless of whether the path happens to exist.
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".dwg" && ext != ".dxf")
            {
                result.Error = $"Unsupported file type: {ext}";
                return result;
            }

            if (!File.Exists(filePath))
            {
                result.Error = $"File not found: {filePath}";
                return result;
            }

            CadDocument doc;
            try
            {
                if (ext == ".dwg")
                    // Fully-qualified: BinaVibe.Mcp.Tools has its OWN static DwgReader
                    // (Revit-based) that would otherwise shadow ACadSharp's. ACadSharp
                    // 1.1.19 also has no single-arg Read — it needs a
                    // NotificationEventHandler (nullable).
                    doc = ACadSharp.IO.DwgReader.Read(filePath, null);
                else
                    doc = ACadSharp.IO.DxfReader.Read(filePath);
            }
            catch (Exception ex)
            {
                result.Error = $"Could not read file: {ex.Message}";
                return result;
            }

            result.Ok = true;
            result.SourceInfo = DetectSource(doc);

            // Layers
            result.Layers = doc.Layers.Select(l => l.Name).ToList();

            // Entities
            foreach (var entity in doc.Entities)
            {
                switch (entity)
                {
                    case Insert insert:
                        var block = new CadBlock
                        {
                            Name = insert.Block?.Name ?? "",
                            X = Math.Round(insert.InsertPoint.X, 2),
                            Y = Math.Round(insert.InsertPoint.Y, 2),
                            Z = Math.Round(insert.InsertPoint.Z, 2),
                            Rotation = Math.Round(insert.Rotation * 180 / Math.PI, 2),
                            Layer = insert.Layer?.Name ?? "",
                            XScale = Math.Round(insert.XScale, 3),
                            YScale = Math.Round(insert.YScale, 3),
                        };
                        // Block attributes
                        foreach (var att in insert.Attributes)
                        {
                            if (!string.IsNullOrEmpty(att.Tag))
                                block.Attributes[att.Tag] = att.Value ?? "";
                        }
                        result.Blocks.Add(block);
                        break;

                    case TextEntity text:
                        result.Texts.Add(new CadText
                        {
                            Type = "TEXT",
                            Content = text.Value ?? "",
                            X = Math.Round(text.InsertPoint.X, 2),
                            Y = Math.Round(text.InsertPoint.Y, 2),
                            Layer = text.Layer?.Name ?? "",
                            Height = text.Height > 0 ? Math.Round(text.Height, 2) : null,
                        });
                        break;

                    case MText mtext:
                        result.Texts.Add(new CadText
                        {
                            Type = "MTEXT",
                            Content = mtext.Value ?? "",
                            X = Math.Round(mtext.InsertPoint.X, 2),
                            Y = Math.Round(mtext.InsertPoint.Y, 2),
                            Layer = mtext.Layer?.Name ?? "",
                            Height = mtext.Height > 0 ? Math.Round(mtext.Height, 2) : null,
                        });
                        break;

                    case Line line:
                        result.Lines.Add(new CadLine
                        {
                            X1 = line.StartPoint.X,
                            Y1 = line.StartPoint.Y,
                            Z1 = line.StartPoint.Z,
                            X2 = line.EndPoint.X,
                            Y2 = line.EndPoint.Y,
                            Z2 = line.EndPoint.Z,
                            Layer = line.Layer?.Name ?? "",
                        });
                        result.LineCount++;
                        break;

                    case LwPolyline lwp:
                        ExtractPolylineSegments(lwp, result);
                        break;

                    case Polyline2D p2d:
                        ExtractPolyline2DSegments(p2d, result);
                        break;

                    // Arc derives from Circle in ACadSharp, so the more-derived
                    // Arc case MUST come first — otherwise Circle swallows arcs
                    // (miscounts them, and makes the Arc case unreachable: CS8120).
                    case Arc _:
                        result.ArcCount++;
                        break;

                    case Circle _:
                        result.CircleCount++;
                        break;

                    case Dimension _:
                        result.DimensionCount++;
                        break;

                    case Hatch _:
                        result.HatchCount++;
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Extract line segments from an LwPolyline (lightweight polyline).
        /// Each straight segment becomes a CadLine. Bulge (arc) segments are
        /// approximated as straight chords (v1 simplification).
        /// </summary>
        private static void ExtractPolylineSegments(LwPolyline lwp, CadExtractionResult result)
        {
            var layer = lwp.Layer?.Name ?? "";
            double elevation = lwp.Elevation; // LwPolyline has a single Z elevation
            var verts = lwp.Vertices.ToList();
            if (verts.Count < 2) return;

            for (int i = 0; i < verts.Count - 1; i++)
            {
                var v1 = verts[i];
                var v2 = verts[i + 1];
                result.Lines.Add(new CadLine
                {
                    X1 = v1.Location.X,
                    Y1 = v1.Location.Y,
                    Z1 = elevation,
                    X2 = v2.Location.X,
                    Y2 = v2.Location.Y,
                    Z2 = elevation,
                    Layer = layer,
                });
                result.LineCount++;
            }
            // Close if flagged
            if (lwp.IsClosed && verts.Count > 2)
            {
                var first = verts[0];
                var last = verts[verts.Count - 1];
                result.Lines.Add(new CadLine
                {
                    X1 = last.Location.X,
                    Y1 = last.Location.Y,
                    Z1 = elevation,
                    X2 = first.Location.X,
                    Y2 = first.Location.Y,
                    Z2 = elevation,
                    Layer = layer,
                });
                result.LineCount++;
            }
        }

        /// <summary>
        /// Extract line segments from a Polyline2D.
        /// </summary>
        private static void ExtractPolyline2DSegments(Polyline2D p2d, CadExtractionResult result)
        {
            var layer = p2d.Layer?.Name ?? "";
            double elevation = p2d.Elevation; // Polyline2D has a single Z elevation
            var verts = p2d.Vertices.ToList();
            if (verts.Count < 2) return;

            for (int i = 0; i < verts.Count - 1; i++)
            {
                var v1 = verts[i];
                var v2 = verts[i + 1];
                result.Lines.Add(new CadLine
                {
                    X1 = v1.Location.X,
                    Y1 = v1.Location.Y,
                    Z1 = elevation,
                    X2 = v2.Location.X,
                    Y2 = v2.Location.Y,
                    Z2 = elevation,
                    Layer = layer,
                });
                result.LineCount++;
            }
            // Close if flagged
            if (p2d.IsClosed && verts.Count > 2)
            {
                var first = verts[0];
                var last = verts[verts.Count - 1];
                result.Lines.Add(new CadLine
                {
                    X1 = last.Location.X,
                    Y1 = last.Location.Y,
                    Z1 = elevation,
                    X2 = first.Location.X,
                    Y2 = first.Location.Y,
                    Z2 = elevation,
                    Layer = layer,
                });
                result.LineCount++;
            }
        }

        /// <summary>
        /// Get lines from specific layer(s) for wall extraction.
        /// Returns lines in CAD units (typically mm).
        /// </summary>
        public static List<CadLine> GetLinesForLayer(string filePath, string? layerFilter)
        {
            var extraction = Extract(filePath);
            if (!extraction.Ok) return new List<CadLine>();

            if (string.IsNullOrEmpty(layerFilter))
                return extraction.Lines;

            // Support comma-separated or single layer filter
            var filters = layerFilter.Split(',')
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToList();

            // Match layer names more precisely:
            // - Exact match: "WALL" matches "WALL"
            // - Filter as prefix: "WALL" matches "WALL-EXT", "WALL_INT"
            // - Filter as suffix: "WALL" matches "A-WALL", "S-WALL"
            // - Filter as word: "WALL" matches "A-WALL-EXT"
            // But NOT: "WALL" should NOT match "FURNITURE_WALL_MOUNT" where WALL is in the middle
            //          without delimiters on both sides matching the filter
            return extraction.Lines
                .Where(l => filters.Any(f => LayerMatches(l.Layer, f)))
                .ToList();
        }

        /// <summary>
        /// Check if a layer name matches a filter.
        /// Matches if: exact, filter is prefix/suffix, or filter appears as a delimited word.
        /// </summary>
        private static bool LayerMatches(string layer, string filter)
        {
            if (string.Equals(layer, filter, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check if filter appears as a delimited segment
            // Delimiters: - _ space
            var delims = new[] { '-', '_', ' ' };
            var layerUpper = layer.ToUpperInvariant();
            var filterUpper = filter.ToUpperInvariant();

            int idx = layerUpper.IndexOf(filterUpper);
            if (idx < 0) return false;

            // Check character before (must be start or delimiter)
            bool startOk = idx == 0 || delims.Contains(layer[idx - 1]);
            // Check character after (must be end or delimiter)
            int endIdx = idx + filter.Length;
            bool endOk = endIdx >= layer.Length || delims.Contains(layer[endIdx]);

            return startOk && endOk;
        }

        /// <summary>
        /// Get block census — count of each unique block name.
        /// For door/window/furniture type mapping.
        /// </summary>
        public static Dictionary<string, object?> GetBlockCensus(string filePath)
        {
            var extraction = Extract(filePath);
            if (!extraction.Ok)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = extraction.Error };

            var counts = extraction.Blocks
                .GroupBy(b => b.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => new Dictionary<string, object> { ["name"] = g.Key, ["count"] = g.Count() })
                .ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["block_census"] = counts,
                ["total_blocks"] = extraction.Blocks.Count,
                ["unique_blocks"] = counts.Count,
                ["source_info"] = extraction.SourceInfo,
            };
        }

        /// <summary>
        /// Get text content grouped by layer.
        /// For room names, grid labels.
        /// </summary>
        public static Dictionary<string, object?> GetTextByLayer(string filePath)
        {
            var extraction = Extract(filePath);
            if (!extraction.Ok)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = extraction.Error };

            var byLayer = extraction.Texts
                .GroupBy(t => t.Layer)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => new Dictionary<string, object?>
                    {
                        ["content"] = t.Content,
                        ["x"] = t.X,
                        ["y"] = t.Y,
                    }).ToList() as object
                );

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["text_by_layer"] = byLayer,
                ["total_texts"] = extraction.Texts.Count,
                ["source_info"] = extraction.SourceInfo,
            };
        }

        /// <summary>
        /// Full extraction as dictionary for JSON serialization.
        /// </summary>
        public static Dictionary<string, object?> ExtractToDict(string filePath)
        {
            var extraction = Extract(filePath);
            if (!extraction.Ok)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = extraction.Error };

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["source_info"] = new Dictionary<string, object?>
                {
                    ["source"] = extraction.SourceInfo?.Source,
                    ["supported"] = extraction.SourceInfo?.Supported,
                    ["warning"] = extraction.SourceInfo?.Warning,
                },
                ["layers"] = extraction.Layers,
                ["blocks"] = extraction.Blocks.Select(b => new Dictionary<string, object?>
                {
                    ["name"] = b.Name,
                    ["x"] = b.X,
                    ["y"] = b.Y,
                    ["rotation"] = b.Rotation,
                    ["layer"] = b.Layer,
                    ["xscale"] = b.XScale,
                    ["yscale"] = b.YScale,
                    ["attributes"] = b.Attributes.Count > 0 ? b.Attributes : null,
                }).ToList(),
                ["texts"] = extraction.Texts.Select(t => new Dictionary<string, object?>
                {
                    ["type"] = t.Type,
                    ["content"] = t.Content,
                    ["x"] = t.X,
                    ["y"] = t.Y,
                    ["layer"] = t.Layer,
                }).ToList(),
                ["summary"] = new Dictionary<string, int>
                {
                    ["layers"] = extraction.Layers.Count,
                    ["blocks"] = extraction.Blocks.Count,
                    ["texts"] = extraction.Texts.Count,
                    ["lines"] = extraction.LineCount,
                    ["circles"] = extraction.CircleCount,
                    ["arcs"] = extraction.ArcCount,
                    ["dimensions"] = extraction.DimensionCount,
                    ["hatches"] = extraction.HatchCount,
                },
            };
        }
    }
}
