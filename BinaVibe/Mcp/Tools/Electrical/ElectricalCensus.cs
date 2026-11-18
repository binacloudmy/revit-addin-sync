#nullable enable
// Electrical census — phase 0 of the CAD electrical to BIM work (spec 2026-11-17 §5.1, §5.2).
//
// One walk of a DWG/DXF, counted: which block classes are inserted where, what each layer
// holds, and the drawing facts (units, AEC warning, wall-like layers, plan clusters, legend)
// that decide whether the later phases can work on a drawing at all. It measures; it does not
// decide what a block or a layer MEANS. That is the AI's job in later phases, so nothing here
// guesses at meaning, and no instance coordinate leaves this file.
//
// Revit-free and WPF-free: Cad2Bim.* (the CAD to BIM engine), CadSourceVerdict and System only,
// so it is linked into Tests and the headless CLI as well as compiled into the add-in. It must
// also compile on net48 (Revit 2023/2024): no Math.Clamp, no ^1 indices, no char overloads of
// StartsWith/EndsWith/Contains, no string.Join(char, ...).
//
// Lengths and points are in millimetres once Units has resolved the drawing's scale.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BinaVibe.Mcp.Tools.Electrical
{
    // Inside the namespace, so a type of the same name further up BinaVibe.* cannot shadow them.
    using ACadSharp;
    using Cad2Bim;
    using Cad2Bim.Services;

    /// <summary>Caps and distances for one census run. Millimetres.</summary>
    internal sealed class CensusOptions
    {
        public int MaxBlockClasses = 150;
        public int MaxLayers = 150;
        public int MaxLegendRows = 40;
        public int MaxPlans = 60;

        public double NearWallMm = 400;
        public double TextReachFactor = 1.5;
        public double LongOpenPolylineMm = 1000;

        /// <summary>A layer with more segments than this is not tested for wall pairs: the
        /// pairing is near-linear but the merge is not free, and such a layer is a hatch or a
        /// survey, not a wall layer.</summary>
        public int MaxWallTestSegments = 80000;
        public int MinWallTestSegments = 8;
    }

    /// <summary>The drawing as a whole: units, size, how its inserts break down.</summary>
    internal sealed class CensusDrawing
    {
        public double MmPerUnit = 1.0;
        public string UnitsSource = "inferred";
        public string HeaderUnits = "";
        public double? HeaderMmPerUnit;
        public double WidthM;
        public double HeightM;
        public int Entities;
    }

    internal sealed class CensusCount
    {
        public string Name = "";
        public int Count;
    }

    internal sealed class CensusAttribute
    {
        public string Tag = "";
        public List<string> Samples = new List<string>();
    }

    /// <summary>Every insert of one block, however many times and wherever it is placed.</summary>
    internal sealed class CensusBlockClass
    {
        public string Key = "";
        public bool Anonymous;
        public List<string> RecordNames = new List<string>();
        public int Count;
        public List<CensusCount> Layers = new List<CensusCount>();
        public int MinDepth;
        public int MaxDepth;
        public double WidthMm;
        public double HeightMm;
        public bool IsContainer;
        public bool UnresolvedXref;
        public List<CensusAttribute> Attributes = new List<CensusAttribute>();
    }

    /// <summary>What one layer holds. Lengths in metres.</summary>
    internal sealed class CensusLayer
    {
        public string Name = "";
        public int Lines;
        public int OpenPolylines;
        public int ClosedPolylines;
        public int Arcs;
        public int Texts;
        public int Inserts;
        public int HatchLoops;
        public int DimensionPieces;
        public double LengthM;
        public double LooseLengthM;
        public double InBlocksLengthM;
        public int OpenPolylinesOver1m;
        public double AxisAlignedShare;
    }

    internal sealed class CensusPlan
    {
    }

    internal sealed class CensusLegendRow
    {
    }

    internal sealed class CensusTotals
    {
        public int Inserts;
        public int TopLevel;
        public int Nested;
        public int Named;
        public int Anonymous;
        public List<string> UnresolvedXrefs = new List<string>();
        public int BoundXrefLayers;
    }

    internal sealed class ElectricalCensusResult
    {
        public CensusDrawing Drawing = new CensusDrawing();
        public List<CensusBlockClass> Blocks = new List<CensusBlockClass>();
        public List<CensusLayer> Layers = new List<CensusLayer>();
        public List<CensusPlan> Plans = new List<CensusPlan>();
        public List<CensusLegendRow> Legend = new List<CensusLegendRow>();
        public CensusTotals Totals = new CensusTotals();
        public bool BlocksTruncated, LayersTruncated, PlansTruncated, LegendTruncated, SymbolsAreExploded;
        public Dictionary<string, double> TimingsSeconds = new Dictionary<string, double>();
    }

    internal static class ElectricalCensus
    {
        /// <summary>Reads a DWG or DXF and takes its census.</summary>
        public static ElectricalCensusResult Read(string path, CensusOptions? options = null)
        {
            var clock = Stopwatch.StartNew();
            CadDocument document = CadRenderSource.Read(path);
            double read = clock.Elapsed.TotalSeconds;

            ElectricalCensusResult result = Build(document, options);
            result.TimingsSeconds["read"] = read;
            return result;
        }

        public static ElectricalCensusResult Build(CadDocument document, CensusOptions? options = null)
        {
            CensusOptions o = options ?? new CensusOptions();
            var result = new ElectricalCensusResult();
            var clock = Stopwatch.StartNew();

            var sink = new CensusSink();
            CadRenderSource.Walk(document, sink);

            double scale = Units.Resolve(document, sink.ExtentAsSegments());
            sink.Rescale(scale);

            double? header = Units.FromHeader(document);
            result.Drawing.MmPerUnit = scale;
            result.Drawing.HeaderMmPerUnit = header;
            result.Drawing.HeaderUnits = document.Header.InsUnits.ToString();
            result.Drawing.UnitsSource = header.HasValue && header.Value == scale ? "header" : "inferred";
            result.Drawing.Entities = document.Entities.Count;
            if (sink.HasExtent)
            {
                result.Drawing.WidthM = (sink.MaxX - sink.MinX) / 1000.0;
                result.Drawing.HeightM = (sink.MaxY - sink.MinY) / 1000.0;
            }

            result.TimingsSeconds["walk"] = clock.Elapsed.TotalSeconds;

            BuildBlockClasses(sink, o, result);
            BuildLayers(sink, o, result);
            BuildTotals(sink, result);

            return result;
        }

        // ---------------------------------------------------------------- block classes

        private static void BuildBlockClasses(CensusSink sink, CensusOptions o, ElectricalCensusResult result)
        {
            var classes = new List<CensusBlockClass>();

            foreach (var group in sink.Instances.GroupBy(instance => instance.Key, StringComparer.Ordinal))
            {
                List<Instance> list = group.ToList();
                CadBlockInfo info = list[0].Block;

                var block = new CensusBlockClass
                {
                    Key = group.Key,
                    Anonymous = info.IsAnonymous,
                    Count = list.Count,
                    MinDepth = list.Min(instance => instance.Depth),
                    MaxDepth = list.Max(instance => instance.Depth),
                    WidthMm = Math.Round(Median(list.Select(instance => instance.WidthMm)), 1),
                    HeightMm = Math.Round(Median(list.Select(instance => instance.HeightMm)), 1),
                    IsContainer = list.Any(instance => instance.Block.NestedInserts > 0),
                    UnresolvedXref = list.Any(instance => instance.Block.IsUnresolvedXref),
                };

                block.Layers = list
                    .GroupBy(instance => instance.Layer, StringComparer.Ordinal)
                    .Select(byLayer => new CensusCount { Name = byLayer.Key, Count = byLayer.Count() })
                    .OrderByDescending(count => count.Count).ThenBy(count => count.Name, StringComparer.Ordinal)
                    .Take(5)
                    .ToList();

                if (info.IsAnonymous)
                {
                    block.RecordNames = list.Select(instance => instance.Block.RecordName)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .Take(3)
                        .ToList();
                }

                var attributes = new Dictionary<string, CensusAttribute>(StringComparer.OrdinalIgnoreCase);
                foreach (Instance instance in list)
                {
                    foreach (KeyValuePair<string, string> pair in instance.Attributes)
                    {
                        if (!attributes.TryGetValue(pair.Key, out CensusAttribute? attribute))
                        {
                            attribute = new CensusAttribute { Tag = pair.Key };
                            attributes[pair.Key] = attribute;
                        }

                        string value = Truncate(pair.Value, 60);
                        if (attribute.Samples.Count < 3 && value.Length > 0 &&
                            !attribute.Samples.Contains(value, StringComparer.Ordinal))
                        {
                            attribute.Samples.Add(value);
                        }
                    }
                }
                block.Attributes = attributes.Values.OrderBy(a => a.Tag, StringComparer.Ordinal).ToList();

                classes.Add(block);
            }

            classes = classes
                .OrderByDescending(block => block.Count)
                .ThenBy(block => block.Key, StringComparer.Ordinal)
                .ToList();

            result.BlocksTruncated = classes.Count > o.MaxBlockClasses;
            result.Blocks = classes.Take(o.MaxBlockClasses).ToList();
        }

        /// <summary>
        /// The name a block class is filed under. A named block is its name. Anonymous blocks
        /// ("*U12", "*U13", ...) are a new record for every copy, so they are grouped by shape
        /// instead: what they are made of and how big they are, to the nearest 10 mm.
        /// </summary>
        internal static string ClassKey(CadBlockInfo block, double mmPerUnit)
        {
            if (!block.IsAnonymous) return block.Name;

            var parts = block.EntityCounts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value)
                .ToList();
            parts.Add("w=" + RoundTo10(block.Width * mmPerUnit).ToString(System.Globalization.CultureInfo.InvariantCulture));
            parts.Add("h=" + RoundTo10(block.Height * mmPerUnit).ToString(System.Globalization.CultureInfo.InvariantCulture));

            byte[] hash;
            using (SHA1 sha = SHA1.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";", parts)));
            }

            return "anon:" + BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
        }

        private static double RoundTo10(double mm) => Math.Round(mm / 10.0) * 10.0;

        // ---------------------------------------------------------------- layers

        private static void BuildLayers(CensusSink sink, CensusOptions o, ElectricalCensusResult result)
        {
            var layers = new List<CensusLayer>();

            foreach (LayerTally tally in sink.Layers.Values)
            {
                double alignedTotal = tally.StraightLength + tally.ArcLength;

                layers.Add(new CensusLayer
                {
                    Name = tally.Name,
                    Lines = tally.Lines,
                    OpenPolylines = tally.OpenPolylines,
                    ClosedPolylines = tally.ClosedPolylines,
                    Arcs = tally.Arcs,
                    Texts = tally.Texts,
                    Inserts = tally.Inserts,
                    HatchLoops = tally.HatchLoops,
                    DimensionPieces = tally.DimensionPieces,
                    LengthM = Math.Round((tally.LooseLength + tally.InBlocksLength) / 1000.0, 3),
                    LooseLengthM = Math.Round(tally.LooseLength / 1000.0, 3),
                    InBlocksLengthM = Math.Round(tally.InBlocksLength / 1000.0, 3),
                    OpenPolylinesOver1m = tally.OpenRunLengths.Count(length => length > o.LongOpenPolylineMm),
                    AxisAlignedShare = alignedTotal > 0 ? Math.Round(tally.AxisAlignedLength / alignedTotal, 3) : 0,
                });
            }

            layers = layers
                .OrderByDescending(layer => layer.LengthM + layer.Inserts)
                .ThenBy(layer => layer.Name, StringComparer.Ordinal)
                .ToList();

            result.LayersTruncated = layers.Count > o.MaxLayers;
            result.Layers = layers.Take(o.MaxLayers).ToList();
        }

        // ---------------------------------------------------------------- totals

        private static void BuildTotals(CensusSink sink, ElectricalCensusResult result)
        {
            CensusTotals totals = result.Totals;
            foreach (Instance instance in sink.Instances)
            {
                totals.Inserts++;
                if (instance.Depth == 0) totals.TopLevel++; else totals.Nested++;
                if (instance.Block.IsAnonymous) totals.Anonymous++; else totals.Named++;
            }

            totals.UnresolvedXrefs = sink.Instances
                .Where(instance => instance.Block.IsUnresolvedXref)
                .Select(instance => instance.Block.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            totals.BoundXrefLayers = sink.Layers.Keys.Count(name =>
                name.IndexOf("$0$", StringComparison.Ordinal) >= 0 || name.IndexOf("|", StringComparison.Ordinal) >= 0);
        }

        // ---------------------------------------------------------------- helpers

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        private static string Truncate(string? value, int length)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string trimmed = value!.Trim();
            return trimmed.Length <= length ? trimmed : trimmed.Substring(0, length);
        }

        // ---------------------------------------------------------------- the walk

        /// <summary>One placed symbol, in millimetres once rescaled. Never leaves this file.</summary>
        private sealed class Instance
        {
            public CadBlockInfo Block = null!;
            public string Key = "";
            public string Layer = "";
            public double X, Y;
            public double WidthMm, HeightMm;
            public int Depth;
            public IReadOnlyDictionary<string, string> Attributes = null!;
        }

        private sealed class CensusText
        {
            public double X, Y, Height;
            public string Value = "";
            public string Layer = "";
        }

        /// <summary>Running counts for one layer, in drawing units until rescaled.</summary>
        private sealed class LayerTally
        {
            public string Name = "";
            public int Lines, OpenPolylines, ClosedPolylines, Arcs, Texts, Inserts, HatchLoops, DimensionPieces;
            public double LooseLength, InBlocksLength, StraightLength, ArcLength, AxisAlignedLength;
            public List<double> OpenRunLengths = new List<double>();

            /// <summary>Straight pieces from model space, blocks and proxy graphics, as
            /// (x1, y1, x2, y2). Hatch boundaries and dimension pieces are counted, not kept.</summary>
            public List<(double X1, double Y1, double X2, double Y2)> Pieces =
                new List<(double X1, double Y1, double X2, double Y2)>();
        }

        private sealed class CensusSink : ICadSink, ICadSymbolSink
        {
            public readonly Dictionary<string, LayerTally> Layers = new Dictionary<string, LayerTally>(StringComparer.Ordinal);
            public readonly List<Instance> Instances = new List<Instance>();
            public readonly List<CensusText> Texts = new List<CensusText>();

            public bool HasExtent;
            public double MinX = double.MaxValue, MinY = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue;

            // The effective layer of each open insert, outermost first. Symbol arrives in
            // pre-order, so popping to the new insert's depth leaves its parent on top.
            private readonly List<(int Depth, string Layer)> _open = new List<(int Depth, string Layer)>();

            private LayerTally Tally(string layer)
            {
                if (!Layers.TryGetValue(layer, out LayerTally? tally))
                {
                    tally = new LayerTally { Name = layer };
                    Layers[layer] = tally;
                }
                return tally;
            }

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source)
            {
                LayerTally tally = Tally(layer);

                if (source == CadSource.Hatch) { tally.HatchLoops++; return; }
                if (source == CadSource.Dimension) { tally.DimensionPieces++; return; }

                if (isClosed) tally.ClosedPolylines++;
                else if (points.Count == 2) tally.Lines++;
                else tally.OpenPolylines++;

                double run = 0;
                int last = isClosed ? points.Count : points.Count - 1;
                for (int i = 0; i < last; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double length = Math.Sqrt((dx * dx) + (dy * dy));
                    if (length <= 0) continue;

                    run += length;
                    tally.StraightLength += length;
                    if (IsAxisAligned(dx, dy)) tally.AxisAlignedLength += length;
                    tally.Pieces.Add((a.X, a.Y, b.X, b.Y));
                    Grow(a.X, a.Y);
                    Grow(b.X, b.Y);
                }

                if (source == CadSource.Geometry) tally.LooseLength += run; else tally.InBlocksLength += run;
                if (!isClosed) tally.OpenRunLengths.Add(run);
            }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source)
            {
                LayerTally tally = Tally(layer);
                if (source == CadSource.Hatch) { tally.HatchLoops++; return; }
                if (source == CadSource.Dimension) { tally.DimensionPieces++; return; }

                tally.Arcs++;
                double length = 0;
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    double dx = points[i + 1].X - points[i].X, dy = points[i + 1].Y - points[i].Y;
                    length += Math.Sqrt((dx * dx) + (dy * dy));
                }

                tally.ArcLength += length;
                if (source == CadSource.Geometry) tally.LooseLength += length; else tally.InBlocksLength += length;
            }

            public void Text(double x, double y, double height, string value, string layer, CadSource source)
            {
                LayerTally tally = Tally(layer);
                if (source == CadSource.Dimension) { tally.DimensionPieces++; return; }
                if (source == CadSource.Hatch) return;

                string clean = ModelSource.CleanText(value).Trim();
                if (clean.Length == 0) return;

                tally.Texts++;
                Texts.Add(new CensusText { X = x, Y = y, Height = height, Value = clean, Layer = layer });
            }

            public void Symbol(CadBlockInfo block, string layer, double x, double y, double rotationRad,
                               double scaleX, double scaleY, bool mirrored, int depth,
                               IReadOnlyDictionary<string, string> attributes)
            {
                while (_open.Count > 0 && _open[_open.Count - 1].Depth >= depth) _open.RemoveAt(_open.Count - 1);

                // An insert on layer 0 inside a block takes the layer its parent was placed on,
                // which is how AutoCAD draws it.
                string effective = layer ?? string.Empty;
                if ((effective.Length == 0 || effective == "0") && _open.Count > 0)
                {
                    effective = _open[_open.Count - 1].Layer;
                }
                _open.Add((depth, effective));

                Tally(effective).Inserts++;
                Instances.Add(new Instance
                {
                    Block = block,
                    Layer = effective,
                    X = x,
                    Y = y,
                    WidthMm = Math.Abs(block.Width * scaleX),
                    HeightMm = Math.Abs(block.Height * scaleY),
                    Depth = depth,
                    Attributes = attributes,
                });
            }

            private void Grow(double x, double y)
            {
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return;
                HasExtent = true;
                if (x < MinX) MinX = x;
                if (y < MinY) MinY = y;
                if (x > MaxX) MaxX = x;
                if (y > MaxY) MaxY = y;
            }

            /// <summary>The drawing's extent as one diagonal segment. Units.Resolve only looks at
            /// the bounding box, so this gives the same answer as every segment would.</summary>
            public List<Segment> ExtentAsSegments() =>
                HasExtent
                    ? new List<Segment> { new Segment(new Point(MinX, MinY), new Point(MaxX, MaxY)) }
                    : new List<Segment>();

            /// <summary>Restates everything kept in millimetres, once the scale is known.</summary>
            public void Rescale(double scale)
            {
                foreach (Instance instance in Instances)
                {
                    instance.Key = ClassKey(instance.Block, scale);
                }

                if (scale == 1.0) return;

                if (HasExtent)
                {
                    MinX *= scale; MinY *= scale; MaxX *= scale; MaxY *= scale;
                }

                foreach (LayerTally tally in Layers.Values)
                {
                    tally.LooseLength *= scale;
                    tally.InBlocksLength *= scale;
                    tally.StraightLength *= scale;
                    tally.ArcLength *= scale;
                    tally.AxisAlignedLength *= scale;
                    for (int i = 0; i < tally.OpenRunLengths.Count; i++) tally.OpenRunLengths[i] *= scale;
                    for (int i = 0; i < tally.Pieces.Count; i++)
                    {
                        var p = tally.Pieces[i];
                        tally.Pieces[i] = (p.X1 * scale, p.Y1 * scale, p.X2 * scale, p.Y2 * scale);
                    }
                }

                foreach (Instance instance in Instances)
                {
                    instance.X *= scale;
                    instance.Y *= scale;
                    instance.WidthMm *= scale;
                    instance.HeightMm *= scale;
                }

                foreach (CensusText text in Texts)
                {
                    text.X *= scale;
                    text.Y *= scale;
                    text.Height *= scale;
                }
            }

            /// <summary>Within 2 degrees of horizontal or vertical.</summary>
            private static bool IsAxisAligned(double dx, double dy)
            {
                double angle = Math.Abs(Math.Atan2(dy, dx)) * 180.0 / Math.PI; // 0..180
                double off = Math.Min(Math.Abs(angle), Math.Min(Math.Abs(angle - 90), Math.Abs(angle - 180)));
                return off <= 2.0;
            }
        }
    }
}
