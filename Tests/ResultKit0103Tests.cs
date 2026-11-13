#nullable enable
// Part 2 of the design order: dark theme at the board's values, three button styles, headline outside the card.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultKit0103Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        // ---- the dark theme at the board's values ----

        [Theory]
        [InlineData("Cp.PanelBg", "#0E1014")]
        [InlineData("Cp.Bg", "#15181E")]
        [InlineData("Cp.Menu", "#1B1F27")]
        [InlineData("Cp.Raised", "#1B1F27")]
        [InlineData("Cp.Sunken", "#121419")]
        [InlineData("Cp.Ink", "#EDEFF3")]
        [InlineData("Cp.Muted", "#A6ADBA")]
        [InlineData("Cp.Faint", "#8A91A0")]
        [InlineData("Cp.Accent", "#D2F65A")]
        [InlineData("Cp.Green", "#5BD69A")]
        [InlineData("Cp.Amber", "#F5B841")]
        [InlineData("Cp.AmberBg", "#221C10")]
        [InlineData("Cp.AccentContrast", "#0E1014")]
        public void TheDarkColumn_IsTheBoardsValue(string key, string dark) =>
            Assert.Equal(dark, ThemeTokens.Solid[key].Dark, ignoreCase: true);

        [Fact]
        public void TheLightColumn_KeepsItsAccentReadableOnWhite()
        {
            // The board has no light theme; the light accent stays the blue that reads on a white ground.
            Assert.Equal("#2a69c6", ThemeTokens.Solid["Cp.Accent"].Light, ignoreCase: true);
            Assert.False(string.Equals(ThemeTokens.Solid["Cp.Accent"].Light, ThemeTokens.Solid["Cp.Accent"].Dark, System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void TheWholePaneUsesGeist_WithInterAsTheFallback()
        {
            var xaml = Src("UI/Copilot/CopilotTokens.xaml");
            Assert.Matches("x:Key=\"Cp.Font\">pack://application:,,,/RevitWebAppSync;component/Fonts/#Geist, pack://application:,,,/RevitWebAppSync;component/Fonts/#Inter", xaml);
            Assert.Contains("Fonts/#Geist Mono", xaml);
        }

        // ---- the three buttons ----

        [Fact]
        public void CardKit_HasSecondaryGhostPrimary_ToTheBoardsSizes()
        {
            var s = Src("UI/Copilot/Controls/CardKit.cs");
            Assert.Contains("public static Button Secondary(", s);
            Assert.Contains("public static Button Ghost(", s);
            Assert.Contains("public static Button Primary(", s);
            Assert.Contains("MinHeight = compact ? 30 : 32", s);
            Assert.Contains("CornerRadius='8'", s);
            Assert.Contains("\"Cp.Raised\"", s);
            Assert.Contains("\"Cp.Hair2\"", s);
        }

        [Fact]
        public void NoCallSiteUsesTheOldPill()
        {
            // The type tabs were the last "type . n" pill; the Changes body now draws the types as tiles.
            var hits = new List<string>();
            foreach (var f in Directory.GetFiles(Path.Combine(Root(), "UI"), "*.cs", SearchOption.AllDirectories))
            {
                if (f.EndsWith("CardKit.cs")) continue;
                foreach (var line in File.ReadAllLines(f).Where(l => l.Contains("CardKit.Chip(")))
                    hits.Add(Path.GetFileName(f) + ": " + line.Trim());
            }
            Assert.Empty(hits);
        }

        [Fact]
        public void TheOldStyles_AreNotUsedByTheCards()
        {
            foreach (var f in new[] { "ResultCardView", "CreatedCard", "ItemsCard", "SourcePanel", "TurnEvidenceView", "AskCard" })
            {
                var s = Src("UI/Copilot/Controls/" + f + ".cs");
                Assert.DoesNotContain("Cp.RunButton", s);
                Assert.DoesNotContain("FlatButton.Apply(", s);
            }
        }

        [Fact]
        public void TheGhostAndPrimaryRoles_AreWhereTheBoardPutsThem()
        {
            // The Changes body moved to ChangesBody.cs. The Partial board draws "Write these values" and "Show in Revit"
            // as the 32px button (Secondary), and only "Leave as is" and "Keep all as written" as ghosts.
            var s = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.Contains("CardKit.Ghost(ms ? \"Biarkan\" : \"Leave as is\"", s);
            Assert.Contains("CardKit.Ghost(ms ? \"Kekalkan semua\" : \"Keep all as written\"", s);
            Assert.Contains("CardKit.Secondary(ms ? \"Tunjuk dalam Revit\" : \"Show in Revit\"", s);
            Assert.Contains("CardKit.Secondary(ms ? \"Tulis nilai ini\" : \"Write these values\"", s);
        }

        // ---- headline outside the card, chips, one action row ----

        [Fact]
        public void TheResultCard_IsAHeadlineThenChipsThenANeutralCardThenOneActionRow()
        {
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("block.Children.Add(HeadlineRow(card, actions, canUndo, undone, undoOff))", s);   // Undo beside the headline
            Assert.Contains("StatusChips(card, ms, actions)", s);
            Assert.Contains("CardKit.Card(\"Cp.Line\", \"Cp.Bg\"", s);
            Assert.Contains("ActionRow(card, ms, actions, canUndo, undone, undoOff)", s);
            Assert.Contains("CardKit.Text(CardText.Headline(card.Headline), 17", s);
            Assert.Contains("card.ReadBackText(", s);
            Assert.Contains("Read back from Revit: ", Src("UI/Copilot/Model/ResultCardModel.cs"));
            Assert.DoesNotContain("new Border { Padding = new Thickness(12, 8, 10, 8), BorderThickness = new Thickness(0, 0, 0, 1) };\n            head.", s);
            Assert.DoesNotContain("new Expander", Src("UI/Copilot/Controls/TurnEvidenceView.cs").Split("if (ev.HasWrites && !hasCard)")[0]);
        }

        [Fact]
        public void TheCardClipsItsChildrenToTheRoundedOutline()
        {
            var s = Src("UI/Copilot/Controls/CardKit.cs");
            Assert.Contains("new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius)", s);
        }

        [Fact]
        public void TheChatDrawsTheLabelAndLeavesTheProseAndTheSaveRowToTheCard()
        {
            var s = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("bool cardShown = showEvidence", s);
            Assert.Contains("!(cardShown && m.ResultCard.ReplacesProse)", s);
            Assert.Contains("evActions.SaveAsCommand = () => Vm?.OpenSaveCommandSheet(m)", s);
            Assert.Contains("if (canSave && !cardShown)", s);
        }

        // ---- the server's reply is the headline ----

        private static TurnResultDto Server(string reply, string kind = "created") => new TurnResultDto { Reply = reply, Kind = kind };

        [Fact]
        public void AServerReply_BecomesTheHeadlineOfAnyCard_AndReplacesTheProse()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes, Headline = "3 changes applied" }
                .WithServerResult(Server("I filled Model and URL on both types."));
            Assert.Equal("I filled Model and URL on both types.", card.Headline);
            Assert.True(card.ReplacesProse);
        }

        [Fact]
        public void WithoutAServerReply_TheEnvelopeHeadlineStays_AndTheProseIsKept()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes, Headline = "3 changes applied" }.WithServerResult(null);
            Assert.Equal("3 changes applied", card.Headline);
            Assert.False(card.ReplacesProse);
        }

        [Fact]
        public void AReplyWithNoCard_ReplacesNothing()
        {
            var card = new ResultCardModel().WithServerResult(Server("Hello."));
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.False(card.ReplacesProse);
        }
    }
}
