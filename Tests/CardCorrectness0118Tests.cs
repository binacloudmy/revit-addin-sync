#nullable enable
// Overnight fixes from the RC 0.0.117 recordings: the isolate card only for a turn that itself isolated, and one headline on a card.
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardCorrectness0118Tests
    {
        // ---- (1) the isolate card ---------------------------------------------------------------

        [Theory]
        [InlineData("Isolate all doors", "view.IsolateElementsTemporary(ids);")]
        [InlineData("show them", "view.IsolateCategoriesTemporary(cats);")]
        [InlineData("show them", "view.EnableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);")]
        [InlineData("Isolate the 52 downlights in the view", "")]
        [InlineData("Temporarily isolate the doors", "")]
        public void ATurnThatIsolates_IsMarked(string intent, string code) => Assert.True(ResultCardModel.IsolatesView(intent, code));

        [Theory]
        // the tag no-op turn: it mentions the isolated view but only tags
        [InlineData("Tag all untagged doors in the isolated view", "foreach (var d in doors) { /* the view is isolated */ IndependentTag.Create(doc, view.Id, r, false, 0, 0, p); }")]
        [InlineData("Tag the isolated doors", "")]
        // the reset turn
        [InlineData("Reset the temporary hide/isolate", "view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);")]
        [InlineData("Reset the temporary hide/isolate", "")]
        [InlineData("Turn off the isolate in this view", "")]
        [InlineData("Count the isolated elements", "")]
        [InlineData("Select the doors", "uidoc.Selection.SetElementIds(ids);")]
        public void ATurnThatOnlyMentionsIsolation_IsNotMarked(string intent, string code) => Assert.False(ResultCardModel.IsolatesView(intent, code));

        // ---- (2) one headline ---------------------------------------------------------------------

        private static ResultCardModel CountCard() => new ResultCardModel
        {
            Kind = ResultKind.Changes,
            CategoryCounts = new System.Collections.Generic.List<(string, int)> { ("Center Line", 14), ("Pipes", 14), ("class:Element", 1) },
        };

        private const string Slope = "2% slope applied to 14 of the 36 selected pipes; 22 vertical risers were skipped because they have no slope.";

        [Fact]
        public void WithNoServerReply_TheFirstSentenceOfTheModelsTextIsTheHeadline()
        {
            var card = CountCard();
            Assert.True(card.AdoptProseHeadline(Slope + " Everything else is unchanged."));
            Assert.Equal(Slope, card.Headline);
            Assert.Equal(Slope, card.ServerReply);
            Assert.True(card.ReplacesProse);
            Assert.Equal("Everything else is unchanged.", card.AdoptedRest);
        }

        [Fact]
        public void ASingleSentenceHasNoRest()
        {
            var card = CountCard();
            Assert.True(card.AdoptProseHeadline(Slope));
            Assert.Equal("", card.AdoptedRest);
        }

        [Fact]
        public void AServerReplyIsNeverReplaced()
        {
            var card = CountCard();
            card.ServerReply = "Set the slope on 14 pipes.";
            Assert.False(card.AdoptProseHeadline(Slope));
            Assert.Equal("", card.AdoptedRest);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("Done.")]
        [InlineData("| a | b |\n|---|---|\n| 1 | 2 | and more words here")]
        [InlineData("- first item of a long list of words\n- second")]
        [InlineData("# Heading with several words in it")]
        public void StructuredOrTinyTextIsLeftAlone(string? text)
        {
            var card = CountCard();
            Assert.False(card.AdoptProseHeadline(text));
            Assert.Equal("", card.ServerReply);
        }

        [Theory]
        [InlineData(ResultKind.Findings)]
        [InlineData(ResultKind.Selection)]
        [InlineData(ResultKind.None)]
        public void OnlyChangesAndCreatedCardsAdopt(ResultKind kind)
        {
            var card = new ResultCardModel { Kind = kind };
            Assert.False(card.AdoptProseHeadline(Slope));
        }

        [Fact]
        public void TheCountLineStaysAsTheDetail()
        {
            var card = CountCard();
            card.AdoptProseHeadline(Slope);
            card.RefreshHeadline(ReceiptLanguage.En);
            Assert.Equal(Slope, card.Headline);   // the refresh keeps the adopted sentence as the headline
        }

        // ---- wiring ---------------------------------------------------------------------------------

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UI", "Copilot", "Model", "ProseSplit.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheChatView_AdoptsTheHeadlineBeforeItDecidesWhatToHide()
        {
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            var adopt = chat.IndexOf("m.ResultCard.AdoptProseHeadline(m.Text)", System.StringComparison.Ordinal);
            var hide = chat.IndexOf("!(cardShown && m.ResultCard.ReplacesProse)", System.StringComparison.Ordinal);
            Assert.True(adopt > 0 && hide > adopt);
        }
    }
}
