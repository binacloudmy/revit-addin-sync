// Inspectors — read-only Revit API implementations for the 10 INSPECT
// tools the bina-ai Inspector preflight calls.
//
// Each method MUST run on Revit's main thread (callers are inside the
// IExternalEventHandler execute). Returns are plain dicts/lists so
// JSON serialization is trivial — never embed Revit objects.

using System;
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
            var nameContains = TryGetString(args, "name_contains");
            var bic = ResolveBuiltInCategory(category);
            if (!bic.HasValue)
            {
                // Unknown category must FAIL LOUDLY — running unfiltered used to
                // return arrowheads as "plumbing fixtures" and the agent toured
                // for rounds trying to make sense of it.
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"unknown category '{category}' — pass a BuiltInCategory like OST_PlumbingFixtures or a friendly name like 'Plumbing Fixtures'",
                };
            }

            // One pass over INSTANCES grouped by type id → per-type counts.
            // This is what lets the agent answer "berapa banyak X" in a SINGLE
            // round trip instead of touring list→count_by→find (measured 5-round
            // tours for one count question).
            var counts = new Dictionary<long, int>();
            int totalInstances = 0;
            var icol = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue)
                icol = icol.OfCategory(bic.Value);
            foreach (var el in icol)
            {
                var tid = el.GetTypeId().Value;
                if (tid == ElementId.InvalidElementId.Value) continue;
                counts[tid] = counts.TryGetValue(tid, out var n) ? n + 1 : 1;
                totalInstances++;
            }

            var col = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (bic.HasValue)
                col = col.OfCategory(bic.Value);
            IEnumerable<Element> q = col;

            bool NameHit(Element t)
            {
                if (string.IsNullOrEmpty(nameContains)) return true;
                var tn = t.Name;
                var fn = (t as ElementType)?.FamilyName;
                return (tn != null && tn.IndexOf(nameContains, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    || (fn != null && fn.IndexOf(nameContains, System.StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var types = q
                .Where(NameHit)
                .Take(500)
                .Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id.Value,
                    ["name"] = t.Name,
                    ["family_name"] = (t as ElementType)?.FamilyName,
                    ["instances"] = counts.TryGetValue(t.Id.Value, out var c) ? c : 0,
                })
                .ToList<object>();
            return new Dictionary<string, object?>
            {
                ["category"] = category,
                ["name_contains"] = nameContains,
                ["types"] = types,
                ["total_instances_in_category"] = totalInstances,
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
            if (!bic.HasValue)
            {
                // Same loud-failure rule as ListFamilyTypes: an unfiltered
                // collector returns junk ("Project Phase Information" as a
                // plumbing fixture) and sends the agent touring.
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"unknown category '{category}' — pass a BuiltInCategory like OST_PlumbingFixtures or a friendly name like 'Plumbing Fixtures'",
                };
            }

            var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue) col = col.OfCategory(bic.Value);
            IEnumerable<Element> q = col;

            bool isRooms = bic == BuiltInCategory.OST_Rooms;
            const double ft2ToM2 = 0.092903;

            var matches = new List<object>();
            foreach (var el in q.Take(50))
            {
                if (!PredicateMatches(el, doc, predicate)) continue;
                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;
                var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.LevelId) : null;

                // params dict — seeds the mirror with per-element attributes.
                var paramsDict = new Dictionary<string, object?>();
                if (isRooms && el is SpatialElement room)
                {
                    // Revit stores room Area in ft² (×0.092903 → m²). Skip
                    // unplaced/unbounded rooms (Area <= 0) — they have no area.
                    double areaFt2 = room.Area;
                    if (areaFt2 > 0)
                        paramsDict["Area"] = System.Math.Round(areaFt2 * ft2ToM2, 6);
                }

                matches.Add(new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["name"] = el.Name,
                    ["type_name"] = typeEl?.Name,
                    ["level"] = lvl?.Name,
                    ["params"] = paramsDict,
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

        // ─── get_material_quantities ────────────────────────────────────────
        /// <summary>
        /// args: { element_ids?: [long] }
        /// Returns material takeoff (volume m³, area m²) aggregated by material name.
        /// If element_ids is absent or empty, processes all non-type elements (capped at 5000).
        /// Revit stores volume in ft³ (×0.0283168 → m³) and area in ft² (×0.0929030 → m²).
        /// </summary>
        public static Dictionary<string, object?> GetMaterialQuantities(Document doc, JsonElement args)
        {
            const int elementCap = 5000;
            const double ft3ToM3 = 0.0283168;
            const double ft2ToM2 = 0.0929030;

            // Resolve element list: supplied ids or all non-type elements (capped).
            IEnumerable<Element> elements;
            var suppliedIds = new List<long>();
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("element_ids", out var idsEl) &&
                idsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in idsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n))
                        suppliedIds.Add(n);
                }
            }

            if (suppliedIds.Count > 0)
            {
                elements = suppliedIds
                    .Select(id => doc.GetElement(new ElementId(id)))
                    .Where(el => el != null);
            }
            else
            {
                elements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Take(elementCap);
            }

            // Aggregate by material name.
            var matVolume = new Dictionary<string, double>();
            var matArea = new Dictionary<string, double>();

            foreach (var el in elements)
            {
                if (el == null) continue;
                try
                {
                    foreach (ElementId matId in el.GetMaterialIds(false))
                    {
                        var matEl = doc.GetElement(matId);
                        if (matEl == null) continue;
                        var matName = matEl.Name ?? "(unknown)";

                        double volFt3 = el.GetMaterialVolume(matId);
                        double areaFt2 = el.GetMaterialArea(matId, false);

                        matVolume.TryGetValue(matName, out var v);
                        matVolume[matName] = v + volFt3 * ft3ToM3;

                        matArea.TryGetValue(matName, out var a);
                        matArea[matName] = a + areaFt2 * ft2ToM2;
                    }
                }
                catch { /* skip elements that throw (no geometry, etc.) */ }
            }

            var materials = matVolume.Keys
                .OrderBy(k => k)
                .Select(k => (object)new Dictionary<string, object?>
                {
                    ["name"] = k,
                    ["volume_m3"] = System.Math.Round(matVolume[k], 6),
                    ["area_m2"] = System.Math.Round(matArea.TryGetValue(k, out var a2) ? a2 : 0.0, 6),
                })
                .ToList<object>();

            double totalVol = matVolume.Values.Sum();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["materials"] = materials,
                ["total_volume_m3"] = System.Math.Round(totalVol, 6),
            };
        }

        // ─── get_model_warnings ──────────────────────────────────────────
        /// <summary>
        /// Returns the model's open Revit warnings: description + the element ids involved.
        /// Uses Document.GetWarnings() (available Revit 2015+). Capped at 500 warnings.
        /// Returns {ok, warnings:[{description, element_ids:[...]}], count}.
        /// </summary>
        public static Dictionary<string, object?> GetModelWarnings(Document doc)
        {
            const int cap = 500;
            var warnings = doc.GetWarnings();
            var result = new List<object>();

            foreach (var w in warnings.Take(cap))
            {
                var eids = w.GetFailingElements()
                    .Select(eid => (object)eid.Value)
                    .ToList<object>();
                result.Add(new Dictionary<string, object?>
                {
                    ["description"] = w.GetDescriptionText(),
                    ["element_ids"] = eids,
                });
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["warnings"] = result,
                ["count"] = result.Count,
            };
        }

        // ─── list_view_filters ──────────────────────────────────────────
        /// <summary>
        /// Returns all ParameterFilterElement instances in the document:
        /// the reusable view/parameter filters available to views.
        /// Uses FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).
        /// Returns {ok, filters:[{id, name}]}.
        /// </summary>
        public static Dictionary<string, object?> ListViewFilters(Document doc)
        {
            var filters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Select(pf => (object)new Dictionary<string, object?>
                {
                    ["id"] = pf.Id.Value,
                    ["name"] = pf.Name,
                })
                .ToList<object>();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["filters"] = filters,
            };
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
            var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue) col = col.OfCategory(bic.Value);
            IEnumerable<Element> q = col;

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
                // JKR names embed codes in parentheses ("Tandas Cangkung (LSc096a)")
                // and the agent often passes just one part — "(LSc096a)" or
                // "Tandas Cangkung". Exact-equals returned nothing for every
                // JKR-coded family, forcing codegen fallback. Match type name OR
                // family name, containment either way. Levels stay exact-match
                // below ("Level 1" must never match "Level 10").
                return TypeNameMatches(t?.Name, want)
                    || TypeNameMatches((t as ElementType)?.FamilyName, want);
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

        private static bool TypeNameMatches(string? actual, string want)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(want)) return false;
            if (string.Equals(actual, want, System.StringComparison.OrdinalIgnoreCase)) return true;
            return actual.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0
                || want.IndexOf(actual, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static BuiltInCategory? ResolveBuiltInCategory(string category)
        {
            // Accept either the BIC enum literal ("OST_Walls") or the
            // friendly category name ("Walls", "Doors", "Plumbing Fixtures").
            // The old 7-entry switch silently returned null for everything
            // else — and callers then ran UNFILTERED collectors, handing the
            // agent 500 junk types ("Arrowhead" as a plumbing fixture). That
            // garbage is what forced the multi-round tool tours on every
            // "tandas" question. Resolve generically: any friendly name maps
            // to OST_<NameWithoutSpaces>.
            if (string.IsNullOrWhiteSpace(category)) return null;
            if (category.StartsWith("OST_", System.StringComparison.OrdinalIgnoreCase)
                && System.Enum.TryParse<BuiltInCategory>(category, true, out var bic))
                return bic;
            var compact = "OST_" + category.Replace(" ", "").Replace("-", "");
            if (System.Enum.TryParse<BuiltInCategory>(compact, true, out var bic2))
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

        // ─── open_view ──────────────────────────────────────────────────
        // Activate a graphical view by (partial) name. UI navigation, not a
        // document edit — no Transaction. Runs on the Revit UI thread (the
        // ExternalEvent handler), so setting ActiveView takes effect at once.
        public static Dictionary<string, object?> OpenView(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            string name = (args.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no view name given" };

            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.Internal
                            && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser).ToList();

            string lname = name.ToLowerInvariant();
            bool wants3d = lname == "3d" || lname == "3 d" || lname == "{3d}" || lname == "three d"
                           || lname == "3d view" || lname == "three dimensional" || lname == "3d views";

            List<View> candidates;
            if (wants3d)
            {
                candidates = views.Where(v => v is View3D).ToList();
            }
            else
            {
                // An exact name match wins outright (no ambiguity).
                var exact = views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return OpenOne(uidoc, exact);
                candidates = views.Where(v => v.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (candidates.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no view matching '{name}'" };
            if (candidates.Count == 1)
                return OpenOne(uidoc, candidates[0]);

            // If exactly one match is a plan, that's almost certainly the intent
            // ("open Aras 01" → the plan, not the ceiling/3D with the same name).
            if (!wants3d)
            {
                var plans = candidates.Where(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan).ToList();
                if (plans.Count == 1) return OpenOne(uidoc, plans[0]);
            }

            // Genuinely ambiguous (e.g. "open 3d view" with many 3D views) — hand
            // the candidates back so the agent ASKS the user which one, instead of
            // silently opening an arbitrary one.
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["ambiguous"] = true,
                ["note"] = $"{candidates.Count} views match '{name}' — ask the user which one to open (by name), do not guess.",
                ["matches"] = candidates.OrderBy(v => v.Name).Take(25).Select(v => (object)new Dictionary<string, object?>
                {
                    ["name"] = v.Name, ["type"] = v.ViewType.ToString(), ["view_id"] = v.Id.Value,
                }).ToList(),
            };
        }

        private static Dictionary<string, object?> OpenOne(UIDocument uidoc, View view)
        {
            try { uidoc.ActiveView = view; }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"cannot open '{view.Name}': {ex.Message}" };
            }
            return new Dictionary<string, object?> { ["ok"] = true, ["opened"] = view.Name, ["view_id"] = view.Id.Value };
        }

        // ─── select_elements ────────────────────────────────────────────
        // Highlight (select) + zoom to elements by id. The reliable way to
        // satisfy "highlight / show me / bring me to" — pure UI selection, no
        // Transaction. Pair with find_elements_by_filter to get the ids.
        public static Dictionary<string, object?> SelectElements(UIDocument uidoc, JsonElement args)
        {
            var ids = new List<ElementId>();
            if (args.TryGetProperty("element_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v))
                        ids.Add(new ElementId(v));
            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no element ids given" };

            uidoc.Selection.SetElementIds(ids);     // highlight (turns them blue)
            try { uidoc.ShowElements(ids); } catch { /* zoom best-effort */ }
            return new Dictionary<string, object?> { ["ok"] = true, ["selected"] = ids.Count };
        }

        // ─── count_by ───────────────────────────────────────────────────
        // Count a category broken down by level / type / workset. Read-only.
        public static Dictionary<string, object?> CountBy(Document doc, JsonElement args)
        {
            string category = TryGetString(args, "category") ?? "";
            string groupBy = (TryGetString(args, "group_by") ?? "level").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(category))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no category given" };
            var bic = ResolveCategoryRobust(doc, category);
            if (bic == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };

            var els = new FilteredElementCollector(doc).OfCategory(bic.Value)
                .WhereElementIsNotElementType().ToList();

            string KeyOf(Element el)
            {
                if (groupBy == "type")
                {
                    var t = el.GetTypeId();
                    return (t != null && t.Value != ElementId.InvalidElementId.Value
                        ? doc.GetElement(t)?.Name : null) ?? "(no type)";
                }
                if (groupBy == "workset")
                {
                    var wp = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    return wp?.AsValueString() ?? "(no workset)";
                }
                // default: level — direct LevelId, else a level-ish param for hosted elements
                var lid = el.LevelId;
                if (lid != null && lid.Value != ElementId.InvalidElementId.Value)
                    return doc.GetElement(lid)?.Name ?? "(no level)";
                var lp = el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                      ?? el.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                var lpId = lp?.AsElementId();
                if (lpId != null && lpId.Value != ElementId.InvalidElementId.Value)
                    return doc.GetElement(lpId)?.Name ?? "(no level)";
                return "(no level)";
            }

            var groups = els.GroupBy(KeyOf)
                .Select(g => new Dictionary<string, object?> { ["group"] = g.Key, ["count"] = g.Count() })
                .OrderByDescending(d => (int)d["count"]!)
                .Cast<object>().ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["category"] = category, ["group_by"] = groupBy,
                ["total"] = els.Count, ["groups"] = groups,
            };
        }

        // ─── export_schedule_to_excel ───────────────────────────────────
        // Read a ViewSchedule's body cells and write a .xlsx on the Desktop.
        // Read-only on the document (no Transaction); only writes a file.
        public static Dictionary<string, object?> ExportScheduleToExcel(Document doc, JsonElement args)
        {
            string name = TryGetString(args, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no schedule name given" };

            var sched = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .FirstOrDefault(s => s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (sched == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no schedule matching '{name}'" };

            var body = sched.GetTableData().GetSectionData(SectionType.Body);
            int nCols = body.NumberOfColumns, nRows = body.NumberOfRows;
            var headers = new List<string>();
            for (int c = 0; c < nCols; c++) headers.Add(sched.GetCellText(SectionType.Body, 0, c) ?? "");
            var rows = new List<List<string>>();
            for (int r = 1; r < nRows; r++)
            {
                var row = new List<string>();
                for (int c = 0; c < nCols; c++) row.Add(sched.GetCellText(SectionType.Body, r, c) ?? "");
                rows.Add(row);
            }

            string fileName = SanitizeFileName(sched.Name) + ".xlsx";
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = System.IO.Path.Combine(dir, fileName);
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Sheet1");
                for (int c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
                for (int r = 0; r < rows.Count; r++)
                    for (int c = 0; c < rows[r].Count; c++) ws.Cell(r + 2, c + 1).Value = rows[r][c];
                wb.SaveAs(path);
            }
            // kind/headline/path mirror the SetResult(kind="file") shape so the
            // pane renders the Download / Show-in-folder card.
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["kind"] = "file", ["headline"] = fileName,
                ["path"] = dir, ["sub"] = rows.Count + " rows · " + sched.Name,
                ["full_path"] = path,
            };
        }

        private static string SanitizeFileName(string s)
        {
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s.Replace(' ', '_');
        }

        // ─── find_missing_parameter ─────────────────────────────────────
        // Elements with NO value for a parameter — checks instance AND type,
        // by display-name lookup (catches shared/type params like Fire Rating).
        public static Dictionary<string, object?> FindMissingParameter(Document doc, JsonElement args)
        {
            string category = TryGetString(args, "category") ?? "";
            string param = TryGetString(args, "parameter") ?? "";
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(param))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "need category and parameter" };
            var bic = ResolveCategoryRobust(doc, category);
            if (bic == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };

            var els = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToList();
            var missing = new List<object>();
            foreach (var e in els)
            {
                if (!string.IsNullOrWhiteSpace(ResolveParamValue(doc, e, param))) continue;
                var typeEl = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId()) : null;
                missing.Add(new Dictionary<string, object?>
                {
                    ["id"] = e.Id.Value,
                    ["type_name"] = typeEl?.Name,
                    ["level"] = doc.GetElement(e.LevelId)?.Name,
                });
            }
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["category"] = category, ["parameter"] = param,
                ["missing"] = missing.Count, ["total"] = els.Count, ["elements"] = missing,
            };
        }

        // Resolve a parameter's value by display name on the instance, falling
        // back to its type. "" when truly absent/blank. Shared by find/colour.
        internal static string ResolveParamValue(Document doc, Element e, string name)
        {
            string Val(Parameter q) => q == null ? null : (q.AsString() ?? q.AsValueString());
            var v = Val(e?.LookupParameter(name));
            if (string.IsNullOrWhiteSpace(v))
            {
                var typeEl = e != null && e.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(e.GetTypeId()) : null;
                v = Val(typeEl?.LookupParameter(name));
            }
            return v ?? "";
        }

        // Robust category resolver — friendly name, OST_ enum, or live
        // Category.Name lookup (handles "Plumbing Fixtures" etc.).
        // internal (was private) so Mutators.SetCategoryVisibility reuses the same
        // friendly-name -> BuiltInCategory resolution the INSPECT tools use.
        internal static BuiltInCategory? ResolveCategoryRobust(Document doc, string category)
        {
            var simple = ResolveBuiltInCategory(category);
            if (simple != null) return simple;
            var compact = "OST_" + category.Replace(" ", "");
            foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, c);
                    if (cat == null) continue;
                    if (string.Equals(cat.Name, category, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ToString(), compact, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                catch { /* some BICs have no category in this doc */ }
            }
            return null;
        }

        // ─── list_phases ────────────────────────────────────────────────
        public static Dictionary<string, object?> ListPhases(Document doc)
        {
            var phases = new List<object>();
            foreach (Phase p in doc.Phases)
                phases.Add(new Dictionary<string, object?> { ["id"] = p.Id.Value, ["name"] = p.Name });
            return new Dictionary<string, object?> { ["ok"] = true, ["phases"] = phases, ["count"] = phases.Count };
        }

        // ─── list_design_options ────────────────────────────────────────
        public static Dictionary<string, object?> ListDesignOptions(Document doc)
        {
            var options = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption)).Cast<DesignOption>()
                .Select(o => new Dictionary<string, object?>
                {
                    ["id"] = o.Id.Value,
                    ["name"] = o.Name,
                    ["is_primary"] = o.IsPrimary,
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["design_options"] = options, ["count"] = options.Count };
        }

        // ─── list_rvt_links ─────────────────────────────────────────────
        public static Dictionary<string, object?> ListRvtLinks(Document doc)
        {
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(l =>
                {
                    var linkDoc = l.GetLinkDocument();
                    return new Dictionary<string, object?>
                    {
                        ["id"] = l.Id.Value,
                        ["name"] = l.Name,
                        ["loaded"] = linkDoc != null,
                        ["path"] = linkDoc?.PathName ?? "",
                    };
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["links"] = links, ["count"] = links.Count };
        }

        // ─── list_revisions ─────────────────────────────────────────────
        public static Dictionary<string, object?> ListRevisions(Document doc)
        {
            var revs = new FilteredElementCollector(doc)
                .OfClass(typeof(Revision)).Cast<Revision>()
                .OrderBy(r => r.SequenceNumber)
                .Select(r => new Dictionary<string, object?>
                {
                    ["id"] = r.Id.Value,
                    ["sequence"] = r.SequenceNumber,
                    ["date"] = r.RevisionDate,
                    ["description"] = r.Description,
                    ["issued"] = r.Issued,
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["revisions"] = revs, ["count"] = revs.Count };
        }

        // ─── list_model_groups ──────────────────────────────────────────
        public static Dictionary<string, object?> ListModelGroups(Document doc)
        {
            var groups = new FilteredElementCollector(doc)
                .OfClass(typeof(Group)).Cast<Group>()
                .Where(g => g.Category != null && g.Category.Id.Value == (long)BuiltInCategory.OST_IOSModelGroups)
                .GroupBy(g => g.GroupType?.Name ?? g.Name)
                .Select(grp => new Dictionary<string, object?>
                {
                    ["type_name"] = grp.Key,
                    ["instances"] = grp.Count(),
                    ["ids"] = grp.Select(g => g.Id.Value).ToList(),
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["groups"] = groups, ["count"] = groups.Count };
        }

        // ─── get_sheet_viewports ────────────────────────────────────────
        // args: sheet_id (long) OR sheet_number (string) — at least one.
        public static Dictionary<string, object?> GetSheetViewports(Document doc, JsonElement args)
        {
            ViewSheet? sheet = null;
            var sheetId = ArgsHelp.GetLong(args, "sheet_id");
            var sheetNumber = ArgsHelp.GetString(args, "sheet_number");
            if (sheetId.HasValue)
                sheet = doc.GetElement(new ElementId(sheetId.Value)) as ViewSheet;
            else if (!string.IsNullOrEmpty(sheetNumber))
                sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == sheetNumber);
            if (sheet == null)
                throw new InvalidOperationException("sheet not found — pass sheet_id or sheet_number (use list_sheets)");
            var viewports = sheet.GetAllViewports().Select(vpId =>
            {
                var vp = (Viewport)doc.GetElement(vpId);
                var view = doc.GetElement(vp.ViewId) as View;
                var center = vp.GetBoxCenter();
                return new Dictionary<string, object?>
                {
                    ["viewport_id"] = vp.Id.Value,
                    ["view_id"] = vp.ViewId.Value,
                    ["view_name"] = view?.Name ?? "",
                    ["center_mm"] = new[] { center.X * 304.8, center.Y * 304.8 },
                };
            }).ToList<object>();
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["sheet_number"] = sheet.SheetNumber, ["sheet_name"] = sheet.Name,
                ["viewports"] = viewports, ["count"] = viewports.Count,
            };
        }

        // ─── list_project_parameters ────────────────────────────────────
        public static Dictionary<string, object?> ListProjectParameters(Document doc)
        {
            var result = new List<object>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                var def = it.Key;
                var binding = it.Current;
                var cats = new List<string>();
                var catSet = (binding as ElementBinding)?.Categories;
                if (catSet != null)
                    foreach (Category c in catSet) cats.Add(c.Name);
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = def.Name,
                    ["is_instance"] = binding is InstanceBinding,
                    ["categories"] = cats,
                });
            }
            return new Dictionary<string, object?> { ["ok"] = true, ["parameters"] = result, ["count"] = result.Count };
        }

        // ─── get_type_parameters ────────────────────────────────────────
        // args: element_id (long) — instance or type id; resolves to the type.
        public static Dictionary<string, object?> GetTypeParameters(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new InvalidOperationException("element_id required");
            var el = doc.GetElement(new ElementId(id))
                ?? throw new InvalidOperationException($"element {id} not found");
            Element typeEl = el;
            if (el is not ElementType)
            {
                var typeId = el.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                    typeEl = doc.GetElement(typeId) ?? el;
            }
            var parameters = new List<object>();
            foreach (Parameter p in typeEl.Parameters)
            {
                parameters.Add(new Dictionary<string, object?>
                {
                    ["name"] = p.Definition?.Name ?? "",
                    ["value"] = p.StorageType switch
                    {
                        StorageType.String => p.AsString(),
                        StorageType.Integer => p.AsInteger(),
                        StorageType.Double => p.AsDouble(),   // Revit internal units
                        StorageType.ElementId => p.AsElementId().Value,
                        _ => p.AsValueString(),
                    },
                    ["display_value"] = p.AsValueString(),    // formatted in project units (metric)
                    ["read_only"] = p.IsReadOnly,
                });
            }
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["type_id"] = typeEl.Id.Value, ["type_name"] = typeEl.Name,
                ["parameters"] = parameters, ["count"] = parameters.Count,
            };
        }
    }
}
