#nullable enable
// Live 0.0.187 ("select all the doors in this view and isolate them"): a find selected the 2 doors, run_revit_code isolated
// them, and Revit dropped the selection with the isolate; the reply still said the doors were selected. The decision is
// Revit-free (TurnSelection.KeepAfterIsolate); the engine call is pinned by its source here.
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class IsolateSelectionTests
    {
        private static ToolCallDto Code(string code, string tool = ToolNames.RunRevitCode) => new ToolCallDto
        {
            Tool = tool,
            Args = JsonDocument.Parse(JsonSerializer.Serialize(new { code, intent = "isolate" })).RootElement.Clone(),
        };

        private const string Isolate = "view.IsolateElementsTemporary(new List<ElementId>{ new ElementId(977255L), new ElementId(977284L) }); Print(\"ok\");";
        private static bool All(long id) => true;

        [Fact]
        public void AnIsolateThatEmptiedTheSelection_GivesTheEarlierIdsBack()
        {
            var back = TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, new long[] { 977255, 977284 }, new long[0], All);
            Assert.Equal(new long[] { 977255, 977284 }, back);
            // Every isolate call name Revit has, singular and plural.
            foreach (var call in new[] { "view.IsolateElementTemporary(id);", "view.IsolateCategoriesTemporary(cats);", "view.IsolateCategoryTemporary (cat);" })
                Assert.NotNull(TurnSelection.KeepAfterIsolate(new[] { Code(call) }, new long[] { 1 }, new long[0], All));
        }

        [Fact]
        public void NoIsolate_LeavesTheSelectionAlone()
        {
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code("view.HideElements(ids);") }, new long[] { 1, 2 }, new long[0], All));
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code("view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);") }, new long[] { 1 }, new long[0], All));
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code(Isolate, ToolNames.SetParameters) }, new long[] { 1 }, new long[0], All));   // only run_revit_code's code
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { new ToolCallDto { Tool = ToolNames.RunRevitCode } }, new long[] { 1 }, new long[0], All));   // no args
            Assert.Null(TurnSelection.KeepAfterIsolate(null, new long[] { 1 }, new long[0], All));
        }

        [Fact]
        public void ASelectionThatIsBackOrChosenByTheCode_IsNotSetAgain()
        {
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, new long[] { 1, 2 }, new long[] { 2, 1 }, All));   // already back
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, new long[] { 1, 2 }, new long[] { 7 }, All));      // the code's own
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, new long[0], new long[0], All));                   // nothing held before: nothing is selected
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, null, null, All));
        }

        [Fact]
        public void IdsThatNoLongerExist_AreDropped()
        {
            Assert.Equal(new long[] { 2 }, TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, new long[] { 1, 2, 2 }, new long[0], id => id != 1));
            Assert.Null(TurnSelection.KeepAfterIsolate(new[] { Code(Isolate) }, new long[] { 1, 2 }, new long[0], id => false));
        }

        [Fact]
        public void BothWriteRequests_GiveTheSelectionBack_AfterTheSwapAndBeforeTheRefresh()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var engine = File.ReadAllText(Path.Combine(dir!.FullName, "BinaVibe", "Copilot", "Writes", "RevitWriteEngine.cs")).Replace("\r\n", "\n");
            var commit = engine.Substring(engine.IndexOf("public Task<CommitOutcome> CommitAsync(", System.StringComparison.Ordinal));
            commit = commit.Substring(0, commit.IndexOf("}, ct);", System.StringComparison.Ordinal));
            var forget = commit.IndexOf("Code.RevitCodeHost.ForgetSelectionSwap();", System.StringComparison.Ordinal);
            var keep = commit.IndexOf("KeepSelectionAfterIsolate(app, batch, selectedAtStart);", System.StringComparison.Ordinal);
            var refresh = commit.IndexOf("RefreshCommandState(app, batch.TurnId, selectedAtStart);", System.StringComparison.Ordinal);
            Assert.True(forget > 0 && keep > forget && refresh > keep);
            Assert.Contains("Code.TurnSelection.KeepAfterIsolate(batch.Calls, selectedAtStart, SelectedIds(app),", engine);
        }
    }
}
