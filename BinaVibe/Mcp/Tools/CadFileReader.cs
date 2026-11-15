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
        public double Z { get; set; }
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

    /// <summary>
    /// Text-only read: TEXT/MTEXT/ATTRIB entities (including text nested in block
    /// references) plus the file's millimetres-per-unit.
    ///
    /// It skips line/block *extraction*, but NOT the parse: both this and
    /// <see cref="CadFileReader.Extract"/> run a full DwgReader/DxfReader.Read,
    /// so a large drawing still costs the same I/O and CPU time on the Revit
    /// thread. Do not treat this as a cheap header probe.
    /// </summary>
    public class CadTextReadResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public double UnitToMm { get; set; } = 1.0;
        public bool UnitsKnown { get; set; }
        public bool UnitsInferred { get; set; }
        public string? SourceWarning { get; set; }
        public List<CadText> Texts { get; set; } = new();
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

            // Text can live in model space, inside an INSERT's block, or in the
            // INSERT's ATTRIB records. One walker handles all three so the full
            // and text-only readers return the same label set.
            CollectTexts(doc, result.Texts);

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
        /// Read TEXT/MTEXT/ATTRIB entities (including text nested in INSERT blocks)
        /// and the file's unit scale, for linked/imported CAD whose text Revit's
        /// ImportInstance API cannot expose. This still parses the whole file.
        /// </summary>
        public static CadTextReadResult ReadText(string filePath)
        {
            var result = new CadTextReadResult();

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
                // Fully-qualified: BinaVibe.Mcp.Tools has its own Revit-based static
                // DwgReader that would otherwise shadow ACadSharp's.
                doc = ext == ".dwg"
                    ? ACadSharp.IO.DwgReader.Read(filePath, null)
                    : ACadSharp.IO.DxfReader.Read(filePath);
            }
            catch (Exception ex)
            {
                result.Error = $"Could not read file: {ex.Message}";
                return result;
            }

            result.Ok = true;
            var mm = Cad2Bim.Units.FromHeader(doc);   // reuses the shared unit table
            if (mm.HasValue)
            {
                result.UnitToMm = mm.Value;
                result.UnitsKnown = true;
            }
            else
            {
                // The header is only a claim; for a unitless file the drawing's
                // extents make a better guess than assuming millimetres.
                result.UnitToMm = InferUnitToMm(doc);
                result.UnitsInferred = true;
            }
            result.SourceWarning = DetectSource(doc).Warning;

            CollectTexts(doc, result.Texts);
            return result;
        }

        /// <summary>Depth guard for self-referencing/recursive block definitions.</summary>
        private const int MaxBlockDepth = 8;

        private static void CollectTexts(CadDocument doc, List<CadText> texts)
        {
            foreach (var entity in doc.Entities)
                CollectTexts(entity, Array.Empty<CSMath.Transform>(), texts, 0);
        }

        /// <summary>
        /// Walk one entity and any block it inserts. <paramref name="transforms"/>
        /// is the chain of INSERT transforms from model space down to this entity
        /// (outermost first); text points are transformed inner-to-outer so nested
        /// block text lands in the same coordinate space as top-level text.
        /// ATTRIB records already carry model-space coordinates at the top level,
        /// so only the parent chain is applied to them.
        /// </summary>
        private static void CollectTexts(Entity entity, IReadOnlyList<CSMath.Transform> transforms,
                                         List<CadText> texts, int depth)
        {
            switch (entity)
            {
                // Block definitions carry attribute templates (ATTDEF). Their
                // visible values are the INSERT's ATTRIB records, collected below.
                case AttributeDefinition _:
                    break;

                // AttributeEntity derives from TextEntity, so it must match first.
                case AttributeEntity attribute:
                    var attributePoint = ApplyTransforms(attribute.InsertPoint, transforms);
                    texts.Add(new CadText
                    {
                        Type = "ATTRIB",
                        Content = AttributeContent(attribute),
                        X = attributePoint.X,
                        Y = attributePoint.Y,
                        Z = attributePoint.Z,
                        Layer = attribute.Layer?.Name ?? "",
                        Height = attribute.Height > 0 ? attribute.Height : null,
                    });
                    break;

                case TextEntity text:
                    var textPoint = ApplyTransforms(text.InsertPoint, transforms);
                    texts.Add(new CadText
                    {
                        Type = "TEXT",
                        Content = text.Value ?? "",
                        X = textPoint.X,
                        Y = textPoint.Y,
                        Z = textPoint.Z,
                        Layer = text.Layer?.Name ?? "",
                        Height = text.Height > 0 ? text.Height : null,
                    });
                    break;

                case MText mtext:
                    var mtextPoint = ApplyTransforms(mtext.InsertPoint, transforms);
                    texts.Add(new CadText
                    {
                        Type = "MTEXT",
                        Content = PlainText(mtext),
                        X = mtextPoint.X,
                        Y = mtextPoint.Y,
                        Z = mtextPoint.Z,
                        Layer = mtext.Layer?.Name ?? "",
                        Height = mtext.Height > 0 ? mtext.Height : null,
                    });
                    break;

                case Insert insert when depth < MaxBlockDepth:
                    var chain = new List<CSMath.Transform>(transforms.Count + 1);
                    chain.AddRange(transforms);
                    chain.Add(insert.GetTransform());

                    // ATTRIB values are block metadata (beam size, door type...).
                    // Their points are already in the parent's coordinate space,
                    // so do NOT apply this INSERT's own transform to them.
                    //
                    // ACadSharp can expose the block's ATTDEF defaults alongside
                    // the real ATTRIB values, with the same tag. Walk backwards
                    // (real values come after defaults) and keep the last value
                    // for each tag.
                    var attributes = insert.Attributes.ToList();
                    var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = attributes.Count - 1; i >= 0; i--)
                    {
                        var tag = attributes[i].Tag;
                        if (!string.IsNullOrEmpty(tag) && !seenTags.Add(tag)) continue;
                        CollectTexts(attributes[i], transforms, texts, depth + 1);
                    }

                    var block = insert.Block;
                    if (block?.Entities == null) break;
                    foreach (var child in block.Entities)
                        CollectTexts(child, chain, texts, depth + 1);
                    break;
            }
        }

        private static CSMath.XYZ ApplyTransforms(CSMath.XYZ point,
                                                  IReadOnlyList<CSMath.Transform> transforms)
        {
            for (int i = transforms.Count - 1; i >= 0; i--)
                point = transforms[i].ApplyTransform(point, true);
            return point;
        }

        private static string AttributeContent(AttributeEntity attribute)
        {
            if (attribute.MText != null)
            {
                var mtext = PlainText(attribute.MText);
                if (!string.IsNullOrWhiteSpace(mtext)) return mtext;
            }
            return attribute.Value ?? "";
        }

        private static string PlainText(MText mtext)
        {
            try
            {
                // Value is the raw DXF string ("{\fArial|b0;B1}\P300x600");
                // PlainText strips the formatting codes and turns \P into a newline.
                return mtext.PlainText ?? mtext.Value ?? "";
            }
            catch
            {
                return mtext.Value ?? "";
            }
        }

        private static double InferUnitToMm(CadDocument doc)
        {
            try
            {
                var bounds = doc.ModelSpace?.GetBoundingBox();
                if (bounds.HasValue)
                {
                    double width = bounds.Value.Max.X - bounds.Value.Min.X;
                    double height = bounds.Value.Max.Y - bounds.Value.Min.Y;
                    if (width > 0 && height > 0)
                        return Cad2Bim.Units.InferScaleFromExtents(width, height);
                }
            }
            catch
            {
                // A malformed entity must not lose the text we already have.
            }
            return 1.0;
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

            return extraction.Lines
                .Where(l => filters.Any(f =>
                    l.Layer.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
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
                        ["z"] = t.Z,
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
                    ["z"] = t.Z,
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
