using Autodesk.Revit.DB;
using RevitWebAppSync.Services;
using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Revit side of <see cref="JkrFixBatch.CountAffected"/>: how many elements a
    /// batch will change, counting every instance of a type that a rename or a
    /// type-level parameter write touches. Read-only; call it inside a
    /// RevitDispatcher job, before the confirm dialog.
    ///
    /// Cost: resolving the steps is one GetElement per step. Only when some step is
    /// type-wide does it walk the model's non-type elements once, and it stops as
    /// soon as <paramref name="stopAt"/> is reached, so a big batch on a big model
    /// usually returns after a fraction of the model.
    /// </summary>
    public static class JkrAffectedElements
    {
        /// <param name="renames">Element ids queued for rename_type.</param>
        /// <param name="parameterFixes">Parameter fixes (Target "type" is type-wide).</param>
        public static int Count(Document doc, IEnumerable<long> renames, IEnumerable<JkrFixAction> parameterFixes,
                                int stopAt = JkrFixBatch.PauseWarningElementCount)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var direct = new List<long>();
            var typeWide = new HashSet<long>();

            foreach (var id in renames ?? Array.Empty<long>())
            {
                var elem = doc.GetElement(ElemIds.From(id));
                if (elem == null) continue;
                if (elem is Grid || elem is Level) { direct.Add(id); continue; }
                if (elem is ElementType) { direct.Add(id); typeWide.Add(id); continue; }

                var typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    direct.Add(IdValue(typeId));
                    typeWide.Add(IdValue(typeId));
                }
                else direct.Add(id);
            }

            foreach (var fix in parameterFixes ?? Array.Empty<JkrFixAction>())
            {
                if (fix == null) continue;
                direct.Add(fix.ElementId);
                if (!string.Equals(fix.Target, "type", StringComparison.OrdinalIgnoreCase)) continue;
                var elem = doc.GetElement(ElemIds.From(fix.ElementId));
                if (elem == null) continue;
                if (elem is ElementType) { typeWide.Add(fix.ElementId); continue; }
                var typeId = elem.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    typeWide.Add(IdValue(typeId));
            }

            return JkrFixBatch.CountAffected(direct, typeWide, Instances(doc), stopAt);
        }

        private static long IdValue(ElementId id)
        {
#if REVIT2023_24
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        private static IEnumerable<(long Id, long TypeId)> Instances(Document doc)
        {
            using (var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                foreach (var e in collector)
                {
                    var typeId = e.GetTypeId();
                    if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                    yield return (IdValue(e.Id), IdValue(typeId));
                }
            }
        }
    }
}
