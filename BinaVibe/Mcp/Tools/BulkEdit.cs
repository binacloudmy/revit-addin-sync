// Filter-scoped bulk writes with preview + built-in verification
// (bina-ai R2 Task 22, bulk parameter/type pack).
//
//   set_parameter_by_filter {category, predicate?, parameter, value, only_empty?, include_grouped?, dry_run}
//   swap_type_by_filter     {category, predicate?, type_name, dry_run}
//
// Target set = category ∩ predicate (same DSL as find_elements_by_filter),
// computed here — no id list from the model, no 100 cap. dry_run returns the
// exact per-element diff and accounts for every matched element; apply runs
// ONE transaction, then re-reads what it wrote and reports `verified`.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.BulkEdit;

namespace BinaVibe.Mcp.Tools
{
    internal static class BulkEdit
    {
        private static List<Element> Targets(Document doc, JsonElement args, out Dictionary<string, object?>? error)
        {
            error = null;
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var predicate = ArgsHelp.GetString(args, "predicate");
            var bic = Inspectors.ResolveCategoryRobust(doc, category);
            if (bic == null)
            {
                error = new() { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };
                return new List<Element>();
            }
            return new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType()
                .Where(e => Inspectors.PredicateMatches(e, doc, predicate)).ToList();
        }

        private static string DisplayName(Document doc, Element e)
        {
            try
            {
                var t = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId())?.Name : null;
                return string.IsNullOrEmpty(t) ? (e.Name ?? e.Id.Value.ToString()) : $"{t} #{e.Id.Value}";
            }
            catch { return e.Id.Value.ToString(); }
        }

        public static Dictionary<string, object?> SetParameterByFilter(Document doc, JsonElement args)
        {
            var paramName = ArgsHelp.GetString(args, "parameter") ?? ArgsHelp.GetString(args, "param")
                            ?? throw new ArgumentException("missing parameter");
            var value = ArgsHelp.GetValueRaw(args, "value")?.ToString() ?? throw new ArgumentException("missing value");
            var onlyEmpty = ArgsHelp.GetBool(args, "only_empty") ?? false;
            var includeGrouped = ArgsHelp.GetBool(args, "include_grouped") ?? false;
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;

            var targets = Targets(doc, args, out var err);
            if (err != null) return err;
            if (targets.Count == 0)
                return new() { ["ok"] = true, ["dry_run"] = dryRun, ["matched"] = 0, ["would_set"] = 0, ["set"] = 0,
                               ["preview"] = new List<object>(), ["unchanged"] = 0, ["read_only"] = 0, ["grouped_skipped"] = 0,
                               ["nothing"] = true, ["headline"] = "no elements matched" };

            // Wrong parameter name → suggestions, never a silent zero.
            if (!targets.Any(e => e.LookupParameter(paramName) != null
                                  || (e.GetTypeId().Value != ElementId.InvalidElementId.Value && doc.GetElement(e.GetTypeId())?.LookupParameter(paramName) != null)))
                return new()
                {
                    ["ok"] = false,
                    ["error"] = $"parameter '{paramName}' does not exist on any matched element or type — the name is wrong",
                    ["suggestions"] = Inspectors.SuggestParamNames(doc, targets[0], paramName),
                };

            var rows = targets.Select(e =>
            {
                var p = e.LookupParameter(paramName);
                return new ParamRow
                {
                    Id = (long)e.Id.Value,
                    Name = DisplayName(doc, e),
                    Current = Inspectors.ResolveParamValue(doc, e, paramName),
                    ReadOnly = p == null || p.IsReadOnly,
                    Grouped = e.GroupId.Value != ElementId.InvalidElementId.Value,
                };
            }).ToList();
            var plan = ParamPlan.Build(rows, value, onlyEmpty, includeGrouped);
            if (dryRun) return plan.ToPreview(cap: 200);

            int set = 0;
            var skips = new List<object>();
            var expected = new Dictionary<long, string>();
            using (var tx = new Transaction(doc, $"BinaVibe: set_parameter_by_filter {paramName}"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    foreach (var c in plan.Changes)
                    {
                        var e = doc.GetElement(ElemIds.From(c.Id));
                        var p = e?.LookupParameter(paramName);
                        if (p == null || p.IsReadOnly) { skips.Add(new Dictionary<string, object?> { ["id"] = c.Id, ["reason"] = "read-only" }); continue; }
                        try { Mutators.SetParamValue(p, value); set++; expected[c.Id] = value; }
                        catch (Exception ex) { skips.Add(new Dictionary<string, object?> { ["id"] = c.Id, ["reason"] = ex.Message }); }
                    }
                    tx.Commit();
                }
                catch { tx.RollBack(); throw; }
            }
            doc.Regenerate();
            var verified = WriteVerification.Verify(expected, id =>
            {
                var e = doc.GetElement(ElemIds.From(id));
                return e == null ? null : Inspectors.ResolveParamValue(doc, e, paramName);
            });
            return new()
            {
                ["ok"] = true,
                ["matched"] = plan.Matched,
                ["would_set"] = plan.Changes.Count,
                ["set"] = set,
                ["skipped"] = plan.Changes.Count - set,
                ["skips"] = skips,
                ["unchanged"] = plan.Unchanged,
                ["read_only"] = plan.ReadOnly,
                ["grouped_skipped"] = plan.GroupedSkipped,
                ["verified"] = verified,
                ["transactions"] = new List<string> { $"BinaVibe: set_parameter_by_filter {paramName}" },
                ["headline"] = $"{set} of {plan.Changes.Count} set, {verified["matches"]} verified",
            };
        }

