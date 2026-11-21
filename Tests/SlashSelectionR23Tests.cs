#nullable enable
// r23 (0.0.166), Linear AI-448 sub-issues (AI-460, AI-456, and AI-464's running composer):
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
using System.Collections.Generic;
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

        // ─── AI-464 (live 3c11223a on 0.0.165): the running composer in a Malay chat ─────────────────────────────────────

        [Fact]
        public void TheRunningComposer_ReadsMalay_InAMalayChat_AndEnglishIsUnchanged()
        {
            Assert.Equal(RunningText.QueuePlaceholder, RunningText.QueuePlaceholderFor(ReceiptLanguage.En));
            Assert.Equal(RunningText.QueueNote, RunningText.QueueNoteFor(ReceiptLanguage.En));
            Assert.Equal(RunningText.QueueHint, RunningText.QueueHintFor(ReceiptLanguage.En));
            Assert.Equal("Taip langkah seterusnya. Ia dijalankan selepas yang ini selesai.", RunningText.QueuePlaceholderFor(ReceiptLanguage.Ms));
            Assert.Equal("Beratur selepas larian ini", RunningText.QueueNoteFor(ReceiptLanguage.Ms));
            foreach (var t in new[] { RunningText.QueuePlaceholderFor(ReceiptLanguage.Ms), RunningText.QueueNoteFor(ReceiptLanguage.Ms), RunningText.QueueHintFor(ReceiptLanguage.Ms) })
            {
                Assert.DoesNotContain("Type", t);
                Assert.DoesNotContain("Queues", t);
            }
        }

        [Fact]
        public void TheScopeChipAndTheRunningButtonTips_FollowTheRunLanguage()
        {
            Assert.Equal("{PTBR_PELAN LANTAI ARAS 01} · 2 dipilih", RunningText.ScopeText("PTBR_PELAN LANTAI ARAS 01", 2, ReceiptLanguage.Ms));
            Assert.Equal("{PTBR_PELAN LANTAI ARAS 01} · 2 selected", RunningText.ScopeText("PTBR_PELAN LANTAI ARAS 01", 2, ReceiptLanguage.En));
            Assert.Equal(RunningText.ScopeText("{3D}", 52), RunningText.ScopeText("{3D}", 52, ReceiptLanguage.En));   // the English two-argument form is unchanged
            Assert.Equal("{3D}", RunningText.ScopeText("3D", 0, ReceiptLanguage.Ms));
            Assert.Equal("Berhenti", RunningText.StopTip(ReceiptLanguage.Ms));
            Assert.Equal("Stop", RunningText.StopTip(ReceiptLanguage.En));
            Assert.Equal("Beratur", RunningText.QueueTip(ReceiptLanguage.Ms));
            Assert.Equal("Queue", RunningText.QueueTip(ReceiptLanguage.En));
            var vm = Src("UI", "Copilot", "CopilotViewModel.cs");
            Assert.Contains("RunningText.ScopeText(snap?.View?.Name, snap?.Selection?.Count ?? 0, router.ChatLanguage);", vm);
            var bar = Body(Src("UI", "Copilot", "Controls", "PromptBar.xaml.cs"), "private void ApplyRunLanguage()");
            Assert.Contains("_queueBtn.ToolTip = RunningText.QueueTip(_runLanguage);", bar);
            Assert.Contains("SendBtn.ToolTip = RunningText.StopTip(_runLanguage);", bar);
        }

        [Fact]
        public void TheToolsList_IsNeverEmpty_WhenThePromptLibraryOrTheCatalogueIsUnavailable()
        {
            // The "/" list is ToolCatalog.All (saved commands, then the curated tier), not the Commands tab's prompt library. With the
            // backend catalogue unavailable (null or no live row) it is the built-in nine.
            var builtIn = ToolCatalog.CuratedFrom(null);
            Assert.Equal(new[] { "create", "delete", "change", "rename", "open-view", "count", "clone", "place", "audit" }, builtIn.Select(t => t.Id));
            Assert.Same(ToolCatalog.Curated, ToolCatalog.CuratedFrom(new List<RevitWebAppSync.Models.CatalogCommandDto>()));
            Assert.Contains(ToolCatalog.All, t => t.Name == "Count / List");
            Assert.True(ToolCatalog.All.Count >= 9);
        }

        [Fact]
        public void TheChat_SetsTheRunLanguage_AsItDoesForTheWorkingCard()
        {
            var bar = Src("UI", "Copilot", "Controls", "PromptBar.xaml.cs");
            var apply = Body(bar, "private void ApplyRunLanguage()");
            Assert.Contains("Input.PlaceholderText = Busy ? RunningText.QueuePlaceholderFor(_runLanguage) : Placeholder;", apply);
            Assert.Contains("_queueNote.Text = RunningText.QueueNoteFor(_runLanguage);", apply);
            Assert.Contains("pb.ApplyRunLanguage();", Body(bar, "private static void OnBusyChanged("));
            Assert.Contains("QueueHint.Text = RunningText.QueueHintFor(_runLanguage);", bar);
            Assert.DoesNotContain("busy ? RunningText.QueuePlaceholder :", bar);
            var chat = Src("UI", "Copilot", "Screens", "ChatView.xaml.cs");
            Assert.Contains("if (Prompt != null && Vm != null && Vm.IsSending) Prompt.RunLanguage = LiveLanguage();", chat);
            Assert.Contains("_activityView.Language = LiveLanguage();", chat);   // the same source as the Working card's "Langkah"
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
            // A commit that did not commit changed nothing: it gives back as a preview does, then forgets.
            var commitAsync = engine.Substring(engine.IndexOf("public Task<CommitOutcome> CommitAsync(", System.StringComparison.Ordinal));
            commitAsync = commitAsync.Substring(0, commitAsync.IndexOf("}, ct);", System.StringComparison.Ordinal));
            Assert.Contains("if (outcome?.Kind != CommitKind.Committed) Code.RevitCodeHost.GiveBackSelection(app.ActiveUIDocument?.Document, app.ActiveUIDocument);", commitAsync);
            Assert.True(commitAsync.IndexOf("GiveBackSelection", System.StringComparison.Ordinal) < commitAsync.IndexOf("ForgetSelectionSwap", System.StringComparison.Ordinal));
        }

        // ─── the swap ledger, as the tool and the engine drive it (audit of r23) ─────────────────────────────────────────

        private static readonly long[] Doors = { 977255, 977284 };

        [Fact]
        public void ABatchOfTwoSelectionRuns_KeepsTheFirstSwap_SoACancelAfterThePreviewGivesTheDoorsBack()
        {
            var ledger = new SelectionSwapLedger();
            // Preview: call 0 finds the find's doors selected, swaps in the turn's (empty) selection and records it.
            ledger.Record(Doors, null, new long[0]);
            // Call 1 finds the turn's selection already in place: UseTurnSelection records nothing, the first swap stays open.
            Assert.True(ledger.Open);
            // After the rollback (Cancel at the card next): the doors come back.
            var back = ledger.Take(new long[0], All);
            Assert.Equal(Doors, back!.Ids);
            Assert.Null(back.References);
            Assert.False(ledger.Open);                       // consumed: a second give-back does nothing
            Assert.Null(ledger.Take(new long[0], All));
        }

        [Fact]
        public void TheSwapRecordsWhatRevitKept_NotWhatWasAsked()
        {
            // The turn listed 3 ids but Revit kept 2 after the set (UiSelectionReadBack, c61a1d44): the give-back compares with the 2.
            var ledger = new SelectionSwapLedger();
            ledger.Record(Doors, null, new long[] { 101, 102 });
            Assert.Equal(Doors, ledger.Take(new long[] { 102, 101 }, All)!.Ids);
            // Had it recorded the 3 asked for, the same live selection would not match and nothing would come back.
            Assert.Null(TurnSelection.GiveBack(Doors, new long[] { 101, 102, 103 }, new long[] { 101, 102 }, All));
        }

        [Fact]
        public void LinkedPicks_ComeBackAsReferences_NeverAsTheLinkInstance()
        {
            var ledger = new SelectionSwapLedger();
            var refs = new[] { "host-door-stable", "link-instance:0:RVTLINK/linked-door-stable" };
            ledger.Record(new long[] { 977255, 5550001 /* the RevitLinkInstance GetElementIds reports */ }, refs, new long[0]);
            var back = ledger.Take(new long[0], All);
            Assert.Equal(refs, back!.References);
            Assert.Empty(back.Ids);                          // no SetElementIds of the link instance
            // The same guards hold: changed since, or written by the commit, nothing comes back.
            ledger.Record(new long[] { 977255 }, refs, new long[0]);
            Assert.Null(ledger.Take(new long[] { 42 }, All));
            ledger.Record(new long[] { 977255 }, refs, new long[0]);
            Assert.Null(ledger.Take(new long[0], All, written: new long[] { 977255 }));
        }

        [Fact]
        public void ForgetOrTheCodesOwnSelect_ClosesTheSwap()
        {
            var ledger = new SelectionSwapLedger();
            ledger.Record(Doors, null, new long[0]);
            ledger.Forget();
            Assert.False(ledger.Open);
            Assert.Null(ledger.Take(new long[0], All));
            ledger.Record(Doors, null, new long[0]);
            Assert.Null(ledger.Take(new long[0], All, written: new long[] { 977255 }));   // a commit wrote a door: r13
            ledger.Record(Doors, null, new long[0]);
            Assert.Null(ledger.Take(new long[] { 7 }, All));                              // the code selected 7 since
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
            // A swap is recorded only when this call set the selection, with what Revit kept (read back after the set); a call that
            // found the turn's selection in place never resets an earlier call's swap (audit: a batch of two runs lost it).
            Assert.DoesNotContain("Forget", use);
            Assert.Contains("if (decision.Restore) Swaps.Record(live, linked, uidoc.Selection.GetElementIds().Select(id => (long)id.Value).ToList());", use);
            Assert.True(use.IndexOf("if (decision.Restore) uidoc.Selection.SetElementIds(", System.StringComparison.Ordinal)
                        < use.IndexOf("Swaps.Record(", System.StringComparison.Ordinal));
            Assert.Contains("var linked = decision.Restore ? LinkedPickRefs(doc, uidoc) : null;", use);
            var give = Body(host, "public static void GiveBackSelection(");
            Assert.Contains("Swaps.Take(live,", give);
            Assert.Contains("uidoc.Selection.SetReferences(refs);", give);
            Assert.Contains("catch (Exception", give);
            // The code's own select request wins: it closes the swap before it sets the selection.
            Assert.Contains("if (!plan.HasSelection) return;\n                Swaps.Forget();", host);
            Assert.Contains("if (!plan.HasReferences) return;\n                Swaps.Forget();", host);
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
