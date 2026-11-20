#nullable enable
// Was CardEnglishOnly0121Tests (D1 of the 16 Nov handover list, under G8: the result card's views held no Malay label). The owner's
// decision of 21 Nov ("follow the best practice") replaces G8: the cards follow the chat's language. A Malay card says its fixed
// labels in Malay and an English card says exactly what it said before; Revit names (categories, parameters, families, types, views,
// levels, units) and the model's own sentences are drawn as sent. So the views carry both labels again, and these tests pin that each
// Malay label sits beside its English one, and that the helpers return the English text unchanged for an English card.
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardChatLanguage0121Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Theory]
        [InlineData("UI/Copilot/Controls/ChangesBody.cs", "\"Kekalkan semua\" : \"Keep all as written\"")]
        [InlineData("UI/Copilot/Controls/ChangesBody.cs", "\"Buka sebagai grid\" : \"Open as grid\"")]
        [InlineData("UI/Copilot/Controls/ChangesBody.cs", "\"Salin jadual\" : \"Copy table\"")]
        [InlineData("UI/Copilot/Controls/ChangesBody.cs", "\"Sumber: \" : \"Source: \"")]
        [InlineData("UI/Copilot/Controls/CreatedCard.cs", "\"Salin senarai\" : \"Copy list\"")]
        [InlineData("UI/Copilot/Controls/ItemsCard.cs", "\"Lajur digunakan\" : \"Columns used\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Langkah\" : \"Steps\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Buat asal\" : \"Undo\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Buat semula\" : \"Redo\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Simpan sebagai arahan\" : \"Save as command\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Eksport ke Excel\" : \"Export to Excel\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Pilih semua\" : \"Select all\"")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs", "\"Pergi ke view\" : CardButtons.GoToView")]
        [InlineData("UI/Copilot/Controls/SourcePanel.cs", "\"Sumber · \" : \"Source · \"")]
        [InlineData("UI/Copilot/Controls/SourcePanel.cs", "\"Buka PDF\" : \"Open PDF\"")]
        [InlineData("UI/Copilot/Controls/TurnEvidenceView.cs", "\"Buat asal\" : \"Undo\"")]
        public void TheCardViews_SayEachLabelInMalayOnAMalayCard_AndInEnglishOtherwise(string file, string pair)
        {
            Assert.Contains("ms ? " + pair, Src(file));
        }

        [Fact]
        public void TheCardLanguage_IsTheDraftersLanguage()
        {
            Assert.Equal(ReceiptLanguage.Ms, PromptLanguage.Card(PromptLanguage.Detect("Tukar semua lampu jenis Demo_dl_13W kepada 10W")));
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Card(PromptLanguage.Detect("Change all Demo_dl_13W lights to 10W")));
        }

        [Fact]
        public void TheFixedCardChrome_HasMalayTwins_AndAnEnglishCardIsUnchanged()
        {
            Assert.Equal("Prompt anda", CreatedSummary.PromptSource(ReceiptLanguage.Ms));
            Assert.Equal("Your prompt", CreatedSummary.PromptSource(ReceiptLanguage.En));
            Assert.Equal("Dibuat asal. Model kembali seperti asal.", UndoBanner.For(null, ReceiptLanguage.Ms).Title);
            Assert.Equal("Undone. The model is back to how it was.", UndoBanner.For(null, ReceiptLanguage.En).Title);
            var marks = Src("UI/Copilot/Controls/MarksButton.cs");   // a WPF control: pinned by its source
            Assert.Contains("lang != ReceiptLanguage.Ms ? LabelFor(state) : state == MarksState.Shown ? \"Sembunyikan tanda\" : \"Tunjuk dalam model\"", marks);
            Assert.Contains("var lang = actions.CardLanguage;", marks);
            Assert.Contains("actions.CardLanguage = lang;", Src("UI/Copilot/Controls/ResultCardView.cs"));
        }

        [Fact]
        public void TheReadBackLine_KeepsCheckedOnTheWire_AndAMalayCardShowsDisemak()
        {
            const string line = "Checked: section box on, name PTBR_ARAS 01, box bottom -300 mm.";
            Assert.True(ChecksCardText.IsVerified("info", line, 0));
            Assert.Equal("Disemak: section box on, name PTBR_ARAS 01, box bottom -300 mm.", ChecksCardText.VerifiedDisplay(line, ms: true));
            Assert.Equal(line, ChecksCardText.VerifiedDisplay("  " + line, ms: false));                 // English: as before (trimmed)
            Assert.True(ChecksCardText.IsVerified("info", "Disemak: section box on.", 0));               // robust if a Malay prefix ever comes
            Assert.Equal("Section box on.", ChecksCardText.VerifiedDisplay("Section box on.", ms: true)); // no prefix: as sent
        }

        // ---- the question card ----------------------------------------------------------------------------------------

        private static AskDto Sheets() => new AskDto
        {
            RequestId = "r", Question = "2 aras perlu keputusan.", Title = "Sebelum saya buat helaian", Noun = "sheet", Ready = new System.Collections.Generic.List<AskReadyDto>(),
            Fields = new System.Collections.Generic.List<AskFieldDto>
            {
                new AskFieldDto { Id = "Aras 2", Label = "Aras 2", Type = AskFieldTypes.Choice, Group = "Tiada floor plan", Choices = new System.Collections.Generic.List<AskOptionDto>
                {
                    new AskOptionDto { Id = "Create a FloorPlan for Aras 2", Label = "Buat pelan", Kind = AskOptionKinds.Create },
                    new AskOptionDto { Id = "Skip Aras 2", Label = "Langkau", Kind = AskOptionKinds.Skip },
                } },
            },
        };

        [Fact]
        public void TheQuestionCardsChrome_IsMalayOnAMalayTurn_AndEnglishAsBeforeOnAnEnglishTurn()
        {
            var ms = AskCardModel.From(Sheets(), ReceiptLanguage.Ms);
            var en = AskCardModel.From(Sheets(), ReceiptLanguage.En);
            Assert.Equal("Jangan jawab", ms.DeclineLabel);
            Assert.Equal("Don't answer", en.DeclineLabel);
            Assert.Equal("Soalan daripada pembantu", ms.FromLabel);
            Assert.Equal("Question from the assistant", en.FromLabel);
            Assert.Equal("Teruskan", ms.FooterLabel);            // one question, nothing picked yet
            Assert.Equal("Continue", en.FooterLabel);
            Assert.Equal("Hantar (2)", AskCardModel.SendWithCount(2, ReceiptLanguage.Ms));
            Assert.Equal(AskCardModel.SendWithCount(2), AskCardModel.SendWithCount(2, ReceiptLanguage.En));
            Assert.Equal("Tunjuk 7 lagi", AskCardModel.ShowMoreLabel(7, ReceiptLanguage.Ms));
            Assert.Equal("Show 7 more", AskCardModel.ShowMoreLabel(7, ReceiptLanguage.En));
            Assert.Equal("3 lagi", AskCardModel.CountLabel(3, ReceiptLanguage.Ms));
            Assert.Equal("3 left", AskCardModel.CountLabel(3, ReceiptLanguage.En));
            Assert.Equal("Buat 5 helaian", AskCardModel.MakeLabel(5, "sheet", ReceiptLanguage.Ms));
            Assert.Equal("Make 5 sheets", AskCardModel.MakeLabel(5, "sheet", ReceiptLanguage.En));
            Assert.Equal("Pelan baharu", AskCardModel.NewLabel("Buat pelan", ReceiptLanguage.Ms));
            Assert.Equal("FloorPlan baharu", AskCardModel.NewLabel("Buat FloorPlan untuk Aras 2", ReceiptLanguage.Ms));
            Assert.Equal(AskCardModel.NewLabel("Create plan"), AskCardModel.NewLabel("Create plan", ReceiptLanguage.En));
            foreach (var en1 in new[] { AskCardModel.SkippedText, AskCardModel.ContinueLabel, AskCardModel.SendLabel, AskCardModel.TypeOwnHint, AskCardModel.AnsweredHeading })
            {
                Assert.Equal(en1, AskCardModel.Say(ReceiptLanguage.En, en1));
                Assert.NotEqual(en1, AskCardModel.Say(ReceiptLanguage.Ms, en1));
            }
        }

        [Fact]
        public void TheBackendsMalayLabels_KeepTheirIds_AndARevitNameIsDrawnAsSent()
        {
            var ms = AskCardModel.From(Sheets(), ReceiptLanguage.Ms);
            var f = Assert.Single(ms.Fields);
            Assert.Equal("Aras 2", f.Label);
            Assert.Equal(new[] { "Create a FloorPlan for Aras 2", "Skip Aras 2" }, f.Choices.ConvertAll(c => c.Id));
            Assert.Equal(new[] { "Buat pelan", "Langkau" }, f.Choices.ConvertAll(c => c.Label));
            Assert.False(ms.SomethingWasDropped);                 // no Malay label reads as an approval
            Assert.True(AskCardModel.IsDeclineLike("Batal, jangan tulis apa-apa"));
            Assert.True(AskCardModel.IsDeclineLike("Langkau"));
        }

        [Fact]
        public void TheMalayGateCard_IsStillTheGate()
        {
            var ask = new AskDto
            {
                RequestId = "g", Question = "Sebelum saya tulis: Comments = X pada 150 elemen.\n• Walls: 120, dan 2 lagi",
                Options = new System.Collections.Generic.List<AskOptionDto>
                {
                    new AskOptionDto { Id = "write_all", Label = "Tulis ke semua (150 elemen)" },
                    new AskOptionDto { Id = "write_cancel", Label = "Batal, jangan tulis apa-apa" },
                },
            };
            var card = AskCardModel.From(ask, ReceiptLanguage.Ms);
            Assert.NotNull(card.Wide);
            Assert.Equal("Perlu persetujuan anda", card.Wide!.Eyebrow);
            Assert.Equal(2, card.Wide.Groups[0].MoreTypes);       // "dan 2 lagi" read as the more line
            Assert.Equal("Walls", card.Wide.Groups[0].Rows[0].Name);
        }
    }
}
