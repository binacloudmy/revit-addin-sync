#nullable enable
// Live test of 0.0.107: a run_revit_code write of 2097 elements showed no "Undo all". Every committed write offers it.
using System.IO;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoOffered0108Tests
    {
        private static Envelope Env(string phase, string status, string? mode = null) => new Envelope
        {
            Ok = true, Status = status, Phase = phase, Summary = "ran",
            Data = mode == null ? default : JsonDocument.Parse("{\"mode\":\"" + mode + "\"}").RootElement.Clone(),
        };

        [Fact]
        public void ACommitPhaseDoneOrPartialWrite_IsCommitted()
        {
            Assert.True(TurnEvidence.IsCommitted(Env(EnvelopePhase.Commit, EnvelopeStatus.Done)));
            Assert.True(TurnEvidence.IsCommitted(Env(EnvelopePhase.Commit, EnvelopeStatus.Partial)));
        }

        [Fact]
        public void APreviewOrAFailedWrite_IsNot()
        {
            Assert.False(TurnEvidence.IsCommitted(Env(EnvelopePhase.Preview, EnvelopeStatus.Done)));
            Assert.False(TurnEvidence.IsCommitted(Env(EnvelopePhase.Commit, EnvelopeStatus.Failed)));
            Assert.False(TurnEvidence.IsCommitted(Env(EnvelopePhase.Preview, EnvelopeStatus.Done, "write")));   // a rolled-back preview of code
        }

        [Fact]
        public void ACodeWriteWhoseEnvelopeDoesNotSayCommit_StillCounts()
        {
            Assert.True(TurnEvidence.IsCommitted(Env(EnvelopePhase.Read, EnvelopeStatus.Done, "write")));
            Assert.False(TurnEvidence.IsCommitted(Env(EnvelopePhase.Read, EnvelopeStatus.Done, "read")));
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheActionRow_OffersUndoAll_EvenWhenItIsOff_WithTheReasonAsATooltip()
        {
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("ActionRow(card, ms, actions, canUndo, undone, undoOff)", s);
            Assert.Contains("off.IsEnabled = false;", s);
            Assert.Contains("off.ToolTip = undoOff;", s);
            Assert.Contains("ToolTipService.SetShowOnDisabled(off, true);", s);
        }

        // the real turn of the live test: run_revit_code commit, done, mode write, touched ids EMPTY, counts by category only
        private static (ToolRequestDto Request, Envelope[] Envs) CodeWriteTurn(bool withPreviewFailure)
        {
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"Set Comments WIDE-TEST\"}").RootElement.Clone() };
            var request = new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } };
            var commit = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran: Set Comments WIDE-TEST on all non-grouped elements.",
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
                Changes = new ChangeReportDto
                {
                    TouchedByCategory = new SideEffectsDto { ModifiedByCategory = { ["Materials"] = 166, ["Walls"] = 818, ["Curtain Panels"] = 297, ["Views"] = 251 } },
                },
            };
            return (request, new[] { commit });
        }

        [Fact]
        public void ACodeWriteWithNoIds_IsAChangesCard_WithTheCountsByCategory_NotAFindingsCard()
        {
            var (request, envs) = CodeWriteTurn(false);
            var ev = new TurnEvidence();
            ev.Add(request, envs);
            Assert.True(ev.AnyCommitted);
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(ResultKind.Changes, card.Kind);
            Assert.Equal("1,532 elements changed".Replace(",", ""), card.Headline);
            Assert.Equal(("Walls", 818), card.CategoryCounts[0]);
            Assert.Equal(4, card.CategoryCounts.Count);
            Assert.Contains("Walls 818", card.Detail);
        }

        [Fact]
        public void TheCountsHeadlineInMalay_AndASingularElement()
        {
            var (request, envs) = CodeWriteTurn(false);
            var ev = new TurnEvidence(); ev.Add(request, envs);
            Assert.Equal("1532 elemen diubah", ResultCardModel.From(ev, ReceiptLanguage.Ms).Headline);
            var one = new TurnEvidence();
            var env = envs[0];
            env.Changes!.TouchedByCategory = new SideEffectsDto { ModifiedByCategory = { ["Walls"] = 1 } };
            one.Add(request, new[] { env });
            Assert.Equal("1 element changed", ResultCardModel.From(one, ReceiptLanguage.En).Headline);
        }

        [Fact]
        public void ACodeWriteThatTouchedNothing_StillMakesNoChangesCard()
        {
            var (request, envs) = CodeWriteTurn(false);
            envs[0].Changes!.TouchedByCategory = new SideEffectsDto();
            var ev = new TurnEvidence(); ev.Add(request, envs);
            Assert.NotEqual(ResultKind.Changes, ResultCardModel.From(ev, ReceiptLanguage.En).Kind);
        }

        [Fact]
        public void UndoAllIsOfferedOnAnyKindOfCard_NotOnlyChanges()
        {
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.DoesNotContain("if (card.Kind != ResultKind.Findings)\n            {\n                if (actions.Undo != null && canUndo)", s);
        }
    }
}
