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

            // 1. Try vetted-action classifier. Empty token still attempts
            //    the request — dev bina-ai doesn't enforce auth on /route.
            RouteResponse routed = null;
            if (!string.IsNullOrEmpty(token))
            {
                try { routed = await _ai.RouteAsync(message, ctx, userId, _sessionId, null, token); }
                catch { routed = null; }
            }

            if (routed != null)
            {
                if (routed.NeedsClarification)
                    return new RouteResult { NeedsClarification = true, ClarifyingQuestion = routed.ClarifyingQuestion };

                string vettedCode = routed.Actions?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Code))?.Code;
                if (!string.IsNullOrWhiteSpace(vettedCode))
                {
                    var vettedPlan = routed.Actions?
                        .Where(a => !string.IsNullOrWhiteSpace(a.Description))
                        .Select(a => a.Description)
                        .ToList();
                    return new RouteResult
                    {
                        ToolId = fallbackToolId,
                        Plan = (vettedPlan != null && vettedPlan.Count > 0) ? vettedPlan : null,
                        Code = vettedCode,
                        Reply = routed.Reply,
                    };
                }
                // Route had nothing actionable → fall through to /generate.
            }

            // 2. Free-form prompt → /generate. Inspector preflight runs
            //    server-side: looks up real levels/types/selection via
            //    WSS tunnel into the customer's live Revit before
            //    codegen, so emitted C# references real names.
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
                        // Always overwrite the toolId with the neutral
                        // "ai-generated" entry so the proposal visual
                        // reflects free-form codegen, not the
                        // QueryInterpreter's category guess.
                        ToolId = "ai-generated",
                        Code = gen.Code,
                        Reply = gen.Explanation ?? "Generated. Review and Run when ready.",
                        Plan = new List<string> { "Generated via bina-ai (with Inspector preflight)" },
                    };
                }
            }
            catch { /* fall to local fallback */ }

            return null; // viewmodel handles local fallback
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
