#nullable enable
// Live test of 0.0.98: "Isolate all the doors in this view" sometimes goes run_revit_code then report only (no selection first):
// the card was a plain "1/1 done / Undo" with no "Reset isolate" although the view stayed isolated.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class IsolateRoutes0098Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static ToolCallDto Call(string tool, string args = "{}") =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse(args).RootElement.Clone() };
        private static ToolRequestDto Request(string kind, params ToolCallDto[] calls) => new ToolRequestDto { RequestId = "r", Kind = kind, Calls = calls.ToList() };

        private static Envelope Found(int n) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = n + " found", Count = n,
            Highlight = new HighlightDto { Selected = n, VisibleInView = n },
        };

        private static string IsolateArgs(string intent = "Isolate all doors visible in the active view") =>
            JsonSerializer.Serialize(new { intent, code = "view.IsolateElementsTemporary(ids);" });

        private static Envelope CodeDone(string phase, string mode = "read") => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = phase, Summary = "ran", Count = 1,
            Data = JsonDocument.Parse("{\"mode\":\"" + mode + "\"}").RootElement.Clone(),
        };

        // Route 1: find_elements (highlight) then run_revit_code.
        private static ResultCardModel SelectThenIsolate()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.FindElements)), new[] { Found(56) });
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, IsolateArgs())), new[] { CodeDone(EnvelopePhase.Read) });
            return ResultCardModel.From(ev, ReceiptLanguage.En);
        }

        // Route 2: run_revit_code only, then report: nothing was selected first.
        private static ResultCardModel IsolateOnly(string mode = "read", string phase = EnvelopePhase.Read, string intent = "Isolate all doors visible in the active view")
        {
            var ev = new TurnEvidence();
            ev.Add(Request(mode == "write" ? ToolRequestKinds.Write : ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, IsolateArgs(intent))), new[] { CodeDone(phase, mode) });
            return ResultCardModel.From(ev, ReceiptLanguage.En);
        }

        [Fact]
        public void Route1_SelectionThenIsolate_IsASelectionCard_WithTheIsolateLineAndReset()
        {
            var m = SelectThenIsolate();

            Assert.Equal(ResultKind.Selection, m.Kind);
            Assert.True(m.Isolated);
            Assert.Equal("56 selected", m.Headline);
            var lines = SelectionLines.For(m, ReceiptLanguage.En);
            Assert.Equal("Selected in Revit: 56", lines[0]);
            Assert.StartsWith("This view is isolated", lines.Last());
        }

        [Fact]
        public void Route2_CodeThatOnlyIsolates_ReadMode_IsStillACard_ThatKnowsTheViewIsIsolated()
        {
            var m = IsolateOnly();

            Assert.Equal(ResultKind.Selection, m.Kind);        // not "no card": the reset must have somewhere to live
            Assert.True(m.Isolated);
            Assert.Equal("View isolated", m.Headline);
            Assert.Equal(0, m.Selected);
            var lines = SelectionLines.For(m, ReceiptLanguage.En);
            Assert.Equal(1, lines.Count);                       // no "Selected in Revit: 0"
            Assert.StartsWith("This view is isolated", lines[0]);
        }

        [Fact]
        public void Route2_WriteModeCommit_WithNoChangedValues_IsTheSameCard()
        {
            var m = IsolateOnly(mode: "write", phase: EnvelopePhase.Commit);

            Assert.Equal(ResultKind.Selection, m.Kind);
            Assert.True(m.Isolated);
            Assert.Equal("View isolated", m.Headline);
        }

        [Fact]
        public void Route2_InMalay_ReadsInMalay()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, IsolateArgs("Asingkan semua pintu"))), new[] { CodeDone(EnvelopePhase.Read) });
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, IsolateArgs("Isolate doors"))), new[] { CodeDone(EnvelopePhase.Read) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.Ms);

            Assert.Equal("View diasingkan", m.Headline);
            Assert.StartsWith("View ini diasingkan", SelectionLines.For(m, ReceiptLanguage.Ms)[0]);
        }

        [Fact]
        public void ACodeCallThatDoesNotIsolate_LeavesNoCard()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, JsonSerializer.Serialize(new { intent = "Count the doors", code = "var n = 1;" }))),
                new[] { CodeDone(EnvelopePhase.Read) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.False(m.Isolated);
            Assert.Equal(ResultKind.None, m.Kind);
        }

        [Fact]
        public void AFailedIsolate_IsNotAnIsolatedView()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, IsolateArgs())),
                new[] { new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Read, Summary = "failed" } });

            Assert.False(ResultCardModel.From(ev, ReceiptLanguage.En).Isolated);
        }

        [Fact]
        public void AWriteTurnThatAlsoIsolates_KeepsItsKind_ButKnowsTheViewIsIsolated()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Mark", Count = 1,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto> { new FieldChangeDto { Id = 1, Field = "Mark", Before = "", After = "D1" } } } },
                Data = JsonDocument.Parse("{\"param\":\"Mark\",\"type_param\":false,\"instances_category\":\"Doors\"}").RootElement.Clone(),
            };
            var ev = new TurnEvidence();
            ev.Add(Request(ToolRequestKinds.Write, Call(ToolNames.SetParameters)), new[] { env });
            ev.Add(Request(ToolRequestKinds.Read, Call(ToolNames.RunRevitCode, IsolateArgs())), new[] { CodeDone(EnvelopePhase.Read) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En);

            Assert.Equal(ResultKind.Changes, m.Kind);
            Assert.True(m.Isolated);
        }

        [Fact]
        public void TheCardView_GivesEveryKindOfCardTheIsolateLineAndReset_NotOnlyASelection()
        {
            var v = Src("UI/Copilot/Controls/ResultCardView.cs");
            var b = Src("UI/Copilot/Controls/SelectionBody.cs");

            Assert.Contains("if (card.Isolated && card.Kind != ResultKind.Selection) IsolatedBlock(root, card, lang, actions);", v);
            Assert.Contains("SelectionBody.IsolatedBlock(root, card, lang, actions)", v);   // the view only calls into the body class
            Assert.Contains("SelectionLines.IsolatedLines(lang)", b);
            Assert.True(b.Split("AddResetIsolate(").Length - 1 >= 3, "defined once, called from the selection card and from the isolated block");
            Assert.DoesNotContain("Serlahkan dalam model", b);   // result marks design, rule 3: a selection has no marks (Revit's selection is its mark), so no highlight button
        }
    }
}
