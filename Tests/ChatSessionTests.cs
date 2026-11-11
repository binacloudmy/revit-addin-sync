#nullable enable
// AI-305: the Turn model and the ChatSession reducer, tested from recorded
// frames and envelopes, without Revit and without WPF.
//
// Recorded input:
//   contracts/copilot/examples/frames.json   one full turn (read, confirmed write, question, reply)
//                                            and a refused one (error + failed turn_end)
//   contracts/copilot/examples/envelope-*.json  the add-in's envelopes
//   Tests/data/copilot-turns/*.sse           wire transcripts for the cases frames.json lacks
//                                            (repeated progress, tool round, limit, cancel)
//
// Parity: the same recorded turn is also run through the real CopilotTurnClient
// into CopilotTurnRouter (the pane today) and into ChatSession (SessionTurnHandler
// + SessionOutcome). They must agree, so the model can take over from the router's
// private turn state without changing what the pane shows.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ChatSessionTests
    {
        private const string Base = "https://bina-ai-stg-onebistro.binacloud.ai";

        // ------------------------------------------------------------------
        // Recorded fixtures
        // ------------------------------------------------------------------

        private static string ContractsDir([CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "contracts", "copilot");
            if (File.Exists(Path.Combine(copied, "examples", "frames.json"))) return copied;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "copilot");
                if (File.Exists(Path.Combine(candidate, "examples", "frames.json"))) return candidate;
            }
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "contracts", "copilot"));
        }

        private static string DataFile(string name, [CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "data", "copilot-turns", name);
            return File.Exists(copied) ? copied : Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "data", "copilot-turns", name);
        }

        private static Envelope LoadEnvelope(string example) =>
            CopilotJson.Deserialize<Envelope>(File.ReadAllText(Path.Combine(ContractsDir(), "examples", example)))!;

        /// <summary>One recorded turn: the SSE wire of each stream leg, and what the add-in answered.</summary>
        private sealed class Recording
        {
            public string Prompt = "";
            /// <summary>One wire per stream: the start's, then one per results POST.</summary>
            public List<string> Legs = new List<string>();
            public Dictionary<string, Envelope> EnvelopeByCall = new Dictionary<string, Envelope>();
            public AnswerDto? Answer;
        }

        private static Recording Record(string arrayName)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "examples", "frames.json")));
            var rec = new Recording();
            StringBuilder? leg = null;
            foreach (var f in doc.RootElement.GetProperty(arrayName).EnumerateArray())
            {
                if (f.TryGetProperty("request", out _))
                {
                    if (leg != null) rec.Legs.Add(leg.ToString());
                    leg = new StringBuilder();
                    var body = f.GetProperty("body");
                    if (body.TryGetProperty("text", out var text)) rec.Prompt = text.GetString() ?? "";
                    if (body.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                        foreach (var r in results.EnumerateArray())
                            rec.EnvelopeByCall[r.GetProperty("call_id").GetString()!] = CopilotJson.Deserialize<Envelope>(r.GetProperty("envelope"))!;
                    if (body.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.Object)
                        rec.Answer = CopilotJson.Deserialize<AnswerDto>(answer);
                }
                else if (f.TryGetProperty("event", out var ev))
                {
                    leg!.Append("event: ").Append(ev.GetString()).Append("\ndata: ").Append(JsonSerializer.Serialize(f.GetProperty("data"))).Append("\n\n");
                }
            }
            if (leg != null) rec.Legs.Add(leg.ToString());
            return rec;
        }

        // ------------------------------------------------------------------
        // Reducer replay (no HTTP): an SSE wire into a session
        // ------------------------------------------------------------------

        /// <summary>Feeds a recorded wire to the reducer the way the client and router would. A tool
        /// request runs <paramref name="run"/> (the add-in's answer); a write also opens and decides the confirm card.</summary>
        private static async Task ReplayAsync(ChatSession s, string turnId, string wire, Func<ToolRequestDto, Envelope[]> run)
        {
            string? lastError = null;
            await foreach (var e in CopilotSse.Parse(new MemoryStream(Encoding.UTF8.GetBytes(wire))))
            {
                switch (e.Type)
                {
                    case SseEventTypes.Progress: s.OnProgress(turnId, CopilotJson.Deserialize<ProgressDto>(e.Data)); break;
                    case SseEventTypes.ReplyDelta: s.OnReplyDelta(turnId, CopilotJson.Deserialize<ReplyDeltaDto>(e.Data)); break;
                    case SseEventTypes.Error:
                        var err = CopilotJson.Deserialize<StreamErrorDto>(e.Data);
                        lastError = err?.Message;
                        s.OnError(turnId, err);
                        break;
                    case SseEventTypes.ToolRequest:
                        var req = CopilotJson.Deserialize<ToolRequestDto>(e.Data)!;
                        s.OnToolRequest(turnId, req);
                        var envs = run(req);
                        s.OnToolResults(turnId, req, envs);
                        break;
                    case SseEventTypes.Ask: s.OnAsk(turnId, CopilotJson.Deserialize<AskDto>(e.Data)); break;
                    case SseEventTypes.TurnEnd: s.OnEnd(turnId, CopilotJson.Deserialize<TurnEndDto>(e.Data), lastError); break;
                }
            }
        }

        private static Func<ToolRequestDto, Envelope[]> Answers(Recording rec) =>
            req => req.Calls.Select(c => rec.EnvelopeByCall[c.CallId]).ToArray();

        private static Func<ToolRequestDto, Envelope[]> AnswersWith(Envelope env) =>
            req => req.Calls.Select(_ => env).ToArray();

        private static (ChatSession Session, Turn Turn) Start(string id = "t1", string prompt = "Naikkan siling Aras 2 ke 2700 mm", ReceiptLanguage lang = ReceiptLanguage.Ms)
        {
            var s = new ChatSession();
            return (s, s.Begin(id, prompt, lang));
        }

        private static async Task<(ChatSession Session, Turn Turn)> ReplayFileAsync(string file, Func<ToolRequestDto, Envelope[]>? run = null, string prompt = "How many doors?")
        {
            var (s, t) = Start("t1", prompt, ReceiptLanguage.En);
            await ReplayAsync(s, t.Id, File.ReadAllText(DataFile(file)), run ?? AnswersWith(LoadEnvelope("envelope-read-find.json")));
            return (s, t);
        }

        private static List<(string Id, string Phase, string Label, StepState State)> Rows(IEnumerable<ProgressStep> steps) =>
            steps.Select(x => (x.StepId, x.Phase, x.Label, x.State)).ToList();

        // ------------------------------------------------------------------
        // The recorded full turn
        // ------------------------------------------------------------------

        [Fact]
        public async Task RecordedTurn_ReadWriteQuestionAndReply_EndsDoneWithOneEvidence()
        {
            var rec = Record("frames");
            Assert.Equal(4, rec.Legs.Count);
            var (s, t) = Start("0b6f2a52-6a43-4c8e-9a51-3f2f1f6f8c11", rec.Prompt);
            var run = Answers(rec);

            await ReplayAsync(s, t.Id, rec.Legs[0], run);
            await ReplayAsync(s, t.Id, rec.Legs[1], run);
            Assert.Equal(TurnPhase.Running, t.State);
            await ReplayAsync(s, t.Id, rec.Legs[2], run);

            // Parked on the project-note question, which belongs to this turn.
            Assert.Equal(TurnPhase.AwaitingAnswer, t.State);
            Assert.NotNull(t.Ask);
            Assert.True(t.Ask!.IsProjectNote);
            Assert.Equal(t.Id, t.Ask.TurnId);
            Assert.Equal("req_03", t.Ask.RequestId);
            Assert.Single(t.Questions);
            Assert.False(t.IsFinished);

            Assert.True(s.AnswerAsk(t.Id, "Ya, simpan"));
            Assert.Equal(TurnPhase.Running, t.State);
            Assert.Null(t.Ask);
            Assert.Equal("Ya, simpan", t.Questions[0].Card.AnsweredSummary);
            await ReplayAsync(s, t.Id, rec.Legs[3], run);

            Assert.Equal(TurnPhase.Done, t.State);
            Assert.True(t.IsFinished);
            Assert.Equal("Siap: 34 siling di Aras 2 kini pada 2700 mm (disemak 34/34). Nota projek disimpan.", t.LegReply);
            Assert.Equal(t.LegReply, t.Reply);                      // the ask carried no text, so the whole reply is the last leg's
            Assert.Equal("ses_4c1d9e7a", t.SessionId);
            Assert.Equal(new[] { "find_elements", "set_parameters" }, t.ToolsUsed);
            Assert.Equal(2, t.Evidence.Calls.Count);
            Assert.True(t.Evidence.AnyCommitted);
            Assert.False(t.Evidence.Caution);
            Assert.True(t.CanUndo);
            Assert.Empty(t.Notes);
            Assert.False(t.Failed);
            Assert.NotNull(t.Result);
            Assert.Equal(ResultKind.Changes, t.Result!.Kind);
            // Every step ended; the budget row is not shown.
            Assert.All(t.StepsSnapshot(), x => Assert.NotEqual(StepState.Running, x.State));
            Assert.Equal(1, t.Steps.Count(x => x.StepId == "ask"));
        }

        [Fact]
        public async Task RecordedTurn_Projection_UserClarifyReply_EvidenceOnExactlyOneMessage()
        {
            var rec = Record("frames");
            var (s, t) = Start("0b6f2a52-6a43-4c8e-9a51-3f2f1f6f8c11", rec.Prompt);
            var run = Answers(rec);
            await ReplayAsync(s, t.Id, rec.Legs[0], run);
            await ReplayAsync(s, t.Id, rec.Legs[1], run);
            await ReplayAsync(s, t.Id, rec.Legs[2], run);

            // While the question is open the question is the last message: it carries the evidence.
            var parked = TurnProjection.Messages(t);
            Assert.Equal(new[] { CpMsgKind.User, CpMsgKind.Clarify }, parked.Select(m => m.Kind).ToArray());
            Assert.Same(t.Evidence, parked[1].Evidence);
            Assert.Null(parked[0].Evidence);

            s.AnswerAsk(t.Id, "Ya, simpan");
            var running = TurnProjection.Messages(t);
            Assert.Equal(CpMsgKind.Thinking, running.Last().Kind);          // live tail, no evidence drawn yet
            Assert.All(running, m => Assert.Null(m.Evidence));

            await ReplayAsync(s, t.Id, rec.Legs[3], run);
            var done = TurnProjection.Messages(t);

            Assert.Equal(new[] { CpMsgKind.User, CpMsgKind.Clarify, CpMsgKind.AiReply }, done.Select(m => m.Kind).ToArray());
            var holders = Enumerable.Range(0, done.Count).Where(i => EvidenceDedupe.IsLastHolder(done.Select(m => (object?)m.Evidence).ToList(), i)).ToList();
            Assert.Equal(new[] { 2 }, holders);                              // one owner: the final reply
            Assert.Single(done, m => m.Evidence != null);
            var reply = done[2];
            Assert.Equal(t.LegReply, reply.Text);
            Assert.Equal(t.Id, reply.TurnId);
            Assert.Equal(new[] { "find_elements", "set_parameters" }, reply.ToolsUsed);
            Assert.Same(t.Result, reply.ResultCard);
            Assert.Equal(ReceiptLanguage.Ms, reply.Lang);
            Assert.Equal(rec.Prompt, done[0].Text);
            Assert.Null(t.Ask);                                               // answered question is a record, not open
            Assert.Equal("Ya, simpan", done[1].Ask!.AnsweredSummary);
        }

        [Fact]
        public async Task RecordedRefusedTurn_ErrorThenFailedEnd_NoReply_IsAFailureWithTheCloudsMessage()
        {
            var rec = Record("failure_frames");
            Assert.Single(rec.Legs);
            var (s, t) = Start("t_fail", rec.Prompt, ReceiptLanguage.En);

            await ReplayAsync(s, t.Id, rec.Legs[0], Answers(rec));

            Assert.Equal(TurnPhase.Failed, t.State);
            Assert.True(t.Failed);
            Assert.Equal(RouteFailure.Other, t.FailureKind);
            Assert.Equal("This month's copilot credits are used up.", t.PaneNote);
            Assert.Equal("", t.LegReply);
            Assert.Contains(t.StepsSnapshot(), x => x.StepId == "error:1" && x.State == StepState.Error);
            var msgs = TurnProjection.Messages(t);
            Assert.Equal(new[] { CpMsgKind.User, CpMsgKind.AiReply }, msgs.Select(m => m.Kind).ToArray());
            Assert.Equal("This month's copilot credits are used up.", msgs[1].PaneNote);
        }

        // ------------------------------------------------------------------
        // Recorded wires for the other cases
        // ------------------------------------------------------------------

        [Fact]
        public async Task ToolRound_RepeatedProgressKeepsOneRow_ReplyGetsOneBlankLineAtTheRound()
        {
            var (_, t) = await ReplayFileAsync("turn-tool-round.sse");

            Assert.Equal(TurnPhase.Done, t.State);
            Assert.Equal("Saya semak pintu.\n\nAda 34 pintu.", t.Reply);
            // seq 1 and 2 are the same thinking text: one row, not two.
            Assert.Equal(1, t.Steps.Count(x => x.Phase == ProgressKinds.Thinking && x.StepId.StartsWith("p:", StringComparison.Ordinal)));
            Assert.DoesNotContain(t.Steps, x => x.StepId == "p:2");
            var rows = Rows(t.StepsSnapshot());
            Assert.Equal(new[] { "thinking", "p:1", "call:call_a", "p:5" }, rows.Select(r => r.Id).ToArray());
            Assert.Equal("Finding elements", rows.First(r => r.Id == "call:call_a").Label);
            Assert.All(rows, r => Assert.Equal(StepState.Done, r.State));    // all closed at the end
            Assert.Equal("ses_round", t.SessionId);
            Assert.Equal(34, Assert.Single(t.Evidence.Evidence).Count);
        }

        [Fact]
        public async Task Limit_KeepsTheModelsText_AddsThePanesLimitNote_NotAFailureCard()
        {
            var (_, t) = await ReplayFileAsync("turn-limit.sse");

            Assert.Equal(TurnPhase.Limit, t.State);
            Assert.Equal("Separuh jawapan.", t.LegReply);
            Assert.Equal("This turn stopped at its limit.", t.PaneNote);
            Assert.False(t.Failed);
            var reply = TurnProjection.Messages(t).Last();
            Assert.Equal("Separuh jawapan.", reply.Text);
            Assert.Equal("This turn stopped at its limit.", reply.PaneNote);
        }

        [Fact]
        public async Task Cancelled_KeepsTheReceipt_NotesCancelled_UndoStillOffered()
        {
            var commit = LoadEnvelope("envelope-commit-setparams.json");
            var (_, t) = await ReplayFileAsync("turn-cancelled-after-commit.sse", AnswersWith(commit));

            Assert.Equal(TurnPhase.Interrupted, t.State);
            Assert.Equal(new[] { "Cancelled." }, t.Notes);
            Assert.True(t.Evidence.AnyCommitted);                            // what already ran is in the receipt
            Assert.True(t.CanUndo);
            var reply = TurnProjection.Messages(t).Last();
            Assert.True(reply.Interrupted);
            Assert.Equal("Cancelled.", reply.PaneNote);
            Assert.Same(t.Evidence, reply.Evidence);
            Assert.NotNull(reply.ResultCard);
        }

        [Fact]
        public async Task Failed_WithPartialReply_KeepsTheReplyAndShowsTheCloudsReasonAsTheNote()
        {
            var (s, t) = Start("t1", "How many doors?", ReceiptLanguage.En);
            s.OnReplyDelta(t.Id, new ReplyDeltaDto { Seq = 1, Text = "Half an answer." });
            s.OnError(t.Id, new StreamErrorDto { Seq = 2, Code = "upstream", Message = "The model stopped." });
            s.OnEnd(t.Id, new TurnEndDto { Seq = 3, Status = TurnEndStatus.Failed, SessionId = "s" }, "The model stopped.");

            Assert.Equal(TurnPhase.Failed, t.State);
            Assert.False(t.Failed);                                          // there is a reply: no failure card
            Assert.Equal("The model stopped.", t.PaneNote);
            Assert.Equal("Half an answer.", t.LegReply);
        }

        [Fact]
        public void TransportFailure_IsFailedWithItsKind_StepsClosed()
        {
            var (s, t) = Start("t1", "How many doors?", ReceiptLanguage.En);

            s.OnFailure(t.Id, RouteFailure.Unreachable, "Cannot reach BINA.");

            Assert.Equal(TurnPhase.Failed, t.State);
            Assert.True(t.Failed);
            Assert.Equal(RouteFailure.Unreachable, t.FailureKind);
            Assert.Equal("Cannot reach BINA.", t.PaneNote);
            Assert.All(t.Steps, x => Assert.NotEqual(StepState.Running, x.State));
        }

        [Fact]
        public void Stop_InterruptsTheTurn_AndLaterFramesAreDropped()
        {
            var (s, t) = Start("t1", "tukar mark pintu", ReceiptLanguage.Ms);
            s.OnReplyDelta(t.Id, new ReplyDeltaDto { Seq = 1, Text = "Baik: " });

            s.OnStopped(t.Id);
            s.OnReplyDelta(t.Id, new ReplyDeltaDto { Seq = 2, Text = "late text" });
            s.OnProgress(t.Id, new ProgressDto { Seq = 3, Kind = "running", Text = "late" });
            s.OnEnd(t.Id, new TurnEndDto { Seq = 4, Status = TurnEndStatus.Done, SessionId = "late" });

            Assert.Equal(TurnPhase.Interrupted, t.State);
            Assert.Equal("Baik: ", t.Reply);                                  // nothing late was added
            Assert.Equal("Dibatalkan.", t.PaneNote);
            Assert.Null(t.SessionId);                                         // a late turn_end does not name a session
            Assert.DoesNotContain(t.Steps, x => x.StepId == "p:3");
        }

        // ------------------------------------------------------------------
        // Question and undo intents
        // ------------------------------------------------------------------

        [Fact]
        public void Ask_TextBeforeItStaysAboveTheCard_TheAnswersLegShowsOnlyItsOwnText()
        {
            var (s, t) = Start("t1", "naikkan siling", ReceiptLanguage.Ms);
            s.OnReplyDelta(t.Id, new ReplyDeltaDto { Seq = 1, Text = "Saya perlu tahu satu perkara." });
            var card = s.OnAsk(t.Id, new AskDto { Seq = 2, RequestId = "req_q", Question = "Aras mana?", Purpose = AskPurposes.Clarify });

            Assert.NotNull(card);
            Assert.Equal(TurnPhase.AwaitingAnswer, t.State);
            Assert.Equal("Menunggu jawapan anda", t.Steps.First(x => x.StepId == "ask").Label);
            var parked = TurnProjection.Messages(t);
            Assert.Equal("Saya perlu tahu satu perkara.", parked[1].Text);
            Assert.Equal("Aras mana?", parked[1].Question);

            Assert.True(s.AnswerAsk(t.Id, "Aras 2"));
            Assert.False(s.AnswerAsk(t.Id, "again"));                          // already answered
            s.OnReplyDelta(t.Id, new ReplyDeltaDto { Seq = 3, Text = "Baik, Aras 2." });
            s.OnEnd(t.Id, new TurnEndDto { Seq = 4, Status = TurnEndStatus.Done, SessionId = "s" });

            Assert.Equal("Baik, Aras 2.", t.LegReply);                          // this leg only
            Assert.Equal("Saya perlu tahu satu perkara.Baik, Aras 2.", t.Reply);
            var done = TurnProjection.Messages(t);
            Assert.Equal(new[] { CpMsgKind.User, CpMsgKind.Clarify, CpMsgKind.AiReply }, done.Select(m => m.Kind).ToArray());
            Assert.Equal("Baik, Aras 2.", done[2].Text);
            Assert.Null(done[1].Evidence);                                     // evidence rides on the reply only
            Assert.Same(t.Evidence, done[2].Evidence);
        }

        [Fact]
        public void AnswerAsk_NeedsAParkedQuestion()
        {
            var (s, t) = Start("t1", "x", ReceiptLanguage.En);

            Assert.False(s.AnswerAsk(t.Id, "x"));                              // nothing parked
            Assert.False(s.AnswerAsk("nope", "x"));
            Assert.Equal(TurnPhase.Running, t.State);
        }

        [Fact]
        public async Task Undo_IsOfferedAfterACommit_OnceOnly_AndNotedOnTheTurn()
        {
            var commit = LoadEnvelope("envelope-commit-setparams.json");
            var (s, t) = Start("t1", "Naikkan siling Aras 2 ke 2700 mm", ReceiptLanguage.Ms);
            Assert.False(s.CanUndo(t.Id));                                     // not ended
            var wire = "event: tool_request\ndata: {\"seq\":1,\"request_id\":\"r\",\"kind\":\"write\",\"calls\":[{\"call_id\":\"c\",\"idempotency_key\":\"k\",\"tool\":\"set_parameters\",\"args\":{}}]}\n\n"
                     + "event: turn_end\ndata: {\"seq\":2,\"status\":\"done\",\"session_id\":\"s\"}\n\n";
            await ReplayAsync(s, t.Id, wire, AnswersWith(commit));

            Assert.True(s.CanUndo(t.Id));
            Assert.True(s.UndoDone(t.Id, "Undone: 34 ceilings restored."));
            Assert.False(s.CanUndo(t.Id));
            Assert.False(s.UndoDone(t.Id, "again"));
            Assert.True(t.Undone);
            Assert.Equal("Undone: 34 ceilings restored.", t.PaneNote);
        }

        [Fact]
        public void Undo_NotOfferedForAReadOnlyTurn()
        {
            var (s, t) = Start("t1", "x", ReceiptLanguage.En);
            s.OnEnd(t.Id, new TurnEndDto { Seq = 1, Status = TurnEndStatus.Done, SessionId = "s" });

            Assert.False(s.CanUndo(t.Id));
            Assert.False(s.UndoDone(t.Id, "x"));
        }

        // ------------------------------------------------------------------
        // Edit a cell of the result card
        // ------------------------------------------------------------------

        private const string WriteTurnWire =
            "event: tool_request\ndata: {\"seq\":1,\"request_id\":\"r\",\"kind\":\"write\",\"calls\":[{\"call_id\":\"c\",\"idempotency_key\":\"k\",\"tool\":\"set_parameters\",\"args\":{}}]}\n\n"
            + "event: turn_end\ndata: {\"seq\":2,\"status\":\"done\",\"session_id\":\"s\"}\n\n";

        /// <summary>A finished write turn whose Changes card comes from the recorded commit envelope.</summary>
        private static async Task<(ChatSession Session, Turn Turn)> WriteTurnAsync()
        {
            var (s, t) = Start("t1", "Naikkan siling Aras 2 ke 2700 mm", ReceiptLanguage.Ms);
            await ReplayAsync(s, t.Id, WriteTurnWire, AnswersWith(LoadEnvelope("envelope-commit-setparams.json")));
            return (s, t);
        }

        [Fact]
        public async Task EditCell_KeepsTheTypedTextOnTheTurn_WithWhatThePaneNeedsToWriteIt()
        {
            var (s, t) = await WriteTurnAsync();
            Assert.Equal(ResultKind.Changes, t.Result!.Kind);
            var row = t.Result.Groups[0].Rows[0];
            Assert.Equal("2700 mm", row.After);
            var raised = new List<string?>();
            t.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            Assert.True(s.EditCell(t.Id, 0, 0, "2650"));
            Assert.True(s.EditCell(t.Id, 0, 0, "2650 mm"));                     // typing on: one edit, latest text

            var edit = Assert.Single(t.CellEdits);
            Assert.Same(edit, t.FindCellEdit(0, 0));
            Assert.Equal("2650 mm", edit.Text);
            Assert.Equal(CellEditState.Draft, edit.State);
            Assert.Equal("2700 mm", edit.Was);
            Assert.Equal("Height Offset From Level", edit.Param);
            Assert.False(edit.IsType);
            Assert.Equal(34, edit.Ids.Count);
            Assert.Equal(t.Result.Groups[0].Ids, edit.Ids);
            Assert.Equal("2700 mm", row.After);                                 // a draft does not touch the card
            Assert.Contains(nameof(Turn.CellEdits), raised);
        }

        [Fact]
        public async Task EditCell_BackToTheShownValue_DropsTheDraft_Discard_DropsItToo()
        {
            var (s, t) = await WriteTurnAsync();

            Assert.True(s.EditCell(t.Id, 0, 0, "2650 mm"));
            Assert.True(s.EditCell(t.Id, 0, 0, "2700 mm"));                     // changed back
            Assert.Empty(t.CellEdits);

            Assert.True(s.EditCell(t.Id, 0, 0, "2600 mm"));
            Assert.True(s.DiscardCellEdit(t.Id, 0, 0));
            Assert.Empty(t.CellEdits);
            Assert.False(s.DiscardCellEdit(t.Id, 0, 0));                         // nothing left
        }

        [Fact]
        public async Task EditCell_Applied_UpdatesTheRowAndItsSource_AndRaisesResult()
        {
            var (s, t) = await WriteTurnAsync();
            var row = t.Result!.Groups[0].Rows[0];
            Assert.False(s.CellEditApplied(t.Id, 0, 0));                         // no edit yet
            s.EditCell(t.Id, 0, 0, "2650 mm");
            Assert.False(s.CellEditApplied(t.Id, 0, 0));                         // a draft is not written yet

            Assert.True(s.CellEditApplying(t.Id, 0, 0));
            Assert.Equal(CellEditState.Applying, t.FindCellEdit(0, 0)!.State);
            Assert.False(s.EditCell(t.Id, 0, 0, "2600 mm"));                    // not while it is being written
            Assert.False(s.DiscardCellEdit(t.Id, 0, 0));
            var raised = new List<string?>();
            t.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            Assert.True(s.CellEditApplied(t.Id, 0, 0));

            Assert.Equal(CellEditState.Applied, t.FindCellEdit(0, 0)!.State);
            Assert.Equal("2650 mm", row.After);
            Assert.Equal("2400 mm", row.Before);                                 // Before stays the model's original
            Assert.Equal(ChatSession.EditedSource, row.Source);
            Assert.Contains(nameof(Turn.Result), raised);
            Assert.Contains(nameof(Turn.CellEdits), raised);
            Assert.Same(t.Result, TurnProjection.Messages(t).Last().ResultCard);  // the projection shows the edited card
            Assert.Single(t.Evidence.Calls);                                      // not added to the turn's evidence

            // A new edit of an applied cell starts from its new value.
            Assert.True(s.EditCell(t.Id, 0, 0, "2700 mm"));
            var again = Assert.Single(t.CellEdits);
            Assert.Equal(CellEditState.Draft, again.State);
            Assert.Equal("2650 mm", again.Was);
        }

        [Fact]
        public async Task EditCell_Failed_KeepsTheTextForARetry()
        {
            var (s, t) = await WriteTurnAsync();
            s.EditCell(t.Id, 0, 0, "abc");
            s.CellEditApplying(t.Id, 0, 0);

            Assert.True(s.CellEditFailed(t.Id, 0, 0, "Height Offset From Level needs a length."));

            var edit = t.FindCellEdit(0, 0)!;
            Assert.Equal(CellEditState.Failed, edit.State);
            Assert.Equal("abc", edit.Text);
            Assert.Equal("Height Offset From Level needs a length.", edit.Error);
            Assert.Equal("2700 mm", t.Result!.Groups[0].Rows[0].After);
            Assert.False(s.CellEditFailed(t.Id, 0, 0, "again"));                // only from Applying

            Assert.True(s.CellEditApplying(t.Id, 0, 0));                         // retry
            Assert.Null(t.FindCellEdit(0, 0)!.Error);
            Assert.True(s.CellEditFailed(t.Id, 0, 0, null));
            Assert.True(s.EditCell(t.Id, 0, 0, "2650 mm"));                      // or fix the text: a draft again
            Assert.Equal(CellEditState.Draft, t.FindCellEdit(0, 0)!.State);
        }

        [Fact]
        public async Task EditCell_RefusedOutsideAnEditableChangesRow()
        {
            var (s, t) = await WriteTurnAsync();
            Assert.False(s.EditCell("nope", 0, 0, "x"));                         // unknown turn
            Assert.False(s.EditCell(t.Id, 1, 0, "x"));                           // out of range
            Assert.False(s.EditCell(t.Id, 0, 1, "x"));
            Assert.False(s.EditCell(t.Id, -1, 0, "x"));

            var (s2, running) = Start("t2", "x", ReceiptLanguage.En);
            Assert.False(s2.EditCell(running.Id, 0, 0, "x"));                    // still running: no card yet

            var (s3, read) = Start("t3", "x", ReceiptLanguage.En);
            s3.OnEnd(read.Id, new TurnEndDto { Seq = 1, Status = TurnEndStatus.Done, SessionId = "s" });
            Assert.False(s3.EditCell(read.Id, 0, 0, "x"));                       // no Changes card
            Assert.Empty(t.CellEdits);
        }

        [Fact]
        public async Task Undo_DropsUnsentEdits_AndNoCellIsEditableAfterIt()
        {
            var (s, t) = await WriteTurnAsync();
            s.EditCell(t.Id, 0, 0, "2650 mm");

            Assert.True(s.UndoDone(t.Id, "Undone."));

            Assert.Empty(t.CellEdits);
            Assert.False(s.EditCell(t.Id, 0, 0, "2650 mm"));
        }

        // ------------------------------------------------------------------
        // Session
        // ------------------------------------------------------------------

        [Fact]
        public void Begin_IsIdempotentPerId_Reset_DropsEveryTurn_FramesForUnknownTurnsAreIgnored()
        {
            var s = new ChatSession();
            var a = s.Begin("a", "one", ReceiptLanguage.En);

            Assert.Same(a, s.Begin("a", "again", ReceiptLanguage.En));
            Assert.Equal("one", a.Prompt);
            s.OnReplyDelta("ghost", new ReplyDeltaDto { Seq = 1, Text = "x" });
            s.OnProgress("ghost", new ProgressDto { Seq = 1, Kind = "running", Text = "x" });
            Assert.Single(s.Turns);

            s.Reset();

            Assert.Empty(s.Turns);
            Assert.Null(s.Find("a"));
        }

        [Fact]
        public void TwoTurns_KeepTheirOwnEvidenceStepsAndReply()
        {
            var s = new ChatSession();
            var a = s.Begin("a", "first", ReceiptLanguage.En);
            var b = s.Begin("b", "second", ReceiptLanguage.En);

            s.OnReplyDelta("a", new ReplyDeltaDto { Seq = 1, Text = "A" });
            s.OnReplyDelta("b", new ReplyDeltaDto { Seq = 1, Text = "B" });
            s.OnEnd("a", new TurnEndDto { Seq = 2, Status = TurnEndStatus.Done, SessionId = "sa" });

            Assert.Equal("A", a.Reply);
            Assert.Equal("B", b.Reply);
            Assert.NotSame(a.Evidence, b.Evidence);
            Assert.True(a.IsFinished);
            Assert.False(b.IsFinished);
            var all = TurnProjection.Messages(s);
            Assert.Equal(new[] { CpMsgKind.User, CpMsgKind.AiReply, CpMsgKind.User, CpMsgKind.Thinking }, all.Select(m => m.Kind).ToArray());
        }

        [Fact]
        public void Begin_CarriesTheUserBubblesExtras_IntoTheProjection()
        {
            var s = new ChatSession();
            var t = s.Begin("a", "count these", ReceiptLanguage.En, new TurnStart
            {
                SourcePrompt = "count these\n[Attached: x.csv]",
                AttachmentName = "x.csv",
                Images = new[] { "aGk=" },
                Files = new[] { new FileAttachment("x.csv", "a,b") },
                SessionEpoch = 7,
            });

            var user = TurnProjection.Messages(t)[0];

            Assert.Equal("count these", user.Text);
            Assert.Equal(new[] { "aGk=" }, user.ImagesBase64);
            Assert.Equal("x.csv", Assert.Single(user.Files!).Name);
            Assert.Equal(7, t.SessionEpoch);
            Assert.Equal("count these\n[Attached: x.csv]", t.SourcePrompt);
            s.OnEnd("a", new TurnEndDto { Seq = 1, Status = TurnEndStatus.Done, SessionId = "s" });
            Assert.Equal("count these\n[Attached: x.csv]", TurnProjection.Messages(t).Last().SourcePrompt);
        }

        [Fact]
        public void RevitWaiting_And_PreviewAgain_AreStepsOfTheRunningTurn()
        {
            var (s, t) = Start("t1", "x", ReceiptLanguage.En);

            Assert.True(s.OnRevitWaiting(t.Id, "Duplicate marks"));
            Assert.False(s.OnPreviewStarting(t.Id, "b1"));                    // first preview already has its row
            Assert.True(s.OnPreviewStarting(t.Id, "b1"));                     // the second says "again"

            Assert.Contains(t.Steps, x => x.Phase == ProgressKinds.RevitBusy && x.Label == "Revit is waiting on a dialog: Duplicate marks" && x.State == StepState.Error);
            Assert.Contains(t.Steps, x => x.StepId == "preview:b1" && x.Label == "Previewing the change in Revit again…");
        }

        // ------------------------------------------------------------------
        // Change notification
        // ------------------------------------------------------------------

        [Fact]
        public void TurnRaisesPropertyChanged_AndStepsRaiseCollectionChanged_ForTheViewToRedrawOnItsOwn()
        {
            var (s, t) = Start("t1", "x", ReceiptLanguage.En);
            var props = new List<string>();
            ((INotifyPropertyChanged)t).PropertyChanged += (_, e) => props.Add(e.PropertyName!);
            var stepChanges = 0;
            t.Steps.CollectionChanged += (_, __) => stepChanges++;

            s.OnReplyDelta(t.Id, new ReplyDeltaDto { Seq = 1, Text = "Hi" });
            s.OnProgress(t.Id, new ProgressDto { Seq = 2, Kind = "running", Text = "Working" });
            s.OnEnd(t.Id, new TurnEndDto { Seq = 3, Status = TurnEndStatus.Done, SessionId = "s" });

            Assert.Contains(nameof(Turn.Reply), props);
            Assert.Contains(nameof(Turn.State), props);
            Assert.Contains(nameof(Turn.SessionId), props);
            Assert.True(stepChanges >= 1);
        }

        // ------------------------------------------------------------------
        // Parity with the pane's router, over the real client
        // ------------------------------------------------------------------

        /// <summary>Answers every POST with the next recorded stream leg.</summary>
        private sealed class ScriptedCloud : HttpMessageHandler
        {
            private readonly Queue<string> _legs;
            public ScriptedCloud(IEnumerable<string> legs) { _legs = new Queue<string>(legs); }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path.EndsWith("/cancel", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                var wire = _legs.Count > 0 ? _legs.Dequeue() : "";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(wire))),
                });
            }
        }

        private sealed class RecordedTools : ITurnTools
        {
            private readonly Recording _rec;
            public RecordedTools(Recording rec) { _rec = rec; }
            public ReceiptLanguage Language { get; set; }
            public SnapshotModel Snapshot() => new SnapshotModel();
            private Envelope[] For(IReadOnlyList<ToolCallDto> calls) => calls.Select(c => _rec.EnvelopeByCall[c.CallId]).ToArray();
            public Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel turnSnapshot, CancellationToken ct) => Task.FromResult(For(calls));
            public Task<Envelope[]> RunWritesAsync(string turnId, string requestId, IReadOnlyList<ToolCallDto> calls, string? description, SnapshotModel turnSnapshot, CancellationToken ct) => Task.FromResult(For(calls));
        }

        private static CopilotTurnClient ClientOn(HttpMessageHandler cloud) =>
            new CopilotTurnClient(new HttpClient(cloud), new CopilotClientOptions
            {
                BaseUrl = Base,
                Token = () => "t",
                ReleaseBuild = true,
                Delay = (_, __) => Task.CompletedTask,
            });

        [Fact]
        public async Task Parity_RecordedFullTurn_RouterAndSessionAgree_OnEveryLeg()
        {
            var rec = Record("frames");

            // The pane today: CopilotTurnRouter over the real client.
            var router = new CopilotTurnRouter(new RecordedTools(rec), ClientOn(new ScriptedCloud(rec.Legs)));
            var asked = await router.RouteAsync(rec.Prompt, "ai-generated");
            var card = asked.Ask!;
            var routed = await router.SubmitAskAsync(card, card.AnswerForOptions(new[] { "yes" }));

            // The model: the same client, events and outcomes into ChatSession.
            var session = new ChatSession();
            var client = ClientOn(new ScriptedCloud(rec.Legs));
            var turn = session.Begin("t-parity", rec.Prompt, PromptLanguage.Detect(rec.Prompt));
            ToolRunner run = (req, ct) => Task.FromResult(req.Calls.Select(c => rec.EnvelopeByCall[c.CallId]).ToArray());
            var handler = new SessionTurnHandler(session, turn.Id, run, budget: () => client.ActiveBudget?.Describe());
            var first = await client.StartTurnAsync(new TurnRequestDto { TurnId = turn.Id, Text = rec.Prompt }, handler, CancellationToken.None);
            SessionOutcome.Apply(session, turn.Id, first);

            // First leg: both parked on the same question, with the same text above it.
            Assert.Equal(TurnPhase.AwaitingAnswer, turn.State);
            Assert.Equal(asked.Reply, turn.Questions[0].LeadText);
            Assert.Equal(asked.Ask!.Question, turn.Ask!.Question);
            Assert.Equal(asked.Ask.IsProjectNote, turn.Ask.IsProjectNote);
            Assert.Equal(asked.Ask.Options.Select(o => o.Id), turn.Ask.Options.Select(o => o.Id));

            Assert.True(session.AnswerAsk(turn.Id, "Ya, simpan"));
            var second = await client.AnswerAsync(turn.Id, turn.Questions[0].Card.RequestId, rec.Answer!, handler, CancellationToken.None);
            SessionOutcome.Apply(session, turn.Id, second);

            // Final leg: same reply, steps, tools, evidence, notes, session.
            Assert.Equal(TurnPhase.Done, turn.State);
            Assert.False(routed.Failed);
            Assert.Equal(routed.Reply, turn.LegReply);
            Assert.Equal(Rows(routed.Steps!), Rows(turn.StepsSnapshot()));
            Assert.Equal(routed.ToolsUsed, turn.ToolsUsed.ToList());
            Assert.Equal(routed.PaneNote, turn.PaneNote);
            Assert.Equal(routed.Interrupted, turn.State == TurnPhase.Interrupted);
            Assert.Equal(router.SessionId, turn.SessionId);
            Assert.Equal(routed.Evidence!.Calls.Count, turn.Evidence.Calls.Count);
            Assert.Equal(routed.Evidence.Receipt.Done, turn.Evidence.Receipt.Done);
            Assert.Equal(routed.Evidence.Receipt.Total, turn.Evidence.Receipt.Total);
            Assert.Equal(routed.Evidence.Caution, turn.Evidence.Caution);
            Assert.Equal(routed.Evidence.AnyCommitted, turn.Evidence.AnyCommitted);
            Assert.Equal(routed.Evidence.Evidence.Select(e => (e.Tool, e.Count)), turn.Evidence.Evidence.Select(e => (e.Tool, e.Count)));
            Assert.Equal(
                ResultCardModel.From(routed.Evidence, routed.Language).Headline,
                turn.Result!.Headline);
        }

        [Fact]
        public async Task Parity_RecordedRefusedTurn_RouterAndSessionAgree()
        {
            var rec = Record("failure_frames");

            var router = new CopilotTurnRouter(new RecordedTools(rec), ClientOn(new ScriptedCloud(rec.Legs)));
            var routed = await router.RouteAsync(rec.Prompt, "ai-generated");

            var session = new ChatSession();
            var client = ClientOn(new ScriptedCloud(rec.Legs));
            var turn = session.Begin("t-parity", rec.Prompt, PromptLanguage.Detect(rec.Prompt));
            var handler = new SessionTurnHandler(session, turn.Id, (req, ct) => Task.FromResult(new Envelope[0]));
            var outcome = await client.StartTurnAsync(new TurnRequestDto { TurnId = turn.Id, Text = rec.Prompt }, handler, CancellationToken.None);
            SessionOutcome.Apply(session, turn.Id, outcome);

            Assert.True(routed.Failed);
            Assert.Equal(routed.Failed, turn.Failed);
            Assert.Equal(routed.FailureKind, turn.FailureKind);
            Assert.Equal(routed.PaneNote, turn.PaneNote);
            Assert.Equal(routed.Reply, turn.LegReply);
            Assert.Equal(Rows(routed.Steps!), Rows(turn.StepsSnapshot()));
            Assert.Equal(router.SessionId, turn.SessionId);
        }

        [Fact]
        public async Task Parity_UpdateRequiredAndUnauthorizedOutcomes_MapToTheRoutersFailureKinds()
        {
            var cases = new[]
            {
                (HttpStatusCode.Unauthorized, RouteFailure.SignIn),
                ((HttpStatusCode)426, RouteFailure.UpdateRequired),
                (HttpStatusCode.Conflict, RouteFailure.TurnActive),
            };
            foreach (var (code, kind) in cases)
            {
                var cloud = new StatusCloud(code);
                var router = new CopilotTurnRouter(new RecordedTools(new Recording()), ClientOn(cloud));
                var routed = await router.RouteAsync("hello there", "ai-generated");

                var session = new ChatSession();
                var turn = session.Begin("t", "hello there", ReceiptLanguage.En);
                var handler = new SessionTurnHandler(session, turn.Id, (r, ct) => Task.FromResult(new Envelope[0]));
                var outcome = await ClientOn(new StatusCloud(code)).StartTurnAsync(new TurnRequestDto { TurnId = turn.Id, Text = "hello there" }, handler, CancellationToken.None);
                SessionOutcome.Apply(session, turn.Id, outcome);

                Assert.Equal(kind, routed.FailureKind);
                Assert.Equal(routed.FailureKind, turn.FailureKind);
                Assert.Equal(routed.PaneNote, turn.PaneNote);
                Assert.Equal(TurnPhase.Failed, turn.State);
            }
        }

        private sealed class StatusCloud : HttpMessageHandler
        {
            private readonly HttpStatusCode _code;
            public StatusCloud(HttpStatusCode code) { _code = code; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent("{\"update_required\":true}") });
        }
    }
}
