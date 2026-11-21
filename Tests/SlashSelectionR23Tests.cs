#nullable enable
// r23 (0.0.166), Linear AI-448 sub-issues:
// AI-460 (Z8): the "/" button on an EMPTY message box put a "/" in it and no tools list opened. PromptBar set Editor.Text = "/" and only
//   then the caret; the text change ran the "/" detection while the caret was still 0, where no token ends. MentionInput.SeedSlash now
//   seeds "/", puts the caret after it and detects, as a typed "/". The with-draft path (373b099c OpenSlashOverDraft, live L14 a0fadee2)
//   and the "@" mention path are unchanged.
// AI-456 (live c7601a4a then e7898999, user 9001, 20 Nov): "Set Comments ... on all doors with Mark FD1" found and selected the 2 FD1
//   doors (snapshot after seq 21: 977255, 977284), then the set-parameter script's read pass ran with the turn's own (empty) selection
//   swapped in and nothing gave the doors back (snapshot after seq 29: none). The write was cancelled, the card said "selected 2", and
//   "on the selected doors" found none. The swap stays (code sees the drafter's selection, live 14 Nov); a read run and a plan now give
//   back what Revit held before it, and so does the write engine after a preview's rollback (a Cancel at the card) and after a commit
//   that changed or made none of those elements; a commit that wrote them ends on the turn's selection (r13 2acc6aaf, live fb713fc2).
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Code;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SlashSelectionR23Tests
    {
        private static string Src(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
        }

        private static string Body(string src, string signature)
        {
            var at = src.IndexOf(signature, System.StringComparison.Ordinal);
            Assert.True(at >= 0, signature);
            return src.Substring(at, src.IndexOf("\n        }\n", at, System.StringComparison.Ordinal) - at);
        }

        private static bool All(long id) => true;

        // ─── AI-460: the "/" button ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ALoneSlash_IsAToken_OnlyWithTheCaretAfterIt()
        {
            Assert.Equal(-1, SlashTrigger.Find("/", 0));   // the old empty-box path: the text set ran detection at caret 0
            Assert.Equal(0, SlashTrigger.Find("/", 1));    // SeedSlash: caret after the "/"
            Assert.Equal(0, SlashTrigger.Find("/cou", 4));
        }

        [Fact]
        public void TheSlashRule_IsUnchanged_ForDraftsPathsAndProse()
        {
            Assert.Equal(14, SlashTrigger.Find("door schedule /", 15));
            Assert.Equal(-1, SlashTrigger.Find("and/or", 6));
            Assert.Equal(-1, SlashTrigger.Find("/count doors", 12));   // a space after the token: no longer the command word
            Assert.Equal(-1, SlashTrigger.Find("", 0));
            Assert.Equal(0, SlashTrigger.Find("/", 9));                // a caret past the end is clamped, never throws
        }

        [Fact]
        public void TheEmptyBox_SeedsTheSlash_ThenDetects_AndADraft_StillOpensOverIt()
        {
            var bar = Body(Src("UI", "Copilot", "Controls", "PromptBar.xaml.cs"), "public void OpenCommandPalette()");
            Assert.Contains("if (string.IsNullOrWhiteSpace(Input.Editor.Text)) Input.SeedSlash();", bar);
            Assert.Contains("else Input.OpenSlashOverDraft();", bar);
            Assert.Contains("if (!Input.HasSlashPalette) { Input.Editor.Focus(); return; }", bar);   // no lone "/" where no list is attached
            Assert.DoesNotContain("Input.Editor.Text = \"/\";", bar);

            var input = Src("UI", "Copilot", "Controls", "MentionInput.xaml.cs");
            var seed = Body(input, "public void SeedSlash()");
            var text = seed.IndexOf("Editor.Text = \"/\";", System.StringComparison.Ordinal);
            var caret = seed.IndexOf("Editor.CaretIndex = 1;", System.StringComparison.Ordinal);
            var detect = seed.IndexOf("DetectToken();", System.StringComparison.Ordinal);
            Assert.True(text >= 0 && text < caret && caret < detect);
            Assert.Contains("private static int LastSlashTrigger(string text, int caret) => SlashTrigger.Find(text, caret);", input);
            // The "@" mention path is untouched: the same token finder, the same picker.
            var detectBody = Body(input, "private void DetectToken()");
            Assert.Contains("int at = Model.MentionToken.Find(text, caret, out string query);", detectBody);
            Assert.Contains("BuildPicker(query);", detectBody);
            // A pick strips the "/query" from the slash index (0 for a seeded "/") as for a typed one.
            Assert.Contains("Editor.Select(_slashIndex, caret - _slashIndex);", Body(input, "private void OnSlashToolPicked(SlashTool tool)"));
        }

        // ─── AI-456: the selection a find made survives a read run in the same turn ──────────────────────────────────────

        [Fact]
        public void AfterARead_TheFindsSelection_IsGivenBack()
        {
            // c7601a4a: the find held the 2 FD1 doors, the swap put the turn's empty selection in, the read left it empty.
            var back = TurnSelection.GiveBack(new long[] { 977255, 977284 }, new long[0], new long[0], All);
            Assert.Equal(new long[] { 977255, 977284 }, back);
            // The drafter's 11 at the turn start, the find's 2 after it: the read sees the 11, Revit then holds the 2 again.
            var eleven = Enumerable.Range(1, 11).Select(i => (long)i).ToArray();
            Assert.Equal(new long[] { 977255, 977284 }, TurnSelection.GiveBack(new long[] { 977255, 977284 }, eleven, eleven.Reverse(), All));
        }

        [Fact]
        public void NothingIsGivenBack_OverTheCodesOrTheDraftersOwnChoice()
        {
            Assert.Null(TurnSelection.GiveBack(new long[] { 1, 2 }, new long[0], new long[] { 7 }, All));          // the code selected 7
            Assert.Null(TurnSelection.GiveBack(new long[] { 1, 2 }, new long[] { 5 }, new long[0], All));          // something emptied it since
            Assert.Null(TurnSelection.GiveBack(new long[0], new long[] { 5, 6 }, new long[] { 6, 5 }, All));       // nothing held before: the drafter's stays
            Assert.Null(TurnSelection.GiveBack(new long[] { 1, 2 }, new long[0], new long[0], id => false));       // all gone
            Assert.Null(TurnSelection.GiveBack(new long[] { 5 }, new long[] { 5 }, new long[] { 5 }, All));        // already as it was
            Assert.Null(TurnSelection.GiveBack(null, null, null, All));
            Assert.Equal(new long[] { 2 }, TurnSelection.GiveBack(new long[] { 1, 2, 2 }, new long[0], new long[0], id => id != 1));
        }

        [Fact]
        public void AfterACommit_TheSelectionComesBack_OnlyWhenTheWriteLeftThoseElementsAlone()
        {
            // r13 (fb713fc2): a write turn never ends with its own targets selected. A commit that changed the FD1 doors: leave it.
            Assert.Null(TurnSelection.GiveBack(new long[] { 977255, 977284 }, new long[0], new long[0], All, written: new long[] { 977284 }));
            // A commit that changed or made other elements (a schedule, the walls): the find's doors are selected again.
            Assert.Equal(new long[] { 977255, 977284 }, TurnSelection.GiveBack(new long[] { 977255, 977284 }, new long[0], new long[0], All, written: new long[] { 3431003 }));
            Assert.Equal(new long[] { 977255, 977284 }, TurnSelection.GiveBack(new long[] { 977255, 977284 }, new long[0], new long[0], All, written: new long[0]));
        }

        [Fact]
        public void TheWriteEngine_GivesBack_AfterThePreviewsRollback_AndAfterTheCommit_AndForgetsTheSwapAtTheEnd()
        {
            var engine = Src("BinaVibe", "Copilot", "Writes", "RevitWriteEngine.cs");
            var rollback = engine.IndexOf("RollBackIfOpen(group);\n                    group.Dispose();\n                    st.Mark(\"rollback\");", System.StringComparison.Ordinal);
            Assert.True(rollback >= 0);
            Assert.True(engine.IndexOf("Code.RevitCodeHost.GiveBackSelection(doc, uidoc);", rollback, System.StringComparison.Ordinal) > rollback);
            const string commit = "Code.RevitCodeHost.GiveBackSelection(doc!, uidoc, envelopes.Where(e => e != null).SelectMany(TouchedIds).Concat(builder.AddedIds).ToList());";
            var at = engine.IndexOf(commit, System.StringComparison.Ordinal);
            Assert.True(at > engine.IndexOf("AfterCommitRunner.Run(", System.StringComparison.Ordinal));               // after the code's own UI work
            Assert.True(at < engine.IndexOf("if (!noop) Highlight(uidoc!, doc!, envelopes, builder.AddedIds, select: false);", System.StringComparison.Ordinal));   // measured after
            Assert.Contains("try { return PreviewBatch(app, batch); } finally { Code.RevitCodeHost.ForgetSelectionSwap(); }", engine);
            Assert.Contains("try { return CommitBatch(app, batch, preview, planHash); } finally { Code.RevitCodeHost.ForgetSelectionSwap(); }", engine);
        }

        [Fact]
        public void TheTool_GivesBackAfterReadsAndPlans_NeverInsideAWriteRun()
        {
            var tool = Src("BinaVibe", "Copilot", "Tools", "RunRevitCodeTool.cs");
            const string giveBack = "finally { RevitCodeHost.GiveBackSelection(ctx.Doc, ctx.UiDoc); }";
            Assert.Equal(3, tool.Split(new[] { giveBack }, System.StringSplitOptions.None).Length - 1);   // Run, Plan, Execute (read mode)
            var plan = tool.Substring(tool.IndexOf("public WritePlan Plan(", System.StringComparison.Ordinal));
            Assert.True(plan.IndexOf("RevitCodeHost.Binding(ctx.Doc, ctx.UiDoc)", System.StringComparison.Ordinal) < plan.IndexOf(giveBack, System.StringComparison.Ordinal));
            var execute = tool.Substring(tool.IndexOf("public Envelope Execute(", System.StringComparison.Ordinal));
            var write = execute.IndexOf("RevitCodeHost.Write(", System.StringComparison.Ordinal);
            Assert.True(execute.IndexOf(giveBack, System.StringComparison.Ordinal) < write);
            Assert.DoesNotContain("GiveBackSelection", execute.Substring(write));

            var host = Src("BinaVibe", "Copilot", "Code", "RevitCodeHost.cs");
            var use = Body(host, "public static void UseTurnSelection(");
            // Code that never reads the selection keeps an earlier call's swap (a batch of two runs); code that reads it starts afresh.
            Assert.True(use.IndexOf("UsesSelection != true) return;", System.StringComparison.Ordinal) < use.IndexOf("_swap = null;", System.StringComparison.Ordinal));
            Assert.Contains("if (decision.Restore) _swap = new SelectionSwap(live, decision.Ids);", use);
            var give = Body(host, "public static void GiveBackSelection(");
            Assert.Contains("TurnSelection.GiveBack(swap.Before, swap.SwappedIn, live,", give);
            Assert.Contains("catch (Exception", give);
            // The code's own select request wins: it drops the swap before it sets the selection.
            Assert.Contains("if (!plan.HasSelection) return;\n                _swap = null;", host);
            Assert.Contains("if (!plan.HasReferences) return;\n                _swap = null;", host);
        }

        [Fact]
        public void TheWriteTurnRule_OfR13_StillHolds()
        {
            var engine = Src("BinaVibe", "Copilot", "Writes", "RevitWriteEngine.cs");
            Assert.Contains("if (!noop) Highlight(uidoc!, doc!, envelopes, builder.AddedIds, select: false);", engine);
            Assert.False(new RevitWebAppSync.UI.Copilot.Highlights.HighlightSet().SelectsAtTheEnd);
        }
    }
}
