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

        [Fact]
        public void A_3D_view_named_with_the_level_number_counts_for_that_level()
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
                    new SpecView { Id = 1, Name = "PTBR_ISOMETRIK ARAS 1", Kind = "ThreeD" },
                    new SpecView { Id = 2, Name = "PTBR_ISOMETRIK ARAS TANGKI AIR", Kind = "ThreeD" },
                },
            };
            var fails = Of(m, "C1.1.3").Where(r => r.Status == "fail").ToList();
            Assert.Single(fails);                                   // only Aras 02 is missing
            Assert.Contains("PTBR_Aras 02", fails[0].Note);
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
    }
}
