using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Section D (Architectural Families) and E (View Templates) spec evaluators
    /// (Services/JkrSpec/SpecChecks.DE.cs) over hand-built snapshots. Rules:
    /// Tests/data/jkr_spec_rules.tsv rows Da.1–De.3, E.1.
    /// </summary>
    public class SpecChecksDETests
    {
        private static long _nextId = 1000;

        private static SpecType T(string category, string typeName, string family = "", int instances = 2)
        {
            var typeId = ++_nextId;
            var ids = Enumerable.Range(0, instances).Select(_ => ++_nextId).ToList();
            return new SpecType
            {
                TypeId = typeId, Category = category, TypeName = typeName, FamilyName = family,
                InstanceCount = instances, InstanceIds = ids,
            };
        }

        private static SpecModel Model(params SpecType[] types)
        {
            var m = new SpecModel();
            m.Types.AddRange(types);
            return m;
        }

        private static List<SpecResult> Run(string id, SpecModel m) =>
            SpecChecks.Rules[id](m.Normalize()).ToList();

        private static List<SpecResult> Fails(List<SpecResult> r) => r.Where(x => x.Status == "fail").ToList();
        private static SpecResult PassOf(List<SpecResult> r) => r.Single(x => x.Status == "pass");
        private static List<SpecResult> Passes(List<SpecResult> r) => r.Where(x => x.Status == "pass").ToList();

        private static SpecCheckResult Report(string id, SpecModel m)
        {
            var results = Run(id, m);
            foreach (var r in results) r.Source = SpecReport.Model;
            return SpecReport.Build(results).For(id);
        }

        // ── registry ──

        [Fact]
        public void Every_DE_check_is_registered()
        {
            foreach (var id in new[] { "Da.1", "Db.1", "Db.2", "Dc.1", "Dd.1", "Dd.2", "De.1", "De.2", "De.3", "E.1" })
                Assert.True(SpecChecks.Rules.ContainsKey(id), id);
            foreach (var id in new[] { "Da.1", "Db.1", "Db.2" })
                Assert.False(string.IsNullOrWhiteSpace(SpecChecks.BackendOnly[id]), id);
            // Spec revision 2: E.1 no longer judges WIP views, so it needs no backend data.
            Assert.False(SpecChecks.BackendOnly.ContainsKey("E.1"));
        }

        [Fact]
        public void Every_DE_check_runs_on_an_empty_model_without_a_pass_it_did_not_prove()
        {
            var m = new SpecModel();
            foreach (var id in new[] { "Da.1", "Db.1", "Db.2", "Dc.1", "Dd.1", "E.1", "De.2", "De.3" })
                Assert.DoesNotContain(Run(id, m), r => r.Status == "pass" && r.Coverage == SpecCoverage.Full);
        }

        // ── Da.1 ──

        [Fact]
        public void Da1_generic_names_fail_and_JKR_coded_names_pass_partly()
        {
            var door1 = T("Doors", "Door 1");
            var basic = T("Walls", "Basic Wall");
            var coded = T("Doors", "(PT2p600a) 900 x 2325 a");
            var prefixed = T("Walls", "jkrAR_wll-b_(bb02)-3");
            var r = Run("Da.1", Model(door1, basic, coded, prefixed));

            var fails = Fails(r);
            Assert.Equal(2, fails.Count);
            Assert.All(fails, f => Assert.Equal(SpecCoverage.FailOnly, f.Coverage));
            var f1 = fails.Single(f => f.ElementName == "Doors · Door 1");
            Assert.Equal(door1.InstanceIds[0], f1.ElementId);
            Assert.Equal(door1.InstanceIds, f1.ElementIds);
            Assert.Contains("nama generik", f1.Note);
            Assert.Contains("generic name", f1.NoteEn);
            Assert.Equal("Doors", f1.Category);
            Assert.Equal("Walls", fails.Single(f => f != f1).Category);

            // One aggregated pass per category, filed under that category.
            var passes = Passes(r);
            Assert.Equal(new[] { "Doors", "Walls" }, passes.Select(p => p.Category).OrderBy(c => c));
            Assert.All(passes, p => Assert.Equal(1, p.ElementCount));
            Assert.All(passes, p => Assert.Equal(SpecCoverage.FailOnly, p.Coverage));
        }

        [Fact]
        public void Da1_a_loadable_family_coded_in_its_family_name_passes_system_families_still_fail()
        {
            // Real model, 16 Nov: the code sits in the family name, the type holds the size.
            var window = T("Windows", "1800 X 500 mm @TK11a", "jkrAR18_wdw_TKt_Q12b1_ex_(TKt001)-3 @TK01");
            var trap = T("Plumbing Fixtures", "Standard", "jkrAR20_plb-fx_LSf_(LSm240)-3 Floor Trap rectangular");
            var curtain = T("Walls", "Curtain Wall", "Curtain Wall");
            var panel = T("Curtain Panels", "Wall", "System Panel");
            var counter = T("Casework", "Square_rectangle_concrete_counter_top_toilet 1000", "Square_rectangle_concrete_counter_top");
            var r = Run("Da.1", Model(window, trap, curtain, panel, counter));

            var failed = Fails(r).Select(f => f.ElementName).OrderBy(n => n).ToList();
            Assert.Equal(new[]
            {
                "Casework · Square_rectangle_concrete_counter_top_toilet 1000",
                "Curtain Panels · Wall",
                "Walls · Curtain Wall",
            }, failed);
            var note = r.Single(x => x.Coverage == SpecCoverage.Context);
            Assert.Contains("2 type", note.Note);
            Assert.Contains("family name", note.NoteEn);
        }

        [Fact]
        public void Da1_bare_material_name_is_not_JKR_coding()
        {
            var r = Run("Da.1", Model(T("Windows", "UPVC")));
            Assert.Single(Fails(r));
        }

        [Fact]
        public void Da1_ignores_categories_outside_section_D_and_gives_stable_ids()
        {
            var framing = T("Structural Framing", "W10x12");
            Assert.Empty(Run("Da.1", Model(framing)));

            var door = T("Doors", "Door 1");
            var a = Run("Da.1", Model(door)).Single();
            var b = Run("Da.1", Model(door)).Single();
            Assert.Equal(a.IssueId, b.IssueId);
        }

        [Fact]
        public void Da1_with_only_fails_is_a_failing_check()
        {
            var s = Report("Da.1", Model(T("Doors", "Door 1")));
            Assert.Equal(SpecState.Fail, s.State);
        }

        [Fact]
        public void Da1_with_only_coded_names_is_partly_checked_never_a_full_pass()
        {
            var s = Report("Da.1", Model(T("Doors", "(PT2p600a) 900 x 2325 a")));
            Assert.Equal(SpecState.Partial, s.State);
        }

        // ── Db.1 ──

        [Fact]
        public void Db1_below_LOD_400_judges_nothing()
        {
            var t = T("Doors", "(PTc080a) 1800 x 2100");
            t.Texts["Kod_Jenis_jkr_stt"] = "";
            var m = Model(t); m.Lod = 300;
            Assert.Empty(Run("Db.1", m));
        }

        [Fact]
        public void Db1_at_LOD_400_empty_common_parameters_fail()
        {
            var empty = T("Doors", "(PTc080a) 1800 x 2100");
            empty.Texts["Kod_Jenis_jkr_stt"] = " ";
            empty.Texts["Pembekal_jkr_sit"] = "";
            empty.Texts["Kontraktor_jkr_sit"] = "Greatech Sdn Bhd";
            var filled = T("Walls", "(DGb493a) 150");
            filled.Texts["Kod_Jenis_jkr_stt"] = "DGb";
            var unread = T("Floors", "(LBk201a) 150");   // carries none of the parameters: not judged
            var m = Model(empty, filled, unread); m.Lod = 400;

            var r = Run("Db.1", m);
            var f = Fails(r).Single();
            Assert.Equal(SpecCoverage.FailOnly, f.Coverage);
            Assert.Contains("2/3", f.Note);
            Assert.Contains("Kod_Jenis_jkr_stt", f.Note);
            Assert.Contains("Pembekal_jkr_sit", f.Note);
            Assert.DoesNotContain("Kontraktor_jkr_sit,", f.Note);
            Assert.Equal(1, PassOf(r).ElementCount);
            Assert.Equal(SpecCoverage.FailOnly, PassOf(r).Coverage);
        }

        // ── Db.2 ──

        [Fact]
        public void Db2_no_material_or_non_JKR_material_fails()
        {
            var none = T("Doors", "(PTc080a) 1800 x 2100");
            var revit = T("Windows", "(TKk360a) 600 x 1800");
            revit.Materials.AddRange(new[] { "jkrAR_wdw-b_(kl)", "Glass" });
            var jkr = T("Walls", "(DGb493a) 150");
            jkr.Materials.Add("jkrAR_wll-b_(bb02)-3");
            var r = Run("Db.2", Model(none, revit, jkr));

            var fails = Fails(r);
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note.Contains("tiada material") && f.NoteEn.Contains("has no material"));
            Assert.Contains(fails, f => f.Note.Contains("'Glass'") && !f.Note.Contains("jkrAR_wdw"));
            Assert.Equal(1, PassOf(r).ElementCount);
            Assert.All(r, x => Assert.Equal(SpecCoverage.FailOnly, x.Coverage));
        }

        [Theory]
        [InlineData("jkrAR_dor-b_(ab)-2 Aluminium Bronzed Anodised", true)]
        [InlineData("jkrAR_(kt)", true)]
        [InlineData("JKR Glass", false)]
        [InlineData("Glass", false)]
        [InlineData("", false)]
        public void Db2_JKR_material_prefix(string name, bool expected) =>
            Assert.Equal(expected, SpecChecks.IsJkrMaterial(name));

        // ── Dc.1 ──

        [Theory]
        [InlineData("Walls", "Wall 146", "Width", 146)]
        [InlineData("Walls", "Interior - 138mm Partition (1-hr)", "Width", 138)]
        [InlineData("Walls", "(DBb300a) Dinding 150mm", "Width", 150)]
        [InlineData("Floors", "Generic 150mm", "Thickness", 150)]
        [InlineData("Ramps", "Ramp 50mm", "Thickness", 50)]
        [InlineData("Railings", "1100mm", "Height", 1100)]
        public void Dc1_reads_one_number_as_the_category_parameter(string cat, string name, string param, double value)
        {
            var got = SpecChecks.NameNumbers(cat, name);
            Assert.Single(got);
            Assert.Equal(param, got[0].Param);
            Assert.Equal(value, got[0].Value);
        }

        [Theory]
        [InlineData("Ceilings", "600 x 600mm Grid")]     // a grid, not a thickness
        [InlineData("Railings", "Handrail - Pipe 40mm")]  // pipe size, not the height
        [InlineData("Walls", "Fire wall 60 min")]
        [InlineData("Walls", "Generic - 8\"")]
        [InlineData("Walls", "CW 102-85-100p")]           // several numbers: ambiguous
        [InlineData("Floors", "Slab 2.4m")]
        [InlineData("Doors", "Single-Flush")]
        [InlineData("Casework", "Counter 600")]           // no parameter to compare
        public void Dc1_does_not_read_ambiguous_or_non_mm_numbers(string cat, string name)
        {
            Assert.Empty(SpecChecks.NameNumbers(cat, name));
        }

        [Fact]
        public void Dc1_door_width_and_height_from_the_name()
        {
            var got = SpecChecks.NameNumbers("Doors", "(PT2p600a) 900 x 2325 a");
            Assert.Equal(new[] { ("Width", 900.0), ("Height", 2325.0) }, got.Select(x => (x.Param, x.Value)));
            got = SpecChecks.NameNumbers("Windows", "M_Fixed 0915 x 1220mm");
            Assert.Equal(new[] { ("Width", 915.0), ("Height", 1220.0) }, got.Select(x => (x.Param, x.Value)));
        }

        [Fact]
        public void Dc1_lists_mismatches_and_counts_matches()
        {
            var wall = T("Walls", "Wall 146");
            wall.Numbers["Width"] = 150;
            var wallOk = T("Walls", "Wall 200");
            wallOk.Numbers["Width"] = 200.4;
            var door = T("Doors", "(PT2p600a) 900 x 2325 a");
            door.Numbers["Width"] = 900; door.Numbers["Height"] = 2100;
            var noHeight = T("Doors", "(PT1p850a) 850 x 2100");
            noHeight.Numbers["Width"] = 850;
            var r = Run("Dc.1", Model(wall, wallOk, door, noHeight));

            var fails = Fails(r);
            Assert.Equal(2, fails.Count);
            var w = fails.Single(f => f.ElementName == "Walls · Wall 146");
            Assert.Contains("Width 150 mm (nama: 146 mm)", w.Note);
            Assert.Contains("Width 150 mm (name: 146 mm)", w.NoteEn);
            Assert.Equal("Width 150 mm", w.Actual);
            Assert.Equal("Width 146 mm", w.Required);
            var d = fails.Single(f => f.ElementName.StartsWith("Doors · (PT2p600a)"));
            Assert.Contains("Height 2100 mm (nama: 2325 mm)", d.Note);
            Assert.DoesNotContain("Width 900", d.Note);

            var unj = r.Single(x => x.Status == "cannot_verify");
            Assert.Contains("Height", unj.Note);
            Assert.Equal(1, PassOf(r).ElementCount);
        }

        // ── Dd.1 ──

        [Fact]
        public void Dd1_window_code_or_name_in_Doors_fails()
        {
            var tkInDoors = T("Doors", "(TKh281a) 600 x 1800");
            var windowInDoors = T("Doors", "Window 1200");
            var floorAsGeneric = T("Generic Models", "Floor 150");
            var okDoor = T("Doors", "(PT2p600a) 900 x 2325 a");
            var okWall = T("Walls", "Wall 146");
            var r = Run("Dd.1", Model(tkInDoors, windowInDoors, floorAsGeneric, okDoor, okWall));

            var fails = Fails(r);
            Assert.Equal(3, fails.Count);
            var tk = fails.Single(f => f.ElementName.Contains("TKh281a"));
            Assert.Equal("Doors", tk.Actual);
            Assert.Contains("Windows", tk.Required);
            Assert.Contains("kod (TK…)", tk.Note);
            Assert.Contains(fails, f => f.ElementName == "Generic Models · Floor 150" && f.Required == "Floors" && f.Category == "Generic Models");
            Assert.Equal(new[] { "Doors", "Walls" }, Passes(r).Select(p => p.Category).OrderBy(c => c));
            Assert.All(r, x => Assert.Equal(SpecCoverage.FailOnly, x.Coverage));
        }

        [Fact]
        public void Dd1_accessories_named_after_their_host_are_not_flagged()
        {
            var r = Run("Dd.1", Model(
                T("Plumbing Fixtures", "Floor Trap 100"),
                T("Casework", "Wall Cabinet 600"),
                T("Specialty Equipment", "Door Closer"),
                T("Doors", "Double Glass", "Curtain Wall Dbl Glass"),
                T("Doors", "915 x 2134mm", "M_Single-Flush"),
                T("Railings", "Stair Railing 900")));
            Assert.Empty(Fails(r));
        }

        [Fact]
        public void Dd1_family_name_says_the_category_when_the_type_name_does_not()
        {
            var r = Run("Dd.1", Model(T("Doors", "0915 x 1220mm", "Window-Fixed")));
            var f = Fails(r).Single();
            Assert.Contains("famili", f.Note);
            Assert.Contains("family", f.NoteEn);
        }

        // ── Dd.2, De.1–De.3 ──

        [Fact]
        public void Dd2_is_context_for_a_person()
        {
            var r = Run("Dd.2", new SpecModel()).Single();
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.Contains("SEMAK MANUAL", r.Note);
        }

        [Fact]
        public void De1_points_at_A6_and_C16_with_the_room_count()
        {
            var m = new SpecModel();
            m.Rooms.Add(new SpecRoom { Id = 1, Area = 12 });
            m.Rooms.Add(new SpecRoom { Id = 2, Area = 0 });
            var r = Run("De.1", m).Single();
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
            Assert.Equal(1, r.ElementCount);
            Assert.Contains("A6 / C1.6", r.Note);
        }

        [Fact]
        public void De2_both_counts_in_one_note_and_scope_asked_only_for_the_zero_category()
        {
            // The real-model run: "Columns 0" while the model had 4 Structural Columns.
            var m = new SpecModel();
            m.CategoryCounts["Columns"] = 0;
            m.CategoryCounts["Structural Columns"] = 4;
            var zero = Run("De.2", m).Single();
            Assert.Equal(SpecCoverage.Context, zero.Coverage);
            Assert.Equal("Columns: 0 · Structural Columns: 4 — Columns 0 elemen: sahkan skop", zero.Note);
            Assert.Equal("Columns: 0 · Structural Columns: 4 — Columns 0 elements: confirm scope", zero.NoteEn);
            Assert.Equal("Columns", zero.Category);
            Assert.False(string.IsNullOrEmpty(zero.IssueId));
            Assert.Equal(SpecChecks.StableId("De.2", "Columns"), zero.IssueId);   // same id as before the merge

            var both = new SpecModel();
            both.CategoryCounts["Walls"] = 3;                 // counts read, no columns at all
            var b = Run("De.2", both).Single();
            Assert.Equal("Columns: 0 · Structural Columns: 0 — Columns, Structural Columns 0 elemen: sahkan skop", b.Note);
            Assert.False(string.IsNullOrEmpty(b.IssueId));

            var none = new SpecModel();
            none.CategoryCounts["Columns"] = 2;
            none.CategoryCounts["Structural Columns"] = 4;
            var n = Run("De.2", none).Single();
            Assert.Equal("", n.IssueId);
            Assert.StartsWith("Columns: 2 · Structural Columns: 4", n.Note);
            Assert.DoesNotContain("sahkan skop", n.Note);

            Assert.Equal(SpecState.NeedsPerson, Report("De.2", m).State);
            Assert.Empty(Run("De.2", new SpecModel()));   // counts not read: nothing
        }

        [Fact]
        public void De3_lists_the_Buku_Parameter_categories_not_modelled()
        {
            var m = new SpecModel();
            m.CategoryCounts["Walls"] = 10;
            m.CategoryCounts["Doors"] = 3;
            m.CategoryCounts["Structural Columns"] = 2;
            m.CategoryCounts["Ramps"] = 0;
            var r = Run("De.3", m);
            Assert.Equal(17, r.Count);                  // 20 PB AR categories − Wall, Door, Column
            Assert.All(r, x => Assert.Equal(SpecCoverage.Listing, x.Coverage));
            Assert.Contains(r, x => x.Category == "Ramps" && x.Note.StartsWith("Ramp:"));
            Assert.Contains(r, x => x.Category == "Plumbing Fixtures");
            Assert.DoesNotContain(r, x => x.Category == "Walls" || x.Category == "Doors");
            Assert.DoesNotContain(r, x => x.Category == "Columns");   // Structural Columns count for Column
            Assert.Empty(Run("De.3", new SpecModel()));
        }

        // ── E.1 ──

        private static SpecView V(long id, string name, string template) =>
            new SpecView { Id = id, Name = name, Kind = "FloorPlan", TemplateName = template };

        [Fact]
        public void E1_each_set_uses_its_own_template()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "BOMBA - Aras 01", "JKR_BOMBA Floor Plan"));
            m.Views.Add(V(2, "PBT - Aras 01", "JKR_BOMBA Floor Plan"));
            m.Views.Add(V(3, "Dokumen - Aras 01", ""));
            m.Views.Add(V(4, "Dokumen - Seksyen A", "Dokumen Section"));
            m.Views.Add(V(5, "Level 1", ""));                       // in no set: not judged
            var r = Run("E.1", m);

            var fails = Fails(r);
            Assert.Equal(2, fails.Count);
            var pbt = fails.Single(f => f.ElementId == 2);
            Assert.Contains("bukan template PBT", pbt.Note);
            Assert.Equal("JKR_BOMBA Floor Plan", pbt.Actual);
            Assert.Contains(fails, f => f.ElementId == 3 && f.Actual == "tiada" && f.Category == "Dokumen");
            Assert.Equal("PBT", pbt.Category);
            Assert.Equal("PBT - Aras 01", pbt.ElementName);
            var passes = Passes(r);
            Assert.Equal(new[] { "BOMBA", "Dokumen" }, passes.Select(p => p.Category).OrderBy(c => c));
            Assert.All(passes, p => Assert.Equal(SpecCoverage.Full, p.Coverage));
            Assert.All(fails, f => Assert.False(string.IsNullOrEmpty(f.ElementName)));
        }

        [Fact]
        public void E1_WIP_views_are_listed_with_their_template_and_never_judged()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "WIP_Aras 01", ""));
            m.Views.Add(V(2, "WIP_Aras 02", "PBT Plan"));
            m.Views.Add(V(3, "WIP_Aras 03", "Architectural Plan"));
            m.Views.Add(V(4, "BOMBA - Aras 01", "Bomba"));
            var r = Run("E.1", m);
            Assert.Empty(Fails(r));
            Assert.DoesNotContain(r, x => x.Status == "cannot_verify");
            var wip = r.Where(x => x.Coverage == SpecCoverage.Listing).ToList();
            Assert.Equal(3, wip.Count);
            Assert.All(wip, x => Assert.Equal("WIP", x.Category));
            Assert.All(wip, x => Assert.Equal("", x.IssueId));
            Assert.Equal(new[] { "WIP_Aras 01", "WIP_Aras 02", "WIP_Aras 03" }, wip.Select(x => x.ElementName));
            Assert.Contains(wip, x => x.Note == "WIP | view 'WIP_Aras 01' | template: tiada — tiada peraturan template WIP (menunggu pengesahan JKR)");
            Assert.Contains(wip, x => x.NoteEn.StartsWith("WIP | view 'WIP_Aras 02' | template: 'PBT Plan'"));
            // WIP no longer holds BOMBA's pass back: every BOMBA / PBT / Dokumen view was judged → Full.
            var pass = r.Single(x => x.Status == "pass" && x.Coverage != SpecCoverage.Listing);
            Assert.Equal("BOMBA", pass.Category);
            Assert.Equal(SpecCoverage.Full, pass.Coverage);
            var s = Report("E.1", m);
            Assert.Equal(SpecState.Pass, s.State);
            Assert.Equal(1, s.Passed);
            Assert.Equal(3, s.Listed);
        }

        [Fact]
        public void E1_only_WIP_views_is_unjudged_never_a_pass()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "WIP_Aras 01", "Architectural Plan"));
            var r = Run("E.1", m);
            Assert.Single(r, x => x.Coverage == SpecCoverage.Listing);
            var u = r.Single(x => x.Status == "cannot_verify");
            Assert.Contains("BOMBA / PBT / Dokumen", u.Note);
            Assert.Equal(SpecState.NeedsPerson, Report("E.1", m).State);
        }

        [Fact]
        public void E1_an_unjudged_set_view_makes_the_passes_partial()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "BOMBA - Aras 01", "Bomba"));
            m.Views.Add(V(2, "BOMBA PBT Aras 02", "Bomba"));
            var r = Run("E.1", m);
            Assert.Equal(SpecCoverage.FailOnly, Passes(r).Single().Coverage);
            var u = r.Single(x => x.Status == "cannot_verify");
            Assert.Equal("BOMBA PBT Aras 02", u.ElementName);
            Assert.Equal("BOMBA", u.Category);
        }

        [Fact]
        public void E1_set_tokens_are_whole_words_and_two_sets_are_unjudged()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "Wipe test", ""));                     // "Wipe" is not WIP
            m.Views.Add(V(2, "BOMBA PBT Aras 01", "Bomba"));
            var r = Run("E.1", m);
            var u = r.Single();
            Assert.Equal("cannot_verify", u.Status);
            Assert.Equal(2, u.ElementId);
        }

        private static SpecView InFolder(long id, string name, string kind, string folder, string template) =>
            new SpecView { Id = id, Name = name, Kind = kind, TemplateName = template, FolderPath = folder,
                FolderBy = "View_jkr_pit / Jenis_Lukisan_jkr_pit / Family", SetParam = folder.Split(' ')[0].Replace("???", "") };

        [Fact]
        public void E1_the_browser_folder_decides_the_set_not_the_name()
        {
            var m = new SpecModel();
            // In 07d_Dokumen with a Dokumen template: a Dokumen pass, not a BOMBA fail.
            m.Views.Add(InFolder(1, "BOMBA Pelan Aras 01", "FloorPlan", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", "jkrAR_07d_Dokumen_(Pelan Aras 1)"));
            // In 07d_Dokumen with no template and no "Dokumen" in its name: judged, fails.
            m.Views.Add(InFolder(2, "KERATAN J-J", "Detail", "07d_Dokumen / 02_Butiran / Detail Views", ""));
            var r = Run("E.1", m);
            var f = Assert.Single(Fails(r));
            Assert.Equal(2, f.ElementId);
            Assert.Equal("Dokumen", f.Category);
            var p = Assert.Single(Passes(r));
            Assert.Equal("Dokumen: 1 view guna template set yang betul", p.Note);
            Assert.DoesNotContain(r, x => x.Category == "BOMBA");
        }

        [Fact]
        public void E1_skips_legends_and_says_so_once()
        {
            var m = new SpecModel();
            m.Views.Add(InFolder(1, "Petunjuk Bomba", "DraftingView", "05b_Bomba / 02_Butiran / Drafting Views", ""));
            m.Views.Add(InFolder(2, "jkrAR_lgd_Kehendak Bomba", "Legend", "??? / ??? / Legends", ""));
            m.Views.Add(InFolder(3, "jkrAR_lgd_Petunjuk Bomba", "Legend", "??? / ??? / Legends", ""));
            m.Views.Add(InFolder(4, "jkrAR_lgd_Simbol", "Legend", "??? / ??? / Legends", ""));   // in no set
            var r = Run("E.1", m);
            var f = Assert.Single(Fails(r));
            Assert.Equal("Petunjuk Bomba", f.ElementName);
            var skipped = Assert.Single(r, x => x.Coverage == SpecCoverage.Listing);
            Assert.Equal("2 legend dilangkau — legend tidak boleh guna view template (jkrAR_lgd_Kehendak Bomba, jkrAR_lgd_Petunjuk Bomba)", skipped.Note);
            Assert.StartsWith("2 legends skipped", skipped.NoteEn);
            Assert.Equal("BOMBA", skipped.Category);
        }

        [Fact]
        public void E1_a_view_whose_only_set_is_its_template_counts_for_that_set()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "Pelan Aras 01", "jkrAR_05b_Bomba_(Pelan Aras 1)"));
            var p = Assert.Single(Run("E.1", m));
            Assert.Equal("pass", p.Status);
            Assert.Equal("BOMBA", p.Category);
        }

        [Fact]
        public void E1_no_set_view_is_unjudged_not_passed()
        {
            var m = new SpecModel();
            m.Views.Add(V(1, "Level 1", "Architectural Plan"));
            var r = Run("E.1", m).Single();
            Assert.Equal("cannot_verify", r.Status);
            Assert.Empty(Run("E.1", new SpecModel()));
        }
    }
}
