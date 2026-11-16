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
                Assert.False(string.IsNullOrWhiteSpace(r.NoteEn), r.CheckId + " has no English note: " + r.Note);
            }
        }

        [Fact]
        public void A4_1_has_no_evaluator_and_states_its_backend_dependency()
        {
            Assert.False(SpecChecks.Rules.ContainsKey("A4.1"));
            Assert.Contains("base point", SpecChecks.BackendOnly["A4.1"]);
            Assert.True(SpecChecks.BackendOnly.ContainsKey("A3.3"));
            foreach (var id in new[] { "A1.1", "A1.2", "A1.3", "A1.4", "A1.5", "A1.6", "A1.7", "A2.1", "A2.2", "A3.1", "A3.2", "A3.3",
                                       "A3.4", "A3.5", "A5.1", "A5.2", "A5.3", "A6.1", "A6.2", "A6.3", "A7.1", "A7.2", "A7.3" })
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
            Assert.Equal("Aras 02: siling 2700 mm < 3000 mm", f.Note);
            Assert.Equal("Level 02: ceiling 2700 mm < 3000 mm", f.NoteEn);
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
            Assert.Contains("Basement 2 (basement): siling 2400 mm < 2500 mm", f.Note);
        }

        [Fact]
        public void A1_1_no_ceilings_is_unjudged_never_a_pass()
        {
            var rs = Run(new SpecModel { Levels = { new SpecLevel { Name = "01" } } }, "A1.1");
            var r = Assert.Single(rs);
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("Tiada siling", r.Note);
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
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.StartsWith("Aras 02"));
            Assert.Equal(SpecState.NeedsPerson, State(m, "A1.1"));
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
            Assert.Equal(3, fails.Count);
            Assert.Contains(fails, f => f.Note == "Pintu : (PT1p600a) 850 | 850 | 2100 mm: lebar < 900 mm (single)");
            Assert.Contains(fails, f => f.Note == "Double Flush : 1400 | 1400 | 2100 mm: lebar < 1500 mm (double)");
            Assert.Contains(fails, f => f.Note.Contains("tinggi < 2100 mm") && f.ElementId == 40);
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
            Assert.Contains(fails, f => f.Note == "Pintu utama Pintu Utama : Kayu: tebal 35 mm < 40 mm");
            Assert.Contains(fails, f => f.Note.Contains("parameter Thickness tiada"));
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
            Assert.Contains("calon: Single : Ext (tebal 45 mm)", r.Note);
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
        public void A1_4_RC_flat_roof_without_waterproofing_fails_and_a_flat_roof_gets_a_context_note()
        {
            var m = new SpecModel { Roofs = { new SpecRoof { Id = 7, TypeName = "Roof 150", SlopeDegrees = 0.5, Materials = { "jkrAR_(kt)- Konkrit tetulang" }, LayerFunctions = { "Structure" } } } };
            var rs = Run(m, "A1.4");
            var f = Assert.Single(Fails(rs));
            Assert.Equal(7, f.ElementId);
            Assert.Contains(rs, r => r.Coverage == SpecCoverage.Context && r.Note.Contains("kewajaran"));
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
        public void A1_4_unknown_material_or_possible_double_roofing_goes_to_a_person()
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
        }

        // ════ A1.5 fence ════

        [Fact]
        public void A1_5_no_fence_is_scope_for_a_person_not_a_pass()
        {
            var m = new SpecModel { Types = { Type("Railings", "Railing", "Pagar Tangga 1000", height: 1000) } };
            var r = Assert.Single(Run(m, "A1.5"));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("sahkan skop projek", r.Note);
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
            Assert.Equal("Pagar Basic Wall : Pagar Hiasan 1800: tinggi 1800 mm < 2400 mm", f.Note);
            Assert.Contains(rs, r => r.Status == "pass" && r.ElementCount == 1);
        }

        // ════ A1.6 / A1.7 ════

        [Fact]
        public void A1_6_lists_every_NS_clause_the_spec_marks_manual()
        {
            var manual = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "jkr_spec_ns_clauses.tsv"))
                .Where(l => !l.StartsWith("#")).Skip(1).Select(l => l.Split('\t'))
                .Where(c => c.Length >= 4 && c[3].StartsWith("Tidak")).ToList();
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
            Assert.Equal("Semakan berdasarkan Need Statement minimum kerajaan; konsep reka bentuk tidak disemak.", r.Note);
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
            Assert.Contains("8 segmen (jkrAR24 | 5a | (BEde1A_p14-001) | A1 | w-01 | (S) | DS | 220222a)", r.Note);
            var bad = Assert.Single(Run(new SpecModel { FileName = "Project1_final.rvt" }, "A2.1"));
            Assert.Equal("fail", bad.Status);
            Assert.Contains("2 segmen (Project1 | final)", bad.Note);
            Assert.Equal("cannot_verify", Assert.Single(Run(new SpecModel(), "A2.1")).Status);
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
            Assert.Contains("WX01 (model pecah)", r.Note);
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
            Assert.Equal("BPEP tidak tersedia → tidak dapat disemak", bpep.Note);
            Assert.Equal("BPEP not available → cannot be checked", bpep.NoteEn);
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
            Assert.Contains(fails, f => f.Note == "Pecahan WX02 tiada sebagai link dalam model");
            Assert.Contains(fails, f => f.Note == "Pecahan WX03 tiada sebagai link dalam model");
            Assert.Contains(fails, f => f.Note.StartsWith("Link pecahan WX02 tidak dijumpai"));
            Assert.DoesNotContain(fails, f => f.Note.Contains("WX01") || f.Note.Contains("WX04"));
        }

        [Fact]
        public void A3_split_with_no_links_asks_for_links_first()
        {
            var m = new SpecModel { FileName = File01 };
            var a34 = Assert.Single(Run(m, "A3.4"));
            Assert.Equal(SpecCoverage.Listing, a34.Coverage);
            Assert.Contains("Model perlu di-link dahulu sebelum semakan A3 boleh dijalankan", a34.Note);
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
            Assert.Equal("Grid 1,2,3 → 11: urutan melompat (4–10 tiada)", f.Note);
            Assert.Equal("Grids 1,2,3 → 11: sequence jumps (4–10 missing)", f.NoteEn);
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
            Assert.Contains("C,D tiada", Assert.Single(Fails(Run(jump, "A5.2"))).Note);
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
            Assert.Equal("Sub-grid ikut pola: 1: 1A, 1B · A: A1", Assert.Single(rs).Note);
            Assert.Equal(SpecState.Pass, State(m, "A5.3"));
        }

        [Fact]
        public void A5_3_orphan_out_of_bay_or_skipped_sub_grids_go_to_a_person()
        {
            var m = Grids(Grid("1", 0), Grid("2", 6000), Grid("7A", 100), Grid("1A", 9000), Grid("1C", 3000));
            var rs = Run(m, "A5.3");
            Assert.Empty(Fails(rs));
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.Contains("grid induk 7 tiada"));
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.Contains("1A di luar petak 1–2"));
            Assert.Contains(rs, r => r.Status == "cannot_verify" && r.Note.Contains("1B tiada"));
        }

        [Fact]
        public void A5_1_lists_grid_and_level_counts()
        {
            var m = Grids(Grid("2", 1), Grid("1", 0), Grid("A", 0, false));
            m.Levels.Add(new SpecLevel { Name = "01" });
            var r = Assert.Single(Run(m, "A5.1"));
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
            Assert.Equal("3 grid, 1 aras — grid: 1,2 | A", r.Note);
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
            Assert.Equal("Aras 01: 1 room — Lobi | 001 | Pentadbiran | 45.0 m²", rs[0].Note);
            Assert.Contains("1 room tidak ditempatkan", rs[1].Note);
            Assert.Equal(SpecState.Listed, State(m, "A6.1"));
        }

        [Fact]
        public void A6_2_a_slab_with_no_room_point_on_its_level_fails_with_floor_level_and_grid()
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
            var fails = Fails(rs);
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.ElementId == 12 && f.Note == "Aras 01 | grid 2/B | lif: lantai 12 (Lantai Lif) tiada room");
            Assert.Contains(fails, f => f.ElementId == 13 && f.Note.StartsWith("Aras 02"));
            Assert.Equal(SpecCoverage.FailOnly, Assert.Single(rs, r => r.Status == "pass").Coverage);
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
            Assert.Contains("1 daripada 2 room tiada Department", r.Note);
            Assert.Equal(SpecState.NeedsPerson, State(m, "A6.3"));
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
            Assert.Equal("Walls | Basic Wall : Generic 200 | 3 elemen: tiada material", w.Note);
            Assert.Equal(new long[] { 10, 11, 12 }, w.ElementIds);
            Assert.Equal(1, Assert.Single(rs, r => r.Status == "pass").ElementCount);
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
            var warns = rs.Where(r => r.Status == "warning").ToList();
            Assert.Equal(2, warns.Count);
            Assert.Contains(warns, w => w.Note == "Material \"Glass\" (Doors, Windows), 12 elemen: nama default Revit");
            Assert.Contains(warns, w => w.Note.Contains("Kaca Biru") && w.Note.EndsWith("bukan naming JKR"));
            Assert.DoesNotContain(rs, r => r.Note.Contains("Metal White"));   // unused
            Assert.Equal(SpecState.Fail, State(m, "A7.2"));
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
            Assert.Contains(rs, r => r.Note == "Doors: 2 material, 1 tidak ikut JKR");
            Assert.Equal(SpecState.NeedsPerson, State(m, "A7.3"));
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
