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
            var connectivity = ArgsHelp.GetString(args, "connectivity");
            var maxElements = (int)(ArgsHelp.GetLong(args, "max_elements") ?? 50);

            if (category == null && elementType == null && !familySymbolId.HasValue)
                throw new InvalidOperationException(
                    "filter_elements needs at least one of: category, element_type, family_symbol_id");
            if ((bboxMin != null) != (bboxMax != null))
                throw new InvalidOperationException("bbox_min_mm and bbox_max_mm must be passed together");
            if (connectivity != null
                && connectivity != "connected" && connectivity != "unconnected" && connectivity != "no_connectors")
                throw new InvalidOperationException(
                    "connectivity must be one of: connected, unconnected, no_connectors");

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
            if (connectivity != null)
                filtered = filtered.Where(e => ClassifyConnectivity(e) == connectivity);

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

        // "connected" | "unconnected" | "no_connectors" — mirrors the
        // connectivity dimension in Inspectors.CountByDimensionKey (count_by)
        // so the two tools never disagree on what "unconnected" means. No
        // ConnectorManager, or one with an empty connector set (placeholder
        // families), is "no_connectors" — never mislabeled as connected.
        private static string ClassifyConnectivity(Element e)
        {
            var cm = MutatorsMepRouting.GetConnectorManager(e);
            if (cm == null) return "no_connectors";
            bool sawAny = false, sawFree = false;
            foreach (Connector c in cm.Connectors)
            {
                sawAny = true;
                if (!c.IsConnected) sawFree = true;
            }
            if (!sawAny) return "no_connectors";
            return sawFree ? "unconnected" : "connected";
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

    // Category-name resolver shared by Inspectors (list_family_types,
    // find_elements_by_filter, count_by) and ElementFilter (filter_elements).
    // Extracted verbatim from Inspectors.ResolveBuiltInCategory — see that
    // method's original comment: unknown categories must FAIL LOUDLY rather
    // than fall through to an unfiltered collector (that's what forced the
    // multi-round tool tours on "tandas"-style questions).
    internal static class CategoryResolve
    {
        public static BuiltInCategory? Resolve(string nameOrFriendly)
        {
            // Accept either the BIC enum literal ("OST_Walls") or the
            // friendly category name ("Walls", "Doors", "Plumbing Fixtures").
            // The old 7-entry switch silently returned null for everything
            // else — and callers then ran UNFILTERED collectors, handing the
            // agent 500 junk types ("Arrowhead" as a plumbing fixture). That
            // garbage is what forced the multi-round tool tours on every
            // "tandas" question. Resolve generically: any friendly name maps
            // to OST_<NameWithoutSpaces>.
            var category = nameOrFriendly;
            if (string.IsNullOrWhiteSpace(category)) return null;
            if (category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BuiltInCategory>(category, true, out var bic))
                return bic;
            var compact = "OST_" + category.Replace(" ", "").Replace("-", "");
            if (Enum.TryParse<BuiltInCategory>(compact, true, out var bic2))
                return bic2;
            return category.ToLowerInvariant() switch
            {
                "walls" => BuiltInCategory.OST_Walls,
                "doors" => BuiltInCategory.OST_Doors,
                "windows" => BuiltInCategory.OST_Windows,
                "floors" => BuiltInCategory.OST_Floors,
                "rooms" => BuiltInCategory.OST_Rooms,
                "levels" => BuiltInCategory.OST_Levels,
                "grids" => BuiltInCategory.OST_Grids,
                _ => null,
            };
        }
    }
}
