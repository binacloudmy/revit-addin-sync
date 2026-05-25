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
    /// PRD revit_copilot_v2: chat routing runs LOCALLY (QueryInterpreter regex).
    /// Vetted recipes synthesize C# in-process via RevitCopilotExecutor.SynthForChat;
    /// only the NeedsAI path crosses the network, hitting bina-ai's codegen endpoint
    /// (/agents/revit-ai/generate). The old /agents/revit-ai/route call is gone — backend
    /// no longer routes.
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
            // 1. Local routing — decide vetted vs codegen without touching the network.
            var decision = QueryInterpreter.Decide(message, fallbackToolId);

            if (decision.Kind == RouteResultKind.VettedTool)
            {
                // Synthesize C# from the bound params via the same type-aware synthesizer the
                // forms use. Runs offline; no codegen tokens spent.
                decision.Code = RevitCopilotExecutor.SynthForChat(decision.ToolName, decision.ToolParams);
                if (string.IsNullOrWhiteSpace(decision.Code))
                {
                    // Synth refused (e.g. missing required param) — fall through to AI rather
                    // than ship empty code to the user.
                    decision = RouteResult.NeedsAI(message, fallbackToolId);
                }
                else
                {
                    return decision;
                }
            }

            // 2. NeedsAI — call bina-ai /generate.
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
