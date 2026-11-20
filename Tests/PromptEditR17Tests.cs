#nullable enable
// r17 (0.0.155), tester feedback on the Copilot pane:
// (1) a suggested prompt (a next-step chip under a card) did not scroll the chat down like a typed prompt: every send ends in
//     CopilotViewModel.ChatSend, and the chat now pins to the bottom whenever the drafter's own message is appended;
// (2) option cards: several picks where the server says multi; an exclusive question (scope, yes/no, a write or plan card's
//     action and its Cancel) stays a single choice even if a frame says multi, and Skip on a multi card stands alone;
// (3) the drafter can edit an earlier prompt (pencil / double-click on their bubble, Up in an empty composer): the text goes
//     back in the composer, nothing is sent, the thread is not changed, never while a turn runs.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class PromptEditR17Tests
    {
        private static ChatMessage User(string text, SlashTool? slash = null) =>
            new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text, SlashCommand = slash };

        private static ChatMessage Ai(string text) => new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = text };

        private static AskOptionDto Opt(string id, string label, string? kind = null) => new AskOptionDto { Id = id, Label = label, Kind = kind };

        private static AskCardModel Card(bool multi, params AskOptionDto[] options) => AskCardModel.From(new AskDto
        {
            RequestId = "r", Question = "Which?", Purpose = AskPurposes.Clarify, Multi = multi, Options = options.ToList(),
        }, ReceiptLanguage.En);

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        // ─── (1) A suggested prompt follows the bottom like a typed one ───────────────────────────────────────────────

        [Fact]
        public void TheChat_PinsToTheBottom_WhenTheDraftersMessageIsAppended()
        {
            Assert.True(ChatFollow.PinsOnAppend(appended: true, newestRole: "user"));
            Assert.False(ChatFollow.PinsOnAppend(appended: true, newestRole: "ai"));     // a reply follows only while stuck
            Assert.False(ChatFollow.PinsOnAppend(appended: false, newestRole: "user"));  // a resize or a card redraw: no jump
            Assert.False(ChatFollow.PinsOnAppend(appended: true, newestRole: null));     // a new, empty chat
        }

        [Fact]
        public void ChipsAndTypedPrompts_TakeTheSamePath_AndTheRebuildPins()
        {
            var chat = Src(Path.Combine("UI", "Copilot", "Screens", "ChatView.xaml.cs"));
            Assert.Contains("SendPrompt = t => Vm?.ChatSend(t)", chat);                // next-step chips
            Assert.Contains("ChatFollow.PinsOnAppend(", chat);
            Assert.Contains("if (pinToEnd) PinToEnd(deferred: true);", chat);
            var vm = Src(Path.Combine("UI", "Copilot", "CopilotViewModel.cs"));
            Assert.Contains("ChatSendCommand = new RelayCommand(ChatSendAny)", vm);     // the composer's send
            Assert.Contains("FollowUpCommand = new RelayCommand(ChatSendAny)", vm);     // follow-up chips
        }

        // ─── (3) Edit a previous prompt ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheDraftersOwnPrompt_CanBeEdited_InEnglishOrMalay()
        {
            Assert.Equal("Tag all doors in this view", PromptRecall.TextFor(User("  Tag all doors in this view "), busy: false));
            Assert.Equal("Tandakan semua pintu dalam pandangan ini", PromptRecall.TextFor(User("Tandakan semua pintu dalam pandangan ini"), busy: false));
        }

        [Fact]
        public void NoEdit_WhileATurnRuns_ForASlashCommand_ForAReply_OrEmptyText()
        {
            Assert.Null(PromptRecall.TextFor(User("Tag all doors"), busy: true));
            Assert.Null(PromptRecall.TextFor(User("", new SlashTool { Id = "level-builder" }), busy: false));
            Assert.Null(PromptRecall.TextFor(User("args", new SlashTool { Id = "level-builder" }), busy: false));
            Assert.Null(PromptRecall.TextFor(Ai("Done."), busy: false));
            Assert.Null(PromptRecall.TextFor(User("   "), busy: false));
            Assert.Null(PromptRecall.TextFor(null!, busy: false));
        }

        [Fact]
        public void Up_RecallsTheNewestTypedPrompt_PassingOverSlashCommandsAndReplies()
        {
            var thread = new List<ChatMessage>
            {
                User("Buat jadual pintu"), Ai("Siap."), User("x", new SlashTool { Id = "sheet-set" }), Ai("Done."),
            };
            Assert.Equal("Buat jadual pintu", PromptRecall.Last(thread));
            thread.Add(User("Make a door schedule by level"));
            Assert.Equal("Make a door schedule by level", PromptRecall.Last(thread));
            Assert.Null(PromptRecall.Last(new List<ChatMessage> { Ai("Hello") }));
            Assert.Null(PromptRecall.Last(null!));
        }

        [Fact]
        public void Up_RecallsOnlyIntoAnEmptyComposer_WithNoListOpen_AndNoTurnRunning()
        {
            Assert.True(PromptRecall.RecallsOnUp("", popupOpen: false, busy: false));
            Assert.True(PromptRecall.RecallsOnUp(null!, popupOpen: false, busy: false));
            Assert.False(PromptRecall.RecallsOnUp("draft", popupOpen: false, busy: false));   // Up moves the caret in a draft
            Assert.False(PromptRecall.RecallsOnUp("", popupOpen: true, busy: false));         // the @ / "/" list owns Up
            Assert.False(PromptRecall.RecallsOnUp("", popupOpen: false, busy: true));
        }

        [Fact]
        public void EditingPutsTheText_InTheComposer_AndNeverSends()
        {
            var vm = Src(Path.Combine("UI", "Copilot", "CopilotViewModel.cs"));
            var edit = vm.Substring(vm.IndexOf("public bool EditPrompt(ChatMessage m)"));
            edit = edit.Substring(0, edit.IndexOf("public string RecallLastPrompt()"));
            Assert.Contains("PromptToEdit?.Invoke(text)", edit);
            Assert.DoesNotContain("ChatSend", edit);
            Assert.DoesNotContain("Thread", edit);                                       // history is never changed
            var bubble = Src(Path.Combine("UI", "Copilot", "Controls", "CopilotMessageBubble.cs"));
            Assert.Contains("Action onEdit = null", bubble);                             // History passes none: no pencil there
        }

        // ─── (2) Several picks where several make sense, one where the question is exclusive ─────────────────────────

        [Fact]
        public void AListQuestion_KeepsItsTickBoxes()
        {
            var card = Card(true, Opt("Doors", "Doors"), Opt("Walls", "Walls"), Opt("Windows", "Windows"), Opt("Skip", "Skip", "skip"));
            Assert.True(card.Multi);
            card.ToggleMulti("Doors"); card.ToggleMulti("Walls");
            Assert.Equal("Send (2)", card.FooterLabel);
            Assert.Equal(new[] { "Doors", "Walls" }, card.MultiPicked.Select(o => o.Id));
        }

        [Fact]
        public void ExclusiveQuestions_StaySingle_EvenWhenTheFrameSaysMulti()
        {
            Assert.False(Card(true, Opt("a", "Whole model (77 doors)"), Opt("b", "This view (46 doors)"), Opt("Skip", "Skip", "skip")).Multi);
            Assert.False(Card(true, Opt("a", "Make 1 circuit"), Opt("b", "Cancel, make nothing", "skip")).Multi);
            Assert.False(Card(true, Opt("a", "Move 7 levels"), Opt("b", "Cancel, move nothing")).Multi);
            Assert.False(Card(true, Opt("a", "Change 6 doors to another type"), Opt("x", "Keep them"), Opt("Skip", "Skip", "skip")).Multi);
            Assert.False(Card(true, Opt("a", "No, leave the tags"), Opt("b", "Tidak")).Multi);
            Assert.False(Card(true, Opt("a", "Pintu D1"), Opt("Skip", "Skip", "skip")).Multi);           // one real option and its Skip
        }

        [Fact]
        public void AMultiPick_NeverApprovesAWrite_TheGateStaysTheGate()
        {
            var gate = Card(true, Opt(WideWriteModel.WriteAllId, "Write to all (2 elements)"), Opt(WideWriteModel.WriteCancelId, "Cancel, write nothing"));
            Assert.False(gate.Multi);
            Assert.NotNull(gate.Wide);                                                 // drawn as the write gate, one click
        }

        [Fact]
        public void TheForceQuestion_StaysSingle()
        {
            Assert.False(Card(true, Opt("write_force", "Write anyway"), Opt("force_cancel", "Cancel")).Multi);
        }

        [Fact]
        public void EditingKeepsAnUnsentDraft()
        {
            var input = Src(Path.Combine("UI", "Copilot", "Controls", "MentionInput.xaml.cs"));
            var put = input.Substring(input.IndexOf("public void PutPrompt(string text)"));
            put = put.Substring(0, put.IndexOf("private void SetText("));
            Assert.Contains("draft.TrimEnd() + \"\\n\" + (text ?? \"\")", put);   // appended under the draft, never replacing it
        }

        [Fact]
        public void Skip_StandsAlone_OnAMultiOptionsCard()
        {
            var card = Card(true, Opt("L1", "Level 1"), Opt("L2", "Level 2"), Opt("L3", "Level 3"), Opt("Skip", "Skip", "skip"));
            card.ToggleMulti("L1"); card.ToggleMulti("L2");
            card.ToggleMulti("Skip");
            Assert.Equal(new[] { "Skip" }, card.MultiPicked.Select(o => o.Id));
            card.ToggleMulti("L3");
            Assert.Equal(new[] { "L3" }, card.MultiPicked.Select(o => o.Id));
            card.ToggleMulti("L3");
            Assert.Empty(card.MultiPicked);
        }

        [Fact]
        public void TheAnswer_CarriesEveryTick()
        {
            var card = Card(true, Opt("Doors", "Doors"), Opt("Walls", "Walls"), Opt("Windows", "Windows"));
            card.ToggleMulti("Walls"); card.ToggleMulti("Doors");
            var answer = card.AnswerForOptions(card.MultiPicked.Select(o => o.Id));
            Assert.Equal(new[] { "Doors", "Walls" }, answer.Values.Keys.OrderBy(k => k));
        }

        [Fact]
        public void ASingleQuestion_StaysSingle_ThePaneNeverAddsTickBoxes()
        {
            Assert.False(Card(false, Opt("Doors", "Doors"), Opt("Walls", "Walls"), Opt("Windows", "Windows")).Multi);
        }
    }
}
