#nullable enable
// The copilot pane's turn reducer (AI-305, redesign plan item #21).
//
// Router events (the protocol-3 frames, the add-in's tool runs) and drafter
// intents (answer a question, decide a confirm card, undo, edit a cell of the
// result card) go in; Turn state
// with change notification comes out. WPF-free and Revit-free, so whole turns
// are unit-tested from recorded SSE events and envelopes (Tests/ChatSessionTests).
//
// Threading: the reducer does not marshal. Router events arrive on a
// thread-pool thread; the pane calls the reducer from the UI thread or
// serialises the calls itself. Turn locks its own fields, and raises
// PropertyChanged outside its lock. Turns.CollectionChanged is raised on the
// calling thread: bind to it from the UI thread.
//
// Rules the reducer keeps (copied from CopilotTurnRouter's private Turn so the
// two agree until the router adopts this class):
//   - a progress frame that repeats the previous step's kind and text keeps its row
//   - a tool request closes the open progress rows and marks a step boundary:
//     the next reply text gets a blank line before it, never doubled
//   - an ask closes the leg: the text before it stays above the card, later text
//     is a new leg
//   - turn end completes every running step; late frames for an ended turn are dropped
//
// What it does not do: wire enums (AI-298), wiring into CopilotViewModel (the
// pane keeps building ChatMessage itself until AI-306 / AI-310; TurnProjection
// is the one-way bridge).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>What a new turn starts with, besides its id, prompt and language.</summary>
    public sealed class TurnStart
    {
        public long SessionEpoch;
        public SnapshotModel? Snapshot;
        /// <summary>What goes to the cloud when it differs from the typed text.</summary>
        public string? SourcePrompt;
        public string? AttachmentName;
        public IReadOnlyList<string>? Images;
        public IReadOnlyList<FileAttachment>? Files;
        public SlashTool? Slash;
    }

    public sealed class ChatSession
    {
        private readonly object _sync = new object();
        private readonly ObservableCollection<Turn> _turns = new ObservableCollection<Turn>();

        public ObservableCollection<Turn> Turns => _turns;

        public Turn? Find(string? turnId)
        {
            if (string.IsNullOrEmpty(turnId)) return null;
            lock (_sync) return _turns.FirstOrDefault(t => t.Id == turnId);
        }

        /// <summary>A chat change ("+" or continuing another chat): every turn is dropped.</summary>
        public void Reset()
        {
            lock (_sync) _turns.Clear();
        }

        // ------------------------------------------------------------------
        // Intents: the drafter
        // ------------------------------------------------------------------

        /// <summary>The drafter sent a message: a new turn, thinking. Returns the existing
        /// turn when the id is already known (a send is never started twice).</summary>
        public Turn Begin(string turnId, string prompt, ReceiptLanguage language, TurnStart? start = null)
        {
            lock (_sync)
            {
                var existing = _turns.FirstOrDefault(t => t.Id == turnId);
                if (existing != null) return existing;
                var turn = new Turn(turnId, prompt, language);
                if (start != null)
                {
                    turn.SessionEpoch = start.SessionEpoch;
                    if (start.Snapshot != null) turn.Snapshot = start.Snapshot;
                    if (start.SourcePrompt != null) turn.SourcePrompt = start.SourcePrompt;
                    turn.AttachmentName = start.AttachmentName;
                    if (start.Images != null) turn.Images = start.Images;
                    if (start.Files != null) turn.Files = start.Files;
                    turn.Slash = start.Slash;
                }
                turn.Step("thinking", "", Labels.Thinking(language), StepState.Running);
                _turns.Add(turn);
                return turn;
            }
        }

        /// <summary>The drafter answered the parked question (card or typed text): the card
        /// becomes a record and the turn continues. False when there was nothing to answer.</summary>
        public bool AnswerAsk(string turnId, string? summary)
        {
            var turn = Find(turnId);
            var ask = turn?.Ask;
            if (turn == null || ask == null || turn.State != TurnPhase.AwaitingAnswer || ask.AnsweredSummary != null) return false;
            ask.AnsweredSummary = string.IsNullOrWhiteSpace(summary) ? "✓" : summary;
            turn.ClearAsk();
            turn.SetState(TurnPhase.Running);
            turn.Step("thinking", "", Labels.Thinking(turn.Language), StepState.Running);
            return true;
        }

        /// <summary>Undo is offered for the turn (it ended, a write committed, not undone yet).</summary>
        public bool CanUndo(string turnId) => Find(turnId)?.CanUndo ?? false;

        /// <summary>The turn's undo ran; <paramref name="note"/> is the pane's line about it.</summary>
        public bool UndoDone(string turnId, string? note)
        {
            var turn = Find(turnId);
            if (turn == null || !turn.CanUndo) return false;
            turn.MarkUndone();
            turn.AddNote(note);
            // The rows are back to their Before values: unsent edits of them are moot.
            turn.ChangeEdits(edits => edits.RemoveAll(e => e.State == CellEditState.Draft || e.State == CellEditState.Failed) > 0);
            return true;
        }

        // ---- Edit a cell: one value in the result card's Changes table ----------------
        //
        // The drafter types over a row's After value. The text lives on the turn, not in
        // the view, so a redraw keeps it (spec 2026-11-10: "card state lives in the visual
        // tree, so an editable cell would lose its text"). The pane writes it to Revit
        // itself (set_parameters on the edit's Ids) and reports back with
        // CellEditApplying / CellEditApplied / CellEditFailed. An applied edit updates the
        // row (After, Source = "edited"); it is not added to the turn's Evidence, so the
        // turn's Undo does not cover it. Nothing is sent to the cloud: the next turn does
        // not know the drafter changed a value (that needs a protocol field).

        /// <summary>The Source of a value the drafter edited in the card.</summary>
        public const string EditedSource = "edited";

        /// <summary>The drafter typed <paramref name="text"/> into Result.Groups[group].Rows[row].After.
        /// Text equal to the row's value drops the draft (changed back). False when the cell is not
        /// editable: unknown or running turn, no Changes card, undone, out of range, or being written.</summary>
        public bool EditCell(string turnId, int group, int row, string? text)
        {
            var turn = Find(turnId);
            var cell = EditableRow(turn, group, row, out var g);
            if (turn == null || cell == null || g == null) return false;
            text ??= "";
            return turn.ChangeEdits(edits =>
            {
                var i = edits.FindIndex(e => e.Group == group && e.Row == row);
                if (i >= 0 && edits[i].State == CellEditState.Applying) return false;
                if (string.Equals(text, cell.After ?? "", StringComparison.Ordinal))
                {
                    if (i < 0 || edits[i].State == CellEditState.Applied) return true;   // nothing to write
                    edits.RemoveAt(i);
                    return true;
                }
                if (i >= 0 && edits[i].State != CellEditState.Applied)
                {
                    edits[i].Text = text;
                    edits[i].State = CellEditState.Draft;
                    edits[i].Error = null;
                }
                else
                {
                    var edit = new CellEdit(group, row, g, cell, text);
                    if (i >= 0) edits[i] = edit; else edits.Add(edit);   // a new edit of an applied cell starts from its new value
                }
                return true;
            });
        }

        /// <summary>The drafter dropped an unsent (draft or failed) edit.</summary>
        public bool DiscardCellEdit(string turnId, int group, int row)
        {
            var turn = Find(turnId);
            return turn != null && turn.ChangeEdits(edits =>
                edits.RemoveAll(e => e.Group == group && e.Row == row
                                     && (e.State == CellEditState.Draft || e.State == CellEditState.Failed)) > 0);
        }

        /// <summary>The pane started writing a draft (or retrying a failed edit) to Revit.</summary>
        public bool CellEditApplying(string turnId, int group, int row) =>
            Move(turnId, group, row, s => s == CellEditState.Draft || s == CellEditState.Failed, (turn, e) =>
            {
                e.State = CellEditState.Applying;
                e.Error = null;
                return false;
            });

        /// <summary>Revit took the edit: the row shows the new value, sourced to the drafter.</summary>
        public bool CellEditApplied(string turnId, int group, int row) =>
            Move(turnId, group, row, s => s == CellEditState.Applying, (turn, e) =>
            {
                var cell = turn.Result!.Groups[group].Rows[row];
                cell.After = e.Text;
                cell.Source = EditedSource;
                e.State = CellEditState.Applied;
                return true;
            });

        /// <summary>The write failed; the text stays for a retry or a discard.</summary>
        public bool CellEditFailed(string turnId, int group, int row, string? error) =>
            Move(turnId, group, row, s => s == CellEditState.Applying, (turn, e) =>
            {
                e.State = CellEditState.Failed;
                e.Error = string.IsNullOrWhiteSpace(error) ? null : error;
                return false;
            });

        /// <summary>Moves one edit from a state <paramref name="from"/> accepts; <paramref name="apply"/>
        /// returns true when it changed the card's values.</summary>
        private bool Move(string turnId, int group, int row, Func<CellEditState, bool> from, Func<Turn, CellEdit, bool> apply)
        {
            var turn = Find(turnId);
            if (turn == null) return false;
            var resultChanged = false;
            var moved = turn.ChangeEdits(edits =>
            {
                var e = edits.FirstOrDefault(x => x.Group == group && x.Row == row);
                if (e == null || !from(e.State)) return false;
                resultChanged = apply(turn, e);
                return true;
            });
            if (moved && resultChanged) turn.ResultValuesChanged();
            return moved;
        }

        /// <summary>The Changes row a drafter may edit, or null.</summary>
        private static ChangeRow? EditableRow(Turn? turn, int group, int row, out TypeGroup? g)
        {
            g = null;
            if (turn == null || !turn.IsFinished || turn.Undone) return null;
            var card = turn.Result;
            if (card == null || card.Kind != ResultKind.Changes) return null;
            if (group < 0 || group >= card.Groups.Count) return null;
            g = card.Groups[group];
            if (row < 0 || row >= g.Rows.Count) { g = null; return null; }
            return g.Rows[row];
        }

        // ------------------------------------------------------------------
        // Events: the router (protocol-3 frames and the add-in's tool runs)
        // ------------------------------------------------------------------

        public void OnProgress(string turnId, ProgressDto? progress)
        {
            var turn = Live(turnId);
            if (turn == null || progress == null) return;
            var label = string.IsNullOrWhiteSpace(progress.Text) ? progress.Kind : progress.Text;
            turn.Progress(progress.Seq, progress.Kind, label,
                progress.Kind == ProgressKinds.RevitBusy ? StepState.Error : StepState.Running);
        }

        /// <summary>Returns the cumulative text of the current leg (what the pane streams).</summary>
        public string? OnReplyDelta(string turnId, ReplyDeltaDto? delta)
        {
            var turn = Live(turnId);
            if (turn == null || delta == null || string.IsNullOrEmpty(delta.Text)) return null;
            return turn.AppendReply(delta.Text);
        }

        public void OnError(string turnId, StreamErrorDto? error)
        {
            var turn = Live(turnId);
            if (turn == null || error == null) return;
            turn.Step("error:" + error.Seq, "error", error.Message, StepState.Error);
        }

        /// <summary>A tool_request arrived and is about to run in Revit.</summary>
        public void OnToolRequest(string turnId, ToolRequestDto? request)
        {
            var turn = Live(turnId);
            if (turn == null || request == null) return;
            var ms = turn.Language == ReceiptLanguage.Ms;
            turn.CloseProgress();
            turn.MarkStepBoundary();
            foreach (var c in request.Calls)
            {
                turn.AddTool(c.Tool);
                turn.Step("call:" + c.CallId, "tool", Labels.Tool(c.Tool, ms), StepState.Running);
            }
            if (request.Kind == ToolRequestKinds.Write)
                turn.Step("preview:" + request.RequestId, "preview", Labels.Preview(ms), StepState.Running);
        }

        /// <summary>The add-in's envelopes for a tool_request, one per call in call order.
        /// <paramref name="budgetText"/> is the developer budget line, or null.</summary>
        public void OnToolResults(string turnId, ToolRequestDto? request, IReadOnlyList<Envelope>? envelopes, string? budgetText = null)
        {
            var turn = Live(turnId);
            if (turn == null || request == null) return;
            envelopes ??= Array.Empty<Envelope>();
            if (request.Kind == ToolRequestKinds.Write)
                turn.Step("preview:" + request.RequestId, "preview", "", StepState.Done);
            turn.Evidence.Add(request, envelopes);
            for (var i = 0; i < request.Calls.Count; i++)
            {
                Envelope? env = i < envelopes.Count ? envelopes[i] : null;
                var ok = env != null && (env.Ok || env.Status == EnvelopeStatus.Rejected);
                turn.Step("call:" + request.Calls[i].CallId, "tool", "", ok ? StepState.Done : StepState.Error,
                    detail: env == null ? null : Trim(env.Summary, 140));
            }
            if (!string.IsNullOrEmpty(budgetText)) turn.Budget(budgetText!);
        }

        /// <summary>Revit opened a dialog with no safe answer during a write and waits for the drafter.</summary>
        public bool OnRevitWaiting(string turnId, string? dialogText)
        {
            var turn = Live(turnId);
            if (turn == null) return false;
            var ms = turn.Language == ReceiptLanguage.Ms;
            var text = (dialogText ?? "").Trim();
            var label = (ms ? "Revit sedang menunggu dialog" : "Revit is waiting on a dialog")
                        + (text.Length > 0 ? ": " + Trim(text, 120) : "");
            turn.Step("dialog:" + turn.NextDialog(), ProgressKinds.RevitBusy, label, StepState.Error);
            return true;
        }

        /// <summary>The runner is about to preview a batch. A re-preview (the model moved under the
        /// card) says "previewing again"; the first preview already has its row.</summary>
        public bool OnPreviewStarting(string turnId, string? batchId)
        {
            var turn = Live(turnId);
            if (turn == null || string.IsNullOrEmpty(batchId)) return false;
            if (!turn.PreviewAgain(batchId!)) return false;
            var ms = turn.Language == ReceiptLanguage.Ms;
            turn.Step("preview:" + batchId, "preview", Labels.PreviewAgain(ms), StepState.Running);
            return true;
        }

        /// <summary>The turn parks on the assistant's question. The text written before it stays above the card.</summary>
        public AskCardModel? OnAsk(string turnId, AskDto? ask)
        {
            var turn = Live(turnId);
            if (turn == null || ask == null) return null;
            var card = AskCardModel.From(ask, turn.Language);
            card.TurnId = turn.Id;
            turn.Step("ask", "", turn.Language == ReceiptLanguage.Ms ? "Menunggu jawapan anda" : "Waiting for your answer", StepState.Done);
            turn.CompleteSteps();
            turn.AddQuestion(card, turn.TakeLead());
            turn.StartNewLeg();
            turn.SetState(TurnPhase.AwaitingAnswer);
            return card;
        }

        /// <summary>turn_end. <paramref name="errorMessage"/> is the stream's error text, if the cloud sent one.</summary>
        public void OnEnd(string turnId, TurnEndDto? end, string? errorMessage = null)
        {
            var turn = Live(turnId);
            if (turn == null || end == null) return;
            var ms = turn.Language == ReceiptLanguage.Ms;
            turn.SetSessionId(end.SessionId);
            turn.CompleteSteps();
            var interrupted = end.Status == TurnEndStatus.Cancelled || end.Status == TurnEndStatus.Interrupted;
            if (interrupted) turn.AddNote(Labels.Cancelled(ms));
            if (end.Status == TurnEndStatus.Failed || end.Status == TurnEndStatus.Limit)
            {
                if (end.Status == TurnEndStatus.Limit)
                    turn.AddNote(ms ? "Giliran ini berhenti pada hadnya." : "This turn stopped at its limit.");
                if (string.IsNullOrWhiteSpace(turn.LegReply))
                {
                    turn.MarkFailed(RouteFailure.Other);
                    turn.AddNote(!string.IsNullOrWhiteSpace(errorMessage) ? errorMessage
                        : ms ? "Copilot tidak dapat menyiapkan permintaan ini." : "The copilot could not finish this request.");
                }
                else if (!string.IsNullOrWhiteSpace(errorMessage))
                    turn.AddNote(errorMessage);
            }
            Finish(turn, interrupted ? TurnPhase.Interrupted
                : end.Status == TurnEndStatus.Failed ? TurnPhase.Failed
                : end.Status == TurnEndStatus.Limit ? TurnPhase.Limit
                : TurnPhase.Done);
        }

        /// <summary>The turn could not run or finish (refused, update required, sign-in, unreachable, ...). The
        /// reply so far stays the model's; the failure is the pane's note.</summary>
        public void OnFailure(string turnId, RouteFailure kind, string? message)
        {
            var turn = Live(turnId);
            if (turn == null) return;
            turn.CompleteSteps();
            turn.MarkFailed(kind);
            turn.AddNote(message ?? "The copilot could not finish this request.");
            Finish(turn, TurnPhase.Failed);
        }

        /// <summary>The drafter pressed Stop, or the chat changed under the turn. The receipt of
        /// what already ran stays; the pane adds its own "Cancelled" line.</summary>
        public void OnStopped(string turnId)
        {
            var turn = Live(turnId);
            if (turn == null) return;
            turn.CompleteSteps();
            turn.AddNote(Labels.Cancelled(turn.Language == ReceiptLanguage.Ms));
            Finish(turn, TurnPhase.Interrupted);
        }

        // ------------------------------------------------------------------

        private static void Finish(Turn turn, TurnPhase phase)
        {
            turn.SetResult(BuildResult(turn));
            turn.SetState(phase);
        }

        private static ResultCardModel? BuildResult(Turn turn)
        {
            try
            {
                var card = ResultCardModel.From(turn.Evidence, turn.Language, turn.AttachmentName);
                return card.Kind == ResultKind.None ? null : card;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] result card: " + ex.Message);
                return null;
            }
        }

        /// <summary>The turn if it exists and has not ended: frames for an ended turn are dropped.</summary>
        private Turn? Live(string turnId)
        {
            var turn = Find(turnId);
            return turn == null || turn.IsFinished ? null : turn;
        }

        private static string Trim(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s!.Length <= max ? s : s.Substring(0, max - 1) + "…";

        /// <summary>Add-in-owned step and note text (English and Malay).</summary>
        internal static class Labels
        {
            public static string Thinking(ReceiptLanguage l) => l == ReceiptLanguage.Ms ? "Berfikir…" : "Thinking…";
            public static string Cancelled(bool ms) => ms ? "Dibatalkan." : "Cancelled.";
            public static string Preview(bool ms) => ms ? "Pratonton perubahan dalam Revit…" : "Previewing the change in Revit…";
            public static string PreviewAgain(bool ms) => ms ? "Pratonton semula perubahan dalam Revit…" : "Previewing the change in Revit again…";

            public static string Tool(string tool, bool ms) => tool switch
            {
                ToolNames.ModelOverview => ms ? "Membaca gambaran model" : "Reading the model overview",
                ToolNames.FindElements => ms ? "Mencari elemen" : "Finding elements",
                ToolNames.Inspect => ms ? "Memeriksa parameter" : "Inspecting parameters",
                ToolNames.Check => ms ? "Menyemak peraturan" : "Checking the rules",
                ToolNames.SetParameters => ms ? "Menetapkan parameter" : "Setting parameters",
                ToolNames.Look => ms ? "Mengambil imej model" : "Taking a picture of the model",
                ToolNames.RunRevitCode => ms ? "Menjalankan kod Revit" : "Running Revit code",
                _ => tool,
            };
        }
    }
}
