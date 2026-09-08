using System.Linq;
using Xunit;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;

namespace Tests
{
    // The settings window's draft: what the drafter types is validated and only then written
    // back to the persisted settings (Cancel leaves them untouched).
    public class CadToBimSettingsDraftTests
    {
        [Fact]
        public void Draft_round_trips_the_defaults()
        {
            var settings = new CadToBimSettings();
            var draft = CadToBimSettingsDraft.From(settings);
            Assert.Equal(50, draft.SMin);
            Assert.Equal(400, draft.SMax);
            Assert.Equal(3000, draft.Height);
            Assert.Equal("wall, dinding, tembok, partition, bata", draft.WallHints);
            Assert.Null(draft.Validate());

            draft.WriteTo(settings);
            Assert.Equal(new[] { "wall", "dinding", "tembok", "partition", "bata" }, settings.WallLayerHints);
            Assert.Equal(400, settings.SMaxMm);
        }

        [Fact]
        public void Min_must_be_below_max()
        {
            var draft = CadToBimSettingsDraft.From(new CadToBimSettings());
            draft.SMin = 400;
            draft.SMax = 300;
            Assert.Equal("Minimum thickness must be smaller than the maximum.", draft.Validate());
            draft.DoorMin = 1500; draft.DoorMax = 500; draft.SMin = 50; draft.SMax = 400;
            Assert.Equal("Door swing minimum radius must be smaller than the maximum.", draft.Validate());
        }

        [Fact]
        public void Lists_split_on_commas_and_drop_blanks()
        {
            Assert.Equal(new[] { "A-WALL", "Dinding" }, CadToBimSettingsDraft.SplitList(" A-WALL ,, Dinding , "));
            Assert.Empty(CadToBimSettingsDraft.SplitList(null));
        }

        [Fact]
        public void Template_path_blank_writes_null()
        {
            var settings = new CadToBimSettings { TemplatePath = @"C:\old.rte" };
            var draft = CadToBimSettingsDraft.From(settings);
            draft.TemplatePath = "   ";
            draft.WriteTo(settings);
            Assert.Null(settings.TemplatePath);
        }
    }
}
