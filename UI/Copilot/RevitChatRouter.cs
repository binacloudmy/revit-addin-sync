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
    /// Revit-aware chat router. Calls AIService.RouteAsync with live ModelContext + the stored
    /// login token. Returns null on any failure (not logged in, backend unreachable) so the
    /// viewmodel falls back to the deterministic QueryInterpreter proposal.
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
            if (string.IsNullOrEmpty(cfg?.AccessToken))
                return new RouteResult { NotAuthenticated = true }; // not logged in — tell the user to sign in

            var ctx = BuildContext();
            var resp = await _ai.RouteAsync(
                message, ctx,
                cfg.UserId > 0 ? cfg.UserId : (int?)null,
                _sessionId, null, cfg.AccessToken);

            if (resp == null) return null;
            if (resp.NeedsClarification)
                return new RouteResult { NeedsClarification = true, ClarifyingQuestion = resp.ClarifyingQuestion };

            // Prefer generated code; but if the first action is a VETTED tool (params, no code),
            // synthesize it via the type-aware synthesizer so chat open_view/select/etc. behave
            // exactly like the forms (e.g. "open 3d view" actually opens a 3D view).
            var first = resp.Actions?.FirstOrDefault();
            string code = first?.Code;
            if (string.IsNullOrWhiteSpace(code) && first != null
                && !string.IsNullOrEmpty(first.Tool) && first.Tool != "code")
            {
                code = RevitCopilotExecutor.SynthForChat(first.Tool, first.Params);
            }
            if (string.IsNullOrWhiteSpace(code))
                code = resp.Actions?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Code))?.Code;

            var plan = resp.Actions?
                .Where(a => !string.IsNullOrWhiteSpace(a.Description))
                .Select(a => a.Description)
                .ToList();

            return new RouteResult
            {
                Intent = resp.Intent,                          // real intent → proposal title
                ToolId = fallbackToolId,                       // catalog id (icon + execution/history only)
                Plan = (plan != null && plan.Count > 0) ? plan : null,
                Code = code,
                Reply = resp.Reply,
            };
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
