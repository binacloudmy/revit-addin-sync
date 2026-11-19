#nullable enable
// Electrical census — phase 0 of the CAD electrical to BIM work (spec 2026-11-17 §5.1, §5.2).
//
// One walk of a DWG/DXF, counted: which block classes are inserted where, what each layer
// holds, and the drawing facts (units, AEC warning, wall-like layers, plan clusters, legend)
// that decide whether the later phases can work on a drawing at all. It measures; it does not
// decide what a block or a layer MEANS. That is the AI's job in later phases, so nothing here
// guesses at meaning, and no instance coordinate reaches the census or its wire shape.
//
// The same walk also returns an ElectricalScan (Scan, ScanFile): the instances and wall faces
// with their coordinates, for the in-process phase 1 steps (fit, resolve). It never goes on
// the wire.
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

        /// <summary>The most merged segments paired in one go. A layer with more is split into
        /// tiles of at most this many (see PairInTiles), never skipped: on a real drawing the
        /// main wall layer was the one over the old cap.</summary>
        public int MaxWallTestSegments = 80000;
        public int MinWallTestSegments = 8;

        /// <summary>Linework inside an insert at most this wide and high (world mm) is a
        /// symbol's, and is left out of the wall hint.</summary>
        public double SymbolMaxExtentMm = 2000;
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

        /// <summary>AutoCAD Architecture / Civil 3D / MEP objects: refused means nothing else
        /// in the drawing was readable; a warning means some were skipped.</summary>
        public bool AecRefused;
        public string? AecWarning;
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
        /// <summary>The five layers holding most of this class's instances: the table's view.</summary>
        public List<CensusCount> Layers = new List<CensusCount>();

        /// <summary>Every layer, uncapped, for the summary's answers; never in the wire shape.
        /// Null on a class built by hand, where <see cref="Layers"/> is all there is.</summary>
        public List<CensusCount>? AllLayers;

        public int MinDepth;
        public int MaxDepth;
        public double WidthMm;
        public double HeightMm;
        public bool IsContainer;
        public bool UnresolvedXref;
        public List<CensusAttribute> Attributes = new List<CensusAttribute>();

        /// <summary>The words most often written near an instance of this class.</summary>
        public List<CensusCount> NearTexts = new List<CensusCount>();

        /// <summary>Instances within NearWallMm of a face on a wall-like layer.</summary>
        public int NearWall;
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

        // Wall hint: does this layer's own linework pair up like wall faces?
        public int WallPairs;
        public double PairedLengthShare;
        public bool PairsLikeWalls;
        /// <summary>Tiles the wall test split the layer into: 0 when not tested, 1 when paired
        /// whole.</summary>
        public int WallTiles;
    }

    /// <summary>One cluster of walls on the sheet - usually one floor plan. No coordinates.</summary>
    internal sealed class CensusPlan
    {
        public int Cluster;
        public double WidthM;
        public double HeightM;
        public int Walls;
        public List<string> Titles = new List<string>();
        public int Symbols;
    }

    /// <summary>One row of a drawn legend: the symbol, and the words written beside it.</summary>
    internal sealed class CensusLegendRow
    {
        public string Block = "";
        public string? Text;
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

        /// <summary>Instances inside any reported plan.</summary>
        public int InsidePlans;

        /// <summary>Texts reading LEGEND, PETUNJUK, SIMBOL or SYMBOL: a legend exists even
        /// when none of its rows could be read.</summary>
        public int LegendTitleTexts;
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

        /// <summary>Every block class and every layer before the caps, in the same order as
        /// <see cref="Blocks"/> and <see cref="Layers"/>. The caps only shorten the printed
        /// and JSON tables; the A1-A3 answers are worked out from these. Never in the wire
        /// shape. Null on a result built by hand, where the capped lists are all there is.</summary>
        public List<CensusBlockClass>? AllBlocks;
        public List<CensusLayer>? AllLayers;

        /// <summary>Every legend row before the cap, for the A1 evidence: a sheet of several
        /// plans repeats its symbol columns in schedules, and the rows that describe a symbol
        /// can fall past the cap. Never in the wire shape; null on a result built by hand.</summary>
        public List<CensusLegendRow>? AllLegend;

        /// <summary>The census as plain nested dictionaries and lists with snake_case keys -
        /// the one wire shape, for the CLI's JSON and for a tool result alike. It carries
        /// counts, names and sizes; never an instance's coordinates.</summary>
        public Dictionary<string, object?> ToWire()
        {
            var drawing = new Dictionary<string, object?>
            {
                ["units"] = new Dictionary<string, object?>
                {
                    ["mm_per_unit"] = Drawing.MmPerUnit,
                    ["source"] = Drawing.UnitsSource,
                    ["header_units"] = Drawing.HeaderUnits,
                    ["header_mm_per_unit"] = Drawing.HeaderMmPerUnit,
                },
                ["extent_m"] = new Dictionary<string, object?>
                {
                    ["width"] = Math.Round(Drawing.WidthM, 2),
                    ["height"] = Math.Round(Drawing.HeightM, 2),
                },
                ["entities"] = Drawing.Entities,
                ["aec"] = new Dictionary<string, object?>
                {
                    ["refused"] = Drawing.AecRefused,
                    ["warning"] = Drawing.AecWarning,
                },
                ["inserts"] = new Dictionary<string, object?>
                {
                    ["total"] = Totals.Inserts,
                    ["top_level"] = Totals.TopLevel,
                    ["nested"] = Totals.Nested,
                    ["named"] = Totals.Named,
                    ["anonymous"] = Totals.Anonymous,
                    ["inside_plans"] = Totals.InsidePlans,
                },
                ["unresolved_xrefs"] = Totals.UnresolvedXrefs.Cast<object?>().ToList(),
                ["bound_xref_layers"] = Totals.BoundXrefLayers,
                ["legend_title_texts"] = Totals.LegendTitleTexts,
                ["symbols_are_exploded"] = SymbolsAreExploded,
            };

            List<object?> blocks = Blocks.Select(block => (object?)new Dictionary<string, object?>
            {
                ["key"] = block.Key,
                ["anonymous"] = block.Anonymous,
                ["record_names"] = block.RecordNames.Cast<object?>().ToList(),
                ["count"] = block.Count,
                ["layers"] = block.Layers.Select(layer => (object?)new Dictionary<string, object?>
                {
                    ["layer"] = layer.Name,
                    ["count"] = layer.Count,
                }).ToList(),
                ["depth"] = new Dictionary<string, object?> { ["min"] = block.MinDepth, ["max"] = block.MaxDepth },
                ["extent_mm"] = new Dictionary<string, object?>
                {
                    ["w"] = Math.Round(block.WidthMm),
                    ["h"] = Math.Round(block.HeightMm),
                },
                ["is_container"] = block.IsContainer,
                ["unresolved_xref"] = block.UnresolvedXref,
                ["attributes"] = block.Attributes.Select(attribute => (object?)new Dictionary<string, object?>
                {
                    ["tag"] = attribute.Tag,
                    ["samples"] = attribute.Samples.Cast<object?>().ToList(),
                }).ToList(),
                ["near_texts"] = block.NearTexts.Select(text => (object?)new Dictionary<string, object?>
                {
                    ["text"] = text.Name,
                    ["count"] = text.Count,
                }).ToList(),
                ["near_wall"] = block.NearWall,
            }).ToList();

            List<object?> layers = Layers.Select(layer => (object?)new Dictionary<string, object?>
            {
                ["layer"] = layer.Name,
                ["counts"] = new Dictionary<string, object?>
                {
                    ["lines"] = layer.Lines,
                    ["open_polylines"] = layer.OpenPolylines,
                    ["closed_polylines"] = layer.ClosedPolylines,
                    ["arcs"] = layer.Arcs,
                    ["texts"] = layer.Texts,
                    ["inserts"] = layer.Inserts,
                    ["hatch_loops"] = layer.HatchLoops,
                    ["dimension_pieces"] = layer.DimensionPieces,
                },
                ["length_m"] = new Dictionary<string, object?>
                {
                    ["total"] = layer.LengthM,
                    ["loose"] = layer.LooseLengthM,
                    ["in_blocks"] = layer.InBlocksLengthM,
                },
                ["open_polylines_over_1m"] = layer.OpenPolylinesOver1m,
                ["axis_aligned_share"] = layer.AxisAlignedShare,
                ["walls"] = new Dictionary<string, object?>
                {
                    ["pairs"] = layer.WallPairs,
                    ["paired_length_share"] = layer.PairedLengthShare,
                    ["pairs_like_walls"] = layer.PairsLikeWalls,
                    ["tiles"] = layer.WallTiles,
                },
            }).ToList();

            List<object?> plans = Plans.Select(plan => (object?)new Dictionary<string, object?>
            {
                ["cluster"] = plan.Cluster,
                ["width_m"] = plan.WidthM,
                ["height_m"] = plan.HeightM,
                ["walls"] = plan.Walls,
                ["titles"] = plan.Titles.Cast<object?>().ToList(),
                ["symbols"] = plan.Symbols,
            }).ToList();

            List<object?> legend = Legend.Select(row => (object?)new Dictionary<string, object?>
            {
                ["block"] = row.Block,
                ["text"] = row.Text,
            }).ToList();

            var timings = new Dictionary<string, object?>();
            foreach (string name in new[] { "read", "walk", "walls", "plans", "proximity", "legend" })
            {
                timings[name] = TimingsSeconds.TryGetValue(name, out double seconds) ? Math.Round(seconds, 3) : 0.0;
            }

            var wire = new Dictionary<string, object?>
            {
                ["drawing"] = drawing,
                ["blocks"] = blocks,
                ["blocks_truncated"] = BlocksTruncated,
                ["layers"] = layers,
                ["layers_truncated"] = LayersTruncated,
                ["plans"] = plans,
                ["plans_truncated"] = PlansTruncated,
                ["legend"] = legend,
                ["legend_truncated"] = LegendTruncated,
                ["timings_s"] = timings,
            };

            Sanitize(wire);
            return wire;
        }

        /// <summary>JSON has no NaN or Infinity, and System.Text.Json throws on them: whatever
        /// slipped past the walk becomes null here, so the census can always be written.</summary>
        private static void Sanitize(object? node)
        {
            if (node is Dictionary<string, object?> map)
            {
                foreach (string key in map.Keys.ToList())
                {
                    object? value = map[key];
                    if (value is double number && !ElectricalCensus.IsFinite(number)) map[key] = null;
                    else Sanitize(value);
                }
            }
            else if (node is List<object?> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is double number && !ElectricalCensus.IsFinite(number)) list[i] = null;
                    else Sanitize(list[i]);
                }
            }
        }
    }

    /// <summary>A plain 2D segment in millimetres: a wall centreline or face, drawing or model.</summary>
    internal struct FitSegment
    {
        public double X1, Y1, X2, Y2;

        public FitSegment(double x1, double y1, double x2, double y2)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        public double Length => Math.Sqrt(((X2 - X1) * (X2 - X1)) + ((Y2 - Y1) * (Y2 - Y1)));
    }

    /// <summary>One placed insert, with where and how it is drawn. In process only.</summary>
    internal sealed class ScanSymbol
    {
        public string Key = "", Layer = "";
        /// <summary>The drawn centre (see DrawnCentre), mm. Never the insert point.</summary>
        public double X, Y;
        public double RotationRad;
        public bool Mirrored;
        public double WidthMm, HeightMm;
        public int Depth;
        /// <summary>Index into <see cref="ElectricalScan.Symbols"/> of the insert holding this
        /// one; -1 at depth 0.</summary>
        public int Parent = -1;
        /// <summary>The census plan this one is in; -1 when in none.</summary>
        public int Cluster = -1;
        public bool IsContainer;
        /// <summary>False when the position is not finite: counted, never placed.</summary>
        public bool Placed;
        public IReadOnlyDictionary<string, string> Attributes = new Dictionary<string, string>();
    }

    /// <summary>One census plan's box on the sheet, mm. Same index as the census plans.</summary>
    internal sealed class ScanCluster
    {
        public int Index;
        public double MinX, MinY, MaxX, MaxY;
        public List<string> Titles = new List<string>();
    }

    /// <summary>
    /// What one census walk saw, with coordinates, for the in-process steps that follow it:
    /// the census itself, every insert where it is drawn, the plan boxes, and each layer's
    /// wall-face pieces (symbol linework left out, see IsInsideSymbol). Never on the wire.
    /// </summary>
    internal sealed class ElectricalScan
    {
        public ElectricalCensusResult Census;
        public double MmPerUnit;
        public List<ScanSymbol> Symbols;
        public List<ScanCluster> Clusters;
        public IReadOnlyCollection<string> LayerNames;
        public IReadOnlyCollection<string> WallLikeLayers;

        private readonly Dictionary<string, List<(double X1, double Y1, double X2, double Y2)>> _facePieces;
        private readonly CensusOptions _options;
        private readonly List<(double X, double Y, double Height, string Value)> _titleTexts;

        internal ElectricalScan(ElectricalCensusResult census, double mmPerUnit, List<ScanSymbol> symbols,
                                List<ScanCluster> clusters, IReadOnlyCollection<string> layerNames,
                                IReadOnlyCollection<string> wallLikeLayers,
                                Dictionary<string, List<(double X1, double Y1, double X2, double Y2)>> facePieces,
                                CensusOptions options,
                                List<(double X, double Y, double Height, string Value)>? titleTexts = null)
        {
            Census = census;
            MmPerUnit = mmPerUnit;
            Symbols = symbols;
            Clusters = clusters;
            LayerNames = layerNames;
            WallLikeLayers = wallLikeLayers;
            _facePieces = facePieces;
            _options = options;
            _titleTexts = titleTexts ?? new List<(double X, double Y, double Height, string Value)>();
        }

        /// <summary>The plan titles of a box on the sheet, mm, by the census's own rule for a
        /// plan's box: title words written under it or close beside it, tallest first, at most
        /// three.</summary>
        public List<string> TitlesIn(double minX, double minY, double maxX, double maxY) =>
            ElectricalCensus.TitlesFor(_titleTexts, minX, minY, maxX, maxY);

        /// <summary>The non-symbol straight pieces of these layers, mm, each tagged with its
        /// layer. A name the drawing does not have adds nothing.</summary>
        public List<Segment> FacePieces(IEnumerable<string> layers)
        {
            var segments = new List<Segment>();
            foreach (string layer in layers.Distinct(StringComparer.Ordinal))
            {
                if (!_facePieces.TryGetValue(layer, out List<(double X1, double Y1, double X2, double Y2)>? pieces)) continue;
                foreach (var p in pieces)
                {
                    segments.Add(new Segment(new Point(p.X1, p.Y1), new Point(p.X2, p.Y2)) { Layer = layer });
                }
            }
            return segments;
        }

        /// <summary>
        /// The wall centrelines of one plan, paired from these layers' faces the way the
        /// census pairs them (MergeByDirection, then PairInTiles, never one merge of a whole
        /// layer). Per layer unless <paramref name="pairAcrossLayers"/>, which pools the layers
        /// so a wall whose two faces sit on two layers is found. Kept when the centreline's
        /// midpoint is inside the plan's box grown by the near-wall distance.
        /// </summary>
        public List<FitSegment> WallCentrelines(int cluster, IReadOnlyCollection<string> layers, bool pairAcrossLayers)
        {
            if (cluster < 0 || cluster >= Clusters.Count) throw new ArgumentOutOfRangeException(nameof(cluster));
            ScanCluster box = Clusters[cluster];
            double sMin = Units.DefaultMinWallThicknessMm, sMax = Units.DefaultMaxWallThicknessMm;

            IEnumerable<IEnumerable<string>> groups = pairAcrossLayers
                ? new[] { (IEnumerable<string>)layers }
                : layers.Select(layer => (IEnumerable<string>)new[] { layer });

            var walls = new List<Wall>();
            foreach (IEnumerable<string> group in groups)
            {
                List<Segment> faces = FacePieces(group);
                if (faces.Count == 0) continue;
                walls.AddRange(ElectricalCensus.PairInTiles(ElectricalCensus.MergeByDirection(faces), sMin, sMax,
                                                            _options.MaxWallTestSegments, out _));
            }

            double margin = _options.NearWallMm;
            var centrelines = new List<FitSegment>();
            foreach (Wall wall in CadClassifier.DeduplicateWalls(walls))
            {
                Segment c = wall.Centerline;
                double mx = (c.P1.x + c.P2.x) / 2, my = (c.P1.y + c.P2.y) / 2;
                if (mx < box.MinX - margin || mx > box.MaxX + margin || my < box.MinY - margin || my > box.MaxY + margin) continue;
                centrelines.Add(new FitSegment(c.P1.x, c.P1.y, c.P2.x, c.P2.y));
            }
            return centrelines;
        }
    }

    internal static class ElectricalCensus
    {
        /// <summary>Reads a DWG or DXF and takes its census.</summary>
        public static ElectricalCensusResult Read(string path, CensusOptions? options = null) =>
            ScanFile(path, options).Census;

        /// <summary>Reads a DWG or DXF and scans it: the census plus the in-process scan.</summary>
        public static ElectricalScan ScanFile(string path, CensusOptions? options = null)
        {
            var clock = Stopwatch.StartNew();
            // Read here rather than through CadRenderSource.Read so the reader is disposed and
            // the file is not left locked once the census is taken.
            CadDocument document;
            if (path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new ACadSharp.IO.DxfReader(path)) document = reader.Read();
            }
            else
            {
                using (var reader = new ACadSharp.IO.DwgReader(path)) document = reader.Read();
            }
            double read = clock.Elapsed.TotalSeconds;

            ElectricalScan scan = Scan(document, options);
            scan.Census.TimingsSeconds["read"] = read;
            return scan;
        }

        public static ElectricalCensusResult Build(CadDocument document, CensusOptions? options = null) =>
            Scan(document, options).Census;

        /// <summary>One walk of the document: the census, and the scan the later steps work on.</summary>
        public static ElectricalScan Scan(CadDocument document, CensusOptions? options = null)
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

            result.Drawing.AecRefused = AecVerdict(document, sink, out string? aecWarning);
            result.Drawing.AecWarning = aecWarning;

            clock.Restart();
            Dictionary<string, List<(double X1, double Y1, double X2, double Y2)>> facePieces = FacePiecesByLayer(sink, o);
            List<Wall> walls = TestLayersForWalls(sink, facePieces, o);
            result.TimingsSeconds["walls"] = clock.Elapsed.TotalSeconds;

            clock.Restart();
            List<PlanCluster> clusters = BuildPlans(sink, walls, o, result);
            result.TimingsSeconds["plans"] = clock.Elapsed.TotalSeconds;

            clock.Restart();
            MarkNearWalls(sink, o);
            var texts = new TextGrid(sink.Texts, 1000);
            Dictionary<string, List<CensusCount>> nearTexts = NearTexts(sink, texts, o);
            result.TimingsSeconds["proximity"] = clock.Elapsed.TotalSeconds;

            BuildBlockClasses(sink, o, result);
            foreach (CensusBlockClass block in result.Blocks)
            {
                if (nearTexts.TryGetValue(block.Key, out List<CensusCount>? near)) block.NearTexts = near;
            }
            BuildLayers(sink, o, result);
            BuildTotals(sink, result);

            clock.Restart();
            BuildLegend(sink, texts, clusters, o, result);
            result.TimingsSeconds["legend"] = clock.Elapsed.TotalSeconds;

            // Exploded: every symbol-like insert sits on the architectural background, so the
            // electrical symbols themselves can only be loose linework.
            result.SymbolsAreExploded = !sink.Instances.Any(instance =>
                instance.Block.NestedInserts == 0 && !sink.Layers[instance.Layer].PairsLikeWalls);

            return new ElectricalScan(
                result, scale, ScanSymbols(sink), ScanClusters(clusters, result),
                sink.Layers.Keys.ToList(),
                sink.Layers.Values.Where(tally => tally.PairsLikeWalls).Select(tally => tally.Name).ToList(),
                facePieces, o, TitleTexts(sink));
        }

        private static List<ScanSymbol> ScanSymbols(CensusSink sink)
        {
            var index = new Dictionary<Instance, int>(sink.Instances.Count);
            for (int i = 0; i < sink.Instances.Count; i++) index[sink.Instances[i]] = i;

            var symbols = new List<ScanSymbol>(sink.Instances.Count);
            foreach (Instance instance in sink.Instances)
            {
                symbols.Add(new ScanSymbol
                {
                    Key = instance.Key,
                    Layer = instance.Layer,
                    X = instance.X,
                    Y = instance.Y,
                    RotationRad = instance.RotationRad,
                    Mirrored = instance.Mirrored,
                    WidthMm = instance.WidthMm,
                    HeightMm = instance.HeightMm,
                    Depth = instance.Depth,
                    Parent = instance.Parent != null && index.TryGetValue(instance.Parent, out int parent) ? parent : -1,
                    Cluster = instance.Cluster,
                    IsContainer = instance.Block.NestedInserts > 0,
                    Placed = instance.Placed,
                    Attributes = instance.Attributes,
                });
            }
            return symbols;
        }

        private static List<ScanCluster> ScanClusters(List<PlanCluster> clusters, ElectricalCensusResult result)
        {
            var boxes = new List<ScanCluster>(clusters.Count);
            for (int i = 0; i < clusters.Count; i++)
            {
                boxes.Add(new ScanCluster
                {
                    Index = i,
                    MinX = clusters[i].MinX,
                    MinY = clusters[i].MinY,
                    MaxX = clusters[i].MaxX,
                    MaxY = clusters[i].MaxY,
                    Titles = result.Plans[i].Titles.ToList(),
                });
            }
            return boxes;
        }

        // ---------------------------------------------------------------- AEC

        private static bool AecVerdict(CadDocument document, CensusSink sink, out string? warning)
        {
            warning = null;
            try
            {
                int readable = sink.Layers.Values.Sum(tally => tally.Pieces.Count) + sink.Texts.Count;
                IEnumerable<(string, bool, int)> classes = document.Classes == null
                    ? Enumerable.Empty<(string, bool, int)>()
                    : document.Classes.Select(c => (c.DxfName, c.IsAnEntity, c.InstanceCount)).ToList();

                bool refused = RevitWebAppSync.Services.CadToBim.CadSourceVerdict.IsRefused(classes, readable, out string verdictWarning);
                warning = verdictWarning;
                return refused;
            }
            catch (Exception ex)
            {
                // A census never fails on its warning.
                warning = "AEC check failed: " + ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- walls

        /// <summary>
        /// Pairs each layer's own linework into walls, so a layer that draws walls says so.
        /// Per layer, so a pair is never made across two layers: that misses walls whose faces
        /// sit on different layers, and keeps electrical linework from pairing with the walls.
        /// Returns the walls of every wall-like layer.
        ///
        /// The thickness bounds are the defaults, passed to ClassifyWalls rather than set on
        /// the shared Wall.SMin/SMax: the census can run beside a detect pass in the same
        /// process (the add-in), and must neither see nor change the pane's settings. It still
        /// reads Wall.MinFaceLength/MinFaceAspect/MaxFaceUses, which nothing here changes.
        /// </summary>
        private static List<Wall> TestLayersForWalls(
            CensusSink sink, Dictionary<string, List<(double X1, double Y1, double X2, double Y2)>> facePieces,
            CensusOptions o)
        {
            var wallLike = new List<Wall>();
            double sMin = Units.DefaultMinWallThicknessMm, sMax = Units.DefaultMaxWallThicknessMm;

            foreach (LayerTally tally in sink.Layers.Values)
            {
                // Only pieces that can be a wall face (see FacePiecesByLayer). The share is
                // taken over the same pieces.
                List<(double X1, double Y1, double X2, double Y2)> pieces = facePieces[tally.Name];

                int faces = pieces.Count(p => PieceLength(p) >= Wall.MinFaceLength);
                if (faces < o.MinWallTestSegments) continue;

                List<Segment> segments = LayerTally.Segments(pieces, tally.Name);
                List<Wall> merged = PairInTiles(MergeByDirection(segments), sMin, sMax,
                                                o.MaxWallTestSegments, out int tiles);
                tally.WallTiles = tiles;
                tally.WallPairs = merged.Count;

                double straight = pieces.Sum(PieceLength);
                double paired = merged.Sum(wall => wall.Centerline.Length) * 2;
                tally.PairedLengthShare = straight > 0 ? Math.Min(1.0, paired / straight) : 0;
                tally.PairsLikeWalls = PairsLikeWalls(tally.WallPairs, tally.PairedLengthShare);

                if (!tally.PairsLikeWalls) continue;
                wallLike.AddRange(merged);
                tally.WallSegments = segments;
            }

            return wallLike;
        }

        /// <summary>Per layer, the pieces that can be a wall face: loose ones, and ones inside a
        /// block that is not a symbol (see IsInsideSymbol).</summary>
        private static Dictionary<string, List<(double X1, double Y1, double X2, double Y2)>> FacePiecesByLayer(
            CensusSink sink, CensusOptions o)
        {
            var byLayer = new Dictionary<string, List<(double X1, double Y1, double X2, double Y2)>>(StringComparer.Ordinal);
            foreach (LayerTally tally in sink.Layers.Values)
            {
                var pieces = new List<(double X1, double Y1, double X2, double Y2)>(tally.Pieces.Count);
                for (int i = 0; i < tally.Pieces.Count; i++)
                {
                    if (!IsInsideSymbol(tally.PieceOwners[i], o)) pieces.Add(tally.Pieces[i]);
                }
                byLayer[tally.Name] = pieces;
            }
            return byLayer;
        }

        /// <summary>
        /// A layer draws walls when its linework pairs into enough walls, over enough of its
        /// length. Eight pairs over a quarter of the length is a floor plan. A small floor (an
        /// attic, a roof room) has fewer walls, and a real one had few pairs on nearly all of its
        /// wall layer and was missed: so fewer pairs count when nearly all the linework pairs.
        /// Two or three pairs stay out, whatever the share: a symbol layer's stray box does that.
        /// </summary>
        internal static bool PairsLikeWalls(int pairs, double pairedLengthShare) =>
            (pairs >= 8 && pairedLengthShare >= 0.25) || (pairs >= 4 && pairedLengthShare >= 0.6);

        /// <summary>
        /// MergeCollinearSegments one direction at a time, in 2 degree bins centred on the
        /// axes. The merge groups pieces by angle by chaining neighbours 2 degrees apart; on a
        /// dense layer, with pieces at every angle (curves, hatching, furniture), the chain
        /// reaches from -90 to +90 degrees, every piece lands in one group and is re-laid
        /// along the first one's angle. On a real dense wall layer that left few faces long
        /// enough to pair out of a great many pieces, where pairing the pieces unmerged found more
        /// walls. A bin caps the chain at its width. Straight up and straight down are the same
        /// direction, so the two vertical half-bins are one.
        /// </summary>
        internal static List<Segment> MergeByDirection(List<Segment> segments)
        {
            const double bin = Math.PI / 90;
            var byDirection = new Dictionary<int, List<Segment>>();
            foreach (Segment segment in segments)
            {
                double dx = segment.P2.x - segment.P1.x, dy = segment.P2.y - segment.P1.y;
                if (dx == 0 && dy == 0) continue;
                if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }

                int key = (int)Math.Round(Math.Atan2(dy, dx) / bin);
                if (key == -45) key = 45;
                if (!byDirection.TryGetValue(key, out List<Segment>? list))
                {
                    list = new List<Segment>();
                    byDirection[key] = list;
                }
                list.Add(segment);
            }

            var merged = new List<Segment>(segments.Count);
            foreach (int key in byDirection.Keys.OrderBy(k => k))
            {
                merged.AddRange(CadClassifier.MergeCollinearSegments(byDirection[key]));
            }
            return merged;
        }

        /// <summary>How far a tile reaches past its own area, over the thickest wall: a wall
        /// whose centre is in the tile has both faces within sMax / 2 of that centre, and the
        /// rest buys the faces competing for them.</summary>
        internal const double TileMarginMm = 500;

        /// <summary>No tile is split below this many overlaps across, so a run of long lines
        /// crossing everything cannot split for ever.</summary>
        private const int MinTileOverlaps = 4;
        private const int MaxTileDepth = 12;

        /// <summary>
        /// ClassifyWalls over segments already merged, in tiles of at most <paramref name="budget"/>
        /// segments when there are more. A k-d split: a tile over budget is cut across its
        /// longer side at the median of the segment midpoints in it, until each is under budget.
        /// A grid by plan cluster is not an option (the clusters are built from these walls),
        /// and the median follows the linework, where a fixed grid would put one plan in one
        /// over-full cell and leave the sheet's margins empty.
        ///
        /// Each tile pairs every segment whose box comes within sMax + TileMarginMm of it, whole
        /// and in the layer's order, so a wall across a tile edge is found in full in both
        /// tiles. The tile that holds the centreline's midpoint keeps it (tile areas are
        /// half-open, so exactly one does), and DeduplicateWalls drops a wall that two tiles
        /// paired differently. Faces are merged before the split, over the whole layer, so a
        /// tile edge never cuts a face.
        /// </summary>
        internal static List<Wall> PairInTiles(List<Segment> segments, double sMin, double sMax, int budget, out int tiles)
        {
            if (segments.Count <= Math.Max(budget, 1))
            {
                // Deduplicated here too, so a layer's pair count does not depend on whether it
                // was tiled.
                tiles = 1;
                return CadClassifier.DeduplicateWalls(CadClassifier.ClassifyWalls(segments, sMin, sMax));
            }

            int n = segments.Count;
            var minX = new double[n]; var minY = new double[n]; var maxX = new double[n]; var maxY = new double[n];
            var midX = new double[n]; var midY = new double[n];
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                Segment s = segments[i];
                minX[i] = Math.Min(s.P1.x, s.P2.x); maxX[i] = Math.Max(s.P1.x, s.P2.x);
                minY[i] = Math.Min(s.P1.y, s.P2.y); maxY[i] = Math.Max(s.P1.y, s.P2.y);
                midX[i] = (s.P1.x + s.P2.x) / 2; midY[i] = (s.P1.y + s.P2.y) / 2;
                x0 = Math.Min(x0, minX[i]); y0 = Math.Min(y0, minY[i]);
                x1 = Math.Max(x1, maxX[i]); y1 = Math.Max(y1, maxY[i]);
            }

            double reach = sMax + TileMarginMm;
            var found = new List<Wall>();
            int leaves = 0;

            List<int> Within(List<int> from, double ax, double ay, double bx, double by) =>
                from.Where(i => maxX[i] >= ax - reach && minX[i] <= bx + reach &&
                                maxY[i] >= ay - reach && minY[i] <= by + reach).ToList();

            void Leaf(List<int> members, double ax, double ay, double bx, double by)
            {
                leaves++;
                // Members stay in ascending index order, so a tile pairs in the layer's order.
                List<Segment> local = members.Select(i => segments[i]).ToList();
                foreach (Wall wall in CadClassifier.ClassifyWalls(local, sMin, sMax))
                {
                    double cx = (wall.Centerline.P1.x + wall.Centerline.P2.x) / 2;
                    double cy = (wall.Centerline.P1.y + wall.Centerline.P2.y) / 2;
                    if (cx >= ax && cx < bx && cy >= ay && cy < by) found.Add(wall);
                }
            }

            void Split(List<int> members, double ax, double ay, double bx, double by, int depth)
            {
                bool alongX = (bx - ax) >= (by - ay);
                double side = alongX ? bx - ax : by - ay;
                if (members.Count <= budget || depth >= MaxTileDepth || side < MinTileOverlaps * reach)
                {
                    Leaf(members, ax, ay, bx, by);
                    return;
                }

                double lo = alongX ? ax : ay, hi = alongX ? bx : by;
                double[] mid = alongX ? midX : midY;
                List<double> inside = members.Select(i => mid[i]).Where(v => v >= lo && v < hi).OrderBy(v => v).ToList();
                double cut = inside.Count > 0 ? inside[inside.Count / 2] : (lo + hi) / 2;
                // Keep both halves at least one overlap wide.
                cut = Math.Max(lo + reach, Math.Min(hi - reach, cut));

                List<int> first = alongX ? Within(members, ax, ay, cut, by) : Within(members, ax, ay, bx, cut);
                List<int> second = alongX ? Within(members, cut, ay, bx, by) : Within(members, ax, cut, bx, by);
                if (first.Count == members.Count && second.Count == members.Count)
                {
                    // Every segment reaches both halves: splitting buys nothing.
                    Leaf(members, ax, ay, bx, by);
                    return;
                }

                if (alongX)
                {
                    Split(first, ax, ay, cut, by, depth + 1);
                    Split(second, cut, ay, bx, by, depth + 1);
                }
                else
                {
                    Split(first, ax, ay, bx, cut, depth + 1);
                    Split(second, ax, cut, bx, by, depth + 1);
                }
            }

            // Half-open tiles: nudge the far edges so the drawing's last midpoint is inside.
            Split(Enumerable.Range(0, n).ToList(), x0, y0, x1 + 1, y1 + 1, 0);

            tiles = leaves;
            return CadClassifier.DeduplicateWalls(found);
        }

        /// <summary>
        /// True when a piece drawn inside this insert (null: loose, in model space) is part of
        /// a symbol, and so kept out of the wall hint. A socket symbol's two parallel strokes
        /// 100 mm apart pair exactly like a tiny wall: 318 of them made a socket layer look
        /// like walls. Leaving out all block content would be simpler, but an architect's
        /// background inserted as one block, or a bound xref, draws every wall inside a block,
        /// and those must still count. So the rule is size: a piece is symbol linework when the
        /// insert holding it, or any insert around that one, is at most SymbolMaxExtentMm
        /// (2000 mm) across in world units. No plan fits in 2 m, so the rule needs no test for
        /// nested inserts: a small block built of sub-blocks (a DB symbol) is still a symbol,
        /// and a large block, container or not, keeps its walls.
        /// </summary>
        private static bool IsInsideSymbol(Instance? owner, CensusOptions o)
        {
            for (Instance? at = owner; at != null; at = at.Parent)
            {
                if (Math.Max(at.WidthMm, at.HeightMm) <= o.SymbolMaxExtentMm) return true;
            }
            return false;
        }

        private static double PieceLength((double X1, double Y1, double X2, double Y2) p)
        {
            double dx = p.X2 - p.X1, dy = p.Y2 - p.Y1;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        // ---------------------------------------------------------------- plans

        private static readonly string[] TitleWords = { "PELAN", "PLAN", "LAYOUT", "TINGKAT", "ARAS", "LANTAI", "BUMBUNG" };

        /// <summary>Clusters the wall-like layers' walls into plans, titles them from nearby
        /// text, and counts the symbols each one holds.</summary>
        private static List<PlanCluster> BuildPlans(CensusSink sink, List<Wall> walls, CensusOptions o,
                                                    ElectricalCensusResult result)
        {
            // Biggest first, then left to right and bottom to top, so the indices do not depend
            // on the order the recursion found them in.
            List<PlanCluster> clusters = ClusterRecursively(CadClassifier.DeduplicateWalls(walls), 0)
                .OrderByDescending(cluster => cluster.Area)
                .ThenBy(cluster => cluster.MinX)
                .ThenBy(cluster => cluster.MinY)
                .ToList();
            result.PlansTruncated = clusters.Count > o.MaxPlans;
            clusters = clusters.Take(o.MaxPlans).ToList();

            List<(double X, double Y, double Height, string Value)> titleTexts = TitleTexts(sink);

            for (int i = 0; i < clusters.Count; i++)
            {
                PlanCluster cluster = clusters[i];
                List<string> titles = TitlesFor(titleTexts, cluster.MinX, cluster.MinY, cluster.MaxX, cluster.MaxY);

                result.Plans.Add(new CensusPlan
                {
                    Cluster = i,
                    WidthM = Math.Round(cluster.Width / 1000.0, 2),
                    HeightM = Math.Round(cluster.Height / 1000.0, 2),
                    Walls = cluster.Walls.Count,
                    Titles = titles,
                });
            }

            // A cluster's box runs along wall centrelines, so it cuts the outer half of every
            // outside wall off, and with it a light or a socket fixed to the outer face. The
            // box grows by the near-wall distance to take them in.
            double margin = o.NearWallMm;
            foreach (Instance instance in sink.Instances)
            {
                if (!instance.Placed) continue;
                for (int i = 0; i < clusters.Count; i++)
                {
                    PlanCluster c = clusters[i];
                    if (instance.X < c.MinX - margin || instance.X > c.MaxX + margin ||
                        instance.Y < c.MinY - margin || instance.Y > c.MaxY + margin) continue;
                    result.Plans[i].Symbols++;
                    instance.InPlan = true;
                    instance.Cluster = i;
                    break;
                }
                if (instance.InPlan) result.Totals.InsidePlans++;
            }

            return clusters;
        }

        private const int MaxClusterDepth = 6;

        /// <summary>
        /// The plan clustering, run again inside each cluster it finds until one comes back
        /// whole. One pass bands the sheet down it, then each band across: plans stacked in a
        /// column beside a column offset by half a pitch leave no blank row across the whole
        /// sheet, so the down pass finds no gap and each column keeps its stacked plans. Inside
        /// one column the gaps show. A wall bridging the gaps still keeps them together; the fit's
        /// footprint crop and rivals carry that case. The engine itself is left as the CAD to
        /// BIM pane uses it.
        /// </summary>
        private static List<PlanCluster> ClusterRecursively(List<Wall> walls, int depth)
        {
            List<PlanCluster> clusters = CadClassifier.ClusterPlans(walls, null);
            if (clusters.Count <= 1 || depth >= MaxClusterDepth) return clusters;

            var split = new List<PlanCluster>();
            foreach (PlanCluster cluster in clusters) split.AddRange(ClusterRecursively(cluster.Walls, depth + 1));
            return split;
        }

        /// <summary>The sheet's texts that hold a title word.</summary>
        private static List<(double X, double Y, double Height, string Value)> TitleTexts(CensusSink sink) =>
            sink.Texts.Where(text => ContainsAny(text.Value, TitleWords))
                .Select(text => (text.X, text.Y, text.Height, text.Value))
                .ToList();

        /// <summary>A plan's titles: title texts under its box, or close beside it, tallest
        /// first, at most three.</summary>
        internal static List<string> TitlesFor(IEnumerable<(double X, double Y, double Height, string Value)> titleTexts,
                                               double boxMinX, double boxMinY, double boxMaxX, double boxMaxY)
        {
            double minX = boxMinX - 2000, maxX = boxMaxX + 2000;
            double minY = boxMinY - TitleReachBelow(boxMaxY - boxMinY), maxY = boxMaxY + 2000;

            return titleTexts
                .Where(text => text.X >= minX && text.X <= maxX && text.Y >= minY && text.Y <= maxY)
                .OrderByDescending(text => text.Height)
                .Select(text => Truncate(text.Value, 60))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();
        }

        /// <summary>How far under a plan its title may sit: a fifth of its height, and at least
        /// 4 m (a title in a 10 m gap can sit more than 2.6 m under a 13 m plan; 4 m stays under
        /// the 5 m plan gap).</summary>
        private static double TitleReachBelow(double planHeight) => Math.Max(0.2 * planHeight, 4000);

        private static bool ContainsAny(string value, string[] words)
        {
            string upper = value.ToUpperInvariant();
            foreach (string word in words)
            {
                if (upper.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- proximity

        /// <summary>Marks every instance within NearWallMm of a face on a wall-like layer.</summary>
        private static void MarkNearWalls(CensusSink sink, CensusOptions o)
        {
            var faces = new List<Segment>();
            foreach (LayerTally tally in sink.Layers.Values)
            {
                if (tally.WallSegments != null) faces.AddRange(tally.WallSegments);
            }
            if (faces.Count == 0) return;

            var index = new SegmentIndex(faces, 2000);
            foreach (Instance instance in sink.Instances)
            {
                if (!instance.Placed) continue;
                var at = new Point(instance.X, instance.Y);

                // A zero-length query segment: Near only reads its box. Never ask it whether it
                // is parallel to anything - it has no direction, and isParallelTo divides by it.
                foreach (int i in index.Near(new Segment(at, at), o.NearWallMm))
                {
                    if (DistanceToSegment(at, faces[i]) > o.NearWallMm) continue;
                    instance.NearWall = true;
                    break;
                }
            }
        }

        private static double DistanceToSegment(Point p, Segment s)
        {
            double dx = s.P2.x - s.P1.x, dy = s.P2.y - s.P1.y;
            double lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared > 0 ? (((p.x - s.P1.x) * dx) + ((p.y - s.P1.y) * dy)) / lengthSquared : 0;
            t = Math.Max(0, Math.Min(1, t));
            double x = s.P1.x + (t * dx) - p.x, y = s.P1.y + (t * dy) - p.y;
            return Math.Sqrt((x * x) + (y * y));
        }

        /// <summary>The words written near each class's instances: per class, the five most
        /// frequent values, each counted once per instance it is near.</summary>
        private static Dictionary<string, List<CensusCount>> NearTexts(CensusSink sink, TextGrid grid, CensusOptions o)
        {
            var perClass = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            foreach (Instance instance in sink.Instances)
            {
                if (!instance.Placed) continue;
                double reach = o.TextReachFactor * Math.Max(instance.WidthMm, instance.HeightMm);
                reach = Math.Max(300, Math.Min(3000, reach));

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (CensusText text in grid.Within(instance.X - reach, instance.Y - reach,
                                                        instance.X + reach, instance.Y + reach))
                {
                    double dx = text.X - instance.X, dy = text.Y - instance.Y;
                    if ((dx * dx) + (dy * dy) > reach * reach) continue;
                    seen.Add(Truncate(text.Value, 60));
                }
                if (seen.Count == 0) continue;

                if (!perClass.TryGetValue(instance.Key, out Dictionary<string, int>? counts))
                {
                    counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    perClass[instance.Key] = counts;
                }
                foreach (string value in seen)
                {
                    counts[value] = counts.TryGetValue(value, out int count) ? count + 1 : 1;
                }
            }

            return perClass.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(5)
                    .Select(entry => new CensusCount { Name = entry.Key, Count = entry.Value })
                    .ToList(),
                StringComparer.Ordinal);
        }

        /// <summary>Texts filed by grid cell, for "what is written near here".</summary>
        private sealed class TextGrid
        {
            private readonly double _cell;
            private readonly Dictionary<(long, long), List<CensusText>> _cells = new Dictionary<(long, long), List<CensusText>>();

            public TextGrid(IEnumerable<CensusText> texts, double cell)
            {
                _cell = cell;
                foreach (CensusText text in texts)
                {
                    var key = (Cell(text.X), Cell(text.Y));
                    if (!_cells.TryGetValue(key, out List<CensusText>? list))
                    {
                        list = new List<CensusText>();
                        _cells[key] = list;
                    }
                    list.Add(text);
                }
            }

            private long Cell(double value) => (long)Math.Floor(value / _cell);

            public IEnumerable<CensusText> Within(double minX, double minY, double maxX, double maxY)
            {
                long x0 = Cell(minX), x1 = Cell(maxX), y0 = Cell(minY), y1 = Cell(maxY);
                for (long x = x0; x <= x1; x++)
                {
                    for (long y = y0; y <= y1; y++)
                    {
                        if (!_cells.TryGetValue((x, y), out List<CensusText>? list)) continue;
                        foreach (CensusText text in list)
                        {
                            if (text.X >= minX && text.X <= maxX && text.Y >= minY && text.Y <= maxY) yield return text;
                        }
                    }
                }
            }
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
                    NearWall = list.Count(instance => instance.NearWall),
                };

                block.AllLayers = list
                    .GroupBy(instance => instance.Layer, StringComparer.Ordinal)
                    .Select(byLayer => new CensusCount { Name = byLayer.Key, Count = byLayer.Count() })
                    .OrderByDescending(count => count.Count).ThenBy(count => count.Name, StringComparer.Ordinal)
                    .ToList();
                block.Layers = block.AllLayers.Take(5).ToList();

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
            result.AllBlocks = classes;
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
                    WallPairs = tally.WallPairs,
                    PairedLengthShare = Math.Round(tally.PairedLengthShare, 3),
                    PairsLikeWalls = tally.PairsLikeWalls,
                    WallTiles = tally.WallTiles,
                });
            }

            layers = layers
                .OrderByDescending(layer => layer.LengthM + layer.Inserts)
                .ThenBy(layer => layer.Name, StringComparer.Ordinal)
                .ToList();

            result.LayersTruncated = layers.Count > o.MaxLayers;
            result.AllLayers = layers;
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

        // ---------------------------------------------------------------- legend

        private static readonly string[] LegendWords = { "LEGEND", "PETUNJUK", "SIMBOL", "SYMBOL" };

        /// <summary>
        /// Reads a drawn legend: a column of different symbols off the plans, each with its
        /// description written to its right. A column of one repeated symbol is a row of
        /// sockets along a wall, not a legend, so a column needs mostly distinct classes.
        /// </summary>
        private static void BuildLegend(CensusSink sink, TextGrid grid, List<PlanCluster> clusters, CensusOptions o,
                                        ElectricalCensusResult result)
        {
            result.Totals.LegendTitleTexts = sink.Texts.Count(text => ContainsAny(text.Value, LegendWords));

            const double Margin = 1000;
            List<Instance> candidates = sink.Instances
                .Where(instance => instance.Placed && instance.Block.NestedInserts == 0)
                .Where(instance => !clusters.Any(cluster =>
                    instance.X >= cluster.MinX - Margin && instance.X <= cluster.MaxX + Margin &&
                    instance.Y >= cluster.MinY - Margin && instance.Y <= cluster.MaxY + Margin))
                .OrderBy(instance => instance.X)
                .ToList();

            // Columns: symbols whose x lies within 500 mm of the column's first one.
            var columns = new List<List<Instance>>();
            foreach (Instance instance in candidates)
            {
                List<Instance>? last = columns.Count > 0 ? columns[columns.Count - 1] : null;
                if (last != null && instance.X - last[0].X <= 500) last.Add(instance);
                else columns.Add(new List<Instance> { instance });
            }

            var rows = new List<CensusLegendRow>();

            foreach (List<Instance> column in columns)
            {
                foreach (List<Instance> run in SplitByGap(column))
                {
                    int distinct = run.Select(instance => instance.Key).Distinct(StringComparer.Ordinal).Count();
                    if (run.Count < 3 || distinct < Math.Max(3, 0.6 * run.Count)) continue;

                    foreach (Instance instance in run)
                    {
                        rows.Add(new CensusLegendRow { Block = instance.Key, Text = DescriptionOf(instance, grid) });
                    }
                }
            }

            result.LegendTruncated = rows.Count > o.MaxLegendRows;
            result.AllLegend = rows;
            result.Legend = rows.Take(o.MaxLegendRows).ToList();
        }

        /// <summary>A column top to bottom, broken wherever two rows sit more than six symbol
        /// heights apart.</summary>
        private static IEnumerable<List<Instance>> SplitByGap(List<Instance> column)
        {
            List<Instance> sorted = column.OrderByDescending(instance => instance.Y).ToList();
            var run = new List<Instance> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
                double height = Math.Max(SymbolHeight(sorted[i - 1]), SymbolHeight(sorted[i]));
                if (sorted[i - 1].Y - sorted[i].Y > 6 * height)
                {
                    yield return run;
                    run = new List<Instance>();
                }
                run.Add(sorted[i]);
            }

            yield return run;
        }

        private static double SymbolHeight(Instance instance) =>
            Math.Max(Math.Max(instance.HeightMm, instance.WidthMm), 100);

        /// <summary>The nearest text to the right of a legend symbol, on its line.</summary>
        private static string? DescriptionOf(Instance instance, TextGrid grid)
        {
            double symbolHeight = SymbolHeight(instance);
            CensusText? best = null;
            double bestDx = double.MaxValue;

            foreach (CensusText text in grid.Within(instance.X, instance.Y - 3000, instance.X + 8000, instance.Y + 3000))
            {
                double dx = text.X - instance.X;
                double dy = Math.Abs(text.Y + (text.Height / 2) - instance.Y);
                if (dx <= 0 || dx > 8000) continue;
                if (dy > Math.Max(text.Height, symbolHeight)) continue;
                if (dx >= bestDx) continue;

                best = text;
                bestDx = dx;
            }

            return best == null ? null : Truncate(best.Value, 60);
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

        // double.IsFinite is not on net48.
        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool IsFinite(double x, double y) => IsFinite(x) && IsFinite(y);

        // ---------------------------------------------------------------- the walk

        /// <summary>One placed symbol, in millimetres once rescaled. Leaves this file only as a
        /// ScanSymbol.</summary>
        private sealed class Instance
        {
            public CadBlockInfo Block = null!;
            public string Key = "";
            public string Layer = "";
            public double X, Y;
            public double RotationRad;
            public bool Mirrored;
            /// <summary>The plan it is in, -1 when in none: set where InPlan is.</summary>
            public int Cluster = -1;
            public double WidthMm, HeightMm;
            public int Depth;
            public IReadOnlyDictionary<string, string> Attributes = null!;
            public bool NearWall;
            public bool InPlan;

            /// <summary>The insert this one was placed inside, null at depth 0.</summary>
            public Instance? Parent;

            /// <summary>False when the walk handed over a NaN or infinite position: the
            /// instance is counted, but kept out of every "where is it" question.</summary>
            public bool Placed = true;
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

            public int WallPairs;

            /// <summary>The segments the wall test paired, kept for the near-wall pass when
            /// the layer pairs like walls; null otherwise.</summary>
            public List<Segment>? WallSegments;
            public double PairedLengthShare;
            public bool PairsLikeWalls;
            public int WallTiles;

            /// <summary>Straight pieces from model space, blocks and proxy graphics, as
            /// (x1, y1, x2, y2). Hatch boundaries and dimension pieces are counted, not kept.</summary>
            public List<(double X1, double Y1, double X2, double Y2)> Pieces =
                new List<(double X1, double Y1, double X2, double Y2)>();

            /// <summary>Per piece, the innermost insert it was drawn in; null when loose.</summary>
            public List<Instance?> PieceOwners = new List<Instance?>();

            /// <summary>Pieces as classifier segments on a layer, built only when asked for.</summary>
            public static List<Segment> Segments(List<(double X1, double Y1, double X2, double Y2)> pieces, string layer)
            {
                var segments = new List<Segment>(pieces.Count);
                foreach (var p in pieces)
                {
                    segments.Add(new Segment(new Point(p.X1, p.Y1), new Point(p.X2, p.Y2)) { Layer = layer });
                }
                return segments;
            }
        }

        private sealed class CensusSink : ICadSink, ICadSymbolSink, ICadSymbolScopeSink
        {
            public readonly Dictionary<string, LayerTally> Layers = new Dictionary<string, LayerTally>(StringComparer.Ordinal);
            public readonly List<Instance> Instances = new List<Instance>();
            public readonly List<CensusText> Texts = new List<CensusText>();

            public bool HasExtent;
            public double MinX = double.MaxValue, MinY = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue;

            // The effective layer of each open insert, outermost first. Symbol pushes, SymbolEnd
            // pops once the insert's contents are through, so the top is always the insert the
            // next piece sits in. Symbol also pops to its own depth, in case a walker never
            // says SymbolEnd.
            private readonly List<(int Depth, string Layer, Instance Instance)> _open =
                new List<(int Depth, string Layer, Instance Instance)>();

            /// <summary>The insert the next piece sits in, null in model space.</summary>
            private Instance? Owner => _open.Count > 0 ? _open[_open.Count - 1].Instance : null;

            /// <summary>Anything on layer 0 inside a block is drawn on the layer of the insert
            /// that holds it, which is how AutoCAD shows it. Outside a block, 0 is 0.</summary>
            private string Effective(string? layer)
            {
                string own = layer ?? string.Empty;
                if ((own.Length == 0 || own == "0") && _open.Count > 0) return _open[_open.Count - 1].Layer;
                return own;
            }

            public void SymbolEnd(int depth)
            {
                while (_open.Count > 0 && _open[_open.Count - 1].Depth >= depth) _open.RemoveAt(_open.Count - 1);
            }

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
                layer = Effective(layer);
                LayerTally tally = Tally(layer);

                if (source == CadSource.Hatch) { tally.HatchLoops++; return; }
                if (source == CadSource.Dimension) { tally.DimensionPieces++; return; }

                if (isClosed) tally.ClosedPolylines++;
                else if (points.Count == 2) tally.Lines++;
                else tally.OpenPolylines++;

                Instance? owner = Owner;
                double run = 0;
                int last = isClosed ? points.Count : points.Count - 1;
                for (int i = 0; i < last; i++)
                {
                    var a = points[i];
                    var b = points[(i + 1) % points.Count];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double length = Math.Sqrt((dx * dx) + (dy * dy));

                    // A NaN passes "length <= 0", and one bad vertex would poison every sum
                    // of the layer and the JSON with it: skip the pieces it touches.
                    if (!IsFinite(length) || length <= 0 || !IsFinite(a.X, a.Y) || !IsFinite(b.X, b.Y)) continue;

                    run += length;
                    tally.StraightLength += length;
                    if (IsAxisAligned(dx, dy)) tally.AxisAlignedLength += length;
                    tally.Pieces.Add((a.X, a.Y, b.X, b.Y));
                    tally.PieceOwners.Add(owner);
                    GrowBySegment(a, b);
                }

                if (source == CadSource.Geometry) tally.LooseLength += run; else tally.InBlocksLength += run;
                if (!isClosed) tally.OpenRunLengths.Add(run);
            }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source)
            {
                layer = Effective(layer);
                LayerTally tally = Tally(layer);
                if (source == CadSource.Hatch) { tally.HatchLoops++; return; }
                if (source == CadSource.Dimension) { tally.DimensionPieces++; return; }

                tally.Arcs++;
                double length = 0;
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    double dx = points[i + 1].X - points[i].X, dy = points[i + 1].Y - points[i].Y;
                    double chord = Math.Sqrt((dx * dx) + (dy * dy));
                    if (!IsFinite(chord)) continue;
                    length += chord;

                    // ModelSource keeps an arc the transform kept round as an arc, and only an
                    // arc it squashed as chords; only the chords size the drawing there.
                    if (parameters == null) GrowBySegment(points[i], points[i + 1]);
                }

                tally.ArcLength += length;
                if (source == CadSource.Geometry) tally.LooseLength += length; else tally.InBlocksLength += length;
            }

            public void Text(double x, double y, double height, string value, string layer, CadSource source)
            {
                layer = Effective(layer);
                LayerTally tally = Tally(layer);
                if (source == CadSource.Dimension) { tally.DimensionPieces++; return; }
                if (source == CadSource.Hatch) return;

                string clean = ModelSource.CleanText(value).Trim();
                if (clean.Length == 0) return;

                tally.Texts++;

                // Counted, but only a text with a real position is kept for "what is written near".
                if (!IsFinite(x, y)) return;
                Texts.Add(new CensusText { X = x, Y = y, Height = IsFinite(height) ? height : 0, Value = clean, Layer = layer });
            }

            public void Symbol(CadBlockInfo block, string layer, double x, double y, double rotationRad,
                               double scaleX, double scaleY, bool mirrored, int depth,
                               IReadOnlyDictionary<string, string> attributes)
            {
                while (_open.Count > 0 && _open[_open.Count - 1].Depth >= depth) _open.RemoveAt(_open.Count - 1);

                // An insert on layer 0 inside a block takes the layer its parent was placed on.
                string effective = Effective(layer);
                Instance? parent = Owner;

                Tally(effective).Inserts++;
                double width = Math.Abs(block.Width * scaleX), height = Math.Abs(block.Height * scaleY);
                (double cx, double cy) = DrawnCentre(block, x, y, rotationRad, scaleX, scaleY, mirrored);
                var instance = new Instance
                {
                    Parent = parent,
                    Block = block,
                    Layer = effective,
                    X = cx,
                    Y = cy,
                    RotationRad = IsFinite(rotationRad) ? rotationRad : 0,
                    Mirrored = mirrored,
                    Placed = IsFinite(cx, cy),
                    WidthMm = IsFinite(width) ? width : 0,
                    HeightMm = IsFinite(height) ? height : 0,
                    Depth = depth,
                    Attributes = attributes,
                };
                Instances.Add(instance);
                _open.Add((depth, effective, instance));
            }

            /// <summary>
            /// Where an insert is drawn: the centre of its definition's box, carried to world by
            /// the insert's rotation, scale and mirror. Not the insert point: a block whose base
            /// point sits far from its own linework puts the insert point there too. On a real
            /// drawing every light's base point was kilometres off the plan while its linework
            /// sat in the rooms, so no light counted as inside a plan or near a wall. A block
            /// that draws nothing has no box, and keeps its insert point.
            /// </summary>
            internal static (double X, double Y) DrawnCentre(CadBlockInfo block, double x, double y, double rotationRad,
                                                             double scaleX, double scaleY, bool mirrored)
            {
                if (!block.HasGeometry) return (x, y);
                double lx = (block.MinX + block.MaxX) / 2, ly = (block.MinY + block.MaxY) / 2;
                double cos = Math.Cos(rotationRad), sin = Math.Sin(rotationRad);
                double flip = mirrored ? -1 : 1;
                // Columns of the insert's linear map: x axis scaleX * (cos, sin), y axis
                // scaleY * (-sin, cos), turned over when mirrored.
                double wx = x + (scaleX * cos * lx) - (flip * scaleY * sin * ly);
                double wy = y + (scaleX * sin * lx) + (flip * scaleY * cos * ly);
                return IsFinite(wx, wy) ? (wx, wy) : (x, y);
            }

            /// <summary>
            /// Grows the extent by one segment exactly when ModelSource.Read (default filter)
            /// would keep it as a classifier segment, so Units.Resolve sees the same box here as
            /// in the wall pipeline: straight pieces from model space and blocks, chords of
            /// non-conformal arcs, never hatch or dimension pieces or true arcs, nothing under
            /// ModelSink's 1e-3 unit minimum. What remains different: the pane passes its own
            /// layer filter (include/exclude, roles), which the census cannot know, and a
            /// NaN/infinite point that ModelSource keeps is skipped here.
            /// </summary>
            private void GrowBySegment((double X, double Y) a, (double X, double Y) b)
            {
                const double MinSegmentLengthSquared = 1e-6; // ModelSource.ModelSink's
                double dx = b.X - a.X, dy = b.Y - a.Y;
                if (!IsFinite(a.X, a.Y) || !IsFinite(b.X, b.Y) || (dx * dx) + (dy * dy) < MinSegmentLengthSquared) return;
                Grow(a.X, a.Y);
                Grow(b.X, b.Y);
            }

            private void Grow(double x, double y)
            {
                if (!IsFinite(x, y)) return;
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
                // One key per definition, not per insert: an anonymous key is a SHA-1 of the
                // block's shape, and a drawing inserts the same few blocks thousands of times.
                // CadBlockInfo is one object per definition and compares by reference.
                var keys = new Dictionary<CadBlockInfo, string>();
                foreach (Instance instance in Instances)
                {
                    if (!keys.TryGetValue(instance.Block, out string? key))
                    {
                        key = ClassKey(instance.Block, scale);
                        keys[instance.Block] = key;
                    }
                    instance.Key = key;
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