        public static Dictionary<string, object?> SwapTypeByFilter(Document doc, JsonElement args)
        {
            var newTypeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;
            var targets = Targets(doc, args, out var err);
            if (err != null) return err;

            var newType = new FilteredElementCollector(doc).WhereElementIsElementType()
                .FirstOrDefault(t => string.Equals(t.Name, newTypeName, StringComparison.OrdinalIgnoreCase));
            if (newType == null)
            {
                var candidates = new FilteredElementCollector(doc).WhereElementIsElementType()
                    .Where(t => t.Name.IndexOf(newTypeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => t.Name).Distinct().Take(8).ToList();
                return new() { ["ok"] = false, ["error"] = $"type '{newTypeName}' not found in document", ["candidates"] = candidates };
            }

            string TypeName(Element e) => e.GetTypeId().Value != ElementId.InvalidElementId.Value
                ? (doc.GetElement(e.GetTypeId())?.Name ?? "") : "";
            var rows = targets.Select(e => new TypeRow { Id = (long)e.Id.Value, FromType = TypeName(e) }).ToList();
            var plan = TypeSwapPlan.Build(rows, newType.Name);
            if (dryRun) return plan.ToPreview(cap: 200);

            if (newType is FamilySymbol fs && !fs.IsActive)
            {
                using var txA = new Transaction(doc, "BinaVibe: activate_symbol");
                TxGuard.StartSwallowing(txA);
                fs.Activate(); doc.Regenerate(); txA.Commit();
            }

            int swapped = 0;
            var skips = new List<object>();
            var newIds = new List<object>();
            var expected = new Dictionary<long, string>();
            using (var tx = new Transaction(doc, "BinaVibe: swap_type_by_filter"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    foreach (var c in plan.Changes)
                    {
                        var el = doc.GetElement(ElemIds.From(c.Id));
                        if (el == null) continue;
                        try
                        {
                            if (el is FamilyInstance fi && newType is FamilySymbol sym && fi.Symbol.Family.Id != sym.Family.Id)
                            {
                                var nid = Mutators.ReplaceCrossFamily(doc, fi, sym);
                                if (nid == null) { skips.Add(new Dictionary<string, object?> { ["id"] = c.Id, ["reason"] = "cross-family replace failed (no location point)" }); continue; }
                                swapped++; newIds.Add(nid.Value); expected[(long)nid.Value] = newType.Name;
                                continue;
                            }
                            el.ChangeTypeId(newType.Id);
                            swapped++; newIds.Add(c.Id); expected[c.Id] = newType.Name;
                        }
                        catch (Exception ex) { skips.Add(new Dictionary<string, object?> { ["id"] = c.Id, ["reason"] = ex.Message }); }
                    }
                    tx.Commit();
                }
                catch { tx.RollBack(); throw; }
            }
            doc.Regenerate();
            var verified = WriteVerification.Verify(expected, id =>
            {
                var e = doc.GetElement(ElemIds.From(id));
                return e == null ? null : TypeName(e);
            });
            return new()
            {
                ["ok"] = true,
                ["matched"] = plan.Matched,
                ["would_swap"] = plan.Changes.Count,
                ["swapped"] = swapped,
                ["skipped"] = plan.Changes.Count - swapped,
                ["skips"] = skips,
                ["unchanged"] = plan.Unchanged,
                ["new_ids"] = newIds,
                ["verified"] = verified,
                ["transactions"] = new List<string> { "BinaVibe: swap_type_by_filter" },
                ["headline"] = $"{swapped} of {plan.Changes.Count} swapped to {newType.Name}, {verified["matches"]} verified",
            };
        }
    }
}
