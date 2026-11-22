using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Regressions from the first real-model run (MrBiggie, jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01,
    /// 16 Nov 2026), where the pane's claims were checked against the model.
    /// </summary>
    public class SpecRealModelFixTests
    {
        private static List<SpecResult> Of(SpecModel m, string id) =>
            SpecChecks.Run(m).Where(r => r.CheckId == id).ToList();

        [Theory]
        [InlineData("PTBR_Aras 01", "aras 1")]
        [InlineData("PTBR_ISOMETRIK ARAS 1", "aras 1")]
        [InlineData("Level 02 +4.500", "aras 2")]
        [InlineData("L03", "aras 3")]
        [InlineData("Aras Tanah", "aras tanah")]
        [InlineData("PTBR_Tangki Air", null)]
        public void Level_keys_match_level_and_view_names(string name, string key)
        {
            Assert.Equal(key, SpecBC.LevelKey(name));
        }

        // The real model named its 3D views "PTBR_ISOMETRIK ARAS 1" (no "Dokumen"); C1.5.2 counts
        // only the Dokumen set, so the level matching is tested with Dokumen-named views.
        [Fact]
        public void A_Dokumen_3D_view_named_with_the_level_number_counts_for_that_level()
        {
            var m = new SpecModel
            {
                Levels =
                {
                    new SpecLevel { Name = "PTBR_Aras 01", Elevation = 0 },
                    new SpecLevel { Name = "PTBR_Aras 02", Elevation = 4000 },
                    new SpecLevel { Name = "PTBR_Tangki Air", Elevation = 8000 },
                },
                Views =
                {
                    new SpecView { Id = 1, Name = "PTBR_Dokumen_ISOMETRIK ARAS 1", Kind = "ThreeD" },
                    new SpecView { Id = 2, Name = "PTBR_Dokumen_ISOMETRIK ARAS TANGKI AIR", Kind = "ThreeD" },
                },
            };
            var fails = Of(m, "C1.5.2").Where(r => r.Status == "fail").ToList();
            Assert.Single(fails);                                   // only Aras 02 is missing
            Assert.Contains("PTBR_Aras 02", fails[0].Note);
        }

        [Fact]
        public void The_real_model_3D_views_without_Dokumen_in_the_name_fail_C1_5_2_and_are_named()
        {
            var m = new SpecModel
            {
                Levels = { new SpecLevel { Name = "PTBR_Aras 01", Elevation = 0 } },
                Views = { new SpecView { Id = 1, Name = "PTBR_ISOMETRIK ARAS 1", Kind = "ThreeD" } },
            };
            var fail = Of(m, "C1.5.2").Single(r => r.Status == "fail");
            Assert.Contains("PTBR_Aras 01 tiada", fail.Note);
            Assert.Contains("'PTBR_ISOMETRIK ARAS 1' di luar set Dokumen", fail.Note);
        }

        [Fact]
        public void A_floor_on_a_beam_level_counts_the_room_of_the_story_level_beside_it()
        {
            var m = new SpecModel
            {
                Levels =
                {
                    new SpecLevel { Name = "PTBR_Aras 01", Elevation = 0, IsStory = true },
                    new SpecLevel { Name = "PTBR_Beam Aras 01", Elevation = -150, IsStory = false },
                },
                Floors = { new SpecFloor { Id = 893449, TypeName = "jkrAR_LT_RC_150", LevelName = "PTBR_Beam Aras 01", MinX = 0, MinY = 0, MaxX = 5000, MaxY = 5000 } },
                Rooms = { new SpecRoom { Id = 7, Name = "TANDAS GUNASAMA", Number = "PTBR_00_28", LevelName = "PTBR_Aras 01", Area = 12, X = 2500, Y = 2500 } },
            };
            Assert.DoesNotContain(Of(m, "A6.2"), r => r.Status == "fail");
        }

        [Fact]
        public void A_ceiling_on_a_beam_level_is_measured_from_the_floor_and_the_lowest_one_is_located()
        {
            var m = new SpecModel
            {
                Levels =
                {
                    new SpecLevel { Name = "PTBR_Aras 01", Elevation = 0, IsStory = true },
                    new SpecLevel { Name = "PTBR_Beam Aras 01", Elevation = -50, IsStory = false },
                },
                Ceilings =
                {
                    new SpecCeiling { Id = 1103317, LevelName = "PTBR_Aras 01", HeightOffset = 2600 },
                    new SpecCeiling { Id = 3088853, LevelName = "PTBR_Aras 01", HeightOffset = 2500 },
                    new SpecCeiling { Id = 900001, LevelName = "PTBR_Beam Aras 01", HeightOffset = 2550 },
                },
            };
            var fail = Of(m, "A1.1").Single(r => r.Status == "fail");
            Assert.Equal(3088853, fail.ElementId);                  // the 2500 one, not the 2600 one
            Assert.Contains("2500", fail.Note);
            Assert.Contains("PTBR_Aras 01", fail.ElementName);      // the beam-level ceiling joined its storey
            Assert.Equal(3, fail.ElementIds.Count);
        }

        // ═════ Round 2 (16 Nov 2026): the view sets as the real model organises them ═════
        // The Project Browser organisation "00a_All" groups views by View_jkr_pit, then
        // Jenis_Lukisan_jkr_pit; view templates are named jkrAR_01w_WIP_(…), jkrAR_07d_Dokumen_(…).
        // Every view below is the model's own (model-evidence-r2.txt): kind, name, level,
        // template, browser folders, on a sheet.

        private const string RealFolderBy = "View_jkr_pit / Jenis_Lukisan_jkr_pit / Family";

        private static SpecView V(string kind, string name, string level, string template, string folder, bool onSheet)
        {
            var top = folder.Split(new[] { " / " }, System.StringSplitOptions.None)[0];
            return new SpecView
            {
                Name = name, Kind = kind, LevelName = level, TemplateName = template, OnSheet = onSheet,
                FolderPath = folder, FolderBy = RealFolderBy, SetParam = top == "???" ? "" : top,
            };
        }

        private static List<SpecView> RealViews()
        {
            var views = new List<SpecView>
            {
            V("AreaPlan", "PTBR_Aras 01", "PTBR_Aras 01", "", "03p_Persembahan / 01_Lukisan Utama / Area Plans", true),
            V("AreaPlan", "PTBR_Aras 01 (Bilik Mesyuarat Besar)", "PTBR_Aras 01", "", "03p_Persembahan / 01_Lukisan Utama / Area Plans", false),
            V("AreaPlan", "PTBR_Aras 01 (Kafeteria)", "PTBR_Aras 01", "", "03p_Persembahan / 01_Lukisan Utama / Area Plans", false),
            V("AreaPlan", "PTBR_Aras 01 (Pusat Kaunseling)", "PTBR_Aras 01", "", "03p_Persembahan / 01_Lukisan Utama / Area Plans", false),
            V("AreaPlan", "PTBR_Aras 02", "PTBR_Aras 02", "", "03p_Persembahan / 01_Lukisan Utama / Area Plans", true),
            V("AreaPlan", "PTBR_Aras Tangki Air", "PTBR_Aras Tangki Air", "jkrAR_03p_Persembahan (Pelan Aras 1)", "03p_Persembahan / 01_Lukisan Utama / Area Plans", true),
            V("CeilingPlan", "PTBR_Beam Aras 01", "PTBR_Beam Aras 01", "", "??? / ??? / Ceiling Plans", false),
            V("CeilingPlan", "PTBR_Beam Aras 02", "PTBR_Beam Aras 02", "", "??? / ??? / Ceiling Plans", false),
            V("CeilingPlan", "PTBR_REFLECTED CEILING PLAN ARAS 01", "PTBR_Aras 01", "jkrAR_07d_Dokumen_(Pelan Siling)", "07d_Dokumen / 01_Lukisan Utama / Ceiling Plans", true),
            V("CeilingPlan", "PTBR_REFLECTED CEILING PLAN ARAS 01 Copy 1", "PTBR_Aras 01", "jkrAR_01w_WIP_(Pelan Siling)", "01w_WIP / 01_Lukisan Utama / Ceiling Plans", false),
            V("CeilingPlan", "PTBR_REFLECTED CEILING PLAN ARAS 02", "PTBR_Aras 02", "jkrAR_07d_Dokumen_(Pelan Siling)", "07d_Dokumen / 01_Lukisan Utama / Ceiling Plans", true),
            V("CeilingPlan", "PTBR_REFLECTED CEILING PLAN ARAS 02 Copy 1", "PTBR_Aras 02", "jkrAR_01w_WIP_(Pelan Siling)", "01w_WIP / 01_Lukisan Utama / Ceiling Plans", false),
            V("Detail", "DETAIL 01", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL 02", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL 03", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL 04", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL 04 T", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL 05", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL 05 T", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL A", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "DETAIL A PANTRI", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "Detail 1", "", "", "07d_Dokumen / 02_Butiran / Detail Views", false),
            V("Detail", "FRONT DROP-OFF", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN A-A", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN A-A Copy 1", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN A-A DROP", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN B-B", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN B-B DROP", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN C-C", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN C-C Copy 1", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN C-C DROP", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN D-D", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN D-D STEP", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN E-E", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN F-F", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN H-H", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN J-J", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN X-X", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "KERATAN X-X ROOF DETAIL", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "PANTRI", "", "jkrAR_07d_Dokumen_(Pelan Data Ruang)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "PELAN LANTAI ARAS BAWAH", "", "jkrAR_07d_Dokumen_(Pelan Data Ruang)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "PELAN LANTAI ARAS BAWAH VIP", "", "jkrAR_07d_Dokumen_(Pelan Data Ruang)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "REAR SERVICE RAMP", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "REAR STEPS", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "RISER 3 BAWAH", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "RISER 3 BAWAH 1", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "SIDE STEPS", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "STEPS TO PARKING LOT", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TANDAS STAF 2 (L)", "", "jkrAR_07d_Dokumen_(Pelan Data Ruang)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TANDAS STAF 2 (P)", "", "jkrAR_07d_Dokumen_(Pelan Data Ruang)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TANGGA 1 & TYPICAL RISER BAWAH", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TANGGA 1 & TYPICAL RISER SATU", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TANGGA 2 & TYPICAL RISER 2 BAWAH", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TANGGA 2 & TYPICAL RISER 2 SATU", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "TYPICAL GUTTER (MAIN ROOF)", "", "", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("Detail", "VIEW 1 PANTRI", "", "jkrAR_07d_Dokumen_(Butiran Keratan)", "07d_Dokumen / 02_Butiran / Detail Views", true),
            V("DraftingView", "01 Butiran Kemaskini (01w_WIP)", "", "", "01w_WIP / 02_Butiran / Drafting Views", false),
            V("DraftingView", "Petunjuk Bomba", "", "", "05b_Bomba / 02_Butiran / Drafting Views", false),
            V("Elevation", "ABC01 Barat (07d_Dokumen - Data Ruang)", "", "jkrAR_07d_Dokumen_(Pandangan)", "07d_Dokumen / 03_Data Ruang / Elevations", false),
            V("Elevation", "ABC02 Barat (07d_Dokumen - Data Ruang)", "", "jkrAR_07d_Dokumen_(Pandangan)", "07d_Dokumen / 03_Data Ruang / Elevations", false),
            V("Elevation", "PTBR TAMPAK SISI KIRI", "", "jkrAR_07d_Dokumen_(Pandangan)", "07d_Dokumen / 01_Lukisan Utama / Elevations", true),
            V("Elevation", "PTBR TAMPAK SISI KIRI Copy 1", "", "jkrAR_01w_WIP_(Pandangan)", "01w_WIP / 01_Lukisan Utama / Elevations", false),
            V("Elevation", "PTBR_TAMPAK SISI BELAKANG", "", "jkrAR_07d_Dokumen_(Pandangan)", "07d_Dokumen / 01_Lukisan Utama / Elevations", true),
            V("Elevation", "PTBR_TAMPAK SISI BELAKANG Copy 1", "", "jkrAR_01w_WIP_(Pandangan)", "01w_WIP / 01_Lukisan Utama / Elevations", false),
            V("Elevation", "PTBR_TAMPAK SISI HADAPAN", "", "jkrAR_07d_Dokumen_(Pandangan)", "07d_Dokumen / 01_Lukisan Utama / Elevations", true),
            V("Elevation", "PTBR_TAMPAK SISI HADAPAN Copy 1", "", "jkrAR_01w_WIP_(Pandangan)", "01w_WIP / 01_Lukisan Utama / Elevations", false),
            V("Elevation", "PTBR_TAMPAK SISI KANAN", "", "jkrAR_07d_Dokumen_(Pandangan)", "07d_Dokumen / 01_Lukisan Utama / Elevations", true),
            V("Elevation", "PTBR_TAMPAK SISI KANAN Copy 1", "", "jkrAR_01w_WIP_(Pandangan)", "01w_WIP / 01_Lukisan Utama / Elevations", false),
            V("FloorPlan", "PTBR_Aras Tanah", "PTBR_Aras Tanah", "jkrAR_01w_WIP_(Pelan Aras 1)", "01w_WIP / 01_Lukisan Utama / Floor Plans", false),
            V("FloorPlan", "PTBR_PELAN ARAS TANGKI AIR & BUMBUNG", "PTBR_Aras Bumbung Tangki Air", "jkrAR_07d_Dokumen_(Pelan Aras 1)", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", true),
            V("FloorPlan", "PTBR_PELAN ARAS TANGKI AIR & BUMBUNG Copy 1", "PTBR_Aras Bumbung Tangki Air", "jkrAR_01w_WIP_(Pelan Bumbung)", "01w_WIP / 01_Lukisan Utama / Floor Plans", false),
            V("FloorPlan", "PTBR_PELAN BUMBUNG TANGKI AIR", "PTBR_Aras Bumbung Tangki Air", "jkrAR_07d_Dokumen_(Pelan Aras 1)", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", true),
            V("FloorPlan", "PTBR_PELAN BUMBUNG TANGKI AIR Copy 1", "PTBR_Aras Bumbung Tangki Air", "jkrAR_01w_WIP_(Pelan Bumbung)", "01w_WIP / 01_Lukisan Utama / Floor Plans", false),
            V("FloorPlan", "PTBR_PELAN LANTAI ARAS 01", "PTBR_Aras 01", "jkrAR_07d_Dokumen_(Pelan Aras 1)", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", true),
            V("FloorPlan", "PTBR_PELAN LANTAI ARAS 01 Copy 1", "PTBR_Aras 01", "jkrAR_01w_WIP_(Pelan Aras 1)", "01w_WIP / 01_Lukisan Utama / Floor Plans", false),
            V("FloorPlan", "PTBR_PELAN LANTAI ARAS 02", "PTBR_Aras 02", "jkrAR_07d_Dokumen_(Pelan Aras 1)", "07d_Dokumen / 01_Lukisan Utama / Floor Plans", true),
            V("FloorPlan", "PTBR_PELAN LANTAI ARAS 02 Copy 1", "PTBR_Aras 02", "jkrAR_01w_WIP_(Pelan Aras 1)", "01w_WIP / 01_Lukisan Utama / Floor Plans", false),
            V("Legend", "jkrAR_lgd Tagging", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Jadual Internal Glazing-nota", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Jadual Pintu-nota", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Jadual Tingkap-nota", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Kehendak Bomba", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Kod Komponen", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Legend Am", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Legend Bahan", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Legend Komponen", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Nota Siling", "", "", "03t_Tender / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Perakuan Pengarah", "", "", "??? / ??? / Legends", false),
            V("Legend", "jkrAR_lgd_Petunjuk Bomba", "", "", "??? / ??? / Legends", true),
            V("Legend", "jkrAR_lgd_Petunjuk Pintu", "", "", "??? / ??? / Legends", true),
            V("Legend", "jkrAR_lgd_Petunjuk Tingkap", "", "", "??? / ??? / Legends", true),
            V("Legend", "jkrAR_lgd_Simbol", "", "", "??? / ??? / Legends", true),
            V("Legend", "jkrAR_lgd_Simbol Pintu & Tingkap", "", "", "??? / ??? / Legends", true),
            V("Section", "PTBR_KERATAN A-A", "", "jkrAR_07d_Dokumen_(Keratan)", "07d_Dokumen / 01_Lukisan Utama / Sections", true),
            V("Section", "PTBR_KERATAN A-A Copy 1", "", "jkrAR_01w_WIP_(Keratan)", "01w_WIP / 01_Lukisan Utama / Sections", false),
            V("Section", "PTBR_KERATAN B-B", "", "jkrAR_07d_Dokumen_(Keratan)", "07d_Dokumen / 01_Lukisan Utama / Sections", true),
            V("Section", "PTBR_KERATAN B-B Copy 1", "", "jkrAR_01w_WIP_(Keratan)", "01w_WIP / 01_Lukisan Utama / Sections", false),
            V("ThreeD", "3D RENDERED", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "3D Structure", "", "", "01w_WIP / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "PTBR_ISOMETRIK ARAS 1", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "PTBR_ISOMETRIK ARAS 2", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "PTBR_ISOMETRIK ARAS TANGKI AIR", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "PTBR_ISOMETRIK KERATAN A-A", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "PTBR_ISOMETRIK KERATAN B-B", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "{3D}", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            V("ThreeD", "{3D} Copy 1", "", "jkrAR_07d_Dokumen_(3D)", "07d_Dokumen / 01_Lukisan Utama / 3D Views", false),
            };
            for (var i = 0; i < views.Count; i++) views[i].Id = 900000 + i;
            return views;
        }

        /// <summary>The model's levels (name, elevation mm, Building Story).</summary>
        private static List<SpecLevel> RealLevels() => new List<SpecLevel>
        {
            new SpecLevel { Id = 847600, Name = "PTBR_Aras Asas", Elevation = -1250, IsStory = false },
            new SpecLevel { Id = 847601, Name = "PTBR_Beam Aras Tanah", Elevation = -50, IsStory = false },
            new SpecLevel { Id = 847682, Name = "PTBR_Aras Tanah", Elevation = 0, IsStory = true },
            new SpecLevel { Id = 847700, Name = "PTBR_Beam Aras 01", Elevation = 550, IsStory = false },
            new SpecLevel { Id = 847783, Name = "PTBR_Aras 01", Elevation = 600, IsStory = true },
            new SpecLevel { Id = 848200, Name = "PTBR_Beam Aras 02", Elevation = 4550, IsStory = false },
            new SpecLevel { Id = 848287, Name = "PTBR_Aras 02", Elevation = 4600, IsStory = true },
            new SpecLevel { Id = 876780, Name = "PTBR_Aras Tangki Air", Elevation = 8600, IsStory = true },
            new SpecLevel { Id = 876781, Name = "PTBR_Beam Aras Bumbung", Elevation = 8600, IsStory = true },
            new SpecLevel { Id = 876782, Name = "PTBR_Beam Bumbung Tangki Air", Elevation = 11600, IsStory = false },
            new SpecLevel { Id = 876783, Name = "PTBR_Aras Bumbung Tangki Air", Elevation = 11600, IsStory = true },
            new SpecLevel { Id = 876784, Name = "PTBR_Aras Bumbung", Elevation = 15550, IsStory = true },
        };

        private static SpecModel RealModel() => new SpecModel
        {
            FileName = "jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01.rvt", Lod = 300,
            Levels = RealLevels(), Views = RealViews(),
        }.Normalize();

        [Fact]
        public void Real_model_sets_come_from_the_browser_folder()
        {
            var m = RealModel();
            Assert.Equal(106, m.Views.Count);
            Assert.Equal(15, SpecViewSets.Views(m, "WIP").Count);
            Assert.Equal(66, SpecViewSets.Views(m, "Dokumen").Count);
            Assert.Empty(SpecViewSets.Views(m, "PBT"));
            // 05b_Bomba holds only the drafting view; two legends (blank folder) say Bomba in their names.
            var bomba = SpecViewSets.Views(m, "BOMBA");
            Assert.Equal(new[] { "Petunjuk Bomba" }, bomba.Where(v => SpecViewSets.Of(v).Source == ViewSetSource.Folder).Select(v => v.Name));
            Assert.All(bomba.Where(v => v.Name != "Petunjuk Bomba"), v => Assert.Equal("Legend", v.Kind));
            // The 3D views of 07d_Dokumen have no "Dokumen" in their names.
            var iso = m.Views.Single(v => v.Name == "PTBR_ISOMETRIK ARAS 1");
            Assert.True(SpecViewSets.Of(iso).In("Dokumen"));
        }

        [Fact]
        public void Real_model_C1_2_1_lists_the_15_WIP_views_found_by_browser_folder()
        {
            var r = Of(RealModel(), "C1.2.1");
            Assert.Equal("15 view WIP — untuk semakan manusia; set dikenal pasti ikut folder Project Browser (View_jkr_pit) 15", r[0].Note);
            Assert.Equal("15 WIP views — for a person to review; set found by browser folder View_jkr_pit 15", r[0].NoteEn);
            Assert.Contains(r, x => x.Note == "PTBR_Aras 01 | floor plan ✓ | ceiling plan ✓ | section ✓ (set)");
            Assert.Contains(r, x => x.Note == "PTBR_Aras Tanah | floor plan ✓ | ceiling plan – | section ✓ (set)");
            Assert.Contains(r, x => x.Note.StartsWith("Set WIP: 2 section, 4 elevation"));
        }

        [Fact]
        public void Real_model_C1_5_1_lists_the_66_Dokumen_views()
        {
            var r = Of(RealModel(), "C1.5.1");
            Assert.StartsWith("66 view Dokumen, ", r[0].Note);
            Assert.EndsWith("; set dikenal pasti ikut folder Project Browser (View_jkr_pit) 66", r[0].Note);
            Assert.Equal(67, r.Count);
            Assert.Contains(r, x => x.Note.StartsWith("PTBR_ISOMETRIK ARAS 1 | 3D | — | dalam sheet ✗"));
        }

        [Fact]
        public void Real_model_C1_5_2_finds_3_of_7_story_levels()
        {
            var r = Of(RealModel(), "C1.5.2");
            var fails = r.Where(x => x.Status == "fail").ToList();
            Assert.Equal(new[] { "PTBR_Aras Tanah", "PTBR_Beam Aras Bumbung", "PTBR_Aras Bumbung Tangki Air", "PTBR_Aras Bumbung" },
                fails.Select(f => f.ElementName));
            Assert.All(fails, f => Assert.StartsWith("3D view setiap aras: 3/7 ✗ (", f.Note));
            Assert.Equal("3D view setiap aras: 3/7 ✗ (PTBR_Aras Tanah tiada)", fails[0].Note);
            Assert.Equal(847682, fails[0].ElementId);
        }

        [Fact]
        public void Real_model_C1_3_1_and_C1_4_1_say_how_the_set_was_found()
        {
            var m = RealModel();
            var pbt = Of(m, "C1.3.1");
            Assert.Equal(3, pbt.Count(x => x.Status == "fail"));
            Assert.All(pbt, x => Assert.Contains("tiada view dijumpai ikut folder Project Browser", x.Note));
            var bomba = Of(m, "C1.4.1").Where(x => x.Status == "fail").ToList();
            Assert.Equal(2, bomba.Count);                            // no floor plan, no section; the drafting view is there
            Assert.Contains("drafting view ✓", bomba[0].Note);
            Assert.Contains("ikut nama view 2, ikut folder Project Browser (View_jkr_pit) 1", bomba[0].Note);
        }

        [Fact]
        public void Real_model_E1_judges_the_Dokumen_views_without_a_template_and_skips_legends()
        {
            var r = Of(RealModel(), "E.1");
            var fails = r.Where(x => x.Status == "fail").ToList();
            var dok = fails.Where(f => f.Category == "Dokumen").ToList();
            Assert.Equal(30, dok.Count);
            Assert.All(dok, f => Assert.Equal("tiada", f.Actual));
            var details = RealModel().Views.Where(v => v.Kind == "Detail" && v.TemplateName == "").Select(v => v.Name).ToList();
            Assert.Equal(details.OrderBy(n => n), dok.Select(f => f.ElementName).OrderBy(n => n));
            // Bomba: the drafting view has no template; the legends are not judged.
            var bomba = Assert.Single(fails, f => f.Category == "BOMBA");
            Assert.Equal("Petunjuk Bomba", bomba.ElementName);
            Assert.DoesNotContain(fails, f => f.ElementName.StartsWith("jkrAR_lgd"));
            var skipped = Assert.Single(r, x => x.Note.Contains("legend dilangkau"));
            Assert.StartsWith("2 legend dilangkau", skipped.Note);
            Assert.Equal(SpecCoverage.Listing, skipped.Coverage);
            // The 36 Dokumen views with a Dokumen template pass; the WIP views are listed only.
            var pass = Assert.Single(r, x => x.Status == "pass" && x.Category == "Dokumen" && x.Coverage != SpecCoverage.Listing);
            Assert.Equal("Dokumen: 36 view guna template set yang betul", pass.Note);
            Assert.Equal(15, r.Count(x => x.Category == "WIP" && x.Coverage == SpecCoverage.Listing));
        }

        [Fact]
        public void E1_a_view_in_one_set_using_another_sets_template_fails()
        {
            var m = RealModel();
            m.Views.Single(v => v.Name == "PTBR_PELAN LANTAI ARAS 01").TemplateName = "jkrAR_01w_WIP_(Pelan Aras 1)";
            var f = Assert.Single(Of(m, "E.1"), x => x.Status == "fail" && x.ElementName == "PTBR_PELAN LANTAI ARAS 01");
            Assert.Equal("Dokumen | view 'PTBR_PELAN LANTAI ARAS 01' guna template 'jkrAR_01w_WIP_(Pelan Aras 1)' — bukan template Dokumen (template set WIP)", f.Note);
            Assert.Equal("Dokumen", f.Category);
        }

        [Fact]
        public void Real_model_C3_1_1_ramps_in_multi_category_schedules_and_takeoffs_pass()
        {
            var m = RealModel();
            m.CategoryCounts["Ramps"] = 3;
            foreach (var (name, mto) in new[]
                     {
                         ("jkrAR_sch_gen-mo_Jarak Perjalanan (Bomba)", false),
                         ("jkrAR_mto_wll_Multi-Category Material Takeoff (Dinding)", true),
                         ("jkrAR_mto_mc_Multi-Category Material Takeoff", true),
                         ("jkrAR_mto_mc_Bahan dan Kemasan SPRM", true),
                     })
                m.Schedules.Add(new SpecSchedule { Name = name, IsMaterialTakeoff = mto, RowCategories = new List<string> { "Walls", "Ramps" },
                    RowElementIds = new List<long> { 1060438 } });
            var pass = Assert.Single(Of(m, "C3.1.1"));
            Assert.Equal("pass", pass.Status);
            Assert.StartsWith("1 kategori PB dimodelkan, semua ada schedule (Ramp dalam schedule pelbagai kategori 'jkrAR_sch_gen-mo_Jarak Perjalanan (Bomba)', material takeoff", pass.Note);
        }

        // ═════ The Dewan (jkrAR24_5a_(BEde1A_p14-001)_A1_w-01, 17 Nov 2026) ═════
        // A1.1 and A6.2 judged by level and bounding box gave wrong answers there. Element ids,
        // levels, elevations, sizes, rooms and which floor lies under which ceiling / room are
        // the model's own (model-evidence-1 … 8); the plan coordinates are laid out to keep
        // those relations (the evidence has no coordinates). One assumption: floor 828071
        // (under ceiling 927649) sits in room UM01 Laluan Belakang Pentas — the evidence says a
        // room covers it, not which.

        private const string Tile = "jkrAR_flr_LFh_jh01_30_TDS_in_(LFh301a)-3 @JH1 Anti-slip Porcelain Tiles - 300mm x 300mm  (B1a- MS1294)";
        private const string Svs = "jkrAR_flr_LFp_ls02_30_SVS_in_(LFp602a)-3";
        private const string Ext = "jkrAR_flr_LBk_kt01_150_UMM_ex_(LBk201)-3 @KT1";
        private const string Apron = "jkrAR_flr_LFp_ls01_30_APR_ex_(LFp601a)-3";
        private const string Pb = "jkrAR_clg_SLg_TDS_pb_b2-4.5_in_(SLg701) @S2 Plaster Board 60";

        private static List<SpecPoint> Ring(double x0, double y0, double x1, double y1) => new List<SpecPoint>
        {
            new SpecPoint { X = x0, Y = y0 }, new SpecPoint { X = x1, Y = y0 },
            new SpecPoint { X = x1, Y = y1 }, new SpecPoint { X = x0, Y = y1 },
        };

        private static List<List<SpecPoint>> Box(double x0, double y0, double x1, double y1) =>
            new List<List<SpecPoint>> { Ring(x0, y0, x1, y1) };

        private static SpecFloor DF(long id, string type, string level, double top, double x0, double y0, double x1, double y1) => new SpecFloor
        {
            Id = id, TypeName = type, LevelName = level, TopElevation = top, Loops = Box(x0, y0, x1, y1),
            MinX = x0, MinY = y0, MaxX = x1, MaxY = y1, Area = (x1 - x0) * (y1 - y0) / 1e6,
        };

        private static SpecRoom DR(long id, string number, string name, string level, double baseElev, double height,
                                   double x0, double y0, double x1, double y1) => new SpecRoom
        {
            Id = id, Number = number, Name = name, LevelName = level, Placed = true, Area = (x1 - x0) * (y1 - y0) / 1e6,
            X = (x0 + x1) / 2, Y = (y0 + y1) / 2, BaseElevation = baseElev, Height = height, Loops = Box(x0, y0, x1, y1),
        };

        private static SpecCeiling DC(long id, string level, double offset, double bottom, double x0, double y0, double x1, double y1) => new SpecCeiling
        {
            Id = id, TypeName = Pb, LevelName = level, HeightOffset = offset, BottomElevation = bottom, Loops = Box(x0, y0, x1, y1),
        };

        /// <summary>The 16 plasterboard ceilings beside the stage (Aras 01 + 3450 = 3900 abs) and
        /// the floor finish under each: (ceiling, floor, floor top, box).</summary>
        private static readonly (long Ceiling, long Floor, double Top, double X0, double Y0, double X1, double Y1)[] StageSide =
        {
            // UM05 Bilik Persalinan (P)
            (927361, 736370, 1300, 30000, -20000, 31500, -17000), (927406, 736136, 1250, 31500, -20000, 33000, -17000),
            (927415, 736152, 1250, 30000, -17000, 31500, -14000), (927424, 736168, 1250, 31500, -17000, 33000, -14000),
            // UM07 Tandas Perempuan
            (927379, 735336, 1250, 33000, -20000, 34500, -17000), (927388, 735550, 1650, 34500, -20000, 36000, -17000),
            (927397, 735580, 1650, 33000, -17000, 34500, -14000), (927370, 736318, 1275, 34500, -17000, 36000, -14000),
            // UM04 Bilik Persalinan (L)
            (927525, 735201, 1250, 30000, -6000, 31500, -3000), (927445, 736402, 1300, 31500, -6000, 33000, -3000),
            (927507, 736099, 1250, 30000, -3000, 31500, 0), (927516, 736083, 1250, 31500, -3000, 33000, 0),
            // UM06 Tandas Lelaki
            (927489, 734941, 1250, 33000, -6000, 34500, -3000), (927480, 735048, 1650, 34500, -6000, 36000, -3000),
            (927471, 735938, 1650, 33000, -3000, 34500, 0), (927454, 735102, 1275, 34500, -3000, 36000, 0),
        };

        private static readonly long[] PadIds =
        {
            930585, 930892, 930900, 930908, 930916, 930924, 930932, 930940, 930948, 930956, 930964,
            930972, 930980, 930988, 930996, 931004, 931012, 931020, 931028, 931036, 931044, 931052,
        };

        /// <summary>The Dewan as the fixed reader reads it: Ceilings category only (the four roof
        /// soffits 2060831, 2061904, 2062122, 2062156 are not in it), loops and elevations on
        /// floors, ceilings and rooms.</summary>
        private static SpecModel Dewan()
        {
            var m = new SpecModel
            {
                FileName = "Copy of jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt", Lod = 300,
                Levels =
                {
                    new SpecLevel { Id = 311, Name = "Aras Tanah", Elevation = 0 },
                    new SpecLevel { Id = 306865, Name = "Aras 01", Elevation = 450 },
                    new SpecLevel { Id = 1449182, Name = "Aras Pentas", Elevation = 1350 },
                    new SpecLevel { Id = 1109767, Name = "Aras Langkan", Elevation = 4950 },
                    new SpecLevel { Id = 694, Name = "Aras 02", Elevation = 6000 },
                    new SpecLevel { Id = 307460, Name = "Aras Bumbung", Elevation = 10650 },
                },
                Rooms =
                {
                    DR(1, "DE02", "Dewan Utama", "Aras 01", 450, 5550, 0, -20000, 30000, 0),
                    DR(2, "UM04", "Bilik Persalinan (L)", "Aras 01", 450, 3450, 30000, -6000, 33000, 0),
                    DR(3, "UM06", "Tandas Lelaki", "Aras 01", 450, 3450, 33000, -6000, 36000, 0),
                    DR(4, "UM05", "Bilik Persalinan (P)", "Aras 01", 450, 3450, 30000, -20000, 33000, -14000),
                    DR(5, "UM07", "Tandas Perempuan", "Aras 01", 450, 3450, 33000, -20000, 36000, -14000),
                    DR(6, "UM01", "Laluan Belakang Pentas", "Aras 01", 450, 3450, 30000, -14000, 36000, -6000),
                    DR(7, "RM06", "Tandas Perempuan", "Aras 01", 450, 3250, 36000, -17000, 40000, -14000),
                    DR(8, "RM05", "Tandas Anjung Lelaki", "Aras 01", 450, 3250, 36000, -20000, 40000, -17000),
                    DR(9, "RM02", "Anjung", "Aras 01", 450, 3950, 40000, -14000, 44000, -6000),
                    DR(10, "RM03", "Anjung Kanan", "Aras 01", 450, 3210, 40000, -20000, 44000, -15000),
                    DR(11, "DE03", "Bilik Kawalan", "Aras 02", 6000, 3448, 30000, -6000, 36000, 0),
                },
                Floors =
                {
                    DF(736950, Tile, "Aras 01", 450, 0, -20000, 30000, 0),
                    DF(828071, Svs, "Aras 01", 1350, 31000, -10000, 34800, -9000),
                    // the three real slabs with no room: 3.78 × 1.09, 3.79 × 0.74, 3.78 × 0.73 m
                    DF(827925, Svs, "Aras 01", 1300, 36000, -2000, 39780, -910),
                    DF(828016, Svs, "Aras 01", 1350, 36000, -5000, 39790, -4260),
                    DF(828043, Svs, "Aras 01", 1350, 36000, -8000, 39780, -7270),
                    DF(739171, Tile, "Aras 01", 300, 36000, -17000, 40000, -14000),
                    DF(739283, Tile, "Aras 01", 300, 36000, -20000, 40000, -17000),
                    DF(906566, Ext, "Aras Tanah", 50, 40000, -14000, 44000, -6000),
                    DF(737051, Tile, "Aras 01", 350, 40000, -20000, 44000, -15000),
                    DF(868077, Svs, "Aras 02", 6000, 30000, -6000, 36000, 0),
                },
                Ceilings =
                {
                    DC(894658, "Aras 02", 3448, 9448, 0, -20000, 30000, 0),
                    DC(1100131, "Aras 01", 3210, 3660, 1000, -3000, 5000, -1000),
                    DC(927561, "Aras 01", 3250, 3700, 36000, -17000, 40000, -14000),
                    DC(927583, "Aras 01", 3250, 3700, 36000, -20000, 40000, -17000),
                    DC(927649, "Aras 01", 3450, 3900, 31000, -10000, 34800, -9000),
                    DC(1099813, "Aras 01", 3950, 4400, 40000, -14000, 44000, -6000),
                    DC(1312453, "", 0, 3660, 40000, -20000, 44000, -15000),       // no level: measured from the floor only
                    DC(2083190, "Aras 02", 3448, 9448, 30000, -6000, 36000, 0),
                },
            };
            foreach (var s in StageSide.OrderBy(s => s.Ceiling))
            {
                m.Floors.Add(DF(s.Floor, Tile, "Aras 01", s.Top, s.X0, s.Y0, s.X1, s.Y1));
                m.Ceilings.Add(DC(s.Ceiling, "Aras 01", 3450, 3900, s.X0, s.Y0, s.X1, s.Y1));
            }
            // The 50.7 × 25.2 m external apron: a ring round the building, no room on it.
            m.Floors.Add(new SpecFloor
            {
                Id = 900149, TypeName = Apron, LevelName = "Aras 01", TopElevation = 50,
                Loops = { Ring(-5000, -22600, 45700, 2600), Ring(0, -20000, 44000, 0) },
                MinX = -5000, MinY = -22600, MaxX = 45700, MaxY = 2600, Area = 397.6,
            });
            // 22 concrete pads, 0.67 × 0.67 m, at 2652 mm on Aras Tanah.
            for (var i = 0; i < PadIds.Length; i++)
                m.Floors.Add(DF(PadIds[i], Ext, "Aras Tanah", 2652.4, 46000 + i * 1000, 1000, 46670 + i * 1000, 1670));
            foreach (var (name, x) in new[] { ("1", 0.0), ("2", 3900.0), ("3", 8400.0), ("4", 12900.0), ("5", 17400.0), ("6", 21900.0),
                                              ("7", 26400.0), ("8", 30900.0), ("9", 35400.0), ("10", 39900.0), ("11", 44400.0), ("12", 48300.0), ("13", 55800.0) })
                m.Grids.Add(new SpecGrid { Name = name, IsVertical = true, Offset = x });
            foreach (var (name, y) in new[] { ("A", 0.0), ("B", -2400.0), ("C", -8400.0), ("D", -11400.0), ("E", -14400.0), ("F", -20400.0), ("G", -22800.0) })
                m.Grids.Add(new SpecGrid { Name = name, IsVertical = false, Offset = y });
            return m.Normalize();
        }

        [Fact]
        public void Dewan_A1_1_fails_Aras_01_on_the_stage_side_ceilings_measured_from_the_floor_under_them()
        {
            var r = Of(Dewan(), "A1.1");
            var fail = Assert.Single(r, x => x.Status == "fail");
            Assert.Equal("Aras 01: siling 2250 mm < 3000 mm — UM07 Tandas Perempuan 2250 mm, UM06 Tandas Lelaki 2250 mm, "
                         + "UM01 Laluan Belakang Pentas 2550 mm, UM05 Bilik Persalinan (P) 2600 mm, UM04 Bilik Persalinan (L) 2600 mm", fail.Note);
            Assert.Equal("Level Aras 01: ceiling 2250 mm < 3000 mm — UM07 Tandas Perempuan 2250 mm, UM06 Tandas Lelaki 2250 mm, "
                         + "UM01 Laluan Belakang Pentas 2550 mm, UM05 Bilik Persalinan (P) 2600 mm, UM04 Bilik Persalinan (L) 2600 mm", fail.NoteEn);
            Assert.Equal("Aras 01", fail.ElementName);
            Assert.Equal("2250 mm", fail.Actual);
            // All 16 stage-side ceilings and 927649 (over floor 828071, top 1350: 2550 mm), lowest first.
            Assert.Equal(17, fail.ElementIds.Count);
            Assert.Equal(StageSide.Select(s => s.Ceiling).Append(927649L).OrderBy(x => x), fail.ElementIds.OrderBy(x => x));
            Assert.Equal(927388, fail.ElementId);
            // The 3450 mm above Aras 01 they were passed on before is not what is reported.
            Assert.DoesNotContain("3450", fail.Note);
            // Aras Tanah (Anjung 4350) and Aras 02 (3448) pass; the hall's ceiling counts for Aras 01 (8998).
            var pass = Assert.Single(r, x => x.Status == "pass");
            Assert.Equal("2 aras: siling ≥ 3000 mm (basement ≥ 2500 mm)", pass.Note);
            Assert.Equal(SpecState.Fail, SpecReport.Build(SpecChecks.Run(Dewan())).For("A1.1").State);
        }

        [Fact]
        public void Dewan_A1_1_does_not_ask_a_person_about_Aras_Bumbung_any_more()
        {
            var r = Of(Dewan(), "A1.1");
            Assert.DoesNotContain(r, x => x.Status == "cannot_verify");
            Assert.DoesNotContain(r, x => x.Note.Contains("Bumbung"));
        }

        [Fact]
        public void The_reader_takes_ceilings_from_the_Ceilings_category_not_every_Ceiling_class_element()
        {
            // Roof Soffits are Ceiling-class elements; the Dewan's four soffits (−787 mm offset) made
            // the pane ask "Aras Bumbung: ceiling height not read". The reader is Revit-only, so
            // this reads its source.
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Services", "JkrSpec", "SpecModelReader.cs"));
            var start = src.IndexOf("private static void ReadCeilings(", System.StringComparison.Ordinal);
            Assert.True(start > 0);
            var body = src.Substring(start, src.IndexOf("m.Ceilings.Add(x);", start, System.StringComparison.Ordinal) - start);
            Assert.Contains("OfCategory(BuiltInCategory.OST_Ceilings)", body);
            Assert.DoesNotContain("OfClass(typeof(Ceiling))", body);
        }

        [Fact]
        public void Dewan_A1_1_still_measures_a_ceiling_from_its_level_when_no_floor_footprint_is_under_it()
        {
            var m = Dewan();
            foreach (var f in m.Floors) f.Loops.Clear();          // an old snapshot: no footprints
            var r = Of(m, "A1.1");
            // Height above the host level: every Aras 01 ceiling is ≥ 3210 mm, so the level-only
            // reading passes the stage side (what the pane showed); Aras 01 and Aras 02 pass.
            Assert.DoesNotContain(r, x => x.Status == "fail");
            Assert.Equal("2 aras: siling ≥ 3000 mm (basement ≥ 2500 mm)", Assert.Single(r, x => x.Status == "pass").Note);
            // 1312453 has no level and no floor to measure from: a person checks it.
            var unread = Assert.Single(r, x => x.Status == "cannot_verify");
            Assert.Equal(1312453, unread.ElementId);
        }

        [Fact]
        public void Dewan_A6_2_lists_the_three_slabs_and_the_apron_never_fails_and_passes_floors_inside_rooms()
        {
            // AI-504 (Q5): the 50 % share, the 1 m² pad rule and the 1500 mm slack are gone. A slab with
            // no room is listed for a person, whatever its size; a slab with a room on it complies.
            var r = Of(Dewan(), "A6.2");
            Assert.DoesNotContain(r, x => x.Status == "fail");
            var listed = r.Where(x => x.Coverage == SpecCoverage.Context).ToList();
            foreach (var id in new long[] { 827925, 828016, 828043, 900149 })
                Assert.Contains(listed, f => f.ElementId == id);
            Assert.StartsWith("Aras 01 | grid 10/B: lantai 827925 (" + Svs + ") tiada room", listed.Single(f => f.ElementId == 827925).Note);
            var apron = listed.Single(f => f.ElementId == 900149);
            Assert.StartsWith("Aras 01 | grid ", apron.Note);
            Assert.Contains(" | apron: lantai 900149 (" + Apron + ") tiada room", apron.Note);
            Assert.All(listed, f => Assert.NotEqual("", f.IssueId));
            // The floor finishes the bounding-box rule failed are inside their rooms.
            foreach (var id in StageSide.Select(s => s.Floor).Append(828071L))
                Assert.DoesNotContain(listed, f => f.ElementId == id);
            // The pads are slabs like any other: each with no room is listed, none is skipped.
            foreach (var id in PadIds) Assert.Contains(listed, f => f.ElementId == id);
            var pass = Assert.Single(r, x => x.Status == "pass" && x.Coverage == SpecCoverage.Full);
            Assert.Equal(22, pass.ElementCount);
            Assert.DoesNotContain("%", pass.Note);
            // Nothing judged except passes and rows for a person: the check stays Pass.
            Assert.Equal(SpecState.Pass, SpecReport.Build(SpecChecks.Run(Dewan())).For("A6.2").State);
        }
    }
}
