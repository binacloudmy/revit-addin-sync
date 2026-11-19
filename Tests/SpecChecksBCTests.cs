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
    /// Spec sections B and C (Services/JkrSpec/SpecChecks.BC.cs) over hand-built snapshots,
    /// and the Buku Parameter data they use (SpecPbData.cs).
    /// </summary>
    public class SpecChecksBCTests
    {
        private static readonly string[] Ids =
        {
            "B1.1", "B1.2", "B1.3", "C1.0.1", "C1.1.1", "C1.1.2", "C1.1.3", "C1.2.1", "C1.3.1",
            "C1.4.1", "C1.5.1", "C1.5.2", "C1.6.1", "C1.6.2", "C1.6.3", "C2.1.1", "C3.1.1", "C3.1.2", "C4.1.1",
            "C4.1.2", "C4.1.3", "C4.2.1", "C4.2.2", "C4.2.3", "C5.1.1", "C5.1.2", "C5.1.3",
        };

        private static List<SpecResult> Run(string id, SpecModel m) =>
            SpecChecks.Rules[id]((m ?? new SpecModel()).Normalize()).ToList();

        private static List<SpecResult> Fails(List<SpecResult> r) => r.Where(x => x.Status == "fail").ToList();

        private static SpecCheckResult State(string id, SpecModel m)
        {
            var results = Run(id, m);
            foreach (var r in results) { r.Source = SpecReport.Model; if (string.IsNullOrEmpty(r.CheckId)) r.CheckId = id; }
            return SpecReport.Build(results).For(id);
        }

        /// <summary>Every AR Project Information field filled with a real value.</summary>
        private static Dictionary<string, string> FullProjectInfo() =>
            SpecPbData.ProjectInfoAr.ToDictionary(f => f.Name, f => "nilai " + f.Name);

        private static SpecLevel Level(long id, string name, double elev) => new SpecLevel { Id = id, Name = name, Elevation = elev, IsStory = true };

        private static SpecView View(long id, string name, string kind, string level = "", bool onSheet = false, string sheet = "") =>
            new SpecView { Id = id, Name = name, Kind = kind, LevelName = level, OnSheet = onSheet, SheetNumber = sheet };

        /// <summary>A small AR model that meets every B / C rule the add-in decides.</summary>
        private static SpecModel Compliant()
        {
            var m = new SpecModel { Discipline = "AR", Lod = 300, ProjectInfo = FullProjectInfo() };
            m.Levels.Add(Level(1, "Aras 01", 0));
            m.Levels.Add(Level(2, "Aras 02", 4000));
            foreach (var lv in m.Levels)
            {
                m.Views.Add(View(10 + lv.Id, "WIP " + lv.Name, "FloorPlan", lv.Name));
                m.Views.Add(View(20 + lv.Id, "WIP Siling " + lv.Name, "CeilingPlan", lv.Name));
                m.Views.Add(View(30 + lv.Id, "3D " + lv.Name, "ThreeD"));
                m.Views.Add(View(40 + lv.Id, "Dokumen 3D " + lv.Name, "ThreeD", "", true, "A-301"));
            }
            m.Views.Add(View(50, "PBT Pelan Aras 01", "FloorPlan", "Aras 01"));
            m.Views.Add(View(51, "PBT Elevation Utara", "Elevation"));
            m.Views.Add(View(52, "PBT Section A-A", "Section"));
            m.Views.Add(View(53, "BOMBA Pelan Aras 01", "FloorPlan", "Aras 01"));
            m.Views.Add(View(54, "BOMBA Section B-B", "Section"));
            m.Views.Add(View(55, "BOMBA Drafting Laluan", "DraftingView"));
            m.Views.Add(View(56, "Legend Pintu", "Legend", "", true, "A-901"));
            m.ViewTemplates.Add("jkrAR_Pelan");

            m.Rooms.Add(new SpecRoom { Id = 100, Name = "Bilik Mesyuarat 1", Number = "001", LevelName = "Aras 01", Area = 20,
                Params = SpecPbData.RoomFields.ToDictionary(f => f.Name, f => "x") });
            m.Rooms.Add(new SpecRoom { Id = 101, Name = "Pejabat", Number = "002", LevelName = "Aras 01", Area = 15,
                Params = SpecPbData.RoomFields.ToDictionary(f => f.Name, f => "y") });
            m.Schedules.Add(new SpecSchedule { Id = 200, Name = "Jadual Ruang", Category = "Rooms",
                Fields = SpecPbData.RoomFields.Select(f => f.Name).Concat(new[] { "Area" }).ToList(), RowElementIds = new List<long> { 100, 101 } });
            m.Schedules.Add(new SpecSchedule { Id = 201, Name = "Jadual Pintu", Category = "Doors" });
            m.CategoryCounts["Doors"] = 4;
            m.CategoryCounts["Rooms"] = 2;
            m.CategoryCounts["Lines"] = 40;   // not a Buku Parameter category

            m.Sheets.Add(Sheet(300, "A-101", "Pelan Aras", new[] { "FloorPlan", "CeilingPlan" }, new[] { "Dokumen Pelan", "Dokumen Siling" }));
            m.Sheets.Add(Sheet(301, "A-201", "Elevation & Section", new[] { "Elevation", "Section" }, new[] { "Dokumen Elev", "Dokumen Sec" }));
            m.Sheets.Add(Sheet(302, "A-501", "Detail", new[] { "Detail", "DraftingView" }, new[] { "Detail Pintu D1", "Detail Tingkap W1" }));

            m.Links.Add(new SpecLink { Name = "jkrST24_5a_(BEde1A_p14-001)_S1_w-01.rvt", Status = "Loaded" });
            m.Links.Add(new SpecLink { Name = "jkrME24_5a_(BEde1A_p14-001)_M1_w-01.rvt", Status = "Loaded" });
            m.Links.Add(new SpecLink { Name = "jkrEL24_5a_(BEde1A_p14-001)_E1_w-01.rvt", Status = "Loaded" });
            return m;
        }

        private static SpecSheet Sheet(long id, string no, string name, string[] kinds, string[] views) => new SpecSheet
        {
            Id = id, Number = no, Name = name, TitleBlock = "jkrAR_TitleBlock_A1 : A1",
            ViewKinds = kinds.ToList(), ViewNames = views.ToList(),
            Params = new Dictionary<string, string>
            {
                ["Project Name"] = "Ibu Pejabat JKR", ["Sheet Name"] = name, ["Drawn By"] = "Faisal bin Ali", ["Sheet Issue Date"] = "16.11.2026",
            },
        };

        // ══════════════ the registry ══════════════

        [Fact]
        public void Every_B_and_C_check_has_an_evaluator()
        {
            foreach (var id in Ids) Assert.True(SpecChecks.Rules.ContainsKey(id), id + " has no evaluator");
        }

        [Theory]
        [InlineData("C1.1.1")]
        [InlineData("C1.1.2")]
        [InlineData("C5.1.2")]
        public void Backend_dependencies_are_stated(string id)
        {
            Assert.True(SpecChecks.BackendOnly.TryGetValue(id, out var why));
            Assert.False(string.IsNullOrWhiteSpace(why));
        }

        [Fact]
        public void Every_result_has_a_BM_and_an_English_note()
        {
            foreach (var model in new[] { Compliant(), new SpecModel() })
                foreach (var id in Ids)
                    foreach (var r in Run(id, model))
                    {
                        Assert.False(string.IsNullOrWhiteSpace(r.Note), id + " result without a note");
                        Assert.False(string.IsNullOrWhiteSpace(r.NoteEn), id + " result without English: " + r.Note);
                    }
        }

        [Fact]
        public void The_compliant_model_fails_nothing()
        {
            var m = Compliant();
            foreach (var id in Ids)
                Assert.True(Fails(Run(id, m)).Count == 0, id + ": " + string.Join(" / ", Fails(Run(id, m)).Select(r => r.Note)));
        }

        [Fact]
        public void An_empty_model_gets_no_pass_from_B_or_C()
        {
            // Nothing read is never a pass (list-only and context rows are not verdicts).
            foreach (var id in Ids)
                foreach (var r in Run(id, new SpecModel()))
                    if (r.Coverage == SpecCoverage.Full || r.Coverage == SpecCoverage.FailOnly || r.Coverage == null)
                        Assert.False(r.Status == "pass" && !r.NotApplicable, id + " passed an empty model: " + r.Note);
        }

        // ══════════════ Buku Parameter data ══════════════

        [Fact]
        public void PB_AR_project_information_is_transcribed_with_its_LOi()
        {
            var ar = SpecPbData.ProjectInfoAr;
            Assert.Equal(101, ar.Count);
            Assert.Equal(ar.Count, ar.Select(f => f.Name).Distinct().Count());
            PbField F(string n) => ar.Single(f => f.Name == n);
            Assert.Equal("100–500", F("Building Name").LoiRange);
            Assert.Equal("100–500", F("Pengarah_Projek_jkr_sit").LoiRange);
            Assert.Equal("200–500", F("Perunding_Arkitek_Landskap_jkr_sit").LoiRange);
            Assert.Equal("100–500", F("Kedudukan_Project_Base_Point_jkr_sit").LoiRange);
            Assert.Equal("300–500", F("URL_Projek_jkr_siu").LoiRange);
            Assert.Equal("400–500", F("Harga_Kontrak_jkr_sic").LoiRange);
            Assert.Equal("400–500", F("Kategori_Premis_jkr_sit").LoiRange);
            Assert.Equal("400–500", F("Sub_Kategori_Premis_jkr_sit").LoiRange);
            Assert.Equal("500", F("No_DPA_jkr_sit").LoiRange);
            Assert.Equal("400–500", F("Aset_Warisan_jkr_siy").LoiRange);   // its "Contoh" ✓ is not an LOi tick
            Assert.All(ar, f => Assert.True(f.HasLoi && f.ToLoi == 500 && f.Page >= 30 && f.Page <= 34, f.ToString()));
        }

        [Fact]
        public void PB_LOi_filtering_counts_only_the_fields_marked_at_that_LOi()
        {
            var ar = SpecPbData.ProjectInfoAr;
            var at100 = SpecPbData.RequiredAt(ar, 100);
            var at300 = SpecPbData.RequiredAt(ar, 300);
            var at500 = SpecPbData.RequiredAt(ar, 500);
            Assert.Contains(at100, f => f.Name == "Building Name");
            Assert.DoesNotContain(at100, f => f.Name == "Perunding_Arkitek_jkr_sit");
            Assert.Contains(at300, f => f.Name == "URL_Projek_jkr_siu");
            Assert.DoesNotContain(at300, f => f.Name == "Harga_Kontrak_jkr_sic");
            Assert.Equal(ar.Count, at500.Count);
            Assert.True(at100.Count < at300.Count && at300.Count < at500.Count);
            Assert.Equal(at100.Select(f => f.Name), SpecPbData.RequiredAtEveryLoi(ar).Select(f => f.Name));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(50, 0)]
        [InlineData(100, 100)]
        [InlineData(350, 300)]
        [InlineData(400, 400)]
        [InlineData(500, 500)]
        [InlineData(600, 500)]
        public void Project_LOD_maps_to_an_LOi_column(int lod, int loi) => Assert.Equal(loi, SpecPbData.LoiOf(lod));

        [Fact]
        public void PB_other_disciplines_and_room_columns_are_transcribed()
        {
            Assert.Equal(71, SpecPbData.ProjectInfoSt.Count);
            Assert.Equal(80, SpecPbData.ProjectInfoMe.Count);
            Assert.Equal(80, SpecPbData.ProjectInfoEl.Count);
            Assert.Null(SpecPbData.ProjectInfoFor("CD"));
            Assert.Same(SpecPbData.ProjectInfoMe, SpecPbData.ProjectInfoFor(" me "));
            Assert.Equal("300–500", SpecPbData.ProjectInfoSt.Single(f => f.Name == "Harga_Kontrak_jkr_sic").LoiRange);
            Assert.Equal("400–500", SpecPbData.ProjectInfoMe.Single(f => f.Name == "Bilangan_Blok_Bangunan_jkr_sii").LoiRange);
            Assert.False(SpecPbData.ProjectInfoEl.Single(f => f.Name == "No_Model_BIM_jkr_sit").HasLoi);
            Assert.True(SpecPbData.HasGaps(SpecPbData.ProjectInfoEl));
            Assert.False(SpecPbData.HasGaps(SpecPbData.ProjectInfoAr));

            var room200 = SpecPbData.RequiredAt(SpecPbData.RoomFields, 200).Select(f => f.Name).ToList();
            Assert.Contains("Floor Finish", room200);
            Assert.Contains("Diterima_jkr_siy", room200);
            Assert.DoesNotContain("Kod_DAK_Lokasi_jkr_sit", room200);
            var room400 = SpecPbData.RequiredAt(SpecPbData.RoomFields, 400).Select(f => f.Name).ToList();
            Assert.Contains("Kod_DAK_Lokasi_jkr_sit", room400);
            Assert.DoesNotContain("Diterima_jkr_siy", room400);
            Assert.Empty(SpecPbData.RequiredAt(SpecPbData.RoomFields, 100));
            Assert.Contains(SpecPbData.SheetFields, f => f.Name == "Drawn By" && f.LoiRange == "300–500");
        }

        [Fact]
        public void PB_AR_categories_are_the_spec_sheet_list()
        {
            var tsv = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "jkr_spec_pb_categories.tsv"))
                .Where(l => !l.StartsWith("#")).Skip(1).Select(l => l.Split('\t'))
                .Where(c => c.Length >= 2 && int.TryParse(c[1], out var p) && p >= 36 && p <= 99)
                .Select(c => (c[0], int.Parse(c[1]))).ToList();
            Assert.Equal(tsv, SpecPbData.ArCategories.Select(c => (c.Name, c.Page)).ToList());
            Assert.All(SpecPbData.ArCategories, c => Assert.NotEmpty(c.RevitCategories));
        }

        [Fact]
        public void PB_names_match_Revit_parameters_loosely()
        {
            var p = new Dictionary<string, string>
            {
                ["building name"] = "Pejabat",
                ["Pengarah_Perunding_Reka_Bentuk_Elektrik_jkr_sit"] = "Ir. Husin",
                ["Pengarah_Kanan_Mekanikal_jkr_sit"] = "Ir. Zulkifli",
            };
            Assert.True(SpecPbData.TryFind(p, "Building Name", out _, out var v1)); Assert.Equal("Pejabat", v1);
            Assert.True(SpecPbData.TryFind(p, "Pengarah_Perunding Reka Bentuk_Elektrik_jkr_sit", out _, out var v2)); Assert.Equal("Ir. Husin", v2);
            Assert.True(SpecPbData.TryFind(p, "Pengarah_Kanan_Mekanikal", out _, out var v3)); Assert.Equal("Ir. Zulkifli", v3);
            Assert.False(SpecPbData.TryFind(p, "Arkitek_jkr_sit", out _, out _));
            Assert.False(SpecPbData.TryFind(null, "Building Name", out _, out _));
        }

        // ══════════════ B — Project Information ══════════════

        [Fact]
        public void B1_1_passes_when_every_field_mandatory_at_the_LOi_is_filled()
        {
            var r = Run("B1.1", Compliant());
            var pass = Assert.Single(r);
            Assert.Equal("pass", pass.Status);
            Assert.Equal(SpecCoverage.Full, pass.Coverage);
            Assert.Contains("LOi 300", pass.Note);
            Assert.Equal(SpecState.Pass, State("B1.1", Compliant()).State);
        }

        [Fact]
        public void B1_1_fails_each_empty_missing_or_default_field_with_its_LOi()
        {
            var m = Compliant();
            m.ProjectInfo["Arkitek_jkr_sit"] = "  ";
            m.ProjectInfo["Project Address"] = "Enter address here";
            m.ProjectInfo["Client Name"] = "Owner";
            m.ProjectInfo.Remove("URL_Projek_jkr_siu");
            m.ProjectInfo["Harga_Kontrak_jkr_sic"] = "";      // LOi 400–500: not mandatory at 300
            var fails = Fails(Run("B1.1", m));
            Assert.Equal(4, fails.Count);
            var arkitek = fails.Single(f => f.Note.StartsWith("Arkitek_jkr_sit"));
            Assert.Equal("Arkitek_jkr_sit | wajib LOi 100–500 | — | kosong", arkitek.Note);
            Assert.Equal("Arkitek_jkr_sit | mandatory at LOi 100–500 | — | empty", arkitek.NoteEn);
            Assert.Contains(fails, f => f.Note == "Project Address | wajib LOi 100–500 | Enter address here | nilai lalai Revit");
            Assert.Contains(fails, f => f.Note.StartsWith("Client Name") && f.Note.EndsWith("nilai lalai Revit"));
            Assert.Contains(fails, f => f.Note.StartsWith("URL_Projek_jkr_siu | wajib LOi 300–500") && f.Note.EndsWith("tiada parameter"));
            Assert.DoesNotContain(fails, f => f.Note.StartsWith("Harga_Kontrak"));
            Assert.Equal(fails.Count, fails.Select(f => f.IssueId).Distinct().Count());
            Assert.Equal(SpecState.Fail, State("B1.1", m).State);
        }

        [Fact]
        public void B1_1_issue_ids_are_stable_across_runs()
        {
            var m = Compliant();
            m.ProjectInfo["Building Name"] = "";
            Assert.Equal(Fails(Run("B1.1", m)).Single().IssueId, Fails(Run("B1.1", m)).Single().IssueId);
        }

        [Fact]
        public void B1_1_with_an_unknown_LOD_checks_only_the_every_LOi_fields_and_is_partial()
        {
            var m = Compliant();
            m.Lod = 0;
            var pass = Assert.Single(Run("B1.1", m));
            Assert.Equal(SpecCoverage.FailOnly, pass.Coverage);
            Assert.Equal(SpecState.Partial, State("B1.1", m).State);
            m.ProjectInfo["Pengarah_Projek_jkr_sit"] = "";
            Assert.Equal(SpecCoverage.FailOnly, Fails(Run("B1.1", m)).Single().Coverage);
        }

        [Fact]
        public void B1_1_unread_project_information_is_unjudged()
        {
            var m = Compliant();
            m.ProjectInfo.Clear();
            Assert.Equal("cannot_verify", Assert.Single(Run("B1.1", m)).Status);
            var cd = Compliant();
            cd.Discipline = "CD";
            Assert.Equal("cannot_verify", Assert.Single(Run("B1.1", cd)).Status);
        }

        [Fact]
        public void B1_2_is_not_applicable_to_an_AR_model()
        {
            var r = Assert.Single(Run("B1.2", Compliant()));
            Assert.True(r.NotApplicable);
            Assert.Equal(SpecState.NotApplicable, State("B1.2", Compliant()).State);
        }

        [Fact]
        public void B1_2_gives_one_not_compliant_row_for_an_ST_model_with_its_unfilled_fields()
        {
            var m = new SpecModel { Discipline = "ST", Lod = 300, ProjectInfo = SpecPbData.ProjectInfoSt.ToDictionary(f => f.Name, f => "v") };
            Assert.Equal("pass", Assert.Single(Run("B1.2", m)).Status);
            m.ProjectInfo["Project Name"] = "Project Name";
            m.ProjectInfo["Kos_Projek_jkr_sic"] = "";
            var f = Assert.Single(Run("B1.2", m));
            Assert.Equal("fail", f.Status);
            Assert.StartsWith("ST: Tidak patuh — 2 field belum diisi:", f.Note);
            Assert.Contains("Project Name", f.Note);
            Assert.Contains("Kos_Projek_jkr_sic", f.Note);
            Assert.StartsWith("ST: Not compliant", f.NoteEn);
        }

        [Fact]
        public void B1_2_on_an_EL_model_is_fail_only_because_of_the_PB_gap()
        {
            var m = new SpecModel { Discipline = "EL", Lod = 300, ProjectInfo = SpecPbData.ProjectInfoEl.ToDictionary(f => f.Name, f => "v") };
            Assert.Equal(SpecCoverage.FailOnly, Assert.Single(Run("B1.2", m)).Coverage);
        }

        [Fact]
        public void B1_3_lists_the_share_of_mandatory_fields_filled()
        {
            var m = Compliant();
            m.Lod = 100;
            var total = SpecPbData.RequiredAt(SpecPbData.ProjectInfoAr, 100).Count;
            m.ProjectInfo["Building Name"] = "";
            var r = Assert.Single(Run("B1.3", m));
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
            var pct = (int)Math.Round(100.0 * (total - 1) / total, MidpointRounding.AwayFromZero);
            Assert.StartsWith((total - 1) + "/" + total + " field diisi (" + pct + "%)", r.Note);
            Assert.Equal(SpecState.Listed, State("B1.3", m).State);
            Assert.Empty(Run("B1.3", new SpecModel()));
        }

        // ══════════════ C1 — Views ══════════════

        [Fact]
        public void C1_0_1_is_a_parent_row_listing()
        {
            var r = Assert.Single(Run("C1.0.1", new SpecModel()));
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
            Assert.Contains("baris induk", r.Note);
        }

        [Fact]
        public void C1_1_1_is_unjudged_because_the_template_view_list_is_backend_data()
        {
            var m = Compliant();
            m.Views.RemoveAll(v => v.Kind == "CeilingPlan" && v.LevelName == "Aras 02");   // no longer a fail here
            m.Views.RemoveAll(v => v.Kind == "Section");
            var r = Assert.Single(Run("C1.1.1", m));
            Assert.Equal("cannot_verify", r.Status);
            Assert.StartsWith("Senarai view template JKR AR tiada dalam add-in (disimpan di backend)", r.Note);
            // the project's views by kind, so a person can compare
            Assert.Contains("floor plan (4): WIP Aras 01, WIP Aras 02, PBT Pelan Aras 01, BOMBA Pelan Aras 01", r.Note);
            Assert.Contains("ceiling plan (1): WIP Siling Aras 01", r.Note);
            Assert.Contains("3D (4):", r.Note);
            Assert.DoesNotContain("section (", r.Note);
            Assert.Contains("floor plan (4):", r.NoteEn);
            Assert.Contains("backend", SpecChecks.BackendOnly["C1.1.1"]);
            Assert.Equal(SpecState.NeedsPerson, State("C1.1.1", Compliant()).State);
        }

        [Fact]
        public void C1_1_1_without_views_is_unjudged()
        {
            var r = Assert.Single(Run("C1.1.1", new SpecModel()));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Equal("Tiada view dibaca daripada model", r.Note);
            var m = Compliant();
            m.ReadErrors.Add("Views: access denied");
            Assert.Contains("access denied", Assert.Single(Run("C1.1.1", m)).Note);
        }

        [Fact]
        public void C1_1_2_is_unjudged_and_lists_the_project_views_and_templates()
        {
            var r = Assert.Single(Run("C1.1.2", Compliant()));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("nama ditukar", r.Note);
            Assert.Contains("menunggu pengesahan JKR", r.Note);
            Assert.Contains("WIP Aras 01", r.Note);
            Assert.Contains("1 view template: jkrAR_Pelan", r.Note);
            Assert.Contains("renamed", r.NoteEn);
        }

        [Fact]
        public void C1_1_3_lists_the_levels_for_a_person()
        {
            var r = Assert.Single(Run("C1.1.3", Compliant()));
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.StartsWith("2 aras dikesan (Aras 01, Aras 02)", r.Note);
            Assert.Equal(SpecState.NeedsPerson, State("C1.1.3", Compliant()).State);
            Assert.False(SpecChecks.Rules.ContainsKey("C1.1.4"));
        }

        [Fact]
        public void Level_names_match_as_whole_words()
        {
            Assert.True(SpecBC.ContainsWord("3D Aras 1", "Aras 1"));
            Assert.True(SpecBC.ContainsWord("3D_aras 1_Dokumen", "Aras 1"));
            Assert.False(SpecBC.ContainsWord("3D Aras 10", "Aras 1"));
            Assert.False(SpecBC.ContainsWord("", "Aras 1"));
            Assert.False(SpecBC.ContainsWord("3D", " "));
        }

        [Fact]
        public void C1_2_1_lists_the_WIP_views_by_level_without_a_verdict()
        {
            var r = Run("C1.2.1", Compliant());
            Assert.All(r, x => Assert.Equal(SpecCoverage.Listing, x.Coverage));
            Assert.Equal("4 view WIP — untuk semakan manusia; set dikenal pasti ikut nama view 4", r[0].Note);
            Assert.Equal("4 WIP views — for a person to review; set found by name 4", r[0].NoteEn);
            Assert.Equal("Aras 01 | floor plan ✓ | ceiling plan ✓ | section – (set)", r[1].Note);
            Assert.Equal("Aras 02 | floor plan ✓ | ceiling plan ✓ | section – (set)", r[2].Note);
            Assert.StartsWith("Set WIP: 0 section, 0 elevation", r[3].Note);
            Assert.Contains(r, x => x.Note == "floor plan | Aras 01 | WIP Aras 01");
            Assert.Equal(SpecState.Listed, State("C1.2.1", Compliant()).State);
        }

        [Fact]
        public void C1_2_1_shows_a_level_without_WIP_views_and_orders_the_list_by_level()
        {
            var m = Compliant();
            m.Levels.Add(Level(3, "Aras 03", 8000));
            m.Levels.Add(Level(5, "Aras Bawah", -3000));
            m.Views.Add(View(60, "WIP Section A-A", "Section"));
            m.Views.Add(View(61, "WIP Pelan Aras Bawah", "FloorPlan", "Aras Bawah"));
            var r = Run("C1.2.1", m);
            Assert.DoesNotContain(r, x => x.Status != "pass" || x.Coverage != SpecCoverage.Listing);
            var rows = r.Select(x => x.Note).ToList();
            Assert.Equal("6 view WIP — untuk semakan manusia; set dikenal pasti ikut nama view 6", rows[0]);
            Assert.Equal("Aras Bawah | floor plan ✓ | ceiling plan – | section ✓ (set)", rows[1]);
            Assert.Equal("Aras 03 | floor plan – | ceiling plan – | section ✓ (set)", rows[4]);
            Assert.StartsWith("Set WIP: 1 section, 0 elevation", rows[5]);
            Assert.Equal(new[]
            {
                "floor plan | Aras Bawah | WIP Pelan Aras Bawah",
                "floor plan | Aras 01 | WIP Aras 01",
                "ceiling plan | Aras 01 | WIP Siling Aras 01",
                "floor plan | Aras 02 | WIP Aras 02",
                "ceiling plan | Aras 02 | WIP Siling Aras 02",
                "section | — | WIP Section A-A",
            }, rows.Skip(6).ToList());
        }

        [Fact]
        public void C1_3_1_PBT_needs_floor_plan_elevation_and_section()
        {
            var pass = Assert.Single(Run("C1.3.1", Compliant()));
            Assert.Equal("Set PBT: floor plan ✓ | elevation ✓ | section ✓ (3 view; set dikenal pasti ikut nama view 3)", pass.Note);
            Assert.Equal(SpecState.Pass, State("C1.3.1", Compliant()).State);
            var m = Compliant();
            m.Views.RemoveAll(v => v.Name == "PBT Section A-A");
            var f = Assert.Single(Fails(Run("C1.3.1", m)));
            Assert.Equal("Set PBT: tiada section (floor plan ✓ | elevation ✓ | section ✗); set dikenal pasti ikut nama view 2", f.Note);
        }

        [Fact]
        public void C1_4_1_BOMBA_needs_floor_plan_section_and_drafting_view()
        {
            Assert.Equal("pass", Assert.Single(Run("C1.4.1", Compliant())).Status);
            var m = Compliant();
            m.Views.RemoveAll(v => v.Name.StartsWith("BOMBA"));
            Assert.Equal(3, Fails(Run("C1.4.1", m)).Count);
        }

        [Fact]
        public void C1_5_1_lists_Dokumen_views_with_sheet_status_only()
        {
            var r = Run("C1.5.1", Compliant());
            Assert.All(r, x => Assert.Equal(SpecCoverage.Listing, x.Coverage));
            Assert.Equal("2 view Dokumen, 2 dalam sheet (biru); set dikenal pasti ikut nama view 2", r[0].Note);
            Assert.Contains(r, x => x.Note == "Dokumen 3D Aras 01 | 3D | — | dalam sheet ✓ (A-301)");
            Assert.DoesNotContain(r, x => x.Note.StartsWith("3D view"));
            Assert.Equal(3, r.Count);
        }

        [Fact]
        public void C1_5_2_fails_each_level_without_a_Dokumen_3D_view()
        {
            var m = Compliant();
            m.Levels.Add(Level(3, "Aras 03", 8000));
            m.Levels.Add(Level(4, "Aras 04", 12000));
            m.Views.Add(View(90, "Dokumen 3D - Aras 04", "ThreeD"));
            m.Views.Add(View(91, "Dokumen 3D Aras 030", "ThreeD"));                    // not Aras 03
            m.Views.Add(View(92, "Dokumen Pelan Aras 03", "FloorPlan", "Aras 03"));    // not 3D
            var f = Fails(Run("C1.5.2", m)).Single();
            Assert.Equal("3D view setiap aras: 3/4 ✗ (Aras 03 tiada)", f.Note);
            Assert.Equal("3D view for every level: 3/4 ✗ (Aras 03 missing)", f.NoteEn);
            Assert.Equal(3, f.ElementId);
            Assert.Equal(SpecCoverage.Full, f.Coverage);

            var pass = Assert.Single(Run("C1.5.2", Compliant()));
            Assert.Equal("pass", pass.Status);
            Assert.Equal(SpecCoverage.Full, pass.Coverage);
            Assert.Equal("3D view setiap aras: 2/2 ✓ (set Dokumen)", pass.Note);
            Assert.Equal(SpecState.Pass, State("C1.5.2", Compliant()).State);
        }

        [Fact]
        public void C1_5_2_a_3D_view_outside_the_Dokumen_set_does_not_count_and_is_named()
        {
            var m = Compliant();
            m.Views.RemoveAll(v => v.Name == "Dokumen 3D Aras 02");
            var f = Fails(Run("C1.5.2", m)).Single();
            Assert.Equal("3D view setiap aras: 1/2 ✗ (Aras 02 tiada) — ada 3D view '3D Aras 02' di luar set Dokumen (folder, template dan nama tiada 'Dokumen'), tidak dikira", f.Note);
            Assert.Contains("'3D Aras 02' exists outside the Dokumen set", f.NoteEn);
        }

        [Fact]
        public void C1_5_2_with_no_Dokumen_3D_views_fails_every_level()
        {
            var m = Compliant();
            m.Views.RemoveAll(v => v.Kind == "ThreeD");
            var fails = Fails(Run("C1.5.2", m));
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, x => x.Note == "3D view setiap aras: 0/2 ✗ (Aras 01 tiada) — tiada 3D view dalam set Dokumen");
            Assert.Contains(fails, x => x.Note == "3D view setiap aras: 0/2 ✗ (Aras 02 tiada) — tiada 3D view dalam set Dokumen");
        }

        [Fact]
        public void C1_5_2_without_levels_or_views_is_unjudged()
        {
            Assert.Equal("cannot_verify", Assert.Single(Run("C1.5.2", new SpecModel())).Status);
            var m = Compliant();
            m.Levels.Clear();
            var r = Assert.Single(Run("C1.5.2", m));
            Assert.Equal("cannot_verify", r.Status);
            Assert.Contains("Tiada aras", r.Note);
        }

        // ─── C1.6 room schedule ───

        [Fact]
        public void C1_6_1_fails_without_a_room_schedule_and_offers_no_paid_action()
        {
            Assert.Equal("pass", Assert.Single(Run("C1.6.1", Compliant())).Status);
            var m = Compliant();
            m.Schedules.RemoveAll(s => s.Category == "Rooms");
            var f = Assert.Single(Run("C1.6.1", m));
            Assert.Equal("fail", f.Status);
            Assert.Equal("Tiada jadual ruang (room schedule)", f.Note);
            Assert.Equal("No room schedule", f.NoteEn);
            Assert.DoesNotContain("berbayar", f.Note);
        }

        [Fact]
        public void C1_6_2_fails_each_placed_room_not_in_the_schedule()
        {
            var m = Compliant();
            m.Rooms.Add(new SpecRoom { Id = 102, Name = "Stor", Number = "003", LevelName = "Aras 01", Area = 5 });
            m.Rooms.Add(new SpecRoom { Id = 103, Name = "Tidak diletak", Area = 0 });
            var f = Assert.Single(Fails(Run("C1.6.2", m)));
            Assert.Equal(102, f.ElementId);
            Assert.Equal("Room 003 Stor (Aras 01) tiada dalam jadual ruang", f.Note);
            Assert.Equal("cannot_verify", Assert.Single(Run("C1.6.2", new SpecModel())).Status);
        }

        [Fact]
        public void C1_6_3_fails_each_room_with_empty_columns()
        {
            var m = Compliant();
            m.Rooms[0].Params["Floor Finish"] = "";
            m.Rooms[0].Params["Wall Finish"] = " ";
            var f = Assert.Single(Fails(Run("C1.6.3", m)));
            Assert.Equal("001 Bilik Mesyuarat 1 | Wall Finish, Floor Finish", f.Note);
            Assert.Equal(100, f.ElementId);
            Assert.Equal(SpecCoverage.Full, f.Coverage);
            Assert.Equal(SpecState.Pass, State("C1.6.3", Compliant()).State);
        }

        [Fact]
        public void C1_6_3_flags_PB_columns_the_schedule_lacks_and_rooms_without_them()
        {
            var m = Compliant();
            var s = m.Schedules.Single(x => x.Category == "Rooms");
            s.Fields = new List<string> { "Number", "Name", "Area" };
            m.Rooms[1].Params.Remove("Department");
            var fails = Fails(Run("C1.6.3", m));
            Assert.Contains(fails, f => f.Note.StartsWith("Jadual 'Jadual Ruang' tiada kolum wajib LOi 300 (PB p.37):") && f.Note.Contains("Floor Finish"));
            Assert.Contains(fails, f => f.ElementId == 101 && f.Note.Contains("Department"));
        }

        [Fact]
        public void C1_6_3_with_unknown_LOD_checks_only_the_schedule_fields_as_fail_only()
        {
            var m = Compliant();
            m.Lod = 0;
            var pass = Assert.Single(Run("C1.6.3", m));
            Assert.Equal(SpecCoverage.FailOnly, pass.Coverage);
            Assert.Equal(SpecState.Partial, State("C1.6.3", m).State);
        }

        // ══════════════ C2 / C3 ══════════════

        [Fact]
        public void C2_1_1_passes_with_a_legend_and_lists_its_sheet_status()
        {
            var r = Assert.Single(Run("C2.1.1", Compliant()));
            Assert.Equal("pass", r.Status);
            Assert.Contains("Legend Pintu | legend | dalam lukisan ✓", r.Note);
            var m = Compliant();
            m.Views.RemoveAll(v => v.Kind == "Legend");
            Assert.Equal("Tiada legend dalam model", Assert.Single(Fails(Run("C2.1.1", m))).Note);
        }

        [Fact]
        public void C3_1_1_fails_each_modelled_PB_category_without_a_schedule()
        {
            var pass = Assert.Single(Run("C3.1.1", Compliant()));
            Assert.Equal("2 kategori PB dimodelkan, semua ada schedule", pass.Note);
            var m = Compliant();
            m.CategoryCounts["Railings"] = 5;
            m.CategoryCounts["Ramps"] = 0;
            m.CategoryCounts["Dimensions"] = 99;
            var f = Assert.Single(Fails(Run("C3.1.1", m)));
            Assert.Equal("Railing: 5 elemen, tiada schedule", f.Note);
            Assert.Equal("Railing: 5 elements, no schedule", f.NoteEn);
            Assert.Equal("cannot_verify", Assert.Single(Run("C3.1.1", new SpecModel())).Status);
        }

        [Fact]
        public void C3_1_1_ignores_schedule_templates()
        {
            var m = Compliant();
            m.Schedules.Single(s => s.Category == "Doors").IsTemplate = true;
            Assert.Equal("Door: 4 elemen, tiada schedule", Assert.Single(Fails(Run("C3.1.1", m))).Note);
        }

        [Fact]
        public void C3_1_1_ramps_listed_by_a_multi_category_schedule_or_takeoff_count_as_scheduled()
        {
            // Real model, 16 Nov 2026: 3 ramps scheduled only in multi-category schedules / takeoffs.
            var m = Compliant();
            m.CategoryCounts["Ramps"] = 3;
            m.Schedules.Add(new SpecSchedule { Id = 210, Name = "jkrAR_mto_mc_Multi-Category Material Takeoff", Category = "",
                IsMaterialTakeoff = true, RowElementIds = new List<long> { 1060438, 904233 }, RowCategories = new List<string> { "Walls", "Ramps" } });
            m.Schedules.Add(new SpecSchedule { Id = 211, Name = "jkrAR_sch_gen-mo_Jarak Perjalanan (Bomba)", Category = "",
                RowCategories = new List<string> { "Ramps", "Doors" } });
            var pass = Assert.Single(Run("C3.1.1", m));
            Assert.Equal("pass", pass.Status);
            Assert.Equal("3 kategori PB dimodelkan, semua ada schedule (Ramp dalam material takeoff 'jkrAR_mto_mc_Multi-Category Material Takeoff', "
                         + "schedule pelbagai kategori 'jkrAR_sch_gen-mo_Jarak Perjalanan (Bomba)')", pass.Note);
            Assert.Contains("Ramp in material takeoff", pass.NoteEn);
        }

        [Fact]
        public void C3_1_1_a_schedule_without_row_categories_is_matched_by_its_row_ids()
        {
            var m = Compliant();
            m.CategoryCounts["Ramps"] = 1;
            m.Types.Add(new SpecType { TypeId = 5, Category = "Ramps", TypeName = "Ramp 1/12", InstanceCount = 1, InstanceIds = new List<long> { 904233 } });
            m.Schedules.Add(new SpecSchedule { Id = 212, Name = "MTO (Dinding)", IsMaterialTakeoff = true, RowElementIds = new List<long> { 904233 } });
            Assert.Equal("pass", Assert.Single(Run("C3.1.1", m)).Status);

            // A takeoff listing walls only does not schedule the ramp.
            m.Schedules.Single(s => s.Id == 212).RowElementIds = new List<long> { 777 };
            Assert.Equal("Ramp: 1 elemen, tiada schedule", Assert.Single(Fails(Run("C3.1.1", m))).Note);
        }

        [Fact]
        public void C3_1_1_with_other_fails_still_says_which_category_a_takeoff_covers()
        {
            var m = Compliant();
            m.CategoryCounts["Ramps"] = 3;
            m.CategoryCounts["Railings"] = 5;
            m.Schedules.Add(new SpecSchedule { Id = 210, Name = "MTO mc", IsMaterialTakeoff = true, RowCategories = new List<string> { "Ramps" } });
            var r = Run("C3.1.1", m);
            Assert.Equal("Railing: 5 elemen, tiada schedule", Assert.Single(Fails(r)).Note);
            var listed = r.Single(x => x.Coverage == SpecCoverage.Listing);
            Assert.Contains("Ramp dalam material takeoff 'MTO mc'", listed.Note);
        }

        // ─── which set a view belongs to (SpecViewSets) ───

        private const string FolderBy = "View_jkr_pit / Jenis_Lukisan_jkr_pit / Family";

        private static SpecView InFolder(long id, string name, string kind, string folder, string template = "", string level = "") =>
            new SpecView { Id = id, Name = name, Kind = kind, LevelName = level, TemplateName = template,
                FolderPath = folder, FolderBy = FolderBy, SetParam = folder.Split(new[] { " / " }, StringSplitOptions.None)[0].Replace("???", "") };

        [Fact]
        public void The_browser_folder_beats_the_view_name()
        {
            var v = InFolder(1, "PTBR_PELAN WIP Aras 01", "FloorPlan", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", "jkrAR_01w_WIP_(Pelan Aras 1)");
            var hit = SpecViewSets.Of(v);
            Assert.Equal(new[] { "Dokumen" }, hit.Sets);
            Assert.Equal(ViewSetSource.Folder, hit.Source);
            Assert.Equal("View_jkr_pit", hit.By);
            Assert.False(hit.In("WIP"));

            var m = new SpecModel { Views = { v } };
            Assert.Empty(SpecViewSets.Views(m, "WIP"));
            Assert.Single(SpecViewSets.Views(m, "Dokumen"));
        }

        [Theory]
        [InlineData("01w_WIP", "WIP")]
        [InlineData("05b_Bomba", "BOMBA")]
        [InlineData("06p_PBT", "PBT")]
        [InlineData("07d_Dokumen", "Dokumen")]
        public void Set_folders_of_the_JKR_template_are_read(string folder, string set)
        {
            var hit = SpecViewSets.Of(InFolder(1, "View", "FloorPlan", folder + " / 01_Lukisan Utama / Floor Plans"));
            Assert.Equal(new[] { set }, hit.Sets);
        }

        [Fact]
        public void A_View_jkr_pit_folder_of_another_set_decides_no_set_and_the_name_is_not_read()
        {
            var v = InFolder(1, "WIP Area Plan", "AreaPlan", "03p_Persembahan / 01_Lukisan Utama / Area Plans");
            var hit = SpecViewSets.Of(v);
            Assert.Empty(hit.Sets);
            Assert.Equal(ViewSetSource.Folder, hit.Source);
            // The parameter alone (no folders read) decides the same.
            v.FolderPath = ""; v.FolderBy = "";
            hit = SpecViewSets.Of(v);
            Assert.Empty(hit.Sets);
            Assert.Equal(ViewSetSource.Parameter, hit.Source);
        }

        [Fact]
        public void Without_a_folder_the_template_name_decides_then_the_view_name()
        {
            var t = SpecViewSets.Of(new SpecView { Name = "PTBR_ISOMETRIK ARAS 1", Kind = "ThreeD", TemplateName = "jkrAR_07d_Dokumen_(3D)" });
            Assert.Equal(new[] { "Dokumen" }, t.Sets);
            Assert.Equal(ViewSetSource.Template, t.Source);

            // A blank folder ("???") is no folder: the legend falls back to its name.
            var n = SpecViewSets.Of(InFolder(2, "jkrAR_lgd_Kehendak Bomba", "Legend", "??? / ??? / Legends"));
            Assert.Equal(new[] { "BOMBA" }, n.Sets);
            Assert.Equal(ViewSetSource.Name, n.Source);

            var none = SpecViewSets.Of(new SpecView { Name = "Wipe test", Kind = "FloorPlan", TemplateName = "Architectural Plan" });
            Assert.Empty(none.Sets);
            Assert.Equal(ViewSetSource.None, none.Source);
        }

        [Fact]
        public void C1_2_1_and_C1_5_1_say_the_set_was_found_by_browser_folder()
        {
            var m = Compliant();
            m.Views.Add(InFolder(70, "PTBR_PELAN LANTAI ARAS 01", "FloorPlan", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", "jkrAR_07d_Dokumen_(Pelan Aras 1)", "Aras 01"));
            m.Views.Add(InFolder(71, "PTBR_PELAN LANTAI ARAS 01 Copy 1", "FloorPlan", "01w_WIP / 01_Lukisan Utama / Floor Plans", "jkrAR_01w_WIP_(Pelan Aras 1)", "Aras 01"));
            Assert.Equal("5 view WIP — untuk semakan manusia; set dikenal pasti ikut nama view 4, ikut folder Project Browser (View_jkr_pit) 1",
                Run("C1.2.1", m)[0].Note);
            var dok = Run("C1.5.1", m)[0];
            Assert.Equal("3 view Dokumen, 2 dalam sheet (biru); set dikenal pasti ikut nama view 2, ikut folder Project Browser (View_jkr_pit) 1", dok.Note);
            Assert.Equal("3 Dokumen views, 2 on a sheet (blue); set found by name 2, by browser folder View_jkr_pit 1", dok.NoteEn);
        }

        [Fact]
        public void C3_1_2_lists_the_PB_categories_not_modelled_as_na()
        {
            var r = Assert.Single(Run("C3.1.2", Compliant()));
            Assert.Equal(SpecCoverage.Listing, r.Coverage);
            Assert.StartsWith("n/a (tidak dimodelkan, bukan kekurangan): Area, Casework", r.Note);
            Assert.DoesNotContain("Door", r.Note);
            Assert.DoesNotContain("Lines", r.Note);
        }

        // ══════════════ C4 — Sheets ══════════════

        [Fact]
        public void C4_1_1_lists_every_sheet()
        {
            var r = Run("C4.1.1", Compliant());
            Assert.Equal(4, r.Count);
            Assert.Contains(r, x => x.Note == "A-101 | Pelan Aras | 2 view | floor plan, ceiling plan");
        }

        [Fact]
        public void C4_1_2_needs_every_kind_across_the_sheets()
        {
            var pass = Assert.Single(Run("C4.1.2", Compliant()));
            Assert.Equal("floor plan ✓ | ceiling plan ✓ | elevation ✓ | section ✓ | detail pintu ✓ | detail tingkap ✓ (3 sheet)", pass.Note);
            var m = Compliant();
            m.Sheets.RemoveAll(s => s.Number == "A-501");
            var fails = Fails(Run("C4.1.2", m));
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note.StartsWith("Tiada detail pintu dalam mana-mana sheet"));
            Assert.Contains(fails, f => f.NoteEn.StartsWith("No window detail on any sheet"));
        }

        [Fact]
        public void C4_1_2_a_drafting_view_named_door_counts_as_a_door_detail()
        {
            var m = Compliant();
            m.Sheets.RemoveAll(s => s.Number == "A-501");
            m.Views.Add(View(95, "Door Details", "DraftingView", "", true, "A-502"));
            m.Views.Add(View(96, "Tingkap - Butiran", "Detail", "", true, "A-502"));
            m.Views.Add(View(97, "Window plan", "FloorPlan", "Aras 01", true, "A-502"));
            Assert.Empty(Fails(Run("C4.1.2", m)));
        }

        [Fact]
        public void C4_1_2_a_sheet_named_door_details_holding_the_door_legend_counts()
        {
            // Real Dewan model, 16 Nov: sheets "bPT02 Butiran Pintu" / "bTK01 Butiran Tingkap"
            // carry the door / window legends; no detail view is named pintu / tingkap.
            var m = Compliant();
            m.Sheets.RemoveAll(s => s.Number == "A-501");
            m.Sheets.Add(new SpecSheet { Id = 401, Number = "bPT02", Name = "Butiran Pintu", TitleBlock = "JKR A1",
                ViewKinds = { "Legend" }, ViewNames = { "jkrAR_lgd_Petunjuk Pintu 02" } });
            m.Sheets.Add(new SpecSheet { Id = 402, Number = "bTK01", Name = "Butiran Tingkap", TitleBlock = "JKR A1",
                ViewKinds = { "Legend" }, ViewNames = { "jkrAR_lgd_Petunjuk Tingkap" } });
            Assert.Empty(Fails(Run("C4.1.2", m)));

            // An empty sheet with that name proves nothing.
            var empty = Compliant();
            empty.Sheets.RemoveAll(s => s.Number == "A-501");
            empty.Sheets.Add(new SpecSheet { Id = 403, Number = "bPT02", Name = "Butiran Pintu", TitleBlock = "JKR A1" });
            Assert.Contains(Fails(Run("C4.1.2", empty)), f => f.NoteEn.StartsWith("No door detail"));
        }

        [Fact]
        public void C4_1_3_fails_each_empty_sheet()
        {
            var m = Compliant();
            m.Sheets.Add(new SpecSheet { Id = 309, Number = "A-901", Name = "Kosong", TitleBlock = "JKR A1" });
            var f = Assert.Single(Fails(Run("C4.1.3", m)));
            Assert.Equal("Sheet A-901 Kosong tiada view", f.Note);
            Assert.Equal("Sheet A-901 Kosong has no view", f.NoteEn);
            Assert.Equal(309, f.ElementId);
            Assert.Equal("cannot_verify", Assert.Single(Run("C4.1.3", new SpecModel())).Status);
        }

        [Fact]
        public void C4_2_1_fails_a_non_JKR_or_missing_title_block()
        {
            Assert.Equal("pass", Assert.Single(Run("C4.2.1", Compliant())).Status);
            var m = Compliant();
            m.Sheets[0].TitleBlock = "A1 metric : A1";
            m.Sheets[1].TitleBlock = "";
            var fails = Fails(Run("C4.2.1", m));
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note == "Sheet A-101 Pelan Aras: title block 'A1 metric : A1' bukan JKR");
            Assert.Contains(fails, f => f.Note == "Sheet A-201 Elevation & Section: tiada title block");
        }

        [Fact]
        public void C4_2_2_fails_each_sheet_and_empty_title_block_field()
        {
            Assert.Equal("pass", Assert.Single(Run("C4.2.2", Compliant())).Status);
            var m = Compliant();
            m.Sheets[0].Params["Drawn By"] = "Author";
            m.Sheets[0].Params["Sheet Issue Date"] = "";
            m.Sheets[1].Params.Remove("Project Name");      // falls back to Project Information
            var fails = Fails(Run("C4.2.2", m));
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note == "Sheet A-101 Pelan Aras: dimodelkan oleh (Drawn By) nilai lalai Revit");
            Assert.Contains(fails, f => f.Note == "Sheet A-101 Pelan Aras: timestamp (Sheet Issue Date) kosong");
            Assert.Equal(2, fails.Select(f => f.IssueId).Distinct().Count());
        }

        [Fact]
        public void C4_2_2_a_sheet_whose_parameters_were_not_read_is_unjudged()
        {
            var m = Compliant();
            m.Sheets[2].Params.Clear();
            var r = Run("C4.2.2", m);
            Assert.Contains(r, x => x.Status == "cannot_verify" && x.ElementId == 302);
            Assert.Equal(SpecState.NeedsPerson, State("C4.2.2", m).State);
        }

        [Fact]
        public void C4_2_3_shows_the_softcopy_timestamps_for_a_person()
        {
            var r = Assert.Single(Run("C4.2.3", Compliant()));
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.Contains("A-101 = 16.11.2026", r.Note);
            Assert.Contains("SEMAK MANUAL", r.Note);
        }

        // ══════════════ C5 — Links ══════════════

        [Fact]
        public void C5_1_1_needs_ST_ME_and_EL_linked_and_loaded()
        {
            Assert.Equal("pass", Assert.Single(Run("C5.1.1", Compliant())).Status);
            var m = Compliant();
            m.Links.RemoveAll(l => l.Name.StartsWith("jkrEL"));
            m.Links.Single(l => l.Name.StartsWith("jkrME")).Status = "Unloaded";
            var fails = Fails(Run("C5.1.1", m));
            Assert.Equal(2, fails.Count);
            Assert.Contains(fails, f => f.Note == "KRITIKAL: tiada link EL" && f.NoteEn == "CRITICAL: no EL link");
            Assert.Contains(fails, f => f.Note.StartsWith("KRITIKAL: link ME 'jkrME24") && f.Note.EndsWith("Unloaded"));
            Assert.Equal(3, Fails(Run("C5.1.1", new SpecModel())).Count);
        }

        [Theory]
        [InlineData("jkrST24_5a_x.rvt", "ST", true)]
        [InlineData("Projek_ST_Blok A.rvt", "ST", true)]
        [InlineData("ME24-Plumbing.rvt", "ME", true)]
        [InlineData("Stair study.rvt", "ST", false)]
        [InlineData("Home_me.rvt", "ME", false)]
        [InlineData("EL.rvt", "EL", true)]
        [InlineData("", "EL", false)]
        public void Link_discipline_is_read_from_the_file_name(string name, string d, bool expected) =>
            Assert.Equal(expected, SpecBC.LinkIs(new SpecLink { Name = name }, d));

        [Fact]
        public void C5_1_1_does_not_ask_a_model_to_link_its_own_discipline()
        {
            var m = Compliant();
            m.Discipline = "ST";
            m.Links.RemoveAll(l => l.Name.StartsWith("jkrST"));
            Assert.Equal("pass", Assert.Single(Run("C5.1.1", m)).Status);
        }

        [Fact]
        public void C5_1_2_is_unjudged_without_a_BPEP()
        {
            var r = Assert.Single(Run("C5.1.2", Compliant()));
            Assert.Equal("cannot_verify", r.Status);
            Assert.StartsWith("BPEP tidak tersedia → tidak dapat disemak", r.Note);
        }

        [Fact]
        public void C5_1_3_is_a_manual_remark()
        {
            var r = Assert.Single(Run("C5.1.3", Compliant()));
            Assert.Equal(SpecCoverage.Context, r.Coverage);
            Assert.StartsWith("Kena semak versi terkini secara manual", r.Note);
        }

        [Fact]
        public void Read_errors_make_checks_unjudged_not_passed()
        {
            var m = Compliant();
            m.ReadErrors.Add("Sheets: InvalidOperationException");
            m.ReadErrors.Add("Links: boom");
            m.ReadErrors.Add("Schedules: boom");
            foreach (var id in new[] { "C4.1.2", "C4.1.3", "C4.2.1", "C4.2.2", "C5.1.1", "C1.6.1", "C3.1.1" })
                Assert.Equal("cannot_verify", Assert.Single(Run(id, m)).Status);
            Assert.Empty(Run("C4.1.1", m));
        }
    }
}
