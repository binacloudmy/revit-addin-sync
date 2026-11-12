// The small Revit jobs around a CAD to BIM build (AI-344): find which built ids are gone,
// "Undo this build", and "Select in model". Like Cad2BimBuilder these run only on Revit's
// main thread, inside CadToBimBuildHandler.Execute, and never throw: a failure is a
// BuildReport with Error set.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services.CadToBim
{
    public static class Cad2BimModelOps
    {
        /// <summary>Which document a report's ids belong to: its path, or its title while unsaved.</summary>
        public static string KeyOf(Document doc)
        {
            if (doc == null) return null;
            return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
        }

        /// <summary>The ids in <paramref name="ids"/> that no element of the document has.</summary>
        public static List<long> Missing(Document doc, IEnumerable<long> ids)
        {
            var missing = new List<long>();
            if (doc == null || ids == null) return missing;
            foreach (long id in ids.Distinct())
            {
                if (id <= 0 || doc.GetElement(ElementIdCompat.ToElementId(id)) == null) missing.Add(id);
            }
            return missing;
        }

        /// <summary>CheckBuilt: the pane's built ids that the active model no longer has. With no
        /// project open nothing can be judged, so nothing is reported missing.</summary>
        public static BuildReport CheckBuilt(UIApplication app, BuildRequest req)
        {
            var report = new BuildReport { Kind = BuildJobKind.CheckBuilt };
            try
            {
                Document doc = app?.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument) return report;
                report.DocumentKey = KeyOf(doc);
                report.MissingIds.AddRange(Missing(doc, req?.ElementIds));
            }
            catch (Exception ex)
            {
                report.Error = Innermost(ex).Message;
            }
            return report;
        }

        /// <summary>
        /// Undo this build: deletes the ids the build recorded, in one transaction, so it is one
        /// Ctrl+Z to bring them back. Works after other edits, which Revit's own undo does not.
        /// Ids already gone (deleted by hand, or with their host) are passed over.
        /// </summary>
        public static BuildReport Undo(UIApplication app, BuildRequest req)
        {
            var report = new BuildReport { Kind = BuildJobKind.Undo };
            try
            {
                Document doc = ActiveFor(app, req, out string error);
                if (doc == null) { report.Error = error; return report; }
                report.DocumentKey = KeyOf(doc);

                List<long> requested = req?.ElementIds ?? new List<long>();
                var goneBefore = new HashSet<long>(Missing(doc, requested));

                using (var tx = new Transaction(doc, "CAD to BIM: undo build"))
                {
                    tx.Start();
                    FailureHandlingOptions options = tx.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new SilenceJoinFailures());
                    tx.SetFailureHandlingOptions(options);

                    // One at a time, in the order the report gives (rooms → openings → walls →
                    // views → levels): deleting a wall takes its doors with it, deleting the
                    // sketch plane takes its room separation lines, and a collection Delete
                    // throws on the first id that is already gone.
                    foreach (long id in requested)
                    {
                        ElementId elementId = ElementIdCompat.ToElementId(id);
                        if (doc.GetElement(elementId) == null) continue;
                        try
                        {
                            doc.Delete(elementId);
                        }
                        catch
                        {
                            // Pinned or otherwise protected: leave it, delete the rest.
                        }
                    }

                    tx.Commit();
                }

                // Counted after the commit, so ids removed with their host count once and ids
                // that were already gone before the undo do not count at all.
                report.MissingIds.AddRange(Missing(doc, requested));
                report.DeletedIds.AddRange(report.MissingIds.Where(id => !goneBefore.Contains(id)));
                report.Affected = report.DeletedIds.Count;
            }
            catch (Exception ex)
            {
                report.Error = Innermost(ex).Message;
            }
            return report;
        }

        /// <summary>Select in model: selects the ids that still exist and zooms the active view
        /// to them. No transaction.</summary>
        public static BuildReport Select(UIApplication app, BuildRequest req)
        {
            var report = new BuildReport { Kind = BuildJobKind.Select };
            try
            {
                Document doc = ActiveFor(app, req, out string error);
                if (doc == null) { report.Error = error; return report; }

                List<ElementId> ids = (req?.ElementIds ?? new List<long>())
                    .Distinct()
                    .Select(ElementIdCompat.ToElementId)
                    .Where(id => doc.GetElement(id) != null)
                    .ToList();

                UIDocument uidoc = app.ActiveUIDocument;
                uidoc.Selection.SetElementIds(ids);
                if (ids.Count > 0)
                {
                    try { uidoc.ShowElements(ids); }
                    catch { /* the active view cannot show them (a schedule, a sheet): selection still stands */ }
                }
                report.Affected = ids.Count;
            }
            catch (Exception ex)
            {
                report.Error = Innermost(ex).Message;
            }
            return report;
        }

        /// <summary>The active project when it is the one the ids belong to; null with a message
        /// the drafter can act on otherwise.</summary>
        private static Document ActiveFor(UIApplication app, BuildRequest req, out string error)
        {
            error = null;
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                error = "Open the project the build went into first.";
                return null;
            }

            string wanted = req?.DocumentKey;
            if (!string.IsNullOrEmpty(wanted) &&
                !string.Equals(wanted, KeyOf(doc), StringComparison.OrdinalIgnoreCase))
            {
                error = "Switch back to " + System.IO.Path.GetFileName(wanted) + " first: the build went into that model.";
                return null;
            }

            return doc;
        }

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
