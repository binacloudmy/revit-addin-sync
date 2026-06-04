using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Builds @-mention picker groups from the live Revit document.</summary>
    public class RevitMentionProvider : IMentionProvider
    {
        private readonly Func<UIApplication> _getApp;
        private static readonly IMentionProvider _fallback = new StaticMentionProvider();

        public RevitMentionProvider(Func<UIApplication> getApp) => _getApp = getApp;

        public List<MentionGroup> GetGroups()
        {
            var uidoc = ResolveUiDoc();
            var doc = uidoc?.Document;
            if (doc == null)
            {
                // No document/context yet (e.g. pane auto-restored before a
                // ribbon click). Categories is a fixed enum, so still useful.
                System.Diagnostics.Debug.WriteLine(
                    "[BinaVibe][mention] no active document — categories-only fallback");
                return _fallback.GetGroups();
            }

            // Each group is collected independently: one failing query no longer
            // collapses the whole picker to categories-only (the bug where only
            // "Categories" showed). Empty groups are simply skipped by the UI.
            return new List<MentionGroup>
            {
                new MentionGroup("level", "Levels", Safe(() => Levels(doc), "levels")),
                new MentionGroup("category", "Categories",
                    new[] { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture", "Casework" }),
                new MentionGroup("room", "Rooms", Safe(() => Rooms(doc), "rooms")),
                new MentionGroup("view", "Views", Safe(() => Views(doc), "views")),
                new MentionGroup("selection", "Current selection", Safe(() => Selection(uidoc), "selection")),
            };
        }

        // Prefer the context pushed in via the ribbon command; fall back to the
        // app cached from the Idling event so the picker works on an
        // auto-restored pane too.
        private UIDocument ResolveUiDoc()
        {
            try
            {
                var app = _getApp() ?? RevitWebAppSync.App.UiApp;
                return app?.ActiveUIDocument;
            }
            catch { return null; }
        }

        private static List<string> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).Select(l => l.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

        private static List<string> Views(Document doc)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate).Select(v => v.Name).Distinct().Take(12).ToList();
            var active = doc.ActiveView?.Name;
            if (!string.IsNullOrEmpty(active)) { views.Remove(active); views.Insert(0, active); }
            return views;
        }

        // Upper bound on rooms shown so the popup stays responsive on large
        // models (the picker filters live as you type). Raise if a project has
        // more rooms than this and you need them all in one unfiltered list.
        private const int RoomCap = 500;

        // Rooms shown as "Number - Name" (e.g. "101 - Office"), or just the name
        // when a room has no number. Scoped to the active view when that view
        // contains rooms (plan/section); otherwise (3D/elevation/template) falls
        // back to the whole model so the group is never mysteriously empty.
        // Unplaced/unbounded rooms (Area <= 0) only appear in the whole-model
        // fallback, marked "(unplaced)".
        private static List<string> Rooms(Document doc)
        {
            var view = doc.ActiveView;
            if (view != null && !view.IsTemplate)
            {
                var inView = RoomNames(new FilteredElementCollector(doc, view.Id));
                if (inView.Count > 0) return inView;
            }
            return RoomNames(new FilteredElementCollector(doc));
        }

        private static List<string> RoomNames(FilteredElementCollector col) =>
            col.OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Autodesk.Revit.DB.Architecture.Room>()
                .OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase)
                .Select(RoomLabel)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(RoomCap).ToList();

        private static string RoomLabel(Autodesk.Revit.DB.Architecture.Room r)
        {
            var name = string.IsNullOrWhiteSpace(r.Number) ? r.Name : $"{r.Number} - {r.Name}";
            if (string.IsNullOrWhiteSpace(name)) return null;
            return r.Area > 0 ? name : $"{name} (unplaced)";
        }

        private static List<string> Selection(UIDocument uidoc)
        {
            var selCount = uidoc.Selection.GetElementIds().Count;
            return selCount > 0
                ? new List<string> { $"Current selection · {selCount} element{(selCount == 1 ? "" : "s")}" }
                : new List<string>();
        }

        private static List<string> Safe(Func<List<string>> query, string label)
        {
            try { return query() ?? new List<string>(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinaVibe][mention] {label} query failed: {ex.Message}");
                return new List<string>();
            }
        }
    }
}
