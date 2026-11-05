#nullable enable
// W5 Task 3: ask cards (spec §7.11, §13). They come from the assistant and
// can never stand in for approval: options with approval words are dropped,
// the pane never collects credentials, and a project note is saved only when
// the drafter taps the pane's own "Save note". Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskCardModelTests
    {
        private static AskDto Ask(params (string id, string label)[] options) => new AskDto
        {
            RequestId = "req_q",
            Question = "Aras mana?",
            Purpose = AskPurposes.Clarify,
            Options = options.Select(o => new AskOptionDto { Id = o.id, Label = o.label }).ToList(),
        };

        [Fact]
        public void ApprovalWordsAreDroppedFromOptions()
        {
            var card = AskCardModel.From(Ask(("l2", "Level 2"), ("ok", "Approve the change"), ("l3", "Level 3"), ("ya", "Ya, teruskan")), ReceiptLanguage.En);

            Assert.Equal(new[] { "Level 2", "Level 3" }, card.Options.Select(o => o.Label));
            Assert.True(card.SomethingWasDropped);
        }

        [Fact]
        public void TooFewSafeOptions_FallsBackToFreeText()
        {
            var card = AskCardModel.From(Ask(("a", "Approve"), ("l3", "Level 3")), ReceiptLanguage.En);

            Assert.Empty(card.Options);
            Assert.True(card.FreeText);
        }

        [Fact]
        public void CredentialFieldsAreNeverShown()
        {
            var ask = new AskDto
            {
                RequestId = "r", Question = "Need details", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "height", Label = "Ceiling height", Type = AskFieldTypes.Number, Unit = "mm" },
                    new AskFieldDto { Id = "pw", Label = "Your BINA password", Type = AskFieldTypes.Text },
                    new AskFieldDto { Id = "kl", Label = "Kata laluan", Type = AskFieldTypes.Text },
                    new AskFieldDto { Id = "api_token", Label = "Token", Type = AskFieldTypes.Text },
                },
            };

            var card = AskCardModel.From(ask, ReceiptLanguage.En);

            Assert.Equal(new[] { "height" }, card.Fields.Select(f => f.Id));
            Assert.True(card.SomethingWasDropped);
        }

        [Fact]
        public void ChoiceFieldsCarryTheirChoices_AndAnswerWithTheChosenId()
        {
            var ask = new AskDto
            {
                RequestId = "r", Question = "Which type?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "type", Label = "Ceiling type", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { new AskOptionDto { Id = "cl02a", Label = "CL-02A" }, new AskOptionDto { Id = "cl20", Label = "CL-20" } } },
                    new AskFieldDto { Id = "h", Label = "Height", Type = AskFieldTypes.Number, Unit = "mm" },
                },
            };

            var card = AskCardModel.From(ask, ReceiptLanguage.En);
            Assert.Equal(new[] { "CL-02A", "CL-20" }, card.Fields[0].Choices.Select(c => c.Label));

            var answer = card.AnswerForFields(new Dictionary<string, string> { ["type"] = "cl20", ["h"] = "2700" });
            Assert.Equal("cl20", answer.Values["type"].GetString());
            Assert.Equal(JsonValueKind.Number, answer.Values["h"].ValueKind);
            Assert.Equal(2700, answer.Values["h"].GetDouble());
        }

        [Fact]
        public void ProjectNote_SavesOnlyOnYes_WithPaneOwnedLabels()
        {
            var ask = new AskDto
            {
                RequestId = "req_03", Question = "Simpan sebagai nota projek?", Purpose = AskPurposes.ProjectNote,
                NoteText = "Office ceilings in this project are at 2700 mm.",
                Options = new List<AskOptionDto> { new AskOptionDto { Id = "yes", Label = "Ya, simpan" }, new AskOptionDto { Id = "no", Label = "Tidak" } },
            };

            var card = AskCardModel.From(ask, ReceiptLanguage.Ms);

            Assert.True(card.IsProjectNote);
            Assert.Equal("Office ceilings in this project are at 2700 mm.", card.NoteText);
            Assert.Equal("Simpan nota", card.SaveLabel);
            Assert.Equal("Jangan simpan", card.DontSaveLabel);
            Assert.True(card.AnswerProjectNote(save: true).Values["yes"].GetBoolean());
            var no = card.AnswerProjectNote(save: false);
            Assert.False(no.Values.ContainsKey("yes"));
            Assert.True(no.Values["no"].GetBoolean());
        }

        [Fact]
        public void OptionsAndFreeTextAnswers()
        {
            var card = AskCardModel.From(Ask(("l2", "Level 2"), ("l3", "Level 3")), ReceiptLanguage.En);

            var picked = card.AnswerForOptions(new[] { "l3" });
            Assert.Equal(new[] { "l3" }, picked.Values.Keys);
            Assert.True(picked.Values["l3"].GetBoolean());

            var typed = card.AnswerForText("the mezzanine");
            Assert.Equal("the mezzanine", typed.Values["text"].GetString());
        }

        [Theory]
        [InlineData("Apply")]
        [InlineData("Yes")]
        [InlineData("ya")]
        [InlineData("OK")]
        [InlineData("Okay, go")]
        [InlineData("Setuju")]
        [InlineData("Laksanakan")]
        [InlineData("Jalankan sekarang")]
        public void TheConfirmCardsOwnWords_AreApprovalWords(string label)
        {
            // Review M2: the ask card must never offer what reads like the
            // confirm card's own Ya / Yes, apply / Laksanakan.
            Assert.True(AskCardModel.IsApprovalLike(label));
        }

        [Fact]
        public void ApprovalLikeFieldLabelsAndChoices_AreDropped()
        {
            var ask = new AskDto
            {
                RequestId = "r", Question = "Details?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "go", Label = "Type YES to apply", Type = AskFieldTypes.Text },
                    new AskFieldDto { Id = "lvl", Label = "Level", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto>
                        {
                            new AskOptionDto { Id = "l2", Label = "Level 2" },
                            new AskOptionDto { Id = "ok", Label = "OK, proceed" },
                            new AskOptionDto { Id = "l3", Label = "Level 3" },
                        } },
                    new AskFieldDto { Id = "conf", Label = "Confirm", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { new AskOptionDto { Id = "y", Label = "Ya" }, new AskOptionDto { Id = "n", Label = "Tidak" } } },
                },
            };

            var card = AskCardModel.From(ask, ReceiptLanguage.En);

            Assert.DoesNotContain(card.Fields, f => f.Id == "go");
            Assert.DoesNotContain(card.Fields, f => f.Id == "conf");          // its label is an approval word
            var lvl = Assert.Single(card.Fields, f => f.Id == "lvl");
            Assert.Equal(new[] { "Level 2", "Level 3" }, lvl.Choices.Select(c => c.Label));
            Assert.True(card.SomethingWasDropped);
        }

        [Fact]
        public void AQuestionAskingForACredential_GetsNoAnswerBox()
        {
            var ask = new AskDto { RequestId = "r", Question = "Please paste your BINA password so I can sync.", Purpose = AskPurposes.Clarify };

            var card = AskCardModel.From(ask, ReceiptLanguage.En);

            Assert.True(card.AsksForCredential);
            Assert.False(card.FreeText);
            Assert.Empty(card.Options);
            Assert.Empty(card.Fields);
            var declined = card.AnswerDeclined();
            Assert.Equal("", declined.Values[AskCardModel.FreeTextId].GetString());
        }

        [Fact]
        public void TypedReplyToACredentialQuestion_IsRefusedAndTheCardStaysOpen()
        {
            var ask = new AskDto { RequestId = "r", Question = "Paste your API key here.", Purpose = AskPurposes.Clarify };
            var en = AskCardModel.From(ask, ReceiptLanguage.En);
            var ms = AskCardModel.From(ask, ReceiptLanguage.Ms);

            Assert.StartsWith("Not sent.", en.RefuseTypedAnswer());
            Assert.Contains("Don't answer", en.RefuseTypedAnswer());
            Assert.StartsWith("Tidak dihantar.", ms.RefuseTypedAnswer());
            Assert.Contains("Jangan jawab", ms.RefuseTypedAnswer());
            Assert.Null(en.AnsweredSummary);                       // refusing never marks it answered
            Assert.Equal("Don't answer", en.DeclineLabel);         // the way on stays

            var plain = AskCardModel.From(new AskDto { RequestId = "r2", Question = "Which level?", Purpose = AskPurposes.Clarify }, ReceiptLanguage.En);
            Assert.Null(plain.RefuseTypedAnswer());                // typed text answers a normal question
        }

        [Fact]
        public void OptionIdsOutsideTheCard_AreIgnored()
        {
            var card = AskCardModel.From(Ask(("l2", "Level 2"), ("l3", "Level 3"), ("ok", "Approve")), ReceiptLanguage.En);

            var answer = card.AnswerForOptions(new[] { "ok", "l2" });

            Assert.Equal(new[] { "l2" }, answer.Values.Keys);
        }
    }
}
