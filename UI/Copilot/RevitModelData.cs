using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        // Prefer the context pushed in via the ribbon command; fall back to the
        // app cached from the Idling event so the tool forms still read the live
        // model when the pane was auto-restored on startup (no ribbon click) and
        // _uiApp was never set — which left dropdowns on the static placeholders.
        private Document Doc()
        {
            try { return (_getApp() ?? RevitWebAppSync.App.UiApp)?.ActiveUIDocument?.Document; }
            catch { return null; }
        }

        public List<string> Views(string viewType)
        {
            try
            {
                var doc = Doc();
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

        public List<string> MatchViews(string query)
        {
            try
            {
                var doc = Doc();
                if (doc == null || string.IsNullOrWhiteSpace(query)) return new List<string>();

                var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v != null && !v.IsTemplate && !string.IsNullOrEmpty(v.Name))
                    .Select(v => v.Name).Distinct().ToList();

                var q = query.Trim();
                // Precise name typed (e.g. picked from the clarify list) → open it.
                var exact = names.FirstOrDefault(n => string.Equals(n, q, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return new List<string> { exact };

                var tokens = q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return names.Where(n => tokens.All(t => TokenMatches(n, t)))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        // A number token matches the same number with/without leading zeros as a
        // whole run ("1" → "Aras 01" but not "Aras 11"); a word token is a plain
        // case-insensitive substring.
        private static bool TokenMatches(string name, string token)
        {
            if (token.All(char.IsDigit))
            {
                var bare = token.TrimStart('0');
                if (bare.Length == 0) bare = "0";
                return Regex.IsMatch(name, $@"\b0*{bare}\b");
            }
            return name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public List<string> Schedules()
        {
            try
            {
                var doc = Doc();
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
