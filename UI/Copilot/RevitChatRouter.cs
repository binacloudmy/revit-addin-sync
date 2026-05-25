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
    /// Chat tab always routes to bina-ai /generate. Vetted recipes are reserved for the
    /// Library tab; the chat path skips QueryInterpreter.Decide so qualifiers like
    /// "wider than 1000mm" or "in red" aren't swallowed by greedy regexes.
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

        public async Task<RouteResult> RouteAsync(string message, string fallbackToolId, CancellationToken ct = default)
        {
            var cfg = BinaConfig.Load();
            if (string.IsNullOrEmpty(cfg?.AccessToken))
                return RouteResult.NotAuthed();

            var req = new AIRequest
            {
                Prompt = message,
                Context = BuildContext(),
                UserId = cfg.UserId > 0 ? cfg.UserId : (int?)null,
                SessionId = _sessionId,
            };

            AIResponse resp;
            try { resp = await _ai.GenerateCodeAsync(req, cfg.AccessToken, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return RouteResult.Failed(ex.Message); }

            if (resp == null || resp.Success == false)
                return RouteResult.Failed(resp?.Error);

            return new RouteResult
            {
                Kind = RouteResultKind.NeedsAI,
                ToolId = fallbackToolId,
                Code = resp.Code,
                Reply = resp.Explanation,
                Intent = HumanizeIntent(resp.Intent),
            };
        }

        // ── helpers ─────────────────────────────────────────────────────
        private static string HumanizeIntent(string raw)
        {
            // Backend ships snake_case labels (e.g. "create_view_from_view"); the
            // chat card displays them as title-case ("Create view from view").
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw.Replace('_', ' ').Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join(" ", parts);
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
