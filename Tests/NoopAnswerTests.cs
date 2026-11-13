#nullable enable
// A correct no-op (a tag re-run: "All 56 doors are already tagged; nothing added.") is a plain Done answer: no banner, no issue card,
// no "Check receipt", no highlight chip, and the server's sentence is the headline.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NoopAnswerTests
    {
        private const string Reply = "All 56 doors in Aras 01 are already tagged; nothing added.";

        private static ToolRequestDto Request(string tool) => new ToolRequestDto
        {
            RequestId = "r", Kind = ToolRequestKinds.Write,
            Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse("{}").RootElement.Clone() } },
        };

        private static Envelope Noop(bool withWarning) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Commit, Summary = "The code ran but changed nothing.", Count = 0,
            Warnings = withWarning ? new List<WarningDto> { new WarningDto { Text = "The code ran but changed nothing.", Severity = WarningSeverity.Warning } } : null,
        };

        private static TurnEvidence Evidence(Envelope env, string tool = ToolNames.RunRevitCode)
        {
            var ev = new TurnEvidence();
            ev.Add(Request(tool), new[] { env });
            return ev;
        }

        [Fact]
        public void ADeclaredNoop_HasNoCaution()
        {
            var ev = Evidence(Noop(withWarning: false));
            Assert.False(ev.Caution);
        }

        [Fact]
        public void ANoopThatWarns_StillHasCaution() =>
            Assert.True(Evidence(Noop(withWarning: true)).Caution);

        [Fact]
        public void AFailedWrite_StillHasCaution()
        {
            var failed = new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Commit, Summary = "x" };
            Assert.True(Evidence(failed).Caution);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ANoop_NeverMakesAnIssueCard(bool withWarning)
        {
            var card = ResultCardModel.From(Evidence(Noop(withWarning)), ReceiptLanguage.En);
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.Empty(card.Findings);
            Assert.True(card.NoChange);
        }

        [Fact]
        public void ANoop_UsesTheServersSentenceAsTheHeadline()
        {
            var result = new TurnResultDto { Reply = Reply, Kind = TurnResultKinds.Changes };
            var card = ResultCardModel.From(Evidence(Noop(withWarning: false)), ReceiptLanguage.En).WithServerResult(result);
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.True(card.ProseResult);
            Assert.Equal(Reply, card.ServerReply);
        }

        [Theory]
        [InlineData(TurnResultKinds.Changes)]
        [InlineData(TurnResultKinds.Selection)]
        [InlineData(TurnResultKinds.Findings)]
        public void AShortServerReplyWithNoCard_IsTheHeadline(string kind)
        {
            var result = new TurnResultDto { Reply = "No door tags were added: all 56 doors visible in Aras 01 already have tags.", Kind = kind, Counts = new ResultCountsDto { Elements = 56 } };
            var card = ResultCardModel.From(new TurnEvidence(), ReceiptLanguage.En).WithServerResult(result);
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.True(card.ProseResult);
        }

        [Fact]
        public void ANullServerResult_IsNeverAHeadline() =>
            Assert.False(new ResultCardModel().WithServerResult(null).ProseResult);

        [Fact]
        public void TheViewModel_BuildsTheCardModelWheneverTheServerSentAResult() =>
            Assert.Contains("rr.Evidence != null || rr.ServerResult != null", Src("UI/Copilot/CopilotViewModel.cs"));

        private static Envelope PreviewNoop() => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Preview, Summary = "The code ran but changed nothing.", Count = 0,
        };

        [Fact]
        public void APreviewThatMeasuredNoChange_IsTheFinalNoop()
        {
            // A batch whose preview shows no change ends there (no confirm card): the preview's noop envelope is the result.
            var card = ResultCardModel.From(Evidence(PreviewNoop()), ReceiptLanguage.En);
            Assert.True(card.NoChange);
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.Empty(card.Findings);
        }

        [Fact]
        public void ACodeWriteThatChangedNothingAndWarnedNot_HasOnlyCleanNoops()
        {
            var ev = Evidence(PreviewNoop());
            Assert.True(ev.OnlyCleanNoops);   // no "0/1 done · 1 unchanged" bar
            Assert.False(ev.Caution);         // no banner, no "Check receipt"
        }

        [Fact]
        public void ANoopThatWarned_IsNotClean()
        {
            var ev = Evidence(Noop(withWarning: true));
            Assert.False(ev.OnlyCleanNoops);
            Assert.True(ev.Caution);
        }

        [Fact]
        public void ATurnWithARealWriteAlongsideANoop_StillDrawsItsReceipt()
        {
            var ev = Evidence(PreviewNoop());
            var failed = new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Commit, Summary = "x" };
            ev.Add(Request(ToolNames.RunRevitCode), new[] { failed });
            Assert.False(ev.OnlyCleanNoops);
            Assert.True(ev.Caution);
        }

        [Fact]
        public void ATurnWithNoWrites_HasNoCleanNoops() => Assert.False(new TurnEvidence().OnlyCleanNoops);

        [Fact]
        public void TheReceiptBar_IsNotDrawnForCleanNoops() =>
            Assert.Contains("!ev.OnlyCleanNoops", Src("UI/Copilot/Controls/TurnEvidenceView.cs"));

        [Fact]
        public void ANoopWithNoServerReply_UsesTheModelsFirstSentenceAsTheHeadline() =>
            Assert.Contains("m.ResultCard.NoChange && Model.ProseSplit.IsPlainProse(m.Text, 6)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));

        [Fact]
        public void ACommittedWrite_IsNotANoop()
        {
            var done = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Tagged 3 doors", Count = 3,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Added = new List<ElementRef> { new ElementRef(1, null), new ElementRef(2, null), new ElementRef(3, null) } } },
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
            };
            var card = ResultCardModel.From(Evidence(done), ReceiptLanguage.En);
            Assert.False(card.NoChange);
            Assert.Equal(ResultKind.Created, card.Kind);
        }

        [Fact]
        public void AReadOnlyTurn_IsNotANoop() =>
            Assert.False(ResultCardModel.From(new TurnEvidence(), ReceiptLanguage.En).NoChange);

        // ---- engine and chat wiring (source text) ----------------------------------------------

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UI", "Copilot", "Model", "ProseSplit.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheEngine_WarnsAboutAChangedNothingRunOnlyWhenItDidNotDeclareThat()
        {
            var engine = Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs");
            Assert.Contains("if (declaredChange)", engine);
            Assert.Contains("plan.DeclaredChange", engine);
            Assert.Contains("env.Status = EnvelopeStatus.Noop;", engine);   // the status stays noop either way
        }

        [Fact]
        public void TheChatView_ShowsNoHighlightChipForANoop() =>
            Assert.Contains("!(m.ResultCard != null && m.ResultCard.NoChange)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
    }
}
