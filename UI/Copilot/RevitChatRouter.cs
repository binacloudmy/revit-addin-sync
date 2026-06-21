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
    /// Revit-aware chat router.
    ///
    /// Flow:
    ///   1. Try AIService.RouteAsync (vetted-action classifier on bina-ai)
    ///      → if it returns code or a clarifying question, use that.
    ///   2. Else fall through to AIService.GenerateCodeAsync (/generate)
    ///      so the bina-ai Inspector preflight (PRD §12 Step 1) fires and
    ///      free-form prompts get real-model codegen instead of degrading
    ///      to a local QueryInterpreter guess (which used to pick "Doors"
    ///      as its default category).
    ///   3. Returns null only on hard failures (backend unreachable, not
    ///      logged in for routes that require it). Viewmodel still has a
    ///      local fallback for that case.
    /// </summary>
    public class RevitChatRouter : IChatRouter
    {
        private readonly Func<UIApplication> _getApp;
        private readonly AIService _ai;
        private readonly ToolLoopRunner _toolLoop;
        private readonly string _sessionId = Guid.NewGuid().ToString();

        /// <summary>The session id stamped on every backend call this router makes
        /// (route/generate/tool-loop). Exposed so feedback (👍/👎) can carry the
        /// same session the rated response was produced under.</summary>
        public string SessionId => _sessionId;

        // Shared HttpClient for the tool-loop (long timeout — a tool's Revit
        // execution can run minutes on a cold/large model).
        private static readonly System.Net.Http.HttpClient _toolHttp =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(620) };

        public RevitChatRouter(Func<UIApplication> getApp)
        {
            _getApp = getApp;
            _ai = new AIService(BinaConfig.Load().ResolvedAIBaseUrl);
            _toolLoop = new ToolLoopRunner(new ToolLoopService(_toolHttp));
        }

        // Tunnel-free tool-calling: the agent calls vetted MUTATE tools the addin
        // runs in real Revit; when no tool fits it emits codegen on the SAME tool
        // turn (the done frame carries the C#, run via the normal executor).
        //
        // TOOL PATH IS NOW THE ONLY ROUTE. The legacy codegen-fallback endpoints
        // (/generate, /generate/stream, /retry, /record-fix) were removed from the
        // backend (drop-legacy-codegen-fallback), so the BINA_VIBE_TOOL_HTTP=0
        // escape hatch would 404. Forced true; the /generate fall-through below is
        // now DEAD CODE — remove GenerateCodeAsync / AIServiceStream / the
        // post-tool block in a Windows session where the build can be verified.
        private static bool ToolHttpEnabled => true;

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

        /// <summary>Screenshots pasted with the NEXT prompt (base64 PNG). Set by
        /// the viewmodel right before RouteAsync, consumed and cleared by the
        /// route that builds the request — same per-call pattern as OnProgress.</summary>
        public List<string> PendingImages { get; set; }

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
        private sealed class PendingHitl
        {
            public string RunId;
            public string SessionId;
            public List<ClarifyRequirement> Clarify;
        }
        private PendingHitl _pendingHitl;

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
                };
                return new RouteResult
                {
                    ToolId = "ai-generated",
                    NeedsClarification = true,
                    ClarifyingQuestion = ComposeClarifyQuestion(outcome),
                    IsQuery = true,
                    Steps = outcome.Steps,
                };
            }
            return new RouteResult
            {
                ToolId = "ai-generated",
                // Empty when tools ran (nothing for the pane to execute);
                // populated when the agent fell back to codegen → the pane
                // runs it through the normal executor (compile-gate + tx).
                Code = outcome.Code ?? "",
                Reply = !string.IsNullOrWhiteSpace(outcome.Reply)
                    ? outcome.Reply
                    : (outcome.Success ? "Done." : (outcome.Error ?? "Tool run failed.")),
                IsQuery = string.IsNullOrWhiteSpace(outcome.Code) || outcome.IsQuery,
                ToolCallTrace = outcome.ToolsUsed.Count > 0 ? outcome.ToolsUsed : null,
                Steps = outcome.Steps,
            };
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
            return answers;
        }

        // Close any phase rows still ▶ at successful completion, then snapshot the
        // live trail into an immutable list for the final bubble (same rule as
        // ToolLoopRunner). A finished run has no genuinely-running step.
        private static List<ProgressStep> SnapshotTrail(
            System.Collections.ObjectModel.ObservableCollection<ProgressStep> trail)
        {
            ProgressReducer.MoveStepToEnd(trail, "review");
            ProgressReducer.CompleteRunning(trail);
            return new List<ProgressStep>(trail);
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
            var __swCtx = System.Diagnostics.Stopwatch.StartNew();
            var ctx = BuildContext(message);
            __swCtx.Stop();
            System.Diagnostics.Debug.WriteLine(
                $"[BinaVibe][timing] BuildContext={__swCtx.ElapsedMilliseconds}ms (UI thread) views={ctx?.Views?.Count ?? 0} levels={ctx?.Levels?.Count ?? 0}");
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;

            // Consume any screenshots pasted with this prompt (cleared so they
            // never leak into the next route).
            var images = PendingImages;
            PendingImages = null;

            // ─── HITL clarify continuation ───────────────────────────────────
            // The previous turn paused on get_user_input — THIS message is the
            // user's ANSWER, not a new command. Resume the paused run with it.
            var hitl = _pendingHitl;
            if (hitl != null)
            {
                _pendingHitl = null;
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
                        hcts.Token, onReply: t => { try { OnCodeStream?.Invoke(t); } catch { /* UI hiccup */ } }
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
                    return new RouteResult { ToolId = "ai-generated", Reply = "Stopped — if Revit was mid-operation (e.g. opening a view), it may still finish that step.", IsQuery = true };
                return ToolOutcomeToRoute(ho);
            }

            // ─── Tunnel-free tool-calling path (opt-in: BINA_VIBE_TOOL_HTTP=1) ──
            // The agent calls vetted MUTATE tools that the addin runs in real
            // Revit via /tool/generate ↔ /tool/resume. No codegen, no tunnel.
            if (ToolHttpEnabled)
            {
                var treq = new AIRequest
                {
                    Prompt = message, Context = ctx, UserId = userId, SessionId = _sessionId,
                    Images = images,
                };

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
                        onReply: t => { try { OnCodeStream?.Invoke(t); } catch { /* UI hiccup */ } }
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
                    return new RouteResult { ToolId = "ai-generated", Reply = "Stopped — if Revit was mid-operation (e.g. opening a view), it may still finish that step.", IsQuery = true };
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][timing] tool-loop total={__swRoute.ElapsedMilliseconds}ms tools={string.Join(",", outcome.ToolsUsed)} ok={outcome.Success}");
                return ToolOutcomeToRoute(outcome);
            }

            // Plan mode removed — the tool-calling agent acts directly and
            // streams (no plan card, no approve gate). Every turn goes to
            // /generate below.

            // bina-ai (Python) only exposes /agents/revit-ai/generate.
            // The legacy /route endpoint (NestJS bina-be) is not in this
            // backend — calling it gets HTTP 404 which AIService
            // synthesizes into a fake "NeedsClarification" response,
            // poisoning the chat flow. So we skip /route entirely and
            // go straight to /generate, which runs the Inspector
            // preflight (PRD §12 Step 1) against the live Revit session
            // via the WSS tunnel. Local QueryInterpreter still picks
            // vetted tools from keywords for the viewmodel's tool form
            // path — that's unrelated to this method.
            try
            {
                var req = new AIRequest
                {
                    Prompt = message,
                    Context = ctx,
                    UserId = userId,
                    SessionId = _sessionId,
                    Images = images,
                };

                // Streaming path — preferred. Chunks arrive in <1s even
                // when total codegen takes 8-12s, so the user sees the
                // chat fill in token by token instead of waiting for a
                // single big delivery. Falls back to one-shot on any
                // streaming error (server returns 404 on /stream, etc).
                if (OnCodeStream != null || OnProgress != null)
                {
                    // New CTS per request so the pane's Cancel button can abort
                    // exactly this stream. Replaces any stale one (defensive —
                    // routes are serial in practice).
                    CancellationTokenSource cts = new CancellationTokenSource();
                    lock (_cancelLock)
                    {
                        try { _streamCts?.Dispose(); } catch { }
                        _streamCts = cts;
                    }
                    try
                    {
                        AIResponse final = null;
                        var sb = new System.Text.StringBuilder();
                        var replySb = new System.Text.StringBuilder();
                        // Live step trail (BIMLogiq-style): each status/tool event
                        // is reduced into a step row and the whole trail is
                        // re-rendered (▶ running, ✓ done, ✗ error) into the
                        // thinking bubble. step_id pairs running->done onto one row.
                        var trail = new System.Collections.ObjectModel.ObservableCollection<ProgressStep>();
                        await foreach (var chunk in _ai.GenerateCodeStreamAsync(req, token, cts.Token))
                        {
                            if (chunk.Kind == StreamChunkKind.Status || chunk.Kind == StreamChunkKind.Tool)
                            {
                                // Reduce into the trail and re-render. Absent fields
                                // (un-upgraded backend) still render as a transient
                                // line via the synthesized step_id + default state.
                                ProgressReducer.Apply(trail, chunk.StepId, chunk.Phase,
                                    chunk.StatusLabel, chunk.Detail, ProgressTrail.StateFrom(chunk.State));
                                EmitProgress(ProgressTrail.Render(trail));
                            }
                            else if (chunk.Kind == StreamChunkKind.Reply)
                            {
                                // The user-facing MARKDOWN message streams in first;
                                // show it live (final markdown bubble lands at done).
                                replySb.Append(chunk.Delta);
                                try { OnCodeStream?.Invoke(replySb.ToString()); } catch { /* UI hiccup */ }
                            }
                            else if (chunk.Kind == StreamChunkKind.CodePartial)
                            {
                                // Code accumulates silently — it RUNS, it is never
                                // shown as chat text.
                                sb.Append(chunk.Delta);
                            }
                            else if (chunk.Kind == StreamChunkKind.Done)
                            {
                                final = chunk.Final;
                            }
                            else if (chunk.Kind == StreamChunkKind.Error)
                            {
                                ClearProgress();   // clear the progress card on error
                                return new RouteResult { ToolId = "ai-generated", Reply = $"Backend error: {chunk.Error}" };
                            }
                        }
                        ClearProgress();   // clear the progress card on stream completion
                        if (final != null && final.Success)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[BinaVibe][timing] backend round-trip (stream, off-UI)={__swRoute.ElapsedMilliseconds}ms codeLen={final.Code?.Length ?? 0}");
                            // Backend already split the response: `reply` = markdown
                            // message (rendered via MarkdownRenderer), `code` = C#
                            // that runs. Return even when code is empty (a pure
                            // message / clarification) so it is NOT re-run as a
                            // one-shot below.
                            var replyText = !string.IsNullOrWhiteSpace(final.Reply)
                                ? final.Reply
                                : replySb.ToString();
                            bool hasCode = !string.IsNullOrWhiteSpace(final.Code);
                            if (string.IsNullOrWhiteSpace(replyText))
                                replyText = hasCode ? "" : "Done.";
                            return new RouteResult
                            {
                                ToolId = "ai-generated",
                                Code = final.Code ?? "",
                                Reply = replyText,
                                PlanSteps = hasCode ? new List<string> { "Generated via bina-ai (streaming)" } : null,
                                IsQuery = final.IsQuery,
                                Verdict = final.ReviewerVerdict,
                                // Close any phase rows still ▶ at successful
                                // completion so the persisted trail shows all ✓.
                                Steps = SnapshotTrail(trail),
                            };
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // User hit Cancel — clear the card and report it plainly
                        // instead of degrading to a one-shot retry (which would
                        // ignore the cancel and keep the model spinning).
                        ClearProgress();
                        return new RouteResult { ToolId = "ai-generated", Reply = "Stopped." };
                    }
                    catch
                    {
                        // Fall through to one-shot below.
                        ClearProgress();
                    }
                    finally
                    {
                        lock (_cancelLock)
                        {
                            if (ReferenceEquals(_streamCts, cts)) _streamCts = null;
                        }
                        try { cts.Dispose(); } catch { }
                    }
                }

                var gen = await _ai.GenerateCodeAsync(req, token);
                if (gen != null && gen.Success)
                {
                    // Tool-calling agent path (VIBE_AGENT_MODE=tool):
                    //   - gen.Reply has the natural-language answer
                    //   - gen.Code may be empty when MUTATE tools did the
                    //     work; populated when the agent fell back to
                    //     raw C# for a visibility / crop override etc.
                    //   - gen.ToolCalls carries the tool trace
                    bool isToolMode = string.Equals(gen.AgentMode, "tool", StringComparison.OrdinalIgnoreCase);
                    string code = gen.Code ?? "";
                    string reply = !string.IsNullOrWhiteSpace(gen.Reply)
                        ? gen.Reply
                        : (gen.Explanation ?? "Generated. Review and Run when ready.");
                    var trace = gen.ToolCalls?.Select(tc => tc?.Tool).Where(t => !string.IsNullOrEmpty(t)).ToList();

                    // Return when there's code to run, a tool-mode answer, OR a
                    // reply-only response (a clarification / answer-from-context
                    // with no code — e.g. "which view did you mean?"). The last
                    // case MUST return here; otherwise rr is null, the viewmodel
                    // falls back to a catalog tool, and the user gets an empty
                    // "0 lines" proposal that fails on Run. (The streaming path
                    // already returns on empty code — keep this in parity.)
                    if (isToolMode || !string.IsNullOrWhiteSpace(code)
                        || !string.IsNullOrWhiteSpace(gen.Reply))
                    {
                        return new RouteResult
                        {
                            ToolId = "ai-generated",
                            Code = code,
                            Reply = reply,
                            PlanSteps = new List<string> { isToolMode ? "Tool-calling agent (native MCP)" : "Generated via bina-ai (Inspector-preflighted)" },
                            IsQuery = gen.IsQuery || string.IsNullOrEmpty(code),
                            ToolCallTrace = trace != null && trace.Count > 0 ? trace : null,
                            Verdict = gen.ReviewerVerdict,
                        };
                    }
                }
                if (gen != null && !gen.Success && !string.IsNullOrWhiteSpace(gen.Error))
                {
                    // Surface real backend errors to the user instead of
                    // silently degrading — empty proposal cards are
                    // worse than an explicit "backend said X".
                    return new RouteResult
                    {
                        ToolId = "ai-generated",
                        Code = "",
                        Reply = $"Backend error: {gen.Error}",
                    };
                }
            }
            catch (Exception ex)
            {
                return new RouteResult
                {
                    ToolId = "ai-generated",
                    Code = "",
                    Reply = $"Couldn't reach bina-ai: {ex.Message}",
                };
            }

            return null; // viewmodel uses its local catalog fallback
        }


        // Cap the view list so large projects don't blow up token cost / add noise.
        private const int MaxViewsInContext = 60;

        // Bound the view list: if there are more than the cap, prefer views whose
        // name shares a word with the prompt (so "open aras 01" surfaces the Aras
        // 01 views), then fill the rest up to the cap. Small projects send all.
        private static List<ViewInfo> BoundViews(List<ViewInfo> all, string prompt)
        {
            if (all == null || all.Count <= MaxViewsInContext) return all;
            var tokens = (prompt ?? "")
                .Split(new[] { ' ', '\t', '\n', ',', '.', '(', ')', '"', '\'' },
                       System.StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Select(t => t.ToLowerInvariant())
                .ToList();
            bool Matches(ViewInfo v) => tokens.Any(t =>
                (v.Name ?? "").ToLowerInvariant().Contains(t));
            var matched = all.Where(Matches).ToList();
            if (matched.Count >= MaxViewsInContext) return matched.Take(MaxViewsInContext).ToList();
            var rest = all.Where(v => !matched.Contains(v)).Take(MaxViewsInContext - matched.Count);
            return matched.Concat(rest).ToList();
        }

        private ModelContext BuildContext(string prompt = "")
        {
            var ctx = new ModelContext
            {
                Levels = new List<string>(),
                Categories = new List<string> { "Walls", "Doors", "Windows", "Floors", "Roofs", "Ceilings", "Rooms", "Furniture", "Columns" },
                Phases = new List<string>(),
                SelectedElementIds = new List<int>(),
            };

            // Set the project id that matches the snapshot namespace the
            // DocumentChangedIndexer uses for /vibe/snapshot/{tenant}/{project}.
            // BinaConfig.ProjectId is the integer project id from bina-be,
            // stored in the same config that the indexer reads at startup.
            try
            {
                var cfgForProject = BinaConfig.Load();
                if ((cfgForProject?.ProjectId ?? 0) > 0)
                    ctx.ProjectId = cfgForProject.ProjectId.ToString();
            }
            catch { /* best-effort */ }

            try
            {
                var uidoc = _getApp()?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return ctx;

                ctx.ProjectName = doc.Title;
                ctx.RevitVersion = uidoc.Application.Application.VersionNumber;
                ctx.Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
                var view = doc.ActiveView;
                if (view != null) { ctx.ActiveViewName = view.Name; ctx.ActiveViewType = view.ViewType.ToString(); }
                ctx.SelectedElementIds = uidoc.Selection.GetElementIds().Select(id => (int)id.Value).ToList();
                ctx.Phases = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>().Select(p => p.Name).ToList();

                // Real view list (id+name+type) — lets the agent resolve
                // "open Aras 01" to the exact view instead of guessing. Bounded.
                var __allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => new ViewInfo
                    {
                        Id = (int)v.Id.Value,
                        Name = v.Name,
                        ViewType = v.ViewType.ToString(),
                        OwnerView = (v as ViewPlan)?.GenLevel?.Name ?? "",
                    })
                    .ToList();
                ctx.Views = BoundViews(__allViews, prompt);
            }
            catch { /* best-effort context */ }
            return ctx;
        }
    }
}
