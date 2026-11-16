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
    }
}
