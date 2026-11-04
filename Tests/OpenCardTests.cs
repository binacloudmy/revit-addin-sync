// AI-205: an approval card has no time limit. It waits until the drafter
// answers it (Allow / Reject) or presses Stop. These pin which card Stop
// closes and what closing it leaves behind.

using System.Collections.Generic;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class OpenCardTests
    {
        private static ChatMessage BatchCard() =>
            new ChatMessage { Role = "ai", Kind = CpMsgKind.ConfirmActions, PendingBatch = new object() };

        private static ChatMessage CodeCard() =>
            new ChatMessage { Role = "ai", Kind = CpMsgKind.ConfirmActions, PendingCode = "// code" };

        private static ChatMessage User(string t) =>
            new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = t };

        [Fact]
        public void Waiting_batch_card_is_found()
        {
            var card = BatchCard();
            var thread = new List<ChatMessage> { User("tukar pintu"), card };
            Assert.Same(card, OpenCard.Find(thread));
        }

        [Fact]
        public void Waiting_code_card_is_found()
        {
            var card = CodeCard();
            Assert.Same(card, OpenCard.Find(new List<ChatMessage> { User("x"), card }));
        }

        [Fact]
        public void Answered_card_is_not_open()
        {
            var card = BatchCard();
            card.ActionsResolved = true;
            Assert.Null(OpenCard.Find(new List<ChatMessage> { card }));
        }

        [Fact]
        public void Card_with_a_message_after_it_is_not_open()
        {
            // Typing past a card declines it, so Stop must not touch it again.
            var thread = new List<ChatMessage> { BatchCard(), User("lain") };
            Assert.Null(OpenCard.Find(thread));
        }

        [Fact]
        public void Auto_approved_card_is_not_open()
        {
            var card = BatchCard();
            card.ActionsResolved = true;
            card.ActionsApproved = true;
            card.AutoApproved = true;
            Assert.Null(OpenCard.Find(new List<ChatMessage> { card }));
        }

        [Fact]
        public void Card_with_nothing_to_run_is_not_open()
        {
            var card = new ChatMessage { Role = "ai", Kind = CpMsgKind.ConfirmActions };
            Assert.Null(OpenCard.Find(new List<ChatMessage> { card }));
        }

        [Fact]
        public void Empty_thread_has_no_card()
        {
            Assert.Null(OpenCard.Find(new List<ChatMessage>()));
            Assert.Null(OpenCard.Find(null));
        }

        [Fact]
        public void Stopped_card_reads_as_rejected_and_closes()
        {
            var card = BatchCard();
            var thread = new List<ChatMessage> { card };
            OpenCard.MarkStopped(card);
            Assert.True(card.ActionsResolved);
            Assert.False(card.ActionsApproved);
            Assert.Null(OpenCard.Find(thread));
        }

        [Fact]
        public void Stop_lines_say_nothing_ran()
        {
            Assert.Contains("tidak dijalankan", StopReport.ParkedMessage);
            Assert.Contains("tidak dijalankan", StopReport.CodeNotRunMessage);
        }
    }
}
