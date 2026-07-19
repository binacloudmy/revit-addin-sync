using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Idling-built snapshot of everything BuildContext needs from the
    /// Revit API. BuildContext used to run collectors + Selection reads + the
    /// PlacementFacts scene digest synchronously on the WPF UI thread at Send —
    /// not a valid Revit API context. That was the second half of the round-29
    /// CIDB crash repro (Floor picked via Schedule > Highlight in Model, then
    /// Send → "unrecoverable error"). The UI thread now only reads this
    /// snapshot; refresh happens exclusively inside the Idling event.</summary>
    public static class CopilotContextSnapshot
    {
        public sealed class Snap
        {
            public string ProjectName;
            public string RevitVersion;
            public List<string> Levels = new();
            public List<string> Phases = new();
            public string ActiveViewName;
            public string ActiveViewType;
            public List<int> SelectedElementIds = new();
            public List<Dictionary<string, object>> SceneDigest;
            public List<ViewInfo> AllViews = new();
        }

        private static volatile Snap _snap;
        private static long _lastHeavyTs;
        private static string _lastSelKey = "";

        public static Snap Current => _snap;

        /// <summary>Called from App.cs Idling (valid API context). Selection and
        /// active view refresh on every idle (cheap, must be fresh at Send);
        /// collectors and the scene digest are throttled / selection-driven.</summary>
        public static void Refresh(UIApplication app)
        {
            try
            {
                var uidoc = app?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) { _snap = null; return; }

                var snap = _snap;
                bool fresh = snap == null;
                if (fresh) snap = new Snap();

                var selIds = uidoc.Selection.GetElementIds().Select(id => (int)id.Value).ToList();
                var selKey = string.Join(",", selIds);
                bool selChanged = selKey != _lastSelKey;

                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                double freq = System.Diagnostics.Stopwatch.Frequency;
                bool heavyDue = fresh || _lastHeavyTs == 0 || (now - _lastHeavyTs) / freq >= 2.0;

                var next = new Snap
                {
                    ProjectName = doc.Title,
                    RevitVersion = uidoc.Application.Application.VersionNumber,
                    ActiveViewName = doc.ActiveView?.Name,
                    ActiveViewType = doc.ActiveView?.ViewType.ToString(),
                    SelectedElementIds = selIds,
                    Levels = snap.Levels,
                    Phases = snap.Phases,
                    AllViews = snap.AllViews,
                    SceneDigest = snap.SceneDigest,
                };

                if (heavyDue)
                {
                    _lastHeavyTs = now;
                    next.Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
                    next.Phases = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>()
                        .Select(p => p.Name).ToList();
                    next.AllViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate)
                        .Select(v => new ViewInfo
                        {
                            Id = (int)v.Id.Value,
                            Name = v.Name,
                            ViewType = v.ViewType.ToString(),
                            OwnerView = (v as ViewPlan)?.GenLevel?.Name ?? "",
                        })
                        .ToList();
                }

                if (selChanged)
                {
                    _lastSelKey = selKey;
                    // Scene digest: placement facts for the working set. Cap 40;
                    // best-effort per element. Selection-driven so a Send right
                    // after selecting still gets a digest matching the ids.
                    var digest = new List<Dictionary<string, object>>();
                    foreach (var selId in uidoc.Selection.GetElementIds().Take(40))
                    {
                        try
                        {
                            var selEl = doc.GetElement(selId);
                            if (selEl == null) continue;
                            var facts = BinaVibe.Mcp.Tools.QueryGeometry.PlacementFacts(doc, selEl);
                            digest.Add(new Dictionary<string, object>
                            {
                                ["id"] = (int)selId.Value,
                                ["xyz"] = facts.TryGetValue("xyz", out var xyz) ? xyz : null,
                                ["facing"] = facts.TryGetValue("facing", out var fac) ? fac : null,
                                ["room"] = facts.TryGetValue("room", out var rm) ? rm : null,
                                ["hostId"] = facts.TryGetValue("host_id", out var h) ? h : null,
                            });
                        }
                        catch { /* skip this element, keep the rest */ }
                    }
                    next.SceneDigest = digest.Count > 0 ? digest : null;
                }

                _snap = next;
            }
            catch
            {
                // never propagate into Idling; keep the previous snapshot
            }
        }
    }
}
