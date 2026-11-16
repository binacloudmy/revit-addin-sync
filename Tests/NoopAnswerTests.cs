#nullable enable
// A correct no-op (a tag re-run: "All 56 doors are already tagged; nothing added.") is a plain Done answer: no banner, no issue card,
// no "Check receipt", no highlight chip, and the server's sentence is the headline.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Highlights;
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
        public void ANoopThatWarns_ButClaimsNothing_HasNoCaution() =>
            // The narrow rule (user, 16 Nov): a code no-op is quiet unless a declaration or the server's result claims a change (see the claim tests below).
            Assert.False(Evidence(Noop(withWarning: true)).Caution);

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
            Assert.Contains("rr.Evidence != null || serverResult != null", Src("UI/Copilot/CopilotViewModel.cs"));

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
            Assert.Contains("if (!declaredNoChange)", engine);
            Assert.Contains("plan.DeclaredNoChange", engine);
            Assert.Contains("env.Status = EnvelopeStatus.Noop;", engine);   // the status stays noop either way
        }

        [Fact]
        public void TheChatView_ShowsNoHighlightChipForANoop() =>
            Assert.Contains("!(m.ResultCard != null && m.ResultCard.NoChange)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));

        // ---- redone on the senior dev's code: an already-tagged re-run ends as a plain no-op ----

        private static Envelope PreviewNoop() => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Preview, Summary = "The code ran but changed nothing.", Count = 0,
        };

        [Fact]
        public void APreviewThatMeasuredNoChange_IsTheFinalNoop()
        {
            var card = ResultCardModel.From(Evidence(PreviewNoop()), ReceiptLanguage.En);
            Assert.True(card.NoChange);
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.Empty(card.Findings);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CodeThatOnlyChangedNothing_DrawsNoReceiptBar_NoCaution(bool withWarning)
        {
            var ev = Evidence(withWarning ? Noop(true) : PreviewNoop());
            Assert.True(ev.OnlyNoopCode);   // no "0/1 done . 1 unchanged" bar
            Assert.False(ev.Caution);       // no banner, no "Check receipt"
        }

        [Fact]
        public void ARealFailureBesideANoop_StillDrawsTheReceipt()
        {
            var ev = Evidence(PreviewNoop());
            var failed = new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Commit, Summary = "x" };
            ev.Add(Request(ToolNames.RunRevitCode), new[] { failed });
            Assert.False(ev.OnlyNoopCode);
            Assert.True(ev.Caution);
        }

        [Fact]
        public void ASetParametersNoop_StillDrawsTheReceipt() => Assert.False(Evidence(PreviewNoop(), ToolNames.SetParameters).OnlyNoopCode);

        [Fact]
        public void ATurnWithNoWrites_HasNoNoopOnlyReceipt() => Assert.False(new TurnEvidence().OnlyNoopCode);

        [Fact]
        public void TheReceiptBar_IsNotDrawnForANoopOnlyTurn_AndAPlainNoopSentenceIsTheHeadline()
        {
            Assert.Contains("!ev.OnlyNoopCode", Src("UI/Copilot/Controls/TurnEvidenceView.cs"));
            Assert.Contains("m.ResultCard.NoChange && Model.ProseSplit.IsPlainProse(m.Text, 6)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
        }

        // ---- a "changes" result with ALL-ZERO counts after a no-op code run (slope on pipes connected at both ends) ----

        private const string SlopeReply = "No pipe was re-sloped: all 12 run between connected fittings, and Revit will not move them.";
        private const string Chip = "Re-slope the connected runs too";

        private static TurnResultDto ZeroChanges(bool withChips = true) => new TurnResultDto
        {
            Reply = SlopeReply, Kind = TurnResultKinds.Changes,
            Counts = new ResultCountsDto { Elements = 0, Values = 0, Written = 0 },
            Checks = withChips ? new List<ResultCheckDto>
            {
                new ResultCheckDto { Severity = "info", Text = "12 pipes are connected at both ends." },
                new ResultCheckDto { Severity = "warning", Text = "Slope was not applied.", Actions = new List<ResultActionDto>
                {
                    new ResultActionDto { Label = Chip, Kind = ResultActionKinds.Prompt, Args = JsonDocument.Parse("{\"text\":\"" + Chip + "\"}").RootElement.Clone() },
                } },
            } : null,
            Next = withChips ? new List<string> { Chip } : null,
        };

        [Fact]
        public void AZeroCountChangesResult_AfterANoopRun_ShowsTheHeadlineAndTheChips()
        {
            var card = ResultCardModel.From(Evidence(PreviewNoop()), ReceiptLanguage.En).WithServerResult(ZeroChanges());
            Assert.Equal(SlopeReply, card.Headline);              // the server's sentence, not "0 elements changed"
            Assert.True(card.ReplacesProse);
            Assert.NotEqual(ResultKind.None, card.Kind);          // a card is drawn, so its checks and Next chips are
            Assert.Equal(ResultKind.Created, card.Kind);          // no change: not the Changes card (no read-back chip, no Undo)
            Assert.Equal("", card.Counts);
            Assert.Equal("", card.Detail);
            Assert.Equal(0, card.ChangeCount);
            Assert.Equal(new[] { Chip }, card.Next);
            Assert.Equal(2, card.Checks.Count);
            Assert.Contains(card.Checks, c => c.Actions.Any(a => a.Label == Chip));
        }

        [Fact]
        public void AZeroCountChangesResult_DrawsNoBar_NoReceipt_NoBanner()
        {
            var ev = Evidence(PreviewNoop());
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(ZeroChanges());
            Assert.False(ev.Caution);                              // no amber banner, no "Check receipt"
            Assert.True(ev.OnlyNoopCode);                          // no "0/1 done · 1 unchanged" receipt bar
            Assert.True(card.NoChange);                            // no "Highlight in model" chip
            Assert.Empty(card.Findings);                           // no "1 issue" card from the noop's own note
            var set = HighlightSet.From(card, ev.Calls.Select(c => c.Env).ToList());
            Assert.True(set.IsEmpty);                              // nothing to mark: no slim bar, no dots, no selection
        }

        [Fact]
        public void AZeroCountChangesResult_WithNoChips_IsTheBareHeadline()
        {
            var card = ResultCardModel.From(Evidence(PreviewNoop()), ReceiptLanguage.En).WithServerResult(ZeroChanges(withChips: false));
            Assert.Equal(ResultKind.None, card.Kind);
            Assert.True(card.ProseResult);
            Assert.Equal(SlopeReply, card.ServerReply);
        }
        // ---- a failed turn is never the green check-disc headline ----

        [Fact]
        public void AFailedTurn_HasNoServerResultForTheCard_EvenIfOneCame()
        {
            var result = new TurnResultDto { Reply = "Done.", Kind = TurnResultKinds.Selection };
            Assert.Same(result, TurnStatus.ServerResultOf(new RouteResult { ServerResult = result }));                                  // a done turn keeps it
            Assert.Null(TurnStatus.ServerResultOf(new RouteResult { ServerResult = result, EndedFailed = true }));                    // ended "failed", text shown
            Assert.Null(TurnStatus.ServerResultOf(new RouteResult { ServerResult = result, Failed = true }));
            Assert.Null(TurnStatus.ServerResultOf(null));
        }

        [Fact]
        public void TheViewModel_GivesTheCardOnlyTheResultOfATurnThatDidNotFail() =>
            Assert.Contains(".WithServerResult(serverResult)", Src("UI/Copilot/CopilotViewModel.cs"));
        // ---- the narrow no-op rule A+B (user's decision 16 Nov; DELIBERATELY narrower than the senior dev's "every code no-op is quiet") ----

        private static TurnEvidence EvidenceWith(string argsJson, Envelope env, string tool = ToolNames.RunRevitCode)
        {
            var request = new ToolRequestDto
            {
                RequestId = "r", Kind = ToolRequestKinds.Write,
                Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse(argsJson).RootElement.Clone() } },
            };
            var ev = new TurnEvidence();
            ev.Add(request, new[] { env });
            return ev;
        }

        private const string DeclaredAdds = "{\"intent\":\"Create 5 sheets\",\"expected\":{\"adds\":5,\"modifies\":0,\"deletes\":0}}";
        private const string DeclaredZero = "{\"intent\":\"Tag all doors\",\"expected\":{\"adds\":0,\"modifies\":0,\"deletes\":0}}";

        [Fact]
        public void A_ANoopThatDeclaredChanges_RaisesTheCaution_TheNote_AndTheReceiptBar()
        {
            var ev = EvidenceWith(DeclaredAdds, Noop(withWarning: true));
            Assert.True(ev.Caution);
            Assert.True(ev.NoopContradictsClaim);
            Assert.False(ev.OnlyNoopCode);                     // the receipt bar is drawn
            Assert.Contains(TurnEvidence.ClaimNote, ev.Notes);
        }

        [Fact]
        public void A_DeclaredZero_OrAStoredPlaybook_OrNoDeclaration_StaysQuiet()
        {
            Assert.False(EvidenceWith(DeclaredZero, Noop(false)).Caution);
            Assert.False(EvidenceWith("{\"intent\":\"x\",\"playbook\":\"tag-all-in-view\",\"expected\":{\"adds\":5}}", Noop(false)).Caution);
            Assert.False(EvidenceWith("{\"intent\":\"x\"}", Noop(true)).Caution);
            Assert.False(EvidenceWith("{}", Noop(true)).NoopContradictsClaim);
        }

        private static TurnResultDto Result(string kind, ResultCountsDto? counts = null, List<ResultItemDto>? items = null) =>
            new TurnResultDto { Reply = "Done.", Kind = kind, Counts = counts, Items = items };

        [Theory]
        [InlineData(TurnResultKinds.Changes, 56, 0, 0, true)]
        [InlineData(TurnResultKinds.Created, 0, 0, 5, true)]
        [InlineData(TurnResultKinds.Changes, 0, 3, 0, true)]
        [InlineData(TurnResultKinds.Changes, 0, 0, 0, false)]       // all zero: be dev's slope chip, the tag re-run
        [InlineData(TurnResultKinds.Selection, 12, 0, 0, false)]
        [InlineData(TurnResultKinds.Findings, 9, 0, 0, false)]
        public void B_TheServerResultClaimsAChange_OnlyForChangesOrCreatedWithCounts(string kind, int elements, int written, int created, bool claims)
        {
            var counts = new ResultCountsDto { Elements = elements, Written = written, Created = created };
            Assert.Equal(claims, TurnEvidence.ServerClaimsChange(Result(kind, counts)));
            var ev = EvidenceWith("{\"intent\":\"x\"}", Noop(false));
            ev.NoteServerResult(Result(kind, counts));
            Assert.Equal(claims, ev.Caution);
            Assert.Equal(claims, ev.NoopContradictsClaim);
        }

        [Fact]
        public void B_RowsWithElementIds_AreAClaim_RowsWithoutAreNot()
        {
            var ids = new List<System.Text.Json.JsonElement> { JsonDocument.Parse("101").RootElement.Clone() };
            Assert.True(TurnEvidence.ServerClaimsChange(Result(TurnResultKinds.Changes, null, new List<ResultItemDto> { new ResultItemDto { Title = "Doors", Ids = ids } })));
            Assert.False(TurnEvidence.ServerClaimsChange(Result(TurnResultKinds.Changes, null, new List<ResultItemDto> { new ResultItemDto { Title = "Doors" } })));
            Assert.False(TurnEvidence.ServerClaimsChange(null));
        }

        [Fact]
        public void TheClaims_AreOffWhenAnotherWriteOfTheTurnWasDone_OrTheScriptStoppedOnPurpose()
        {
            var ev = EvidenceWith(DeclaredAdds, Noop(false));
            var done = new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Made 5 sheets" };
            ev.Add(Request(ToolNames.RunRevitCode), new[] { done });
            Assert.False(ev.NoopContradictsClaim);             // a real write in the turn: the no-op is a retry or the rest of a batch
            var stop = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Commit, Summary = "x",
                Data = JsonDocument.Parse("{\"mode\":\"write\",\"output\":\"NOTHING MADE: ask each ASK\"}").RootElement.Clone(),
            };
            Assert.False(EvidenceWith(DeclaredAdds, stop).NoopContradictsClaim);
        }

        [Fact]
        public void ASetParametersNoop_IsUnchangedByTheRule()
        {
            var ev = EvidenceWith(DeclaredAdds, Noop(false), ToolNames.SetParameters);
            Assert.True(ev.Caution);                           // set_parameters keeps its own caution (a target may have been skipped)
            Assert.False(ev.NoopContradictsClaim);
        }

        [Fact]
        public void TheCard_TurnsItsHeadlineDiscAmber_ForAClaimedNoop_AndNotForACorrectOne()
        {
            var claimed = EvidenceWith(DeclaredAdds, PreviewNoop());
            Assert.True(ResultCardModel.From(claimed, ReceiptLanguage.En).ClaimMismatch);
            Assert.False(ResultCardModel.From(EvidenceWith(DeclaredZero, PreviewNoop()), ReceiptLanguage.En).ClaimMismatch);
            var viaServer = EvidenceWith("{\"intent\":\"x\"}", PreviewNoop());
            viaServer.NoteServerResult(Result(TurnResultKinds.Created, new ResultCountsDto { Created = 2 }));
            Assert.True(ResultCardModel.From(viaServer, ReceiptLanguage.En).ClaimMismatch);
        }

        [Fact]
        public void TheWiring_ViewModelNotesTheResult_AndTheDiscsFollowTheFlag()
        {
            Assert.Contains("rr.Evidence?.NoteServerResult(rr.ServerResult);", Src("UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("|| card.ClaimMismatch;", Src("UI/Copilot/Controls/ResultCardView.cs"));
            Assert.Contains("warn: warn)", Src("UI/Copilot/Controls/ProseAnswer.cs"));
            Assert.Contains("warn: m.ResultCard != null && m.ResultCard.ClaimMismatch", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
        }
    }
}
