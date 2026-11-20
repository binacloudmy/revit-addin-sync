#nullable enable
// r18 (live Round L L15 8f1279fc on 0.0.155, Malay chat, "Buat jadual tiang struktur ikut jenis"): ONE schedule call drew three Steps
// rows, "Buat jadual tiang struktur ikut jenis 5.0s" + "Apa yang akan saya jalankan 5.0s" + "Menyemak kod dalam Revit... 3.x s", and a
// repeated write could never fold into "×2" because those sub-rows sat between the two calls. Also "Stop" and "AGENT ACTIVITY" stayed
// English while the rows were Malay. Now: one row per real step (the sub-rows fold into the call row), a repeat folds, and the running
// chrome follows the drafter's language as the rows do (G8 still makes the result card English).
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StepRowsR18Tests
    {
        private const string Intent = "Buat jadual tiang struktur ikut jenis";

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        /// <summary>The steps the router keeps for one write call, in its order (RunToolsAsync).</summary>
        private static ObservableCollection<ProgressStep> OneWriteCall(IDictionary<string, string> alias, string call = "call:1", string req = "req_01", bool done = true)
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "thinking", "", "Berfikir…", "", StepState.Done);
            AddWrite(steps, alias, call, req, done);
            return steps;
        }

        private static void AddWrite(ObservableCollection<ProgressStep> steps, IDictionary<string, string> alias, string call, string req, bool done)
        {
            var id = StepCollapse.AddTool(steps, alias, call, Intent, true);
            ProgressReducer.Apply(steps, "write:" + req, WritePreviewText.Phase, "Apa yang akan saya jalankan", "", StepState.Running);
            ProgressReducer.Apply(steps, "preview:" + req, "preview", "Menyemak kod dalam Revit…", "", StepState.Running);
            if (!done) return;
            ProgressReducer.Apply(steps, "preview:" + req, "preview", "", "", StepState.Done);
            ProgressReducer.Apply(steps, "write:" + req, WritePreviewText.Phase, "", "", StepState.Done);
            ProgressReducer.Apply(steps, StepCollapse.Resolve(alias, call), "tool", "", "", StepState.Done);
        }

        [Fact]
        public void OneWriteCall_IsOneStepsRow()
        {
            var steps = OneWriteCall(new Dictionary<string, string>());
            Assert.Equal(4, steps.Count);   // the turn's own list is unchanged (the running card and Stop line read it)
            var shown = StepCollapse.Shown(steps);
            Assert.Equal(new[] { "Berfikir…", Intent }, shown.Select(s => s.Label).ToArray());
        }

        [Fact]
        public void ARepeatedWriteCall_FoldsIntoTheRowAsTimesTwo()
        {
            var alias = new Dictionary<string, string>();
            var steps = OneWriteCall(alias);
            AddWrite(steps, alias, "call:2", "req_02", done: true);
            var shown = StepCollapse.Shown(steps);
            Assert.Equal(new[] { "Berfikir…", Intent + " ×2" }, shown.Select(s => s.Label).ToArray());
            Assert.Equal("call:1", StepCollapse.Resolve(alias, "call:2"));
        }

        [Fact]
        public void ARetryAfterAFailedWrite_IsStillItsOwnRow()
        {
            var alias = new Dictionary<string, string>();
            var steps = OneWriteCall(alias, done: false);
            ProgressReducer.Apply(steps, "call:1", "tool", "", "", StepState.Error);
            AddWrite(steps, alias, "call:2", "req_02", done: true);
            var shown = StepCollapse.Shown(steps);
            Assert.Equal(3, shown.Count);
            Assert.Equal("Mencuba semula: buat jadual tiang struktur ikut jenis", shown[2].Label);
        }

        [Fact]
        public void ASubRowWithNoCallRowBeforeIt_StaysVisible()
        {
            var steps = new List<ProgressStep>();
            var only = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(only, "preview:b1", "preview", "Previewing again", "", StepState.Running);
            Assert.Single(StepCollapse.Shown(only));
            Assert.Empty(StepCollapse.Shown(steps));
            Assert.Empty(StepCollapse.Shown(null));
        }

        [Fact]
        public void TheWorkingCard_ShowsTheCheckAsTheCallRowsLine_NotAsARow()
        {
            var steps = OneWriteCall(new Dictionary<string, string>(), done: false);
            var rows = RunningCard.Rows(steps);
            Assert.Single(rows);
            Assert.Equal(Intent, rows[0].Text);
            Assert.Equal("Menyemak kod dalam Revit…", rows[0].Note);   // the pane freezes during the check: its words stay, under the row
            // The Stop line still knows a write's values are on screen.
            Assert.True(RunningCard.PreviewOpen(steps));
        }

        [Fact]
        public void TheRunningChrome_FollowsTheDraftersLanguage_TheCardStaysEnglish()
        {
            Assert.Equal(ReceiptLanguage.Ms, PromptLanguage.Activity(Intent));
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Activity("Create a door schedule"));
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Card(PromptLanguage.Detect(Intent)));   // G8 unchanged
            Assert.Equal("Langkah 3", RunningText.StepLabel(new[] { new ProgressStep { StepId = "s", ModelStep = 3, State = StepState.Running } }, PromptLanguage.Activity(Intent)));
        }

        [Fact]
        public void ThePaneWiresTheLanguage_ToTheWorkingCardAndBothStepsLists()
        {
            var view = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Screens", "ChatView.xaml.cs"));
            Assert.Contains("PromptLanguage.Activity(last.Text)", view);                      // the live Working card and Steps list
            Assert.DoesNotContain("PromptLanguage.Card(PromptLanguage.Detect(last.Text))", view);
            Assert.Contains("Language = m.ActivityLanguage", view);                           // a finished turn's Steps list
            var vm = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "CopilotViewModel.cs"));
            Assert.Contains("ActivityLanguage = rr.Language,", vm);
            var activity = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Controls", "AgentActivityView.cs"));
            Assert.Contains("\"AKTIVITI EJEN\"", activity);
            Assert.Contains("StepCollapse.Shown(steps)", activity);
            var running = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Controls", "RunningCardView.cs"));
            Assert.Contains("ms ? \"Berhenti\" : \"Stop\"", running);
        }
    }
}
