using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Builds @-mention picker groups from the live Revit document.
    ///
    /// The picker fires on EVERY keystroke inside an "@" token, on the WPF UI
    /// thread of a modeless pane — which is NOT a valid Revit API context.
    /// Collector/Selection calls from there mostly work by accident and then
    /// hard-crash Revit ("unrecoverable error") under fragile states; the
    /// round-29 CIDB repro was: Floor selected via Schedule > Highlight in
    /// Model, then typing an @-mention — Revit died mid-keystroke, twice.
    /// So the UI thread only ever reads a cached snapshot here, and the cache
    /// is refreshed from the Idling event (a valid API context) in App.cs.</summary>
    public class RevitMentionProvider : IMentionProvider
    {
        private readonly Func<UIApplication> _getApp;
        private static readonly IMentionProvider _fallback = new StaticMentionProvider();

        private static volatile List<MentionGroup> _cache;
        private static long _lastRefreshTs;

        public RevitMentionProvider(Func<UIApplication> getApp) => _getApp = getApp;

        /// <summary>UI-thread safe: returns the last Idling-built snapshot.
        /// Never touches the Revit API.</summary>
        public List<MentionGroup> GetGroups()
        {
            var c = _cache;
            return c != null && c.Count > 0 ? c : _fallback.GetGroups();
        }

        /// <summary>Called from the Idling handler (valid Revit API context).
        /// Throttled; swallows everything — a mention picker must never be
        /// able to take Revit down.</summary>
        public static void RefreshCache(UIApplication app)
        {
            try
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                double freq = System.Diagnostics.Stopwatch.Frequency;
                if (_lastRefreshTs != 0 && (now - _lastRefreshTs) / freq < 2.0) return;
                _lastRefreshTs = now;

                var uidoc = app?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) { _cache = null; return; }

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

                _cache = new List<MentionGroup>
                {
                    new MentionGroup("level", "Levels", levels),
                    new MentionGroup("category", "Categories", new[] { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture", "Casework" }),
                    new MentionGroup("view", "Views", views),
                    new MentionGroup("selection", "Current selection", selection),
                };
            }
            catch
            {
                // keep the previous snapshot; never propagate into Idling
            }
        }
    }
}
