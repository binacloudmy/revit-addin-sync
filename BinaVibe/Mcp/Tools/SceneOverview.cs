// SceneOverview — get_scene_overview, the one-call orientation READ tool.
//
// Pull-based context: the agent no longer receives a pushed ModelContext
// snapshot every turn, so its first look at the model
// would otherwise cost 4-6 tool round-trips (get_project_info + list_levels +
// list_phases + get_active_view + get_current_selection). This tool composes
// those existing Inspector implementations into ONE bounded payload — one
// Idling-pump execution — the Claude Code "one env glance" analog.
//
// Deliberately NOT included: the full view list (call list_views on demand),
// geometry/placement facts (call query_geometry), and per-element parameters
// (call get_element_parameters). Keep this tool cheap: category counts only,
// no geometry walks.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class SceneOverview
    {
        // Selection sample cap — same bound the legacy pushed SceneDigest used.
        private const int MaxSelectionSample = 40;
        // Category counts are for orientation, not audit — top N is plenty.
        private const int MaxCategoryCounts = 15;

        public static Dictionary<string, object?> Run(UIDocument uidoc, UIApplication app)
        {
            var doc = uidoc.Document;

            // Reuse the existing single-purpose inspectors so the shapes stay
            // identical to their standalone tools (no second source of truth).
            var project = Inspectors.GetProjectInfo(doc, app);
            var levels = Inspectors.ListLevels(doc)["levels"];
            var phases = Inspectors.ListPhases(doc)["phases"];
            var activeView = ActiveViewWithLevel(doc);
            var selection = SelectionSummary(uidoc);

            // Cheap counts — element-type-free collectors, no geometry.
            int viewCount = 0, sheetCount = 0;
            try
            {
                viewCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);
                sheetCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Count();
            }
            catch { /* best-effort */ }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["project"] = project,
                ["levels"] = levels,
                ["phases"] = phases,
                ["active_view"] = activeView,
                ["selection"] = selection,
                ["element_counts"] = TopCategoryCounts(doc),
                ["view_count"] = viewCount,
                ["sheet_count"] = sheetCount,
            };
        }

        // get_active_view shape + the plan level it belongs to (list_views'
        // "level" field) so "what level am I on" is answerable from this one
        // call when a plan view is active.
        private static Dictionary<string, object?> ActiveViewWithLevel(Document doc)
        {
            var view = Inspectors.GetActiveView(doc);
            try
            {
                var lvlId = (doc.ActiveView as ViewPlan)?.GenLevel?.Id;
                view["level"] = lvlId != null && lvlId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(lvlId)?.Name : null;
            }
            catch { view["level"] = null; }
            return view;
        }

        // get_current_selection shape, but count + bounded sample so a
        // 5000-element selection doesn't flood the payload.
        private static Dictionary<string, object?> SelectionSummary(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var ids = uidoc.Selection.GetElementIds();
            var sample = new List<object>();
            foreach (var id in ids.Take(MaxSelectionSample))
            {
                try
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                        ? doc.GetElement(el.GetTypeId()) : null;
                    sample.Add(new Dictionary<string, object?>
                    {
                        ["id"] = id.Value,
                        ["category"] = el.Category?.Name,
                        ["type_name"] = typeEl?.Name,
                    });
                }
                catch { /* skip this element, keep the rest */ }
            }
            return new Dictionary<string, object?>
            {
                ["count"] = ids.Count,
                ["sample"] = sample,
                ["sample_truncated"] = ids.Count > MaxSelectionSample,
            };
        }

        // analyze_model_statistics counts, truncated to the biggest categories.
        // Truncation is EXPLICIT (other_categories/other_elements) so the agent
        // never reads top-15 as the whole model.
        private static Dictionary<string, object?> TopCategoryCounts(Document doc)
        {
            try
            {
                var stats = Inspectors.AnalyzeModelStatistics(doc);
                var counts = stats["counts"] as Dictionary<string, object> ?? new Dictionary<string, object>();
                var top = counts.Take(MaxCategoryCounts)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                int otherCats = counts.Count - top.Count;
                int otherElems = counts.Skip(MaxCategoryCounts).Sum(kv => (int)kv.Value);
                return new Dictionary<string, object?>
                {
                    ["top"] = top,
                    ["total"] = stats["total"],
                    ["other_categories"] = otherCats,
                    ["other_elements"] = otherElems,
                };
            }
            catch
            {
                return new Dictionary<string, object?> { ["top"] = new Dictionary<string, object>(), ["total"] = 0 };
            }
        }
    }
}
