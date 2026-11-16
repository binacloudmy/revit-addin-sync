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

            // Doors/windows carry the sizes a fit-check needs. Emitting them here makes
            // "which type fits this 900x2100 opening" ONE call; otherwise the agent pays a
            // get_type_parameters round trip per candidate. Rough openings are what a hole
            // is actually cut to, so they ride along when the family exposes them.
            bool sized = bic.Value == BuiltInCategory.OST_Doors
                      || bic.Value == BuiltInCategory.OST_Windows;

            var types = q
                .Where(NameHit)
                .Take(500)
                .Select(t =>
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["id"] = t.Id.Value,
                        ["name"] = t.Name,
                        ["family_name"] = (t as ElementType)?.FamilyName,
                        ["instances"] = counts.TryGetValue(t.Id.Value, out var c) ? c : 0,
                    };
                    if (sized)
                    {
                        // Omit rather than emit nulls — a 500-type payload should not carry
                        // four dead keys per row.
                        AddIfSome(row, "width_mm", TypeLengthMm(t, "Width"));
                        AddIfSome(row, "height_mm", TypeLengthMm(t, "Height"));
                        AddIfSome(row, "rough_width_mm", TypeLengthMm(t, "Rough Width"));
                        AddIfSome(row, "rough_height_mm", TypeLengthMm(t, "Rough Height"));
                    }
                    return row;
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

        // ─── condition evaluator ─────────────────────────────────────────
        // Display-form string for a parameter: Double params render via
        // AsValueString (project units, e.g. "3.000 m") — SafeParamValue would
        // stringify the rich dict. Shared by EvaluateCondition + PredicateMatches.
        private static string DisplayFormString(Parameter p)
            => p.StorageType == StorageType.Double
                ? (p.AsValueString() ?? "")
                : (SafeParamValue(p)?.ToString() ?? "");

        // ─── helpers ────────────────────────────────────────────────────

        /// <summary>The room's NAME, without Revit's number appended.
        ///
        /// Room.Name returns ROOM_NAME + " " + ROOM_NUMBER concatenated, so a
        /// room named "Bilik Tidur 1" with number 1 reads back as
        /// "Bilik Tidur 1 1" — which is what the copilot then reported to a
        /// drafter on 2026-08-08. The stored name is the parameter, not the
        /// property.</summary>
        private static string RoomName(Autodesk.Revit.DB.Architecture.Room room) =>
            room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()
            ?? room.Name ?? "";

        private static string? TryGetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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

        /// <summary>A length parameter off an element, in mm — or null when the family
        /// doesn't carry it. Length params are Revit-internal feet; 304.8 matches the
        /// factor ParamUnits.MetricTwin uses so every tool agrees.</summary>
        internal static double? TypeLengthMm(Element el, string name)
        {
            try
            {
                var p = el.LookupParameter(name);
                if (p == null || p.StorageType != StorageType.Double || !p.HasValue) return null;
                return System.Math.Round(p.AsDouble() * 304.8, 1);
            }
            catch { return null; }
        }

        /// <summary>Set a key only when the value is present, so optional dimensions don't
        /// pad every row with nulls.</summary>
        internal static void AddIfSome(Dictionary<string, object?> d, string key, double? v)
        {
            if (v.HasValue) d[key] = v.Value;
        }

        // internal (was private): SelectByFilter and Mutators.SetWorksetBulk
        // reuse the exact same predicate semantics as find_elements_by_filter
        // so a select and a find can never disagree about what matches.
        internal static bool PredicateMatches(Element el, Document doc, string? predicate)
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
                // JKR-coded family, forcing codegen fallback. A bare name may be a
                // part of the type name or the family name; "Family: Type" keeps to
                // that family (round 9, F-t04: containment either way let "A: Normal"
                // reach every family's "Normal"). One rule with find_elements.
                // Levels stay exact-match below ("Level 1" must never match "Level 10").
                return t != null && global::BinaVibe.Copilot.Writes.TypeNameMatch.Filter(want, (t as ElementType)?.FamilyName, t.Name);
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

            uidoc.Selection.SetElementIds(ids);     // highlight (turns them blue): ONE call for all of them
            try { ViewZoom.ZoomTo(uidoc, ids); } catch { /* zoom best-effort */ }
            return new Dictionary<string, object?> { ["ok"] = true, ["selected"] = ids.Count };
        }

        // Closest real parameter names on a sample element (instance + type),
        // ranked by character-bigram overlap with the asked-for name. Shared
        // by find_missing_parameter and Mutators.FillMissingParameter for the
        // wrong-param-name guard.
        internal static List<string> SuggestParamNames(Document doc, Element sample, string query)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Collect(Element? el)
            {
                if (el == null) return;
                foreach (Parameter q in el.Parameters)
                {
                    var n = q.Definition?.Name;
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
                }
            }
            Collect(sample);
            Collect(sample.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(sample.GetTypeId()) : null);

            static HashSet<string> Bigrams(string s)
            {
                s = s.ToLowerInvariant().Replace("_", " ");
                var h = new HashSet<string>();
                for (int i = 0; i + 1 < s.Length; i++) h.Add(s.Substring(i, 2));
                return h;
            }
            var qb = Bigrams(query);
            return names
                .Select(n => { var nb = Bigrams(n); var inter = nb.Count(b => qb.Contains(b)); return (n, score: qb.Count + nb.Count == 0 ? 0.0 : (double)inter / (qb.Count + nb.Count - inter)); })
                .OrderByDescending(x => x.score).ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase)
                .Take(5).Select(x => x.n).ToList();
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

        // ─── list_cad_links ─────────────────────────────────────────────
        // CAD (DWG/DXF) links AND imports — both are ImportInstance. is_link
        // false means the drawing was IMPORTED into the .rvt rather than linked,
        // which drafters care about, so it is reported rather than flattened
        // away. dwg_ref is the handle every other DWG tool takes.
        public static Dictionary<string, object?> ListCadLinks(Document doc)
        {
            var cad = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Select(imp => new Dictionary<string, object?>
                {
                    ["dwg_ref"] = "model:" + imp.Id.Value,
                    ["id"] = imp.Id.Value,
                    ["name"] = DwgReader.NameOf(imp),
                    ["is_link"] = imp.IsLinked,
                    ["path"] = DwgReader.PathOf(doc, imp),
                    ["layer_count"] = DwgReader.LayerNames(imp).Count,
                    ["extents_mm"] = DwgReader.Extents(imp),
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["cad_links"] = cad, ["count"] = cad.Count };
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
                    ["name"] = RoomName(r),
                    ["level"] = r.Level?.Name ?? "",
                    ["area_m2"] = Math.Round(r.Area * Ft2ToM2, 2),
                    ["perimeter_m"] = Math.Round(r.Perimeter * FtToM, 2),
                    ["volume_m3"] = Math.Round(r.Volume * Ft3ToM3, 2),
                    ["placed"] = r.Area > 0,   // unplaced/unenclosed rooms report zero area
                }).ToList<object>();
            return new Dictionary<string, object?> { ["ok"] = true, ["rooms"] = rooms, ["count"] = rooms.Count };
        }
    }
}
