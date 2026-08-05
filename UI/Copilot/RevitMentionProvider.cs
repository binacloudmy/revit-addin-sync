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
    /// Families as Category → Family → Type. Called once per picker open
    /// (MentionInput caches while the popup is up), so a full-model walk is
    /// acceptable even on large models — only names are materialized.
    /// </summary>
    public class RevitMentionProvider : IMentionProvider
    {
        private readonly Func<UIApplication> _getApp;
        private static readonly IMentionProvider _fallback = new StaticMentionProvider();

        public RevitMentionProvider(Func<UIApplication> getApp) => _getApp = getApp;

        public List<MentionNode> GetTree()
        {
            try
            {
                var uidoc = _getApp()?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return _fallback.GetTree();

                var roots = new List<MentionNode>();

                var selCount = uidoc.Selection.GetElementIds().Count;
                if (selCount > 0)
                    roots.Add(MentionNode.Leaf("selection", $"Current selection · {selCount} element{(selCount == 1 ? "" : "s")}"));

                var activeName = doc.ActiveView?.Name;
                if (!string.IsNullOrEmpty(activeName))
                    roots.Add(MentionNode.Leaf("view", activeName));

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
                                .Select(n => MentionNode.Leaf("view", n))))));

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
                roots.Add(MentionNode.Group("Families",
                    types.GroupBy(t => t.Category.Name).OrderBy(g => g.Key)
                        .Select(g => new MentionNode("category", g.Key,
                            g.GroupBy(t => t.FamilyName).OrderBy(f => f.Key)
                                .Select(f => new MentionNode("family", f.Key,
                                    f.Select(t => t.Name).Distinct().OrderBy(n => n)
                                        .Select(n => MentionNode.Leaf("type", n)),
                                    pickable: true)),
                            pickable: true))));

                roots.RemoveAll(r => !r.Pickable && r.Children.Count == 0);
                return roots;
            }
            catch
            {
                return _fallback.GetTree();
            }
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
