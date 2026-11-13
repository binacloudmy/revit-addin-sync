#nullable enable
// Round 6: a question card that arrives takes the keyboard focus only from this pane's empty message box, and only
// when it is new and waiting; while it holds that focus, a character it does not use goes back to the message box.
// Revit-free and WPF-free; whether the focus really moves inside Revit only a live run shows.

using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskCardFocusTests
    {
        private static AskOptionDto Opt(string id, string label, string? kind = null) =>
            new AskOptionDto { Id = id, Label = label, Kind = kind };

        private static AskCardModel Fields(bool live = true)
        {
            var card = AskCardModel.From(new AskDto
            {
                RequestId = "req_f", Question = "2 levels need a decision.", Purpose = AskPurposes.Clarify, Title = "Before I make the sheets",
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "pentas", Label = "Aras Pentas", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { Opt("c1", "Create plan", AskOptionKinds.Create), Opt("s1", "Skip", AskOptionKinds.Skip) } },
                    new AskFieldDto { Id = "langkan", Label = "Aras Langkan", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { Opt("c2", "Create plan", AskOptionKinds.Create), Opt("s2", "Skip", AskOptionKinds.Skip) } },
                },
            }, ReceiptLanguage.En);
            card.ArrivedLive = live;
            return card;
        }

        private static AskCardModel FreeText()
        {
            var card = AskCardModel.From(new AskDto { RequestId = "r", Question = "Which room?", Purpose = AskPurposes.Clarify }, ReceiptLanguage.En);
            card.ArrivedLive = true;
            return card;
        }

        // ─── Should the arriving card take the focus ──────────────────────────────────────────────────────

        [Fact]
        public void EmptyMessageBox_WaitingCard_TakesTheFocus() =>
            Assert.True(AskCardFocus.ShouldTakeFocus(AskFocusPlace.MessageBox, "", cardWaiting: true));

        [Fact]
        public void NullTextCountsAsEmpty() =>
            Assert.True(AskCardFocus.ShouldTakeFocus(AskFocusPlace.MessageBox, null, cardWaiting: true));

        [Theory]
        [InlineData("s")]
        [InlineData("Make sheets for")]
        [InlineData(" ")]
        public void TextInTheMessageBox_NeverTakes(string text) =>
            Assert.False(AskCardFocus.ShouldTakeFocus(AskFocusPlace.MessageBox, text, cardWaiting: true));

        [Theory]
        [InlineData(AskFocusPlace.OutsidePane)]
        [InlineData(AskFocusPlace.ElsewhereInPane)]
        public void FocusAnywhereElse_NeverTakes(AskFocusPlace place) =>
            Assert.False(AskCardFocus.ShouldTakeFocus(place, "", cardWaiting: true));

        [Fact]
        public void ACardThatIsNotWaiting_NeverTakes() =>
            Assert.False(AskCardFocus.ShouldTakeFocus(AskFocusPlace.MessageBox, "", cardWaiting: false));

        // ─── Is the card waiting ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ANewLiveCard_IsWaiting()
        {
            Assert.True(AskCardFocus.IsWaiting(Fields(), live: true));
        }

        [Fact]
        public void NotTheThreadsLiveTail_IsNotWaiting() =>
            Assert.False(AskCardFocus.IsWaiting(Fields(), live: false));

        [Fact]
        public void ACardFromHistory_IsNotWaiting() =>
            Assert.False(AskCardFocus.IsWaiting(Fields(live: false), live: true));

        [Fact]
        public void AnAnsweredCard_IsNotWaiting()
        {
            var card = Fields();
            card.AnsweredSummary = "Create plan, Skip";
            Assert.False(AskCardFocus.IsWaiting(card, live: true));
        }

        [Fact]
        public void ARedrawAfterTheOffer_IsNotWaiting()
        {
            var card = Fields();
            card.FocusOffered = true;
            Assert.False(AskCardFocus.IsWaiting(card, live: true));
        }

        [Fact]
        public void ACardWithoutKeys_IsNotWaiting() =>
            Assert.False(AskCardFocus.IsWaiting(FreeText(), live: true));

        [Fact]
        public void TheWideWriteGate_IsNotWaiting_AndNeverTakesTheFocus()
        {
            // The server's gate (write_all / write_cancel) is the team's "Needs your OK" card: no key sink, no focus on
            // arrival, so an Enter or a letter typed for the message box cannot land on its buttons.
            var card = AskCardModel.From(new AskDto
            {
                RequestId = "r", Question = "Before I write to 340 elements.", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto> { Opt("write_all", "Write to all"), Opt("write_cancel", "Cancel, write nothing") },
            }, ReceiptLanguage.En);
            card.ArrivedLive = true;
            Assert.NotNull(card.Wide);
            Assert.False(AskCardFocus.IsWaiting(card, live: true));
        }

        [Fact]
        public void TheLiveAskPaths_MarkTheCardAsArrivedLive()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var root = dir?.FullName ?? "";
            Assert.Contains("card.ArrivedLive = true;", File.ReadAllText(Path.Combine(root, "UI", "Copilot", "CopilotTurnRouter.cs")));
            Assert.Contains("card.ArrivedLive = true;", File.ReadAllText(Path.Combine(root, "UI", "Copilot", "Model", "ChatSession.cs")));
            var view = File.ReadAllText(Path.Combine(root, "UI", "Copilot", "Controls", "AskCard.cs"));
            Assert.Contains("AskCardFocus.ShouldTakeFocus(FocusPlaceNow(box), box?.Text, cardWaiting: true)", view);
            Assert.Contains("_m.FocusOffered = true;", view);
            Assert.Contains("FocusSink(byMouse: false);", view);
        }

        // ─── Characters handed back to the message box ────────────────────────────────────────────────────

        private static AskKeyResult Key(AskCardModel m, char ch) => AskCardKeys.ForChar(m, ch);

        [Theory]
        [InlineData("m")]
        [InlineData("M")]
        [InlineData("/")]
        [InlineData("@")]
        [InlineData("é")]
        public void AnUnusedPrintableCharacter_GoesBack(string text) =>
            Assert.True(AskCardFocus.HandsBack(true, text, commandModifierDown: false, Key(Fields(), text[0])));

        [Theory]
        [InlineData("c")]
        [InlineData("S")]
        public void CreateAndSkip_StayWithTheCard(string text)
        {
            var card = Fields();
            Assert.NotEqual(AskKeyKind.None, Key(card, text[0]).Kind);
            Assert.False(AskCardFocus.HandsBack(true, text, commandModifierDown: false, Key(card, text[0])));
        }

        [Theory]
        [InlineData("1")]
        [InlineData("7")]
        [InlineData("0")]
        public void Digits_StayWithTheCard_EvenOutOfRange(string text) =>
            Assert.False(AskCardFocus.HandsBack(true, text, commandModifierDown: false, Key(Fields(), text[0])));

        [Theory]
        [InlineData(" ")]
        [InlineData("\t")]
        [InlineData("\u0001")]
        [InlineData("")]
        [InlineData("ab")]
        public void WhitespaceControlOrSeveralCharacters_DoNotGoBack(string text) =>
            Assert.False(AskCardFocus.HandsBack(true, text, commandModifierDown: false, AskKeyResult.Nothing));

        [Fact]
        public void WithCtrlAltOrWindowsHeld_NothingGoesBack() =>
            Assert.False(AskCardFocus.HandsBack(true, "m", commandModifierDown: true, AskKeyResult.Nothing));

        [Fact]
        public void FocusNotTakenFromTheMessageBox_NothingGoesBack() =>
            Assert.False(AskCardFocus.HandsBack(false, "m", commandModifierDown: false, AskKeyResult.Nothing));

        // ─── Not armed: a card that took the focus by itself ──────────────────────────────────────────────

        private static AskCardModel SingleQuestion()
        {
            var card = AskCardModel.From(new AskDto
            {
                RequestId = "req_o", Question = "Which levels?", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto> { Opt("all", "All levels"), Opt("some", "Some levels"), Opt("skip", "Skip", AskOptionKinds.Skip) },
            }, ReceiptLanguage.En);
            card.ArrivedLive = true;
            return card;
        }

        [Theory]
        [InlineData("s")]
        [InlineData("c")]
        [InlineData("1")]
        [InlineData(" ")]
        [InlineData("a")]
        [InlineData("S")]
        public void NotArmed_EveryPrintableCharacter_GoesBack(string text) =>
            Assert.Equal(AskSinkText.HandBack, AskCardFocus.ForText(AskSinkMode.NotArmed, text, commandModifierDown: false, Key(Fields(), text[0])));

        [Fact]
        public void NotArmed_WithCtrlAltOrWindowsHeld_NothingHappens() =>
            Assert.Equal(AskSinkText.Ignore, AskCardFocus.ForText(AskSinkMode.NotArmed, "s", commandModifierDown: true, Key(Fields(), 's')));

        [Fact]
        public void NotArmed_EnterDoesNothing()
        {
            Assert.False(AskCardFocus.EnterActs(AskSinkMode.NotArmed));
            Assert.True(AskCardFocus.EnterActs(AskSinkMode.Armed));
            Assert.True(AskCardFocus.EnterActs(AskSinkMode.Plain));
        }

        [Fact]
        public void DownArms()
        {
            Assert.Equal(AskSinkMode.Armed, AskCardFocus.AfterArrow(AskSinkMode.NotArmed));
            Assert.Equal(AskSinkMode.Armed, AskCardFocus.AfterArrow(AskSinkMode.Armed));
            Assert.Equal(AskSinkMode.Plain, AskCardFocus.AfterArrow(AskSinkMode.Plain));
        }

        [Fact]
        public void Armed_S_IsTheCardsSkip()
        {
            var card = Fields();
            var r = Key(card, 's');
            Assert.Equal(AskKeyKind.PickField, r.Kind);
            Assert.True(r.Choice!.IsSkip);
            Assert.Equal(AskSinkText.Card, AskCardFocus.ForText(AskCardFocus.AfterArrow(AskSinkMode.NotArmed), "s", false, r));
        }

        [Fact]
        public void Armed_AnUnusedLetter_StillGoesBack() =>
            Assert.Equal(AskSinkText.HandBack, AskCardFocus.ForText(AskSinkMode.Armed, "m", false, Key(Fields(), 'm')));

        [Fact]
        public void SingleQuestionNotArmed_S_GoesBack_NothingSent()
        {
            var card = SingleQuestion();
            Assert.Equal(AskKeyKind.SendOption, Key(card, 's').Kind);   // what a click-focused card would do
            Assert.Equal(AskSinkText.HandBack, AskCardFocus.ForText(AskSinkMode.NotArmed, "s", false, Key(card, 's')));
            Assert.Null(card.AnsweredSummary);
        }

        [Theory]
        [InlineData("s")]
        [InlineData("1")]
        [InlineData("m")]
        public void ClickedOrTabbedCard_KeysGoToTheCard_AsBefore(string text) =>
            Assert.Equal(AskSinkText.Card, AskCardFocus.ForText(AskSinkMode.Plain, text, false, Key(Fields(), text[0])));

        [Fact]
        public void TheViewUsesTheArming()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var view = File.ReadAllText(Path.Combine(dir?.FullName ?? "", "UI", "Copilot", "Controls", "AskCard.cs"));
            Assert.Contains("_mode = AskSinkMode.NotArmed;", view);
            Assert.Contains("_mode = AskCardFocus.AfterArrow(_mode);", view);
            Assert.Contains("AskCardFocus.EnterActs(_mode)", view);
            Assert.Contains("AskCardFocus.ForText(_mode,", view);
        }
    }
}
