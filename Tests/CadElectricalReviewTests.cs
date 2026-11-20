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

        /// <summary>A census whose classes carry no attributes and one short near text.</summary>
        private static ElectricalCensusResult SmallCensus(int classes, int layers)
        {
            ElectricalCensusResult census = BigCensus(classes, layers);
            foreach (CensusBlockClass block in census.Blocks)
            {
                block.Attributes.Clear();
                block.NearTexts.RemoveRange(1, block.NearTexts.Count - 1);
                block.Layers.RemoveAt(1);
            }
            return census;
        }

        [Fact]
        public void Slim_census_keeps_150_classes_when_small()
        {
            // 200 classes and a typical drawing's 40 layers.
            ElectricalCensusResult census = SmallCensus(200, 40);

            // The caps alone; the budget is tested below.
            Dictionary<string, object?> slim = CadElectricalReview.SlimCensus(census, budgetChars: int.MaxValue);

            Assert.Equal(150, ((List<object?>)slim["blocks"]!).Count);
            Assert.Equal(true, slim["blocks_truncated"]);
            Assert.Equal(40, ((List<object?>)slim["layers"]!).Count);
            Assert.Equal(false, slim["layers_truncated"]);
            Assert.Equal(0, slim["budget_trimmed"]);
            Assert.False(slim.ContainsKey("timings_s"));

            // Layers stop at 150, with the flag.
            Dictionary<string, object?> layered = CadElectricalReview.SlimCensus(SmallCensus(5, 200), budgetChars: int.MaxValue);
            Assert.Equal(150, ((List<object?>)layered["layers"]!).Count);
            Assert.Equal(true, layered["layers_truncated"]);
            Assert.Equal(0, layered["budget_trimmed"]);
        }

        [Fact]
        public void Slim_census_stays_under_budget_with_150_large_classes()
        {
            ElectricalCensusResult census = BigCensus(150, 150);
            foreach (CensusBlockClass block in census.Blocks)
            {
                for (int i = 0; i < 6; i++) block.NearTexts.Add(new CensusCount { Name = "SWITCHED SOCKET OUTLET NOTE " + i, Count = 2 });
                block.Attributes.Add(new CensusAttribute { Tag = "DESCRIPTION", Samples = { "SINGLE SOCKET OUTLET", "TWIN SOCKET OUTLET" } });
            }
            Assert.True(Json(CadElectricalReview.SlimCensus(census, budgetChars: int.MaxValue)).Length > CadElectricalReview.DefaultCensusBudgetChars);

            Dictionary<string, object?> slim = CadElectricalReview.SlimCensus(census);

            int length = Json(slim).Length;
            Assert.True(length <= CadElectricalReview.DefaultCensusBudgetChars, "slim census is " + length + " chars");
            Assert.True((int)slim["budget_trimmed"]! > 0);
            var blocks = ((List<object?>)slim["blocks"]!).Select(Row).ToList();
            Assert.True(blocks.Count == 150 || (bool)slim["blocks_truncated"]!);
            Assert.All(blocks, b => Assert.True(!b.ContainsKey("near_texts") ||
                                                ((List<object?>)b["near_texts"]!).Count <= CadElectricalReview.TrimmedNearTexts));

            // Just over the budget: only the lowest-ranked classes shrink, the top one keeps everything.
            int full = Json(CadElectricalReview.SlimCensus(census, budgetChars: int.MaxValue)).Length;
            Dictionary<string, object?> near = CadElectricalReview.SlimCensus(census, budgetChars: full - 2000);
            Assert.True(Json(near).Length <= full - 2000);
            var nearBlocks = ((List<object?>)near["blocks"]!).Select(Row).ToList();
            Assert.Equal(150, nearBlocks.Count);
            Assert.Equal(9, ((List<object?>)nearBlocks[0]["near_texts"]!).Count);
            Assert.True(nearBlocks[0].ContainsKey("attributes"));
            Assert.Equal(CadElectricalReview.TrimmedNearTexts, ((List<object?>)nearBlocks[149]["near_texts"]!).Count);
            Assert.Equal(false, near["blocks_truncated"]);

            // A tighter budget drops whole classes, lowest-ranked first, and says so.
            Dictionary<string, object?> tight = CadElectricalReview.SlimCensus(census, budgetChars: 25000);
            Assert.True(Json(tight).Length <= 25000);
            var kept = ((List<object?>)tight["blocks"]!).Select(Row).Select(b => (string)b["key"]!).ToList();
            Assert.True(kept.Count < 150);
            Assert.Equal(true, tight["blocks_truncated"]);
            List<string> ranked = ((List<object?>)CadElectricalReview.SlimCensus(census, budgetChars: int.MaxValue)["blocks"]!)
                .Select(Row).Select(b => (string)b["key"]!).ToList();
            Assert.Equal(ranked.Take(kept.Count), kept);
        }

        /// <summary>A read_cad_electrical result as the tool builds it, census left out.</summary>
        private static Dictionary<string, object?> ReadResult(int familyTypes)
        {
            var model = new Dictionary<string, object?>
            {
                ["levels"] = Enumerable.Range(1, 6).Select(i => (object?)new Dictionary<string, object?>
                {
                    ["name"] = "Level " + i, ["elevation_mm"] = 3600.0 * (i - 1),
                }).ToList(),
            };
            CadElectricalReview.AddFamilyTypes(model, FamilyTypes(familyTypes));
            model["conduit_types"] = Enumerable.Range(0, 4).Select(i => (object?)new Dictionary<string, object?>
            {
                ["type"] = "Conduit with Fittings : PVC " + i, ["with_fittings"] = true,
                ["sizes_mm"] = new List<object?> { 16.0, 20.0, 25.0, 32.0, 40.0, 50.0 },
            }).ToList();
            model["cable_tray_types"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "Cable Tray with Fittings : Trunking", ["with_fittings"] = true },
            };
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = "att:00000000-0000-0000-0000-000000000000",
                ["schema"] = "cad.electrical.census/1",
                ["census"] = null,
                ["roles"] = CadElectricalMapping.Roles.Cast<object?>().ToList(),
                ["hosts"] = CadElectricalMapping.Hosts.Cast<object?>().ToList(),
                ["layer_roles"] = CadElectricalMapping.LayerRoles.Cast<object?>().ToList(),
                ["model"] = model,
                ["notes"] = new List<object?>(),
            };
        }

        /// <summary>Invented family types, low-voltage ones listed before electrical ones.</summary>
        private static IEnumerable<ModelFamilyType> FamilyTypes(int count) =>
            Enumerable.Range(0, count).Select(i => new ModelFamilyType
            {
                FamilyType = (i % 2 == 0 ? "Data Outlet : Cat6 Port " : "Socket Outlet : 13A Twin Switched ") + i.ToString("000"),
                Category = i % 2 == 0 ? "Data Devices" : "Electrical Fixtures",
                CategoryRank = i % 2 == 0 ? 5 : 0,
                Hosting = "face",
            });

        [Fact]
        public void Read_result_with_a_large_census_fits_the_budget()
        {
            ElectricalCensusResult census = BigCensus(150, 150);
            foreach (CensusBlockClass block in census.Blocks)
            {
                for (int i = 0; i < 6; i++) block.NearTexts.Add(new CensusCount { Name = "SWITCHED SOCKET OUTLET NOTE " + i, Count = 2 });
            }
            Assert.Equal(40, census.Legend.Count);

            Dictionary<string, object?> result = CadElectricalReview.WithCensus(ReadResult(200), census);

            int length = Json(result).Length;
            Assert.True(length <= CadElectricalReview.ResultBudgetChars, "result is " + length + " chars");
            var slim = Row(result["census"]);
            Assert.True((int)slim["budget_trimmed"]! > 0);
            Assert.Equal(true, slim["blocks_truncated"]);
            // Neither list is emptied for the other: each keeps its first BudgetFloor entries.
            Assert.True(((List<object?>)slim["blocks"]!).Count >= CadElectricalReview.BudgetFloor);
            Assert.True(((List<object?>)slim["layers"]!).Count >= CadElectricalReview.BudgetFloor);
            Assert.Equal(40, ((List<object?>)slim["legend"]!).Count);

            var model = Row(result["model"]);
            var types = ((List<object?>)model["family_types"]!).Select(Row).ToList();
            Assert.Equal(CadElectricalReview.MaxFamilyTypes, types.Count);
            Assert.Equal(200, model["family_types_total"]);
            Assert.Equal(true, model["family_types_truncated"]);
            // The 100 electrical types come first, so they fill all 80.
            Assert.All(types.Take(100), t => Assert.Equal("Electrical Fixtures", t["category"]));
        }

        [Fact]
        public void Read_result_with_a_small_census_is_untouched()
        {
            ElectricalCensusResult census = SmallCensus(10, 5);

            Dictionary<string, object?> result = CadElectricalReview.WithCensus(ReadResult(12), census);

            Assert.True(Json(result).Length <= CadElectricalReview.ResultBudgetChars);
            var slim = Row(result["census"]);
            Assert.Equal(0, slim["budget_trimmed"]);
            Assert.Equal(Json(CadElectricalReview.SlimCensus(census, budgetChars: int.MaxValue)), Json(slim));
            var model = Row(result["model"]);
            Assert.Equal(12, ((List<object?>)model["family_types"]!).Count);
            Assert.Equal(false, model["family_types_truncated"]);
            // Electrical first, then by name.
            List<string> names = ((List<object?>)model["family_types"]!).Select(Row).Select(t => (string)t["family_type"]!).ToList();
            Assert.StartsWith("Socket Outlet", names[0]);
            Assert.StartsWith("Data Outlet", names[6]);
        }

        [Fact]
        public void Legend_capped_at_40_with_truncated_flag()
        {
            ElectricalCensusResult census = SmallCensus(10, 5);
            for (int i = 40; i < 65; i++) census.Legend.Add(new CensusLegendRow { Block = "SYM-" + i.ToString("000"), Text = "LEGEND ROW " + i });
            census.LegendTruncated = false;

            Dictionary<string, object?> slim = CadElectricalReview.SlimCensus(census);

            var legend = (List<object?>)slim["legend"]!;
            Assert.Equal(40, legend.Count);
            Assert.Equal("SYM-000", Row(legend[0])["block"]);
            Assert.Equal(true, slim["legend_truncated"]);

            ElectricalCensusResult exact = SmallCensus(10, 5);
            Dictionary<string, object?> whole = CadElectricalReview.SlimCensus(exact);
            Assert.Equal(40, ((List<object?>)whole["legend"]!).Count);
            Assert.Equal(false, whole["legend_truncated"]);
        }

        [Fact]
        public void Slim_census_puts_symbol_sized_classes_first_and_shrinks_layers()
        {
            ElectricalCensusResult census = BigCensus(150, 5);

            Dictionary<string, object?> slim = CadElectricalReview.SlimCensus(census, budgetChars: int.MaxValue);

            var keys = ((List<object?>)slim["blocks"]!).Select(Row).Select(b => (string)b["key"]!).ToList();
            List<string> symbols = census.Blocks.Where(b => !b.IsContainer && b.WidthMm <= 2000).Select(b => b.Key).ToList();
            Assert.Equal(150, keys.Count);
            Assert.Equal(symbols, keys.Take(symbols.Count));
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

        // ─── place_cad_electrical result ────────────────────────────────

        private static Dictionary<string, object?> DevicesResult(bool ok = true) => ok
            ? new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = "socket_plan:1",
                ["count"] = 2,
                ["created"] = new List<object>
                {
                    new Dictionary<string, object?> { ["index"] = 0, ["created_id"] = 1001L },
                    new Dictionary<string, object?> { ["index"] = 1, ["created_id"] = 1002L },
                },
                ["failed"] = new List<object> { new Dictionary<string, object?> { ["index"] = 2, ["reason"] = "x" } },
                ["skipped_already_present"] = new List<object?> { 3 },
                ["review"] = new Dictionary<string, object?> { ["headline"] = "2 placed; 0 things to check." },
            }
            : new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no candidates selected" };

        private static Dictionary<string, object?> RunsResult() => new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["plan_id"] = "socket_plan:1",
            ["runs_created"] = 1,
            ["runs_failed"] = 0,
            ["created"] = new List<object?>(),
            ["failed"] = new List<object?>(),
            ["skipped"] = new List<object?>(),
            ["new_ids"] = new List<long> { 2001, 2002 },
            ["fitting_ids"] = new List<long> { 2003 },
            ["total_length_mm"] = 12345.0,
            ["open_connectors"] = new Dictionary<string, object?> { ["count"] = 1 },
            ["warnings"] = new List<string> { "run 0: device_not_placed" },
        };

        [Fact]
        public void Write_result_nests_both_halves_and_flattens_new_ids()
        {
            Dictionary<string, object?> devices = DevicesResult(), runs = RunsResult();
            Dictionary<string, object?> result = CadElectricalReview.WriteResult("socket_plan:1", devices, runs);

            Assert.True((bool)result["ok"]!);
            Assert.Equal("socket_plan:1", result["plan_id"]);
            Assert.Same(devices, result["devices"]);
            Assert.Same(runs, result["runs"]);
            Assert.Equal(new List<long> { 1001, 1002, 2001, 2002, 2003 }, result["new_ids"]);
            var review = (Dictionary<string, object?>)result["review"]!;
            Assert.Equal("Devices: 2 placed, 1 failed, 1 already present. " +
                         "Conduit: 1 runs routed, 0 failed, 0 skipped, 12.3 m.", review["headline"]);
            Assert.Equal(1, review["run_warnings"]);
            Assert.False(result.ContainsKey("error"));
        }

        [Fact]
        public void Write_result_with_no_runs_says_skipped()
        {
            Dictionary<string, object?> result = CadElectricalReview.WriteResult("socket_plan:1", DevicesResult(),
                                                                                  CadElectricalReview.SkippedRuns());
            Assert.True((bool)result["ok"]!);
            Assert.Equal("no_runs", ((Dictionary<string, object?>)result["runs"]!)["skipped"]);
            Assert.Equal(new List<long> { 1001, 1002 }, result["new_ids"]);
            Assert.EndsWith(" Conduit: no runs to route.", (string)((Dictionary<string, object?>)result["review"]!)["headline"]!);
        }

        [Fact]
        public void Write_result_is_ok_when_only_the_runs_were_built()
        {
            Dictionary<string, object?> result = CadElectricalReview.WriteResult("socket_plan:1", DevicesResult(ok: false), RunsResult());
            Assert.True((bool)result["ok"]!);
            Assert.StartsWith("Devices: none placed (no candidates selected).",
                              (string)((Dictionary<string, object?>)result["review"]!)["headline"]!);

            Dictionary<string, object?> neither = CadElectricalReview.WriteResult("socket_plan:1", DevicesResult(ok: false),
                                                                                   CadElectricalReview.SkippedRuns());
            Assert.False((bool)neither["ok"]!);
            Assert.Contains("none placed", (string)neither["error"]!);
            Assert.Empty((List<long>)neither["new_ids"]!);
        }
    }
}
