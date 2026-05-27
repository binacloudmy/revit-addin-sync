using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly string _sessionId = Guid.NewGuid().ToString();

        public RevitChatRouter(Func<UIApplication> getApp)
        {
            _getApp = getApp;
            _ai = new AIService(BinaConfig.Load().ResolvedAIBaseUrl);
        }

        /// <summary>Optional callback invoked on every streamed code chunk
        /// from /generate/stream so the chat can render code as it arrives.
        /// Receives the cumulative code string so the UI can replace, not
        /// append. Set null to disable streaming (falls back to one-shot).</summary>
        public Action<string> OnCodeStream { get; set; }

        public async Task<RouteResult> RouteAsync(string message, string fallbackToolId)
        {
            var cfg = BinaConfig.Load();
            var token = cfg?.AccessToken ?? "";
            var ctx = BuildContext();
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;

            // Plan mode: when BINA_VIBE_CHAT_MODE=plan, get a structured Plan
            // from /agents/revit-ai/plan and let the chat render a Plan card. User
            // clicks Approve → addin calls /execute-plan with the same Plan.
            var useVibeV2Mode = System.Environment.GetEnvironmentVariable("BINA_VIBE_CHAT_MODE") ?? "plan";  // plan | tool
            if (string.Equals(useVibeV2Mode, "plan", System.StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var planReq = new AIRequest
                    {
                        Prompt = message,
                        Context = ctx,
                        UserId = userId,
                        SessionId = _sessionId,
                    };
                    var planResp = await _ai.GetPlanAsync(planReq, token);
                    if (planResp != null && planResp.Success && planResp.Plan != null && planResp.Plan.Steps != null && planResp.Plan.Steps.Count > 0)
                    {
                        return new RouteResult
                        {
                            ToolId = "ai-generated",
                            IsPlan = true,
                            Plan = planResp.Plan,
                            PlanId = planResp.PlanId,
                            Reply = planResp.IntentSummary ?? planResp.Plan.Intent,
                        };
                    }
                    if (planResp != null && !planResp.Success)
                    {
                        return new RouteResult
                        {
                            ToolId = "ai-generated",
                            Reply = "Planner error: " + (planResp.Error ?? "unknown"),
                        };
                    }
                    // Plan came back empty — fall through to legacy /generate.
                }
                catch (System.Exception ex)
                {
                    // Fall through to /generate; log the failure for diagnosis.
                    System.Diagnostics.Debug.WriteLine($"[BINA] /plan failed, falling back to /generate: {ex.Message}");
                }
            }

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
                };

                // Streaming path — preferred. Chunks arrive in <1s even
                // when total codegen takes 8-12s, so the user sees the
                // chat fill in token by token instead of waiting for a
                // single big delivery. Falls back to one-shot on any
                // streaming error (server returns 404 on /stream, etc).
                if (OnCodeStream != null)
                {
                    try
                    {
                        AIResponse final = null;
                        var sb = new System.Text.StringBuilder();
                        await foreach (var chunk in _ai.GenerateCodeStreamAsync(req, token))
                        {
                            if (chunk.Kind == StreamChunkKind.CodePartial)
                            {
                                sb.Append(chunk.Delta);
                                try { OnCodeStream(sb.ToString()); } catch { /* UI hiccup */ }
                            }
                            else if (chunk.Kind == StreamChunkKind.Done)
                            {
                                final = chunk.Final;
                            }
                            else if (chunk.Kind == StreamChunkKind.Error)
                            {
                                return new RouteResult { ToolId = "ai-generated", Reply = $"Backend error: {chunk.Error}" };
                            }
                        }
                        if (final != null && final.Success && !string.IsNullOrWhiteSpace(final.Code))
                        {
                            return new RouteResult
                            {
                                ToolId = "ai-generated",
                                Code = final.Code,
                                Reply = final.Explanation ?? "Generated. Review and Run when ready.",
                                PlanSteps = new List<string> { "Generated via bina-ai (streaming, Inspector-preflighted)" },
                                IsQuery = final.IsQuery,
                                Verdict = final.ReviewerVerdict,
                            };
                        }
                    }
                    catch
                    {
                        // Fall through to one-shot below.
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

                    if (isToolMode || !string.IsNullOrWhiteSpace(code))
                    {
                        return new RouteResult
                        {
                            ToolId = "ai-generated",
                            Code = code,
                            Reply = reply,
                            PlanSteps = new List<string> { isToolMode ? "Tool-calling agent (native MCP)" : "Generated via bina-ai (Inspector-preflighted)" },
                            IsQuery = gen.IsQuery || (isToolMode && string.IsNullOrEmpty(code)),
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

        /// <summary>Called by CopilotViewModel.ApprovePlan and
        /// ApproveGate. Sends the Plan back to /execute-plan with the
        /// running list of approved gate_ids. Returns the agent
        /// response (which may carry more pending_approvals).</summary>
        public async Task<AIResponse> ExecutePlanAsync(
            RevitWebAppSync.Models.PlanModel plan,
            string planId,
            System.Collections.Generic.IEnumerable<string> approvalTokens = null)
        {
            var cfg = BinaConfig.Load();
            var token = cfg?.AccessToken ?? "";
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;
            return await _ai.ExecutePlanAsync(
                prompt: plan?.Intent ?? "",
                plan: plan,
                planId: planId,
                context: BuildContext(),
                userId: userId,
                sessionId: _sessionId,
                accessToken: token,
                approvalTokens: approvalTokens);
        }

        private ModelContext BuildContext()
        {
            var ctx = new ModelContext
            {
                Levels = new List<string>(),
                Categories = new List<string> { "Walls", "Doors", "Windows", "Floors", "Roofs", "Ceilings", "Rooms", "Furniture", "Columns" },
                Phases = new List<string>(),
                SelectedElementIds = new List<int>(),
            };
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
            }
            catch { /* best-effort context */ }
            return ctx;
        }
    }
}
