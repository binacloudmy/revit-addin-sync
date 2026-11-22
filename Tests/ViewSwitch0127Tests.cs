#nullable enable
// Hand test 17 Nov (turn 452fe3c8): "Select the pipe with id 1598795." with the sheet "P000 - COVER SHEET" active. The find selected the pipe
// and noted the sheet is no model view; the code then opened the 3D view "Cover"; the report still said "the active view is COVER SHEET"
// from the older snapshot. The code run's result now names the view Revit ends in (data.ui.view_now, switched_from), and the card says
// "Opened view Cover (ThreeD)" and drops the "not visible" split counted in the view before the switch.
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ViewSwitch0127Tests
    {
        [Fact]
        public void Label_NamesTheViewAndItsType()
        {
            Assert.Equal("Cover (ThreeD)", UiViewReport.Label("Cover", "ThreeD"));
            Assert.Equal("COVER SHEET", UiViewReport.Label("COVER SHEET", null));
        }

        [Fact]
        public void Add_AViewChange_SaysWhereRevitIsAndWhereItWas()
        {
            var data = new Dictionary<string, object?>();
            UiViewReport.Add(data, 10, "P000 - COVER SHEET (DrawingSheet)", 20, "Cover (ThreeD)");
            Assert.Equal("Cover (ThreeD)", data["view_now"]);
            Assert.Equal("P000 - COVER SHEET (DrawingSheet)", data["switched_from"]);
        }

        [Fact]
        public void Add_NoChange_OnlyNamesTheView()
        {
            var data = new Dictionary<string, object?>();
            UiViewReport.Add(data, 20, "Cover (ThreeD)", 20, "Cover (ThreeD)");
            Assert.Equal("Cover (ThreeD)", data["view_now"]);
            Assert.False(data.ContainsKey("switched_from"));
        }

        [Fact]
        public void Add_NoViewKnown_AddsNothing()
        {
            var data = new Dictionary<string, object?>();
            UiViewReport.Add(data, 10, "x", null, null);
            Assert.Empty(data);
        }

        [Fact]
        public void Add_NoBefore_NamesOnlyTheNewView()
        {
            var data = new Dictionary<string, object?>();
            UiViewReport.Add(data, null, null, 20, "Cover (ThreeD)");
            Assert.Equal("Cover (ThreeD)", data["view_now"]);
            Assert.False(data.ContainsKey("switched_from"));
        }

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

        private static Envelope Code(string ui) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Ran (read only): open the 3D view.",
            Data = JsonDocument.Parse("{\"mode\":\"read\",\"output\":\"\",\"ui\":" + ui + "}").RootElement.Clone(),
        };

        private static TurnEvidence Turn(params (string Tool, Envelope Env)[] calls)
        {
            var ev = new TurnEvidence();
            foreach (var (tool, env) in calls) ev.Add(Req(tool, tool == ToolNames.RunRevitCode ? "{\"intent\":\"Open the 3D view\"}" : "{}"), new[] { env });
            return ev;
        }

        [Fact]
        public void NoteOf_ASwitchedView_IsTheCardsLine()
        {
            var note = UiViewReport.NoteOf(new[] { Code("{\"applied\":true,\"view\":20,\"view_now\":\"Cover (ThreeD)\",\"switched_from\":\"P000 - COVER SHEET (DrawingSheet)\"}") });
            Assert.Equal("Opened view Cover (ThreeD)", note);
        }

        [Fact]
        public void NoteOf_NoSwitch_IsEmpty()
        {
            Assert.Equal("", UiViewReport.NoteOf(new[] { Code("{\"applied\":true,\"view_now\":\"Cover (ThreeD)\"}") }));
            Assert.Equal("", UiViewReport.NoteOf(new[] { Find(1, 1) }));
        }

        [Fact]
        public void TheLiveTurn_TheCardSaysTheOpenedView_AndDropsTheStaleSplit()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(5, 3)),
                (ToolNames.RunRevitCode, Code("{\"applied\":true,\"view\":20,\"selected\":5,\"view_now\":\"Cover (ThreeD)\",\"switched_from\":\"P000 - COVER SHEET (DrawingSheet)\"}"))), ReceiptLanguage.En);
            Assert.Equal(ResultKind.Selection, card.Kind);
            Assert.Contains("Opened view Cover (ThreeD)", card.Notes);
            Assert.Equal(0, card.VisibleInView);
        }

        [Fact]
        public void NoSwitch_TheCardKeepsItsSplitAndAddsNoNote()
        {
            var card = ResultCardModel.From(Turn((ToolNames.FindElements, Find(52, 50))), ReceiptLanguage.En);
            Assert.DoesNotContain(card.Notes, n => n.StartsWith("Opened view"));
            Assert.Equal(50, card.VisibleInView);
        }
    }
}
