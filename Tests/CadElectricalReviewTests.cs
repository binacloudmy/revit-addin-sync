#nullable enable
// CadElectricalReview: the review card before and after placing a drawing's devices, and the
// slim census. Invented names only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class CadElectricalReviewTests
    {
        private static PlannedPoint Point(int index, string room, string role, params string[] flags)
        {
            var p = new PlannedPoint
            {
                Index = index,
                RoomName = room,
                Role = role,
                SymbolKey = role == "socket" ? "SOCKET" : "SWITCH-1G",
                FamilyType = role == "socket" ? "Socket Outlet : 13A Single" : "Switch : 1 Gang",
                Source = "cad",
                XMm = 12345.678 + index,
                YMm = -6780.125,
                ZMm = 300,
            };
            foreach (string flag in flags) CadSymbolResolve.AddFlag(p, flag);
            return p;
        }

        private static SocketPlan Plan()
        {
            var plan = new SocketPlan
            {
                Source = "cad",
                Alignment = new PlanAlignment
                {
                    Level = "Level 1", Cluster = 0, RotationDeg = 90, TxMm = 98765.4, TyMm = -43210.9,
                    Score = 0.934, Outcome = "accepted", Method = "fit",
                },
            };
            plan.Points.Add(Point(0, "Office", "socket"));
            plan.Points.Add(Point(1, "Office", "socket", "already_present"));
            plan.Points.Add(Point(2, "Office", "switch"));
            plan.Points.Add(Point(3, "Toilet 1", "socket", "wet_room"));
            plan.Points.Add(Point(4, "", "socket", "no_room", "no_wall_in_reach"));
            return plan;
        }

        private static readonly List<UnmappedClass> Unmapped = new List<UnmappedClass>
        {
            new UnmappedClass { Block = "LIGHT-A", Instances = 12, Reason = "no_family_type" },
            new UnmappedClass { Block = "anon:1a2b3c4d", Instances = 40, Reason = "role_unknown" },
        };

        private static string Json(object value) => JsonSerializer.Serialize(value);

        private static Dictionary<string, object?> Row(object? row) => (Dictionary<string, object?>)row!;

        [Fact]
        public void Headline_counts_drawn_placed_and_excluded()
        {
            Dictionary<string, object?> review = CadElectricalReview.PlanReview(Plan(), Unmapped, 0);

            Assert.Equal("Level 1: 5 drawn, 3 will be placed, 2 excluded. Alignment 0.93.", review["headline"]);
            Assert.Equal(5, review["drawn"]);
            Assert.Equal(3, review["will_place"]);
            Assert.Equal(2, review["excluded"]);
            Assert.Equal("accepted", Row(review["alignment"])["outcome"]);
        }

        [Fact]
        public void Room_by_role_table_counts_drawn_and_will_place()
        {
            var rooms = ((List<object?>)CadElectricalReview.PlanReview(Plan(), Unmapped, 0)["rooms"]!).Select(Row).ToList();

            Assert.Equal(
                new[] { "Office/socket 2/1", "Office/switch 1/1", "Toilet 1/socket 1/1", "(no room)/socket 1/0" },
                rooms.Select(r => r["room"] + "/" + r["role"] + " " + r["drawn"] + "/" + r["will_place"]));
        }

        [Fact]
        public void Lights_and_fans_appear_by_role_with_their_ceiling_flags()
        {
            SocketPlan plan = Plan();
            plan.Points.Add(Point(5, "Office", "light"));
            plan.Points.Add(Point(6, "Office", "light", "no_ceiling"));
            plan.Points.Add(Point(7, "Office", "fan", "no_height"));
            Dictionary<string, object?> review = CadElectricalReview.PlanReview(plan, Unmapped, 0);

            var rooms = ((List<object?>)review["rooms"]!).Select(Row)
                .ToDictionary(r => r["room"] + "/" + r["role"], r => r["drawn"] + "/" + r["will_place"]);
            Assert.Equal("2/1", rooms["Office/light"]);
            Assert.Equal("1/0", rooms["Office/fan"]);

            var flagged = ((List<object?>)review["flagged"]!).Select(Row).ToDictionary(r => (string)r["flag"]!);
            Assert.Equal("excluded", flagged["no_ceiling"]["default"]);
            Assert.Equal("excluded", flagged["no_height"]["default"]);
        }

        [Fact]
        public void Flags_are_grouped_with_their_default()
        {
            var flagged = ((List<object?>)CadElectricalReview.PlanReview(Plan(), Unmapped, 0)["flagged"]!).Select(Row)
                .ToDictionary(r => (string)r["flag"]!);

            Assert.Equal(new[] { "already_present", "no_room", "no_wall_in_reach", "wet_room" },
                         flagged.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("excluded", flagged["already_present"]["default"]);
            Assert.Equal("kept", flagged["wet_room"]["default"]);
            Assert.Equal("kept", flagged["no_room"]["default"]);
            Assert.Equal("excluded", flagged["no_wall_in_reach"]["default"]);
            Assert.Equal(1, flagged["wet_room"]["count"]);

            Dictionary<string, object?> item = Row(((List<object?>)flagged["wet_room"]["items"]!).Single());
            Assert.Equal(3, item["index"]);
            Assert.Equal("Toilet 1", item["room"]);
            Assert.Equal("socket", item["role"]);
            Assert.Equal("SOCKET", item["block"]);
        }

        [Fact]
        public void Flagged_items_stop_at_50_but_the_count_does_not()
        {
            var plan = new SocketPlan();
            for (int i = 0; i < 70; i++) plan.Points.Add(Point(i, "Office", "socket", "no_room"));

            Dictionary<string, object?> row = Row(((List<object?>)CadElectricalReview.PlanReview(plan, Unmapped, 0)["flagged"]!).Single());

            Assert.Equal(70, row["count"]);
            Assert.Equal(50, ((List<object?>)row["items"]!).Count);
            Assert.Equal(true, row["truncated"]);
        }

        [Fact]
        public void Unmapped_classes_carry_instance_counts()
        {
            var rows = ((List<object?>)CadElectricalReview.PlanReview(Plan(), Unmapped, 7)["unmapped_classes"]!).Select(Row).ToList();

            Assert.Equal("anon:1a2b3c4d", rows[0]["block"]);
            Assert.Equal(40, rows[0]["instances"]);
            Assert.Equal("role_unknown", rows[0]["reason"]);
            Assert.Equal(12, rows[1]["instances"]);
        }

        [Fact]
        public void Notes_carry_the_mapping_notes_and_other_plans()
        {
            var notes = (List<object?>)CadElectricalReview.PlanReview(Plan(), Unmapped, 7, new[] { "host_defaulted: SOCKET -> wall" })["notes"]!;

            Assert.Equal("host_defaulted: SOCKET -> wall", notes[0]);
            Assert.Contains("7 mapped instances", (string)notes[1]!);
        }

        [Fact]
        public void Family_types_count_what_will_be_placed()
        {
            var rows = ((List<object?>)CadElectricalReview.PlanReview(Plan(), Unmapped, 0)["family_types"]!).Select(Row).ToList();

            Assert.Equal("Socket Outlet : 13A Single", rows[0]["family_type"]);
            Assert.Equal(2, rows[0]["will_place"]);
            Assert.Equal("Switch : 1 Gang", rows[1]["family_type"]);
            Assert.Equal(1, rows[1]["will_place"]);
        }

        [Fact]
        public void Outside_footprint_is_a_count_not_a_flag()
        {
            Dictionary<string, object?> review = CadElectricalReview.PlanReview(Plan(), Unmapped, 0, null, 6);

            Assert.Equal(6, review["outside_model_footprint"]);
            var flags = ((List<object?>)review["flagged"]!).Select(Row).Select(r => (string)r["flag"]!);
            Assert.DoesNotContain("outside_model_footprint", flags);
            Assert.Equal(5, review["drawn"]);
            Assert.Contains((List<object?>)review["notes"]!, n => ((string)n!).StartsWith("6 mapped instances", StringComparison.Ordinal) &&
                                                                  ((string)n!).IndexOf("outside", StringComparison.Ordinal) >= 0);

            Dictionary<string, object?> none = CadElectricalReview.PlanReview(Plan(), Unmapped, 0);
            Assert.Equal(0, none["outside_model_footprint"]);
            Assert.Empty((List<object?>)none["notes"]!);
        }

        [Fact]
        public void Alignment_shows_cropped_and_uncropped_scores()
        {
            SocketPlan plan = Plan();
            plan.Alignment!.ScoreUncropped = 0.31234;
            plan.Alignment.InsideShare = 0.33333;
            plan.Alignment.ModelCoverage = 0.61789;

            Dictionary<string, object?> a = Row(CadElectricalReview.PlanReview(plan, Unmapped, 0)["alignment"]);

            Assert.Equal(0.934, a["score"]);
            Assert.Equal(0.312, a["score_uncropped"]);
            Assert.Equal(0.333, a["inside_share"]);
            Assert.Equal(0.618, a["model_coverage"]);
            Assert.Null(a["pick"]);

            plan.Alignment.Pick = 2;
            Assert.Equal(2, Row(CadElectricalReview.PlanReview(plan, Unmapped, 0)["alignment"])["pick"]);
        }

        [Fact]
        public void Review_carries_no_coordinates()
        {
            string json = Json(CadElectricalReview.PlanReview(Plan(), Unmapped, 3));

            foreach (string number in new[] { "12345", "6780", "98765", "43210" })
                Assert.DoesNotContain(number, json);
            foreach (string key in new[] { "\"x", "\"y", "\"z", "xyz", "\"tx", "\"ty", "facing" })
                Assert.DoesNotContain(key, json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Place_review_counts_what_needs_a_look()
        {
            List<PlannedPoint> placed = Plan().Points.Where(p => !p.Excluded).ToList();
            placed[0].Flags.Add("provisional_alignment");

            Dictionary<string, object?> review = CadElectricalReview.PlaceReview(placed, 2, 1);

            Dictionary<string, object?> needs = Row(review["needs_review"]);
            Assert.Equal(2, needs["facing_uncorrected"]);
            Assert.Equal(1, needs["elevation_unset"]);
            Assert.Equal(0, needs["no_room"]);
            Assert.Equal(1, needs["wet_room"]);
            Assert.Equal(1, needs["provisional_alignment"]);
            Assert.Equal("3 placed; 5 things to check.", review["headline"]);
        }

        private static ElectricalCensusResult BigCensus(int classes, int layers)
        {
            var census = new ElectricalCensusResult();
            for (int i = 0; i < classes; i++)
            {
                bool symbol = i % 3 != 0;
                census.Blocks.Add(new CensusBlockClass
                {
                    Key = (symbol ? "SYM-" : "FURN-") + i.ToString("000"),
                    Count = 1000 - i,
                    Layers = { new CensusCount { Name = "E-POWER", Count = 900 }, new CensusCount { Name = "0", Count = 100 - i / 2 } },
                    MinDepth = 0,
                    MaxDepth = 1,
                    WidthMm = symbol ? 300 : 2400,
                    HeightMm = symbol ? 200 : 1200,
                    IsContainer = i % 10 == 0,
                    Attributes = { new CensusAttribute { Tag = "CIRCUIT", Samples = { "L1/3", "L2/4" } } },
                    NearTexts =
                    {
                        new CensusCount { Name = "13A OUTLET", Count = 40 },
                        new CensusCount { Name = "RM 1-0" + (i % 9), Count = 12 },
                        new CensusCount { Name = "2x13A", Count = 3 },
                    },
                    NearWall = 800,
                });
            }
            census.BlocksTruncated = false;
            for (int i = 0; i < layers; i++)
            {
                census.Layers.Add(new CensusLayer
                {
                    Name = "E-LAYER-" + i.ToString("000"), Lines = 100, OpenPolylines = 20, ClosedPolylines = 5, Arcs = 3,
                    Texts = 9, Inserts = 50, LengthM = 123.456, OpenPolylinesOver1m = 4, PairsLikeWalls = i % 7 == 0,
                });
            }
            for (int i = 0; i < 6; i++) census.Plans.Add(new CensusPlan { Cluster = i, WidthM = 40, HeightM = 30, Walls = 300, Titles = { "PLAN LEVEL " + i } });
            for (int i = 0; i < 40; i++) census.Legend.Add(new CensusLegendRow { Block = "SYM-" + i.ToString("000"), Text = "13A SWITCHED SOCKET OUTLET " + i });
            return census;
        }

        [Fact]
        public void Slim_census_stays_under_40kb_for_150_classes()
        {
            ElectricalCensusResult census = BigCensus(150, 150);

            Dictionary<string, object?> slim = CadElectricalReview.SlimCensus(census);
            int bytes = Encoding.UTF8.GetByteCount(Json(slim));

            Assert.True(bytes < 40 * 1024, "slim census is " + bytes + " bytes");
            var blocks = ((List<object?>)slim["blocks"]!).Select(Row).ToList();
            Assert.Equal(80, blocks.Count);
            Assert.Equal(true, slim["blocks_truncated"]);
            Assert.Equal(60, ((List<object?>)slim["layers"]!).Count);
            Assert.Equal(true, slim["layers_truncated"]);
            Assert.False(slim.ContainsKey("timings_s"));
        }

        [Fact]
        public void Slim_census_puts_symbol_sized_classes_first_and_shrinks_layers()
        {
            ElectricalCensusResult census = BigCensus(150, 5);

            Dictionary<string, object?> slim = CadElectricalReview.SlimCensus(census);

            var keys = ((List<object?>)slim["blocks"]!).Select(Row).Select(b => (string)b["key"]!).ToList();
            List<string> symbols = census.Blocks.Where(b => !b.IsContainer && b.WidthMm <= 2000).Select(b => b.Key).ToList();
            Assert.Equal(symbols.Take(80), keys);
            Dictionary<string, object?> first = Row(((List<object?>)slim["blocks"]!)[0]);
            Assert.False(first.ContainsKey("anonymous"));
            Assert.False(first.ContainsKey("record_names"));
            Assert.True(first.ContainsKey("attributes"));

            Dictionary<string, object?> layer = Row(((List<object?>)slim["layers"]!)[0]);
            Assert.Equal(new[] { "inserts", "layer", "length_m", "open_polylines_over_1m", "pairs_like_walls" },
                         layer.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(false, slim["layers_truncated"]);
        }

        // ---------------------------------------------------------------- conduit (phase 2)

        private static PlannedRun Run(int index, double length, string start, string end, int drops, params string[] flags)
        {
            var run = new PlannedRun
            {
                Index = index, Layer = "E-CONDUIT", TypeName = "Conduit with Fittings : PVC", DiameterMm = 20, RunHeightMm = 2700,
                LengthMm = length, Drops = drops,
                Start = new RunEnd { Kind = start }, End = new RunEnd { Kind = end },
                PointsMm = { new[] { 98765.4321, -43210.987, 3700.5 }, new[] { 98765.4321 + length, -43210.987, 3700.5 } },
            };
            run.Flags.AddRange(flags);
            run.Excluded = flags.Contains("closed_loop");
            return run;
        }

        private static SocketPlan RunPlan()
        {
            SocketPlan plan = Plan();
            plan.Runs.Add(Run(0, 4000, "device", "device", 2));
            plan.Runs.Add(Run(1, 2500, "open", "device", 1, "open_end"));
            plan.Runs.Add(Run(2, 1500, "junction", "open", 0, "open_end", "junction"));
            plan.Runs.Add(Run(3, 4000, "open", "open", 0, "closed_loop"));
            return plan;
        }

        [Fact]
        public void Conduit_block_counts_runs_length_drops_and_open_ends()
        {
            var unplanned = new List<UnplannedLayer>();
            Dictionary<string, object?> block = CadElectricalReview.ConduitReview(RunPlan(), unplanned, 3, 7, "planned", 11, 5);

            Assert.Equal("planned", block["status"]);
            Assert.Equal(4, block["runs"]);
            Assert.Equal(3, block["will_route"]);
            Assert.Equal(12.0, block["total_length_m"]);
            Assert.Equal(3, block["drops"]);
            Assert.Equal(2, block["open_ends"]);
            Assert.Equal(1, block["junctions"]);
            Assert.Equal(3, block["dropped_short"]);
            Assert.Equal(7, block["free_arcs"]);
            Assert.Equal(11, block["pieces_outside_footprint"]);
            Assert.Equal(5, block["clutter_dropped"]);

            Dictionary<string, object?> layer = Row(Assert.Single((List<object?>)block["by_layer"]!));
            Assert.Equal("E-CONDUIT", layer["layer"]);
            Assert.Equal("conduit", layer["kind"]);
            Assert.Equal("Conduit with Fittings : PVC", layer["type"]);
            Assert.Equal(20.0, layer["diameter_mm"]);
            Assert.Equal(4, layer["runs"]);

            List<Dictionary<string, object?>> flagged = ((List<object?>)block["flagged"]!).Select(Row).ToList();
            Dictionary<string, object?> open = flagged.Single(f => (string)f["flag"]! == "open_end");
            Assert.Equal(2, open["count"]);
            Assert.Equal("kept", open["default"]);
            Assert.Equal(new object?[] { 1, 2 }, (List<object?>)open["runs"]!);
            Assert.Equal("excluded", flagged.Single(f => (string)f["flag"]! == "closed_loop")["default"]);
        }

        [Fact]
        public void Needs_run_height_lists_the_layers_and_no_runs()
        {
            var unplanned = new List<UnplannedLayer> { new UnplannedLayer { Layer = "E-CONDUIT", Reason = "no_run_height", Pieces = 40, LengthMm = 81234 } };
            SocketPlan plan = Plan();
            Dictionary<string, object?> block = CadElectricalReview.ConduitReview(plan, unplanned, 0, 0, "needs_run_height");

            Assert.Equal("needs_run_height", block["status"]);
            Assert.Equal(0, block["runs"]);
            Dictionary<string, object?> row = Row(Assert.Single((List<object?>)block["unplanned_layers"]!));
            Assert.Equal("E-CONDUIT", row["layer"]);
            Assert.Equal("no_run_height", row["reason"]);

            string headline = (string)CadElectricalReview.PlanReview(plan, Unmapped, 0, null, 0, block)["headline"]!;
            Assert.EndsWith(" Conduit: run height needed for 1 layer.", headline);
        }

        [Fact]
        public void Unplanned_layers_carry_their_reason()
        {
            var unplanned = new List<UnplannedLayer>
            {
                new UnplannedLayer { Layer = "E-CONDUIT-2", Reason = "no_diameter", Pieces = 3, LengthMm = 4500 },
                new UnplannedLayer { Layer = "E-CONDUIT-1", Reason = "no_conduit_type", Pieces = 9, LengthMm = 12000 },
            };
            List<Dictionary<string, object?>> rows = ((List<object?>)CadElectricalReview.ConduitReview(
                Plan(), unplanned, 0, 0, "none")["unplanned_layers"]!).Select(Row).ToList();

            Assert.Equal(new[] { "E-CONDUIT-1", "E-CONDUIT-2" }, rows.Select(r => (string)r["layer"]!));
            Assert.Equal("no_conduit_type", rows[0]["reason"]);
            Assert.Equal(9, rows[0]["pieces"]);
            Assert.Equal(12.0, rows[0]["length_m"]);
        }

        [Fact]
        public void Conduit_block_carries_no_coordinates()
        {
            SocketPlan plan = RunPlan();
            string json = Json(CadElectricalReview.PlanReview(plan, Unmapped, 0, null, 0,
                CadElectricalReview.ConduitReview(plan, new List<UnplannedLayer>(), 0, 0, "planned")));

            Assert.DoesNotContain("98765", json);
            Assert.DoesNotContain("43210", json);
            Assert.DoesNotContain("3700", json);
            Assert.DoesNotContain("points", json);
        }

        [Fact]
        public void Headline_adds_the_conduit_sentence()
        {
            SocketPlan plan = RunPlan();
            Dictionary<string, object?> review = CadElectricalReview.PlanReview(plan, Unmapped, 0, null, 0,
                CadElectricalReview.ConduitReview(plan, new List<UnplannedLayer>(), 0, 0, "planned"));

            Assert.EndsWith(" Conduit: 4 runs, 12.0 m, 3 drops, 2 open ends.", (string)review["headline"]!);
            Assert.True(review.ContainsKey("conduit"));
            Assert.False(CadElectricalReview.PlanReview(plan, Unmapped, 0).ContainsKey("conduit"));
            Assert.DoesNotContain("Conduit", (string)CadElectricalReview.PlanReview(plan, Unmapped, 0)["headline"]!);
        }

        [Fact]
        public void Trunking_runs_are_named_in_the_headline_and_have_no_open_ends()
        {
            SocketPlan plan = RunPlan();
            PlannedRun tray = Run(4, 6000, "open", "open", 0);
            tray.Layer = "E-TRUNK";
            tray.Kind = "cable_tray";
            tray.TypeName = "Cable Tray with Fittings : Trunking";
            tray.DiameterMm = null;
            tray.WidthMm = 100;
            tray.HeightMm = 50;
            plan.Runs.Add(tray);

            Dictionary<string, object?> block = CadElectricalReview.ConduitReview(plan, new List<UnplannedLayer>(), 0, 0, "planned");
            Assert.Equal(2, block["open_ends"]);
            Dictionary<string, object?> trunk = ((List<object?>)block["by_layer"]!).Select(Row).Single(l => (string)l["layer"]! == "E-TRUNK");
            Assert.Equal("cable_tray", trunk["kind"]);
            Assert.Equal(100.0, trunk["width_mm"]);

            string headline = (string)CadElectricalReview.PlanReview(plan, Unmapped, 0, null, 0, block)["headline"]!;
            Assert.EndsWith(" Conduit and trunking: 5 runs, 18.0 m, 3 drops, 2 open ends.", headline);
        }
    }
}
