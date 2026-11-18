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
        public List<CensusCount> Layers = new List<CensusCount>();
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
        public int WallPairsUnmerged;
        public double PairedLengthShare;
        public bool PairsLikeWalls;
        public string? WallsSkipped;
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
                    ["pairs_unmerged"] = layer.WallPairsUnmerged,
                    ["paired_length_share"] = layer.PairedLengthShare,
                    ["pairs_like_walls"] = layer.PairsLikeWalls,
                    ["skipped"] = layer.WallsSkipped,
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

            return new Dictionary<string, object?>
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
        }
    }

    internal static class ElectricalCensus
    {
        /// <summary>Reads a DWG or DXF and takes its census.</summary>
        public static ElectricalCensusResult Read(string path, CensusOptions? options = null)
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

            result.Drawing.AecRefused = AecVerdict(document, sink, out string? aecWarning);
            result.Drawing.AecWarning = aecWarning;

            clock.Restart();
            List<Wall> walls = TestLayersForWalls(sink, o);
            result.TimingsSeconds["walls"] = clock.Elapsed.TotalSeconds;

            clock.Restart();
            List<PlanCluster> clusters = BuildPlans(sink, walls, o, result);
            result.TimingsSeconds["plans"] = clock.Elapsed.TotalSeconds;

            clock.Restart();
            MarkNearWalls(sink, o);
            Dictionary<string, List<CensusCount>> nearTexts = NearTexts(sink, o);
            result.TimingsSeconds["proximity"] = clock.Elapsed.TotalSeconds;

            BuildBlockClasses(sink, o, result);
            foreach (CensusBlockClass block in result.Blocks)
            {
                if (nearTexts.TryGetValue(block.Key, out List<CensusCount>? near)) block.NearTexts = near;
            }
            BuildLayers(sink, o, result);
            BuildTotals(sink, result);

            clock.Restart();
            BuildLegend(sink, clusters, o, result);
            result.TimingsSeconds["legend"] = clock.Elapsed.TotalSeconds;

            // Exploded: every symbol-like insert sits on the architectural background, so the
            // electrical symbols themselves can only be loose linework.
            result.SymbolsAreExploded = !sink.Instances.Any(instance =>
                instance.Block.NestedInserts == 0 && !sink.Layers[instance.Layer].PairsLikeWalls);

            return result;
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
        private static List<Wall> TestLayersForWalls(CensusSink sink, CensusOptions o)
        {
            var wallLike = new List<Wall>();
            double sMin = Units.DefaultMinWallThicknessMm, sMax = Units.DefaultMaxWallThicknessMm;

            foreach (LayerTally tally in sink.Layers.Values)
            {
                if (tally.Pieces.Count > o.MaxWallTestSegments)
                {
                    tally.WallsSkipped = "too_many_segments";
                    continue;
                }

                int faces = tally.Pieces.Count(p => PieceLength(p) >= Wall.MinFaceLength);
                if (faces < o.MinWallTestSegments) continue;

                List<Segment> segments = tally.Segments();
                List<Wall> merged = CadClassifier.ClassifyWalls(CadClassifier.MergeCollinearSegments(segments), sMin, sMax);
                tally.WallPairsUnmerged = CadClassifier.ClassifyWalls(segments, sMin, sMax).Count;
                tally.WallPairs = merged.Count;

                double paired = merged.Sum(wall => wall.Centerline.Length) * 2;
                tally.PairedLengthShare = tally.StraightLength > 0 ? Math.Min(1.0, paired / tally.StraightLength) : 0;
                tally.PairsLikeWalls = tally.WallPairs >= 8 && tally.PairedLengthShare >= 0.25;

                if (tally.PairsLikeWalls) wallLike.AddRange(merged);
            }

            return wallLike;
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
            List<PlanCluster> clusters = CadClassifier.ClusterPlans(CadClassifier.DeduplicateWalls(walls), null);
            result.PlansTruncated = clusters.Count > o.MaxPlans;
            clusters = clusters.Take(o.MaxPlans).ToList();

            List<CensusText> titleTexts = sink.Texts.Where(text => ContainsAny(text.Value, TitleWords)).ToList();

            for (int i = 0; i < clusters.Count; i++)
            {
                PlanCluster cluster = clusters[i];

                // A plan's title sits under it, or close beside it.
                double minX = cluster.MinX - 2000, maxX = cluster.MaxX + 2000;
                double minY = cluster.MinY - (0.2 * cluster.Height), maxY = cluster.MaxY + 2000;

                List<string> titles = titleTexts
                    .Where(text => text.X >= minX && text.X <= maxX && text.Y >= minY && text.Y <= maxY)
                    .OrderByDescending(text => text.Height)
                    .Select(text => Truncate(text.Value, 60))
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToList();

                result.Plans.Add(new CensusPlan
                {
                    Cluster = i,
                    WidthM = Math.Round(cluster.Width / 1000.0, 2),
                    HeightM = Math.Round(cluster.Height / 1000.0, 2),
                    Walls = cluster.Walls.Count,
                    Titles = titles,
                });
            }

            foreach (Instance instance in sink.Instances)
            {
                var point = new Point(instance.X, instance.Y);
                for (int i = 0; i < clusters.Count; i++)
                {
                    if (!clusters[i].Contains(point)) continue;
                    result.Plans[i].Symbols++;
                    instance.InPlan = true;
                    break;
                }
                if (instance.InPlan) result.Totals.InsidePlans++;
            }

            return clusters;
        }

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
                if (tally.PairsLikeWalls) faces.AddRange(tally.Segments());
            }
            if (faces.Count == 0) return;

            var index = new SegmentIndex(faces, 2000);
            foreach (Instance instance in sink.Instances)
            {
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
        private static Dictionary<string, List<CensusCount>> NearTexts(CensusSink sink, CensusOptions o)
        {
            var grid = new TextGrid(sink.Texts, 1000);
            var perClass = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            foreach (Instance instance in sink.Instances)
            {
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
                    WallPairs = tally.WallPairs,
                    WallPairsUnmerged = tally.WallPairsUnmerged,
                    PairedLengthShare = Math.Round(tally.PairedLengthShare, 3),
                    PairsLikeWalls = tally.PairsLikeWalls,
                    WallsSkipped = tally.WallsSkipped,
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

        // ---------------------------------------------------------------- legend

        private static readonly string[] LegendWords = { "LEGEND", "PETUNJUK", "SIMBOL", "SYMBOL" };

        /// <summary>
        /// Reads a drawn legend: a column of different symbols off the plans, each with its
        /// description written to its right. A column of one repeated symbol is a row of
        /// sockets along a wall, not a legend, so a column needs mostly distinct classes.
        /// </summary>
        private static void BuildLegend(CensusSink sink, List<PlanCluster> clusters, CensusOptions o,
                                        ElectricalCensusResult result)
        {
            result.Totals.LegendTitleTexts = sink.Texts.Count(text => ContainsAny(text.Value, LegendWords));

            const double Margin = 1000;
            List<Instance> candidates = sink.Instances
                .Where(instance => instance.Block.NestedInserts == 0)
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

            var grid = new TextGrid(sink.Texts, 1000);
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
            public bool NearWall;
            public bool InPlan;
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

            public int WallPairs, WallPairsUnmerged;
            public double PairedLengthShare;
            public bool PairsLikeWalls;
            public string? WallsSkipped;

            /// <summary>Straight pieces from model space, blocks and proxy graphics, as
            /// (x1, y1, x2, y2). Hatch boundaries and dimension pieces are counted, not kept.</summary>
            public List<(double X1, double Y1, double X2, double Y2)> Pieces =
                new List<(double X1, double Y1, double X2, double Y2)>();

            /// <summary>The kept pieces as classifier segments, built only when asked for.</summary>
            public List<Segment> Segments()
            {
                var segments = new List<Segment>(Pieces.Count);
                foreach (var p in Pieces)
                {
                    segments.Add(new Segment(new Point(p.X1, p.Y1), new Point(p.X2, p.Y2)) { Layer = Name });
                }
                return segments;
            }
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
