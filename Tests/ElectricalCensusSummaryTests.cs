// ElectricalCensusSummary: the phase 0 answers to assumptions A1-A3, decided from a census.
// Each census here is built by hand, so the verdict rules are pinned without a drawing.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class ElectricalCensusSummaryTests
    {
        private static CensusBlockClass Symbol(string key, int count, string layer, int nearWall = 0) =>
            new CensusBlockClass
            {
                Key = key,
                Count = count,
                WidthMm = 200,
                HeightMm = 200,
                NearWall = nearWall,
                Layers = new List<CensusCount> { new CensusCount { Name = layer, Count = count } },
            };

        private static CensusLayer WallLayer(string name = "A-WALL") =>
            new CensusLayer { Name = name, Lines = 400, LengthM = 900, LooseLengthM = 900, WallPairs = 150, PairedLengthShare = 0.7, PairsLikeWalls = true };

        private static ElectricalCensusResult Census(IEnumerable<CensusBlockClass> blocks, IEnumerable<CensusLayer> layers,
                                                     int plans = 0, bool exploded = false)
        {
            var census = new ElectricalCensusResult
            {
                Blocks = blocks.ToList(),
                Layers = layers.ToList(),
                SymbolsAreExploded = exploded,
            };
            for (int i = 0; i < plans; i++) census.Plans.Add(new CensusPlan { Cluster = i, Walls = 40 });
            census.Totals.Inserts = census.Blocks.Sum(b => b.Count);
            census.Totals.Named = census.Totals.Inserts;
            return census;
        }

        [Fact]
        public void LooksElectrical_matches_the_real_layer_names()
        {
            foreach (string name in new[] { "E-LIGHT", "E-METER", "E-POWR", "LAMPU", "SOKET 13A" })
                Assert.True(ElectricalCensusSummary.LooksElectrical(name), name);
            foreach (string name in new[] { "A-STONE", "A-BLDG", "C-DRAIN", "A-WALL", "0" })
                Assert.False(ElectricalCensusSummary.LooksElectrical(name), name);
        }

        [Fact]
        public void A1_yes_when_named_blocks_sit_on_electrical_layers()
        {
            ElectricalCensusResult census = Census(
                new[] { Symbol("SOCKET-13A", 40, "E-POWER"), Symbol("DOWNLIGHT", 30, "E-LIGHT"), Symbol("CHAIR", 10, "I-FURN") },
                new[] { WallLayer() });

            Assert.Equal("YES", ElectricalCensusSummary.Answer(census).A1.Verdict);
        }

        [Fact]
        public void A1_no_when_electrical_layers_hold_only_loose_linework()
        {
            ElectricalCensusResult census = Census(
                new[] { Symbol("DOOR", 30, "A-WALL") },
                new[] { WallLayer(), new CensusLayer { Name = "E-LIGHT", Lines = 900, LengthM = 300, LooseLengthM = 300 } },
                exploded: true);

            CensusAnswers answers = ElectricalCensusSummary.Answer(census);
            Assert.Equal("NO", answers.A1.Verdict);
            Assert.Equal("CANNOT TELL", answers.A3.Verdict);
        }

        [Fact]
        public void A2_no_without_a_wall_like_layer()
        {
            ElectricalCensusResult census = Census(
                new[] { Symbol("SOCKET-13A", 40, "E-POWER") },
                new[] { new CensusLayer { Name = "A-WALL", Lines = 50, LengthM = 40, LooseLengthM = 40, WallPairs = 2 } });

            Assert.Equal("NO", ElectricalCensusSummary.Answer(census).A2.Verdict);

            ElectricalCensusResult withWalls = Census(
                new[] { Symbol("SOCKET-13A", 40, "E-POWER", nearWall: 30) }, new[] { WallLayer() }, plans: 2);
            Assert.Equal("YES", ElectricalCensusSummary.Answer(withWalls).A2.Verdict);
        }

        [Fact]
        public void A3_yes_for_an_orthogonal_line_layer()
        {
            ElectricalCensusResult census = Census(
                new[] { Symbol("SOCKET-13A", 40, "E-POWER") },
                new[]
                {
                    WallLayer(),
                    new CensusLayer { Name = "E-CONDUIT", Lines = 120, OpenPolylines = 30, LengthM = 250, LooseLengthM = 250,
                                      OpenPolylinesOver1m = 60, AxisAlignedShare = 0.93 },
                });

            Assert.Equal("YES", ElectricalCensusSummary.Answer(census).A3.Verdict);
        }

        [Fact]
        public void A3_partly_flags_wiring_arcs()
        {
            ElectricalCensusResult census = Census(
                new[] { Symbol("SOCKET-13A", 40, "E-POWER") },
                new[]
                {
                    WallLayer(),
                    new CensusLayer { Name = "E-WIRING", Arcs = 300, Lines = 20, LengthM = 180, LooseLengthM = 180,
                                      OpenPolylinesOver1m = 5, AxisAlignedShare = 0.15 },
                });

            CensusAnswer a3 = ElectricalCensusSummary.Answer(census).A3;
            Assert.Equal("PARTLY (reads as wiring arcs)", a3.Verdict);
            Assert.Contains(a3.Lines, line => line.Contains("E-WIRING") && line.Contains("(reads as wiring arcs)"));
        }

        [Fact]
        public void Caps_shorten_the_tables_but_not_the_answers()
        {
            // A furniture class tops the table; the electrical symbols, one of them spread over
            // seven layers, fall past every cap. The answers must not notice the caps.
            ACadSharp.CadDocument doc = new ACadSharp.CadDocument();
            doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
            CensusFixture.Room(doc, "A-WALL", 0, 0, 30000, 20000);
            ACadSharp.Tables.BlockRecord chair = CensusFixture.SquareBlock(doc, "CHAIR", 500);
            ACadSharp.Tables.BlockRecord socket = CensusFixture.SquareBlock(doc, "SOCKET-13A", 100);
            ACadSharp.Tables.BlockRecord light = CensusFixture.SquareBlock(doc, "DOWNLIGHT", 200);
            for (int i = 0; i < 20; i++) CensusFixture.Insert(doc, chair, 1000 + (i * 600), 10000, "I-FURN");
            string[] powerLayers = { "E-POWER", "E-POWER-1", "E-POWER-2", "E-POWER-3", "E-POWER-4", "E-POWER-5", "E-POWER-6" };
            for (int i = 0; i < 35; i++) CensusFixture.Insert(doc, socket, 1000 + (i * 700), 300, powerLayers[i % powerLayers.Length]);
            for (int i = 0; i < 15; i++) CensusFixture.Insert(doc, light, 1000 + (i * 800), 5000, "E-LIGHT");

            ElectricalCensusResult capped = ElectricalCensus.Build(doc, new CensusOptions { MaxBlockClasses = 1, MaxLayers = 1 });
            ElectricalCensusResult uncapped = ElectricalCensus.Build(doc);
            Assert.True(capped.BlocksTruncated && capped.LayersTruncated);
            Assert.Equal(5, uncapped.Blocks.Single(b => b.Key == "SOCKET-13A").Layers.Count);

            CensusAnswers a = ElectricalCensusSummary.Answer(capped), b = ElectricalCensusSummary.Answer(uncapped);
            foreach (var (left, right) in new[] { (a.A1, b.A1), (a.A2, b.A2), (a.A3, b.A3) })
            {
                Assert.Equal(right.Verdict, left.Verdict);
                Assert.Equal(right.Lines, left.Lines);
            }
            Assert.Equal("YES", b.A1.Verdict);
            Assert.Contains(b.A1.Lines, line => line.StartsWith("on electrical-looking layers: 2 classes, 50 instances", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Format_prints_the_three_answers()
        {
            ElectricalCensusResult census = Census(
                new[] { Symbol("SOCKET-13A", 40, "E-POWER", nearWall: 35) }, new[] { WallLayer() }, plans: 1);
            census.TimingsSeconds["read"] = 1.5;
            census.TimingsSeconds["walk"] = 0.5;

            string text = ElectricalCensusSummary.Format(census, "sample.dwg");

            Assert.Contains("file     sample.dwg", text);
            Assert.Contains("read+census 2.0 s", text);
            Assert.Contains("AEC      none", text);
            Assert.Contains("A1  Electrical symbols are named block inserts on electrical layers:  YES", text);
            Assert.Contains("A2  The drawing carries the architectural background:  YES", text);
            Assert.Contains("A3  Conduit is drawn as lines/polylines on its own layer:  NO", text);
            Assert.Contains("legend   0 rows read (legend titles found: 0)", text);
            Assert.Contains("truncated  none", text);
        }
    }
}
