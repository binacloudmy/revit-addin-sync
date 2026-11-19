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
        public void Discipline_is_detected_from_the_model_file_name()
        {
            var (s, _) = New();                            // jkrAR24_5a.rvt
            Assert.Equal("AR", s.DiscValue);
            s.FileLabel = "jkrST22_2b_(BEdeA1_14-001)_A1_w-01_(S)_DS-PC_190124.rvt";
            Assert.Equal("ST", s.DiscValue);
            s.FileLabel = "Copy of jkrEL24-5a_(BEde1a_14).rvt";
            Assert.Equal("EL", s.DiscValue);
        }

        [Theory]
        [InlineData("jkrLD01_landscape.rvt")]              // LD is not offered: AR
        [InlineData("Project1")]                            // unknown: AR
        [InlineData("—")]                                   // no model
        public void Unknown_or_unoffered_disciplines_fall_back_to_ar(string file)
        {
            var (s, _) = New();
            s.FileLabel = "jkrST01.rvt";
            s.FileLabel = file;
            Assert.Equal("AR", s.DiscValue);
        }

        [Fact]
        public void A_discipline_the_user_picks_sticks_until_another_model_opens()
        {
            var (s, core) = New();
            s.DiscValue = "ME";
            Assert.True(s.DiscChosen);
            core.SetLang("en");
            core.Reset();                                   // Reset keeps the choice
            Assert.Equal("ME", s.DiscValue);
            s.FileLabel = "jkrST01.rvt";                    // a different model: detected again
            Assert.Equal("ST", s.DiscValue);
            Assert.False(s.DiscChosen);
        }

        [Fact]
        public void The_dropdown_echoing_the_current_value_is_not_a_user_choice()
        {
            var (s, _) = New();
            s.DiscValue = "AR";                             // same as detected
            Assert.False(s.DiscChosen);
            s.DiscValue = null;
            s.DiscValue = "";
            Assert.Equal("AR", s.DiscValue);
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
            Assert.Equal(new[] { "— Pilih tahap LOD —", "LOD 100", "LOD 200", "LOD 300", "LOD 400", "LOD 500" }, s.LodOptions.Select(o => o.Label));
            Assert.Equal(new[] { "", "100", "200", "300", "400", "500" }, s.LodOptions.Select(o => o.Value));
            Assert.Equal(new[] { "AR · Arkitek", "CD", "EL", "ME", "ST" }, s.DiscOptions.Select(o => o.Label));
            core.SetLang("en");
            Assert.Equal("— Select LOD level —", s.LodOptions[0].Label);
            Assert.Equal("AR · Architect", s.DiscOptions[0].Label);
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
        public void A_language_switch_renames_the_options_but_keeps_the_same_objects()
        {
            // Replacing the lists made the Discipline dropdown go blank after BM → EN.
            var (s, core) = New();
            var lods = s.LodOptions;
            var discs = s.DiscOptions;
            var ar = discs[0];
            s.LodValue = "300";
            string renamed = null;
            ar.PropertyChanged += (_, e) => renamed = e.PropertyName;
            core.SetLang("en");
            Assert.Same(lods, s.LodOptions);
            Assert.Same(discs, s.DiscOptions);
            Assert.Equal("AR · Architect", ar.Label);
            Assert.Equal("Label", renamed);
            Assert.Equal("300", s.LodValue);               // both choices survive the switch
            Assert.Equal("AR", s.DiscValue);
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
    
        [Fact]
        public void Clear_sheet_copy_is_in_both_languages()
        {
            var (s, core) = New();
            core.SetLang("en");
            Assert.Equal("Set up the audit", s.Title);
            Assert.StartsWith("61 checks from the JKR Model Audit spec", s.Lead);
            Assert.Equal("Required", s.LodRequiredTag);
            Assert.Equal("Sets which parameters each family must carry. There is no default.", s.LodHint);
            Assert.Equal("Preset from the file name (AR).", s.DiscHint);
            Assert.Equal("Borang sections", s.SectionsLabel);
            Assert.Equal("61 checks", s.SectionsCount);
            Assert.Equal("Choose a level of development to run the audit.", s.ReadyHint);
            Assert.Equal("AR · Architect", s.DiscOptions[0].Label);
            core.SetLang("ms");
            Assert.Equal("Sediakan audit", s.Title);
            Assert.Equal("Wajib", s.LodRequiredTag);
            Assert.Equal("61 semakan", s.SectionsCount);
        }

        [Fact]
        public void Hint_follows_the_detected_discipline_and_shows_only_without_a_lod()
        {
            var (s, core) = New();
            core.SetLang("en");
            s.FileLabel = "jkrST22_2b.rvt";
            Assert.Equal("Preset from the file name (ST).", s.DiscHint);
            Assert.True(s.ReadyHintVisible);
            s.LodValue = "200";
            Assert.False(s.ReadyHintVisible);
        }

        [Fact]
        public void Borang_sections_list_the_five_sections_totalling_61_checks()
        {
            var (s, core) = New();
            core.SetLang("en");
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, s.Sections.Select(x => x.Letter));
            Assert.Equal(new[] { "24 checks", "3 checks", "24 checks", "9 checks", "1 check" }, s.Sections.Select(x => x.CountLabel));
            Assert.Equal(61, s.Sections.Sum(x => x.Checks));
            Assert.Equal("Model integrity & quality", s.Sections[0].Name);
            core.SetLang("ms");
            Assert.Equal("Integriti & kualiti model", s.Sections[0].Name);
            Assert.Equal("1 semakan", s.Sections[4].CountLabel);
        }
}
}
