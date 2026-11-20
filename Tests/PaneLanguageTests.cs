using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Text the add-in writes itself follows the pane's BM / EN switch: toasts,
    /// busy labels, dialogs, fix-failure reasons, how-to-fix steps, Locate
    /// messages, element words. Every check passes the language explicitly, so
    /// these tests never depend on (or change) the shared current language.
    /// </summary>
    [Xunit.Collection("PaneLanguage")]
    public class PaneLanguageTests
    {
        private const string Ms = PanelStrings.Ms;
        private const string En = PanelStrings.En;

        [Fact]
        public void Messages_come_in_both_languages()
        {
            Assert.Equal("Menulis 3 perubahan…", PaneText.Applying(3, Ms));
            Assert.Equal("Applying 3 changes…", PaneText.Applying(3, En));
            Assert.Equal("Applying 1 change…", PaneText.Applying(1, En));
            Assert.Equal("Pembaikan sedang berjalan — sila tunggu.", PaneText.FixRunning(Ms));
            Assert.Equal("2 isu diabaikan", PaneText.IssuesIgnored(2, Ms));
            Assert.Equal("2 issue(s) ignored", PaneText.IssuesIgnored(2, En));
            Assert.Equal("Dibaiki · Door", PaneText.Decision("Fixed", "Door", Ms));
            Assert.Equal("Reopened · Door", PaneText.Decision("Open", "Door", En));
        }

        [Fact]
        public void Fix_toasts_use_the_prototype_text()
        {
            Assert.Equal("3 change(s) written to model", PaneText.ChangesWritten(3, 0, En));
            Assert.Equal("3 change(s) written to model · 1 failed, still Open", PaneText.ChangesWritten(3, 1, En));
            Assert.Equal(PanelStrings.Get(Ms, "toastFix"), PaneText.ChangesWritten(1, 0, Ms).Substring(2));
            Assert.EndsWith(" · 1 gagal, masih Terbuka", PaneText.ChangesWritten(3, 1, Ms));
        }

        [Fact]
        public void Locate_says_selected_in_model_with_the_element()
        {
            Assert.Equal("Selected in model: Doors [204511] PT2p600a", PaneText.SelectedInModel("Doors [204511] PT2p600a", En));
            Assert.StartsWith(PanelStrings.Get(Ms, "toastLoc") + ": ", PaneText.SelectedInModel("x", Ms));
        }

        [Fact]
        public void Toasts_last_2_6_s_and_longer_when_long()
        {
            Assert.Equal(2600, PaneText.ToastTime("1 change(s) written to model").TotalMilliseconds);
            var reset = PaneText.ResetDone(En);                     // a long notice gets time to be read
            Assert.InRange(PaneText.ToastTime(reset).TotalMilliseconds, 5000, 8000);
            Assert.Equal(8000, PaneText.ToastTime(new string('x', 500)).TotalMilliseconds);
        }

        [Theory]
        [InlineData("Parameter 'Area' is read-only", "Parameter 'Area' hanya-baca (read-only).")]
        [InlineData("Element 999999999 was not found in the model.", "Elemen 999999999 tiada dalam model.")]
        [InlineData("Revit did not apply this change.", "Revit tidak menggunakan perubahan ini.")]
        [InlineData("Rename failed: The name is already in use.", "Tukar nama gagal: The name is already in use.")]
        [InlineData("Parameter 'Kod_Jenis' not found on element 12. Bound from the JKR file.",
                    "Parameter 'Kod_Jenis' tiada pada elemen 12. Bound from the JKR file.")]
        public void Recorded_fix_reasons_are_shown_in_bm(string recorded, string bm)
        {
            Assert.Equal(bm, PaneText.FixReason(recorded, Ms));
            Assert.Equal(recorded, PaneText.FixReason(recorded, En));   // stored text stays English
        }

        [Fact]
        public void A_reason_from_revit_itself_is_shown_as_revit_wrote_it()
        {
            Assert.Equal("The name is already in use.", PaneText.FixReason("The name is already in use.", Ms));
        }

        [Fact]
        public void The_failed_fix_list_follows_the_language()
        {
            var r = new RenameResult();
            r.RecordFailure("p:5:Area", 5, PaneText.ParamWhat("Area", 5, Ms), "Parameter 'Area' is read-only");
            Assert.Equal("Area pada 5: Parameter 'Area' hanya-baca (read-only).", r.BuildFailDetails(20, Ms));
            Assert.Equal("Area pada 5: Parameter 'Area' is read-only", r.BuildFailDetails(20, En));
            for (var i = 0; i < 3; i++) r.RecordFailure("r:" + i, i, "x", "y");
            Assert.EndsWith("…dan 2 lagi", r.BuildFailDetails(2, Ms));
            // The blocklist / audit file keep the recorded (English) reason.
            Assert.Equal("Parameter 'Area' is read-only", r.ReasonFor("set_parameter", 5, "Area"));
        }

        [Fact]
        public void How_to_fix_steps_follow_the_language()
        {
            var vm = new IssueVm { FixAction = "set_parameter", FixParameterName = "Area", FixValue = "12", FixTarget = "instance",
                                   FixError = "Parameter 'Area' is read-only" };
            var ms = vm.StepsIn(Ms);
            Assert.Equal("Pilih elemen dalam Revit (Cari dalam 3D).", ms[0]);
            Assert.Contains(ms, s => s.Contains("hanya-baca"));
            Assert.Equal("Select the element in Revit (Locate in 3D).", vm.StepsIn(En)[0]);
            // The backend's own steps are shown as the backend wrote them.
            Assert.Equal(new[] { "a" }, new IssueVm { Steps = new List<string> { "a" } }.StepsIn(Ms));
        }

        [Fact]
        public void A_missing_parameter_is_not_mistaken_for_a_missing_element()
        {
            var vm = new IssueVm { FixAction = "set_parameter", FixParameterName = "Kod_Jenis", FixValue = "AR", FixTarget = "type",
                                   FixError = "Parameter 'Kod_Jenis' not found on element 12. " };
            var steps = vm.StepsIn(En);
            Assert.Contains(steps, s => s.Contains("JKR shared parameter file"));
            Assert.DoesNotContain(steps, s => s.Contains("no longer in the model"));
        }

        [Fact]
        public void Locate_messages_follow_the_language()
        {
            var r = new JkrLocateResult(JkrLocateKind.OpenedView, 1, "Aras 01");
            Assert.Equal("Paparan 'Aras 01' dibuka.", r.MessageIn(Ms));
            Assert.Equal("Opened the view 'Aras 01'.", r.MessageIn(En));
            Assert.Null(new JkrLocateResult(JkrLocateKind.Shown).MessageIn(Ms));
        }

        [Fact]
        public void Element_words_from_the_local_bim010_rows_follow_the_language()
        {
            Assert.Equal("Elemen", PaneText.ElementName("Element", Ms));
            Assert.Equal("3 elemen", PaneText.ElementName("3 elements", Ms));
            Assert.Equal("3 elements", PaneText.ElementName("3 elements", En));
            Assert.Equal("PT2p600a", PaneText.ElementName("PT2p600a", Ms));
        }

        [Fact]
        public void The_results_list_shows_fix_errors_in_the_screen_language()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core);
            core.SetLod("300");
            core.BeginExternalRun();
            core.FinishExternalRun();
            var results = new ResultsScreenModel(core, start);
            var issue = new IssueVm
            {
                Id = "1", BorangKey = "A4", Title = "t", Status = IssueStatus.Open,
                FixError = "Parameter 'Area' is read-only", Element = new ElementRef { Id = "5", Name = "Element" },
                Spec = new SpecRef { Doc = "" },
            };

            core.SetLang(Ms);
            results.Load(new[] { issue }, null);
            Assert.Equal("Parameter 'Area' hanya-baca (read-only).", results.Rows.Single().FixError);
            Assert.Equal("[5] Elemen", core.Issues.Single().El);

            core.SetLang(En);
            results.Load(new[] { issue }, null);                // the host reloads on BM / EN
            Assert.Equal("Parameter 'Area' is read-only", results.Rows.Single().FixError);
            Assert.Equal("[5] Element", core.Issues.Single().El);
        }

        [Fact]
        public void Every_bm_reason_pattern_has_text()
        {
            // Guard: a reason we record must never come out empty in BM.
            foreach (var recorded in new[]
            {
                "Revit did not accept this change (RolledBack).", "Rename target is empty.", "No new name provided",
                "No parameter name provided", "Element 7 not found", "Type not found for element 7",
                "Unknown fix action: frob", "Set parameter failed: x", "Error: x",
                "Parameter 'P' was bound but still not visible on element 7.",
                "Element 7 is inside a model group — Revit blocks instance parameter edits outside group edit mode.",
                "Unsupported storage type for 'P'", "Failed to set 'P'",
            })
            {
                var bm = PaneText.FixReason(recorded, Ms);
                Assert.False(string.IsNullOrWhiteSpace(bm));
                Assert.NotEqual(recorded, bm);
            }
        }
    }
}
