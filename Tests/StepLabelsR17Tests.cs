#nullable enable
// r17, tester 20 Nov: "the AI WORK PROGRESS shows 'Running Revit code' several times while the copilot works". Rows now say what the step
// does (from the call's arguments), the same label twice in a row is one row with a counter, a retry after a failed row says "Retrying: ...",
// and the server's generic "Running the code in Revit" row gives way to the tool rows. Malay in a Malay chat.
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StepLabelsR17Tests
    {
        private static ToolCallDto Call(string tool, string json) =>
            new ToolCallDto { CallId = "c", Tool = tool, Args = JsonDocument.Parse(json).RootElement.Clone() };

        [Fact]
        public void For_SaysWhatTheStepDoes()
        {
            Assert.Equal("Hide all tags in the active view", StepLabels.For(Call(ToolNames.RunRevitCode, "{\"intent\":\"hide all tags in the active view\",\"code\":\"x\"}"), false));
            Assert.Equal("Running the stored job: change type", StepLabels.For(Call(ToolNames.RunRevitCode, "{\"playbook\":\"change-type\"}"), false));
            Assert.Equal("Menjalankan kerja tersimpan: circuits", StepLabels.For(Call(ToolNames.RunRevitCode, "{\"playbook\":\"circuits\"}"), true));
            Assert.Equal("Finding Doors on Aras 01", StepLabels.For(Call(ToolNames.FindElements, "{\"queries\":[{\"category\":\"Doors\",\"level\":\"Aras 01\"}]}"), false));
            Assert.Equal("Mencari Doors di Aras 01", StepLabels.For(Call(ToolNames.FindElements, "{\"queries\":[{\"category\":\"Doors\",\"level\":\"Aras 01\"}]}"), true));
            Assert.Equal("Taking a picture of Level 1", StepLabels.For(Call(ToolNames.Look, "{\"target\":\"view\",\"view\":\"Level 1\"}"), false));
            Assert.Equal("Writing Comments", StepLabels.For(Call(ToolNames.SetParameters, "{\"param\":\"Comments\",\"value\":\"x\"}"), false));
            Assert.Equal("Reading the views and levels", StepLabels.For(Call(ToolNames.ModelOverview, "{\"sections\":[\"views\",\"levels\"]}"), false));
            Assert.Equal("Running Revit code", StepLabels.For(Call(ToolNames.RunRevitCode, "{\"code\":\"x\"}"), false));   // nothing more to say
        }

        [Fact]
        public void For_ALongIntent_IsCutAtAWord()
        {
            var label = StepLabels.For(Call(ToolNames.RunRevitCode, "{\"intent\":\"" + new string('a', 10) + " " + string.Join(" ", new string[20].Populate("word")) + "\"}"), false);
            Assert.True(label.Length <= 70);
            Assert.EndsWith("…", label);
        }

        [Fact]
        public void AddTool_TheSameLabelTwice_IsOneRowWithACounter()
        {
            var steps = new ObservableCollection<ProgressStep>();
            var alias = new Dictionary<string, string>();
            Assert.Equal("call:1", StepCollapse.AddTool(steps, alias, "call:1", "Running Revit code", false));
            ProgressReducer.Apply(steps, "call:1", "tool", "", "", StepState.Done);
            Assert.Equal("call:1", StepCollapse.AddTool(steps, alias, "call:2", "Running Revit code", false));
            Assert.Single(steps);
            Assert.Equal("Running Revit code ×2", steps[0].Label);
            Assert.Equal(StepState.Running, steps[0].State);
            Assert.Equal("call:1", StepCollapse.Resolve(alias, "call:2"));
            ProgressReducer.Apply(steps, StepCollapse.Resolve(alias, "call:2"), "tool", "", "", StepState.Done);
            StepCollapse.AddTool(steps, alias, "call:3", "Running Revit code", false);
            Assert.Equal("Running Revit code ×3", steps[0].Label);
        }

        [Fact]
        public void AddTool_AfterAFailedRow_SaysRetrying()
        {
            var steps = new ObservableCollection<ProgressStep>();
            var alias = new Dictionary<string, string>();
            StepCollapse.AddTool(steps, alias, "call:1", "Hide all tags in the active view", false);
            ProgressReducer.Apply(steps, "call:1", "tool", "", "", StepState.Error);
            StepCollapse.AddTool(steps, alias, "call:2", "Hide all tags in the active view", false);
            Assert.Equal(2, steps.Count);
            Assert.Equal("Retrying: hide all tags in the active view", steps[1].Label);
            StepCollapse.AddTool(steps, alias, "call:3", "Menyembunyikan tag", true);
            Assert.Equal("Menyembunyikan tag", steps[2].Label);   // other words: a new row
        }

        [Fact]
        public void DropServerRun_TheGenericServerRowGivesWayToTheToolRows()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "p:4", "awaiting_confirm", "Running the code in Revit", "", StepState.Done);
            StepCollapse.DropServerRun(steps);
            Assert.Empty(steps);
            ProgressReducer.Apply(steps, "p:5", "thinking", "Thinking", "", StepState.Done);
            StepCollapse.DropServerRun(steps);
            Assert.Single(steps);   // only the awaiting_confirm row goes
        }

        [Fact]
        public void Server_MalayForTheServersOwnTexts()
        {
            Assert.Equal("Berfikir", StepLabels.Server("Thinking", true));
            Assert.Equal("Menggunakan perubahan dalam Revit", StepLabels.Server("Applying the change in Revit", true));
            Assert.Equal("Thinking", StepLabels.Server("Thinking", false));
        }
    }

    internal static class ArrayFill
    {
        public static string[] Populate(this string[] a, string v) { for (var i = 0; i < a.Length; i++) a[i] = v; return a; }
    }
}
