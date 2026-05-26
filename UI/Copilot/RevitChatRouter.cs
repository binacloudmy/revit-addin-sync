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

        public async Task<RouteResult> RouteAsync(string message, string fallbackToolId)
        {
            var cfg = BinaConfig.Load();
            var token = cfg?.AccessToken ?? "";
            var ctx = BuildContext();
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;

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
                var gen = await _ai.GenerateCodeAsync(req, token);
                if (gen != null && gen.Success && !string.IsNullOrWhiteSpace(gen.Code))
                {
                    return new RouteResult
                    {
                        ToolId = "ai-generated",
                        Code = gen.Code,
                        Reply = gen.Explanation ?? "Generated. Review and Run when ready.",
                        Plan = new List<string> { "Generated via bina-ai (Inspector-preflighted against live model)" },
                        IsQuery = gen.IsQuery,
                    };
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
