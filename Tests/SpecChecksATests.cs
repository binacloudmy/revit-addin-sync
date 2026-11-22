using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Section A spec evaluators (Services/JkrSpec/SpecChecks.A.cs) over hand-built
    /// snapshots: each rule against the spec row in data/jkr_spec_rules.tsv, the grid /
    /// file-name parsers on their own, and the verdict rules (missing data is never a pass).
    /// </summary>
    public class SpecChecksATests
    {
        private const string File00 = "jkrAR24_5a_(BEde1A_p14-001)_A1_W00_(S)_DS_220222a.rvt";
        private const string File01 = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt";

        // ── helpers ──

        private static List<SpecResult> Run(SpecModel m, string id) => SpecChecks.Run(m).Where(r => r.CheckId == id).ToList();

        private static SpecState State(SpecModel m, string id) => SpecReport.Build(SpecChecks.Run(m)).For(id).State;

        private static List<SpecResult> Fails(List<SpecResult> rs) => rs.Where(r => SpecReport.IsFail(r.Status)).ToList();

        private static SpecType Type(string cat, string family, string type, int n = 1, long firstId = 100,
                                     double? width = null, double? height = null, double? thickness = null,
                                     string function = null, params string[] materials)
        {
            var t = new SpecType { Category = cat, FamilyName = family, TypeName = type, InstanceCount = n, TypeId = firstId + 10000 };
            for (var i = 0; i < n; i++) t.InstanceIds.Add(firstId + i);
            if (width != null) t.Numbers["Width"] = width.Value;
            if (height != null) t.Numbers["Height"] = height.Value;
            if (thickness != null) t.Numbers["Thickness"] = thickness.Value;
            if (function != null) t.Texts["Function"] = function;
            t.Materials.AddRange(materials ?? new string[0]);
            return t;
        }

        private static SpecGrid Grid(string name, double offset, bool vertical = true, long id = 0) =>
            new SpecGrid { Name = name, Offset = offset, IsVertical = vertical, Id = id };

        private static SpecModel Grids(params SpecGrid[] g) => new SpecModel { Grids = g.ToList() };

        // ── every result is bilingual; the empty model never fails or crashes in A ──

        [Fact]
        public void Empty_model_gives_no_section_A_failure_and_A1_4_is_the_only_pass()
        {
            var rs = SpecChecks.Run(new SpecModel()).Where(r => r.CheckId.StartsWith("A")).ToList();
            Assert.Empty(Fails(rs));
            Assert.DoesNotContain(rs, r => r.Note.Contains("check failed to run"));
            var passes = rs.Where(r => r.Status == "pass" && r.Coverage == SpecCoverage.Full && !r.NotApplicable).Select(r => r.CheckId).Distinct().ToList();
            Assert.Equal(new[] { "A1.4" }, passes);   // "no RC flat roof" complies, as the spec says
        }

        [Fact]
        public void Every_section_A_result_has_a_BM_and_an_English_note()
        {
            var m = Rich();
            foreach (var r in SpecChecks.Run(m).Where(r => r.CheckId.StartsWith("A")))
            {
                Assert.False(string.IsNullOrWhiteSpace(r.Note), r.CheckId + " has no note");
                Assert.False(string.IsNullOrWhiteSpace(r.Note), r.CheckId + " has no English note: " + r.Note);
            }
        }

        [Fact]
        public void Every_section_A_check_has_an_evaluator_and_only_A3_3_states_a_backend_dependency()
        {
            // A4.1 has its own evaluator now (base point + grid lines in the snapshot); it was
            // backend-only before.
            Assert.False(SpecChecks.BackendOnly.ContainsKey("A4.1"));
            Assert.True(SpecChecks.BackendOnly.ContainsKey("A3.3"));
            foreach (var id in new[] { "A1.1", "A1.2", "A1.3", "A1.4", "A1.5", "A1.6", "A1.7", "A2.1", "A2.2", "A3.1", "A3.2", "A3.3",
                                       "A3.4", "A3.5", "A4.1", "A5.1", "A5.2", "A5.3", "A6.1", "A6.2", "A6.3", "A7.1", "A7.2", "A7.3" })
                Assert.True(SpecChecks.Rules.ContainsKey(id), id);
        }

        /// <summary>A model with something for every section A rule.</summary>
        private static SpecModel Rich() => new SpecModel
        {
            FileName = File01,
            Levels = { new SpecLevel { Name = "01", Elevation = 0 }, new SpecLevel { Name = "02", Elevation = 4000 } },
            Grids = { Grid("1", 0), Grid("2", 8000), Grid("A", 0, false), Grid("B", 8000, false), Grid("1A", 4000) },
            Rooms = { new SpecRoom { Id = 1, Name = "Lobi", Number = "001", LevelName = "01", Area = 40, X = 100, Y = 100 } },
            Floors = { new SpecFloor { Id = 5, TypeName = "Slab", LevelName = "02", MinX = 0, MinY = 0, MaxX = 8000, MaxY = 8000 } },
            Ceilings = { new SpecCeiling { Id = 9, LevelName = "01", HeightOffset = 2700 } },
            Roofs = { new SpecRoof { Id = 7, TypeName = "RC Roof 150", SlopeDegrees = 0, Materials = { "Concrete" }, LayerFunctions = { "Structure" } } },
            Types =
            {
                Type("Doors", "Single", "D1", width: 850, height: 2100, thickness: 35, function: "Exterior"),
                Type("Railings", "Pagar Hiasan", "1800", height: 1800),
                Type("Walls", "Basic Wall", "Generic 200"),
            },
            Materials = { new SpecMaterial { Id = 3, Name = "Glass", UsedBy = 4 } },
            Links = { new SpecLink { Name = "jkrST24_x_w-01.rvt", Status = "Loaded" } },
        };

        // ════ A1.1 ceiling height ════

        [Fact]
        public void A1_1_a_level_with_a_ceiling_below_3000_fails_with_level_and_value()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 }, new SpecLevel { Name = "02", Elevation = 4500 } },
                Ceilings =
                {
                    new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = 3000 },
                    new SpecCeiling { Id = 2, LevelName = "02", HeightOffset = 2700 },
                    new SpecCeiling { Id = 3, LevelName = "02", HeightOffset = 2900 },
                },
            };
            var rs = Run(m, "A1.1");
            var f = Assert.Single(Fails(rs));
            Assert.Equal("Level 02: ceiling 2700 mm < 3000 mm", f.Note);
            Assert.Equal(new long[] { 2, 3 }, f.ElementIds);
            Assert.Contains(rs, r => r.Status == "pass" && r.ElementCount == 1);
            Assert.Equal(SpecState.Fail, State(m, "A1.1"));
        }

        [Fact]
        public void A1_1_basement_needs_only_2500_by_elevation_or_name()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "Lower", Elevation = -3500 }, new SpecLevel { Name = "Aras B1", Elevation = 100 } },
                Ceilings =
                {
                    new SpecCeiling { Id = 1, LevelName = "Lower", HeightOffset = 2600 },
                    new SpecCeiling { Id = 2, LevelName = "Aras B1", HeightOffset = 2500 },
                    new SpecCeiling { Id = 3, LevelName = "Basement 2", HeightOffset = 2400 },
                },
            };
            var f = Assert.Single(Fails(Run(m, "A1.1")));
            Assert.Contains("Level Basement 2 (basement): ceiling 2400 mm < 2500 mm", f.Note);
        }

        [Fact]
        public void A1_1_no_ceilings_is_unjudged_never_a_pass()
        {
            var rs = Run(new SpecModel { Levels = { new SpecLevel { Name = "01" } } }, "A1.1");
            var r = Assert.Single(rs);
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("No ceilings modelled", r.Note);
        }

        [Fact]
        public void A1_1_a_level_with_rooms_but_no_ceiling_goes_to_a_person()
        {
            var m = new SpecModel
            {
                Ceilings = { new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = 3200 } },
                Rooms = { new SpecRoom { Name = "Bilik", LevelName = "02", Area = 20 } },
            };
            var rs = Run(m, "A1.1");
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.StartsWith("Level 02"));
            Assert.Equal(SpecState.NeedsPerson, State(m, "A1.1"));
        }

        private static List<List<SpecPoint>> Box(double x0, double y0, double x1, double y1) => new List<List<SpecPoint>>
        {
            new List<SpecPoint>
            {
                new SpecPoint { X = x0, Y = y0 }, new SpecPoint { X = x1, Y = y0 },
                new SpecPoint { X = x1, Y = y1 }, new SpecPoint { X = x0, Y = y1 },
            },
        };

        private static SpecFloor FloorAt(long id, string level, double top, double x0, double y0, double x1, double y1, string type = "Slab") =>
            new SpecFloor { Id = id, TypeName = type, LevelName = level, TopElevation = top, Loops = Box(x0, y0, x1, y1),
                            MinX = x0, MinY = y0, MaxX = x1, MaxY = y1, Area = (x1 - x0) * (y1 - y0) / 1e6 };

        private static SpecRoom RoomAt(long id, string number, string name, string level, double baseElev, double x0, double y0, double x1, double y1, double height = 3000) =>
            new SpecRoom { Id = id, Number = number, Name = name, LevelName = level, Placed = true, Area = (x1 - x0) * (y1 - y0) / 1e6,
                           X = (x0 + x1) / 2, Y = (y0 + y1) / 2, BaseElevation = baseElev, Height = height, Loops = Box(x0, y0, x1, y1) };

        [Fact]
        public void A1_1_a_ceiling_is_measured_from_the_highest_floor_under_it_and_names_the_room()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 } },
                Floors =
                {
                    FloorAt(10, "01", 0, 0, 0, 10000, 10000),
                    FloorAt(11, "01", 900, 0, 0, 4000, 4000),     // a raised floor in one corner
                },
                Rooms = { RoomAt(20, "R1", "Stor", "01", 0, 0, 0, 4000, 4000) },
                Ceilings =
                {
                    new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = 3300, BottomElevation = 3300, Loops = Box(0, 0, 4000, 4000) },
                    new SpecCeiling { Id = 2, LevelName = "01", HeightOffset = 3300, BottomElevation = 3300, Loops = Box(4000, 0, 10000, 10000) },
                },
            };
            var f = Assert.Single(Fails(Run(m, "A1.1")));
            Assert.Equal("Level 01: ceiling 2400 mm < 3000 mm — R1 Stor 2400 mm", f.Note);
            Assert.Equal(new long[] { 1 }, f.ElementIds);        // ceiling 2 is 3300 over the main floor
        }

        [Fact]
        public void A1_1_two_floors_under_one_ceiling_is_listed_for_a_person_not_failed()
        {
            // AI-504: a raised corner (no share threshold) or a small plinth (no pad size) means two floors.
            // Choosing one needs a number JKR does not give: the ceiling is listed, never failed.
            var corner = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 } },
                Floors =
                {
                    FloorAt(10, "01", 0, 0, 0, 10000, 10000),
                    FloorAt(11, "01", 600, 9000, 9000, 10500, 10500),   // a raised corner under one sample point of 36
                },
                Ceilings = { new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = 3100, BottomElevation = 3100, Loops = Box(0, 0, 10000, 10000) } },
            };
            var plinth = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 } },
                Floors = { FloorAt(10, "01", 0, 0, 0, 3000, 3000), FloorAt(11, "01", 2600, 1000, 1000, 1700, 1700) },
                Ceilings = { new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = 3200, BottomElevation = 3200, Loops = Box(0, 0, 3000, 3000) } },
            };
            foreach (var m in new[] { corner, plinth })
            {
                var rs = Run(m, "A1.1");
                Assert.Empty(Fails(rs));
                Assert.DoesNotContain(rs, r => r.Status == "pass");
                var row = Assert.Single(rs);
                Assert.Equal(SpecCoverage.Context, row.Coverage);
                Assert.NotEqual("", row.IssueId);
                Assert.True(SpecPersonDecides.IsPersonDecides(row));
                Assert.Contains("a person decides", row.Note);
                Assert.Equal(SpecState.NeedsPerson, State(m, "A1.1"));
            }
        }

        [Fact]
        public void A1_1_one_floor_whose_level_is_not_a_story_level_is_listed_no_slack()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "Aras 01", Elevation = 0, IsStory = true }, new SpecLevel { Name = "Beam 01", Elevation = 100, IsStory = false } },
                Floors = { FloorAt(10, "Beam 01", 100, 0, 0, 3000, 3000) },
                Ceilings = { new SpecCeiling { Id = 1, LevelName = "Aras 01", HeightOffset = 2000, BottomElevation = 2000, Loops = Box(0, 0, 3000, 3000) } },
            };
            var rs = Run(m, "A1.1");
            Assert.Empty(Fails(rs));
            Assert.Single(rs, r => r.Coverage == SpecCoverage.Context);
        }

        [Fact]
        public void A1_1_with_no_floor_under_it_a_ceiling_is_measured_from_its_level()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 } },
                Floors = { FloorAt(10, "01", 900, 20000, 20000, 25000, 25000) },   // elsewhere in plan
                Ceilings = { new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = 2800, BottomElevation = 2800, Loops = Box(0, 0, 4000, 4000) } },
            };
            var f = Assert.Single(Fails(Run(m, "A1.1")));
            Assert.Equal("Level 01: ceiling 2800 mm < 3000 mm", f.Note);
        }

        [Fact]
        public void Plan_point_in_polygon_respects_holes_and_area_subtracts_them()
        {
            var ring = Box(0, 0, 10000, 10000);
            ring.Add(Box(2000, 2000, 8000, 8000)[0]);
            Assert.True(SpecPlan.Inside(ring, 1000, 1000));
            Assert.False(SpecPlan.Inside(ring, 5000, 5000));
            Assert.False(SpecPlan.Inside(ring, 11000, 5000));
            Assert.Equal(100e6 - 36e6, SpecPlan.Area(ring), 3);
            var pts = SpecPlan.Samples(ring, 64);
            Assert.InRange(pts.Count, 16, 128);
            Assert.All(pts, p => Assert.True(SpecPlan.Inside(ring, p.X, p.Y)));
            // A thin strip still gets points.
            Assert.NotEmpty(SpecPlan.Samples(Box(0, 0, 3780, 730), 64));
        }

        // ════ A1.2 door size ════

        [Theory]
        [InlineData("Double Flush", "1500x2100", true)]
        [InlineData("M_Door-Dbl-Panel", "x", true)]
        [InlineData("Pintu", "(PT2p600a) 1500 x 2100", true)]
        [InlineData("Pintu", "(PT1p600a) 900 x 2100", false)]
        [InlineData("Pintu Dua Daun", "1600", true)]
        [InlineData("Door 2-Leaf", "", true)]
        [InlineData("Door", "2 leaf 1800", true)]
        [InlineData("Double Swing", "900x2100", false)]
        [InlineData("Single-Flush", "Double Glazed 900", false)]
        [InlineData("Single-Flush", "0915 x 2134mm", false)]
        [InlineData("", "", false)]
        [InlineData(null, null, false)]
        public void Double_leaf_is_read_from_the_name(string family, string type, bool dbl) =>
            Assert.Equal(dbl, SpecA.IsDoubleLeaf(family, type));

        [Fact]
        public void A1_2_lists_each_door_type_below_the_minimum_as_Type_width_height()
        {
            var m = new SpecModel
            {
                Types =
                {
                    Type("Doors", "Pintu", "(PT1p600a) 850", firstId: 10, width: 850, height: 2100),
                    Type("Doors", "Double Flush", "1500", firstId: 20, width: 1500, height: 2100),
                    Type("Doors", "Double Flush", "1400", firstId: 30, width: 1400, height: 2100),
                    Type("Doors", "Single", "900x2000", firstId: 40, width: 900, height: 2000),
                    Type("Doors", "Single", "1000x2400", firstId: 50, width: 1000, height: 2400),
                },
            };
            var rs = Run(m, "A1.2");
            var fails = Fails(rs);
            // AI-504: 900 / 2100 are the minimum every door shares; the double-leaf 1500 mm is read from
            // the name, which is not a source, so the 1400 mm "double" is listed for a person, not failed.
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note.Contains("height < 2100 mm") && f.ElementId == 40);
            var person = Assert.Single(rs, r => r.Coverage == SpecCoverage.Context);
            Assert.Equal(30, person.ElementId);
            Assert.Contains("double-leaf", person.Note);
            var pass = Assert.Single(rs, r => r.Status == "pass");
            Assert.Equal(2, pass.ElementCount);
            Assert.Equal(SpecCoverage.FailOnly, pass.Coverage);
        }

        [Fact]
        public void A1_2_passing_doors_are_only_partly_checked_and_missing_sizes_go_to_a_person()
        {
            var ok = new SpecModel { Types = { Type("Doors", "Single", "1000", width: 1000, height: 2100) } };
            Assert.Equal(SpecState.Partial, State(ok, "A1.2"));
            var unread = new SpecModel { Types = { Type("Doors", "Single", "?", width: 1000) } };
            Assert.Equal("cannot_verify", Assert.Single(Run(unread, "A1.2")).Status);
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A1.2")).Status);
        }

        // ════ A1.3 main door ════

        [Fact]
        public void A1_3_main_door_thinner_than_40_fails_and_a_missing_thickness_fails()
        {
            var m = new SpecModel
            {
                Types =
                {
                    Type("Doors", "Pintu Utama", "Kayu", firstId: 10, thickness: 35),
                    Type("Doors", "Main Entrance", "Solid", firstId: 20),
                },
            };
            var fails = Fails(Run(m, "A1.3"));
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note.Contains("Thickness parameter missing"));
        }

        [Fact]
        public void A1_3_solid_main_door_passes_fully_otherwise_only_partly()
        {
            var solid = new SpecModel { Types = { Type("Doors", "Main Door", "Solid Core 45", thickness: 45) } };
            Assert.Equal(SpecState.Pass, State(solid, "A1.3"));
            var unsure = new SpecModel { Types = { Type("Doors", "Main Door", "Timber 45", thickness: 45) } };
            Assert.Equal(SpecState.Partial, State(unsure, "A1.3"));
            var hollow = new SpecModel { Types = { Type("Doors", "Main Door", "Hollow core 45", thickness: 45) } };
            Assert.Equal(SpecState.Fail, State(hollow, "A1.3"));
        }

        [Fact]
        public void A1_3_no_identifiable_main_door_lists_the_exterior_doors_as_candidates()
        {
            var m = new SpecModel
            {
                Types =
                {
                    Type("Doors", "Single", "Ext", firstId: 10, thickness: 45, function: "Exterior"),
                    Type("Doors", "Single", "Int", firstId: 20, thickness: 40, function: "Interior"),
                    Type("Doors", "Maintenance", "Hatch", firstId: 30, function: "Interior"),
                },
            };
            var rs = Run(m, "A1.3");
            var r = Assert.Single(rs);
            Assert.Equal("cannot_verify", r.Status);
            Assert.Equal(10, r.ElementId);
            Assert.Contains("candidate: Single : Ext (thickness 45 mm)", r.Note);
        }

        // ════ A1.4 flat roof ════

        [Fact]
        public void A1_4_no_RC_flat_roof_complies()
        {
            Assert.Equal(SpecState.Pass, State(new SpecModel(), "A1.4"));
            var pitched = new SpecModel { Roofs = { new SpecRoof { Id = 1, TypeName = "Metal Deck", SlopeDegrees = 15, Materials = { "Metal" } } } };
            Assert.Equal(SpecState.Pass, State(pitched, "A1.4"));
        }

        [Fact]
        public void A1_4_RC_roof_without_waterproofing_is_listed_for_a_person_never_failed()
        {
            // AI-504 (Q4): no JKR document says what a flat roof is, so the code does not decide it.
            var m = new SpecModel { Roofs = { new SpecRoof { Id = 7, TypeName = "Roof 150", SlopeDegrees = 0.5, Materials = { "jkrAR_(kt)- Konkrit tetulang" }, LayerFunctions = { "Structure" } } } };
            var rs = Run(m, "A1.4");
            Assert.Empty(Fails(rs));
            var c = Assert.Single(rs);
            Assert.Equal(SpecCoverage.Context, c.Coverage);
            Assert.Equal(7, c.ElementId);
            Assert.NotEqual("", c.IssueId);
            Assert.DoesNotContain("°", c.Note);
        }

        [Fact]
        public void A1_4_the_slope_and_the_name_no_longer_decide_anything()
        {
            // Real Dewan model, 16 Nov: "Sistem Bumbung rata konkrit tetulang" at 5.2°. It has no
            // waterproofing, so a person is asked; the same roof at 30° is asked too (no flat definition).
            foreach (var slope in new[] { 0.0, 1.9, 2.0, 5.19, 9.9, 10.0, 30.0 })
            {
                var rata = new SpecRoof { Id = 1653186, TypeName = "jkrAR_rof_BGk_gk01_50_(BGk100a)-3 @B1 Sistem Bumbung rata konkrit tetulang",
                    SlopeDegrees = slope, Materials = { "jkrAR_rof-f_(BGfl01)-3 Lepaan Simen" }, LayerFunctions = { "Structure" } };
                var rs = Run(new SpecModel { Roofs = { rata } }, "A1.4");
                Assert.Empty(Fails(rs));
                Assert.Equal(1653186, Assert.Single(rs).ElementId);
            }
            var pitched = new SpecModel { Roofs = { new SpecRoof { Id = 2, TypeName = "Metal Deck", SlopeDegrees = 26.7, Materials = { "Metal Decking" } } } };
            Assert.Equal(SpecState.Pass, State(pitched, "A1.4"));
        }

        [Fact]
        public void A1_4_membrane_layer_or_kalis_air_material_complies()
        {
            var byLayer = new SpecModel { Roofs = { new SpecRoof { Id = 1, TypeName = "RC Roof", SlopeDegrees = 0, Materials = { "Concrete" }, LayerFunctions = { "Structure", "Membrane" } } } };
            Assert.Equal(SpecState.Pass, State(byLayer, "A1.4"));
            var byMat = new SpecModel { Roofs = { new SpecRoof { Id = 1, TypeName = "RC Roof", SlopeDegrees = 0, Materials = { "Concrete", "Lapisan Kalis Air" } } } };
            Assert.Equal(SpecState.Pass, State(byMat, "A1.4"));
        }

        [Fact]
        public void A1_4_unknown_material_or_an_RC_roof_without_waterproofing_goes_to_a_person()
        {
            var noMat = new SpecModel { Roofs = { new SpecRoof { Id = 1, TypeName = "Roof", SlopeDegrees = 0 } } };
            Assert.Equal(SpecState.NeedsPerson, State(noMat, "A1.4"));
            var dbl = new SpecModel
            {
                Roofs =
                {
                    new SpecRoof { Id = 1, TypeName = "RC Slab", SlopeDegrees = 0, Materials = { "Concrete" } },
                    new SpecRoof { Id = 2, TypeName = "Metal Deck", SlopeDegrees = 10, Materials = { "Metal" } },
                },
            };
            Assert.Equal(SpecState.NeedsPerson, State(dbl, "A1.4"));
            Assert.Empty(Fails(Run(dbl, "A1.4")));
        }

        // ════ A1.5 fence ════

        [Fact]
        public void A1_5_no_fence_is_scope_for_a_person_not_a_pass()
        {
            var m = new SpecModel { Types = { Type("Railings", "Railing", "Pagar Tangga 1000", height: 1000) } };
            var r = Assert.Single(Run(m, "A1.5"));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("confirm the project scope", r.Note);
            Assert.Equal(SpecState.NeedsPerson, State(m, "A1.5"));
        }

        [Fact]
        public void A1_5_fence_below_2400_fails_and_at_2400_passes()
        {
            var m = new SpecModel
            {
                Types =
                {
                    Type("Walls", "Basic Wall", "Pagar Hiasan 1800", firstId: 10, height: 1800),
                    Type("Railings", "Decorative Fence", "2400", firstId: 20, height: 2400),
                },
            };
            var rs = Run(m, "A1.5");
            var f = Assert.Single(Fails(rs));
            Assert.Equal("Fence Basic Wall : Pagar Hiasan 1800: height 1800 mm < 2400 mm", f.Note);
            Assert.Contains(rs, r => r.Status == "pass" && r.ElementCount == 1);
        }

        // ════ A1.6 / A1.7 ════

        [Fact]
        public void A1_6_lists_every_NS_clause_the_spec_marks_manual()
        {
            var manual = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "jkr_spec_ns_clauses.tsv"))
                .Where(l => !l.StartsWith("#")).Skip(1).Select(l => l.Split('\t'))
                .Where(c => c.Length >= 4 && c[3].StartsWith("No")).ToList();
            Assert.Equal(manual.Select(c => c[0]), SpecA.ManualNsClauses.Select(c => c.Clause));
            Assert.Equal(manual.Select(c => c[1]), SpecA.ManualNsClauses.Select(c => c.Rule));
            var rs = Run(new SpecModel(), "A1.6");
            Assert.All(rs, r => Assert.Equal(SpecCoverage.Listing, r.Coverage));
            Assert.Equal(SpecState.Listed, State(new SpecModel(), "A1.6"));
        }

        [Fact]
        public void A1_7_carries_the_need_statement_remark()
        {
            var r = Assert.Single(Run(new SpecModel(), "A1.7"));
            Assert.Equal("Checked against the government's minimum Need Statement only; the design concept is not assessed.", r.Note);
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
        }

        // ════ A2 file name / model code ════

        [Fact]
        public void File_segments_keep_bracket_groups_whole()
        {
            Assert.Equal(new[] { "jkrAR24", "5a", "(BEde1A_p14-001)", "A1", "w-01", "(S)", "DS", "220222a" }, SpecA.FileSegments(File01));
            Assert.Equal(new[] { "PRJ", "AR", "Z1" }, SpecA.FileSegments("PRJ-AR-Z1.rvt"));
            Assert.Equal(new[] { "Model" }, SpecA.FileSegments("Model.rvt"));
            Assert.Empty(SpecA.FileSegments(""));
            Assert.Empty(SpecA.FileSegments(null));
        }

        [Fact]
        public void A2_1_passes_with_three_or_more_segments_and_shows_them()
        {
            var r = Assert.Single(Run(new SpecModel { FileName = File01 }, "A2.1"));
            Assert.Equal("pass", r.Status);
            Assert.Contains("8 segments (jkrAR24 | 5a | (BEde1A_p14-001) | A1 | w-01 | (S) | DS | 220222a)", r.Note);
            var bad = Assert.Single(Run(new SpecModel { FileName = "Project1_final.rvt" }, "A2.1"));
            Assert.Equal("fail", bad.Status);
            Assert.Contains("2 segments (Project1 | final)", bad.Note);
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A2.1")).Status);
        }

        [Fact]
        public void A2_1_a_name_that_does_not_open_with_the_JKR_block_fails()
        {
            // Real Dewan model, 16 Nov: "Copy of jkrAR24_…" passed on its segment count.
            var r = Assert.Single(Run(new SpecModel { FileName = "Copy of " + File01 }, "A2.1"));
            Assert.Equal("fail", r.Status);
            Assert.Contains("Copy of jkrAR24", r.Note);
            Assert.Equal("pass", Assert.Single(Run(new SpecModel { FileName = "jkrEL24-5a_(BEde1a_14-001)_A1_w-01_(S)_200506a.rvt" }, "A2.1")).Status);
        }

        [Fact]
        public void A7_2_the_jkrARm_material_library_prefix_is_JKR_naming()
        {
            Assert.True(SpecA.IsJkrMaterialName("jkrARm_clg_(mt3) Aluminium Ceiling Carrier"));
            Assert.True(SpecA.IsJkrMaterialName("jkrARm_gen_(ky-p01) timber painted (Frame Pintu Tingkap - Railing) 01"));
            Assert.False(SpecA.IsWaterproofing("jkrAR_flr-b_(LTdp01)-3 Damp Proof Membrane"));
            Assert.True(SpecA.IsWaterproofing("Roofing, EPDM Membrane"));
            Assert.True(SpecA.IsJkrMaterialName("jkrAR_rof-f_(dl01)-3 Metal Decking"));
            Assert.False(SpecA.IsJkrMaterialName("Aluminum"));
        }

        [Theory]
        [InlineData("jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt", 1, "WX01")]
        [InlineData("jkrAR24_5a_A1_W00_DS.rvt", 0, "W00")]
        [InlineData("jkrAR24_5a_A1_w-00_DS", 0, "W00")]
        [InlineData("jkrAR24_5a_A1_WX02_DS.rvt", 2, "WX02")]
        [InlineData("PRJ-AR-WX12-Tower.rvt", 12, "WX12")]
        public void Model_code_is_read_from_the_file_name(string name, int number, string canonical)
        {
            var c = SpecA.ParseModelCode(name);
            Assert.NotNull(c);
            Assert.Equal(number, c.Number);
            Assert.Equal(canonical, c.Canonical);
        }

        [Theory]
        [InlineData("Project1.rvt")]
        [InlineData("jkrAR24_(BEdeW01)_A1.rvt")]
        [InlineData("Wall01_Tower.rvt")]
        [InlineData("")]
        [InlineData(null)]
        public void No_model_code_when_none_is_written(string name) => Assert.Null(SpecA.ParseModelCode(name));

        [Fact]
        public void A2_2_reports_the_code_and_fails_when_unreadable()
        {
            var r = Assert.Single(Run(new SpecModel { FileName = File01 }, "A2.2"));
            Assert.Equal("pass", r.Status);
            Assert.Contains("WX01 (split model)", r.Note);
            Assert.Equal("fail", Assert.Single(Run(new SpecModel { FileName = "Project1.rvt" }, "A2.2")).Status);
        }

        // ════ A3 split ════

        [Fact]
        public void A3_single_W00_complies_and_the_rest_do_not_apply()
        {
            var m = new SpecModel { FileName = File00 };
            Assert.Equal(SpecState.Pass, State(m, "A3.1"));
            foreach (var id in new[] { "A3.2", "A3.3", "A3.4", "A3.5" })
                Assert.True(Assert.Single(Run(m, id)).NotApplicable, id);
            Assert.Equal(SpecState.NotApplicable, State(m, "A3.2"));
            Assert.Equal("pass", Assert.Single(Run(m, "A3.1")).Status);   // no links: no remark
        }

        [Fact]
        public void A3_1_single_W00_linked_to_ST_ME_EL_only_gets_no_remark()
        {
            var m = new SpecModel
            {
                FileName = File00,
                Links =
                {
                    new SpecLink { Name = "jkrST24_5a_A1_W00_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "jkrME24_5a_A1_WX01_DS.rvt", Status = "Loaded" },   // ME split by system: required, not a part
                    new SpecLink { Name = "jkrME24_5a_A1_WX02_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "jkrME24_5a_A1_WX03_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "jkrEL24_5a_A1_W00_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "Projek_EL01.rvt", Status = "Loaded" },
                },
            };
            var r = Assert.Single(Run(m, "A3.1"));
            Assert.Equal("pass", r.Status);
            Assert.Equal(SpecCoverage.Full, r.Coverage);
        }

        [Fact]
        public void A3_1_single_W00_with_a_part_like_link_has_no_remark()
        {
            var m = new SpecModel
            {
                FileName = File00,
                Links =
                {
                    new SpecLink { Name = "jkrST24_5a_A1_W00_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "jkrAR24_5a_A1_WX02_DS.rvt", Status = "Loaded" },
                },
            };
            var r = Assert.Single(Run(m, "A3.1"));      // AI-504 (Q16): the W00 -> comply logic only
            Assert.Equal("pass", r.Status);
            Assert.DoesNotContain("REMARK", r.Note);
            Assert.DoesNotContain("REMARK", r.Note);
            var c = SpecReport.Build(SpecChecks.Run(m)).For("A3.1");
            Assert.Equal(SpecState.Pass, c.State);
            Assert.Equal(1, c.Passed);
        }

        [Fact]
        public void A3_1_single_W00_with_many_other_links_has_no_remark()
        {
            // AI-504 (Q16): no JKR document gives a link-count rule, so no remark however many links.
            var m = new SpecModel { FileName = File00 };
            for (var i = 1; i <= 12; i++)
                m.Links.Add(new SpecLink { Name = $"Site_{i}.rvt", Status = "Loaded" });
            Assert.Single(Run(m, "A3.1"));
            Assert.Equal(SpecState.Pass, State(m, "A3.1"));
        }

        [Fact]
        public void A3_split_with_every_part_linked_is_partly_checked_and_BPEP_is_unjudged()
        {
            var m = new SpecModel
            {
                FileName = "jkrAR24_5a_A1_WX02_DS.rvt",
                Links =
                {
                    new SpecLink { Name = "jkrAR24_5a_A1_WX01_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "jkrAR24_5a_A1_WX03_DS.rvt", Status = "Unloaded" },
                    new SpecLink { Name = "jkrST24_5a_A1_WX05_DS.rvt", Status = "Loaded" },
                },
            };
            Assert.True(Assert.Single(Run(m, "A3.1")).NotApplicable);
            Assert.Equal(SpecState.Partial, State(m, "A3.2"));
            var bpep = Assert.Single(Run(m, "A3.3"));
            Assert.Equal("cannot_verify", bpep.Status);
            Assert.Equal("BPEP not available → cannot be checked", bpep.Note);
            var how = Assert.Single(Run(m, "A3.5"));
            Assert.Contains("WX01 — jkrAR24_5a_A1_WX01_DS.rvt", how.Note);
            Assert.DoesNotContain("jkrST", how.Note);   // another discipline is not a part
        }

        [Fact]
        public void A3_2_lists_each_missing_part_and_a_not_found_link()
        {
            var m = new SpecModel
            {
                FileName = "jkrAR24_5a_A1_WX04_DS.rvt",
                Links =
                {
                    new SpecLink { Name = "jkrAR24_5a_A1_WX01_DS.rvt", Status = "Loaded" },
                    new SpecLink { Name = "jkrAR24_5a_A1_WX02_DS.rvt", Status = "NotFound" },
                },
            };
            var fails = Fails(Run(m, "A3.2"));
            Assert.Contains(fails, f => f.Note == "Part WX03 is not linked in the model");
            Assert.Contains(fails, f => f.Note.StartsWith("Part link WX02 not found"));
            Assert.DoesNotContain(fails, f => f.Note.Contains("WX01") || f.Note.Contains("WX04"));
        }

        [Fact]
        public void A3_split_with_no_links_asks_for_links_first()
        {
            var m = new SpecModel { FileName = File01 };
            var a34 = Assert.Single(Run(m, "A3.4"));
            Assert.Equal(SpecCoverage.Listing, a34.Coverage);
            Assert.Contains("the model must be linked first before the A3 checks can run", a34.Note);
            Assert.Equal(SpecState.NeedsPerson, State(m, "A3.2"));
            // part 3 with no links: parts 1 and 2 are known to be missing
            Assert.Equal(2, Fails(Run(new SpecModel { FileName = "jkrAR24_A1_WX03_DS.rvt" }, "A3.2")).Count);
        }

        // ════ A5 grids ════

        [Theory]
        [InlineData("1", SpecGridKind.Number, "1")]
        [InlineData("01", SpecGridKind.Number, "1")]
        [InlineData("12", SpecGridKind.Number, "12")]
        [InlineData("A", SpecGridKind.Letter, "A")]
        [InlineData("aa", SpecGridKind.Letter, "AA")]
        [InlineData("1A", SpecGridKind.NumberSub, "1A")]
        [InlineData("1-b", SpecGridKind.NumberSub, "1B")]
        [InlineData("A1", SpecGridKind.LetterSub, "A1")]
        [InlineData("B-2", SpecGridKind.LetterSub, "B2")]
        [InlineData("GL-1", SpecGridKind.Number, "1")]
        [InlineData("GL A", SpecGridKind.Letter, "A")]
        [InlineData("Grid 3", SpecGridKind.Number, "3")]
        [InlineData("G-B", SpecGridKind.Letter, "B")]
        [InlineData("G", SpecGridKind.Letter, "G")]
        [InlineData("G1", SpecGridKind.LetterSub, "G1")]
        [InlineData("GL", SpecGridKind.Letter, "GL")]
        [InlineData("A'", SpecGridKind.Other, "A'")]
        [InlineData("1.5", SpecGridKind.Other, "1.5")]
        [InlineData("Ref Line", SpecGridKind.Other, "REF LINE")]
        [InlineData("", SpecGridKind.Other, "")]
        [InlineData(null, SpecGridKind.Other, "")]
        public void Grid_names_parse(string name, SpecGridKind kind, string core)
        {
            var g = SpecA.ParseGrid(name);
            Assert.Equal(kind, g.Kind);
            Assert.Equal(core, g.Core);
        }

        [Fact]
        public void Sub_grid_parent_and_index()
        {
            var a = SpecA.ParseGrid("3C");
            Assert.Equal(("3", 3, 3), (a.Parent, a.ParentIndex, a.Sub));
            var b = SpecA.ParseGrid("AB-04");
            Assert.Equal(("AB", 28, 4), (b.Parent, b.ParentIndex, b.Sub));
        }

        [Theory]
        [InlineData("A", 1)]
        [InlineData("Z", 26)]
        [InlineData("AA", 27)]
        [InlineData("AZ", 52)]
        [InlineData("BA", 53)]
        [InlineData("", 0)]
        [InlineData("A1", 0)]
        public void Letter_index_round_trips(string letters, int index)
        {
            Assert.Equal(index, SpecA.LetterIndex(letters));
            if (index > 0) Assert.Equal(letters, SpecA.LetterName(index));
        }

        [Fact]
        public void Number_gaps_in_any_order()
        {
            Assert.Equal(new[] { (3, 11) }, SpecA.NumberGaps(new[] { 11, 2, 3, 1 }));
            Assert.Empty(SpecA.NumberGaps(new[] { 5, 4, 3, 2, 1 }));
            Assert.Equal(new[] { (1, 3), (3, 6) }, SpecA.NumberGaps(new[] { 1, 3, 6 }));
            Assert.Empty(SpecA.NumberGaps(new int[0]));
        }

        [Fact]
        public void Letter_gaps_skip_I_and_O_and_run_past_Z()
        {
            int[] L(params string[] s) => s.Select(SpecA.LetterIndex).ToArray();
            Assert.Empty(SpecA.LetterGaps(L("A", "B", "C", "D", "E", "F", "G", "H", "J", "K")));
            Assert.Empty(SpecA.LetterGaps(L("N", "P")));
            Assert.Empty(SpecA.LetterGaps(L("H", "I", "J")));
            Assert.Empty(SpecA.LetterGaps(L("X", "Y", "Z", "AA", "AB")));
            var g = Assert.Single(SpecA.LetterGaps(L("D", "B", "A")));
            Assert.Equal(new[] { "C" }, g.Missing);
            Assert.Equal(new[] { "AA" }, Assert.Single(SpecA.LetterGaps(L("Z", "AB"))).Missing);
            Assert.Equal(new[] { "F", "G", "H", "J" }, Assert.Single(SpecA.LetterGaps(L("E", "K"))).Missing);
        }

        [Fact]
        public void A5_2_jump_is_listed_as_the_run_before_it()
        {
            var m = Grids(Grid("1", 0), Grid("2", 6000), Grid("3", 12000), Grid("11", 18000),
                          Grid("A", 0, false), Grid("B", 6000, false), Grid("C", 12000, false));
            var f = Assert.Single(Fails(Run(m, "A5.2")));
            Assert.Equal("Grids 1,2,3 → 11: sequence jumps (4–10 missing)", f.Note);
        }

        [Fact]
        public void A5_2_reversed_direction_prefixes_and_sub_grids_pass()
        {
            var m = Grids(Grid("GL-3", 0), Grid("GL-2", 6000), Grid("GL-1", 12000), Grid("GL-1A", 9000),
                          Grid("D", 0, false), Grid("C", 5000, false), Grid("B", 10000, false), Grid("A", 15000, false), Grid("B1", 12000, false));
            var rs = Run(m, "A5.2");
            Assert.Empty(Fails(rs));
            Assert.Contains("1–3 | A–D", Assert.Single(rs).Note);
            Assert.Equal(SpecState.Pass, State(m, "A5.2"));
        }

        [Fact]
        public void A5_2_letter_jump_and_out_of_order_on_plan_fail()
        {
            var jump = Grids(Grid("A", 0, false), Grid("B", 1, false), Grid("E", 2, false));
            var order = Grids(Grid("1", 0), Grid("3", 6000), Grid("2", 12000));
            Assert.Contains("1,3,2", Assert.Single(Fails(Run(order, "A5.2"))).Note);
            // mixed directions cannot be ordered on plan: only jumps count
            var mixed = Grids(Grid("1", 0), Grid("3", 6000, false), Grid("2", 12000));
            Assert.Empty(Fails(Run(mixed, "A5.2")));
        }

        [Fact]
        public void A5_2_no_grids_is_unjudged()
        {
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A5.2")).Status);
            Assert.Equal("cannot_verify", Assert.Single(Run(Grids(Grid("X'", 0)), "A5.2")).Status);
        }

        [Fact]
        public void A5_3_sub_grids_are_listed_under_their_parent_and_never_fail()
        {
            var m = Grids(Grid("1", 0), Grid("2", 6000), Grid("1A", 2000), Grid("1B", 4000),
                          Grid("A", 0, false), Grid("B", 6000, false), Grid("A1", 3000, false));
            var rs = Run(m, "A5.3");
            Assert.Empty(Fails(rs));
            Assert.Equal("Sub-grids follow the pattern: 1: 1A, 1B · A: A1", Assert.Single(rs).Note);
            Assert.Equal(SpecState.Pass, State(m, "A5.3"));
        }

        [Fact]
        public void A5_3_orphan_out_of_bay_or_skipped_sub_grids_go_to_a_person()
        {
            var m = Grids(Grid("1", 0), Grid("2", 6000), Grid("7A", 100), Grid("1A", 9000), Grid("1C", 3000));
            var rs = Run(m, "A5.3");
            Assert.Empty(Fails(rs));
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.Contains("1A lies outside bay 1–2"));
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.Contains("1B"));
        }

        [Fact]
        public void A5_1_lists_grid_and_level_counts()
        {
            var m = Grids(Grid("2", 1), Grid("1", 0), Grid("A", 0, false));
            m.Levels.Add(new SpecLevel { Name = "01" });
            var r = Assert.Single(Run(m, "A5.1"));
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
        }

        // ════ A6 rooms ════

        [Fact]
        public void A6_1_lists_rooms_per_level()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "02", Elevation = 4000 }, new SpecLevel { Name = "01", Elevation = 0 } },
                Rooms =
                {
                    new SpecRoom { Name = "Lobi", Number = "001", Department = "Pentadbiran", LevelName = "01", Area = 45 },
                    new SpecRoom { Name = "Stor", Number = "101", LevelName = "02", Area = 0 },
                },
            };
            var rs = Run(m, "A6.1");
            Assert.Equal(2, rs.Count);
            Assert.Equal("Level 01: 1 rooms — Lobi | 001 | Pentadbiran | 45.0 m²", rs[0].Note);
            Assert.Contains("1 rooms not placed", rs[1].Note);
            Assert.Equal(SpecState.Listed, State(m, "A6.1"));
        }

        [Fact]
        public void A6_2_a_slab_with_no_room_is_listed_with_floor_level_and_grid_never_failed()
        {
            var m = new SpecModel
            {
                Grids = { Grid("1", 0), Grid("2", 6000), Grid("A", 0, false), Grid("B", 6000, false) },
                Floors =
                {
                    new SpecFloor { Id = 11, TypeName = "Slab 150", LevelName = "01", MinX = 0, MinY = 0, MaxX = 6000, MaxY = 6000 },
                    new SpecFloor { Id = 12, TypeName = "Lantai Lif", LevelName = "01", MinX = 5000, MinY = 5000, MaxX = 7000, MaxY = 7000 },
                    new SpecFloor { Id = 13, TypeName = "Slab 150", LevelName = "02", MinX = 0, MinY = 0, MaxX = 6000, MaxY = 6000 },
                },
                Rooms =
                {
                    new SpecRoom { Name = "Bilik", LevelName = "01", Area = 20, X = 1000, Y = 1000 },
                    new SpecRoom { Name = "Unplaced", LevelName = "02", Area = 0, X = 1000, Y = 1000 },
                },
            };
            var rs = Run(m, "A6.2");
            Assert.Empty(Fails(rs));
            var listed = rs.Where(r => r.Coverage == SpecCoverage.Context).ToList();
            Assert.Equal(2, listed.Count);
            Assert.Contains(listed, f => f.ElementId == 13 && f.Note.StartsWith("Level 02"));
            Assert.All(listed, f => Assert.NotEqual("", f.IssueId));
            Assert.Equal(SpecCoverage.FailOnly, Assert.Single(rs, r => r.Status == "pass").Coverage);
        }

        [Fact]
        public void A6_2_a_slab_with_a_room_on_part_of_it_complies_whatever_the_share()
        {
            // AI-504 (Q5): the invented 50 % is gone. Principle only: a room is on the slab.
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 } },
                Floors =
                {
                    FloorAt(11, "01", 0, 0, 0, 10000, 10000),
                    FloorAt(12, "01", 0, 20000, 0, 30000, 10000),
                },
                Rooms =
                {
                    RoomAt(1, "001", "Bilik", "01", 0, 0, 0, 2500, 10000),        // 25% of floor 11
                    RoomAt(2, "002", "Dewan", "01", 0, 20000, 0, 27500, 10000),   // 75% of floor 12
                },
            };
            var rs = Run(m, "A6.2");
            Assert.Empty(Fails(rs));
            Assert.DoesNotContain(rs, r => r.Coverage == SpecCoverage.Context);
            var pass = Assert.Single(rs, r => r.Status == "pass");
            Assert.Equal(2, pass.ElementCount);
            Assert.Equal(SpecCoverage.Full, pass.Coverage);
        }

        [Fact]
        public void A6_2_a_room_covers_the_slab_it_stands_on_not_the_next_storeys_slab_over_it()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 }, new SpecLevel { Name = "02", Elevation = 1400 } },
                Floors =
                {
                    FloorAt(11, "01", 0, 0, 0, 5000, 5000),
                    FloorAt(12, "02", 1400, 0, 0, 5000, 5000),     // the floor over a 1.4 m-high room
                    FloorAt(13, "01", 1200, 0, 0, 2000, 2000),     // a raised platform in the room: still the room's
                },
                Rooms = { RoomAt(1, "001", "Bilik", "01", 0, 0, 0, 5000, 5000, height: 1400) },
            };
            var listed = Run(m, "A6.2").Where(r => r.Coverage == SpecCoverage.Context).ToList();
            Assert.Equal(new long[] { 12 }, listed.Select(x => x.ElementId));
        }

        [Fact]
        public void A6_2_a_stair_room_rising_through_storeys_covers_its_landing_slab()
        {
            // PTBR 17 Nov: slab 2137251 on Aras 02 lies inside TANGGA 2, which starts on Aras 01.
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 }, new SpecLevel { Name = "02", Elevation = 4000 } },
                Floors = { FloorAt(21, "02", 4000, 0, 0, 3000, 3000), FloorAt(22, "02", 4000, 5000, 0, 8000, 3000) },
                Rooms =
                {
                    RoomAt(1, "00_48", "TANGGA 2", "01", 0, 0, 0, 3000, 3000, height: 8000),
                    RoomAt(2, "00_49", "STOR", "01", 0, 5000, 0, 8000, 3000, height: 8000),
                },
            };
            // The stair covers its landing; a tall store room does not reach the next storey's slab.
            var listed = Run(m, "A6.2").Where(r => r.Coverage == SpecCoverage.Context).ToList();
            Assert.Equal(new long[] { 22 }, listed.Select(x => x.ElementId));
        }

        [Fact]
        public void A6_2_a_small_slab_is_judged_like_any_other_no_pad_rule()
        {
            // AI-504: the 1 m² pad rule is gone: a small slab with no room is listed, never skipped or failed.
            var m = new SpecModel
            {
                Floors = { FloorAt(31, "Tanah", 2652, 0, 0, 670, 670), FloorAt(32, "Tanah", 2652, 1000, 0, 1670, 670) },
            };
            var rs = Run(m, "A6.2");
            Assert.Empty(Fails(rs));
            var listed = rs.Where(r => r.Coverage == SpecCoverage.Context).ToList();
            Assert.Equal(new long[] { 31, 32 }, listed.Select(r => r.ElementId).OrderBy(x => x));
        }

        [Theory]
        [InlineData("jkrAR_flr_LFp_ls01_30_APR_ex_(LFp601a)-3", "apron")]
        [InlineData("Apron Konkrit", "apron")]
        [InlineData("Lantai Lif", "lif")]
        [InlineData("jkrAR_flr_LFp_ls02_30_SVS_in_(LFp602a)-3", "")]
        [InlineData("Paprika", "")]
        public void A6_2_space_kind_reads_the_JKR_apron_code(string type, string kind)
        {
            Assert.Equal(kind, SpecA.SpaceKind(type));
        }

        [Fact]
        public void A6_2_no_floors_is_unjudged()
        {
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A6.2")).Status);
            var roomsOnly = new SpecModel { Rooms = { new SpecRoom { Name = "R", LevelName = "01", Area = 10 } } };
            Assert.Equal(SpecState.NeedsPerson, State(roomsOnly, "A6.2"));
        }

        [Fact]
        public void A6_3_empty_department_is_information_never_a_fail()
        {
            var m = new SpecModel
            {
                Rooms =
                {
                    new SpecRoom { Name = "Dewan", Number = "001", LevelName = "01", Area = 100 },
                    new SpecRoom { Name = "Pejabat", Number = "002", Department = "Admin", LevelName = "01", Area = 10 },
                },
            };
            var r = Assert.Single(Run(m, "A6.3"));
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.False(SpecReport.IsFail(r.Status));
            Assert.Contains("1 of 2 rooms have no Department", r.Note);
            Assert.Equal(SpecState.NeedsPerson, State(m, "A6.3"));
        }

        [Fact]
        public void A6_3_counts_only_rooms_that_exist_on_plan()
        {
            // Real model, 16 Nov: an unplaced / not-enclosed room (0 m²) was counted, "2 of 140".
            var m = new SpecModel
            {
                Rooms =
                {
                    new SpecRoom { Name = "TANGGA 2", Number = "PTBR_00_48", LevelName = "01", Area = 22.67, Placed = true },
                    new SpecRoom { Name = "TANGGA 2", Number = "PTBR_00_116", LevelName = "01", Area = 0, Placed = true },
                    new SpecRoom { Name = "Pejabat", Number = "002", Department = "Admin", LevelName = "01", Area = 10, Placed = true },
                },
            };
            var r = Assert.Single(Run(m, "A6.3"));
            Assert.DoesNotContain("PTBR_00_116", r.Note);
        }

        // ════ A7 materials ════

        [Fact]
        public void A7_1_type_without_material_fails_with_category_and_count()
        {
            var m = new SpecModel
            {
                Types =
                {
                    Type("Walls", "Basic Wall", "Generic 200", n: 3, firstId: 10),
                    Type("Doors", "Pintu", "P1", firstId: 20, materials: new[] { "jkrAR_dor-b_(ab)-2 Aluminium" }),
                    Type("Rooms", "", "Room", firstId: 30),
                    Type("Planting", "Tree", "Palm", firstId: 40),
                    Type("Doors", "Pintu", "P2", firstId: 50, materials: new[] { " " }),
                },
            };
            var rs = Run(m, "A7.1");
            var fails = Fails(rs);
            Assert.Equal(2, fails.Count);
            var w = Assert.Single(fails, f => f.ElementId == 10);
            Assert.Equal("Walls | Basic Wall : Generic 200 | 3 elements: no material", w.Note);
            Assert.Equal(new long[] { 10, 11, 12 }, w.ElementIds);
            Assert.Equal(1, Assert.Single(rs, r => r.Status == "pass").ElementCount);
        }

        [Fact]
        public void A7_1_judges_each_element_not_the_type()
        {
            // Four walls of one type: the first has a material, two do not.
            var t = Type("Walls", "Basic Wall", "Generic 200", n: 4, firstId: 10);
            t.Materials.Add("jkrAR_wll-b_(bb02)-3");        // what the type's first instance has
            t.ElementMaterialsRead = true;
            t.MateriallessIds = new List<long> { 11, 13 };
            var m = new SpecModel { Types = { t } };
            var rs = Run(m, "A7.1");
            var f = Assert.Single(Fails(rs));
            Assert.Equal("Walls | Basic Wall : Generic 200 | 2 of 4 elements: no material", f.Note);
            Assert.Equal(new long[] { 11, 13 }, f.ElementIds);   // only the bare elements are located
            Assert.Equal(SpecCoverage.Full, f.Coverage);
            var pass = Assert.Single(rs, r => r.Status == "pass");
            Assert.Equal(2, pass.ElementCount);
            Assert.Equal(SpecCoverage.Full, pass.Coverage);
            Assert.Equal(SpecState.Fail, State(m, "A7.1"));
        }

        [Fact]
        public void A7_1_every_element_with_a_material_is_a_full_pass()
        {
            var t = Type("Walls", "Basic Wall", "Generic 200", n: 3, firstId: 10, materials: new[] { "jkrAR_wll-b_(bb02)-3" });
            t.ElementMaterialsRead = true;
            var m = new SpecModel { Types = { t } };
            var pass = Assert.Single(Run(m, "A7.1"));
            Assert.Equal("pass", pass.Status);
            Assert.Equal(3, pass.ElementCount);
            Assert.Equal(SpecCoverage.Full, pass.Coverage);
            Assert.Equal(SpecState.Pass, State(m, "A7.1"));
        }

        [Fact]
        public void A7_1_a_type_with_more_instances_than_were_read_keeps_the_pass_partial()
        {
            var t = Type("Walls", "Basic Wall", "Generic 200", n: 3, firstId: 10, materials: new[] { "jkrAR_wll-b_(bb02)-3" });
            t.ElementMaterialsRead = true;
            t.InstanceCount = 5000;                      // the reader keeps 2000 ids; here, 3
            var m = new SpecModel { Types = { t } };
            Assert.Equal(SpecCoverage.FailOnly, Assert.Single(Run(m, "A7.1")).Coverage);
            Assert.Equal(SpecState.Partial, State(m, "A7.1"));
        }

        [Theory]
        [InlineData("jkrAR_dor-b_(ab)-2 Aluminium Bronzed Anodised", true)]
        [InlineData("jkrAR_(kt)- Konkrit tetulang", true)]
        [InlineData("jkrAR_wll-b_(bb02)-3 Batu Bata", true)]
        [InlineData("jkrST_(kt30) Konkrit", true)]
        [InlineData("jkrAR_Aluminium", false)]
        [InlineData("JKR Aluminium (ab)", false)]
        [InlineData("Glass", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void JKR_material_names_follow_PB_page_35(string name, bool ok) => Assert.Equal(ok, SpecA.IsJkrMaterialName(name));

        [Theory]
        [InlineData("Default", true)]
        [InlineData("Default Wall", true)]
        [InlineData("Glass", true)]
        [InlineData("Glass (door)", true)]
        [InlineData("Concrete - Cast-in-Place Concrete", true)]
        [InlineData("Metal White", true)]
        [InlineData("Wood - Flooring", true)]
        [InlineData("Glassware Custom", false)]
        [InlineData("Kaca Biru", false)]
        [InlineData("", false)]
        public void Revit_default_material_names(string name, bool dflt) => Assert.Equal(dflt, SpecA.IsRevitDefaultMaterial(name));

        [Fact]
        public void A7_2_non_JKR_used_materials_are_a_warning_list()
        {
            var m = new SpecModel
            {
                Materials =
                {
                    new SpecMaterial { Id = 1, Name = "Glass", UsedBy = 12 },
                    new SpecMaterial { Id = 2, Name = "jkrAR_dor-b_(ab)-2 Aluminium", UsedBy = 5 },
                    new SpecMaterial { Id = 3, Name = "Metal White", UsedBy = 0 },
                    new SpecMaterial { Id = 4, Name = "Kaca Biru", UsedBy = 2 },
                },
                Types = { Type("Doors", "Pintu", "P1", materials: new[] { "Glass" }), Type("Windows", "Tingkap", "T1", materials: new[] { "Glass" }) },
            };
            var rs = Run(m, "A7.2");
            // Spec revision 2: a WARNING a person judges — Context with an issue, never a fail or a pass.
            Assert.Equal(2, rs.Count);
            Assert.All(rs, r => Assert.Equal(SpecCoverage.Context, r.Coverage));
            Assert.All(rs, r => Assert.False(string.IsNullOrEmpty(r.IssueId)));
            Assert.DoesNotContain(rs, r => r.Status == "fail" || r.Status == "warning" || r.Status == "pass");
            var glass = rs.Single(w => w.Note == "WARNING: Material \"Glass\" (Doors, Windows), 12 elements: Revit default name");
            Assert.Equal("Glass", glass.Actual);
            Assert.Contains(rs, w => w.Note.Contains("Kaca Biru") && w.Note.EndsWith("not JKR naming"));
            Assert.DoesNotContain(rs, r => r.Note.Contains("Metal White"));   // unused
            Assert.DoesNotContain(rs, r => r.Note.Contains("jkrAR_dor"));     // JKR-named: no warning
            // Stable issue ids across runs (Ignore / Reviewed survive a re-scan).
            Assert.Equal(glass.IssueId, Run(m, "A7.2").Single(w => w.Note.Contains("\"Glass\"")).IssueId);

            var rep = SpecReport.Build(SpecChecks.Run(m));
            Assert.Equal(SpecState.NeedsPerson, rep.For("A7.2").State);
            Assert.Equal(0, rep.For("A7.2").Failed);
            Assert.Equal(0, rep.For("A7.2").Passed);
            Assert.Equal(2, rep.For("A7.2").IssueIds.Count);
        }

        [Fact]
        public void A7_2_all_JKR_named_is_one_note_without_a_verdict()
        {
            var m = new SpecModel { Materials = { new SpecMaterial { Id = 2, Name = "jkrAR_dor-b_(ab)-2 Aluminium", UsedBy = 5 } } };
            var r = Assert.Single(Run(m, "A7.2"));
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.Equal("", r.IssueId);
            Assert.Equal("All 1 used materials follow JKR naming", r.Note);
            Assert.Equal(SpecState.NeedsPerson, State(m, "A7.2"));
        }

        [Fact]
        public void A7_2_and_A7_1_with_nothing_read_are_unjudged()
        {
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A7.2")).Status);
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A7.1")).Status);
        }

        [Fact]
        public void A7_3_is_context_with_materials_per_category()
        {
            var m = new SpecModel { Types = { Type("Doors", "Pintu", "P1", materials: new[] { "Glass", "jkrAR_(ab)- Aluminium" }) } };
            var rs = Run(m, "A7.3");
            Assert.All(rs, r => Assert.Equal(SpecCoverage.Context, r.Coverage));
            Assert.Contains(rs, r => r.Note == "Doors: 2 materials, 1 not JKR-named");
            Assert.Equal(SpecState.NeedsPerson, State(m, "A7.3"));
        }

        // ════ real model, 16 Nov 2026 (jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01.rvt) ════
        // Grids carry the project prefix "PT"; no grid is named exactly "A". The Project
        // Base Point stands exactly on PT-A × PT1.

        private static readonly string[] RealGridNames =
        {
            "1", "2", "3", "4", "B", "K3", "PT-A", "PT-A1", "PT-B", "PT-C", "PT-C1", "PT-D", "PT-E", "PT-F", "PT-G",
            "PT1", "PT2", "PT3", "PT4", "PT4A", "PT5", "PT6", "PT7", "PT8",
        };

        /// <summary>A straight grid: vertical ones at X = offset, horizontal ones at Y = offset.</summary>
        private static SpecGrid LineGrid(string name, double offset, bool vertical, long id) => new SpecGrid
        {
            Name = name, Id = id, Offset = offset, IsVertical = vertical, HasLine = true,
            X0 = vertical ? offset : -60000, X1 = vertical ? offset : 60000,
            Y0 = vertical ? -200000 : offset, Y1 = vertical ? -100000 : offset,
        };

        private static readonly SpecPoint RealBasePoint = new SpecPoint { X = -21968.2, Y = -131598.4 };

        /// <summary>The real grid list with plausible plan positions: letters vertical, numbers
        /// horizontal; PT-A at the base point's X, PT1 at its Y (as measured in the model).</summary>
        private static SpecModel RealGridModel()
        {
            var pos = new Dictionary<string, (double Offset, bool Vertical)>
            {
                ["B"] = (-26370, true), ["K3"] = (-30000, true),
                ["1"] = (-153721.3, false), ["2"] = (-160000, false), ["3"] = (-166000, false), ["4"] = (-172620.9, false),
                ["PT-A"] = (-21968.2, true), ["PT-A1"] = (-19000, true), ["PT-B"] = (-15000, true), ["PT-C"] = (-8000, true),
                ["PT-C1"] = (-5000, true), ["PT-D"] = (0, true), ["PT-E"] = (8000, true), ["PT-F"] = (16000, true), ["PT-G"] = (23031.9, true),
                ["PT1"] = (-131598.4, false), ["PT2"] = (-138000, false), ["PT3"] = (-144000, false), ["PT4"] = (-150000, false),
                ["PT4A"] = (-153000, false), ["PT5"] = (-156000, false), ["PT6"] = (-162000, false), ["PT7"] = (-170000, false),
                ["PT8"] = (-176016.4, false),
            };
            var m = new SpecModel { ProjectBasePoint = new SpecPoint { X = RealBasePoint.X, Y = RealBasePoint.Y } };
            long id = 1000;
            foreach (var n in RealGridNames) m.Grids.Add(LineGrid(n, pos[n].Offset, pos[n].Vertical, id++));
            return m;
        }

        [Fact]
        public void Real_grids_have_the_project_prefix_PT_and_parse_without_it()
        {
            Assert.Equal("PT", SpecA.ProjectGridPrefix(RealGridNames));
            var l = SpecA.ParseGrids(RealGridNames).Zip(RealGridNames, (g, n) => (n, g)).ToDictionary(x => x.n, x => x.g);
            Assert.Equal((SpecGridKind.Letter, 1, "PT"), (l["PT-A"].Kind, l["PT-A"].Main, l["PT-A"].Prefix));
            Assert.Equal((SpecGridKind.Number, 1, "PT"), (l["PT1"].Kind, l["PT1"].Main, l["PT1"].Prefix));
            Assert.Equal((SpecGridKind.Number, 8), (l["PT8"].Kind, l["PT8"].Main));
            Assert.Equal((SpecGridKind.LetterSub, "PT-A", "PT-A1"), (l["PT-A1"].Kind, l["PT-A1"].ParentDisplay, l["PT-A1"].Display));
            Assert.Equal((SpecGridKind.LetterSub, "PT-C"), (l["PT-C1"].Kind, l["PT-C1"].ParentDisplay));
            Assert.Equal((SpecGridKind.NumberSub, "PT4", "PT4A"), (l["PT4A"].Kind, l["PT4A"].ParentDisplay, l["PT4A"].Display));
            // The unprefixed grids keep their own reading.
            Assert.Equal((SpecGridKind.Number, ""), (l["1"].Kind, l["1"].Prefix));
            Assert.Equal((SpecGridKind.Letter, ""), (l["B"].Kind, l["B"].Prefix));
            Assert.Equal((SpecGridKind.LetterSub, "K", ""), (l["K3"].Kind, l["K3"].Parent, l["K3"].Prefix));
        }

        [Fact]
        public void A_prefix_must_be_shared_by_most_grids_and_a_single_letter_is_never_one()
        {
            Assert.Equal("", SpecA.ProjectGridPrefix(new[] { "1", "2", "3", "A", "B", "C", "PT1", "PT2" }));   // 2 of 8
            Assert.Equal("", SpecA.ProjectGridPrefix(new[] { "A1", "A2", "A3", "B1" }));                       // "A" is one letter
            Assert.Equal("", SpecA.ProjectGridPrefix(new[] { "B-1", "B-2", "B-3", "A" }));
            Assert.Equal("BLK", SpecA.ProjectGridPrefix(new[] { "BLK-1", "BLK-2", "BLK-A", "X" }));
            Assert.Equal("", SpecA.ProjectGridPrefix(new string[0]));
            Assert.Equal("", SpecA.ProjectGridPrefix(null));
            // With no prefix the old reading stands: A1 is a sub-grid of letter A.
            Assert.Equal(SpecGridKind.LetterSub, SpecA.ParseGrids(new[] { "A", "B", "1", "2", "A1" })[4].Kind);
        }

        [Fact]
        public void A5_3_real_model_flags_only_K3_and_lists_the_PT_sub_grids_under_their_parents()
        {
            var rs = Run(RealGridModel(), "A5.3");
            Assert.Empty(Fails(rs));
            var person = Assert.Single(rs, r => r.Status == "cannot_verify");
            Assert.Equal("Sub-grid K3: parent grid K missing — confirm", person.Note);
            var pass = Assert.Single(rs, r => r.Status == "pass");
            Assert.Equal("Sub-grids follow the pattern: PT4: PT4A · PT-A: PT-A1 · PT-C: PT-C1", pass.Note);
            Assert.Equal(3, pass.ElementCount);
        }

        [Fact]
        public void A5_2_real_model_passes_with_each_prefix_its_own_series()
        {
            var rs = Run(RealGridModel(), "A5.2");
            Assert.Empty(Fails(rs));
            var r = Assert.Single(rs);
            Assert.Equal("Grids in sequence with no jump: 1–4 | B · PT1–PT8 | PT-A–PT-G", r.Note);
            Assert.Equal(20, r.ElementCount);   // 5 unprefixed mains + 15 PT mains
        }

        [Fact]
        public void A5_2_a_jump_inside_the_prefixed_series_names_it_with_the_prefix()
        {
            var m = Grids(Grid("PT1", 0), Grid("PT2", 6000), Grid("PT5", 12000), Grid("PT-A", 0, false), Grid("PT-B", 6000, false), Grid("1", 0, false));
            var f = Assert.Single(Fails(Run(m, "A5.2")));
            Assert.Equal("Grids PT1,PT2 → PT5: sequence jumps (3–4 missing)", f.Note);
        }

        [Fact]
        public void A5_1_real_model_splits_letters_and_numbers_per_prefix()
        {
            var r = Assert.Single(Run(RealGridModel(), "A5.1"));
            Assert.Equal("24 grids, 0 levels — grids: 1,2,3,4 | B · PT: 1,2,3,4,5,6,7,8 | A,B,C,D,E,F,G; sub-grid: K3, PT-A1, PT-C1, PT4A", r.Note);
        }

        [Fact]
        public void A4_1_real_model_PT_A_x_PT1_is_not_read_as_Grid_A_1_and_is_listed_for_a_person()
        {
            // AI-504 (spec #12): no JKR document reads a prefixed pair as Grid A/1.
            var rs = Run(RealGridModel(), "A4.1");
            var r = Assert.Single(rs);
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("PT-A / PT1", r.Note);
            Assert.NotEqual("", r.IssueId);
            Assert.Equal(2, r.ElementIds.Count);
            Assert.Equal(SpecState.NeedsPerson, State(RealGridModel(), "A4.1"));
        }

        [Fact]
        public void A4_1_plain_A_and_1_with_the_base_point_exactly_on_the_crossing_passes()
        {
            var m = new SpecModel
            {
                ProjectBasePoint = new SpecPoint { X = 0, Y = 0 },
                Grids = { LineGrid("A", 0, true, 1), LineGrid("1", 0, false, 2) },
            };
            var r = Assert.Single(Run(m, "A4.1"));
            Assert.Equal("pass", r.Status);
            Assert.Equal("Project Base Point exactly at the crossing of grids A / 1 (Grid A/1)", r.Note);
            Assert.Equal(SpecState.Pass, State(m, "A4.1"));
        }

        [Fact]
        public void A4_1_any_offset_is_listed_with_the_offset_never_failed()
        {
            var m = new SpecModel
            {
                ProjectBasePoint = new SpecPoint { X = 15, Y = -20 },
                Grids = { LineGrid("A", 0, true, 1), LineGrid("1", 0, false, 2) },
            };
            var rs = Run(m, "A4.1");
            Assert.Empty(Fails(rs));
            var c = Assert.Single(rs);
            Assert.Equal(SpecCoverage.Context, c.Coverage);
            Assert.DoesNotContain("had ≤", c.Note);
            Assert.Equal(2, c.ElementIds.Count);
        }

        [Fact]
        public void A4_1_plain_pair_wins_over_a_prefixed_one()
        {
            var m = new SpecModel
            {
                ProjectBasePoint = new SpecPoint { X = 5000, Y = 7000 },
                Grids = { LineGrid("A", 0, true, 1), LineGrid("1", 0, false, 2), LineGrid("PT-A", 5000, true, 3), LineGrid("PT1", 7000, false, 4), LineGrid("PT2", 9000, false, 5) },
            };
            var labels = SpecA.ParseGrids(m.Grids.Select(g => g.Name));
            Assert.Equal(new[] { (0, 1) }, SpecA.BaseGridPairs(labels));
            Assert.Empty(SpecA.PrefixedBaseGridPairs(labels));
            Assert.Contains("grids A / 1 (Grid A/1): X 5000 mm, Y 7000 mm", Assert.Single(Run(m, "A4.1")).Note);
        }

        [Fact]
        public void A4_1_without_A_or_1_or_without_a_base_point()
        {
            var noA = new SpecModel { ProjectBasePoint = new SpecPoint(), Grids = { LineGrid("B", 0, true, 1), LineGrid("1", 0, false, 2) } };
            var r = Assert.Single(Run(noA, "A4.1"));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("but no grid A and grid 1", r.Note);
            // No base point read: no model result; the backend and the BIM010 checker answer.
            Assert.Empty(Run(new SpecModel { Grids = { LineGrid("A", 0, true, 1), LineGrid("1", 0, false, 2) } }, "A4.1"));
            // Grids read without their lines (an older snapshot): a person.
            var noLines = new SpecModel { ProjectBasePoint = new SpecPoint(), Grids = { Grid("A", 0), Grid("1", 0, false) } };
            Assert.Contains("the crossing cannot be computed", Assert.Single(Run(noLines, "A4.1")).Note);
        }

        [Fact]
        public void A6_1_real_level_splits_unplaced_from_placed_but_not_enclosed()
        {
            var m = new SpecModel { Levels = { new SpecLevel { Name = "PTBR_Aras 01", Elevation = 600 } } };
            m.Rooms.Add(new SpecRoom { Name = "BILIK PENGETUA", Number = "PTBR_00_03", LevelName = "PTBR_Aras 01", Area = 30, Placed = true });
            for (var i = 0; i < 8; i++) m.Rooms.Add(new SpecRoom { Name = "U" + i, LevelName = "PTBR_Aras 01", Area = 0, Placed = false });
            for (var i = 0; i < 44; i++) m.Rooms.Add(new SpecRoom { Name = "N" + i, LevelName = "PTBR_Aras 01", Area = 0, Placed = true });
            var r = Assert.Single(Run(m, "A6.1"));
            Assert.EndsWith("; 8 rooms not placed; 44 rooms not enclosed / redundant", r.Note);
            Assert.StartsWith("Level PTBR_Aras 01: 1 rooms — BILIK PENGETUA", r.Note);
        }

        [Fact]
        public void A7_2_curtain_wall_panels_and_mullions_count_once_under_their_own_category()
        {
            // As the reader builds it: the curtain wall's Walls type also carries its panel and
            // mullion materials, and UsedBy sums type materials × instances (Glass 54, Aluminium 11).
            var m = new SpecModel
            {
                Types =
                {
                    Type("Windows", "Tingkap", "T1", n: 50, firstId: 100, materials: new[] { "Glass" }),
                    Type("Doors", "Pintu", "P1", n: 2, firstId: 200, materials: new[] { "Glass" }),
                    Type("Curtain Panels", "System Panel", "Glazed", n: 1, firstId: 300, materials: new[] { "Glass" }),
                    Type("Walls", "Curtain Wall", "Curtain Wall", n: 1, firstId: 400, materials: new[] { "Glass", "Aluminium" }),
                    Type("Curtain Wall Mullions", "Rectangular Mullion", "50 x 150", n: 4, firstId: 500, materials: new[] { "Aluminium" }),
                    Type("Windows", "Tingkap", "T2", n: 2, firstId: 600, materials: new[] { "Aluminium" }),
                    Type("Generic Models", "Louvre", "L1", n: 4, firstId: 700, materials: new[] { "Aluminium" }),
                },
                Materials = { new SpecMaterial { Id = 1, Name = "Glass", UsedBy = 54 }, new SpecMaterial { Id = 2, Name = "Aluminium", UsedBy = 11 } },
            };
            var rs = Run(m, "A7.2");
            Assert.Contains(rs, r => r.Note == "WARNING: Material \"Aluminium\" (Curtain Wall Mullions, Windows, Generic Models), 10 elements: Revit default name");
            // Without UsedBy (counted from the types) the result is the same.
            m.Materials.Clear();
            Assert.Contains(Run(m, "A7.2"), r => r.Note.StartsWith("WARNING: Material \"Glass\" (Windows, Doors, Curtain Panels), 53 elements"));
            // A basic wall's own glass layer is the wall's, and counts under Walls.
            m.Types.Add(Type("Walls", "Basic Wall", "Glass Block", n: 3, firstId: 800, materials: new[] { "Glass" }));
            Assert.Contains(Run(m, "A7.2"), r => r.Note.StartsWith("WARNING: Material \"Glass\" (Windows, Doors, Curtain Panels, Walls), 56 elements"));
            // A7.1 still sees the curtain wall as having a material.
            Assert.Empty(Fails(Run(m, "A7.1")));
        }

        // ════ misc words ════

        [Theory]
        [InlineData("Pintu Utama", true)]
        [InlineData("Main_Door", true)]
        [InlineData("MainEntrance", true)]
        [InlineData("Maintenance Door", false)]
        [InlineData("Domain", false)]
        public void Main_door_by_name(string family, bool main) =>
            Assert.Equal(main, SpecA.IsMainDoor(new SpecType { FamilyName = family }));

        [Theory]
        [InlineData("Pagar Hiasan", true)]
        [InlineData("Security Fence", true)]
        [InlineData("Pagar Tangga", false)]
        [InlineData("Balcony Fence", false)]
        [InlineData("Handrail", false)]
        public void Fence_by_name(string name, bool fence) => Assert.Equal(fence, SpecA.IsFence(name));
    }
}
