#nullable enable
// r23 (0.0.167), live PC round P11 on 0.0.166:
// (1) AI-464, Malay chat c7e4f9cb / 329dbc75: the IDLE composer still read "Ask Copilot". It reads "Tanya Copilot" in a Malay thread
//     (PromptLanguage.OfThread, the one rule of the Working card and the running composer); a new or English chat is unchanged.
// (2) 83058dfb: the chip "Selesai" from the Malay chat carried into a NEW English chat. The chip's language now comes from the thread and
//     is reset whenever the thread is cleared (new chat) or rebuilt (another chat opened).
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ChatLanguageP11R23Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }

        private static ChatMessage Msg(string role, string text, ReceiptLanguage lang = ReceiptLanguage.En) =>
            new ChatMessage { Role = role, Text = text, Lang = lang };

        [Fact]
        public void TheIdleComposer_ReadsMalay_InAMalayThread_AndEnglishOtherwise()
        {
            var malay = new List<ChatMessage> { Msg("user", "Pilih semua pintu bertanda FD1 dalam view ini."), Msg("ai", "2 pintu dipilih.", ReceiptLanguage.Ms) };
            Assert.Equal("Tanya Copilot", RunningText.IdlePlaceholderFor(PromptLanguage.OfThread(malay), RunningText.IdlePlaceholder));
            Assert.Equal("Ask Copilot", RunningText.IdlePlaceholderFor(PromptLanguage.OfThread(new List<ChatMessage>()), RunningText.IdlePlaceholder));   // a new chat
            Assert.Equal("Ask Copilot", RunningText.IdlePlaceholderFor(ReceiptLanguage.En, RunningText.IdlePlaceholder));
            Assert.Equal("Tanya Copilot", RunningText.IdlePlaceholderFor(ReceiptLanguage.Ms, null));
            Assert.Equal("Describe the view", RunningText.IdlePlaceholderFor(ReceiptLanguage.Ms, "Describe the view"));   // a host's own placeholder stays

            var bar = Src("UI/Copilot/Controls/PromptBar.xaml.cs");
            Assert.Contains("Input.PlaceholderText = Busy ? RunningText.QueuePlaceholderFor(_runLanguage) : RunningText.IdlePlaceholderFor(_runLanguage, Placeholder);", bar);
            Assert.Contains("new PropertyMetadata(RunningText.IdlePlaceholder, OnPlaceholderChanged));", bar);
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            var onThread = chat.Substring(chat.IndexOf("private void OnThread(", System.StringComparison.Ordinal));
            onThread = onThread.Substring(0, onThread.IndexOf("\n        }\n", System.StringComparison.Ordinal));
            Assert.Contains("Prompt.RunLanguage = LiveLanguage();", onThread);                                   // every thread change, a new chat too
            Assert.Contains("if (Prompt != null && Vm != null) Prompt.RunLanguage = LiveLanguage();", chat);     // and when a turn starts or ends
        }

        [Fact]
        public void TheChip_TakesItsLanguageFromTheThread_AndANewChatIsEnglish()
        {
            var malay = new List<ChatMessage> { Msg("user", "Pilih semua pintu bertanda FD1 dalam view ini."), Msg("ai", "2 pintu dipilih.", ReceiptLanguage.Ms) };
            Assert.Equal(ReceiptLanguage.Ms, TurnStatusText.ChipLanguage(malay));
            Assert.Equal(ReceiptLanguage.En, TurnStatusText.ChipLanguage(new List<ChatMessage>()));        // 83058dfb: a new chat
            Assert.Equal(ReceiptLanguage.En, TurnStatusText.ChipLanguage(null));
            Assert.Equal(ReceiptLanguage.En, TurnStatusText.ChipLanguage(new List<ChatMessage> { Msg("user", "Select all doors"), Msg("ai", "2 selected.") }));
            // Audit of a6759dcf: one rule with the composer. A Malay prompt with no reply yet, and an older saved Malay chat whose reply
            // carries no stored language (it loads as English), both read Malay, as "Tanya Copilot" beside them does.
            Assert.Equal(ReceiptLanguage.Ms, TurnStatusText.ChipLanguage(new List<ChatMessage> { Msg("user", "Pilih semua pintu bertanda FD1 dalam view ini.") }));
            var oldSaved = new List<ChatMessage> { Msg("user", "Pilih semua pintu bertanda FD1 dalam view ini."), Msg("ai", "2 pintu dipilih.") };
            Assert.Equal(ReceiptLanguage.Ms, TurnStatusText.ChipLanguage(oldSaved));
            Assert.Equal("Tanya Copilot", RunningText.IdlePlaceholderFor(PromptLanguage.OfThread(oldSaved), RunningText.IdlePlaceholder));
            foreach (var thread in new[] { malay, oldSaved, new List<ChatMessage>(), new List<ChatMessage> { Msg("user", "Select all doors"), Msg("ai", "2 selected.") } })
                Assert.Equal(PromptLanguage.OfThread(thread), TurnStatusText.ChipLanguage(thread));
            Assert.Equal(TurnStatusText.Of("Done", false, ReceiptLanguage.En), TurnStatusText.Of("Done", false, TurnStatusText.ChipLanguage(new List<ChatMessage>())));

            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("NotifyCollectionChangedAction.Reset) ResetStatusLanguage(); };", vm);
            Assert.Contains("_statusLang = Model.TurnStatusText.ChipLanguage(Thread);", vm);
            var cont = vm.Substring(vm.IndexOf("public void ContinueSession(HistoryEntry entry)", System.StringComparison.Ordinal));
            Assert.True(cont.IndexOf("ResetStatusLanguage();", System.StringComparison.Ordinal) < cont.IndexOf("Tab = CpTab.Chat;", System.StringComparison.Ordinal));
        }

        [Fact]
        public void AMalayTurnWithNoCard_EndsWithAMalayChip_EvenInANewChat()
        {
            // L14 e33b2db9 / 824dbaae: a new chat (the chip reset to English), a Malay prompt, a read-only answer with no card. IsSending's
            // end set "Done" before the reply was drawn and AfterResult kept it, so nothing re-read the chip: it said "Done". Now the chip
            // takes the thread's language when the send starts, and is raised again once the turn's language is set.
            var thread = new List<ChatMessage>();
            Assert.Equal(ReceiptLanguage.En, TurnStatusText.ChipLanguage(thread));                 // the new chat
            thread.Add(Msg("user", "Kira jumlah beban elektrik (watt) semua lampu dalam view ini. Jangan ubah apa-apa."));
            thread.Add(Msg("ai", "Thinking…"));                                                     // the live placeholder, English by default
            var atSend = TurnStatusText.ChipLanguage(thread);
            Assert.Equal(ReceiptLanguage.Ms, atSend);
            Assert.Equal(TurnStatusText.Of("Running", false, ReceiptLanguage.Ms), TurnStatusText.Of("Running", false, atSend));
            Assert.Equal(TurnStatusText.Of("Done", false, ReceiptLanguage.Ms), TurnStatusText.Of(TurnStatus.AfterResult("Done", false, false), false, atSend));
            Assert.NotEqual(TurnStatusText.Of("Done", false, ReceiptLanguage.En), TurnStatusText.Of("Done", false, atSend));
            // An English turn in the same pane stays English.
            Assert.Equal(ReceiptLanguage.En, TurnStatusText.ChipLanguage(new List<ChatMessage> { Msg("user", "Calculate the total lighting load in this view.") }));

            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("if (value) { _statusLang = Model.TurnStatusText.ChipLanguage(Thread); RunStatus = \"Running\"; Raise(nameof(RunStatusText)); }", vm);
            var set = vm.IndexOf("_statusLang = cardLang;", System.StringComparison.Ordinal);
            Assert.True(set > 0);
            Assert.True(vm.IndexOf("Raise(nameof(RunStatusText));", set, System.StringComparison.Ordinal) < vm.IndexOf("if (rr.Ask != null)", set, System.StringComparison.Ordinal));
        }

        [Fact]
        public void TheAddInVersion_Is0_0_174()
        {
            var m = Regex.Match(Src("RevitWebAppSync.csproj"), @"<Version Condition=""'\$\(Version\)' == ''"">([0-9.]+)</Version>");
            Assert.True(m.Success);
            Assert.Equal("0.0.174", m.Groups[1].Value);
        }
    }
}
