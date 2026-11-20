#nullable enable
// r18 C1 (live turn 0e65c0ce on 0.0.156): the tag-all "Which category?" card, a form with ONE multi field and the backend's Skip
// row, showed a "1 left" badge by its heading and an "Answer 1 more" button. One question has nothing to count: a single-field
// form shows no badge and its button reads "Send" (multi) or "Continue" (single), disabled until answered. Forms with several
// questions keep "N left" and "Answer K more" exactly as before.
using System;
using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskSingleQuestionR18Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static AskOptionDto Opt(string id, string? kind = null) => new AskOptionDto { Id = id, Label = id, Kind = kind };

        // The C1 shape: one multi field under a heading, its last choice the backend's Skip.
        private static AskCardModel TagAll(bool multi = true) => AskCardModel.From(new AskDto
        {
            RequestId = "req_02", Title = "Which category?", Question = "Which categories should I tag?", Purpose = AskPurposes.Clarify,
            Fields = new List<AskFieldDto>
            {
                new AskFieldDto { Id = "category", Label = "Which category?", Type = AskFieldTypes.Choice, Multi = multi, Group = "Categories",
                    Choices = new List<AskOptionDto> { Opt("Doors"), Opt("Windows"), Opt("Walls"), Opt("Skip", AskOptionKinds.Skip) } },
            },
        }, ReceiptLanguage.En);

        // Two questions in one group: the counters stay.
        private static AskCardModel TwoQuestions() => AskCardModel.From(new AskDto
        {
            RequestId = "req_03", Question = "Which type?; Which product?", Purpose = AskPurposes.Clarify,
            Fields = new List<AskFieldDto>
            {
                new AskFieldDto { Id = "type", Label = "Which type?", Type = AskFieldTypes.Choice, Multi = true, Group = "Pick",
                    Choices = new List<AskOptionDto> { Opt("t1"), Opt("t2") } },
                new AskFieldDto { Id = "product", Label = "Which product?", Type = AskFieldTypes.Choice, Group = "Pick",
                    Choices = new List<AskOptionDto> { Opt("p1"), Opt("p2"), Opt("Skip", AskOptionKinds.Skip) } },
            },
        }, ReceiptLanguage.En);

        [Fact]
        public void OneQuestion_ShowsNoLeftBadge()
        {
            var card = TagAll();
            Assert.True(card.IsSingleQuestion);
            var g = card.Groups()[0];
            Assert.True(AskCardModel.CountsIn(g));      // it has a choice field...
            Assert.False(card.ShowsCountIn(g));         // ...but one question has nothing to count
        }

        [Fact]
        public void OneMultiQuestion_ButtonIsSend_NeverAnswer1More()
        {
            var card = TagAll();
            Assert.Equal("Send", card.FooterLabel);
            Assert.False(card.FooterEnabled);
            card.Choose("category", "Doors");
            Assert.Equal("Send (1)", card.FooterLabel);
            card.Choose("category", "Walls");
            Assert.Equal("Send (2)", card.FooterLabel);
            Assert.True(card.FooterEnabled);
            card.Choose("category", "Doors"); card.Choose("category", "Walls");   // untick both
            Assert.Equal("Send", card.FooterLabel);
            Assert.False(card.FooterEnabled);
        }

        [Fact]
        public void OneQuestion_TheSkipRowStillSendsAtOnce()
        {
            var card = TagAll();
            Assert.NotNull(AskCardModel.SkipChoice(card.Fields[0]));
            Assert.Equal(3, AskCardModel.PickChoices(card.Fields[0]).Count);       // the skip is the quiet row
            Assert.True(card.PickSends("category", "Skip"));
            Assert.False(card.PickSends("category", "Doors"));
        }

        [Fact]
        public void OneSingleChoiceQuestion_ButtonIsContinue()
        {
            var card = TagAll(multi: false);
            Assert.Equal("Continue", card.FooterLabel);
            Assert.False(card.FooterEnabled);
            Assert.False(card.ShowsCountIn(card.Groups()[0]));
            card.Pick("category", "Windows");
            Assert.Equal("Continue", card.FooterLabel);
            Assert.True(card.FooterEnabled);
        }

        [Fact]
        public void SeveralQuestions_KeepTheirCounters()
        {
            var card = TwoQuestions();
            Assert.False(card.IsSingleQuestion);
            var g = card.Groups()[0];
            Assert.True(card.ShowsCountIn(g));
            Assert.Equal("2 left", AskCardModel.CountLabel(card.LeftIn(g)));
            Assert.Equal("Answer 2 more", card.FooterLabel);
            card.Choose("type", "t1");
            Assert.Equal("1 left", AskCardModel.CountLabel(card.LeftIn(g)));
            Assert.Equal("Answer 1 more", card.FooterLabel);
            card.Pick("product", "Skip");
            Assert.Equal("done", AskCardModel.CountLabel(card.LeftIn(g)));
            Assert.Equal("Send (1)", card.FooterLabel);
        }

        [Fact]
        public void TheCardDrawsTheBadgeFromShowsCountIn()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Copilot", "Controls", "AskCard.cs"));
            Assert.Contains("_m.ShowsCountIn(g) ? Visibility.Visible", src);
            Assert.DoesNotContain("AskCardModel.CountsIn(g) ? Visibility.Visible", src);
        }
    }
}
