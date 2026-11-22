using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-504: numbers no JKR document gives were removed from the spec checks. One test per row of the
    /// ticket's table: an input that used to fail (or pass) on the invented number now lists a row for a
    /// person (Context result with an issue, Human in the pane) and never fails. The documented part of
    /// each check keeps its own tests (SpecChecksATests, SpecThresholdBoundaryTests, SpecChecksDETests).
    /// </summary>
    public class SpecInventedThresholdsTests
    {
        private static List<SpecResult> Run(SpecModel m, string id) => SpecChecks.Rules[id](m.Normalize()).ToList();
        private static List<SpecResult> Fails(IEnumerable<SpecResult> rs) => rs.Where(r => r.Status == "fail").ToList();
        private static List<SpecResult> Listed(IEnumerable<SpecResult> rs) => rs.Where(r => r.Coverage == SpecCoverage.Context).ToList();

        private static SpecType T(string cat, string family, string type, double? width = null, double? height = null, double? thickness = null)
        {
            var t = new SpecType { Category = cat, FamilyName = family, TypeName = type, InstanceCount = 1, TypeId = 5000 };
            t.InstanceIds.Add(100);
            if (width != null) t.Numbers["Width"] = width.Value;
            if (height != null) t.Numbers["Height"] = height.Value;
            if (thickness != null) t.Numbers["Thickness"] = thickness.Value;
            return t;
        }

        /// <summary>The result is a row for a person: it gives an issue, has no AI verdict, and the one query
        /// Fix via Copilot / Fix All use says so.</summary>
        private static void AssertPersonRow(SpecResult r)
        {
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.NotEqual("", r.IssueId);
            Assert.True(SpecModelIssues.GivesIssue(r));
            Assert.False(SpecModelIssues.IsAiVerdict(r));
            Assert.True(SpecPersonDecides.IsPersonDecides(r));
        }

        private static SpecModel Ceiling(double heightMm, double levelElevation = 0)
        {
            var m = new SpecModel();
            m.Levels.Add(new SpecLevel { Name = "01", Elevation = levelElevation });
            m.Ceilings.Add(new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = heightMm });
            return m;
        }

        private static List<List<SpecPoint>> Box(double x0, double y0, double x1, double y1) => new List<List<SpecPoint>>
        {
            new List<SpecPoint> { new SpecPoint { X = x0, Y = y0 }, new SpecPoint { X = x1, Y = y0 }, new SpecPoint { X = x1, Y = y1 }, new SpecPoint { X = x0, Y = y1 } },
        };

        // ── Q1 rounding: A1.1 ──

        [Fact]
        public void Q1_A1_1_a_ceiling_of_2999_6_is_listed_not_failed_and_2999_still_fails()
        {
            var rs = Run(Ceiling(2999.6), "A1.1");
            Assert.Empty(Fails(rs));                       // it used to pass silently inside a 0.5 mm slack
            var row = Assert.Single(Listed(rs));
            AssertPersonRow(row);
            Assert.Contains("JKR tiada peraturan pembundaran", row.Note);
            Assert.Contains("JKR gives no rounding rule", row.NoteEn);
            Assert.DoesNotContain("0.5", row.Note);
            Assert.Single(Fails(Run(Ceiling(2999.0), "A1.1")));
            Assert.Empty(Listed(Run(Ceiling(3000.0), "A1.1")));
            Assert.Empty(Fails(Run(Ceiling(3000.0), "A1.1")));
        }

        // ── Q1 rounding: A1.2 (width and height) ──

        [Fact]
        public void Q1_A1_2_a_door_of_899_6_wide_or_2099_6_high_is_listed_not_failed()
        {
            foreach (var (w, h) in new[] { (899.6, 2100.0), (900.0, 2099.6) })
            {
                var rs = Run(new SpecModel { Types = { T("Doors", "Single", "D", width: w, height: h) } }, "A1.2");
                Assert.Empty(Fails(rs));
                AssertPersonRow(Assert.Single(Listed(rs)));
            }
            Assert.Single(Fails(Run(new SpecModel { Types = { T("Doors", "Single", "D", width: 899.0, height: 2100) } }, "A1.2")));
        }

        // ── Q1 rounding: A1.3, no 0.05 ──

        [Fact]
        public void Q1_A1_3_a_main_door_of_39_96_is_listed_not_failed_and_39_9_still_fails()
        {
            var rs = Run(new SpecModel { Types = { T("Doors", "Main Door", "Solid Core", thickness: 39.96) } }, "A1.3");
            Assert.Empty(Fails(rs));
            AssertPersonRow(Assert.Single(Listed(rs)));
            Assert.Single(Fails(Run(new SpecModel { Types = { T("Doors", "Main Door", "Solid Core", thickness: 39.9) } }, "A1.3")));
            // 40.0 and above is the documented minimum, exact.
            Assert.Empty(Fails(Run(new SpecModel { Types = { T("Doors", "Main Door", "Solid Core", thickness: 40.0) } }, "A1.3")));
        }

        // ── Q1 rounding: A1.5 ──

        [Fact]
        public void Q1_A1_5_a_fence_of_2399_6_is_listed_not_failed_and_2399_still_fails()
        {
            var rs = Run(new SpecModel { Types = { T("Railings", "Decorative Fence", "F", height: 2399.6) } }, "A1.5");
            Assert.Empty(Fails(rs));
            AssertPersonRow(Assert.Single(Listed(rs)));
            Assert.Single(Fails(Run(new SpecModel { Types = { T("Railings", "Decorative Fence", "F", height: 2399.0) } }, "A1.5")));
        }

        // ── Q2 base point: A4.1, no 10 mm ──

        private static SpecModel BasePointAt(double dx, double dy = 0)
        {
            var m = new SpecModel { ProjectBasePoint = new SpecPoint { X = dx, Y = dy } };
            m.Grids.Add(new SpecGrid { Id = 1, Name = "A", Offset = 0, IsVertical = true, HasLine = true, X0 = 0, X1 = 0, Y0 = -50000, Y1 = 50000 });
            m.Grids.Add(new SpecGrid { Id = 2, Name = "1", Offset = 0, IsVertical = false, HasLine = true, X0 = -50000, X1 = 50000, Y0 = 0, Y1 = 0 });
            return m;
        }

        [Fact]
        public void Q2_A4_1_an_offset_of_5_or_12_or_300_mm_is_listed_never_failed_and_only_exact_passes()
        {
            foreach (var d in new[] { 0.5, 5.0, 10.0, 12.0, 300.0 })
            {
                var rs = Run(BasePointAt(d), "A4.1");
                Assert.Empty(Fails(rs));                       // 12 and 300 used to fail, 5 and 10 used to pass
                var row = Assert.Single(rs);
                AssertPersonRow(row);
                Assert.DoesNotContain("had ≤", row.Note);
                Assert.DoesNotContain("≤", row.Note);
                Assert.DoesNotContain("limit ≤", row.NoteEn);
            }
            var exact = Assert.Single(Run(BasePointAt(0), "A4.1"));
            Assert.Equal("pass", exact.Status);
            // Feet → mm float noise is a conversion fix, not a tolerance.
            Assert.Equal("pass", Assert.Single(Run(BasePointAt(1e-9, -2e-10), "A4.1")).Status);
        }

        [Fact]
        public void Q2_A4_1_the_BIM010_checker_no_longer_fails_an_offset_either()
        {
            // The old checker said "no" beyond 10 mm; an evaluator row for a person outranks it.
            var model = SpecChecks.Run(BasePointAt(25));
            var checker = new SpecResult { Source = SpecReport.Audit, CheckerId = "base_point", RowKey = "A4", Status = "fail", IssueId = "BIM010-A4" };
            var report = SpecReport.Build(new[] { checker }.Concat(model));
            var a41 = report.For("A4.1");
            Assert.Equal(0, a41.Failed);
            Assert.Equal(SpecState.NeedsPerson, a41.State);
            Assert.DoesNotContain("BIM010-A4", a41.IssueIds);
            Assert.Equal(1, a41.PersonRows);
        }

        // ── Q4 flat roof: A1.4, no 2° / 10° ──

        [Fact]
        public void Q4_A1_4_no_roof_slope_or_name_is_a_flat_roof_so_an_RC_roof_without_waterproofing_is_listed_at_any_slope()
        {
            foreach (var slope in new[] { 0.0, 1.9, 2.0, 9.9, 10.0, 25.0 })
            {
                var rs = Run(new SpecModel { Roofs = { new SpecRoof { Id = 3, TypeName = "Bumbung rata RC", SlopeDegrees = slope, Materials = { "Konkrit tetulang" }, LayerFunctions = { "Structure" } } } }, "A1.4");
                Assert.Empty(Fails(rs));
                var row = Assert.Single(rs);
                AssertPersonRow(row);
                Assert.DoesNotContain("°", row.Note);
            }
            // The documented part is automatic: a waterproofing layer complies, at any slope.
            var ok = new SpecModel { Roofs = { new SpecRoof { Id = 3, TypeName = "RC Roof", SlopeDegrees = 1, Materials = { "Concrete" }, LayerFunctions = { "Structure", "Membrane" } } } };
            Assert.Equal("pass", Assert.Single(Run(ok, "A1.4")).Status);
        }

        // ── Q5 slab vs rooms: A6.2, no 50 % / 1 m² / 1500 mm ──

        private static SpecFloor FloorAt(long id, string level, double top, double x0, double y0, double x1, double y1) =>
            new SpecFloor { Id = id, TypeName = "Slab", LevelName = level, TopElevation = top, Loops = Box(x0, y0, x1, y1),
                            MinX = x0, MinY = y0, MaxX = x1, MaxY = y1, Area = (x1 - x0) * (y1 - y0) / 1e6 };

        private static SpecRoom RoomAt(long id, string name, string level, double x0, double y0, double x1, double y1) =>
            new SpecRoom { Id = id, Number = id.ToString(), Name = name, LevelName = level, Placed = true, Area = (x1 - x0) * (y1 - y0) / 1e6,
                           X = (x0 + x1) / 2, Y = (y0 + y1) / 2, BaseElevation = 0, Height = 3000, Loops = Box(x0, y0, x1, y1) };

        [Fact]
        public void Q5_A6_2_a_slab_under_half_covered_complies_a_small_pad_without_a_room_is_listed_and_nothing_fails()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 } },
                Floors =
                {
                    FloorAt(1, "01", 0, 0, 0, 10000, 10000),        // a room on 10 % of it: used to fail (< 50 %)
                    FloorAt(2, "01", 0, 20000, 0, 20600, 600),      // 0.36 m² pad, no room: used to be skipped (< 1 m²)
                    FloorAt(3, "01", 0, 40000, 0, 50000, 10000),    // no room at all: used to fail
                },
                Rooms = { RoomAt(10, "Bilik", "01", 0, 0, 1000, 10000) },
            };
            var rs = Run(m, "A6.2");
            Assert.Empty(Fails(rs));
            Assert.Equal(1, Assert.Single(rs, r => r.Status == "pass").ElementCount);
            var listed = Listed(rs);
            Assert.Equal(new long[] { 2, 3 }, listed.Select(r => r.ElementId).OrderBy(x => x));
            Assert.All(listed, AssertPersonRow);
            Assert.All(listed, r => Assert.DoesNotContain("%", r.Note));
        }

        [Fact]
        public void Q5_A6_2_a_room_1400_mm_below_the_slab_on_another_level_is_not_its_room_no_slack()
        {
            // The old 1500 mm level slack matched a room standing on another level. Now the room
            // stands on the slab's own level, or it is a stair / lift room whose height holds the slab top.
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "01", Elevation = 0 }, new SpecLevel { Name = "02", Elevation = 1400 } },
                Floors = { FloorAt(1, "02", 1400, 0, 0, 5000, 5000) },
                Rooms = { RoomAt(10, "Bilik", "01", 0, 0, 5000, 5000) },
            };
            var rs = Run(m, "A6.2");
            Assert.Empty(Fails(rs));
            AssertPersonRow(Assert.Single(rs));
        }

        // ── Q8 door size: A1.2, no nominal → clear, no double leaf from the name ──

        [Fact]
        public void Q8_A1_2_a_double_named_door_1400_wide_is_listed_not_failed_and_a_single_850_door_still_fails()
        {
            var dbl = Run(new SpecModel { Types = { T("Doors", "Double Flush", "1400", width: 1400, height: 2100) } }, "A1.2");
            Assert.Empty(Fails(dbl));                      // it used to fail at 1500 read from the name
            var row = Assert.Single(dbl);
            AssertPersonRow(row);
            Assert.Contains("dua daun", row.Note);
            // A door named single or not named double is judged at 900 only.
            Assert.Empty(Fails(Run(new SpecModel { Types = { T("Doors", "Single", "1400", width: 1400, height: 2100) } }, "A1.2")));
            Assert.Single(Fails(Run(new SpecModel { Types = { T("Doors", "Double Flush", "850", width: 850, height: 2100) } }, "A1.2")));
        }

        // ── Q11 Dc.1: exact, feet → mm noise is a conversion fix ──

        [Fact]
        public void Q11_Dc_1_a_name_off_by_one_mm_fails_exactly_and_float_noise_does_not_fail_a_correct_name()
        {
            var off = T("Walls", "Basic Wall", "Wall 146", width: 147);        // used to pass inside ±1 mm
            Assert.Single(Fails(Run(new SpecModel { Types = { off } }, "Dc.1")));
            foreach (var noisy in new[] { 146.00000000000003, 145.99999999999997, 146.0000000001 })
            {
                var ok = T("Walls", "Basic Wall", "Wall 146", width: noisy);
                Assert.Empty(Fails(Run(new SpecModel { Types = { ok } }, "Dc.1")));
            }
            // 900 mm typed in Revit, read back from feet.
            var feet = 900 / 304.8;
            var fromFeet = feet * 304.8;
            var door = T("Doors", "Single", "(PT1p600a) 900 x 2100", width: fromFeet, height: 2100 / 304.8 * 304.8);
            Assert.Empty(Fails(Run(new SpecModel { Types = { door } }, "Dc.1")));
        }

        [Fact]
        public void Q11_Dc_1_is_not_listed_for_a_person_it_stays_an_exact_automatic_check()
        {
            Assert.DoesNotContain("Dc.1", SpecPersonDecides.AiWithHumanPart);
            Assert.False(SpecPersonDecides.IsPersonDecidesCheck("Dc.1"));
            Assert.Equal(SpecCellClass.Ai, SpecCatalog.Find("Dc.1").Class);
        }

        // ── Q16 A3.1 remark ──

        [Fact]
        public void Q16_A3_1_a_W00_model_with_many_links_gets_no_remark_at_all()
        {
            var m = new SpecModel { FileName = "jkrAR24_5a_A1_W00_DS.rvt" };
            for (var i = 0; i < 9; i++) m.Links.Add(new SpecLink { Name = $"Site_{i}.rvt", Status = "Loaded" });
            m.Links.Add(new SpecLink { Name = "jkrAR24_5a_A1_WX02_DS.rvt", Status = "Loaded" });
            var r = Assert.Single(Run(m, "A3.1"));
            Assert.Equal("pass", r.Status);
            Assert.DoesNotContain("REMARK", r.Note + r.NoteEn);
            Assert.DoesNotContain("link", r.Note.ToLowerInvariant().Replace("di-link", ""));
            Assert.DoesNotContain("REMARK", SpecCatalog.Find("A3.1").Manual.Ms + SpecCatalog.Find("A3.1").Manual.En);
        }

        // ── spec #5 "lebih daripada 5 meter" ──

        [Fact]
        public void Spec5_no_check_text_or_remark_cites_5_meter()
        {
            var text = string.Join("\n", SpecCatalog.Checks.SelectMany(c => new[]
            {
                c.Title.Ms, c.Title.En, c.Comply.Ms, c.Comply.En, c.NotComply.Ms, c.NotComply.En, c.Manual.Ms, c.Manual.En, c.Reference,
            }));
            Assert.DoesNotContain("5 meter", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lebih daripada 5", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("5 m ", text.Replace("5 mm", ""), StringComparison.OrdinalIgnoreCase);
            // PBT views stay list / human (C1.3.2 and friends), never a verdict by distance.
            foreach (var c in SpecCatalog.Checks.Where(c => c.Id.StartsWith("C1.3", StringComparison.Ordinal)))
                Assert.DoesNotContain("meter", c.Comply.Ms + c.NotComply.Ms + c.Manual.Ms, StringComparison.OrdinalIgnoreCase);
        }

        // ── spec #10 door / window detail sheet ──

        [Fact]
        public void Spec10_C4_1_2_a_sheet_named_Butiran_Pintu_is_listed_for_a_person_not_counted()
        {
            var m = new SpecModel();
            m.Sheets.Add(new SpecSheet { Id = 401, Number = "bPT02", Name = "Butiran Pintu", ViewKinds = { "Legend" }, ViewNames = { "Petunjuk Pintu" } });
            m.Views.Add(new SpecView { Id = 1, Name = "Plan", Kind = "FloorPlan", OnSheet = true, SheetNumber = "A1" });
            m.Views.Add(new SpecView { Id = 2, Name = "Ceiling", Kind = "CeilingPlan", OnSheet = true, SheetNumber = "A1" });
            m.Views.Add(new SpecView { Id = 3, Name = "Elev", Kind = "Elevation", OnSheet = true, SheetNumber = "A1" });
            m.Views.Add(new SpecView { Id = 4, Name = "Sec", Kind = "Section", OnSheet = true, SheetNumber = "A1" });
            m.Sheets.Add(new SpecSheet { Id = 1, Number = "A1", Name = "Plans", ViewKinds = { "FloorPlan", "CeilingPlan", "Elevation", "Section" }, ViewNames = { "Plan", "Ceiling", "Elev", "Sec" } });
            var rs = Run(m, "C4.1.2");
            var door = Assert.Single(Listed(rs), r => r.Note.Contains("detail pintu"));
            AssertPersonRow(door);
            Assert.Contains("Butiran Pintu", door.Note);
            // The window detail has no sheet at all: still a fail (the documented list of kinds).
            Assert.Contains(Fails(rs), f => f.NoteEn.StartsWith("No window detail"));
            Assert.DoesNotContain(Fails(rs), f => f.NoteEn.StartsWith("No door detail"));
        }

        // ── spec #12 prefixed grids ──

        [Fact]
        public void Spec12_A4_1_prefixed_grids_PT_A_and_PT1_are_listed_not_read_as_Grid_A_1()
        {
            var m = new SpecModel { ProjectBasePoint = new SpecPoint { X = 0, Y = 0 } };
            m.Grids.Add(new SpecGrid { Id = 1, Name = "PT-A", Offset = 0, IsVertical = true, HasLine = true, X0 = 0, X1 = 0, Y0 = -50000, Y1 = 50000 });
            m.Grids.Add(new SpecGrid { Id = 2, Name = "PT1", Offset = 0, IsVertical = false, HasLine = true, X0 = -50000, X1 = 50000, Y0 = 0, Y1 = 0 });
            m.Grids.Add(new SpecGrid { Id = 3, Name = "PT-B", Offset = 5000, IsVertical = true, HasLine = true, X0 = 5000, X1 = 5000, Y0 = -50000, Y1 = 50000 });
            m.Grids.Add(new SpecGrid { Id = 4, Name = "PT2", Offset = 5000, IsVertical = false, HasLine = true, X0 = -50000, X1 = 50000, Y0 = 5000, Y1 = 5000 });
            var labels = SpecA.ParseGrids(m.Grids.Select(g => g.Name));
            Assert.Empty(SpecA.BaseGridPairs(labels));
            Assert.Single(SpecA.PrefixedBaseGridPairs(labels));
            var rs = Run(m, "A4.1");
            Assert.Empty(Fails(rs));
            Assert.DoesNotContain(rs, r => r.Status == "pass");
            var row = Assert.Single(rs);
            AssertPersonRow(row);
            Assert.Contains("PT-A / PT1", row.Note);
        }

        // ── the single question Fix via Copilot / Fix All ask ──

        [Fact]
        public void The_one_question_is_this_check_or_row_for_a_person_and_the_AI_with_human_part_list_is_in_the_catalog()
        {
            // Check level: the catalog class.
            Assert.True(SpecPersonDecides.IsPersonDecidesCheck("A6.3"));
            Assert.True(SpecPersonDecides.IsPersonDecidesCheck("SPEC-A7.3"));
            Assert.False(SpecPersonDecides.IsPersonDecidesCheck("A1.1"));
            Assert.False(SpecPersonDecides.IsPersonDecidesCheck("nope"));
            Assert.False(SpecPersonDecides.IsPersonDecidesCheck(null));
            foreach (var id in SpecPersonDecides.AiWithHumanPart)
            {
                Assert.Equal(SpecCellClass.Ai, SpecCatalog.Find(id).Class);   // class stays AI: the documented part is automatic
                Assert.True(SpecPersonDecides.HasPersonPart("SPEC-" + id));
                Assert.False(SpecPersonDecides.IsPersonDecidesCheck(id));
            }
            // Row level: a fail is an AI verdict, a context row is for a person.
            Assert.False(SpecPersonDecides.IsPersonDecides(Run(Ceiling(2999.0), "A1.1").Single(r => r.Status == "fail")));
            Assert.True(SpecPersonDecides.IsPersonDecides(Run(Ceiling(2999.6), "A1.1").Single(r => r.Coverage == SpecCoverage.Context)));
            Assert.False(SpecPersonDecides.IsPersonDecides(null));
            // Class counts are unchanged: 39 AI (8 with a part a person decides), 8 Human, 14 List.
            Assert.Equal((39, 8, 14), (SpecCatalog.Checks.Count(c => c.Class == SpecCellClass.Ai),
                                       SpecCatalog.Checks.Count(c => c.Class == SpecCellClass.Human),
                                       SpecCatalog.Checks.Count(c => c.Class == SpecCellClass.List)));
        }

        [Fact]
        public void A_check_with_only_rows_for_a_person_counts_as_passed_in_the_percent_like_a_Human_check()
        {
            // AI-474: what a person decides counts as pass. A4.1 with an offset has nothing but a person's row.
            var offset = SpecReport.Build(SpecChecks.Run(BasePointAt(25)));
            Assert.Equal(SpecState.NeedsPerson, offset.For("A4.1").State);
            var t = SpecChecklist.TallyOf(offset);
            var plain = SpecReport.Build(Array.Empty<SpecResult>());
            Assert.Equal(SpecChecklist.TallyOf(plain).ListHuman + 1, t.ListHuman);
            Assert.DoesNotContain(offset.Checks, c => c.Check.Id == "A4.1" && c.State == SpecState.Fail);
            // A real fail beside it still fails the check and is not hidden by the person's row.
            var m = BasePointAt(25);
            m.Types.Add(T("Doors", "Single", "D", width: 800, height: 2100));
            var both = SpecReport.Build(SpecChecks.Run(m));
            Assert.Equal(SpecState.Fail, both.For("A1.2").State);
            Assert.Equal(SpecState.NeedsPerson, both.For("A4.1").State);
        }

        // ── conversion fix, not a tolerance ──

        [Fact]
        public void SpecUnits_removes_feet_to_mm_float_noise_and_nothing_else()
        {
            Assert.Equal(900.0, SpecUnits.CleanMm(899.9999999999999));
            Assert.True(SpecUnits.SameMm(2100.0000000000005, 2100));
            Assert.False(SpecUnits.SameMm(2100.001, 2100));
            Assert.False(SpecUnits.SameMm(899.99, 900));
            Assert.Equal(3000.0, SpecUnits.CleanMm(3000));
            Assert.True(double.IsNaN(SpecUnits.CleanMm(double.NaN)));
        }
    }
}
