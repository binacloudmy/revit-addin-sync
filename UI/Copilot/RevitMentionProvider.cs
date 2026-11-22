using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Builds the @-mention tree from the live Revit document, mirroring the
    /// Project Browser: Levels, Views (grouped by view type), Sheets, and
    /// Families as Category → Family → Type. Called once per picker open on
    /// Revit's thread (the pane's own; MentionInput caches while the popup is up).
    /// Reads only: no transaction, no model change.
    ///
    /// Each type carries its family, category, Type Mark and how many instances
    /// the model and the active view hold. The counts are one pass over the
    /// model's instances of the listed categories under a time budget
    /// (<see cref="ModelCountBudgetMs"/>, <see cref="ViewCountBudgetMs"/>); a pass
    /// that runs out is thrown away (no partial, wrong numbers) and the document
    /// is not counted again while it stays open, so a huge model pays the budget
    /// once, not on every "@".
    /// </summary>
    public class RevitMentionProvider : IMentionProvider, IMentionSelectionSource
    {
        private readonly Func<UIApplication> _getApp;
        private static readonly IMentionProvider _fallback = new StaticMentionProvider();

        public const int ModelCountBudgetMs = 250;
        public const int ViewCountBudgetMs = 120;
        // Documents whose model-wide count ran past its budget (by path, else title).
        private static readonly HashSet<string> _tooBigToCount = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _viewTooBigToCount = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public RevitMentionProvider(Func<UIApplication> getApp) => _getApp = getApp;

        public List<MentionNode> GetTree()
        {
            try
            {
                var uidoc = _getApp()?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return _fallback.GetTree();

                var roots = new List<MentionNode>();

                var sel = ReadSelection(uidoc);
                if (sel != null && sel.Total > 0)
                    roots.Add(new MentionNode("selection", "Current selection · " + MentionText.SelectionLabel(sel)) { Selection = sel });

                var active = doc.ActiveView;
                var activeName = active?.Name;
                if (!string.IsNullOrEmpty(activeName))
                    roots.Add(new MentionNode("view", activeName) { IsActive = true });

                roots.Add(MentionNode.Group("Levels",
                    new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .Select(l => MentionNode.Leaf("level", l.Name))));

                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && !(v is ViewSheet)).ToList();
                roots.Add(MentionNode.Group("Views",
                    views.GroupBy(v => ViewTypeLabel(v.ViewType)).OrderBy(g => g.Key)
                        .Select(g => MentionNode.Group(g.Key,
                            g.Select(v => v.Name).Distinct().OrderBy(n => n)
                                .Select(n => new MentionNode("view", n) { IsActive = n == activeName })))));

                roots.Add(MentionNode.Group("Sheets",
                    new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .OrderBy(s => s.SheetNumber)
                        .Select(s => MentionNode.Leaf("sheet", $"{s.SheetNumber} - {s.Name}"))));

                var types = new FilteredElementCollector(doc).WhereElementIsElementType()
                    .Cast<ElementType>()
                    .Where(t => t.Category != null
                                && t.Category.CategoryType == CategoryType.Model
                                && !string.IsNullOrEmpty(t.FamilyName))
                    .ToList();

                var catIds = types.Select(t => t.Category.Id).Distinct().ToList();
                string docKey = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
                var inModel = CountByType(doc, null, catIds, docKey, _tooBigToCount, ModelCountBudgetMs);
                var inView = active != null && IsCountableView(active)
                    ? CountByType(doc, active, catIds, docKey + "|" + active.Id, _viewTooBigToCount, ViewCountBudgetMs)
                    : null;

                roots.Add(MentionNode.Group("Families",
                    types.GroupBy(t => t.Category.Name).OrderBy(g => g.Key)
                        .Select(g =>
                        {
                            string catKey = CategoryKey(g.First().Category);
                            return new MentionNode("category", g.Key,
                                g.GroupBy(t => t.FamilyName).OrderBy(f => f.Key)
                                    .Select(f => new MentionNode("family", f.Key,
                                        f.GroupBy(t => t.Name).OrderBy(n => n.Key)
                                            .Select(n => TypeNode(n.Key, f.Key, g.Key, catKey, n.ToList(), inModel, inView)),
                                        pickable: true) { Category = g.Key, CategoryKey = catKey }),
                                pickable: true) { CategoryKey = catKey };
                        })));

                roots.RemoveAll(r => !r.Pickable && r.Children.Count == 0);
                return roots;
            }
            catch
            {
                return _fallback.GetTree();
            }
        }

        // One node per (family, type name); a name Revit holds twice in one
        // family (rare) sums its counts.
        private static MentionNode TypeNode(string name, string family, string category, string catKey,
                                            List<ElementType> same, Dictionary<ElementId, int> inModel, Dictionary<ElementId, int> inView)
        {
            string mark = null;
            try { mark = same[0].get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString(); }
            catch { /* no Type Mark on this type */ }
            return new MentionNode("type", name)
            {
                Family = family, Category = category, CategoryKey = catKey,
                TypeMark = string.IsNullOrWhiteSpace(mark) ? null : mark.Trim(),
                Count = inModel == null ? (int?)null : same.Sum(t => inModel.TryGetValue(t.Id, out var c) ? c : 0),
                InView = inView == null ? (int?)null : same.Sum(t => inView.TryGetValue(t.Id, out var c) ? c : 0),
            };
        }

        private static string CategoryKey(Category c)
        {
            try { return c.BuiltInCategory == BuiltInCategory.INVALID ? null : c.BuiltInCategory.ToString(); }
            catch { return null; }
        }

        private static bool IsCountableView(View v) =>
            !v.IsTemplate && !(v is ViewSheet) && !(v is ViewSchedule)
            && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser
            && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined;

        /// <summary>
        /// Instances per type id among the given categories, model-wide (view null)
        /// or in a view. Null when the pass ran past <paramref name="budgetMs"/>
        /// (the key then goes into <paramref name="tooBig"/> so later openings skip
        /// it) or failed. The time check runs every 512 elements.
        /// </summary>
        private static Dictionary<ElementId, int> CountByType(Document doc, View view, ICollection<ElementId> catIds,
                                                              string key, HashSet<string> tooBig, int budgetMs)
        {
            if (catIds == null || catIds.Count == 0 || tooBig.Contains(key)) return null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var collector = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
                collector.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(catIds));
                var counts = new Dictionary<ElementId, int>();
                int n = 0;
                foreach (Element e in collector)
                {
                    var tid = e?.GetTypeId();
                    if (tid != null && tid != ElementId.InvalidElementId)
                        counts[tid] = counts.TryGetValue(tid, out var c) ? c + 1 : 1;
                    if ((++n & 511) == 0 && sw.ElapsedMilliseconds > budgetMs)
                    {
                        tooBig.Add(key);
                        return null;
                    }
                }
                if (sw.ElapsedMilliseconds > budgetMs) tooBig.Add(key);   // done, but next time skip
                return counts;
            }
            catch
            {
                return null;
            }
        }

        public MentionSelection ReadSelection()
        {
            try { return ReadSelection(_getApp()?.ActiveUIDocument); }
            catch { return null; }
        }

        /// <summary>The selection grouped by category, family and type. Elements
        /// without a type (rooms, areas) group by category only.</summary>
        private static MentionSelection ReadSelection(UIDocument uidoc)
        {
            var doc = uidoc?.Document;
            if (doc == null) return null;
            var groups = new Dictionary<string, MentionSelectionGroup>(StringComparer.Ordinal);
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                string cat = e.Category?.Name ?? "";
                string fam = "", type = "";
                var tid = e.GetTypeId();
                if (tid != null && tid != ElementId.InvalidElementId && doc.GetElement(tid) is ElementType et)
                {
                    fam = et.FamilyName ?? "";
                    type = et.Name ?? "";
                }
                string k = cat + "\u001f" + fam + "\u001f" + type;
                if (!groups.TryGetValue(k, out var g)) groups[k] = g = new MentionSelectionGroup(cat, fam, type, 0);
                g.Count++;
            }
            return new MentionSelection(groups.Values);
        }

        private static string ViewTypeLabel(ViewType t)
        {
            switch (t)
            {
                case ViewType.FloorPlan: return "Floor Plans";
                case ViewType.CeilingPlan: return "Ceiling Plans";
                case ViewType.ThreeD: return "3D Views";
                case ViewType.Elevation: return "Elevations";
                case ViewType.Section: return "Sections";
                case ViewType.Detail: return "Detail Views";
                case ViewType.DraftingView: return "Drafting Views";
                case ViewType.Legend: return "Legends";
                case ViewType.Schedule: return "Schedules";
                case ViewType.AreaPlan: return "Area Plans";
                case ViewType.EngineeringPlan: return "Structural Plans";
                default: return "Other Views";
            }
        }
    }
}
