#nullable enable
// Final retest 17 Nov: the composer hint said 'Press "Don't answer"' on the multi-question "Which view?" card, which had no such button (only
// the credential card did), so an unrelated prompt typed under it was taken as its answer. Every question now has the button, and the hint
// appears only where the button is.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskDecline0129Tests
    {
        private static AskDto Options(bool multi, int n = 4) => new AskDto
        {
            RequestId = "r", Question = "Which view should I tag?", Purpose = AskPurposes.Clarify, Multi = multi,
            Options = Enumerable.Range(1, n).Select(i => new AskOptionDto { Id = "v" + i, Label = "View " + i }).ToList(),
        };

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheMultiQuestionCard_OffersDecline_AndTheHintNamesIt()
        {
            var card = AskCardModel.From(Options(multi: true), ReceiptLanguage.En);
            Assert.True(card.Multi);
            Assert.True(card.OffersDecline);
            Assert.Contains("Don't answer", AskCardModel.TypedAnswerHint(card));
        }

        [Fact]
        public void TheSingleChoiceCard_FieldsCard_AndFreeTextCard_OfferDecline()
        {
            Assert.True(AskCardModel.From(Options(multi: false), ReceiptLanguage.En).OffersDecline);
            var fields = new AskDto
            {
                RequestId = "r", Question = "Details?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto> { new AskFieldDto { Id = "h", Label = "Height", Type = AskFieldTypes.Number, Unit = "mm" } },
            };
            Assert.True(AskCardModel.From(fields, ReceiptLanguage.En).OffersDecline);
            var free = new AskDto { RequestId = "r", Question = "Which level?", Purpose = AskPurposes.Clarify };
            var freeCard = AskCardModel.From(free, ReceiptLanguage.En);
            Assert.True(freeCard.FreeText);
            Assert.True(freeCard.OffersDecline);
        }

        [Fact]
        public void TheDeclineAnswer_IsTheEmptyTypedAnswer()
        {
            var answer = AskCardModel.From(Options(multi: true), ReceiptLanguage.En).AnswerDeclined();
            Assert.Equal("", answer.Values[AskCardModel.FreeTextId].GetString());
        }

        [Fact]
        public void NoHint_WhereThereIsNoDeclineButton_ProjectNote_AndTheWideWriteGate()
        {
            var note = AskCardModel.From(new AskDto { RequestId = "r", Question = "Save as a project note?", Purpose = AskPurposes.ProjectNote, NoteText = "x" }, ReceiptLanguage.En);
            Assert.False(note.OffersDecline);
            Assert.Equal("", AskCardModel.TypedAnswerHint(note));
            var gate = AskCardModel.From(new AskDto
            {
                RequestId = "r", Question = "Write 545 elements?", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto> { new AskOptionDto { Id = "write_all", Label = "Write all 545" }, new AskOptionDto { Id = "write_cancel", Label = "Cancel" } },
            }, ReceiptLanguage.En);
            Assert.NotNull(gate.Wide);
            Assert.False(gate.OffersDecline);
            Assert.Equal("", AskCardModel.TypedAnswerHint(gate));
        }

        [Fact]
        public void TheCardsBuild_AddsTheDeclineRowToEveryKindButCredential()
        {
            var src = Src("UI/Copilot/Controls/AskCard.cs");
            Assert.Contains("if (m.OffersDecline && m.Kind != AskCardKind.Credential) sp.Children.Add(DeclineRow());", src);
            Assert.Contains("private FrameworkElement DeclineRow()", src);
        }
    }
}
