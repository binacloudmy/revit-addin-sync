#nullable enable
// r22 (round L6 partials, 21 Nov): the pane's own words follow the chat's language. M1 2ca7d010: the chip "Needs your answer" stayed English on a
// Malay card. M2 b7a76924: Revit's Undo of a BINA entry left "Undone: Set parameter Comments" in a Malay chat. 83e423eb: "ya" typed at a Malay
// write card showed a tick (nothing was written; typed text never approves), and the footer read "Save as command". Entry names stay as Revit shows them.
using System;
using BinaVibe.Copilot.Undo;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R22PartialsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 9, 0, 0);

        [Fact]
        public void Chip_ReadsMalayInAMalayChat_AndEnglishAsBefore()
        {
            Assert.Equal("Perlu jawapan anda", TurnStatusText.Of("Needs input", false, ReceiptLanguage.Ms));
            Assert.Equal("Perlu kelulusan anda", TurnStatusText.Of("Needs input", true, ReceiptLanguage.Ms));
            Assert.Equal("Selesai", TurnStatusText.Of("Done", false, ReceiptLanguage.Ms));
            Assert.Equal("Needs your answer", TurnStatusText.Of("Needs input", false, ReceiptLanguage.En));
            Assert.Equal("Needs your OK", TurnStatusText.Of("Needs input", true, ReceiptLanguage.En));
            Assert.Equal("Done", TurnStatusText.Of("Done", false, ReceiptLanguage.En));
            Assert.Equal("Needs your answer", TurnStatusText.Of("Needs input", ""));   // the old overload is unchanged
        }

        [Fact]
        public void LiveM2_RevitUndoInAMalayChat_ReadsDibuatAsal_WithTheEntryNameAsRevitShowsIt()
        {
            var log = new UndoNotices();
            Assert.True(log.Record(new UndoChange { Kind = UndoChangeKind.Undone, TurnId = "b7a76924", Name = "BINA: Set parameter Comments" }, 3, T0, ReceiptLanguage.Ms));
            Assert.Equal("Dibuat asal: Set parameter Comments", log.Items[0].Text);
            Assert.True(log.Record(new UndoChange { Kind = UndoChangeKind.Undone, TurnId = "e1", Name = "BINA: Revit code" }, 9, T0, ReceiptLanguage.En));
            Assert.Equal("Undone: Revit code", log.Items[1].Text);
        }

        [Fact]
        public void UndoWords_MalayTwin_JoinsAndCountsInMalay_EnglishUnchanged()
        {
            Assert.Equal("Dibuat asal: A, B dan C", UndoWords.Line(false, new[] { "A", "B", "C" }, true));
            Assert.Equal("Dibuat semula: A, B, C dan 2 lagi", UndoWords.Line(true, new[] { "A", "B", "C", "D", "E" }, true));
            Assert.Equal("Undone: A, B and C", UndoWords.Line(false, new[] { "A", "B", "C" }));
            Assert.Equal("Dibuat asal: Revit code (+1 lagi)", UndoWords.ForPane("Undone: Revit code (+1 more)", true));   // r22 (P6 fba25214): the count reads Malay too
            Assert.Equal("Dibuat asal: A, B, C dan 2 lagi", UndoWords.ForPane("Undone: A, B, C and 2 more", true));
            Assert.Equal("Undone: Revit code", UndoWords.ForPane("Undone: Revit code", false));
        }

        [Fact]
        public void Live83e423eb_TypedYaAtAMalayWriteCard_ShowsNoTickAndSaysTypingIsNoApproval()
        {
            var ask = new BinaVibe.Copilot.Contracts.AskDto
            {
                RequestId = "g", Question = "Sebelum saya tulis: Comments = UJI pada 141 elemen.",
                Options = new System.Collections.Generic.List<BinaVibe.Copilot.Contracts.AskOptionDto>
                {
                    new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "write_all", Label = "Tulis ke semua (141 elemen)" },
                    new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "write_cancel", Label = "Batal, jangan tulis apa-apa" },
                },
            };
            var card = AskCardModel.From(ask, ReceiptLanguage.Ms);
            Assert.NotNull(card.Wide);
            Assert.Null(card.TypedRecordNote);
            card.AnsweredSummary = "ya";
            card.AnsweredByTyping = true;
            Assert.Equal("Taip bukan kelulusan: klik Tulis untuk menulis.", card.TypedRecordNote);
            Assert.Equal("Typing is not an approval: click Write to write.", AskCardModel.TypedNotApprovalText(ReceiptLanguage.En));
        }

        [Fact]
        public void ACardTheServerWroteInMalay_GetsMalayChrome_AnEnglishOneKeepsTheTurns()
        {
            Assert.Equal(ReceiptLanguage.Ms, PromptLanguage.ForCard(ReceiptLanguage.En, "Bahagian atas kotak keratan yang mana?",
                "Ketinggian itu akan menggerakkan bahagian atas ke arah bertentangan. Yang mana satu maksud anda?"));
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.ForCard(ReceiptLanguage.En, null, "Which tag type should I use?"));
            Assert.Equal(ReceiptLanguage.Ms, PromptLanguage.ForCard(ReceiptLanguage.Ms, null, "Which tag type should I use?"));
        }
    }
}
