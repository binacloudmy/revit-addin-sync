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
            try
            {
                var uidoc = _getApp()?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return _fallback.GetGroups();

                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).Select(l => l.Name).ToList();

                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate).Select(v => v.Name).Distinct().Take(12).ToList();
                var active = doc.ActiveView?.Name;
                if (!string.IsNullOrEmpty(active)) { views.Remove(active); views.Insert(0, active); }

                var selCount = uidoc.Selection.GetElementIds().Count;
                var selection = selCount > 0
                    ? new List<string> { $"Current selection · {selCount} element{(selCount == 1 ? "" : "s")}" }
                    : new List<string>();

                return new List<MentionGroup>
                {
                    new MentionGroup("level", "Levels", levels),
                    new MentionGroup("category", "Categories", new[] { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture", "Casework" }),
                    new MentionGroup("view", "Views", views),
                    new MentionGroup("selection", "Current selection", selection),
                };
            }
            catch
            {
                return _fallback.GetGroups();
            }
        }
    }
}
