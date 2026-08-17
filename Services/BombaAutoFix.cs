using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Bomba autofix v1: write the required Fire Rating onto unrated /
    /// under-rated element types (the fire_resistance check's two failure
    /// classes — the only bomba findings that are a parameter write rather
    /// than modelling work). One transaction = one undo. Runs through an
    /// ExternalEvent because writes need API context (BombaPickStore
    /// pattern).
    /// </summary>
    public class BombaAutoFixHandler : IExternalEventHandler
    {
        public class FixRequest
        {
            public int RequiredMinutes = 120;
        }

        public FixRequest Pending;
        /// Invoked with the number of types changed (API context — marshal
        /// to the UI thread before touching the pane).
        public Action<int> Completed;

        private static readonly Dictionary<BuiltInCategory, string> _classes =
            new Dictionary<BuiltInCategory, string>
            {
                { BuiltInCategory.OST_Walls, "wall" },
                { BuiltInCategory.OST_Floors, "floor" },
                { BuiltInCategory.OST_StructuralColumns, "column" },
                { BuiltInCategory.OST_StructuralFraming, "framing" },
            };

        public void Execute(UIApplication app)
        {
            var req = Pending;
            Pending = null;
            var done = Completed;
            Completed = null;
            var doc = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            if (req == null || doc == null) return;

            int changed = 0;
            try
            {
                var label = req.RequiredMinutes % 60 == 0
                    ? (req.RequiredMinutes / 60) + " hr"
                    : req.RequiredMinutes + " min";

                using (var tx = new Transaction(doc, "BINA: set fire ratings (" + label + ")"))
                {
                    tx.Start();
                    foreach (var pair in _classes)
                    {
                        var typeIds = new HashSet<ElementId>();
                        foreach (var el in new FilteredElementCollector(doc)
                            .OfCategory(pair.Key).WhereElementIsNotElementType())
                        {
                            var tid = el.GetTypeId();
                            if (tid != ElementId.InvalidElementId) typeIds.Add(tid);
                        }
                        foreach (var tid in typeIds)
                        {
                            var typ = doc.GetElement(tid) as ElementType;
                            if (typ == null) continue;
                            var p = typ.get_Parameter(BuiltInParameter.FIRE_RATING);
                            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                                continue;
                            var minutes = ParseMinutes(p.AsString());
                            if (minutes.HasValue && minutes.Value >= req.RequiredMinutes)
                                continue; // already compliant — never touch it
                            if (p.Set(label)) changed++;
                        }
                    }
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] bomba autofix failed: " + ex.Message);
            }
            if (done != null)
            {
                try { done(changed); } catch { }
            }
        }

        /// Mirrors the engine's parse_rating_minutes (fire_resistance.py):
        /// "2 hr"/"2HR"/"2 jam" → 120; "120"/"120 min" → 120; bare ≤8 reads
        /// as hours; unparseable → null.
        internal static int? ParseMinutes(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().ToLowerInvariant();
            if (t == "-" || t == "n/a" || t == "na" || t == "none") return null;
            var m = Regex.Match(t, @"^(\d+(?:\.\d+)?)\s*(h|hr|hrs|hour|hours|jam)\b");
            if (m.Success)
                return (int)Math.Round(double.Parse(m.Groups[1].Value) * 60);
            m = Regex.Match(t, @"^(\d+)\s*(min|mins|minute|minutes|minit)?\s*$");
            if (m.Success)
            {
                var value = int.Parse(m.Groups[1].Value);
                return value <= 8 && !m.Groups[2].Success ? value * 60 : value;
            }
            return null;
        }

        public string GetName() { return "BINA Bomba autofix"; }
    }
}
