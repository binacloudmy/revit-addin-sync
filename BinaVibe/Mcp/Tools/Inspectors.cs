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
using System.Text.RegularExpressions;
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
                    ["elevation"] = l.Elevation,               // legacy (feet) — kept
                    ["elevation_mm"] = Math.Round(l.Elevation * 304.8, 0),
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
            var bic = CategoryResolve.Resolve(category);
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

            var el = doc.GetElement(ElemIds.From(id));
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
            var bic = CategoryResolve.Resolve(category);
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
            const double ft2ToM2 = 0.09290304;

            const int Cap = 100;
            var matched = new List<Element>();
            foreach (var el in q)
            {
                if (!PredicateMatches(el, doc, predicate)) continue;
                matched.Add(el);
            }

            var matches = new List<object>();
            foreach (var el in matched.Take(Cap))
            {
                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;
                var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.LevelId) : null;

                // params dict — seeds the mirror with per-element attributes.
                var paramsDict = new Dictionary<string, object?>();
                if (isRooms && el is SpatialElement room)
                {
                    // Revit stores room Area in ft² (×0.09290304 → m²). Skip
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
            }

            var result = new Dictionary<string, object?>
            {
                ["category"] = category,
                ["predicate"] = predicate,
                ["matches"] = matches,
            };
            if (matched.Count > Cap)
                result["truncated"] = $"showing {Cap} of {matched.Count} matches — narrow the predicate";

            return result;
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
                    .Select(id => doc.GetElement(ElemIds.From(id)))
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
        /// Returns {ok, warnings:[{description, element_ids:[...]}], count,
        /// by_type:[{type, count, element_ids:[...]}]}.
        /// by_type is the CANONICAL grouping for "summarise/group warnings by type"
        /// asks — group by w.GetDescriptionText(), the real Revit warning-type
        /// string (do not invent categories or group by an invented "priority"
        /// bucket instead). Ordered by count desc; element_ids is the union of
        /// failing-element ids across every warning in that type's group.
        /// The flat `warnings` list is kept EXACTLY as before — callers depend on it.
        /// </summary>
        public static Dictionary<string, object?> GetModelWarnings(Document doc)
        {
            const int cap = 500;
            var warnings = doc.GetWarnings();
            var result = new List<object>();
            var byType = new Dictionary<string, (int Count, List<long> ElementIds)>();

            foreach (var w in warnings.Take(cap))
            {
                var eids = w.GetFailingElements()
                    .Select(eid => (object)eid.Value)
                    .ToList<object>();
                var description = w.GetDescriptionText();
                result.Add(new Dictionary<string, object?>
                {
                    ["description"] = description,
                    ["element_ids"] = eids,
                });

                if (!byType.TryGetValue(description, out var group))
                {
                    group = (0, new List<long>());
                }
                group.Count++;
                foreach (var eid in w.GetFailingElements())
                {
                    if (!group.ElementIds.Contains(eid.Value))
                        group.ElementIds.Add(eid.Value);
                }
                byType[description] = group;
            }

            var byTypeList = byType
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => (object)new Dictionary<string, object?>
                {
                    ["type"] = kv.Key,
                    ["count"] = kv.Value.Count,
                    ["element_ids"] = kv.Value.ElementIds.Cast<object>().ToList(),
                })
                .ToList<object>();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["warnings"] = result,
                ["count"] = result.Count,
                ["by_type"] = byTypeList,
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

            var bic = CategoryResolve.Resolve(category);
            var col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue) col = col.OfCategory(bic.Value);
            IEnumerable<Element> q = col;

            const int cap = 100;
            var elements = new List<object>();
            var matchedTotal = 0;

            foreach (var el in q)
            {
                bool passes = conditions.Count == 0
                    ? true
                    : matchAll
                        ? conditions.All(c => EvaluateCondition(el, doc, c.param, c.op, c.value))
                        : conditions.Any(c => EvaluateCondition(el, doc, c.param, c.op, c.value));

                if (!passes) continue;
                matchedTotal++;
                if (elements.Count >= cap) continue;   // keep counting, stop shaping

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

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["elements"] = elements,
                ["count"] = elements.Count,
            };
            if (matchedTotal > cap)
                result["truncated"] = $"showing {cap} of {matchedTotal} matches — narrow the conditions";
            return result;
        }

        // ─── condition evaluator ─────────────────────────────────────────
        // Display-form string for a parameter: Double params render via
        // AsValueString (project units, e.g. "3.000 m") — SafeParamValue would
        // stringify the rich dict. Shared by EvaluateCondition + PredicateMatches.
        private static string DisplayFormString(Parameter p)
            => p.StorageType == StorageType.Double
                ? (p.AsValueString() ?? "")
                : (SafeParamValue(p)?.ToString() ?? "");

        private static bool EvaluateCondition(Element el, Document doc, string paramName, string op, string wantStr)
        {
            var p = el.LookupParameter(paramName);
            if (p == null) return false;

            // Display-form string for string ops (contains/equals). For Double
            // params AsValueString is the project-units rendering ("3.000 m");
            var rawStr = DisplayFormString(p);

            // Try numeric comparison first when both sides coerce.
            if (double.TryParse(wantStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var wantNum))
            {
                double? actualNum = null;
                if (p.StorageType == StorageType.Double)
                    // Compare in PROJECT DISPLAY UNITS: the drafter/model says
                    // "< 3.0" meaning metres (what Revit shows), not internal
                    // feet. This was the 2026-07 UAT wrong-answer bug: 2.6m
                    // rooms arrived as 8.53 (ft) and passed "< 3.0" = false.
                    actualNum = ParamUnits.ToDisplay(doc, p, p.AsDouble());
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

        // ─── Unit handling ──────────────────────────────────────────────
        // Unit handling for parameter VALUES. Revit stores Double params in
        // internal units (feet/ft²/ft³/radians). Two consumers:
        //   MetricTwin  — fixed metric conversions for the value_mm/value_m2/...
        //                 return twins (predictable keys the model can rely on).
        //   ToDisplay   — project display units, used by numeric condition
        //                 compares so "3.0" means what the drafter sees in Revit.
        internal static class ParamUnits
        {
            public static (string suffix, double factor)? MetricTwin(Definition def)
            {
                ForgeTypeId? spec;
                try { spec = def.GetDataType(); } catch { return null; }
                if (spec == null) return null;
                if (spec.Equals(SpecTypeId.Length)) return ("value_mm", 304.8);
                if (spec.Equals(SpecTypeId.Area)) return ("value_m2", 0.09290304);
                if (spec.Equals(SpecTypeId.Volume)) return ("value_m3", 0.028316846592);
                if (spec.Equals(SpecTypeId.Angle)) return ("value_deg", 180.0 / System.Math.PI);
                return null;
            }

            // Convert a DISPLAY-units double (what the drafter/model says: mm/m per
        // project settings) to Revit-internal units for writes. Inverse of
        // ToDisplay — keeps the write contract symmetric with reads/compares.
        // (UAT 2026-07-11: set_parameter "2600" landed as 2600 FEET = 792m
        // ceiling; the copilot itself diagnosed it from the value_mm twins.)
        public static double ToInternal(Document doc, Parameter p, double displayVal)
        {
            try
            {
                var spec = p.Definition.GetDataType();
                if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return displayVal;
                var unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                return UnitUtils.ConvertToInternalUnits(displayVal, unit);
            }
            catch { return displayVal; }
        }

        // Convert an internal-units double to the PROJECT's display units for
            // this parameter's spec (e.g. metres on JKR templates). Falls back to
            // the raw value when the spec has no unit formatting (plain Number).
            public static double ToDisplay(Document doc, Parameter p, double internalVal)
            {
                try
                {
                    var spec = p.Definition.GetDataType();
                    if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return internalVal;
                    var unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                    return UnitUtils.ConvertFromInternalUnits(internalVal, unit);
                }
                catch { return internalVal; }
            }
        }

        private static object? SafeParamValue(Parameter p)
        {
            try
            {
                return p.StorageType switch
                {
                    StorageType.String => p.AsString(),
                    StorageType.Integer => p.AsInteger(),
                    StorageType.Double => RichDouble(p),
                    StorageType.ElementId => p.AsElementId().Value,
                    _ => p.AsValueString(),
                };
            }
            catch { return null; }
        }

        // Double params carry Revit-internal units — emit the raw value PLUS
        // a fixed-metric twin and the project-units display string, so the
        // model never has to guess units (the 2026-07 UAT wrong-answer class).
        private static Dictionary<string, object?> RichDouble(Parameter p)
        {
            var raw = p.AsDouble();
            var d = new Dictionary<string, object?>
            {
                ["value"] = raw,
                ["display_value"] = p.AsValueString(),
            };
            var twin = ParamUnits.MetricTwin(p.Definition);
            if (twin.HasValue)
                d[twin.Value.suffix] = System.Math.Round(raw * twin.Value.factor, 4);
            return d;
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
                return string.Equals(DisplayFormString(p), want, System.StringComparison.OrdinalIgnoreCase);
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

        // Category-name resolution moved to CategoryResolve.Resolve (ElementFilter.cs)
        // so filter_elements can share the same friendly-name/OST_ logic —
        // one implementation, no copies. See that class for the original
        // comment on why unknown categories must fail loudly.

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
                        ids.Add(ElemIds.From(v));
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
            var simple = CategoryResolve.Resolve(category);
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
            int i = 0;
            foreach (Phase p in doc.Phases)
            {
                phases.Add(new Dictionary<string, object?> { ["id"] = p.Id.Value, ["name"] = p.Name, ["sequence"] = i });
                i++;
            }
            return new Dictionary<string, object?> { ["ok"] = true, ["phases"] = phases, ["count"] = phases.Count };
        }

        // ─── list_design_options ────────────────────────────────────────
        public static Dictionary<string, object?> ListDesignOptions(Document doc)
        {
            var options = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption)).Cast<DesignOption>()
                .Select(o =>
                {
                    var optionSetId = o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
                    var optionSet = (optionSetId != null && optionSetId != ElementId.InvalidElementId)
                        ? doc.GetElement(optionSetId)?.Name ?? ""
                        : "";
                    return new Dictionary<string, object?>
                    {
                        ["id"] = o.Id.Value,
                        ["name"] = o.Name,
                        ["is_primary"] = o.IsPrimary,
                        ["option_set"] = optionSet,
                    };
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
            var modelGroups = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Group)).Cast<Autodesk.Revit.DB.Group>()
                .Where(g => g.Category != null && g.Category.Id.Value == (long)BuiltInCategory.OST_IOSModelGroups)
                .GroupBy(g => g.GroupType?.Name ?? g.Name)
                .Select(grp => new Dictionary<string, object?>
                {
                    ["name"] = grp.Key,
                    ["instances"] = grp.Count(),
                    ["instance_details"] = grp.Select(g => new Dictionary<string, object?>
                    {
                        ["id"] = g.Id.Value,
                        ["members"] = g.GetMemberIds().Count,
                    }).ToList<object>(),
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["model_groups"] = modelGroups, ["count"] = modelGroups.Count };
        }

        // ─── get_sheet_viewports ────────────────────────────────────────
        // args: sheet_id (long) OR sheet_number (string) — scopes to one sheet.
        // Omit both to return every sheet in the project.
        public static Dictionary<string, object?> GetSheetViewports(Document doc, JsonElement args)
        {
            var sheetId = ArgsHelp.GetLong(args, "sheet_id");
            var sheetNumber = ArgsHelp.GetString(args, "sheet_number");
            List<ViewSheet> sheets;
            if (sheetId.HasValue)
            {
                var sheet = doc.GetElement(ElemIds.From(sheetId.Value)) as ViewSheet;
                if (sheet == null)
                    throw new InvalidOperationException($"sheet {sheetId.Value} not found — use list_sheets");
                sheets = new List<ViewSheet> { sheet };
            }
            else if (!string.IsNullOrEmpty(sheetNumber))
            {
                var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == sheetNumber);
                if (sheet == null)
                    throw new InvalidOperationException($"sheet '{sheetNumber}' not found — use list_sheets");
                sheets = new List<ViewSheet> { sheet };
            }
            else
            {
                sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            }

            var sheetResults = sheets.Select(sheet => new Dictionary<string, object?>
            {
                ["sheet_number"] = sheet.SheetNumber,
                ["sheet_name"] = sheet.Name,
                ["viewports"] = sheet.GetAllViewports().Select(vpId =>
                {
                    var vp = (Viewport)doc.GetElement(vpId);
                    var view = doc.GetElement(vp.ViewId) as View;
                    var center = vp.GetBoxCenter();
                    return new Dictionary<string, object?>
                    {
                        ["viewport_id"] = vp.Id.Value,
                        ["view_id"] = vp.ViewId.Value,
                        ["view_name"] = view?.Name ?? "",
                        ["view_type"] = view?.ViewType.ToString() ?? "",
                        ["center_mm"] = new[] { center.X * 304.8, center.Y * 304.8 },
                    };
                }).ToList<object>(),
            }).ToList<object>();

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["sheets"] = sheetResults, ["count"] = sheetResults.Count,
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
                    ["data_type"] = def.GetDataType()?.TypeId ?? "",
                });
            }
            return new Dictionary<string, object?> { ["ok"] = true, ["project_parameters"] = result, ["count"] = result.Count };
        }

        // ─── get_type_parameters ────────────────────────────────────────
        // args: element_id (long) — instance or type id; resolves to the type.
        public static Dictionary<string, object?> GetTypeParameters(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new InvalidOperationException("element_id required");
            var el = doc.GetElement(ElemIds.From(id))
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
                    ["value"] = SafeParamValue(p),
                    ["display_value"] = p.AsValueString(),
                    ["read_only"] = p.IsReadOnly,
                });
            }
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["type_id"] = typeEl.Id.Value, ["type_name"] = typeEl.Name,
                ["family_name"] = (typeEl as ElementType)?.FamilyName ?? "",
                ["parameters"] = parameters, ["count"] = parameters.Count,
            };
        }

        // ─── list_rooms ─────────────────────────────────────────────────
        // adapted from mcp-servers-for-revit (MIT) — ExportRoomDataEventHandler.cs
        // Their core returns feet-based Area/Volume/Perimeter; we convert to
        // metric at the boundary per the mm-first units policy.
        public static Dictionary<string, object?> ListRooms(Document doc, JsonElement args)
        {
            const double Ft2ToM2 = 0.09290304;
            const double Ft3ToM3 = 0.028316846592;
            const double FtToM = 0.3048;
            var levelFilter = ArgsHelp.GetString(args, "level");
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Autodesk.Revit.DB.Architecture.Room>()
                .Where(r => levelFilter == null ||
                            string.Equals(r.Level?.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
                .Select(r => new Dictionary<string, object?>
                {
                    ["id"] = r.Id.Value,
                    ["number"] = r.Number,
                    ["name"] = r.Name,
                    ["level"] = r.Level?.Name ?? "",
                    ["area_m2"] = Math.Round(r.Area * Ft2ToM2, 2),
                    ["perimeter_m"] = Math.Round(r.Perimeter * FtToM, 2),
                    ["volume_m3"] = Math.Round(r.Volume * Ft3ToM3, 2),
                    ["placed"] = r.Area > 0,   // unplaced/unenclosed rooms report zero area
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["rooms"] = rooms, ["count"] = rooms.Count };
        }

        // ─── find_duplicate_walls ────────────────────────────────────────
        /// <summary>
        /// Deterministic duplicate/overlap classification for host walls
        /// (Line or Arc LocationCurve). Ground truth for ClickUp P1
        /// 86ey84zpn: Revit's "walls overlap" model warning fires for ANY
        /// curve overlap — partial included — and the agent was reading
        /// that warning text and self-judging "duplicate", nearly deleting
        /// legitimately overlapping (but non-duplicate) walls. This tool
        /// does the geometry compare itself, in C#, and returns three
        /// CATEGORICALLY DISJOINT buckets — a given wall pair lands in
        /// exactly one of them, never more:
        ///   exact_duplicates              — same curve + type + level + base
        ///                                   offset. Safe auto-delete candidates
        ///                                   (keep the lowest element id).
        ///   partial_overlaps              — collinear Line walls with a real
        ///                                   but partial 1D overlap. NEVER a
        ///                                   safe bulk-delete — stacked/joined
        ///                                   walls are legitimate design.
        ///   same_location_different_type  — identical curve, but different
        ///                                   WallType or base offset (e.g. a
        ///                                   partition redrawn in a different
        ///                                   type on top of the original).
        /// INVARIANT (canonicalize-to-keep, exact wins): every wall id
        /// appears in at most one bucket, EXCEPT keep_ids, which may also
        /// appear in partial/same-location entries — a keep_id survives the
        /// delete, so manual-review flags on it stay meaningful. delete_ids
        /// appear ONLY in exact_duplicates: partial and same-location
        /// entries are emitted with every exact-group member remapped to its
        /// group's keep_id, then deduped, with self-pairs and sub-2-member
        /// groups dropped.
        /// Tolerance: 5mm (converted to feet internally: 5/304.8).
        /// Arc walls only ever land in exact_duplicates or
        /// same_location_different_type — arc partial-overlap detection is
        /// intentionally out of scope (YAGNI; no UAT case has needed it) and
        /// arcs never enter the partial_overlaps pass.
        /// Perf: walls_scanned is capped at 5000 (returns capped:true beyond
        /// that — audit a smaller model/link, this isn't meant for a
        /// federated master). Line walls are bucketed by (level, quantized
        /// 1-degree direction) before the pairwise compare so a normal floor
        /// plan doesn't do an O(n^2) sweep across the whole model — only
        /// within same-level, near-parallel candidates (±1 bucket to absorb
        /// bucket-boundary rounding; the actual parallel/collinear tests
        /// below are exact, the bucket only narrows candidates).
        /// </summary>
        public static Dictionary<string, object?> FindDuplicateWalls(Document doc)
        {
            const double TolFt = 5.0 / 304.8;                 // 5mm, in feet
            const double ParallelSin = 0.0017453283;           // sin(0.1 deg) — cross-product parallel gate
            const int WallCap = 5000;

            var rawWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall)).Cast<Wall>()
                .ToList();

            bool capped = rawWalls.Count > WallCap;
            if (capped) rawWalls = rawWalls.Take(WallCap).ToList();

            // Snapshot — guard every field the classifier needs. Walls with no
            // LocationCurve (some in-place/stacked-wall members) or a curve
            // that's neither Line nor Arc (splines) are out of scope — skip.
            var snaps = new List<DupWallSnap>();
            foreach (var w in rawWalls)
            {
                if (!(w.Location is LocationCurve lc) || lc.Curve == null) continue;
                var curve = lc.Curve;
                if (!(curve is Line) && !(curve is Arc)) continue;

                double baseOffset = 0;
                try { baseOffset = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0; } catch { }
                // Captured per snapshot contract but deliberately NOT part of any
                // equality gate below: Unconnected Height is unreliable for
                // walls whose Top is constrained to a level ("Up to level") —
                // Revit still reports a (stale/cached) number for it, so
                // gating exact_duplicate on it would risk FALSE NEGATIVES
                // (missing real duplicates) rather than the false-positive
                // bug this tool exists to fix.
                double unconnectedHeight = 0;
                try { unconnectedHeight = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0; } catch { }

                var typeId = w.GetTypeId();
                var typeName = typeId.Value != ElementId.InvalidElementId.Value
                    ? (doc.GetElement(typeId) as ElementType)?.Name ?? "" : "";
                var levelName = w.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(w.LevelId)?.Name ?? "" : "";

                snaps.Add(new DupWallSnap
                {
                    Id = w.Id.Value,
                    TypeId = typeId.Value,
                    TypeName = typeName,
                    LevelId = w.LevelId.Value,
                    LevelName = levelName,
                    BaseOffset = baseOffset,
                    UnconnectedHeightFt = unconnectedHeight,
                    Curve = curve,
                    LengthFt = curve.Length,
                });
            }

            int n = snaps.Count;
            var exactParent = DsuMake(n);
            var sameLocParent = DsuMake(n);
            var partialPairs = new List<(int i, int j, double overlapFt)>();

            // Same-level candidate scoping ("different levels = never overlap").
            var byLevel = new Dictionary<long, List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (!byLevel.TryGetValue(snaps[i].LevelId, out var list))
                { list = new List<int>(); byLevel[snaps[i].LevelId] = list; }
                list.Add(i);
            }

            foreach (var group in byLevel.Values)
            {
                var arcIdxs = group.Where(i => snaps[i].Curve is Arc).ToList();
                var lineIdxs = group.Where(i => snaps[i].Curve is Line).ToList();

                // ── Arc-Arc: exact-duplicate / same-location only. ──
                for (int a = 0; a < arcIdxs.Count; a++)
                {
                    for (int b = a + 1; b < arcIdxs.Count; b++)
                    {
                        int i = arcIdxs[a], j = arcIdxs[b];
                        if (!ArcsEqual(snaps[i], snaps[j], TolFt)) continue;
                        if (SameTypeAndOffset(snaps[i], snaps[j], TolFt)) DsuUnion(exactParent, i, j);
                        else DsuUnion(sameLocParent, i, j);
                    }
                }

                // ── Line-Line: bucket by 1-degree direction, then pairwise. ──
                var angleDeg = new Dictionary<int, double>();
                var buckets = new Dictionary<int, List<int>>();
                foreach (var i in lineIdxs)
                {
                    var dir = ((Line)snaps[i].Curve).Direction.Normalize();
                    double ang = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
                    if (ang < 0) ang += 180.0;
                    if (ang >= 180.0) ang -= 180.0;
                    angleDeg[i] = ang;
                    int bucket = (int)Math.Floor(ang);
                    if (!buckets.TryGetValue(bucket, out var list)) { list = new List<int>(); buckets[bucket] = list; }
                    list.Add(i);
                }

                foreach (var i in lineIdxs)
                {
                    int bucket = (int)Math.Floor(angleDeg[i]);
                    var candidates = new List<int>();
                    for (int d = -1; d <= 1; d++)
                    {
                        int bk = ((bucket + d) % 180 + 180) % 180;
                        if (buckets.TryGetValue(bk, out var list)) candidates.AddRange(list);
                    }

                    foreach (var j in candidates)
                    {
                        if (j <= i) continue; // undirected pair, once each
                        if (!LinesParallel(snaps[i], snaps[j], ParallelSin)) continue;

                        if (LinesEqualEndpoints(snaps[i], snaps[j], TolFt))
                        {
                            if (SameTypeAndOffset(snaps[i], snaps[j], TolFt)) DsuUnion(exactParent, i, j);
                            else DsuUnion(sameLocParent, i, j);
                            continue; // exact/same-location pairs never also count as partial
                        }

                        if (!LinesCollinear(snaps[i], snaps[j], TolFt)) continue;
                        double overlapFt = ProjectedOverlap(snaps[i], snaps[j]);
                        if (overlapFt > TolFt)
                            partialPairs.Add((i, j, overlapFt));
                    }
                }
            }

            // ── Materialize groups ──
            // Canonicalize-to-keep, EXACT WINS: without this, disjointness
            // would only hold per-PAIR, not per-WALL. Failure case: walls A,B
            // same type+curve (exact) and C same curve but different offset —
            // the A-C and B-C pairs would put BOTH A and B into
            // same_location_different_type, so B would simultaneously be an
            // auto-delete candidate (delete_ids) AND "semak manual". Fix:
            // every exact-group member id maps to its group's keep_id before
            // partial/same-location entries are emitted; collapsed duplicates
            // are deduped and self-pairs dropped.
            var exactDuplicates = new List<object>();
            var canon = new int[n];                       // snapshot idx -> exact-group keep idx (or self)
            for (int i = 0; i < n; i++) canon[i] = i;
            foreach (var grp in GroupByRoot(exactParent, n).Where(g => g.Count >= 2))
            {
                // NOTE: IEnumerable<long> has no implicit conversion to
                // IEnumerable<object> (generic covariance excludes value
                // types) — box each id via Select(id => (object)id) before
                // ToList(), same as everywhere else below.
                int keepIdx = grp.OrderBy(i => snaps[i].Id).First();
                foreach (var m in grp) canon[m] = keepIdx;
                var idsLong = grp.Select(i => snaps[i].Id).OrderBy(id => id).ToList();
                var first = snaps[keepIdx];
                exactDuplicates.Add(new Dictionary<string, object?>
                {
                    ["keep_id"] = idsLong[0],
                    ["delete_ids"] = idsLong.Skip(1).Select(id => (object)id).ToList(),
                    ["type_name"] = first.TypeName,
                    ["level"] = first.LevelName,
                    ["length_mm"] = Math.Round(first.LengthFt * 304.8, 0),
                });
            }

            var sameLocationGroups = new List<object>();
            var seenSameLoc = new HashSet<string>();       // dedupe groups that collapse identically
            foreach (var grp in GroupByRoot(sameLocParent, n).Where(g => g.Count >= 2))
            {
                var canonIdxs = grp.Select(i => canon[i]).Distinct().ToList();
                if (canonIdxs.Count < 2) continue;         // collapsed to one wall — not a group
                var idsLong = canonIdxs.Select(i => snaps[i].Id).OrderBy(id => id).ToList();
                var sig = string.Join(",", idsLong);
                if (!seenSameLoc.Add(sig)) continue;
                var typeNames = canonIdxs.Select(i => snaps[i].TypeName).Distinct().ToList<object>();
                sameLocationGroups.Add(new Dictionary<string, object?>
                {
                    ["ids"] = idsLong.Select(id => (object)id).ToList(),
                    ["type_names"] = typeNames,
                });
            }

            // Partial pairs: canonicalize both ends, drop self-pairs (both
            // ends collapsed into the same exact group), dedupe collapsed
            // pairs keeping the LARGEST overlap (most conservative flag).
            var partialByPair = new Dictionary<(int lo, int hi), double>();
            foreach (var p in partialPairs)
            {
                int ci = canon[p.i], cj = canon[p.j];
                if (ci == cj) continue;
                var key = ci < cj ? (lo: ci, hi: cj) : (lo: cj, hi: ci);
                if (!partialByPair.TryGetValue(key, out var best) || p.overlapFt > best)
                    partialByPair[key] = p.overlapFt;
            }
            var partialOverlaps = partialByPair
                .OrderBy(kv => snaps[kv.Key.lo].Id).ThenBy(kv => snaps[kv.Key.hi].Id)
                .Select(kv => (object)new Dictionary<string, object?>
                {
                    ["ids"] = new List<long> { snaps[kv.Key.lo].Id, snaps[kv.Key.hi].Id }
                        .OrderBy(id => id).Select(id => (object)id).ToList(),
                    ["overlap_mm"] = Math.Round(kv.Value * 304.8, 0),
                    ["type_names"] = new List<object> { snaps[kv.Key.lo].TypeName, snaps[kv.Key.hi].TypeName },
                })
                .ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["exact_duplicates"] = exactDuplicates,
                ["partial_overlaps"] = partialOverlaps,
                ["same_location_different_type"] = sameLocationGroups,
                ["counts"] = new Dictionary<string, object?>
                {
                    ["exact_groups"] = exactDuplicates.Count,
                    ["partial_pairs"] = partialOverlaps.Count,
                    ["same_location_groups"] = sameLocationGroups.Count,
                    ["walls_scanned"] = snaps.Count,
                },
                ["capped"] = capped,
            };
        }

        // ─── find_duplicate_walls helpers ────────────────────────────────

        private sealed class DupWallSnap
        {
            public long Id;
            public long TypeId;
            public string TypeName = "";
            public long LevelId;
            public string LevelName = "";
            public double BaseOffset;
            public double UnconnectedHeightFt; // captured, intentionally not gated — see comment at snapshot build site
            public Curve Curve = null!;
            public double LengthFt;
        }

        private static bool SameTypeAndOffset(DupWallSnap a, DupWallSnap b, double tolFt)
            => a.TypeId == b.TypeId && Math.Abs(a.BaseOffset - b.BaseOffset) < tolFt;

        private static bool LinesParallel(DupWallSnap a, DupWallSnap b, double parallelSin)
        {
            var da = ((Line)a.Curve).Direction.Normalize();
            var db = ((Line)b.Curve).Direction.Normalize();
            return da.CrossProduct(db).GetLength() < parallelSin;
        }

        private static bool LinesCollinear(DupWallSnap a, DupWallSnap b, double tolFt)
        {
            var la = (Line)a.Curve;
            var lb = (Line)b.Curve;
            var da = la.Direction.Normalize();
            var v = lb.GetEndPoint(0) - la.GetEndPoint(0);
            var perp = v - da.Multiply(v.DotProduct(da));
            return perp.GetLength() < tolFt;
        }

        private static bool LinesEqualEndpoints(DupWallSnap a, DupWallSnap b, double tolFt)
        {
            var la = (Line)a.Curve;
            var lb = (Line)b.Curve;
            var a0 = la.GetEndPoint(0); var a1 = la.GetEndPoint(1);
            var b0 = lb.GetEndPoint(0); var b1 = lb.GetEndPoint(1);
            bool direct = a0.DistanceTo(b0) < tolFt && a1.DistanceTo(b1) < tolFt;
            bool reversed = a0.DistanceTo(b1) < tolFt && a1.DistanceTo(b0) < tolFt;
            return direct || reversed;
        }

        // Projected 1D overlap length (feet) of two collinear lines, measured
        // along wall a's direction. Positive => real overlap; <= 0 => no
        // overlap or corner-touching only (excluded by the TolFt> check).
        private static double ProjectedOverlap(DupWallSnap a, DupWallSnap b)
        {
            var la = (Line)a.Curve;
            var lb = (Line)b.Curve;
            var da = la.Direction.Normalize();
            var origin = la.GetEndPoint(0);
            double t0 = 0.0, t1 = (la.GetEndPoint(1) - origin).DotProduct(da);
            double s0 = (lb.GetEndPoint(0) - origin).DotProduct(da);
            double s1 = (lb.GetEndPoint(1) - origin).DotProduct(da);
            double tMin = Math.Min(t0, t1), tMax = Math.Max(t0, t1);
            double sMin = Math.Min(s0, s1), sMax = Math.Max(s0, s1);
            return Math.Min(tMax, sMax) - Math.Max(tMin, sMin);
        }

        private static bool ArcsEqual(DupWallSnap a, DupWallSnap b, double tolFt)
        {
            var arcA = (Arc)a.Curve;
            var arcB = (Arc)b.Curve;
            if (arcA.Center.DistanceTo(arcB.Center) >= tolFt) return false;
            if (Math.Abs(arcA.Radius - arcB.Radius) >= tolFt) return false;
            var a0 = arcA.GetEndPoint(0); var a1 = arcA.GetEndPoint(1);
            var b0 = arcB.GetEndPoint(0); var b1 = arcB.GetEndPoint(1);
            bool direct = a0.DistanceTo(b0) < tolFt && a1.DistanceTo(b1) < tolFt;
            bool reversed = a0.DistanceTo(b1) < tolFt && a1.DistanceTo(b0) < tolFt;
            return direct || reversed;
        }

        // ── tiny union-find (disjoint set) over snapshot indices ──
        private static int[] DsuMake(int n)
        {
            var p = new int[n];
            for (int i = 0; i < n; i++) p[i] = i;
            return p;
        }

        private static int DsuFind(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        private static void DsuUnion(int[] p, int a, int b)
        {
            int ra = DsuFind(p, a), rb = DsuFind(p, b);
            if (ra != rb) p[ra] = rb;
        }

        private static List<List<int>> GroupByRoot(int[] p, int n)
        {
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = DsuFind(p, i);
                if (!groups.TryGetValue(root, out var list)) { list = new List<int>(); groups[root] = list; }
                list.Add(i);
            }
            return groups.Values.ToList();
        }

        // ─── audit_parameters ───────────────────────────────────────────
        // Bulk fill-rate matrix for ALL instances of a category — the one-call
        // answer to "check every parameter on every door" audits. Read-only.
        public static Dictionary<string, object?> AuditParameters(Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category")
                ?? throw new InvalidOperationException("category required (e.g. Doors / OST_Doors)");
            var bic = CategoryResolve.Resolve(category)
                ?? throw new InvalidOperationException($"unknown category '{category}'");
            var groupFilter = ArgsHelp.GetString(args, "group");           // e.g. "Data"
            var nameFilter = ArgsHelp.GetStringList(args, "param_names");  // optional explicit list

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic).WhereElementIsNotElementType()
                .ToElements();
            if (elements.Count > 2000)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"category too large ({elements.Count} instances) — audit a more specific category, or check one parameter with find_missing_parameter",
                };

            // stats[param] = (group, filled, empty, perType[typeName] = (filled, total))
            var stats = new Dictionary<string, (string group, int filled, int empty,
                Dictionary<string, (int filled, int total)> perType)>();

            foreach (var el in elements)
            {
                var typeName = (doc.GetElement(el.GetTypeId()) as ElementType)?.Name ?? "(no type)";
                foreach (Parameter p in el.Parameters)
                {
                    var def = p.Definition;
                    if (def == null) continue;
                    string grp;
                    try { grp = LabelUtils.GetLabelForGroup(def.GetGroupTypeId()); }
                    catch { grp = ""; }
                    if (groupFilter != null &&
                        !string.Equals(grp, groupFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (nameFilter.Count > 0 &&
                        !nameFilter.Any(n => string.Equals(n, def.Name, StringComparison.OrdinalIgnoreCase))) continue;

                    bool filled = p.HasValue &&
                        !(p.StorageType == StorageType.String && string.IsNullOrWhiteSpace(p.AsString()));

                    if (!stats.TryGetValue(def.Name, out var s))
                        s = (grp, 0, 0, new Dictionary<string, (int, int)>());
                    if (filled) s.filled++; else s.empty++;
                    var t = s.perType.TryGetValue(typeName, out var tv) ? tv : (0, 0);
                    s.perType[typeName] = (t.Item1 + (filled ? 1 : 0), t.Item2 + 1);
                    stats[def.Name] = s;
                }
            }

            var parameters = stats
                .OrderBy(kv => kv.Value.group).ThenBy(kv => kv.Key)
                .Select(kv =>
                {
                    var total = kv.Value.filled + kv.Value.empty;
                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = kv.Key,
                        ["group"] = kv.Value.group,
                        ["filled"] = kv.Value.filled,
                        ["empty"] = kv.Value.empty,
                        ["fill_rate"] = total == 0 ? 0 : Math.Round((double)kv.Value.filled / total, 3),
                    };
                    // Per-type breakdown ONLY when partially filled — the
                    // "Saiz_Fizikal only on D3/D11" pattern.
                    if (kv.Value.filled > 0 && kv.Value.empty > 0)
                        entry["partial_by_type"] = kv.Value.perType
                            .Select(t => new Dictionary<string, object?>
                            {
                                ["type_name"] = t.Key,
                                ["filled"] = t.Value.filled,
                                ["total"] = t.Value.total,
                            }).ToList<object>();
                    return (object)entry;
                }).ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["category"] = category,
                ["total_elements"] = elements.Count,
                ["parameters"] = parameters, ["count"] = parameters.Count,
            };
        }

        // ─── audit_view_names ───────────────────────────────────────────
        // Deterministic view-name compliance: the LLM must NEVER eyeball view
        // names (measured ~88% false-positive rate + a missed seeded
        // 'FloorPlan_final_FINAL' violation when it did). The caller supplies
        // ONE .NET regex derived from the stated naming convention; every
        // audited view is FULL-NAME matched — the tool wraps the pattern as
        // ^(?:pattern)$ itself, so full-match semantics are guaranteed here
        // and never delegated back to the (unreliable) calling model. The
        // wrap is unconditional: ^(?:^A$)$ still matches exactly like ^A$,
        // and detecting "already anchored" is a trap (alternation like A|B
        // makes prefix/suffix checks wrong).
        //
        // Default audit scope = plans/ceilings/sections/area/engineering
        // plans (the surfaces the ClickUp fix line calls out). Elevation /
        // ThreeD / DraftingView / Detail are real model views but sit outside
        // that default — they're reported separately as `other_model_views`,
        // never counted as violations, so callers can still see them without
        // the tool silently making a scope decision for the user.
        //
        // Schedules, legends, sheets and view templates are excluded
        // entirely (they carry their own naming/numbering conventions) —
        // only their counts are returned, never rows.
        public static Dictionary<string, object?> AuditViewNames(Document doc, JsonElement args)
        {
            var pattern = ArgsHelp.GetString(args, "pattern");
            if (string.IsNullOrEmpty(pattern))
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "pattern is required — a .NET regex; the tool matches it against the FULL view name (anchoring is applied by the tool)",
                };

            Regex regex;
            try
            {
                // Full-name match is guaranteed HERE (unconditional ^(?:...)$
                // wrap) — never rely on the caller to anchor. 2s timeout
                // guards against catastrophic backtracking in a
                // caller-supplied pattern.
                regex = new Regex("^(?:" + pattern + ")$", RegexOptions.None, TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"invalid regex: {ex.Message}" };
            }

            var includeViewTypes = ArgsHelp.GetStringList(args, "include_view_types");

            var defaultScope = new HashSet<ViewType>
            {
                ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan,
                ViewType.EngineeringPlan, ViewType.Section,
            };
            var otherModelViewTypes = new HashSet<ViewType>
            {
                ViewType.Elevation, ViewType.ThreeD, ViewType.DraftingView, ViewType.Detail,
            };

            // Excluded-by-design buckets (never audited, counts only). The
            // loop below skips these BEFORE the scope check, so an
            // include_view_types entry naming one of them could never audit
            // anything — validated and rejected loudly below instead of
            // silently returning audited:0 forever.
            var scheduleTypes = new HashSet<ViewType> { ViewType.Schedule, ViewType.ColumnSchedule, ViewType.PanelSchedule };
            var systemTypes = new HashSet<ViewType> { ViewType.Internal, ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Undefined };
            var alwaysExcluded = new HashSet<ViewType>(scheduleTypes.Concat(systemTypes))
            {
                ViewType.DrawingSheet, ViewType.Legend,
            };
            var auditableNames = string.Join(", ",
                Enum.GetNames(typeof(ViewType))
                    .Where(n => Enum.TryParse<ViewType>(n, out var t) && !alwaysExcluded.Contains(t))
                    .OrderBy(n => n));

            HashSet<ViewType> auditScope;
            bool usingDefaultScope = includeViewTypes.Count == 0;
            if (usingDefaultScope)
            {
                auditScope = defaultScope;
            }
            else
            {
                auditScope = new HashSet<ViewType>();
                var unknown = new List<string>();
                foreach (var name in includeViewTypes)
                {
                    if (!Enum.TryParse<ViewType>(name, ignoreCase: false, out var vt) || !Enum.IsDefined(typeof(ViewType), vt))
                    {
                        unknown.Add(name);
                        continue;
                    }
                    if (alwaysExcluded.Contains(vt))
                        return new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["error"] = $"view type '{name}' is excluded from naming audits (schedules/legends/sheets/system); auditable types: {auditableNames}",
                        };
                    auditScope.Add(vt);
                }
                if (unknown.Count > 0)
                {
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = $"unknown view type(s): {string.Join(", ", unknown)} — auditable types: {auditableNames}",
                    };
                }
            }
            var auditedScopeNames = auditScope.Select(v => v.ToString()).OrderBy(n => n).ToList<object>();

            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();

            int excludedSchedules = 0, excludedLegends = 0, excludedSheets = 0, excludedTemplates = 0, excludedOther = 0;
            int compliantCount = 0, auditedCount = 0;
            var violations = new List<object>();
            var otherModelViews = new List<object>();
            const int OtherCap = 50;
            bool otherCapped = false;

            foreach (var v in allViews)
            {
                if (v.IsTemplate) { excludedTemplates++; continue; }

                var vt = v.ViewType;
                // Sheets/schedules are reachable via View subclasses (ViewSheet,
                // ViewSchedule) as well as via ViewType — belt and suspenders.
                if (vt == ViewType.DrawingSheet || v is ViewSheet) { excludedSheets++; continue; }
                if (scheduleTypes.Contains(vt) || v is ViewSchedule) { excludedSchedules++; continue; }
                if (vt == ViewType.Legend) { excludedLegends++; continue; }
                if (systemTypes.Contains(vt)) { excludedOther++; continue; }

                if (auditScope.Contains(vt))
                {
                    auditedCount++;
                    bool compliant;
                    try
                    {
                        compliant = regex.IsMatch(v.Name);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // The 2s timeout fires at MATCH time, not construction —
                        // keep the tool's {ok:false, error} contract instead of
                        // letting the exception escape the dispatcher.
                        return new Dictionary<string, object?>
                        {
                            ["ok"] = false,
                            ["error"] = "regex timed out — simplify the pattern",
                        };
                    }
                    if (compliant)
                    {
                        compliantCount++;
                    }
                    else
                    {
                        violations.Add(new Dictionary<string, object?>
                        {
                            ["id"] = v.Id.Value,
                            ["name"] = v.Name,
                            ["view_type"] = vt.ToString(),
                        });
                    }
                }
                else if (usingDefaultScope && otherModelViewTypes.Contains(vt))
                {
                    if (otherModelViews.Count < OtherCap)
                        otherModelViews.Add(new Dictionary<string, object?>
                        {
                            ["id"] = v.Id.Value,
                            ["name"] = v.Name,
                            ["view_type"] = vt.ToString(),
                        });
                    else
                        otherCapped = true;
                }
                else
                {
                    // Not in the audited scope and not a recognized
                    // "other model view" (Rendering/Walkthrough/Report/... or,
                    // when include_view_types narrows scope, everything else).
                    excludedOther++;
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["audited_scope"] = auditedScopeNames,
                ["compliant_count"] = compliantCount,
                ["violations"] = violations,
                ["other_model_views"] = otherModelViews,
                ["other_model_views_capped"] = otherCapped,
                ["excluded_counts"] = new Dictionary<string, object?>
                {
                    ["schedules"] = excludedSchedules,
                    ["legends"] = excludedLegends,
                    ["sheets"] = excludedSheets,
                    ["templates"] = excludedTemplates,
                    ["other"] = excludedOther,
                },
                ["counts"] = new Dictionary<string, object?>
                {
                    ["audited"] = auditedCount,
                    ["violations"] = violations.Count,
                },
            };
        }

        // ─── find_elements_between_grids ────────────────────────────────────
        /// <summary>
        /// args: { grid_names: [string], category: string, level?: string }
        ///
        /// Elements whose location falls inside the plan box bounded by the
        /// named grids. Two grids define a strip (bounded on one axis only);
        /// four define a bay. Names match Revit's grid names case-insensitively.
        ///
        /// Why this tool exists: grid-bounded selection was the single largest
        /// codegen fallback in July 2026 production — 4 of 5 fallbacks, 18-43s
        /// each, hand-written FilteredElementCollector plus grid-curve maths on
        /// every request ("tandas di Aras 01 antara gridline 1-2 & E-F").
        ///
        /// Returns element_ids + bounds_mm + the grids actually resolved, so a
        /// typo in a grid name is visible instead of silently widening the box.
        /// </summary>
        public static Dictionary<string, object?> FindElementsBetweenGrids(Document doc, JsonElement args)
        {
            var names = new List<string>();
            if (args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("grid_names", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) names.Add(s!);
                    }
            }
            if (names.Count < 2)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "grid_names needs at least 2 grid names (2 = strip, 4 = bay)",
                };

            var categoryName = TryGetString(args, "category") ?? "Walls";
            var bic = CategoryResolve.Resolve(categoryName);
            if (!bic.HasValue)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"unknown category '{categoryName}' — pass a BuiltInCategory like OST_PlumbingFixtures or a friendly name like 'Plumbing Fixtures'",
                };

            var allGrids = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Grids)
                .WhereElementIsNotElementType()
                .OfType<Grid>()
                .ToList();

            var resolved = new List<Grid>();
            var unresolved = new List<string>();
            foreach (var n in names)
            {
                var g = allGrids.FirstOrDefault(
                    x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (g != null) resolved.Add(g); else unresolved.Add(n);
            }
            // Loud failure: a silently dropped grid name widens the box and the
            // agent reports a confident count over the wrong region.
            if (unresolved.Count > 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"grid(s) not found: {string.Join(", ", unresolved)}",
                    ["available_grids"] = allGrids.Select(g => (object)g.Name).ToList<object>(),
                };

            // Collect each resolved grid's plan extent, then bound X and Y by
            // whichever grids actually constrain them. A grid running N-S
            // constrains X; one running E-W constrains Y. Deriving the axis from
            // the curve (rather than from name order) is what lets the caller
            // pass "1", "2", "E", "F" in any order.
            double xMin = double.NegativeInfinity, xMax = double.PositiveInfinity;
            double yMin = double.NegativeInfinity, yMax = double.PositiveInfinity;
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var g in resolved)
            {
                var c = g.Curve;
                if (c == null) continue;
                var p0 = c.GetEndPoint(0);
                var p1 = c.GetEndPoint(1);
                if (Math.Abs(p1.X - p0.X) <= Math.Abs(p1.Y - p0.Y))
                    xs.Add((p0.X + p1.X) / 2.0);      // runs N-S → constrains X
                else
                    ys.Add((p0.Y + p1.Y) / 2.0);      // runs E-W → constrains Y
            }
            if (xs.Count >= 2) { xMin = xs.Min(); xMax = xs.Max(); }
            if (ys.Count >= 2) { yMin = ys.Min(); yMax = ys.Max(); }
            if (xs.Count < 2 && ys.Count < 2)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "the named grids do not bound a region — pass two PARALLEL grids (a strip) or four (a bay)",
                };

            var levelName = TryGetString(args, "level");
            ElementId? levelId = null;
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (lvl == null)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = $"level '{levelName}' not found",
                        ["available_levels"] = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level)).Cast<Level>()
                            .Select(l => (object)l.Name).ToList<object>(),
                    };
                levelId = lvl.Id;
            }

            const int Cap = 200;
            var matches = new List<object>();
            int total = 0;
            foreach (var el in new FilteredElementCollector(doc)
                         .OfCategory(bic.Value).WhereElementIsNotElementType())
            {
                if (levelId != null && el.LevelId.Value != levelId.Value) continue;

                // Centre of the element's bounding box: works for hosted
                // families, walls, rooms and MEP curves alike, where
                // LocationPoint alone is null for most of them.
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                var cx = (bb.Min.X + bb.Max.X) / 2.0;
                var cy = (bb.Min.Y + bb.Max.Y) / 2.0;
                if (cx < xMin || cx > xMax || cy < yMin || cy > yMax) continue;

                total++;
                if (matches.Count >= Cap) continue;
                var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.LevelId) : null;
                matches.Add(new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["name"] = el.Name,
                    ["level"] = lvl?.Name,
                    ["xy_mm"] = new[] { Math.Round(cx * 304.8, 0), Math.Round(cy * 304.8, 0) },
                });
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["matched"] = total,
                ["returned"] = matches.Count,
                ["truncated"] = total > matches.Count,
                ["element_ids"] = matches
                    .Cast<Dictionary<string, object?>>()
                    .Select(m => m["id"]!).ToList<object>(),
                ["matches"] = matches,
                ["grids_resolved"] = resolved.Select(g => (object)g.Name).ToList<object>(),
                ["bounds_mm"] = new Dictionary<string, object?>
                {
                    ["x_min"] = double.IsInfinity(xMin) ? null : (object)Math.Round(xMin * 304.8, 0),
                    ["x_max"] = double.IsInfinity(xMax) ? null : (object)Math.Round(xMax * 304.8, 0),
                    ["y_min"] = double.IsInfinity(yMin) ? null : (object)Math.Round(yMin * 304.8, 0),
                    ["y_max"] = double.IsInfinity(yMax) ? null : (object)Math.Round(yMax * 304.8, 0),
                },
                ["nothing"] = total == 0,
            };
        }

        // ─── find_mep_elements ─────────────────────────────────────────────
        /// <summary>
        /// args: { system: string, level?: string, view?: string }
        ///
        /// Existing MEP elements by plain-word system name. The mapping below is
        /// the whole point of the tool: a hand-written collector for pipes
        /// invented `BuiltInCategory.OST_Pipes`, which does not exist — the real
        /// category is OST_PipeCurves (production trace 224e8f94, 2026-07-22).
        /// Naming the categories once, here, removes them from the model's
        /// guessing surface entirely.
        /// </summary>
        public static Dictionary<string, object?> FindMepElements(Document doc, JsonElement args)
        {
            var system = (TryGetString(args, "system") ?? "").Trim().ToLowerInvariant();
            BuiltInCategory bic;
            switch (system)
            {
                case "pipe": case "pipes": case "paip":
                    bic = BuiltInCategory.OST_PipeCurves; break;
                case "duct": case "ducts": case "ductwork":
                    bic = BuiltInCategory.OST_DuctCurves; break;
                case "conduit": case "conduits":
                    bic = BuiltInCategory.OST_Conduit; break;
                case "cable_tray": case "cabletray": case "cable trays":
                    bic = BuiltInCategory.OST_CableTray; break;
                case "pipe_fitting": case "pipe fittings":
                    bic = BuiltInCategory.OST_PipeFitting; break;
                case "duct_fitting": case "duct fittings":
                    bic = BuiltInCategory.OST_DuctFitting; break;
                case "pipe_accessory": case "pipe accessories": case "valve": case "valves":
                    bic = BuiltInCategory.OST_PipeAccessory; break;
                case "sprinkler": case "sprinklers":
                    bic = BuiltInCategory.OST_Sprinklers; break;
                case "air_terminal": case "air terminals": case "diffuser": case "diffusers":
                    bic = BuiltInCategory.OST_DuctTerminal; break;
                case "plumbing_fixture": case "plumbing fixtures": case "sanitary":
                    bic = BuiltInCategory.OST_PlumbingFixtures; break;
                case "mechanical_equipment": case "mechanical equipment":
                    bic = BuiltInCategory.OST_MechanicalEquipment; break;
                case "electrical_fixture": case "electrical fixtures":
                    bic = BuiltInCategory.OST_ElectricalFixtures; break;
                default:
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = $"unknown MEP system '{system}'",
                        ["supported"] = new List<object>
                        {
                            "pipe", "duct", "conduit", "cable_tray", "pipe_fitting",
                            "duct_fitting", "pipe_accessory", "sprinkler",
                            "air_terminal", "plumbing_fixture",
                            "mechanical_equipment", "electrical_fixture",
                        },
                    };
            }

            var levelName = TryGetString(args, "level");
            ElementId? levelId = null;
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (lvl == null)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false, ["error"] = $"level '{levelName}' not found",
                    };
                levelId = lvl.Id;
            }

            var viewName = TryGetString(args, "view");
            FilteredElementCollector col;
            if (!string.IsNullOrWhiteSpace(viewName))
            {
                var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate &&
                                         string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
                if (view == null)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false, ["error"] = $"view '{viewName}' not found",
                    };
                col = new FilteredElementCollector(doc, view.Id);
            }
            else
            {
                col = new FilteredElementCollector(doc);
            }

            const int Cap = 200;
            var matches = new List<object>();
            int total = 0;
            foreach (var el in col.OfCategory(bic).WhereElementIsNotElementType())
            {
                if (levelId != null && el.LevelId.Value != levelId.Value) continue;
                total++;
                if (matches.Count >= Cap) continue;
                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.GetTypeId()) : null;
                var lvl = el.LevelId.Value != ElementId.InvalidElementId.Value
                    ? doc.GetElement(el.LevelId) : null;
                var entry = new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["name"] = el.Name,
                    ["type_name"] = typeEl?.Name,
                    ["level"] = lvl?.Name,
                };
                // Curve-based MEP carries a real length; mm per the units
                // contract (every model-visible length is *_mm).
                if ((el.Location as LocationCurve)?.Curve is Curve mc)
                    entry["length_mm"] = Math.Round(mc.Length * 304.8, 0);
                matches.Add(entry);
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["category"] = bic.ToString(),
                ["matched"] = total,
                ["returned"] = matches.Count,
                ["truncated"] = total > matches.Count,
                ["element_ids"] = matches
                    .Cast<Dictionary<string, object?>>()
                    .Select(m => m["id"]!).ToList<object>(),
                ["matches"] = matches,
                ["nothing"] = total == 0,
            };
        }
    }
}
