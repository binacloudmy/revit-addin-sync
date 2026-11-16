#nullable enable
// "Select the pipe with id 1598795." (live): find_elements selected all 3051 pipes first (highlight.selected 3051, visible_in_view 82), then
// run_revit_code selected the one pipe (data.ui.selected 1). The card said "Selected in Revit: 3051" and "2969 of the selected elements are
// not visible in this view" (the find's numbers). It now follows what Revit ends the turn with: the last call that selected, or the server's
// own count when it sends one.
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FinalSelection0121Tests
    {
        private static ToolRequestDto Req(string tool, string args = "{}") => new ToolRequestDto
        {
            RequestId = "r", Kind = ToolRequestKinds.Read,
            Calls = { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse(args).RootElement.Clone() } },
        };

        private static Envelope Find(int selected, int visible) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Found " + selected, Count = selected,
            Highlight = new HighlightDto { Selected = selected, VisibleInView = visible },
        };

        private static Envelope CodeSelected(int n) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Ran (read only): select the pipe.",
            Data = JsonDocument.Parse("{\"mode\":\"read\",\"output\":\"selected 1\",\"ui\":{\"selected\":" + n + "}}").RootElement.Clone(),
        };

        private static TurnEvidence Turn(params (string Tool, Envelope Env)[] calls)
        {
            var ev = new TurnEvidence();
            foreach (var (tool, env) in calls) ev.Add(Req(tool, tool == ToolNames.RunRevitCode ? "{\"intent\":\"Select the pipe\"}" : "{}"), new[] { env });
            return ev;
        }

        [Fact]
        public void TheLiveTurn_TheCodesSelectionOfOne_BeatsTheFindsOf3051()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(3051, 82)), (ToolNames.RunRevitCode, CodeSelected(1))), ReceiptLanguage.En);
            Assert.Equal(ResultKind.Selection, card.Kind);
            Assert.Equal(1, card.Selected);
            Assert.Equal(0, card.VisibleInView);                                 // unknown: no "not visible" claim
            var lines = SelectionLines.For(card, ReceiptLanguage.En);
            Assert.Equal("Selected in Revit: 1", lines[0]);
            Assert.DoesNotContain(lines, l => l.Contains("not visible"));
        }

        [Fact]
        public void ASingleFind_KeepsItsNumbersAndTheNotVisibleLine()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(52, 50))), ReceiptLanguage.En);
            Assert.Equal((52, 50), (card.Selected, card.VisibleInView));
            Assert.Contains("2 of the selected elements are not visible in this view.", SelectionLines.For(card, ReceiptLanguage.En));
        }

        [Fact]
        public void TwoFinds_TheLaterSelectionIsRevitsSelection_NotTheBiggerOne()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(3051, 82)), (ToolNames.FindElements, Find(12, 12))), ReceiptLanguage.En);
            Assert.Equal((12, 12), (card.Selected, card.VisibleInView));
        }

        [Fact]
        public void TheServersCount_IsTheFinalSelection_WhenTheReceiptHoldsAnEarlierBiggerOne()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(3051, 82))), ReceiptLanguage.En);
            Assert.Equal(3051, card.Selected);
            var result = new TurnResultDto { Reply = "Selected pipe 1598795.", Kind = TurnResultKinds.Selection, Counts = new ResultCountsDto { Elements = 1 } };
            card.WithServerResult(result);
            Assert.Equal(1, card.Selected);
            Assert.Equal(0, card.VisibleInView);
            Assert.DoesNotContain(SelectionLines.For(card, ReceiptLanguage.En), l => l.Contains("not visible"));
        }

        [Fact]
        public void ASameCountOrAnotherKindOfResult_LeavesTheCardAlone()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(52, 50))), ReceiptLanguage.En);
            card.WithServerResult(new TurnResultDto { Reply = "52 selected.", Kind = TurnResultKinds.Selection, Counts = new ResultCountsDto { Elements = 52 } });
            Assert.Equal((52, 50), (card.Selected, card.VisibleInView));
            var other = ResultCardModel.From(Turn((ToolNames.FindElements, Find(52, 50))), ReceiptLanguage.En);
            other.WithServerResult(new TurnResultDto { Reply = "x", Kind = TurnResultKinds.Findings, Counts = new ResultCountsDto { Elements = 1 } });
            Assert.Equal((52, 50), (other.Selected, other.VisibleInView));
        }

        [Fact]
        public void NoSelectionAnywhere_GivesNoSelectionCard()
        {
            Assert.Equal((0, 0), ResultCardModel.FinalSelection(new[] { new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "x" } }));
        }
    }
}
