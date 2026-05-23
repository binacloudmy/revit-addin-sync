using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Live model data for vetted-tool forms, read from the active Revit document.</summary>
    public class RevitModelData : IModelData
    {
        private readonly Func<UIApplication> _getApp;

        public RevitModelData(Func<UIApplication> getApp) => _getApp = getApp;

        public List<string> Views(string viewType)
        {
            try
            {
                var doc = _getApp()?.ActiveUIDocument?.Document;
                if (doc == null) return new List<string>();

                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v != null && !v.IsTemplate);

                var t = (viewType ?? "").ToLowerInvariant();
                IEnumerable<View> filtered;
                if (t.Contains("3d")) filtered = views.Where(v => v is View3D);
                else if (t.Contains("section")) filtered = views.Where(v => v.ViewType == ViewType.Section);
                else if (t.Contains("elevation")) filtered = views.Where(v => v.ViewType == ViewType.Elevation);
                else if (t.Contains("drafting")) filtered = views.Where(v => v.ViewType == ViewType.DraftingView);
                else if (t.Contains("floor") || t.Contains("plan"))
                    filtered = views.Where(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.AreaPlan);
                else
                    // No specific type → all graphical views, but never schedules (own tool).
                    filtered = views.Where(v => v.ViewType != ViewType.Schedule
                        && v.ViewType != ViewType.ColumnSchedule && v.ViewType != ViewType.PanelSchedule);

                return filtered.Select(v => v.Name).Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().OrderBy(n => n).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public List<string> Schedules()
        {
            try
            {
                var doc = _getApp()?.ActiveUIDocument?.Document;
                if (doc == null) return new List<string>();
                return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => s != null && !s.IsTemplate)
                    .Select(s => s.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().OrderBy(n => n).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
