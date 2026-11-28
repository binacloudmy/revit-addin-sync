#nullable enable
// r12 (0.0.145): Revit's own Undo/Redo (Ctrl+Z, the Undo list) of a BINA entry leaves a small "Undone: …" line in the
// chat thread, one line per burst, never in the saved history or the model's transcript. The pane's own button is not noted.
using System;
using System.IO;
using BinaVibe.Copilot.Undo;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoNotices0145Tests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 19, 10, 0, 0);

        private static UndoChange C(UndoChangeKind k, string name, bool byPane = false, string turn = "t1") =>
            new UndoChange { Kind = k, TurnId = turn, Name = name, ByPane = byPane };

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void RevitUndo_MakesALine_WithoutThePrefix()
        {
            var log = new UndoNotices();
            Assert.True(log.Record(C(UndoChangeKind.Undone, "BINA: Set parameter Mark"), 4, T0));
            Assert.Single(log.Items);
            Assert.Equal("Undone: Set parameter Mark", log.Items[0].Text);
            Assert.Equal(4, log.Items[0].After);
            Assert.Single(log.After(3));
            Assert.Empty(log.After(2));
        }

        [Fact]
        public void Redo_SaysRedid()
        {
            var log = new UndoNotices();
            log.Record(C(UndoChangeKind.Redone, "BINA: Tag doors"), 2, T0);
            Assert.Equal("Redone: Tag doors", log.Items[0].Text);
        }

        [Fact]
        public void PaneButton_Other_AndNoTurn_AreNotNoted()
        {
            var log = new UndoNotices();
            Assert.False(log.Record(C(UndoChangeKind.Undone, "BINA: A", byPane: true), 2, T0));
            Assert.False(log.Record(C(UndoChangeKind.Other, "BINA: A"), 2, T0));
            Assert.False(log.Record(C(UndoChangeKind.Undone, "BINA: A", turn: ""), 2, T0));
            Assert.False(log.Record(C(UndoChangeKind.Undone, "BINA: A"), 0, T0));   // an empty chat
            Assert.False(log.Record(null, 2, T0));
            Assert.Empty(log.Items);
        }

        [Fact]
        public void ABurst_IsOneLine()
        {
            var log = new UndoNotices();
            log.Record(C(UndoChangeKind.Undone, "BINA: Set Mark (batch 2)"), 3, T0);
            log.Record(C(UndoChangeKind.Undone, "BINA: Set Mark (batch 1)"), 3, T0.AddMilliseconds(5));
            log.Record(C(UndoChangeKind.Undone, "BINA: Set Mark (batch 1)"), 3, T0.AddSeconds(1));   // a repeated name once
            Assert.Single(log.Items);
            Assert.Equal("Undone: Set Mark (batch 2) and Set Mark (batch 1)", log.Items[0].Text);
        }

        [Fact]
        public void ManyNames_SayHowManyMore()
        {
            Assert.Equal("Undone: A, B, C and 2 more", UndoNotices.TextOf(UndoChangeKind.Undone, new[] { "A", "B", "C", "D", "E" }));
            Assert.Equal("Undone: A, B and C", UndoNotices.TextOf(UndoChangeKind.Undone, new[] { "A", "B", "C" }));
        }

        [Fact]
        public void ANewLine_AfterAPause_AKindChange_OrANewMessage()
        {
            var log = new UndoNotices();
            log.Record(C(UndoChangeKind.Undone, "BINA: A"), 3, T0);
            log.Record(C(UndoChangeKind.Undone, "BINA: B"), 3, T0 + UndoNotices.Burst + TimeSpan.FromSeconds(1));   // a pause
            log.Record(C(UndoChangeKind.Redone, "BINA: B"), 3, T0.AddSeconds(5));                                  // the other way
            log.Record(C(UndoChangeKind.Redone, "BINA: A"), 4, T0.AddSeconds(5.5));                                // a message came
            Assert.Equal(4, log.Items.Count);
        }

        [Fact]
        public void Strip_HandlesEmptyAndLongNames()
        {
            Assert.Equal("a Copilot change", UndoNotices.Strip("BINA: "));
            Assert.Equal("a Copilot change", UndoNotices.Strip(null));
            Assert.Equal("Rename views", UndoNotices.Strip("Rename views"));
            Assert.True(UndoNotices.Strip("BINA: " + new string('x', 200)).Length <= UndoNotices.MaxNameLength);
        }

        [Fact]
        public void Clear_EmptiesTheLog()
        {
            var log = new UndoNotices();
            log.Record(C(UndoChangeKind.Undone, "BINA: A"), 3, T0);
            log.Clear();
            Assert.Empty(log.Items);
        }

        [Fact]
        public void Wiring_StaysOutOfThreadAndHistory()
        {
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("NoteUndo(change); if (InModel?.GoneWith(change) == true) Revit.ClearMarks(); UndoStateChanged?.Invoke();", vm);
            Assert.Contains("UndoNotices.Record(change, Thread.Count, DateTime.Now, UndoLanguage(change))", vm);   // r22: the notice follows the chat language
            Assert.Contains("NotifyCollectionChangedAction.Reset) UndoNotices.Clear()", vm);
            Assert.DoesNotContain("Thread.Add(new ChatMessage { Text = note", vm);
            var view = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("foreach (var note in Vm.UndoNotices.After(mi))", view);
            Assert.Contains("_threadPanel.Children.RemoveAt(_tailRow);", view);
            // The history/transcript writers never read the notices.
            Assert.DoesNotContain("UndoNotices", Src("UI/Copilot/Model/ChatSession.cs"));
            Assert.DoesNotContain("UndoNotices", Src("UI/Copilot/CopilotTurnRouter.cs"));
        }

        [Fact]
        public void JumpToBottom_ShowsOnlyOffTheBottom_AndRepins()
        {
            var xaml = Src("UI/Copilot/Screens/ChatView.xaml");
            Assert.Contains("x:Name=\"JumpToBottom\"", xaml);
            Assert.Contains("Click=\"OnJumpToBottom\"", xaml);
            // Before the slash and save layers, so they still cover it.
            Assert.True(xaml.IndexOf("x:Name=\"JumpToBottom\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"SlashLayer\"", StringComparison.Ordinal));
            var view = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("bool show = !_stick && Vm != null && Vm.Thread.Count > 0", view);
            Assert.Contains("sv.VerticalOffset < sv.ScrollableHeight - StickThresholdPx", view);
            var click = view.Substring(view.IndexOf("private void OnJumpToBottom", StringComparison.Ordinal));
            click = click.Substring(0, click.IndexOf("}", StringComparison.Ordinal));
            Assert.Contains("_stick = true;", click);
            Assert.Contains("Scroller?.ScrollToEnd();", click);
        }
    }
}
