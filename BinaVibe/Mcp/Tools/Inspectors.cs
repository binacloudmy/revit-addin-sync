// Inspectors — read-only Revit API implementations for the 10 INSPECT
// tools the bina-ai Inspector preflight calls.
//
// Each method MUST run on Revit's main thread (callers are inside the
// IExternalEventHandler execute). Returns are plain dicts/lists so
// JSON serialization is trivial — never embed Revit objects.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class Inspectors
    {
        // ─── list_levels ────────────────────────────────────────────────
        public static Dictionary<string, object?> ListLevels(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new Dictionary<string, object?>
                {
                    ["id"] = l.Id.Value,
                    ["name"] = l.Name,
                    ["elevation"] = l.Elevation,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["levels"] = levels };
        }

        // ─── list_wall_types ────────────────────────────────────────────
        public static Dictionary<string, object?> ListWallTypes(Document doc)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id.Value,
                    ["name"] = t.Name,
                    ["family_name"] = t.FamilyName,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["wall_types"] = types };
        }

        // ─── list_family_types ──────────────────────────────────────────
        public static Dictionary<string, object?> ListFamilyTypes(Document doc, JsonElement args)
        {
            var category = TryGetString(args, "category") ?? "OST_Doors";
            var bic = ResolveBuiltInCategory(category);

            IEnumerable<Element> q = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (bic.HasValue)
                q = q.OfCategory(bic.Value);

            var types = q
                .Take(500)
                .Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id.Value,
                    ["name"] = t.Name,
                    ["family_name"] = (t as ElementType)?.FamilyName,
                })
                .ToList<object>();
            return new Dictionary<string, object?>
            {
                ["category"] = category,
                ["types"] = types,
            };
        }

        // ─── list_view_templates ────────────────────────────────────────
        public static Dictionary<string, object?> ListViewTemplates(Document doc)
        {
            var templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate)
                .Select(v => new Dictionary<string, object?>
                {
                    ["id"] = v.Id.Value,
                    ["name"] = v.Name,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["view_templates"] = templates };
        }

        // ─── list_worksets ──────────────────────────────────────────────
        public static Dictionary<string, object?> ListWorksets(Document doc)
        {
            if (!doc.IsWorkshared)
                return new Dictionary<string, object?> { ["worksets"] = new List<object>(), ["workshared"] = false };

            var ws = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .Select(w => new Dictionary<string, object?>
                {
                    ["id"] = w.Id.IntegerValue,
                    ["name"] = w.Name,
                    ["owner"] = w.Owner,
                    ["open"] = w.IsOpen,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["worksets"] = ws, ["workshared"] = true };
        }

        // ─── get_element_parameters ─────────────────────────────────────
        public static Dictionary<string, object?> GetElementParameters(Document doc, JsonElement args)
        {
            long id = TryGetLong(args, "element_id") ?? 0;
            if (id == 0) throw new System.ArgumentException("missing element_id");

            var el = doc.GetElement(new ElementId(id));
            if (el == null) throw new System.ArgumentException($"element {id} not found");

            var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                ? doc.GetElement(el.GetTypeId())
                : null;
            var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                ? doc.GetElement(el.LevelId)
                : null;

            var paramMap = new Dictionary<string, object?>();
            foreach (Parameter p in el.Parameters)
            {
                if (p == null || p.Definition == null) continue;
                paramMap[p.Definition.Name] = SafeParamValue(p);
            }

            return new Dictionary<string, object?>
            {
                ["id"] = el.Id.Value,
                ["name"] = el.Name,
                ["category"] = el.Category?.Name,
                ["type_name"] = typeEl?.Name,
                ["level_name"] = lvl?.Name,
                ["parameters"] = paramMap,
            };
        }

        // ─── find_elements_by_filter ────────────────────────────────────
        public static Dictionary<string, object?> FindElementsByFilter(Document doc, JsonElement args)
        {
            var category = TryGetString(args, "category") ?? "Walls";
            var bic = ResolveBuiltInCategory(category);
            var predicate = TryGetString(args, "predicate");

            IEnumerable<Element> q = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue) q = q.OfCategory(bic.Value);

            var matches = new List<object>();
            foreach (var el in q.Take(50))
            {
                if (!PredicateMatches(el, doc, predicate)) continue;
                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;
                var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.LevelId) : null;
                matches.Add(new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["name"] = el.Name,
                    ["type_name"] = typeEl?.Name,
                    ["level"] = lvl?.Name,
                });
                if (matches.Count >= 20) break;
            }
            return new Dictionary<string, object?>
            {
                ["category"] = category,
                ["predicate"] = predicate,
                ["matches"] = matches,
            };
        }

        // ─── get_current_selection ──────────────────────────────────────
        public static Dictionary<string, object?> GetCurrentSelection(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var sel = new List<object>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el == null) continue;
                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;
                sel.Add(new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["category"] = el.Category?.Name,
                    ["type_name"] = typeEl?.Name,
                });
            }
            return new Dictionary<string, object?> { ["selection"] = sel };
        }

        // ─── get_active_view ────────────────────────────────────────────
        public static Dictionary<string, object?> GetActiveView(Document doc)
        {
            var v = doc.ActiveView;
            if (v == null) return new Dictionary<string, object?> { ["name"] = null };
            return new Dictionary<string, object?>
            {
                ["id"] = v.Id.Value,
                ["name"] = v.Name,
                ["type"] = v.ViewType.ToString(),
                ["scale"] = v.Scale,
                ["crop_active"] = v.CropBoxActive,
            };
        }

        // ─── get_current_view_elements ──────────────────────────────────
        public static Dictionary<string, object?> GetCurrentViewElements(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var view = doc.ActiveView;
            if (view == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["elements"] = new List<object>() };

            const int cap = 200;
            var elements = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Take(cap)
                .Select(el =>
                {
                    var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                        ? doc.GetElement(el.GetTypeId()) : null;
                    return (object)new Dictionary<string, object?>
                    {
                        ["id"] = el.Id.Value,
                        ["category"] = el.Category?.Name,
                        ["type_name"] = typeEl?.Name,
                    };
                })
                .ToList<object>();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["elements"] = elements,
                ["view_name"] = view.Name,
            };
        }

        // ─── get_project_info ───────────────────────────────────────────
        public static Dictionary<string, object?> GetProjectInfo(Document doc, UIApplication app)
        {
            var info = doc.ProjectInformation;
            string? lengthUnit = null;
            try
            {
                var fmt = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
                lengthUnit = fmt.GetUnitTypeId().TypeId;
            }
            catch { }
            return new Dictionary<string, object?>
            {
                ["name"] = info?.Name,
                ["number"] = info?.Number,
                ["address"] = info?.Address,
                ["revit_version"] = app.Application?.VersionNumber,
                ["units"] = lengthUnit,
                ["title"] = doc.Title,
            };
        }

        // ─── list_views ─────────────────────────────────────────────────
        public static Dictionary<string, object?> ListViews(Document doc)
        {
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate)
                .Select(v =>
                {
                    var lvlId = v.GenLevel?.Id;
                    var lvlName = lvlId != null && lvlId.Value != ElementId.InvalidElementId.Value
                        ? doc.GetElement(lvlId)?.Name : null;
                    return (object)new Dictionary<string, object?>
                    {
                        ["id"] = v.Id.Value,
                        ["name"] = v.Name,
                        ["view_type"] = v.ViewType.ToString(),
                        ["level"] = lvlName,
                    };
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["views"] = views };
        }

        // ─── list_sheets ────────────────────────────────────────────────
        public static Dictionary<string, object?> ListSheets(Document doc)
        {
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Select(s => (object)new Dictionary<string, object?>
                {
                    ["id"] = s.Id.Value,
                    ["number"] = s.SheetNumber,
                    ["name"] = s.Name,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["sheets"] = sheets };
        }

        // ─── list_schedules ─────────────────────────────────────────────
        public static Dictionary<string, object?> ListSchedules(Document doc)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate)
                .Select(v => (object)new Dictionary<string, object?>
                {
                    ["id"] = v.Id.Value,
                    ["name"] = v.Name,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["schedules"] = schedules };
        }

        // ─── list_grids ─────────────────────────────────────────────────
        public static Dictionary<string, object?> ListGrids(Document doc)
        {
            var grids = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Grids)
                .WhereElementIsNotElementType()
                .Select(el => (object)new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["name"] = el.Name,
                })
                .ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["grids"] = grids };
        }

        // ─── analyze_model_statistics ────────────────────────────────────
        public static Dictionary<string, object?> AnalyzeModelStatistics(Document doc)
        {
            var counts = new Dictionary<string, int>();
            int total = 0;

            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var catName = el.Category?.Name;
                if (string.IsNullOrEmpty(catName)) continue;
                counts.TryGetValue(catName, out var n);
                counts[catName] = n + 1;
                total++;
            }

            // Sort descending by count for readability.
            var sorted = counts
                .OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["counts"] = sorted,
                ["total"] = total,
            };
        }

        // ─── find_elements_by_parameter ─────────────────────────────────
        /// <summary>
        /// args: { category, conditions:[{param,op,value}], match:"all"|"any" }
        /// ops: = != &lt; &gt; &lt;= &gt;= contains
        /// </summary>
        public static Dictionary<string, object?> FindElementsByParameter(Document doc, JsonElement args)
        {
            var category = TryGetString(args, "category") ?? "Walls";
            var matchMode = TryGetString(args, "match") ?? "all";
            bool matchAll = !matchMode.Equals("any", System.StringComparison.OrdinalIgnoreCase);

            // Parse conditions array.
            var conditions = new List<(string param, string op, string value)>();
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("conditions", out var condArr) &&
                condArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var cond in condArr.EnumerateArray())
                {
                    if (cond.ValueKind != JsonValueKind.Object) continue;
                    var p = TryGetString(cond, "param") ?? "";
                    var op = TryGetString(cond, "op") ?? "=";
                    var v = TryGetString(cond, "value") ?? "";
                    if (!string.IsNullOrEmpty(p))
                        conditions.Add((p, op, v));
                }
            }

            var bic = ResolveBuiltInCategory(category);
            IEnumerable<Element> q = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue) q = q.OfCategory(bic.Value);

            const int cap = 50;
            var elements = new List<object>();

            foreach (var el in q)
            {
                if (elements.Count >= cap) break;

                bool passes = conditions.Count == 0
                    ? true
                    : matchAll
                        ? conditions.All(c => EvaluateCondition(el, doc, c.param, c.op, c.value))
                        : conditions.Any(c => EvaluateCondition(el, doc, c.param, c.op, c.value));

                if (!passes) continue;

                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;

                // Collect matched param values.
                var matchedParams = new Dictionary<string, object?>();
                foreach (var (paramName, _, _) in conditions)
                {
                    var p = el.LookupParameter(paramName);
                    if (p != null) matchedParams[paramName] = SafeParamValue(p);
                }

                elements.Add(new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["category"] = el.Category?.Name,
                    ["type_name"] = typeEl?.Name,
                    ["params"] = matchedParams,
                });
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["elements"] = elements,
                ["count"] = elements.Count,
            };
        }

        // ─── condition evaluator ─────────────────────────────────────────
        private static bool EvaluateCondition(Element el, Document doc, string paramName, string op, string wantStr)
        {
            var p = el.LookupParameter(paramName);
            if (p == null) return false;

            var raw = SafeParamValue(p);
            var rawStr = raw?.ToString() ?? "";

            // Try numeric comparison first when both sides coerce.
            if (double.TryParse(wantStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var wantNum))
            {
                double? actualNum = null;
                if (p.StorageType == StorageType.Double) actualNum = p.AsDouble();
                else if (p.StorageType == StorageType.Integer) actualNum = (double)p.AsInteger();
                else if (double.TryParse(rawStr, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    actualNum = parsed;

                if (actualNum.HasValue)
                {
                    return op switch
                    {
                        "="  or "==" => System.Math.Abs(actualNum.Value - wantNum) < 1e-9,
                        "!="         => System.Math.Abs(actualNum.Value - wantNum) >= 1e-9,
                        "<"          => actualNum.Value < wantNum,
                        ">"          => actualNum.Value > wantNum,
                        "<="         => actualNum.Value <= wantNum,
                        ">="         => actualNum.Value >= wantNum,
                        "contains"   => rawStr.IndexOf(wantStr, System.StringComparison.OrdinalIgnoreCase) >= 0,
                        _            => false,
                    };
                }
            }

            // String comparison.
            return op switch
            {
                "="  or "==" => string.Equals(rawStr, wantStr, System.StringComparison.OrdinalIgnoreCase),
                "!="         => !string.Equals(rawStr, wantStr, System.StringComparison.OrdinalIgnoreCase),
                "contains"   => rawStr.IndexOf(wantStr, System.StringComparison.OrdinalIgnoreCase) >= 0,
                // Non-numeric elements never satisfy numeric ordering — non-match.
                "<" or ">" or "<=" or ">=" => false,
                _ => false,
            };
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static string? TryGetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static long? TryGetLong(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        private static object? SafeParamValue(Parameter p)
        {
            try
            {
                return p.StorageType switch
                {
                    StorageType.String => p.AsString(),
                    StorageType.Integer => p.AsInteger(),
                    StorageType.Double => p.AsDouble(),
                    StorageType.ElementId => p.AsElementId().Value,
                    _ => p.AsValueString(),
                };
            }
            catch { return null; }
        }

        private static bool PredicateMatches(Element el, Document doc, string? predicate)
        {
            if (string.IsNullOrWhiteSpace(predicate)) return true;
            // Tiny predicate language: "type_name=JKR-Partition-100mm",
            // "level=Level 2", "param:Mark=W-001". Anything fancier needs
            // a real parser (Step 3 work).
            var idx = predicate.IndexOf('=');
            if (idx < 0) return true;
            var key = predicate.Substring(0, idx).Trim();
            var want = predicate.Substring(idx + 1).Trim();

            if (key.Equals("type_name", System.StringComparison.OrdinalIgnoreCase))
            {
                var t = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;
                return string.Equals(t?.Name, want, System.StringComparison.OrdinalIgnoreCase);
            }
            if (key.Equals("level", System.StringComparison.OrdinalIgnoreCase))
            {
                var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.LevelId) : null;
                return string.Equals(lvl?.Name, want, System.StringComparison.OrdinalIgnoreCase);
            }
            if (key.StartsWith("param:", System.StringComparison.OrdinalIgnoreCase))
            {
                var paramName = key.Substring(6);
                var p = el.LookupParameter(paramName);
                if (p == null) return false;
                return string.Equals(SafeParamValue(p)?.ToString(), want, System.StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private static BuiltInCategory? ResolveBuiltInCategory(string category)
        {
            // Accept either the BIC enum literal ("OST_Walls") or the
            // friendly category name ("Walls", "Doors", "Windows").
            if (category.StartsWith("OST_", System.StringComparison.OrdinalIgnoreCase)
                && System.Enum.TryParse<BuiltInCategory>(category, true, out var bic))
                return bic;
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
