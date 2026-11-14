#nullable enable
// The selection run_revit_code's code sees is the drafter's, as it was when they sent the message (live 14 Nov: three walls
// were selected and the turn's snapshot listed them, yet a stored dimension script read an empty uidoc.Selection). The
// decision is Revit-free (TurnSelection); the Revit call (RevitCodeHost.UseTurnSelection) is pinned by its source here.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TurnSelectionTests
    {
        private static SnapshotModel Turn(int count, params long[] ids) => new SnapshotModel
        {
            Doc = new SnapshotDoc { Title = "sample-project" },
            Selection = new SnapshotSelection { Count = count, Ids = ids.ToList() },
        };

        private static bool All(long id) => true;

        [Fact]
        public void EmptiedSelection_IsPutBack_ToTheTurnsIds()
        {
            var d = TurnSelection.Decide(Turn(3, 101, 102, 103), new long[0], All);
            Assert.True(d.Restore);
            Assert.Equal(new long[] { 101, 102, 103 }, d.Ids);
        }

        [Fact]
        public void AFindHighlightOfTheSameTurn_IsReplaced_ByTheDraftersSelection()
        {
            var d = TurnSelection.Decide(Turn(1, 7), new long[] { 900, 901, 902 }, All);
            Assert.True(d.Restore);
            Assert.Equal(new long[] { 7 }, d.Ids);
        }

        [Fact]
        public void NothingSelectedWhenSent_ClearsWhatACallSelectedSince()
        {
            var d = TurnSelection.Decide(Turn(0), new long[] { 900, 901 }, All);
            Assert.True(d.Restore);
            Assert.Empty(d.Ids);
        }

        [Fact]
        public void TheSameSelection_InAnotherOrder_IsLeftAlone()
        {
            Assert.False(TurnSelection.Decide(Turn(3, 101, 102, 103), new long[] { 103, 101, 102 }, All).Restore);
            Assert.False(TurnSelection.Decide(Turn(0), new long[0], All).Restore);
        }

        [Fact]
        public void OnlyIdsThatStillExist_ArePutBack()
        {
            var d = TurnSelection.Decide(Turn(3, 101, 102, 103), new long[0], id => id != 102);
            Assert.True(d.Restore);
            Assert.Equal(new long[] { 101, 103 }, d.Ids);
            // What exists of it is already selected: nothing to do.
            Assert.False(TurnSelection.Decide(Turn(3, 101, 102, 103), new long[] { 101, 103 }, id => id != 102).Restore);
        }

        [Fact]
        public void ALargeSelection_TheSnapshotCut_IsNeverShrunk()
        {
            var listed = Enumerable.Range(1, SnapshotLimits.MaxSelectionIds).Select(i => (long)i).ToArray();
            var d = TurnSelection.Decide(Turn(80, listed), new long[0], All);
            Assert.False(d.Restore);
        }

        [Fact]
        public void NoTurn_NoDocument_OrAStaleSnapshot_LeavesRevitAlone()
        {
            Assert.False(TurnSelection.Decide(null, new long[] { 5 }, All).Restore);
            Assert.False(TurnSelection.Decide(new SnapshotModel { Selection = new SnapshotSelection { Count = 1, Ids = new List<long> { 1 } } }, new long[] { 5 }, All).Restore);
            var stale = Turn(1, 1); stale.Stale = true;
            Assert.False(TurnSelection.Decide(stale, new long[] { 5 }, All).Restore);
            var none = Turn(0); none.Selection = null!;
            Assert.False(TurnSelection.Decide(none, new long[] { 5 }, All).Restore);
        }

        // ---- inspect and check: the same selection, read without selecting anything ----

        [Fact]
        public void AReadTool_ReadsTheTurnsIds_WhenAnotherToolLeftRevitWithAnotherSelection()
        {
            // The drafter selected three walls; a find_elements of the same turn then selected 40 doors.
            var ids = TurnSelection.Ids(Turn(3, 101, 102, 103), Enumerable.Range(900, 40).Select(i => (long)i), All);
            Assert.Equal(new long[] { 101, 102, 103 }, ids);
            // Nothing was selected when the message was sent: the tool's own highlight is not the drafter's selection.
            Assert.Empty(TurnSelection.Ids(Turn(0), new long[] { 900, 901 }, All)!);
        }

        [Fact]
        public void AReadTool_ReadsRevitsSelection_WhenItIsStillTheTurns_OrTheTurnCannotSay()
        {
            Assert.Null(TurnSelection.Ids(Turn(3, 101, 102, 103), new long[] { 102, 103, 101 }, All));
            Assert.Null(TurnSelection.Ids(null, new long[] { 5 }, All));
            var listed = Enumerable.Range(1, SnapshotLimits.MaxSelectionIds).Select(i => (long)i).ToArray();
            Assert.Null(TurnSelection.Ids(Turn(80, listed), new long[0], All));
        }

        [Fact]
        public void InspectAndCheck_TakeTheSelectionFromTheTurn_AndSelectNothing()
        {
            var inspect = Src("BinaVibe/Copilot/Read/InspectExecutor.cs");
            var at = inspect.IndexOf("private static List<ElementRef> SelectedRefs(", System.StringComparison.Ordinal);
            var body = inspect.Substring(at, inspect.IndexOf("\n        }\n", at, System.StringComparison.Ordinal) - at);
            Assert.Contains("TurnSelection.Ids(ctx.TurnSnapshot, live", body);
            Assert.True(body.IndexOf("if (turn != null) return", System.StringComparison.Ordinal)
                        < body.IndexOf("foreach (var id in live)", System.StringComparison.Ordinal));
            Assert.DoesNotContain("SetElementIds", inspect);

            var runner = Src("BinaVibe/Copilot/Check/CheckRunner.cs");
            Assert.Contains("TurnSelection.Ids(turn, live,", runner);
            Assert.DoesNotContain("SetElementIds", runner);
            Assert.Contains("new CheckRunner(ctx.Doc, ctx.UiDoc, parsed.Scope, ctx.TurnSnapshot);", Src("BinaVibe/Copilot/Tools/CheckTool.cs"));
        }

        // ---- the Revit side, by its source (Revit is not loaded in the Mac harness) ----

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        // A Windows checkout may hold CRLF line ends: the searches below are written for LF.
        private static string Src(string path) => File.ReadAllText(Path.Combine(Root(), path)).Replace("\r\n", "\n");

        [Fact]
        public void TheTool_PutsTheTurnsSelectionBack_BeforeThePlanBindsIt_AndBeforeEveryRun()
        {
            var s = Src("BinaVibe/Copilot/Tools/RunRevitCodeTool.cs");
            const string use = "RevitCodeHost.UseTurnSelection(ctx.Doc, ctx.UiDoc, ctx.TurnSnapshot, prepared);";
            // Run (a read request), Plan (before Binding hashes the selection), Execute (preview, commit and read mode).
            Assert.Equal(3, s.Split(new[] { use }, System.StringSplitOptions.None).Length - 1);
            Assert.True(s.IndexOf(use, s.IndexOf("public WritePlan Plan(", System.StringComparison.Ordinal), System.StringComparison.Ordinal)
                        < s.IndexOf("RevitCodeHost.Binding(ctx.Doc, ctx.UiDoc)", System.StringComparison.Ordinal));
            var execute = s.Substring(s.IndexOf("public Envelope Execute(", System.StringComparison.Ordinal));
            Assert.True(execute.IndexOf(use, System.StringComparison.Ordinal) < execute.IndexOf("RevitCodeHost.Read(", System.StringComparison.Ordinal));
            Assert.True(execute.IndexOf(use, System.StringComparison.Ordinal) < execute.IndexOf("RevitCodeHost.Write(", System.StringComparison.Ordinal));
        }

        [Fact]
        public void TheHost_TouchesTheSelection_OnlyForCodeThatUsesIt_AndNeverThrows()
        {
            var s = Src("BinaVibe/Copilot/Code/RevitCodeHost.cs");
            var at = s.IndexOf("public static void UseTurnSelection(", System.StringComparison.Ordinal);
            Assert.True(at >= 0);
            var body = s.Substring(at, s.IndexOf("\n        }\n", at, System.StringComparison.Ordinal) - at);
            Assert.Contains("UsesSelection != true) return;", body);
            Assert.Contains("TurnSelection.Decide(turn, live,", body);
            Assert.Contains("if (decision.Restore) uidoc.Selection.SetElementIds(", body);
            Assert.Contains("catch (Exception", body);
        }
    }
}
