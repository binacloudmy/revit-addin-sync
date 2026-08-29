using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Revit-aware chat router — drives the tunnel-free tool-calling loop
    /// (/tool/generate ↔ /tool/resume via ToolLoopRunner). The agent calls
    /// vetted tools the addin executes in real Revit; scene context is
    /// pull-based (READ tools) with only a lean env header pushed per turn.
    /// </summary>
    public class RevitChatRouter : IChatRouter
    {
        private readonly Func<UIApplication> _getApp;
        private readonly ToolLoopRunner _toolLoop;
        private string _sessionId = Guid.NewGuid().ToString();

        /// <summary>The session id stamped on every backend call this router makes
        /// (route/generate/tool-loop). Exposed so feedback (👍/👎) can carry the
        /// same session the rated response was produced under.</summary>
        public string SessionId => _sessionId;

        /// <summary>Append one line to %LOCALAPPDATA%\Bina\RevitSync\session.log.
        /// "+ New chat" kept landing on the PREVIOUS backend session (all eight
        /// runs of 2026-07-25/26 piled into one session id) and no amount of
        /// source reading settled whether _sessionId was rotating, so record
        /// what the router actually does: every reset, and the session id +
        /// branch of every send. The backend logs the id it RECEIVES; these two
        /// together localise the divergence to one side in a single test.</summary>
        private static void TraceSession(string message)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bina", "RevitSync");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "session.log"),
                    $"{DateTime.Now:HH:mm:ss} [session] {message}{Environment.NewLine}");
            }
            catch { /* diagnostics must never break a turn */ }
        }

        private static string Short(string id) =>
            string.IsNullOrEmpty(id) ? "(none)" : id.Substring(0, Math.Min(8, id.Length));

        /// <summary>Generates a fresh session id so the backend treats the next
        /// request as a brand-new conversation with no prior history, and drops
        /// any parked HITL/confirmation state.
        ///
        /// Clearing the parked state matters because RouteAsync checks
        /// _pendingHitl BEFORE it builds a normal turn: a clarify card left
        /// unanswered in the old chat would swallow the new chat's first prompt
        /// as an ANSWER to the old run — resumed on the old run_id and old
        /// session_id, carrying the old history, with the fresh session id never
        /// sent at all. (That path is a real leak, but it is NOT confirmed as
        /// the cause of the 2026-07-25 "new chat remembers the old topic"
        /// reports: none of the runs in that session recorded a get_user_input
        /// call. TraceSession above is what settles it.)</summary>
        public void ResetSession()
        {
            var previous = _sessionId;
            _sessionId = Guid.NewGuid().ToString();
            // router=<hash> on both reset and send lines: if the hashes differ,
            // "+ New chat" is resetting a DIFFERENT router instance than the one
            // that sends — which would explain a rotated id never reaching the wire.
            TraceSession($"reset {Short(previous)} -> {Short(_sessionId)} router={GetHashCode()} "
                       + $"(hitl={( _pendingHitl != null )} confirm={( _pendingConfirm != null )})");
            _pendingHitl = null;

            // Unpause an abandoned mutate-confirmation server-side (fire-and-
            // forget, same shape as RouteAsync's stale-confirm path) so the run
            // does not sit paused forever with its session unflushed.
            var parked = _pendingConfirm;
            _pendingConfirm = null;
            if (parked != null && parked.TryClaim())
            {
                var token = BinaConfig.Load()?.AccessToken ?? "";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _toolLoop.ResumeWithConfirmationAsync(
                            parked.RunId, parked.SessionId, parked.Pending,
                            approve: false, parked.Narration, null, token).ConfigureAwait(false);
                    }
                    catch { /* best-effort: chat was abandoned, reply discarded */ }
                });
            }
        }

        /// <summary>Continue an earlier conversation: subsequent calls carry its
        /// session id, so the backend replays that session's history. A null/empty
        /// id (history saved before Continue existed) starts a fresh session.</summary>
        public void AdoptSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) ResetSession();
            else _sessionId = sessionId;
        }

        // Shared HttpClient for the tool-loop (long timeout — a tool's Revit
        // execution can run minutes on a cold/large model).
        private static readonly System.Net.Http.HttpClient _toolHttp =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(620) };

        public RevitChatRouter(Func<UIApplication> getApp)
        {
            _getApp = getApp;
            _toolLoop = new ToolLoopRunner(new ToolLoopService(_toolHttp));
        }

        /// <summary>Optional callback invoked on every streamed code chunk
        /// from /generate/stream so the chat can render code as it arrives.
        /// Receives the cumulative code string so the UI can replace, not
        /// append. Set null to disable streaming (falls back to one-shot).</summary>
        public Action<string> OnCodeStream { get; set; }

        /// <summary>Optional callback for live progress — fires on every
        /// backend "status" event (labels like "Analyzing your request…",
        /// "Collecting information…", "Generating code…") and every "tool"
        /// event ("create_wall (running)…"). The pane renders these as a
        /// status line + spinner in a live progress card and clears it on
        /// done/error. When unset, progress lines fall back to OnCodeStream
        /// so the existing "Drafting…" card still updates.</summary>
        public Action<string> OnProgress { get; set; }

        /// <summary>Optional callback for the TYPED step trail — fires alongside
        /// OnProgress on every reducer application (server-streamed tool/status
        /// events AND local Revit-execution ticks), carrying a snapshot
        /// (new List copy) of the live ProgressStep trail instead of a
        /// pre-rendered string. UI consumers that want structured rows (icons,
        /// timestamps, per-row state) should use this instead of parsing
        /// OnProgress's rendered text.</summary>
        public Action<IReadOnlyList<ProgressStep>> OnSteps { get; set; }

        /// <summary>Optional callback for the streaming REASONING timeline — fires
        /// on every `reasoning` SSE event (step_id/label/text_delta/state),
        /// carrying a snapshot of the accumulated <see cref="ReasoningStep"/>
        /// trail. Separate from OnSteps: this carries the backend's working
        /// narrative (multi-sentence body per step), not terse tool labels.</summary>
        public Action<IReadOnlyList<ReasoningStep>> OnReasoning { get; set; }

        /// <summary>Optional callback for the stream-v2 segmented turn body
        /// (copilot-stream-v2 spec) — fires whenever the ordered block list
        /// grows (a narrative leg extends, a new leg opens, a tool card lands).
        /// Never fires on legacy turns (no segment ids), so a pane wired to it
        /// still renders old backends exactly as today.</summary>
        public Action<IReadOnlyList<TurnBlock>> OnBlocks { get; set; }

        /// <summary>Screenshots pasted with the NEXT prompt (base64 PNG). Set by
        /// the viewmodel right before RouteAsync, consumed and cleared by the
        /// route that builds the request — same per-call pattern as OnProgress.</summary>
        public List<string> PendingImages { get; set; }

        /// <summary>P2 slash command for the NEXT prompt: the backend command id
        /// (and optional args) picked from the slash menu. Set by the viewmodel
        /// right before RouteAsync, consumed + cleared when the request is built —
        /// same per-call pattern as PendingImages. When set, /tool/generate
        /// carries command_id and the backend dispatches that P1 definition
        /// (instructions + tool allowlist) instead of a plain NL turn.</summary>
        public string PendingCommandId { get; set; }
        public Dictionary<string, object> PendingCommandArgs { get; set; }

        // Drives Cancel — set per stream so the pane's Cancel button can abort
        // the in-flight HttpClient request (CancelStream() trips this token,
        // which unwinds GenerateCodeStreamAsync's reader and disposes the
        // HttpClient). Guarded by _cancelLock since the pane (UI thread) and
        // the stream loop (router) touch it concurrently.
        private readonly object _cancelLock = new object();
        private CancellationTokenSource _streamCts;

        // HITL clarify pause carried between turns: the agent asked a question,
        // the user's NEXT message is the answer (resumed via /tool/resume-input),
        // not a new command. Cleared on consume; a stale entry (server restart)
        // fails the resume and surfaces as a normal error reply.
        internal sealed class PendingHitl
        {
            public string RunId;
            public string SessionId;
            public List<ClarifyRequirement> Clarify;
            public List<ChoiceRequirement> Choices;
            // One-shot claim, same rationale as PendingConfirm: a typed
            // free-text answer and a tapped option submit must never resume
            // the same requirement twice.
            private int _claimed;
            public bool TryClaim() => System.Threading.Interlocked.Exchange(ref _claimed, 1) == 0;
        }
        private PendingHitl _pendingHitl;

        // Mutate-confirmation pause carried between turns: the loop parked on a
        // pending MUTATE batch and the pane is showing the Ya/Tidak card. Ya/Tidak
        // resolves via ResolvePendingActionsAsync; a NEW user message instead
        // auto-rejects the stale batch in the background (the paused run must be
        // resumed so session history stays coherent) and routes normally.
        internal sealed class PendingConfirm
        {
            public string RunId;
            public string SessionId;
            public List<PendingToolCall> Pending;
            public string Narration;
            public IReadOnlyList<ProgressStep> Steps;
            public IReadOnlyList<ReasoningStep> ReasoningSteps;
            // Stream v2 (T5): the block list at pause time, carried across the
            // confirm so the resumed stream appends to the same visual thread.
            public IReadOnlyList<TurnBlock> Blocks;
            // When the pause began — the thinking timer must EXCLUDE the
            // drafter's decision time (the 457s lesson, 2026-08-18): on
            // resolution every carried step's StartedUtc shifts forward by the
            // pause duration so elapsed math never counts the wait.
            public DateTime PausedUtc = DateTime.UtcNow;
            // One-shot resolution claim. EVERY path that resumes this batch
            // (Ya/Tidak click, Auto mode, the stale-confirm auto-reject, and
            // ResetSession's abandon reject) must claim it first — a batch may
            // only be resumed ONCE, and the race loser gets told the truth
            // instead of resuming twice (backend "run not found") or silently
            // doing nothing (2026-08-18 UAT: Ya clicked → "Tiada tindakan
            // tertunda", nothing executed — a background auto-reject had
            // already spent the batch).
            private int _claimed;
            public bool TryClaim() => System.Threading.Interlocked.Exchange(ref _claimed, 1) == 0;
        }
        private PendingConfirm _pendingConfirm;

        /// <summary>True while a /generate/stream request is in flight — lets
        /// the pane show/enable the Cancel button only when there's something
        /// to cancel.</summary>
        public bool IsStreaming
        {
            get { lock (_cancelLock) return _streamCts != null; }
        }

        /// <summary>Cancel the in-flight streaming request (wired to the pane's
        /// Cancel button). Cancels the underlying HttpClient request; the
        /// stream loop then unwinds and the route call returns. No-op when
        /// nothing is streaming.</summary>
        public void CancelStream()
        {
            CancellationTokenSource cts;
            lock (_cancelLock) cts = _streamCts;
            try { cts?.Cancel(); } catch { /* already disposed */ }
        }

        /// <summary>Push a progress line to the pane. Prefers the dedicated
        /// OnProgress hook; falls back to OnCodeStream so a pane that only
        /// wired the streaming-code callback still sees live updates.</summary>
        private void EmitProgress(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return;
            var sink = OnProgress;
            if (sink != null) { try { sink(label); } catch { /* UI hiccup */ } return; }
            var code = OnCodeStream;
            if (code != null) { try { code(label); } catch { /* UI hiccup */ } }
        }


        /// <summary>Clear the live progress card (on done / error / cancel).
        /// Sends an empty label to OnProgress so the pane can hide the card +
        /// stop the spinner. OnCodeStream is left alone — the pane replaces the
        /// "Drafting…" card with the final reply on its own, so blanking it
        /// here would only flash an empty bubble.</summary>
        private void ClearProgress()
        {
            var sink = OnProgress;
            if (sink != null) { try { sink(""); } catch { /* UI hiccup */ } }
        }

        /// <summary>Map a tool-loop outcome to the wire RouteResult. A clarify
        /// pause stashes the HITL state for the next message and surfaces the
        /// agent's question; otherwise it's the normal reply/code conversion.</summary>
        private RouteResult ToolOutcomeToRoute(ToolLoopOutcome outcome)
        {
            if (outcome == null)
                return new RouteResult { ToolId = "ai-generated", Reply = "Tool run failed.", IsQuery = true };
            if (outcome.AwaitingUserInput)
            {
                _pendingHitl = new PendingHitl
                {
                    RunId = outcome.RunId,
                    SessionId = outcome.SessionId,
                    Clarify = outcome.Clarify,
                    Choices = outcome.Choices,
                };
                return new RouteResult
                {
                    ToolId = "ai-generated",
                    NeedsClarification = true,
                    ClarifyingQuestion = ComposeClarifyQuestion(outcome),
                    Choices = outcome.Choices,
                    ChoiceBatch = _pendingHitl,
                    IsQuery = true,
                    Steps = outcome.Steps,
                };
            }
            if (outcome.AwaitingConfirmation)
            {
                _pendingConfirm = new PendingConfirm
                {
                    RunId = outcome.RunId,
                    SessionId = outcome.SessionId,
                    Pending = outcome.PendingActions,
                    Narration = outcome.NarrationSoFar,
                    Steps = outcome.Steps,
                    ReasoningSteps = outcome.ReasoningSteps,
                    Blocks = outcome.Blocks,
                };
                var labels = new List<string>();
                foreach (var c in outcome.PendingActions ?? new List<PendingToolCall>())
                    labels.Add(ToolLabels.Label(c.Tool, c.Args));
                return new RouteResult
                {
                    ToolId = "ai-generated",
                    NeedsActionConfirmation = true,
                    ActionLabels = labels,
                    // Whatever the agent narrated before pausing (may be empty —
                    // the card carries the action list either way).
                    Reply = outcome.Reply ?? "",
                    IsQuery = true,
                    Steps = outcome.Steps,
                    ReasoningSteps = ToUiReasoning(outcome.ReasoningSteps),
                    ReasoningElapsedSeconds = outcome.ReasoningElapsedSeconds,
                    Blocks = ToUiBlocks(outcome.Blocks),
                    // Action Mode addendum: Auto mode's programmatic-accept path
                    // is only safe when EVERY call in the batch opted out of
                    // confirmation. Empty/null pending list is never auto-eligible
                    // (nothing to accept, and All() on an empty sequence is
                    // vacuously true — guard explicitly rather than rely on that).
                    AutoApprovable = outcome.PendingActions != null && outcome.PendingActions.Count > 0
                        && outcome.PendingActions.All(c => !c.RequiresConfirmation),
                    // Card-owned batch: the SAME object as _pendingConfirm, so a
                    // Ya click resolves THIS batch even if the router field was
                    // cleared/swapped in the meantime (one-shot claim inside).
                    PendingBatch = _pendingConfirm,
                };
            }
            return new RouteResult
            {
                ToolId = "ai-generated",
                Receipt = ToUiReceipt(outcome.Receipt),
                // Empty when tools ran (nothing for the pane to execute);
                // populated when the agent fell back to codegen → the pane
                // runs it through the normal executor (compile-gate + tx).
                Code = outcome.Code ?? "",
                Failed = !outcome.Success,
                Reply = !string.IsNullOrWhiteSpace(outcome.Reply)
                    ? outcome.Reply
                    : (outcome.Success ? "Done." : (outcome.Error ?? "Tool run failed.")),
                IsQuery = string.IsNullOrWhiteSpace(outcome.Code) || outcome.IsQuery,
                ToolCallTrace = outcome.ToolsUsed.Count > 0 ? outcome.ToolsUsed : null,
                Steps = outcome.Steps,
                Tindakan = outcome.Tindakan ?? "",
                ReasoningSteps = ToUiReasoning(outcome.ReasoningSteps),
                ReasoningElapsedSeconds = outcome.ReasoningElapsedSeconds,
                Followups = outcome.Followups,
                ResultSummary = ToUiResultSummary(outcome.ResultSummary),
                CodeRequiresConfirmation = outcome.CodeRequiresConfirmation,
                Blocks = ToUiBlocks(outcome.Blocks),
            };
        }

        // Stream v2: outcome snapshot -> UI list (null stays null — legacy).
        private static List<TurnBlock> ToUiBlocks(IReadOnlyList<TurnBlock> blocks) =>
            blocks == null || blocks.Count == 0 ? null : new List<TurnBlock>(blocks);

        // Turn receipt: service dict -> UI model. Null when nothing actually
        // changed — a receipt claiming zero changes is noise, not evidence.
        private static ReceiptModel ToUiReceipt(Dictionary<string, object> raw)
        {
            if (raw == null) return null;
            int I(string k) => raw.TryGetValue(k, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : 0;
            var m = new ReceiptModel { Added = I("added"), Modified = I("modified"), Deleted = I("deleted") };
            if (m.Added + m.Modified + m.Deleted == 0) return null;
            if (raw.TryGetValue("by_category", out var bc))
            {
                if (bc is Dictionary<string, int> d1)
                    foreach (var kv in d1.OrderByDescending(k => k.Value))
                        m.ByCategory.Add(new KeyValuePair<string, int>(kv.Key, kv.Value));
                else if (bc is System.Collections.IDictionary d2)
                    foreach (System.Collections.DictionaryEntry e in d2)
                        if (int.TryParse(e.Value?.ToString(), out var c))
                            m.ByCategory.Add(new KeyValuePair<string, int>(e.Key?.ToString() ?? "?", c));
            }
            m.BeforeImage = raw.TryGetValue("before_image", out var b) ? b?.ToString() : null;
            m.AfterImage = raw.TryGetValue("after_image", out var a) ? a?.ToString() : null;
            return m;
        }

        // ─── Wire DTO -> UI model mapping (2026-08-02 reasoning-ui spec) ────────
        private static List<ReasoningStep> ToUiReasoning(IReadOnlyList<ReasoningStep> steps) =>
            steps == null ? null : new List<ReasoningStep>(steps);

        private static ResultSummaryModel ToUiResultSummary(ResultSummaryDto dto)
        {
            if (dto == null) return null;
            var m = new ResultSummaryModel { Title = dto.Title ?? "", Total = dto.Total };
            foreach (var r in dto.Rows ?? new List<ResultSummaryRowDto>())
                m.Rows.Add(new ResultSummaryRow(r.Label ?? "", r.Count, r.ColorHint ?? ""));
            return m;
        }

        // The user-facing clarify question: the agent's own reply line first,
        // then each unanswered field's description (the prompt instructs the
        // agent to put the actual available options in there).
        private static string ComposeClarifyQuestion(ToolLoopOutcome o)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(o.Reply)) parts.Add(o.Reply.Trim());
            foreach (var req in o.Clarify ?? new List<ClarifyRequirement>())
                foreach (var f in req.Fields ?? new List<ClarifyField>())
                {
                    if (f.Value != null) continue;   // already pre-filled
                    var line = !string.IsNullOrWhiteSpace(f.Description) ? f.Description : f.Name;
                    if (!string.IsNullOrWhiteSpace(line) && !parts.Contains(line.Trim()))
                        parts.Add(line.Trim());
                }
            return parts.Count > 0 ? string.Join("\n", parts) : "I need one more detail to proceed.";
        }

        // Map the user's free-text answer onto the clarify schema: pre-filled
        // values pass through; the message fills the FIRST empty field. Any
        // remaining empty fields stay unanswered, so the backend re-pauses and
        // asks for them next — sequential Q&A instead of a guessed mapping.
        private static List<ClarifyAnswerDto> BuildAnswers(PendingHitl h, string message)
        {
            var answers = new List<ClarifyAnswerDto>();
            bool used = false;
            foreach (var req in h.Clarify ?? new List<ClarifyRequirement>())
            {
                var a = new ClarifyAnswerDto { RequirementId = req.RequirementId };
                foreach (var f in req.Fields ?? new List<ClarifyField>())
                {
                    if (f.Value != null) { a.Values[f.Name] = f.Value; continue; }
                    if (!used) { a.Values[f.Name] = message; used = true; }
                }
                answers.Add(a);
            }
            // Typed message while an ask_user card is open = the free-text
            // ("Lain-lain") escape: the text answers the FIRST question of
            // each choice requirement (single-question cards in practice).
            foreach (var req in h.Choices ?? new List<ChoiceRequirement>())
            {
                var a = new ClarifyAnswerDto { RequirementId = req.RequirementId };
                foreach (var q in req.Questions ?? new List<AskQuestionDto>())
                {
                    if (!used) { a.Selections[q.Question] = new List<string> { message }; used = true; }
                    else a.Selections[q.Question] = new List<string> { message };
                }
                answers.Add(a);
            }
            return answers;
        }

        /// <summary>Resume an ask_user pause with TAPPED selections —
        /// {question text -> selected labels}. Uses the CARD-OWNED batch
        /// (survives router swaps/stale clears, one-shot claim) and the same
        /// resume-input lane as a typed answer.</summary>
        public async Task<RouteResult> SubmitChoiceSelectionsAsync(
            Dictionary<string, List<string>> selections, object batch = null)
        {
            var hitl = (batch as PendingHitl) ?? _pendingHitl;
            if (hitl == null) return null;
            if (ReferenceEquals(hitl, _pendingHitl)) _pendingHitl = null;
            if (!hitl.TryClaim())
                return new RouteResult
                {
                    ToolId = "ai-generated", IsQuery = true,
                    Reply = "Soalan ini telah pun dijawab. Hantar semula permintaan jika perlu.",
                };

            var answers = new List<ClarifyAnswerDto>();
            foreach (var req in hitl.Choices ?? new List<ChoiceRequirement>())
            {
                var a = new ClarifyAnswerDto { RequirementId = req.RequirementId };
                foreach (var q in req.Questions ?? new List<AskQuestionDto>())
                    if (selections.TryGetValue(q.Question, out var picked) && picked != null && picked.Count > 0)
                        a.Selections[q.Question] = picked;
                if (a.Selections.Count > 0) answers.Add(a);
            }
            if (answers.Count == 0) return null;

            var token = BinaConfig.Load()?.AccessToken ?? "";
            EmitProgress("Thinking…");
            CancellationTokenSource scts = new CancellationTokenSource();
            lock (_cancelLock)
            {
                try { _streamCts?.Dispose(); } catch { }
                _streamCts = scts;
            }
            ToolLoopOutcome so = null;
            bool scanceled = false;
            try
            {
                so = await _toolLoop.ResumeWithInputAsync(
                    hitl.RunId, hitl.SessionId, answers, token, EmitProgress,
                    scts.Token, onReply: t => { try { OnCodeStream?.Invoke(t); } catch { /* UI hiccup */ } },
                    onSteps: steps => { try { OnSteps?.Invoke(steps); } catch { /* UI hiccup */ } },
                    onReasoning: steps => { try { OnReasoning?.Invoke(steps); } catch { /* UI hiccup */ } },
                    onBlocks: blocks => { try { OnBlocks?.Invoke(blocks); } catch { /* UI hiccup */ } }
                    ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { scanceled = true; }
            catch (Exception ex) { so = new ToolLoopOutcome { Success = false, Error = ex.Message }; }
            finally
            {
                if (scts.IsCancellationRequested) scanceled = true;
                ClearProgress();
                lock (_cancelLock)
                {
                    if (ReferenceEquals(_streamCts, scts)) _streamCts = null;
                }
                try { scts.Dispose(); } catch { }
            }
            if (scanceled)
                return new RouteResult { ToolId = "ai-generated", Reply = "Interrupted.", IsQuery = true, Interrupted = true };
            return ToolOutcomeToRoute(so);
        }

        /// <summary>Resolve the parked mutate-confirmation card. Ya (approve=true)
        /// executes the batch in Revit and keeps driving the loop; Tidak resumes
        /// the run with rejected results so the agent acknowledges. Returns the
        /// follow-on RouteResult (done / another confirm card / clarify), or null
        /// when no confirmation is pending (double-click, stale card).</summary>
        public async Task<RouteResult> ResolvePendingActionsAsync(bool approve, object batch = null)
        {
            // The card's own batch wins (survives router swaps and the stale
            // path clearing the field); the router field is the legacy
            // fallback for callers that predate card-owned batches.
            var pc = (batch as PendingConfirm) ?? _pendingConfirm;
            if (pc == null) return null;
            if (ReferenceEquals(pc, _pendingConfirm)) _pendingConfirm = null;
            if (!pc.TryClaim())
                return new RouteResult
                {
                    ToolId = "ai-generated", IsQuery = true,
                    Reply = "Tindakan ini telah pun diselesaikan (mesej baru menolaknya sebelum klik sampai). Hantar semula permintaan untuk cuba lagi.",
                };

            var cfg = BinaConfig.Load();
            var token = cfg?.AccessToken ?? "";

            // ─── Stream v2 confirm continuity (T5) ──────────────────────────
            // The thinking timer must not count the drafter's decision time
            // (the 457s lesson): shift every carried reasoning step's clock
            // forward by the pause duration, so elapsed math resumes where the
            // model actually stopped working. Then stamp the decision into the
            // block thread as a compact record — the resumed frames append
            // UNDER it, so the turn reads as one continuous thread.
            var pause = DateTime.UtcNow - pc.PausedUtc;
            if (pause > TimeSpan.Zero && pc.ReasoningSteps != null)
                foreach (var rs in pc.ReasoningSteps)
                    if (rs != null) rs.StartedUtc = rs.StartedUtc.Add(pause);
            List<TurnBlock> continuedBlocks = null;
            if (pc.Blocks != null && pc.Blocks.Count > 0)
            {
                int n = pc.Pending?.Count ?? 0;
                continuedBlocks = new List<TurnBlock>(pc.Blocks)
                {
                    new TurnBlock
                    {
                        Kind = TurnBlockKind.ConfirmCard,
                        Approved = approve,
                        Text = approve
                            ? (n == 1 ? "1 tindakan diluluskan" : n + " tindakan diluluskan")
                            : (n == 1 ? "1 tindakan ditolak" : n + " tindakan ditolak"),
                    },
                };
            }

            EmitProgress(approve ? "Menjalankan tindakan…" : "Thinking…");
            CancellationTokenSource ccts = new CancellationTokenSource();
            lock (_cancelLock)
            {
                try { _streamCts?.Dispose(); } catch { }
                _streamCts = ccts;
            }
            ToolLoopOutcome co = null;
            bool ccanceled = false;
            try
            {
                co = await _toolLoop.ResumeWithConfirmationAsync(
                    pc.RunId, pc.SessionId, pc.Pending, approve, pc.Narration, pc.Steps,
                    token, EmitProgress, ccts.Token,
                    onReply: t => { try { OnCodeStream?.Invoke(t); } catch { /* UI hiccup */ } },
                    onSteps: steps => { try { OnSteps?.Invoke(steps); } catch { /* UI hiccup */ } },
                    priorReasoningSteps: pc.ReasoningSteps,
                    onReasoning: steps => { try { OnReasoning?.Invoke(steps); } catch { /* UI hiccup */ } },
                    priorBlocks: continuedBlocks,
                    onBlocks: blocks => { try { OnBlocks?.Invoke(blocks); } catch { /* UI hiccup */ } }
                    ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { ccanceled = true; }
            catch (Exception ex) { co = new ToolLoopOutcome { Success = false, Error = ex.Message }; }
            finally
            {
                if (ccts.IsCancellationRequested) ccanceled = true;
                ClearProgress();
                lock (_cancelLock)
                {
                    if (ReferenceEquals(_streamCts, ccts)) _streamCts = null;
                }
                try { ccts.Dispose(); } catch { }
            }
            if (ccanceled)
                return new RouteResult { ToolId = "ai-generated", Reply = "Interrupted.", IsQuery = true, Interrupted = true };
            return ToolOutcomeToRoute(co);
        }

        public async Task<RouteResult> RouteAsync(string message, string fallbackToolId)
        {
            // Phase timing — pinpoint where post-send wall-clock goes. Correlate
            // these with the [idle] UI-thread-blocked heartbeat to see whether a
            // freeze is context capture (UI thread), the backend round-trip
            // (should be off-UI), or post-response work.
            var __swRoute = System.Diagnostics.Stopwatch.StartNew();
            var cfg = BinaConfig.Load();
            var token = cfg?.AccessToken ?? "";
            // Pull-based scene sight: only the static env header goes with the
            // prompt — the agent gathers levels/views/selection on demand via
            // READ tools (get_scene_overview, list_*, query_geometry). No
            // pre-send collectors, no UI-thread freeze, no stale selection.
            var ctx = BuildEnvContext();
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;

            // Consume any screenshots pasted with this prompt (cleared so they
            // never leak into the next route).
            var images = PendingImages;
            PendingImages = null;

            // Consume the slash command (P2) the same way — so the tool turn
            // carries it and it never leaks into the following plain-NL turn.
            var commandId = PendingCommandId;
            var commandArgs = PendingCommandArgs;
            PendingCommandId = null;
            PendingCommandArgs = null;

            // ─── Stale mutate-confirmation ───────────────────────────────────
            // The user typed a NEW message instead of answering the Ya/Tidak
            // card. Auto-reject the parked batch in the background (fire-and-
            // forget, outcome discarded) so the paused run resumes and the
            // session history the next turn reads is complete — then route the
            // new message normally. The VM kills the stale card's buttons.
            var staleConfirm = _pendingConfirm;
            _pendingConfirm = null;   // spent or not, never leave a stale field behind
            if (staleConfirm != null && staleConfirm.TryClaim())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _toolLoop.ResumeWithConfirmationAsync(
                            staleConfirm.RunId, staleConfirm.SessionId, staleConfirm.Pending,
                            approve: false, staleConfirm.Narration, null, token).ConfigureAwait(false);
                    }
                    catch { /* best-effort: abandoned batch, agent reply discarded */ }
                });
            }

            // ─── HITL clarify continuation ───────────────────────────────────
            // The previous turn paused on get_user_input — THIS message is the
            // user's ANSWER, not a new command. Resume the paused run with it.
            var hitl = _pendingHitl;
            if (hitl != null && !hitl.TryClaim()) { _pendingHitl = null; hitl = null; }   // spent by a tapped submit
            if (hitl != null)
            {
                _pendingHitl = null;
                TraceSession($"send HITL-RESUME session={Short(hitl.SessionId)} "
                           + $"(router holds {Short(_sessionId)}) run={Short(hitl.RunId)}");
                EmitProgress("Thinking…");
                CancellationTokenSource hcts = new CancellationTokenSource();
                lock (_cancelLock)
                {
                    try { _streamCts?.Dispose(); } catch { }
                    _streamCts = hcts;
                }
                ToolLoopOutcome ho = null;
                bool hcanceled = false;
                try
                {
                    ho = await _toolLoop.ResumeWithInputAsync(
                        hitl.RunId, hitl.SessionId, BuildAnswers(hitl, message), token, EmitProgress,
                        hcts.Token, onReply: t => { try { OnCodeStream?.Invoke(t); } catch { /* UI hiccup */ } },
                        onSteps: steps => { try { OnSteps?.Invoke(steps); } catch { /* UI hiccup */ } },
                        onReasoning: steps => { try { OnReasoning?.Invoke(steps); } catch { /* UI hiccup */ } },
                        onBlocks: blocks => { try { OnBlocks?.Invoke(blocks); } catch { /* UI hiccup */ } }
                        ).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { hcanceled = true; }
                catch (Exception ex) { ho = new ToolLoopOutcome { Success = false, Error = ex.Message }; }
                finally
                {
                    if (hcts.IsCancellationRequested) hcanceled = true;
                    ClearProgress();
                    lock (_cancelLock)
                    {
                        if (ReferenceEquals(_streamCts, hcts)) _streamCts = null;
                    }
                    try { hcts.Dispose(); } catch { }
                }
                if (hcanceled)
                    return new RouteResult { ToolId = "ai-generated", Reply = "Interrupted.", IsQuery = true, Interrupted = true };
                return ToolOutcomeToRoute(ho);
            }

            // ─── Tunnel-free tool-calling turn — the only route ─────────────
            // The agent calls vetted tools that the addin runs in real Revit
            // via /tool/generate ↔ /tool/resume. (Scope block kept from the
            // old ToolHttpEnabled gate to avoid re-indenting the whole turn.)
            {
                var treq = new AIRequest
                {
                    Prompt = message, Context = ctx, UserId = userId, SessionId = _sessionId,
                    Images = images,
                    CommandId = commandId, CommandArgs = commandArgs,
                };
                TraceSession($"send NORMAL session={Short(_sessionId)} router={GetHashCode()}");

                // Live progress — HONEST, event-driven (no fake timer rotation).
                // /tool/generate is a single non-streaming POST, so until the
                // backend answers we genuinely only know one thing: we're waiting
                // on the model. Show ONE truthful "Thinking…" (the pane's spinner
                // animates, so it doesn't look frozen). The ONLY specific step
                // labels come from the per-tool callback below, which fires when a
                // REAL tool actually executes in Revit — never a guessed phase.
                EmitProgress("Thinking…");

                // Per-request CTS so the pane's Stop button can abort this tool
                // reply mid-flight — CancelStream() trips this token (same gate the
                // codegen path uses). Without this the tool path could not be
                // cancelled until the reply finished.
                CancellationTokenSource cts = new CancellationTokenSource();
                lock (_cancelLock)
                {
                    try { _streamCts?.Dispose(); } catch { }
                    _streamCts = cts;
                }

                ToolLoopOutcome outcome = null;
                bool canceled = false;
                try
                {
                    // onProgress now receives ready-to-show labels (the streaming
                    // first turn pushes "Generating…" / "Running <tool>…" live).
                    // onReply receives the CUMULATIVE answer text as the model
                    // decodes it — surfaced through OnCodeStream so the pane can
                    // render the reply growing live (same hook the codegen
                    // streaming path uses).
                    outcome = await _toolLoop.RunAsync(
                        treq, token, EmitProgress, cts.Token,
                        onReply: t => { try { OnCodeStream?.Invoke(t); } catch { /* UI hiccup */ } },
                        onSteps: steps => { try { OnSteps?.Invoke(steps); } catch { /* UI hiccup */ } },
                        onReasoning: steps => { try { OnReasoning?.Invoke(steps); } catch { /* UI hiccup */ } },
                        onBlocks: blocks => { try { OnBlocks?.Invoke(blocks); } catch { /* UI hiccup */ } }
                        ).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
                catch (Exception ex)
                {
                    outcome = new ToolLoopOutcome { Success = false, Error = ex.Message };
                }
                finally
                {
                    // The tool loop swallows the cancel internally and returns an
                    // error outcome rather than throwing — so trust the TOKEN, not
                    // the exception, to tell a user Stop from a real failure.
                    if (cts.IsCancellationRequested) canceled = true;
                    ClearProgress();
                    lock (_cancelLock)
                    {
                        if (ReferenceEquals(_streamCts, cts)) _streamCts = null;
                    }
                    try { cts.Dispose(); } catch { }
                }

                // User hit Stop — clean message, not the raw "tool/generate failed:
                // The operation was canceled." internal error.
                if (canceled)
                    return new RouteResult { ToolId = "ai-generated", Reply = "Interrupted.", IsQuery = true, Interrupted = true };
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][timing] tool-loop total={__swRoute.ElapsedMilliseconds}ms tools={string.Join(",", outcome.ToolsUsed)} ok={outcome.Success}");
                return ToolOutcomeToRoute(outcome);
            }
        }


        /// <summary>The env header — the Claude Code "env block" analog.
        /// Static identity only: project_id (bina-be config value no READ tool
        /// can supply), project name, Revit version, addin version. NO scene
        /// state — no collectors, no PlacementFacts, O(1) on the UI thread.
        /// The agent pulls scene sight via READ tools (get_scene_overview,
        /// list_*, query_geometry) instead.</summary>
        private ModelContext BuildEnvContext()
        {
            var ctx = new ModelContext();
            try
            {
                var cfgForProject = BinaConfig.Load();
                if ((cfgForProject?.ProjectId ?? 0) > 0)
                    ctx.ProjectId = cfgForProject.ProjectId.ToString();
            }
            catch { /* best-effort */ }
            try
            {
                ctx.AddinVersion = System.Reflection.Assembly
                    .GetExecutingAssembly().GetName().Version?.ToString();
            }
            catch { /* best-effort */ }
            try
            {
                // Capability handshake (spec §8.2) — additive, flag-gated
                // (VibeFlags.ManifestHandshake, default OFF). Sourced from the
                // GENERATED manifest so what we claim == what ToolRegistry
                // dispatches; the backend refuses anything outside the
                // intersection before serialising a frame.
                if (BinaVibe.Policy.VibeFlags.Load().ManifestHandshake)
                {
                    ctx.ProtocolVersion = BinaVibe.Mcp.Tools.InstalledToolManifest.ProtocolVersion;
                    ctx.ManifestVersion = BinaVibe.Mcp.Tools.InstalledToolManifest.Version;
                    ctx.InstalledTools = BinaVibe.Mcp.Tools.InstalledToolManifest.Names;
                }
            }
            catch { /* best-effort: legacy header on any failure */ }
            try
            {
                var uidoc = _getApp()?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return ctx;
                ctx.ProjectName = doc.Title;
                ctx.RevitVersion = uidoc.Application.Application.VersionNumber;
            }
            catch { /* best-effort env header */ }
            return ctx;
        }
    }
}
