// RevitPreviewHighlighter — read-only Preview-in-Revit before an
// approval gate (PRD §6.2 Stage 5, FR-APPROVE-03).
//
// Selects the affected elements in Revit's UI so the drafter can
// eyeball / zoom-to before clicking Approve. Marshalls to Revit's
// main thread through the existing McpExternalEventHandler.
//
// Doesn't modify the model. Restoring the previous selection on
// dismiss is the caller's responsibility (call ClearPreview()).

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Preview
{
    public static class RevitPreviewHighlighter
    {
        public static int Highlight(UIApplication app, IEnumerable<long> elementIds)
        {
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null || elementIds == null) return 0;

            var ids = new List<ElementId>();
            foreach (var raw in elementIds)
            {
                var id = ElemIds.From(raw);
                if (doc.GetElement(id) != null) ids.Add(id);
            }
            uidoc.Selection.SetElementIds(ids);
            return ids.Count;
        }

        public static void ClearPreview(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            if (uidoc == null) return;
            uidoc.Selection.SetElementIds(new List<ElementId>());
        }
    }
}
