#nullable enable
// Revit-bound half of model_overview: computes each section from the existing
// inspectors (Inspectors.List*, GetModelWarnings, GetProjectInfo; R3: called,
// never edited) and caches it per document state (OverviewCacheKey: version
// GUID, session nonce, number of saves, DocumentChanged sequence).
//
// RequestPrewarm() (the pane calls it when it opens) makes the next Idling
// compute every section, so the model's first overview is a cache hit. The
// cache is skipped when SnapshotService is not attached, because then edits
// would not move the key.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Read
{
    public static class OverviewService
    {
        private static readonly OverviewCache Cache = new();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Prewarm = new();

        /// <summary>Compute the whole overview over the next Idling events, one section
        /// per Idling so Revit stays responsive (call when the pane opens).</summary>
        public static void RequestPrewarm()
        {
            while (Prewarm.TryDequeue(out _)) { }
            foreach (var s in OverviewSections.All) Prewarm.Enqueue(s);
        }

        internal static void OnIdling(UIApplication app)
        {
            if (!Prewarm.TryDequeue(out var section)) return;
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                while (Prewarm.TryDequeue(out _)) { }
                return;
            }
            Get(app, doc, section, null);
        }

        /// <summary>One section, from cache when the document has not changed.</summary>
        public static OverviewSection Get(UIApplication app, Document doc, string section, string? category)
        {
            var slot = section == "types" ? "types|" + OverviewSections.Fold(category) : section;
            var key = KeyFor(doc);
            if (key.HasValue && Cache.TryGet(key.Value, slot, out var hit)) return hit;
            OverviewSection computed;
            try { computed = Compute(app, doc, section, category); }
            catch (Exception ex)
            {
                computed = new OverviewSection { Name = section };
                computed.Extra["error"] = "Could not read " + section + ": " + ex.Message;
                return computed;   // never cache a failure
            }
            if (key.HasValue) Cache.Put(key.Value, slot, computed);
            return computed;
        }

        private static OverviewCacheKey? KeyFor(Document doc)
        {
            if (!RevitThread.IsAttached) return null;
            try
            {
                var v = Document.GetDocumentVersion(doc);
                return OverviewCacheKey.Of(v?.VersionGUID.ToString() ?? "", SnapshotService.SessionNonceOf(doc),
                    v?.NumberOfSaves ?? 0, SnapshotService.ChangeSeqOf(doc));
            }
            catch { return null; }
        }

        private static OverviewSection Compute(UIApplication app, Document doc, string section, string? category) =>
            section switch
            {
                "levels" => Levels(doc),
                "grids" => Grids(doc),
                "rooms" => Rooms(doc),
                "types" => Types(doc, category),
                "views" => Views(doc),
                "sheets" => Sheets(doc),
                "links" => Links(doc),
                "worksets" => Worksets(doc),
                "warnings" => Warnings(doc),
                "project" => Project(doc, app),
                _ => throw new ArgumentException("unknown section " + section),
            };

        private static IEnumerable<Dictionary<string, object?>> RowsOf(Dictionary<string, object?> result, string key) =>
            result.TryGetValue(key, out var v) && v is IEnumerable<object> list
                ? list.OfType<Dictionary<string, object?>>()
                : Enumerable.Empty<Dictionary<string, object?>>();

        private static object? Field(Dictionary<string, object?> row, string key) =>
            row.TryGetValue(key, out var v) ? v : null;

        private static OverviewSection Levels(Document doc)
        {
            var s = new OverviewSection { Name = "levels", NarrowHint = "part of a level name" };
            foreach (var r in RowsOf(Inspectors.ListLevels(doc), "levels"))
            {
                var ft = Field(r, "elevation") is double d ? d : 0;
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["name"] = Field(r, "name"),
                    ["elevation"] = RevitValues.FormatLength(doc, ft),
                    ["id"] = Field(r, "id"),
                });
            }
            return s;
        }

        private static OverviewSection Grids(Document doc)
        {
            var s = new OverviewSection { Name = "grids", NarrowHint = "part of a grid name" };
            foreach (var r in RowsOf(Inspectors.ListGrids(doc), "grids"))
                s.Rows.Add(new Dictionary<string, object?> { ["name"] = Field(r, "name"), ["id"] = Field(r, "id") });
            return s;
        }

        private static OverviewSection Rooms(Document doc)
        {
            var s = new OverviewSection { Name = "rooms", NarrowKey = "level,name,number", NarrowHint = "a level or room name, e.g. Level 2" };
            var byLevel = new Dictionary<string, int>(StringComparer.Ordinal);
            int unplaced = 0;
            using var empty = JsonDocument.Parse("{}");
            foreach (var r in RowsOf(Inspectors.ListRooms(doc, empty.RootElement), "rooms"))
            {
                var placed = Field(r, "placed") is bool b && b;
                if (!placed) unplaced++;
                var level = Convert.ToString(Field(r, "level")) ?? "";
                byLevel[level] = byLevel.TryGetValue(level, out var n) ? n + 1 : 1;
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["number"] = Field(r, "number"),
                    ["name"] = Field(r, "name"),
                    ["level"] = level,
                    ["area"] = placed && Field(r, "area_m2") is double a ? a.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " m²" : null,
                    ["placed"] = placed,
                    ["id"] = Field(r, "id"),
                });
            }
            s.Extra["by_level"] = byLevel;
            s.Extra["unplaced"] = unplaced;
            return s;
        }

        private static OverviewSection Types(Document doc, string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                // Family types per category: how many types, and how many placed instances.
                var s = new OverviewSection { Name = "types", NarrowKey = "category", NarrowHint = "a category, e.g. Doors" };
                var types = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType())
                {
                    var c = t.Category;
                    if (c == null || c.CategoryType != CategoryType.Model || string.IsNullOrEmpty(c.Name)) continue;
                    types[c.Name] = types.TryGetValue(c.Name, out var n) ? n + 1 : 1;
                }
                var instances = new Dictionary<string, int>(StringComparer.Ordinal);
                if (Inspectors.AnalyzeModelStatistics(doc).TryGetValue("counts", out var countsObj) && countsObj is Dictionary<string, object> counts)
                    foreach (var kv in counts) instances[kv.Key] = Convert.ToInt32(kv.Value);
                foreach (var kv in types
                    .OrderByDescending(kv => instances.TryGetValue(kv.Key, out var i) ? i : 0)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal))
                    s.Rows.Add(new Dictionary<string, object?>
                    {
                        ["category"] = kv.Key,
                        ["types"] = kv.Value,
                        ["instances"] = instances.TryGetValue(kv.Key, out var i) ? i : 0,
                    });
                s.Extra["note"] = "Give category to list the types of one category with instance counts.";
                return s;
            }

            var bic = RevitValues.ResolveCategory(doc, category!, out var candidates);
            var sec = new OverviewSection { Name = "types", NarrowKey = null, NarrowHint = "a category, e.g. Doors" };
            if (!bic.HasValue)
            {
                sec.Extra["error"] = "Unknown category '" + category + "'.";
                sec.Extra["candidates"] = candidates.ToList();
                return sec;
            }
            using var args = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["category"] = bic.Value.ToString() }));
            var result = Inspectors.ListFamilyTypes(doc, args.RootElement);
            foreach (var r in RowsOf(result, "types"))
            {
                var row = new Dictionary<string, object?>
                {
                    ["name"] = Field(r, "name"),
                    ["family"] = Field(r, "family_name"),
                    ["instances"] = Field(r, "instances"),
                    ["id"] = Field(r, "id"),
                };
                foreach (var dim in new[] { "width_mm", "height_mm" })
                    if (Field(r, dim) is double mm) row[dim.Replace("_mm", "")] = mm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " mm";
                sec.Rows.Add(row);
            }
            sec.Extra["category"] = RevitValues.CategoryLabel(doc, bic.Value);
            sec.Extra["instances_in_category"] = Field(result, "total_instances_in_category");
            return sec;
        }

        private static OverviewSection Views(Document doc)
        {
            var s = new OverviewSection { Name = "views", NarrowKey = "type,name,level", NarrowHint = "a view type or name, e.g. FloorPlan" };
            foreach (var r in RowsOf(Inspectors.ListViews(doc), "views"))
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["name"] = Field(r, "name"),
                    ["type"] = Field(r, "view_type"),
                    ["level"] = Field(r, "level"),
                    ["id"] = Field(r, "id"),
                });
            s.Extra["templates"] = RowsOf(Inspectors.ListViewTemplates(doc), "view_templates").Count();
            return s;
        }

        private static OverviewSection Sheets(Document doc)
        {
            var s = new OverviewSection { Name = "sheets", NarrowKey = "number,name", NarrowHint = "a sheet number prefix or name, e.g. A1" };
            foreach (var r in RowsOf(Inspectors.ListSheets(doc), "sheets")
                .OrderBy(r => Convert.ToString(Field(r, "number")), StringComparer.OrdinalIgnoreCase))
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["number"] = Field(r, "number"),
                    ["name"] = Field(r, "name"),
                    ["id"] = Field(r, "id"),
                });
            return s;
        }

        private static OverviewSection Links(Document doc)
        {
            var s = new OverviewSection { Name = "links", NarrowKey = "name,kind", NarrowHint = "part of a link name" };
            var revit = new Dictionary<string, (bool Loaded, int Instances)>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var name = LinkNames.Display(SnapshotService.LinkTypeName(doc, inst));
                bool loaded = false;
                try { loaded = inst.GetLinkDocument() != null; } catch { }
                revit[name] = revit.TryGetValue(name, out var was) ? (was.Loaded || loaded, was.Instances + 1) : (loaded, 1);
            }
            foreach (var kv in revit.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["name"] = kv.Key,
                    ["kind"] = "revit",
                    ["loaded"] = kv.Value.Loaded,
                    ["instances"] = kv.Value.Instances,
                });
            foreach (var imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                string name;
                try { name = DwgReader.NameOf(imp); } catch { name = imp.Name ?? "CAD"; }
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["kind"] = imp.IsLinked ? "cad_link" : "cad_import",
                    ["id"] = imp.Id.Value,
                });
            }
            return s;
        }

        private static OverviewSection Worksets(Document doc)
        {
            var s = new OverviewSection { Name = "worksets", NarrowHint = "part of a workset name" };
            var result = Inspectors.ListWorksets(doc);
            foreach (var r in RowsOf(result, "worksets"))
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["name"] = Field(r, "name"),
                    ["open"] = Field(r, "open"),
                    ["owner"] = string.IsNullOrEmpty(Convert.ToString(Field(r, "owner"))) ? null : Field(r, "owner"),
                });
            s.Extra["workshared"] = Field(result, "workshared");
            return s;
        }

        private static OverviewSection Warnings(Document doc)
        {
            var s = new OverviewSection { Name = "warnings", NarrowKey = "text", NarrowHint = "words from the warning text", TotalLabel = "warnings" };
            var result = Inspectors.GetModelWarnings(doc);
            foreach (var r in RowsOf(result, "by_type"))
            {
                var ids = Field(r, "element_ids") is IEnumerable<object> list ? list.Take(5).ToList() : new List<object>();
                s.Rows.Add(new Dictionary<string, object?>
                {
                    ["text"] = Field(r, "type"),
                    ["count"] = Field(r, "count"),
                    ["ids"] = ids,
                });
            }
            int total;
            try { total = doc.GetWarnings().Count; } catch { total = Convert.ToInt32(Field(result, "count") ?? 0); }
            s.Total = total;
            s.Extra["groups"] = s.Rows.Count;
            s.Extra["note"] = "Rows group warnings by text (count = warnings of that kind, ids = first 5 elements).";
            return s;
        }

        private static OverviewSection Project(Document doc, UIApplication app)
        {
            var s = new OverviewSection { Name = "project", IsList = false };
            var info = Inspectors.GetProjectInfo(doc, app);
            s.Extra["name"] = Field(info, "name");
            s.Extra["number"] = Field(info, "number");
            s.Extra["address"] = Field(info, "address");
            s.Extra["title"] = doc.Title;
            s.Extra["revit_version"] = Field(info, "revit_version");
            s.Extra["kind"] = doc.IsFamilyDocument ? "family" : "project";
            s.Extra["workshared"] = doc.IsWorkshared;
            s.Extra["length_unit"] = RevitValues.UnitSymbol(doc, SpecTypeId.Length);
            s.Extra["phases"] = RowsOf(Inspectors.ListPhases(doc), "phases").Select(r => Field(r, "name")).ToList();
            s.Extra["design_options"] = RowsOf(Inspectors.ListDesignOptions(doc), "design_options")
                .Select(r => Convert.ToString(Field(r, "option_set")) + ": " + Convert.ToString(Field(r, "name"))
                             + (Field(r, "is_primary") is bool p && p ? " (primary)" : ""))
                .ToList();
            try { s.Extra["warnings"] = doc.GetWarnings().Count; } catch { }
            return s;
        }
    }
}
