#nullable enable
// r12 (live dcf2ce71): a turn that ended failed (turn_end failed, a refused-only turn) still drew the green "done" disc and the
// find table's Export. A failed turn's card and prose wear the amber "!" disc, and offer no Export, no Download CSV, no Save as command.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FailedTurnCard0145Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void AFailedTurnsCard_NeverWearsTheGreenDisc()
        {
            var card = new ResultCardModel { Kind = ResultKind.Selection, Selected = 6 };
            Assert.False(card.HeadlineWarns);   // a done find: green check
            card.Failed = true;
            Assert.True(card.HeadlineWarns);    // failed: amber "!"
        }

        [Fact]
        public void AFailedTurn_OffersNoExport_AndNoSaveAsCommand()
        {
            Assert.True(TurnStatus.CanExport(false));
            Assert.False(TurnStatus.CanExport(true));
            Assert.False(TurnStatus.CanSaveAsCommand(true, 1, "Select all the fire doors", false, failed: true));
        }

        [Fact]
        public void TheWiring_GatesTheDiscAndTheExportOnFailed()
        {
            var view = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("if (m.ResultCard != null) m.ResultCard.Failed = m.Failed;", view);
            Assert.Contains("if (!TurnStatus.CanExport(m.Failed)) evActions.Export = null;", view);
            Assert.Contains("bool tableDownloads = TurnStatus.CanExport(m.Failed) && !evidenceExports;", view);
            Assert.Contains("warn: m.Failed || (m.ResultCard != null && m.ResultCard.ClaimMismatch)", view);
            Assert.Contains("var warn = card.HeadlineWarns;", Src("UI/Copilot/Controls/ResultCardView.cs"));
            Assert.Contains("ResultCardView.HeadlineText(headline, warn: warn, info: info && !warn)", Src("UI/Copilot/Controls/ProseAnswer.cs"));   // r22 P5: plus the info tone
        }
    }
}
