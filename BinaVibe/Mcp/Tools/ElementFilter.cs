// filter_elements — general-purpose element query with spatial bbox support.
// adapted from mcp-servers-for-revit (MIT) — AIElementFilterEventHandler.cs +
// Models/Common/FilterSetting.cs. Their kind-split (types vs instances) and
// bbox intersection filter are kept; result shaping follows our envelope.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class ElementFilter
    {
        public static Dictionary<string, object?> Run(UIApplication app, Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category");
            var elementType = ArgsHelp.GetString(args, "element_type");
            var familySymbolId = ArgsHelp.GetLong(args, "family_symbol_id");
            var includeTypes = ArgsHelp.GetBool(args, "include_types") ?? false;
            var includeInstances = ArgsHelp.GetBool(args, "include_instances") ?? true;
            var visibleInView = ArgsHelp.GetBool(args, "visible_in_current_view") ?? false;
            var bboxMin = ArgsHelp.GetPointMm(args, "bbox_min_mm");
            var bboxMax = ArgsHelp.GetPointMm(args, "bbox_max_mm");
            var maxElements = (int)(ArgsHelp.GetLong(args, "max_elements") ?? 50);

            if (category == null && elementType == null && !familySymbolId.HasValue)
                throw new InvalidOperationException(
                    "filter_elements needs at least one of: category, element_type, family_symbol_id");
            if ((bboxMin != null) != (bboxMax != null))
                throw new InvalidOperationException("bbox_min_mm and bbox_max_mm must be passed together");

            var all = new List<Element>();
            if (includeInstances) all.AddRange(Collect(app, doc, category, visibleInView, types: false));
            if (includeTypes) all.AddRange(Collect(app, doc, category, visibleInView: false, types: true));

            IEnumerable<Element> filtered = all;
            if (elementType != null)
                filtered = filtered.Where(e =>
                    string.Equals(e.GetType().Name, elementType, StringComparison.OrdinalIgnoreCase));
            if (familySymbolId.HasValue)
                filtered = filtered.Where(e =>
                    (e as FamilyInstance)?.Symbol?.Id.Value == familySymbolId.Value
                    || e.Id.Value == familySymbolId.Value);
            if (bboxMin != null && bboxMax != null)
            {
                // Normalize corner order — LLM callers may pass corners unsorted.
                var lo = new XYZ(Math.Min(bboxMin.X, bboxMax.X), Math.Min(bboxMin.Y, bboxMax.Y), Math.Min(bboxMin.Z, bboxMax.Z));
                var hi = new XYZ(Math.Max(bboxMin.X, bboxMax.X), Math.Max(bboxMin.Y, bboxMax.Y), Math.Max(bboxMin.Z, bboxMax.Z));
                var outline = new Outline(lo, hi);
                filtered = filtered.Where(e =>
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) return false;
                    return !(bb.Max.X < outline.MinimumPoint.X || bb.Min.X > outline.MaximumPoint.X
                          || bb.Max.Y < outline.MinimumPoint.Y || bb.Min.Y > outline.MaximumPoint.Y
                          || bb.Max.Z < outline.MinimumPoint.Z || bb.Min.Z > outline.MaximumPoint.Z);
                });
            }

            var list = filtered.ToList();
            var truncated = list.Count > maxElements;
            var shaped = list.Take(maxElements).Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id.Value,
                ["name"] = e.Name,
                ["category"] = e.Category?.Name ?? "",
                ["type_name"] = (e as FamilyInstance)?.Symbol?.Name
                                ?? (doc.GetElement(e.GetTypeId()) as ElementType)?.Name ?? "",
                ["level"] = (doc.GetElement(e.LevelId) as Level)?.Name ?? "",
            }).ToList<object>();

            var result = new Dictionary<string, object?>
                { ["ok"] = true, ["items"] = shaped, ["count"] = shaped.Count };
            if (truncated)
                result["truncated"] = $"showing {maxElements} of {list.Count} — narrow the filter or raise max_elements";
            return result;
        }

        private static List<Element> Collect(UIApplication app, Document doc, string? category,
                                             bool visibleInView, bool types)
        {
            FilteredElementCollector c;
            if (visibleInView && app.ActiveUIDocument?.ActiveView is View v)
                c = new FilteredElementCollector(doc, v.Id);
            else
                c = new FilteredElementCollector(doc);
            c = types ? c.WhereElementIsElementType() : c.WhereElementIsNotElementType();
            if (category != null)
            {
                var bic = CategoryResolve.Resolve(category);   // shared resolver, see Interfaces note
                if (bic.HasValue) c = c.OfCategory(bic.Value);
                else throw new InvalidOperationException($"unknown category '{category}'");
            }
            return c.ToElements().ToList();
        }
    }

    // CategoryResolve moved to its own file (Tools/CategoryResolve.cs) so the
    // test project can source-link it without dragging RevitAPIUI in.
}
