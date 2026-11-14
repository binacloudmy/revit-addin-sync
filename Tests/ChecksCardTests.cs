#nullable enable
// The "Check N things" card and the inline "Next" row (design boards Main, SmartTags, SheetGen, SheetsExcel):
// the words and the main-answer choice (pure), and the numbers and tokens of the drawing (source text).
using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ChecksCardTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static CheckAction A(string kind, string label = "x") => new CheckAction { Kind = kind, Label = label };

        // ---- the title ----

        [Theory]
        [InlineData(1, "Check 1 thing")]
        [InlineData(2, "Check 2 things")]
        [InlineData(11, "Check 11 things")]
        public void TheTitle_IsSingularForOne_AndPluralOtherwise(int n, string expected) =>
            Assert.Equal(expected, ChecksCardText.Title(n, ms: false));

        [Theory]
        [InlineData(1, "Semak 1 perkara")]
        [InlineData(3, "Semak 3 perkara")]
        public void TheMalayTitle_KeepsTheExistingWording(int n, string expected) =>
            Assert.Equal(expected, ChecksCardText.Title(n, ms: true));

        [Fact]
        public void TheNextAndChangeLabels_AreInBothLanguages()
        {
            Assert.Equal("Next", ChecksCardText.NextLabel(false));
            Assert.Equal("Seterusnya", ChecksCardText.NextLabel(true));
            Assert.Equal("Change", ChecksCardText.ChangeLabel(false));
            Assert.Equal("Ubah", ChecksCardText.ChangeLabel(true));
        }

        // ---- which answer is the main one ----

        [Fact]
        public void TheMainAnswer_IsTheFirstThatDoesSomething()
        {
            var list = new List<CheckAction> { A(ResultActionKinds.SetParameters), A(ResultActionKinds.Prompt), A(ResultActionKinds.Dismiss) };
            Assert.Equal(0, ChecksCardText.MainIndex(list));
        }

        [Fact]
        public void LeavingOut_IsNeverTheMainAnswer()
        {
            var list = new List<CheckAction> { A(ResultActionKinds.Dismiss), A(ResultActionKinds.Prompt) };
            Assert.Equal(1, ChecksCardText.MainIndex(list));
            Assert.Equal(-1, ChecksCardText.MainIndex(new List<CheckAction> { A(ResultActionKinds.Dismiss) }));
            Assert.Equal(-1, ChecksCardText.MainIndex(new List<CheckAction>()));
        }

        // ---- what an answered row says ----

        [Fact]
        public void AnAnsweredRow_SaysTheAnswerAndWhatItDid()
        {
            Assert.Equal("Add these 2: Written. Undo takes it back.", ChecksCardText.AnsweredText("Add these 2", ResultActionKinds.SetParameters, ms: false));
            Assert.Equal("Show them: Sent.", ChecksCardText.AnsweredText("Show them", ResultActionKinds.Prompt, ms: false));
            Assert.Equal("Show them: Dihantar.", ChecksCardText.AnsweredText("Show them", ResultActionKinds.Prompt, ms: true));
            Assert.Equal("Ditulis. Undo membuangnya.", ChecksCardText.AnsweredText("", ResultActionKinds.SetParameters, ms: true));
        }

        [Fact]
        public void AFailedAnswer_SaysWhy_AndAVeryLongReasonKeepsItsEnd()
        {
            Assert.Equal("Could not: busy", ChecksCardText.FailureText("busy", ms: false));
            Assert.Equal("Tidak dapat: busy", ChecksCardText.FailureText("busy", ms: true));
            var why = new string('a', 200) + "END";
            var text = ChecksCardText.FailureText(why, ms: false);
            Assert.EndsWith("END", text);
            Assert.Equal("Could not: ".Length + 80, text.Length);
        }

        // ---- the drawing (source text) ----

        [Fact]
        public void TheChecksCard_IsItsOwnAmberCard_WithAHeaderStripAndAWarningIcon()
        {
            var s = Src("UI/Copilot/Controls/ChecksCard.cs");
            Assert.Contains("CardKit.Card(\"Cp.AmberLn\", \"Cp.Bg\"", s);
            Assert.Contains("CardKit.Header(\"Cp.AmberBg\"", s);
            Assert.Contains("CardKit.Text(\"\", 13, \"Cp.AmberFg\", FontWeights.SemiBold", s);
            Assert.Contains("M8,2.5 L14,13 H2 Z", s);
            Assert.Contains("ChecksCardText.Title(open, ms)", s);
        }

        [Fact]
        public void TheBody_IsThirteen_AndTheAnswersAreSecondaryButtons_WithTheMainOneInLimeTokens()
        {
            var s = Src("UI/Copilot/Controls/ChecksCard.cs");
            Assert.Contains("CardKit.Text(c.Text, 13, \"Cp.Ink\"", s);
            Assert.Contains("CardKit.Secondary(action.Label", s);
            Assert.Contains("ChecksCardText.MainIndex(c.Actions)", s);
            Assert.Contains("\"Cp.Accent\"", s);
            Assert.Contains("\"Cp.BlueSoft\"", s);
            Assert.Contains("\"Cp.BlueText\"", s);
            Assert.DoesNotContain("CardKit.Chip", s);
            Assert.DoesNotContain("FlatButton", s);
            Assert.DoesNotContain("Storyboard", s);
        }

        [Fact]
        public void AnAnsweredRow_IsACheckMark_TheResult_AndAnUnderlinedChangeLink_ThatReopensTheAnswers()
        {
            var s = Src("UI/Copilot/Controls/ChecksCard.cs");
            Assert.Contains("\"Cp.Green\"", s);
            Assert.Contains("TextDecorations.Underline", s);
            Assert.Contains("CardKit.Ghost(ChecksCardText.ChangeLabel(ms)", s);
            Assert.Contains("actions.RunCheckAction(action)", s);
            Assert.Contains("ChecksCardText.AnsweredText(action.Label, action.Kind, ms)", s);
            Assert.Contains("ChecksCardText.FailureText(why, ms)", s);
        }

        [Fact]
        public void Next_IsTheInlineRow_ATwelvePixelLabelThenCompactSecondaryChips_ThatSendThePrompt()
        {
            var s = Src("UI/Copilot/Controls/ChecksCard.cs");
            Assert.Contains("CardKit.Text(ChecksCardText.NextLabel(ms), 12, \"Cp.Faint\"", s);
            // The compact Secondary whose words wrap (CardKit.WrappingChip): a long prompt is never cut (live R11, E-c01).
            Assert.Contains("CardKit.WrappingChip(text, () => actions.SendPrompt(text))", s);
        }

        [Fact]
        public void TheResultCardView_HandsTheChecksAndNextToTheCard_AndKeepsNoOldDrawingOfThem()
        {
            var v = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("ChecksAndNext(block, card, lang, actions);", v);   // a sibling of the main card, not inside it
            Assert.Contains("ChecksCard.Add(root, card, lang, actions)", v);
            Assert.DoesNotContain("CheckRow(", v);
            Assert.DoesNotContain("Written. Undo takes it back.", v);
        }
    }
}
