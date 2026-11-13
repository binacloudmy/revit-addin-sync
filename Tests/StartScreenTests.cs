using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 8: the Start screen (frames s-start, s-ready, s-lod100). LOD is
    /// required with no default and Run stays disabled until it is set.
    /// </summary>
    public class StartScreenTests
    {
        private static (StartScreenModel start, PanelCore core) New()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            return (new StartScreenModel(core) { FileLabel = "jkrAR24_5a.rvt" }, core);
        }

        [Fact]
        public void Lod_has_no_default_and_run_is_disabled_until_it_is_set()
        {
            var (s, _) = New();
            Assert.Equal("", s.LodValue);
            Assert.True(s.LodMissing);
            Assert.Equal("need", s.LodTag);
            Assert.False(s.CanRun);
            Assert.False(s.Run());

            s.LodValue = "300";
            Assert.False(s.LodMissing);
            Assert.Equal("", s.LodTag);
            Assert.True(s.CanRun);
        }

        [Fact]
        public void Run_passes_the_chosen_lod_and_discipline()
        {
            var (s, _) = New();
            (int lod, string disc)? asked = null;
            s.RunRequested += (l, d) => asked = (l, d);
            s.LodValue = "400";
            s.DiscValue = "ST";
            Assert.True(s.Run());
            Assert.Equal((400, "ST"), asked);
        }

        [Fact]
        public void Discipline_defaults_to_ar()
        {
            Assert.Equal("AR", New().start.DiscValue);
        }

        [Fact]
        public void The_lod_100_note_shows_only_for_lod_100()
        {
            var (s, _) = New();
            Assert.False(s.Lod100);
            s.LodValue = "100";
            Assert.True(s.Lod100);
            Assert.True(s.CanRun);                         // LOD 100 still runs (naming only)
            s.LodValue = "200";
            Assert.False(s.Lod100);
        }

        [Fact]
        public void Options_follow_the_prototype_in_bm_and_en()
        {
            var (s, core) = New();
            Assert.Equal(new[] { "— Pilih —", "LOD 100", "LOD 200", "LOD 300", "LOD 400", "LOD 500" }, s.LodOptions.Select(o => o.Label));
            Assert.Equal(new[] { "", "100", "200", "300", "400", "500" }, s.LodOptions.Select(o => o.Value));
            Assert.Equal(new[] { "AR · Arkitek", "CD", "EL", "ME", "ST" }, s.DiscOptions.Select(o => o.Label));
            core.SetLang("en");
            Assert.Equal("— Select —", s.LodOptions[0].Label);
            Assert.Equal("AR · Architecture", s.DiscOptions[0].Label);
        }

        [Fact]
        public void Strings_are_the_prototypes()
        {
            var (s, core) = New();
            Assert.Equal(("Projek", "Tahap LOD", "Disiplin", "Jalankan audit"), (s.ProjectLabel, s.LodLabel, s.DiscLabel, s.RunLabel));
            Assert.Equal("Borang Model Audit BIM010 · Seksyen A–E", s.RunHint);
            core.SetLang("en");
            Assert.Equal("Required. Mandatory parameters depend on LOD; the audit cannot run without it.", s.LodRequiredNote);
            Assert.Equal("Checks start from LOD 200. LOD 100 checks naming only.", s.Lod100Note);
            Assert.Equal("Run audit", s.RunLabel);
        }

        [Fact]
        public void The_option_lists_are_kept_until_the_language_changes()
        {
            var (s, core) = New();
            var lods = s.LodOptions;
            s.LodValue = "300";
            Assert.Same(lods, s.LodOptions);              // same list: the ComboBox keeps its item
            core.SetLang("en");
            Assert.NotSame(lods, s.LodOptions);
            Assert.Equal("300", s.LodValue);               // the choice survives the switch
        }

        [Fact]
        public void A_transient_null_from_the_combobox_keeps_the_lod()
        {
            var (s, _) = New();
            s.LodValue = "300";
            s.LodValue = null;
            Assert.Equal("300", s.LodValue);
            s.LodValue = "";                               // choosing "— Select —" clears it
            Assert.True(s.LodMissing);
        }

        [Fact]
        public void Run_starts_the_run_and_reset_comes_back_to_start_without_an_lod()
        {
            var (s, core) = New();
            s.LodValue = "300";
            core.StartRun();
            Assert.Equal(PanelStage.Running, core.Stage);
            core.Reset();
            Assert.Equal(PanelStage.Start, core.Stage);
            Assert.True(s.LodMissing);
            Assert.False(s.CanRun);
        }

        [Fact]
        public void Changes_notify_the_view()
        {
            var (s, _) = New();
            var raised = 0;
            s.PropertyChanged += (_, __) => raised++;
            s.LodValue = "200";
            s.FileLabel = "other.rvt";
            Assert.True(raised >= 2);
        }
    }
}
