using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Guard;
using RevitWebAppSync.Services;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>AI-330: reads (on Revit's thread, inside a write run) the type of every pipe, duct,
    /// cable tray and conduit and the family of every MEP fitting before the call runs, then says which
    /// fittings the call changed when it re-typed those curves. Wording and decision: FittingChangeNote.</summary>
    internal sealed class FittingWatch
    {
        private static readonly BuiltInCategory[] CurveCats =
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexDuctCurves,
        };

        private static readonly BuiltInCategory[] FittingCats =
        {
            BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting,
        };

        private readonly Dictionary<long, long> _curveType = new();
        private readonly Dictionary<long, string> _fittingLabel = new();

        public static FittingWatch? Start(Document doc)
        {
            try
            {
                var w = new FittingWatch();
                foreach (var e in Collect(doc, CurveCats)) w._curveType[(long)e.Id.Value] = (long)e.GetTypeId().Value;
                foreach (var e in Collect(doc, FittingCats)) w._fittingLabel[(long)e.Id.Value] = Label(e);
                return w._curveType.Count == 0 ? null : w;
            }
            catch (Exception)
            {
                return null;   // the note is a courtesy, never a reason to fail a write
            }
        }

        /// <summary>The warning text, or null. Call after the write ran (its changes are in the document).</summary>
        public string? Note(Document doc, ChangeSet changes)
        {
            try
            {
                var retyped = new List<string>();
                foreach (var id in changes.Modified)
                {
                    if (!_curveType.TryGetValue(id, out var before)) continue;
                    var e = doc.GetElement(ElemIds.From(id));
                    if (e == null || (long)e.GetTypeId().Value == before) continue;
                    retyped.Add(e.Category?.Name ?? "Curves");
                }
                if (retyped.Count == 0) return null;

                var added = new List<string>();
                var modified = new List<string>();
                var deleted = new List<string>();
                foreach (var id in changes.Added)
                    if (doc.GetElement(ElemIds.From(id)) is Element a && IsFitting(a)) added.Add(Label(a));
                foreach (var id in changes.Modified)
                    if (doc.GetElement(ElemIds.From(id)) is Element m && IsFitting(m)) modified.Add(Label(m));
                foreach (var id in changes.Deleted)
                    if (_fittingLabel.TryGetValue(id, out var label)) deleted.Add(label);
                return FittingChangeNote.Build(retyped, added, modified, deleted);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IEnumerable<Element> Collect(Document doc, BuiltInCategory[] cats) =>
            new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(cats.ToList()))
                .WhereElementIsNotElementType();

        private static bool IsFitting(Element e)
        {
            var cat = e.Category;
            if (cat == null) return false;
            var id = (long)cat.Id.Value;
            return FittingCats.Any(c => (long)c == id);
        }

        private static string Label(Element e) =>
            (e is FamilyInstance fi && !string.IsNullOrEmpty(fi.Symbol?.FamilyName) ? fi.Symbol!.FamilyName : e.Category?.Name) ?? "Fitting";
    }
}
