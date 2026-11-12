#nullable enable
// The ask card's new look: the 2-3 option choice in the "Needs your OK" style (decline detection, which questions get it),
// and the dark form fields (the Choice field's placeholder and keyboard, required fields, the summary the card keeps).
// The security rule stays: approval-like options are still dropped (AskCardModelTests pins that).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskFormTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static AskDto Ask(params (string id, string label)[] options) => new AskDto
        {
            RequestId = "r", Question = "Which level?", Purpose = AskPurposes.Clarify,
            Options = options.Select(o => new AskOptionDto { Id = o.id, Label = o.label }).ToList(),
        };

        // ---- decline detection ----

        [Theory]
        [InlineData("Cancel")]
        [InlineData("Cancel, write nothing")]
        [InlineData("Batal")]
        [InlineData("Do not write")]
        [InlineData("Don't change anything")]
        [InlineData("No thanks")]
        [InlineData("Skip")]
        [InlineData("None of these")]
        [InlineData("Write nothing")]
        [InlineData("Jangan tulis")]
        [InlineData("No")]
        [InlineData("Tidak")]
        public void DeclineLikeLabels(string label) => Assert.True(AskCardModel.IsDeclineLike(label));

        [Theory]
        [InlineData("Level 2")]
        [InlineData("Write to the 2 downlight types only")]
        [InlineData("Note")]
        [InlineData("Novotel lobby")]
        [InlineData("")]
        [InlineData(null)]
        public void OrdinaryLabelsAreNotDeclineLike(string? label) => Assert.False(AskCardModel.IsDeclineLike(label));

        // ---- which questions wear the "Needs your OK" look ----

        [Fact]
        public void TwoOrThreeOptions_GetTheChoiceLook()
        {
            Assert.True(AskCardModel.From(Ask(("a", "Level 2"), ("b", "Level 3")), ReceiptLanguage.En).IsChoiceLook);
            Assert.True(AskCardModel.From(Ask(("a", "Level 2"), ("b", "Level 3"), ("c", "Skip")), ReceiptLanguage.En).IsChoiceLook);
        }

        [Fact]
        public void FourOptions_SeveralOf_ProjectNotes_AndFields_KeepTheirOwnLook()
        {
            Assert.False(AskCardModel.From(Ask(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4")), ReceiptLanguage.En).IsChoiceLook);

            var multi = Ask(("a", "Level 2"), ("b", "Level 3"));
            multi.Multi = true;
            Assert.False(AskCardModel.From(multi, ReceiptLanguage.En).IsChoiceLook);

            var note = Ask(("yes", "Yes"), ("no", "No"));
            note.Purpose = AskPurposes.ProjectNote;
            Assert.False(AskCardModel.From(note, ReceiptLanguage.En).IsChoiceLook);

            Assert.False(AskCardModel.From(new AskDto { RequestId = "r", Question = "Height?", Purpose = AskPurposes.Clarify }, ReceiptLanguage.En).IsChoiceLook);
        }

        [Fact]
        public void TheApprovalFilter_StillDropsOptions_BeforeTheLookIsChosen()
        {
            // "Approve ..." is dropped, so two safe options remain and the look applies to those two only.
            var card = AskCardModel.From(Ask(("a", "Level 2"), ("b", "Approve the change"), ("c", "Level 3")), ReceiptLanguage.En);

            Assert.Equal(new[] { "Level 2", "Level 3" }, card.Options.Select(o => o.Label));
            Assert.True(card.IsChoiceLook);
            Assert.True(card.SomethingWasDropped);

            // One safe option left: free text, no choice look.
            var one = AskCardModel.From(Ask(("a", "Approve"), ("b", "Level 3")), ReceiptLanguage.En);
            Assert.False(one.IsChoiceLook);
        }

        // ---- the Choice field ----

        [Fact]
        public void ThePlaceholder_IsChoose_AndPilih()
        {
            Assert.Equal("Choose...", AskCardModel.ChoosePlaceholder(ms: false));
            Assert.Equal("Pilih...", AskCardModel.ChoosePlaceholder(ms: true));
        }

        [Theory]
        [InlineData(-1, 4, 1, 0)]      // nothing picked: Down lands on the first row
        [InlineData(-1, 4, -1, 3)]     // nothing picked: Up lands on the last row
        [InlineData(0, 4, 1, 1)]
        [InlineData(3, 4, 1, 3)]       // stops at the end
        [InlineData(0, 4, -1, 0)]      // stops at the start
        [InlineData(2, 4, -1, 1)]
        [InlineData(0, 0, 1, -1)]      // no rows
        public void UpDown_MoveAndStopAtTheEnds(int index, int count, int delta, int expected) =>
            Assert.Equal(expected, AskCardModel.MoveChoice(index, count, delta));

        // ---- the form ----

        private static AskCardModel Form()
        {
            var ask = new AskDto
            {
                RequestId = "r", Question = "Details?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "type", Label = "Ceiling type", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { new AskOptionDto { Id = "cl02a", Label = "CL-02A" }, new AskOptionDto { Id = "cl20", Label = "CL-20" } } },
                    new AskFieldDto { Id = "h", Label = "Height", Type = AskFieldTypes.Number, Unit = "mm" },
                    new AskFieldDto { Id = "note", Label = "Note", Type = AskFieldTypes.Text },
                },
            };
            return AskCardModel.From(ask, ReceiptLanguage.En);
        }

        [Fact]
        public void NoFieldIsRequiredToday_SoSubmitStaysEnabled()
        {
            var card = Form();

            Assert.All(card.Fields, f => Assert.False(f.Required));
            Assert.Empty(card.MissingRequired(new Dictionary<string, string> { ["type"] = "", ["h"] = "", ["note"] = "" }));
        }

        [Fact]
        public void WhenAFieldIsRequired_BlankMeansMissing()
        {
            var card = Form();
            card.Fields[0].Required = true;
            card.Fields[1].Required = true;

            Assert.Equal(new[] { "type", "h" }, card.MissingRequired(new Dictionary<string, string>()));
            Assert.Equal(new[] { "h" }, card.MissingRequired(new Dictionary<string, string> { ["type"] = "cl20", ["h"] = "  " }));
            Assert.Empty(card.MissingRequired(new Dictionary<string, string> { ["type"] = "cl20", ["h"] = "2700" }));
        }

        [Fact]
        public void TheSummary_IsLabelValuePairsJoinedByMiddleDots_AsBefore()
        {
            var card = Form();

            var values = new Dictionary<string, string> { ["type"] = "cl20", ["h"] = "2700", ["note"] = "" };

            Assert.Equal("Ceiling type: CL-20 · Height: 2700 mm", card.SummaryForFields(values));
            Assert.Equal("", card.SummaryForFields(new Dictionary<string, string> { ["type"] = "" }));
        }

        [Fact]
        public void AnAnswer_WithNothingPicked_LeavesTheChoiceOut()
        {
            var card = Form();

            var answer = card.AnswerForFields(new Dictionary<string, string> { ["type"] = "", ["h"] = "2700" });

            Assert.False(answer.Values.ContainsKey("type"));
            Assert.Equal(2700, answer.Values["h"].GetDouble());
        }

        // ---- the view source: tokens and sizes of the form fields ----

        [Fact]
        public void TheChoiceField_IsTheComposersLook_NotANativeComboBox()
        {
            var f = Src("UI/Copilot/Controls/ChoiceField.cs");

            Assert.Contains("internal sealed class ChoiceField : Border", f);
            Assert.Contains("\"Cp.Run.Composer\"", f);
            Assert.Contains("\"Cp.Run.LineStrong\"", f);
            Assert.Contains("\"Cp.Accent\"", f);
            Assert.Contains("\"Cp.Faint\"", f);              // the placeholder
            Assert.Contains("\"Cp.Hair2\"", f);              // the list's border
            Assert.Contains("\"Cp.Hover\"", f);
            Assert.Contains("\"Cp.Bg\"", f);
            Assert.Contains("MinHeight = 34", f);
            Assert.Contains("new CornerRadius(8)", f);
            Assert.Contains("new Thickness(10, 8, 10, 8)", f);
            Assert.Contains("Height = 32", f);               // rows
            Assert.Contains("public event Action? Changed", f);
            Assert.Contains("public Func<string> Getter", f);
            Assert.Contains("Key.Escape", f);
            Assert.Contains("Key.Enter", f);
            Assert.Contains("Key.Space", f);
            Assert.Contains("Key.Down", f);
            Assert.DoesNotContain("Storyboard", f);
        }

        [Fact]
        public void TheAskCard_UsesTheNewFields_AndNoNativeComboBoxOrTextBox()
        {
            var s = Src("UI/Copilot/Controls/AskCard.cs");

            Assert.Contains("new ChoiceField(", s);
            Assert.Contains("new FormTextField(", s);
            Assert.DoesNotContain("new ComboBox", s);
            Assert.DoesNotContain("new TextBox", s);
            Assert.Contains("m.SummaryForFields(values)", s);
            Assert.Contains("m.MissingRequired(", s);
        }
    }
}
