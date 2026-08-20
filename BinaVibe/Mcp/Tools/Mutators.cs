// Mutators — write-side Revit API implementations for the Step-3 MUTATE
// tools the bina-ai Executor calls through the tunnel.
//
// Each method MUST run on Revit's main thread (callers are inside the
// IExternalEventHandler.Execute). Each MUTATE wraps its work in a
// Transaction (or TransactionGroup for multi-step plans — orchestrator-
// owned, see App.cs / CodeExecutionHandler for the wrapper pattern).
//
// Returns are plain dicts so the JSON serializer doesn't have to
// understand Revit types.
//
// Step-3 scope (10 MUTATE tools — PRD §6.4 top 20 / shortlist):
//   set_parameter, set_parameter_bulk, change_type, delete_elements,
//   duplicate_view, apply_view_template, place_door, place_window,
//   create_wall, create_room

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class Mutators
    {
        // ─── set_parameter ──────────────────────────────────────────────
        public static Dictionary<string, object?> SetParameter(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id") ?? throw new ArgumentException("missing element_id");
            var paramName = ArgsHelp.GetString(args, "param") ?? ArgsHelp.GetString(args, "parameter")
                ?? throw new ArgumentException("missing param/parameter");
            var value = ArgsHelp.GetValueRaw(args, "value");

            var el = doc.GetElement(ElemIds.From(id)) ?? throw new ArgumentException($"element {id} not found");
            var p = el.LookupParameter(paramName) ?? throw new ArgumentException($"parameter {paramName} not on element");
            if (p.IsReadOnly) throw new InvalidOperationException($"parameter {paramName} is read-only");

            using var tx = new Transaction(doc, $"BinaVibe: set_parameter {paramName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                SetParamValue(p, value);
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id"] = id,
                ["parameter"] = paramName,
                ["new_value"] = SafeParamValue(p),
            };
        }

        // ─── set_parameter_bulk ─────────────────────────────────────────
        public static Dictionary<string, object?> SetParameterBulk(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var paramName = ArgsHelp.GetString(args, "param") ?? ArgsHelp.GetString(args, "parameter")
                ?? throw new ArgumentException("missing param/parameter");
            var value = ArgsHelp.GetValueRaw(args, "value");

            int updated = 0, skippedReadOnly = 0, skippedMissing = 0, skippedGroups = 0;
            var failures = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: set_parameter_bulk {paramName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var id in ids)
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el == null) { skippedMissing++; continue; }
                    if (el.GroupId.Value != ElementId.InvalidElementId.Value) { skippedGroups++; continue; }
                    var p = el.LookupParameter(paramName);
                    if (p == null) { skippedMissing++; continue; }
                    if (p.IsReadOnly) { skippedReadOnly++; continue; }
                    try { SetParamValue(p, value); updated++; }
                    catch (Exception ex) { failures.Add(new { id, error = ex.Message }); }
                }
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["updated"] = updated,
                ["skipped_groups"] = skippedGroups,
                ["skipped_readonly"] = skippedReadOnly,
                ["skipped_missing"] = skippedMissing,
                ["failures"] = failures,
            };
        }

        // Make a parameter writable on GROUP MEMBERS: flip the definition's
        // "values can vary by group instance" flag (The Building Coder 1960 /
        // InternalDefinition.SetAllowVaryBetweenGroups). Only Text/Area/Volume/
        // Currency/URL/Material INSTANCE params are eligible — an ineligible
        // type throws ArgumentException, returned here as the reason string so
        // the caller keeps skipping grouped elements and reports WHY instead
        // of failing the batch. Must be called inside an open Transaction.
        // NOTE: this is a one-time, project-wide schema change on the
        // definition — after the flip each group instance's member owns its
        // own value (exactly what a per-element fill wants).
        private static string TryEnableVaryBetweenGroups(Document doc, Parameter sample)
        {
            var def = sample?.Definition as InternalDefinition;
            if (def == null) return "parameter definition is not project/shared-bound — cannot vary between groups";
            if (def.VariesAcrossGroups) return null;
            try { def.SetAllowVaryBetweenGroups(doc, true); return null; }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            { return "parameter type cannot vary between groups (only Text/Area/Volume/Currency/URL/Material instance params are eligible)"; }
            catch (Exception ex) { return "could not enable vary-between-groups: " + ex.Message; }
        }

        // WHY is this parameter read-only here? One-call diagnosis so the
        // model reports the cause instead of burning rounds experimenting
        // (measured 2026-08-18: 294 read-only walls → 20+ probing rounds,
        // 3.69M input tokens). Detection order from the read-only research:
        // duplicate same-named twin → stacked subwall → global-parameter
        // association → corrupt binding.
        private static string DiagnoseReadOnly(Document doc, Element e, string paramName)
        {
            try
            {
                var multi = e.GetParameters(paramName);
                if (multi != null && multi.Count > 1)
                    return $"element carries {multi.Count} parameters named '{paramName}' — the lookup returns a read-only twin (duplicate/built-in name collision); rebind or rename the duplicate parameter";
                if (e is Wall w && w.IsStackedWallMember)
                    return "stacked-wall subwall whose owner parameter is also unwritable";
                var p = e.LookupParameter(paramName);
                var gp = p?.GetAssociatedGlobalParameter();
                if (gp != null && gp != ElementId.InvalidElementId)
                    return "parameter is driven by a GLOBAL parameter — set the global parameter's value instead of the instances";
                return "IsReadOnly with no detectable cause — likely a corrupt parameter binding; remove and re-add the project parameter binding for this category";
            }
            catch (Exception ex) { return "diagnosis failed: " + ex.Message; }
        }

        // ─── fill_missing_parameter ─────────────────────────────────────
        // Write half of Inspectors.FindMissingParameter: fills parameter =
        // value on every category element whose value is EMPTY (instance AND
        // type checked via the same ResolveParamValue, so find and fill can
        // never disagree about what "missing" means). Enumerates server-side
        // on purpose — chat-side query results truncate at 100 ids, so an
        // explicit-id contract (set_parameter_bulk) can never cover a
        // category-wide fill (2026-08-17: 1458 walls, "masukkan kontraktor").
        public static Dictionary<string, object?> FillMissingParameter(Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var paramName = ArgsHelp.GetString(args, "parameter") ?? ArgsHelp.GetString(args, "param")
                ?? throw new ArgumentException("missing parameter");
            var value = ArgsHelp.GetValueRaw(args, "value") ?? throw new ArgumentException("missing value");
            var level = ArgsHelp.GetString(args, "level");
            var typeContains = ArgsHelp.GetString(args, "type_name_contains");
            var includeGrouped = ArgsHelp.GetBool(args, "include_grouped") ?? false;

            var bic = Inspectors.ResolveCategoryRobust(doc, category)
                ?? throw new ArgumentException($"category '{category}' not recognised");

            var els = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();
            if (!string.IsNullOrWhiteSpace(level))
                els = els.Where(e => string.Equals(doc.GetElement(e.LevelId)?.Name, level, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(typeContains))
                els = els.Where(e =>
                {
                    var t = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId()) : null;
                    return t?.Name != null && t.Name.IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();

            // Existence guard: a parameter no element carries is a WRONG NAME
            // ("detail kontraktor" vs the real schedule column
            // "Kontraktor_jkr_sit"), not a 100%-empty audit. Refuse and offer
            // real names instead of silently filling nothing.
            var anyHasParam = els.Any(e =>
            {
                if (e.LookupParameter(paramName) != null) return true;
                var t = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId()) : null;
                return t?.LookupParameter(paramName) != null;
            });
            if (els.Count > 0 && !anyHasParam)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"parameter '{paramName}' does not exist on any {category} element or type — the name is wrong",
                    ["suggestions"] = Inspectors.SuggestParamNames(doc, els[0], paramName),
                };

            int updated = 0, alreadyFilled = 0, skippedGroups = 0, skippedReadOnly = 0, paramMissingOn = 0, groupedWritten = 0;
            var failures = new List<object>();
            var resultExtras = new Dictionary<string, object?>();
            string groupedNote = null;

            using var tx = new Transaction(doc, $"BinaVibe: fill_missing_parameter {paramName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Force lane ("masukkan jugak walaupun dalam group"): grouped
                // members are writable once the definition varies between
                // groups. Flip it here (one-time, inside this tx); if the
                // param type is ineligible, fall back to skipping with the
                // reason surfaced instead of erroring the whole fill.
                if (includeGrouped)
                {
                    var sample = els.Select(e => e.LookupParameter(paramName)).FirstOrDefault(q => q != null);
                    groupedNote = TryEnableVaryBetweenGroups(doc, sample);
                    if (groupedNote != null) includeGrouped = false;
                }

                int stackedViaOwner = 0;
                foreach (var e in els)
                {
                    if (!string.IsNullOrWhiteSpace(Inspectors.ResolveParamValue(doc, e, paramName))) { alreadyFilled++; continue; }
                    var grouped = e.GroupId.Value != ElementId.InvalidElementId.Value;
                    if (grouped && !includeGrouped) { skippedGroups++; continue; }
                    var p = e.LookupParameter(paramName);
                    if (p == null) { paramMissingOn++; continue; }   // type-only param — not writable per instance
                    if (p.IsReadOnly)
                    {
                        // Stacked-wall subwall: instance params are inherited
                        // from the OWNER stacked wall and read-only by design
                        // (measured 2026-08-18: 294 "read-only" walls = paired
                        // 55mm brick + 20mm plaster subwalls). The correct
                        // write target is the owner — not a forced write here.
                        var ownerId = (e as Wall)?.StackedWallOwnerId ?? ElementId.InvalidElementId;
                        var ownerP = ownerId != ElementId.InvalidElementId
                            ? doc.GetElement(ownerId)?.LookupParameter(paramName) : null;
                        if (ownerP != null && !ownerP.IsReadOnly)
                        {
                            if (string.IsNullOrWhiteSpace(ownerP.AsString() ?? ownerP.AsValueString()))
                            {
                                try { SetParamValue(ownerP, value); stackedViaOwner++; updated++; }
                                catch (Exception ex) { failures.Add(new { id = e.Id.Value, error = "owner write: " + ex.Message }); }
                            }
                            else stackedViaOwner++;   // owner already carries the value — subwall inherits
                            continue;
                        }
                        skippedReadOnly++;
                        if (!resultExtras.ContainsKey("readonly_diagnosis"))
                            resultExtras["readonly_diagnosis"] = DiagnoseReadOnly(doc, e, paramName);
                        continue;
                    }
                    try { SetParamValue(p, value); updated++; if (grouped) groupedWritten++; }
                    catch (Exception ex) { failures.Add(new { id = e.Id.Value, error = ex.Message }); }
                }
                if (stackedViaOwner > 0) resultExtras["stacked_via_owner"] = stackedViaOwner;
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["updated"] = updated,
                ["already_filled"] = alreadyFilled,
                ["skipped_groups"] = skippedGroups,
                ["skipped_readonly"] = skippedReadOnly,
                ["param_missing_on"] = paramMissingOn,
                // Cap: a mass-fill can fail on hundreds of elements; the model
                // needs the pattern, not 646 rows (compressor-tax lesson,
                // Langfuse 48906553e3).
                ["failures"] = failures.Take(25).ToList(),
                ["failures_total"] = failures.Count,
                ["total"] = els.Count,
            };
            if (groupedWritten > 0) result["grouped_written"] = groupedWritten;
            if (groupedNote != null) result["grouped_note"] = groupedNote;
            foreach (var kv in resultExtras) result[kv.Key] = kv.Value;
            return result;
        }

        // ─── propagate_parameter_by_name ────────────────────────────────
        // "Schedule as mapping table": some rows of a schedule already carry
        // the value, the blank rows should inherit it from rows with the
        // SAME name. Groups category elements by match_key ("type_name" or a
        // parameter display name like "Mark"), then copies each group's
        // filled value onto that group's empty elements. Same
        // ResolveParamValue empty-semantics as find/fill; groups whose filled
        // elements disagree get NOTHING written (reported as conflicts).
        public static Dictionary<string, object?> PropagateParameterByName(Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var paramName = ArgsHelp.GetString(args, "parameter") ?? ArgsHelp.GetString(args, "param")
                ?? throw new ArgumentException("missing parameter");
            var matchKey = ArgsHelp.GetString(args, "match_key") ?? "type_name";
            var includeGrouped = ArgsHelp.GetBool(args, "include_grouped") ?? false;

            var bic = Inspectors.ResolveCategoryRobust(doc, category)
                ?? throw new ArgumentException($"category '{category}' not recognised");

            var els = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();

            var anyHasParam = els.Any(e =>
            {
                if (e.LookupParameter(paramName) != null) return true;
                var t = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId()) : null;
                return t?.LookupParameter(paramName) != null;
            });
            if (els.Count > 0 && !anyHasParam)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"parameter '{paramName}' does not exist on any {category} element or type — the name is wrong",
                    ["suggestions"] = Inspectors.SuggestParamNames(doc, els[0], paramName),
                };

            string KeyOf(Element e)
            {
                if (string.Equals(matchKey, "type_name", StringComparison.OrdinalIgnoreCase))
                {
                    var t = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId()) : null;
                    return t?.Name ?? "";
                }
                return Inspectors.ResolveParamValue(doc, e, matchKey);
            }

            var groups = els.GroupBy(KeyOf).Where(g => !string.IsNullOrWhiteSpace(g.Key)).ToList();

            int updated = 0, alreadyFilled = 0, groupsFilled = 0, skippedGroups = 0, skippedReadOnly = 0, groupedWritten = 0, stackedViaOwner = 0;
            var conflicts = new List<object>();
            var noSource = new List<object>();
            var failures = new List<object>();
            string groupedNote = null;
            string readonlyDiagnosis = null;

            using var tx = new Transaction(doc, $"BinaVibe: propagate_parameter_by_name {paramName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Same force lane as FillMissingParameter: grouped members are
                // writable once the definition varies between groups.
                if (includeGrouped)
                {
                    var sample = els.Select(e => e.LookupParameter(paramName)).FirstOrDefault(q => q != null);
                    groupedNote = TryEnableVaryBetweenGroups(doc, sample);
                    if (groupedNote != null) includeGrouped = false;
                }
                foreach (var g in groups)
                {
                    var filledValues = g
                        .Select(e => Inspectors.ResolveParamValue(doc, e, paramName))
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var blanks = g.Where(e => string.IsNullOrWhiteSpace(Inspectors.ResolveParamValue(doc, e, paramName))).ToList();
                    alreadyFilled += g.Count() - blanks.Count;

                    if (filledValues.Count == 0)
                    {
                        if (blanks.Count > 0) noSource.Add(new { key = g.Key, count = blanks.Count });
                        continue;
                    }
                    if (filledValues.Count > 1)
                    {
                        conflicts.Add(new { key = g.Key, values = filledValues, count = blanks.Count });
                        continue;
                    }
                    if (blanks.Count == 0) continue;

                    var value = filledValues[0];
                    var wroteAny = false;
                    foreach (var e in blanks)
                    {
                        var grouped = e.GroupId.Value != ElementId.InvalidElementId.Value;
                        if (grouped && !includeGrouped) { skippedGroups++; continue; }
                        var p = e.LookupParameter(paramName);
                        if (p == null) continue;                       // type-only param — not writable per instance
                        if (p.IsReadOnly)
                        {
                            // Stacked-wall subwall — write the OWNER instead
                            // (same rationale as FillMissingParameter).
                            var ownerId = (e as Wall)?.StackedWallOwnerId ?? ElementId.InvalidElementId;
                            var ownerP = ownerId != ElementId.InvalidElementId
                                ? doc.GetElement(ownerId)?.LookupParameter(paramName) : null;
                            if (ownerP != null && !ownerP.IsReadOnly)
                            {
                                if (string.IsNullOrWhiteSpace(ownerP.AsString() ?? ownerP.AsValueString()))
                                {
                                    try { SetParamValue(ownerP, value); stackedViaOwner++; updated++; wroteAny = true; }
                                    catch (Exception ex) { failures.Add(new { id = e.Id.Value, error = "owner write: " + ex.Message }); }
                                }
                                else stackedViaOwner++;
                                continue;
                            }
                            skippedReadOnly++;
                            if (readonlyDiagnosis == null)
                                readonlyDiagnosis = DiagnoseReadOnly(doc, e, paramName);
                            continue;
                        }
                        try { SetParamValue(p, value); updated++; wroteAny = true; if (grouped) groupedWritten++; }
                        catch (Exception ex) { failures.Add(new { id = e.Id.Value, error = ex.Message }); }
                    }
                    if (wroteAny) groupsFilled++;
                }
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["updated"] = updated,
                ["groups_filled"] = groupsFilled,
                ["conflicts"] = conflicts,
                ["no_source"] = noSource,
                ["already_filled"] = alreadyFilled,
                ["skipped_groups"] = skippedGroups,
                ["skipped_readonly"] = skippedReadOnly,
                ["failures"] = failures.Take(25).ToList(),
                ["failures_total"] = failures.Count,
                ["total"] = els.Count,
            };
            if (groupedWritten > 0) result["grouped_written"] = groupedWritten;
            if (groupedNote != null) result["grouped_note"] = groupedNote;
            if (stackedViaOwner > 0) result["stacked_via_owner"] = stackedViaOwner;
            if (readonlyDiagnosis != null) result["readonly_diagnosis"] = readonlyDiagnosis;
            return result;
        }

        // ─── set_type_parameter ─────────────────────────────────────────
        // Type-level fix the instance-param family (fill/propagate) points
        // at via param_missing_on: edits a TYPE parameter addressed by type
        // NAME — the model never holds type element ids. One write changes
        // every placed instance, so the result carries instances_affected
        // (the blast radius the model must report).
        public static Dictionary<string, object?> SetTypeParameter(Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var typeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");
            var paramName = ArgsHelp.GetString(args, "param") ?? ArgsHelp.GetString(args, "parameter")
                ?? throw new ArgumentException("missing param/parameter");
            var value = ArgsHelp.GetValueRaw(args, "value");

            var bic = Inspectors.ResolveCategoryRobust(doc, category)
                ?? throw new ArgumentException($"category '{category}' not recognised");

            var types = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().ToList();

            // Name resolution, strictest first: exact, then the segment after
            // the last " @ " (inspectors render types as "family @ type"),
            // then contains. Ambiguity is an answer, not a guess.
            List<Element> Match(string needle) =>
                types.Where(t => string.Equals(t.Name, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            var matched = Match(typeName);
            if (matched.Count == 0 && typeName.Contains(" @ "))
                matched = Match(typeName.Substring(typeName.LastIndexOf(" @ ", StringComparison.Ordinal) + 3));
            if (matched.Count == 0)
                matched = types.Where(t => t.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (matched.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"no {category} type named '{typeName}'",
                    ["candidates"] = types.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
                };
            if (matched.Count > 1)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"type_name '{typeName}' is ambiguous ({matched.Count} matches) — retry with the exact name",
                    ["candidates"] = matched.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
                };

            var typeEl = matched[0];
            var p = typeEl.LookupParameter(paramName);
            if (p == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"parameter '{paramName}' not on type '{typeEl.Name}' — the name is wrong",
                    ["suggestions"] = Inspectors.SuggestParamNames(doc, typeEl, paramName),
                };
            if (p.IsReadOnly) throw new InvalidOperationException($"parameter {paramName} is read-only on type '{typeEl.Name}'");

            var instances = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                .Count(e => e.GetTypeId() == typeEl.Id);

            using var tx = new Transaction(doc, $"BinaVibe: set_type_parameter {paramName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                SetParamValue(p, value);
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["type_name"] = typeEl.Name,
                ["parameter"] = paramName,
                ["new_value"] = value?.ToString(),
                ["instances_affected"] = instances,
            };
        }

        // ─── duplicate_sheet ────────────────────────────────────────────
        // Clone a sheet WITH layout. Plans cannot live on two sheets, so
        // model/plan views are DUPLICATED (WithDetailing) and placed at the
        // source viewport's box centre; legends may live on many sheets and
        // are re-placed directly; ScheduleSheetInstances re-placed too.
        public static Dictionary<string, object?> DuplicateSheet(Document doc, JsonElement args)
        {
            var sourceNumber = ArgsHelp.GetString(args, "source_number") ?? throw new ArgumentException("missing source_number");
            var newNumber = ArgsHelp.GetString(args, "new_number") ?? throw new ArgumentException("missing new_number");
            var newName = ArgsHelp.GetString(args, "new_name");
            var viewSuffix = ArgsHelp.GetString(args, "view_suffix") ?? " - " + newNumber;

            var src = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sourceNumber, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"sheet '{sourceNumber}' not found");
            if (new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Any(s => string.Equals(s.SheetNumber, newNumber, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"sheet number '{newNumber}' already exists");

            // Titleblock type from the source sheet's own titleblock instance.
            var tb = new FilteredElementCollector(doc, src.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault();
            var tbId = tb?.Symbol?.Id ?? ElementId.InvalidElementId;

            int viewsDuplicated = 0, legendsPlaced = 0, schedulesPlaced = 0;
            long sheetId;
            using var tx = new Transaction(doc, "BinaVibe: duplicate_sheet");
            TxGuard.StartSwallowing(tx);
            try
            {
                var sheet = ViewSheet.Create(doc, tbId);
                sheet.SheetNumber = newNumber;
                sheet.Name = newName ?? src.Name;
                sheetId = sheet.Id.Value;

                foreach (var vpId in src.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is not Viewport vp) continue;
                    var view = doc.GetElement(vp.ViewId) as View;
                    if (view == null) continue;
                    var center = vp.GetBoxCenter();

                    ElementId placeId;
                    if (view.ViewType == ViewType.Legend)
                    {
                        placeId = view.Id;              // legends may sit on many sheets
                        legendsPlaced++;
                    }
                    else
                    {
                        var opt = view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing)
                            ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate;
                        placeId = view.Duplicate(opt);
                        if (doc.GetElement(placeId) is View nv)
                        {
                            try { nv.Name = view.Name + viewSuffix; } catch { /* name clash — keep auto name */ }
                        }
                        viewsDuplicated++;
                    }
                    var nvp = Viewport.Create(doc, sheet.Id, placeId, center);
                    try { if (nvp != null && vp.GetTypeId() != ElementId.InvalidElementId) nvp.ChangeTypeId(vp.GetTypeId()); }
                    catch { /* viewport type best-effort */ }
                }

                foreach (var ssi in new FilteredElementCollector(doc, src.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                {
                    if (ssi.IsTitleblockRevisionSchedule) continue;
                    ScheduleSheetInstance.Create(doc, sheet.Id, ssi.ScheduleId, ssi.Point);
                    schedulesPlaced++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["sheet_id"] = sheetId, ["number"] = newNumber,
                ["views_duplicated"] = viewsDuplicated,
                ["legends_placed"] = legendsPlaced,
                ["schedules_placed"] = schedulesPlaced,
            };
        }

        // ─── create_sheets_batch ────────────────────────────────────────
        public static Dictionary<string, object?> CreateSheetsBatch(Document doc, JsonElement args)
        {
            if (!args.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("missing rows");

            var titleblocks = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>().ToList();
            var existing = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);

            var created = new List<object>();
            var skipped = new List<object>();
            using var tx = new Transaction(doc, "BinaVibe: create_sheets_batch");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var row in rowsEl.EnumerateArray())
                {
                    var number = row.TryGetProperty("number", out var n) ? n.GetString() : null;
                    var name = row.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    var tbName = row.TryGetProperty("titleblock", out var tbn) ? tbn.GetString() : null;
                    if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name))
                    { skipped.Add(new { number, reason = "missing number/name" }); continue; }
                    if (existing.Contains(number!))
                    { skipped.Add(new { number, reason = "sheet number already exists" }); continue; }

                    var tbId = ElementId.InvalidElementId;
                    if (!string.IsNullOrWhiteSpace(tbName))
                    {
                        var match = titleblocks.FirstOrDefault(t =>
                            string.Equals(t.Name, tbName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals($"{t.FamilyName} : {t.Name}", tbName, StringComparison.OrdinalIgnoreCase));
                        if (match == null) { skipped.Add(new { number, reason = $"titleblock '{tbName}' not found" }); continue; }
                        tbId = match.Id;
                    }
                    else if (titleblocks.Count > 0) tbId = titleblocks[0].Id;

                    var sheet = ViewSheet.Create(doc, tbId);
                    sheet.SheetNumber = number!;
                    sheet.Name = name!;
                    existing.Add(number!);
                    created.Add(new { sheet_id = sheet.Id.Value, number });
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["created"] = created, ["skipped"] = skipped,
                ["count"] = created.Count,
            };
        }

        // ─── create_views_for_levels ────────────────────────────────────
        public static Dictionary<string, object?> CreateViewsForLevels(Document doc, JsonElement args)
        {
            var viewType = ArgsHelp.GetString(args, "view_type") ?? "floor";
            var pattern = ArgsHelp.GetString(args, "name_pattern") ?? "Pelan {level}";
            var templateName = ArgsHelp.GetString(args, "template_name");
            var wanted = new List<string>();
            if (args.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.Array)
                foreach (var item in lv.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) wanted.Add(item.GetString()!);

            var family = string.Equals(viewType, "ceiling", StringComparison.OrdinalIgnoreCase)
                ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == family)
                ?? throw new InvalidOperationException($"no {family} view family type in this project");

            View? template = null;
            if (!string.IsNullOrWhiteSpace(templateName))
                template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view template '{templateName}' not found");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Where(l => wanted.Count == 0 || wanted.Any(w => string.Equals(w, l.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (levels.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no matching levels" };

            var taken = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            var created = new List<object>();
            using var tx = new Transaction(doc, "BinaVibe: create_views_for_levels");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var level in levels)
                {
                    var view = ViewPlan.Create(doc, vft.Id, level.Id);
                    var baseName = pattern.Replace("{level}", level.Name);
                    var name = baseName;
                    for (int i = 2; taken.Contains(name); i++) name = $"{baseName} ({i})";
                    try { view.Name = name; taken.Add(name); } catch { /* keep auto name */ }
                    if (template != null) view.ViewTemplateId = template.Id;
                    created.Add(new { view_id = view.Id.Value, name = view.Name, level = level.Name });
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true, ["created"] = created, ["count"] = created.Count };
        }

        // ─── align_viewports ────────────────────────────────────────────
        // Copies the source sheet's MAIN (largest-area) viewport box centre
        // onto each target sheet's main viewport. Legends/schedules are not
        // "main" — targets whose largest viewport is a legend are skipped.
        public static Dictionary<string, object?> AlignViewports(Document doc, JsonElement args)
        {
            var sourceNumber = ArgsHelp.GetString(args, "source_sheet_number") ?? throw new ArgumentException("missing source_sheet_number");
            var targets = new List<string>();
            if (args.TryGetProperty("target_sheet_numbers", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var item in t.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) targets.Add(item.GetString()!);
            if (targets.Count == 0) throw new ArgumentException("target_sheet_numbers is empty");

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            ViewSheet Find(string num) => sheets.FirstOrDefault(s => string.Equals(s.SheetNumber, num, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"sheet '{num}' not found");

            Viewport? MainViewport(ViewSheet s) => s.GetAllViewports()
                .Select(id => doc.GetElement(id) as Viewport).Where(vp => vp != null)
                .Select(vp => vp!)
                .Where(vp => (doc.GetElement(vp.ViewId) as View)?.ViewType != ViewType.Legend)
                .OrderByDescending(vp => { var o = vp.GetBoxOutline(); var d = o.MaximumPoint - o.MinimumPoint; return d.X * d.Y; })
                .FirstOrDefault();

            var srcVp = MainViewport(Find(sourceNumber))
                ?? throw new ArgumentException($"sheet '{sourceNumber}' has no model viewport");
            var target = srcVp.GetBoxCenter();

            var aligned = new List<object>();
            var skipped = new List<object>();
            using var tx = new Transaction(doc, "BinaVibe: align_viewports");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var num in targets)
                {
                    if (string.Equals(num, sourceNumber, StringComparison.OrdinalIgnoreCase)) continue;
                    ViewSheet sheet;
                    try { sheet = Find(num); }
                    catch (ArgumentException) { skipped.Add(new { sheet = num, reason = "not found" }); continue; }
                    var vp = MainViewport(sheet);
                    if (vp == null) { skipped.Add(new { sheet = num, reason = "no model viewport" }); continue; }
                    var before = vp.GetBoxCenter();
                    vp.SetBoxCenter(new XYZ(target.X, target.Y, before.Z));
                    var movedMm = Math.Round(Math.Sqrt(Math.Pow(target.X - before.X, 2) + Math.Pow(target.Y - before.Y, 2)) * 304.8, 1);
                    aligned.Add(new { sheet = num, moved_mm = movedMm });
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true, ["aligned"] = aligned, ["skipped"] = skipped };
        }

        // ─── create_revision ────────────────────────────────────────────
        public static Dictionary<string, object?> CreateRevision(Document doc, JsonElement args)
        {
            var description = ArgsHelp.GetString(args, "description") ?? throw new ArgumentException("missing description");
            var date = ArgsHelp.GetString(args, "date");
            var issuedBy = ArgsHelp.GetString(args, "issued_by");
            var issuedTo = ArgsHelp.GetString(args, "issued_to");

            using var tx = new Transaction(doc, "BinaVibe: create_revision");
            TxGuard.StartSwallowing(tx);
            try
            {
                var rev = Revision.Create(doc);
                rev.Description = description;
                if (!string.IsNullOrWhiteSpace(date)) rev.RevisionDate = date;
                if (!string.IsNullOrWhiteSpace(issuedBy)) rev.IssuedBy = issuedBy;
                if (!string.IsNullOrWhiteSpace(issuedTo)) rev.IssuedTo = issuedTo;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["revision_id"] = rev.Id.Value, ["sequence"] = rev.SequenceNumber,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── set_revision_on_sheets ─────────────────────────────────────
        public static Dictionary<string, object?> SetRevisionOnSheets(Document doc, JsonElement args)
        {
            var revArg = ArgsHelp.GetString(args, "revision") ?? throw new ArgumentException("missing revision");
            var sheetNumbers = new List<string>();
            if (args.TryGetProperty("sheet_numbers", out var sn) && sn.ValueKind == JsonValueKind.Array)
                foreach (var item in sn.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) sheetNumbers.Add(item.GetString()!);

            var revisions = new FilteredElementCollector(doc).OfClass(typeof(Revision)).Cast<Revision>().ToList();
            Revision? rev = null;
            if (int.TryParse(revArg, out var seq))
                rev = revisions.FirstOrDefault(r => r.SequenceNumber == seq);
            if (rev == null)
            {
                var byDesc = revisions.Where(r => (r.Description ?? "").IndexOf(revArg, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (byDesc.Count > 1)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = $"revision '{revArg}' is ambiguous",
                        ["candidates"] = byDesc.Select(r => $"{r.SequenceNumber}: {r.Description}").ToList(),
                    };
                rev = byDesc.FirstOrDefault();
            }
            if (rev == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false, ["error"] = $"no revision matching '{revArg}'",
                    ["candidates"] = revisions.Select(r => $"{r.SequenceNumber}: {r.Description}").ToList(),
                };

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => sheetNumbers.Count == 0 || sheetNumbers.Any(n => string.Equals(n, s.SheetNumber, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int stamped = 0, alreadyHad = 0;
            using var tx = new Transaction(doc, "BinaVibe: set_revision_on_sheets");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var sheet in sheets)
                {
                    var ids = sheet.GetAdditionalRevisionIds().ToList();
                    if (ids.Contains(rev.Id)) { alreadyHad++; continue; }
                    ids.Add(rev.Id);
                    sheet.SetAdditionalRevisionIds(ids);
                    stamped++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["stamped"] = stamped, ["already_had"] = alreadyHad,
                ["sheets"] = sheets.Count, ["revision"] = $"{rev.SequenceNumber}: {rev.Description}",
            };
        }

        // ─── set_workset_bulk ───────────────────────────────────────────
        public static Dictionary<string, object?> SetWorksetBulk(Document doc, JsonElement args)
        {
            var worksetName = ArgsHelp.GetString(args, "workset") ?? throw new ArgumentException("missing workset");
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var predicate = ArgsHelp.GetString(args, "predicate");

            if (!doc.IsWorkshared)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "model is not workshared — worksets do not exist here" };

            var workset = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => string.Equals(w.Name, worksetName, StringComparison.OrdinalIgnoreCase));
            if (workset == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false, ["error"] = $"workset '{worksetName}' not found",
                    ["candidates"] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => w.Name).ToList(),
                };

            var bic = Inspectors.ResolveCategoryRobust(doc, category)
                ?? throw new ArgumentException($"category '{category}' not recognised");
            var els = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                .Where(el => Inspectors.PredicateMatches(el, doc, predicate)).ToList();

            int moved = 0, alreadyThere = 0, skipped = 0;
            using var tx = new Transaction(doc, $"BinaVibe: set_workset_bulk {worksetName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var el in els)
                {
                    var p = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly) { skipped++; continue; }
                    if (p.AsInteger() == workset.Id.IntegerValue) { alreadyThere++; continue; }
                    try { p.Set(workset.Id.IntegerValue); moved++; }
                    catch { skipped++; }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["moved"] = moved, ["already_there"] = alreadyThere,
                ["skipped"] = skipped, ["total"] = els.Count, ["workset"] = workset.Name,
            };
        }

        // ─── fix_warnings ───────────────────────────────────────────────
        // Fixes ONE class of warnings per call, dry-run first by contract.
        // duplicate_mark: blank Mark on all but the first element per
        // warning. identical_instances: delete the overlapping duplicate.
        public static Dictionary<string, object?> FixWarnings(Document doc, JsonElement args)
        {
            var kind = ArgsHelp.GetString(args, "kind") ?? throw new ArgumentException("missing kind");
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? true;

            bool isDupMark = string.Equals(kind, "duplicate_mark", StringComparison.OrdinalIgnoreCase);
            bool isIdentical = string.Equals(kind, "identical_instances", StringComparison.OrdinalIgnoreCase);
            if (!isDupMark && !isIdentical)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"unknown kind '{kind}' — use duplicate_mark or identical_instances" };

            var warnings = doc.GetWarnings().Where(w =>
            {
                var text = w.GetDescriptionText() ?? "";
                return isDupMark
                    ? text.IndexOf("Mark", StringComparison.OrdinalIgnoreCase) >= 0
                        && (text.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0
                            || text.IndexOf("same", StringComparison.OrdinalIgnoreCase) >= 0)
                    : text.IndexOf("identical instances", StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            var details = new List<object>();
            var fixIds = new List<ElementId>();     // identical: elements to delete
            var blankIds = new List<ElementId>();   // dup mark: elements whose Mark blanks
            foreach (var w in warnings)
            {
                var ids = w.GetFailingElements().ToList();
                if (ids.Count < 2) continue;
                // keep the FIRST element of each warning, act on the rest
                foreach (var id in ids.Skip(1))
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    if (isIdentical) fixIds.Add(id); else blankIds.Add(id);
                    details.Add(new
                    {
                        id = id.Value,
                        type_name = (el.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(el.GetTypeId())?.Name : null),
                        action = isIdentical ? "delete" : "blank Mark",
                    });
                }
            }

            if (dryRun)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["kind"] = kind, ["dry_run"] = true,
                    ["warnings_matched"] = warnings.Count,
                    ["would_fix"] = details.Count, ["details"] = details.Take(100).ToList(),
                };

            int fixedCount = 0;
            using var tx = new Transaction(doc, $"BinaVibe: fix_warnings {kind}");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (isIdentical)
                {
                    foreach (var id in fixIds.Distinct())
                        try { if (doc.Delete(id).Count > 0) fixedCount++; } catch { /* already gone */ }
                }
                else
                {
                    foreach (var id in blankIds.Distinct())
                    {
                        var p = doc.GetElement(id)?.LookupParameter("Mark");
                        if (p == null || p.IsReadOnly) continue;
                        try { p.Set(""); fixedCount++; } catch { }
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["kind"] = kind, ["dry_run"] = false,
                ["warnings_matched"] = warnings.Count, ["fixed"] = fixedCount,
                ["details"] = details.Take(100).ToList(),
            };
        }

        // ─── apply_parameter_import ─────────────────────────────────────
        // Write half of the Excel roundtrip. table_text = the attached
        // file's CSV (pane flattens .xlsx). element_id column is the key;
        // category/type_name/level columns are informational and ignored.
        public static Dictionary<string, object?> ApplyParameterImport(Document doc, JsonElement args)
        {
            var text = ArgsHelp.GetString(args, "table_text") ?? throw new ArgumentException("missing table_text");
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? true;

            var rows = ParseCsv(text);
            if (rows.Count < 2)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "table has no data rows" };

            var header = rows[0].Select(h => h.Trim()).ToList();
            var idCol = header.FindIndex(h => string.Equals(h, "element_id", StringComparison.OrdinalIgnoreCase));
            if (idCol < 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no element_id column — export with export_parameters_to_excel first" };
            var ignore = new HashSet<string>(new[] { "element_id", "category", "type_name", "level" }, StringComparer.OrdinalIgnoreCase);

            var changes = new List<(long id, string param, string from, string to)>();
            var errors = new List<object>();
            int unchanged = 0;

            foreach (var row in rows.Skip(1))
            {
                if (row.Length <= idCol || string.IsNullOrWhiteSpace(row[idCol])) continue;
                if (!long.TryParse(row[idCol].Trim(), out var idVal))
                { errors.Add(new { row = row[idCol], error = "element_id not a number" }); continue; }
                var el = doc.GetElement(ElemIds.From(idVal));
                if (el == null) { errors.Add(new { id = idVal, error = "element no longer exists" }); continue; }

                for (int c = 0; c < header.Count && c < row.Length; c++)
                {
                    if (ignore.Contains(header[c])) continue;
                    var newVal = (row[c] ?? "").Trim();
                    var current = (Inspectors.ResolveParamValue(doc, el, header[c]) ?? "").Trim();
                    if (string.Equals(current, newVal, StringComparison.Ordinal)) { unchanged++; continue; }
                    changes.Add((idVal, header[c], current, newVal));
                }
            }

            object Diff() => changes.Take(100).Select(ch => new { id = ch.id, param = ch.param, from = ch.from, to = ch.to }).ToList();

            if (dryRun)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["dry_run"] = true, ["changed"] = 0,
                    ["would_change"] = changes.Count, ["unchanged"] = unchanged,
                    ["changes"] = Diff(), ["errors"] = errors,
                };

            int applied = 0;
            using var tx = new Transaction(doc, "BinaVibe: apply_parameter_import");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var ch in changes)
                {
                    var el = doc.GetElement(ElemIds.From(ch.id));
                    var p = el?.LookupParameter(ch.param);
                    if (p == null) { errors.Add(new { id = ch.id, param = ch.param, error = "parameter not on element" }); continue; }
                    if (p.IsReadOnly) { errors.Add(new { id = ch.id, param = ch.param, error = "read-only" }); continue; }
                    try { SetParamValue(p, ch.to); applied++; }
                    catch (Exception ex) { errors.Add(new { id = ch.id, param = ch.param, error = ex.Message }); }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["dry_run"] = false, ["changed"] = applied,
                ["unchanged"] = unchanged, ["changes"] = Diff(), ["errors"] = errors,
            };
        }

        // Minimal RFC-ish CSV parser: quoted fields, embedded commas and
        // newlines. Shared by ApplyParameterImport only — parse_rule_table
        // has its own richer parser backend-side.
        private static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            var field = new System.Text.StringBuilder();
            var current = new List<string>();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(ch);
                }
                else if (ch == '"') inQuotes = true;
                else if (ch == ',') { current.Add(field.ToString()); field.Clear(); }
                else if (ch == '\r') { /* swallow */ }
                else if (ch == '\n')
                {
                    current.Add(field.ToString()); field.Clear();
                    if (current.Any(f => !string.IsNullOrWhiteSpace(f))) rows.Add(current.ToArray());
                    current = new List<string>();
                }
                else field.Append(ch);
            }
            current.Add(field.ToString());
            if (current.Any(f => !string.IsNullOrWhiteSpace(f))) rows.Add(current.ToArray());
            return rows;
        }

        // ─── change_type ────────────────────────────────────────────────
        public static Dictionary<string, object?> ChangeType(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id") ?? throw new ArgumentException("missing element_id");
            var typeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");
            var el = doc.GetElement(ElemIds.From(id)) ?? throw new ArgumentException($"element {id} not found");

            // Find a type with that name in the same category.
            var newType = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfCategoryId(el.Category.Id)
                .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"type '{typeName}' not found in category {el.Category.Name}");

            long? _changeTypeNewId = null;
            using var tx = new Transaction(doc, $"BinaVibe: change_type {typeName}");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Cross-family (different Family) → ChangeTypeId would keep the
                // source's origin + "Offset from Host", misaligning the result.
                // Place a fresh instance preserving placement, then delete.
                if (el is FamilyInstance fiX && newType is FamilySymbol symX
                    && fiX.Symbol.Family.Id != symX.Family.Id)
                    _changeTypeNewId = ReplaceCrossFamily(doc, fiX, symX)?.Value;
                else
                    el.ChangeTypeId(newType.Id);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                // new_id set only for a cross-family replace (fresh instance);
                // null for an in-place ChangeTypeId (the id is unchanged).
                ["new_id"] = _changeTypeNewId,
                ["element_id"] = id,
                ["new_type"] = typeName,
            };
        }

        // ─── cross-family replace (place + delete, preserve placement) ───────
        // ChangeTypeId across families keeps the source's origin + vertical
        // offset (e.g. tandas cangkung 1231.5mm), so the replacement lands
        // misaligned / at the wrong height. Instead place a FRESH target
        // instance at the source's plan point + facing + level, take the TARGET
        // family's own vertical (matched from an existing sibling, never copied
        // from the source), then delete the source. Caller must wrap in a
        // Transaction. Returns false if the source has no usable location point.
        // Returns the NEW instance's id on success (the agent needs it for any
        // follow-up — the src id is deleted), or null when it can't place.
        private static ElementId? ReplaceCrossFamily(Document doc, FamilyInstance src, FamilySymbol sym)
        {
            if (!(src.Location is LocationPoint lp)) return null;
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

            XYZ pt = lp.Point;
            Level level = doc.GetElement(src.LevelId) as Level;
            XYZ srcFacing = src.FacingOrientation;
            bool flipped = src.HandFlipped;

            var nw = level != null
                ? doc.Create.NewFamilyInstance(pt, sym, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                : doc.Create.NewFamilyInstance(pt, sym, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            doc.Regenerate();

            // Match facing (rotate about the location's Z axis).
            double ang = srcFacing.AngleTo(nw.FacingOrientation);
            if (ang > 1e-9)
            {
                XYZ cross = nw.FacingOrientation.CrossProduct(srcFacing);
                if (cross.Z < 0) ang = -ang;
                ElementTransformUtils.RotateElement(
                    doc, nw.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), ang);
            }
            if (flipped && nw.CanFlipHand) nw.flipHand();

            // Vertical: match an existing instance of the TARGET family (its own
            // convention) — NEVER copy the source's offset. Leave default if none.
            var sibling = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .FirstOrDefault(x => x.Symbol.Family.Id == sym.Family.Id && x.Id != nw.Id);
            if (sibling != null)
            {
                var sp = sibling.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                var np = nw.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                if (sp != null && np != null && !np.IsReadOnly && sp.StorageType == StorageType.Double)
                    np.Set(sp.AsDouble());
            }

            doc.Delete(src.Id);
            return nw.Id;
        }

        private static XYZ BBoxCenter(Element e)
        {
            var bb = e.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) * 0.5;
            return (e.Location as LocationPoint)?.Point ?? XYZ.Zero;
        }

        // Average centre of the given elements (their location point, else bbox
        // centre) — the sensible default pivot for "spin in place".
        private static XYZ ElementsCenter(Document doc, IList<ElementId> ids)
        {
            double sx = 0, sy = 0, sz = 0; int n = 0;
            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                XYZ p = (e.Location as LocationPoint)?.Point ?? BBoxCenter(e);
                sx += p.X; sy += p.Y; sz += p.Z; n++;
            }
            return n == 0 ? XYZ.Zero : new XYZ(sx / n, sy / n, sz / n);
        }

        private static string Fmt(XYZ p) =>
            p == null ? "null" : $"({p.X:F2},{p.Y:F2},{p.Z:F2})";

        // ─── replace_with_reference ──────────────────────────────────────────
        // Clone a user-selected REFERENCE instance 1:1 onto each target's
        // location, then delete the targets. The clone INHERITS the reference's
        // exact type, vertical offset and orientation ("follow this format") —
        // no origin math, no re-placement, which is why this is reliable where
        // NewFamilyInstance/ChangeTypeId misalign (different family origins,
        // e.g. tandas duduk's origin sits off the visible pan). Position comes
        // from each target's VISIBLE (bbox) centre; a target facing the opposite
        // way gets the clone rotated 180°.
        public static Dictionary<string, object?> ReplaceWithReference(Document doc, JsonElement args)
        {
            var refId = ArgsHelp.GetLong(args, "reference_id") ?? throw new ArgumentException("missing reference_id");
            var targetIds = ArgsHelp.GetLongList(args, "target_ids");
            var reference = doc.GetElement(ElemIds.From(refId)) as FamilyInstance
                ?? throw new ArgumentException($"reference {refId} is not a family instance");

            XYZ refCenter = BBoxCenter(reference);
            XYZ refLoc = (reference.Location as LocationPoint)?.Point ?? refCenter;
            XYZ refFacing = reference.FacingOrientation;

            int replaced = 0;
            int facingBad = 0;
            var failures = new List<object>();
            var dbg = new List<object>();
            // The new element ids created by this replace, in target order. The
            // agent MUST use these for any follow-up (rotate/verify) — without
            // them it cannot know which elements it just made (the old ids are
            // deleted) and wastes the turn hunting for its own output.
            var newIds = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: replace_with_reference ({targetIds.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var tid in targetIds)
                {
                    try
                    {
                        if (tid == refId) continue;  // never replace the reference itself
                        var target = doc.GetElement(ElemIds.From(tid)) as FamilyInstance;
                        if (target == null) { failures.Add(new { id = tid, error = "not a family instance" }); continue; }

                        XYZ tgtCenter = BBoxCenter(target);
                        XYZ tgtLoc = (target.Location as LocationPoint)?.Point ?? tgtCenter;
                        XYZ tgtFacing = target.FacingOrientation;  // capture before delete
                        // POSITION by VISIBLE bbox-centre (rotate-then-recentre), NOT
                        // by insertion point. A family's LocationPoint can sit ~270mm
                        // off its visible pan, so aligning insertion points across two
                        // different families — or rotating about that point — flings the
                        // fixture into the wall. Instead: copy the reference, rotate it to
                        // the target's facing about its OWN centre, then translate so its
                        // visible centre lands exactly on the target's. XY only — the
                        // clone keeps the reference family's correct vertical.
                        XYZ shift = new XYZ(tgtCenter.X - refCenter.X, tgtCenter.Y - refCenter.Y, 0);

                        var copied = ElementTransformUtils.CopyElement(doc, reference.Id, shift);
                        doc.Regenerate();
                        var clone = copied.Count > 0 ? doc.GetElement(copied.First()) as FamilyInstance : null;

                        bool facingOk = true;
                        if (clone != null)
                        {
                            // 1. Match the TARGET's facing EXACTLY (any angle, not just a
                            //    0/180 flip), rotating about the clone's OWN centre so it
                            //    spins in place instead of swinging on an off-centre pivot.
                            double ang = refFacing.AngleTo(tgtFacing);
                            if (ang > 1e-9)
                            {
                                XYZ cross = refFacing.CrossProduct(tgtFacing);
                                if (cross.Z < 0) ang = -ang;
                                XYZ c0 = BBoxCenter(clone);
                                ElementTransformUtils.RotateElement(
                                    doc, clone.Id, Line.CreateBound(c0, c0 + XYZ.BasisZ), ang);
                                doc.Regenerate();
                            }
                            // 2. Recentre: land the clone's visible centre on the target's
                            //    (XY only — keep the reference family's vertical).
                            XYZ cc = BBoxCenter(clone);
                            XYZ delta = new XYZ(tgtCenter.X - cc.X, tgtCenter.Y - cc.Y, 0);
                            if (delta.GetLength() > 1e-9)
                            {
                                ElementTransformUtils.MoveElement(doc, clone.Id, delta);
                                doc.Regenerate();
                            }
                            // Verify the clone now points the way the target did.
                            facingOk = clone.FacingOrientation.DotProduct(tgtFacing) > 0.99;
                        }
                        if (!facingOk) facingBad++;

                        doc.Delete(target.Id);
                        replaced++;
                        if (clone != null) newIds.Add(clone.Id.Value);
                        dbg.Add(new
                        {
                            target = tid,
                            new_id = clone?.Id.Value,
                            refLoc = Fmt(refLoc), tgtLoc = Fmt(tgtLoc),
                            refCenter = Fmt(refCenter), tgtCenter = Fmt(tgtCenter),
                            shift = Fmt(shift),
                            facing_ok = facingOk,
                        });
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { id = tid, error = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["replaced"] = replaced,
                // The ids created by this replace — use these directly for any
                // follow-up (rotate/verify). NEVER re-search for the swapped
                // elements; the old ids no longer exist.
                ["new_ids"] = newIds,
                ["facing_ok"] = facingBad == 0,
                ["facing_mismatches"] = facingBad,
                ["failures"] = failures,
                ["debug"] = dbg,
            };
        }

        // ─── delete_elements ────────────────────────────────────────────
        public static Dictionary<string, object?> DeleteElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            using var tx = new Transaction(doc, $"BinaVibe: delete_elements ({ids.Count})");
            TxGuard.StartSwallowing(tx);
            int deleted = 0;
            var failures = new List<object>();
            var refusedDatums = new List<object>();
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        // Datums are load-bearing: deleting a Level deletes its
                        // views and everything constrained to it; a Grid anchors
                        // columns. Refuse, never "fail" — the model must see this
                        // as a policy, not a retryable error.
                        var el = doc.GetElement(ElemIds.From(id));
                        if (el is Level || el is Grid)
                        {
                            refusedDatums.Add(new
                            {
                                id,
                                name = el.Name,
                                kind = el is Level ? "Level" : "Grid",
                            });
                            continue;
                        }
                        var del = doc.Delete(ElemIds.From(id));
                        deleted += del?.Count ?? 0;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { id, error = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            var result = new Dictionary<string, object?>
            {
                // Honest ok: a call where nothing was deleted is not a success,
                // no matter how politely each per-id failure was swallowed.
                ["ok"] = deleted > 0 || (ids.Count == 0),
                ["deleted"] = deleted,
                ["failures"] = failures,
            };
            if (refusedDatums.Count > 0)
            {
                result["refused_datums"] = refusedDatums;
                result["refused_reason"] =
                    "Levels/Grids are datums - deleting a level deletes its views and every element constrained to it. " +
                    "Use modify_level_stack to remove a level, or hide the element instead.";
            }
            return result;
        }

        // ─── duplicate_view ─────────────────────────────────────────────
        /// <summary>
        /// args: { source (view name, string), new_name (string), with_detailing (bool) }
        /// Looks up the source view by name, duplicates it, renames the copy to new_name.
        /// (Folds in what DuplicateViewByName did.)
        /// </summary>
        public static Dictionary<string, object?> DuplicateView(Document doc, JsonElement args)
        {
            var sourceName = ArgsHelp.GetString(args, "source") ?? throw new ArgumentException("missing source (view name)");
            var newName = ArgsHelp.GetString(args, "new_name") ?? throw new ArgumentException("missing new_name");
            var withDetailing = ArgsHelp.GetBool(args, "with_detailing") ?? false;
            var asDependent = ArgsHelp.GetBool(args, "as_dependent") ?? false;

            var src = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{sourceName}' not found");

            using var tx = new Transaction(doc, "BinaVibe: duplicate_view");
            TxGuard.StartSwallowing(tx);
            try
            {
                var opt = asDependent ? ViewDuplicateOption.AsDependent
                        : withDetailing ? ViewDuplicateOption.WithDetailing
                        : ViewDuplicateOption.Duplicate;
                var newId = src.Duplicate(opt);
                var newView = doc.GetElement(newId) as View;
                if (newView != null)
                    newView.Name = newName;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_view_id"] = newId.Value,
                    ["name"] = newView?.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── apply_view_template ────────────────────────────────────────
        /// <summary>
        /// args: { template_name, view_ids?:[long], view_names?:[string] }
        /// Accepts view_ids, view_names, or both. When both absent/empty,
        /// applies to the active view. (Folds in ApplyViewTemplateByName.)
        /// </summary>
        public static Dictionary<string, object?> ApplyViewTemplate(Document doc, JsonElement args)
        {
            var templateName = ArgsHelp.GetString(args, "template_name") ?? throw new ArgumentException("missing template_name");
            var viewIds = ArgsHelp.GetLongList(args, "view_ids");
            var viewNames = ArgsHelp.GetStringList(args, "view_names");

            var template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"template '{templateName}' not found");

            // Resolve view_names to ids and merge with view_ids.
            foreach (var vn in viewNames)
            {
                var found = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, vn, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                    throw new ArgumentException($"view '{vn}' not found");
                viewIds.Add(found.Id.Value);
            }

            // When both lists are empty, default to active view.
            if (viewIds.Count == 0)
            {
                var av = doc.ActiveView;
                if (av == null) throw new InvalidOperationException("no active view and no view_ids/view_names supplied");
                viewIds = new List<long> { av.Id.Value };
            }

            int applied = 0;
            using var tx = new Transaction(doc, "BinaVibe: apply_view_template");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var vid in viewIds)
                {
                    var v = doc.GetElement(ElemIds.From(vid)) as View;
                    if (v == null || v.IsTemplate) continue;
                    v.ViewTemplateId = template.Id;
                    applied++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["template"] = templateName,
                ["applied"] = applied,
            };
        }

        // ─── place_door ─────────────────────────────────────────────────
        public static Dictionary<string, object?> PlaceDoor(Document doc, JsonElement args) =>
            PlaceFamilyOnWall(doc, args, BuiltInCategory.OST_Doors, "place_door");

        // ─── place_window ───────────────────────────────────────────────
        public static Dictionary<string, object?> PlaceWindow(Document doc, JsonElement args) =>
            PlaceFamilyOnWall(doc, args, BuiltInCategory.OST_Windows, "place_window");

        // ─── create_wall ────────────────────────────────────────────────
        public static Dictionary<string, object?> CreateWall(Document doc, JsonElement args)
        {
            var p1 = ArgsHelp.GetPointMm(args, "start_mm") ?? ArgsHelp.GetXyz(args, "start") ?? throw new ArgumentException("missing start [x,y,z]");
            var p2 = ArgsHelp.GetPointMm(args, "end_mm") ?? ArgsHelp.GetXyz(args, "end") ?? throw new ArgumentException("missing end [x,y,z]");
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var typeName = ArgsHelp.GetString(args, "type_name");
            var topLevelName = ArgsHelp.GetString(args, "top_level");
            double? heightArg = ArgsHelp.GetLengthMm(args, "height_mm", "height_ft");
            double height = heightArg ?? (3000.0 / 304.8);

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var level = levels
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"level '{levelName}' not found; levels: {string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name))}");

            // Top constraint. Explicit top_level wins; otherwise, when the
            // caller did not pin an explicit height and a level exists above,
            // top-constrain to the next level up. Unconnected-height walls look
            // identical but ignore level moves — the field-guide anti-pattern
            // that made "floor to floor 3.6m" edits silently miss walls.
            Level? topLevel = null;
            if (!string.IsNullOrEmpty(topLevelName))
            {
                topLevel = levels.FirstOrDefault(l => string.Equals(l.Name, topLevelName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"top_level '{topLevelName}' not found; levels: {string.Join(", ", levels.OrderBy(l => l.Elevation).Select(l => l.Name))}");
            }
            else if (heightArg == null)
            {
                topLevel = levels.Where(l => l.Elevation > level.Elevation + 1e-6)
                                 .OrderBy(l => l.Elevation).FirstOrDefault();
            }

            WallType? wallType = null;
            if (!string.IsNullOrEmpty(typeName))
            {
                var allTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
                wallType = allTypes.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"wall type '{typeName}' not found; closest: {TypeCandidates.Nearest(allTypes.Select(t => t.Name), typeName!)}");
            }
            else
            {
                // No name: resolve the document default instead of the parameterless
                // Wall.Create overload, whose height-less signature silently
                // discarded height_mm whenever type_name was absent.
                wallType = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.WallType)) as WallType;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_wall");
            TxGuard.StartSwallowing(tx);
            try
            {
                var line = Line.CreateBound(p1, p2);
                var wall = wallType != null
                    ? Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false)
                    : Wall.Create(doc, line, level.Id, false);
                if (topLevel != null)
                    wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topLevel.Id);
                // Probe: tx.Commit() triggers the regen. Isolates the regen
                // cost from the Wall.Create call. Big commit time => (B) regen tax.
                var _swCommit = System.Diagnostics.Stopwatch.StartNew();
                tx.Commit();
                _swCommit.Stop();
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][timing] create_wall commit+regen={_swCommit.ElapsedMilliseconds}ms");
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = wall.Id.Value,
                    ["level"] = levelName,
                    ["type_name"] = wallType?.Name ?? "<default>",
                    ["top_level"] = topLevel?.Name,
                    ["height_mode"] = topLevel != null ? "level_to_level" : "unconnected",
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── place_family_instance ──────────────────────────────────────
        public static Dictionary<string, object?> PlaceFamilyInstance(Document doc, JsonElement args)
        {
            var familyType = ArgsHelp.GetString(args, "family_type") ?? throw new ArgumentException("missing family_type");
            var xyzMm = ArgsHelp.GetPointMm(args, "xyz_mm");
            double x = xyzMm?.X ?? ArgsHelp.GetDouble(args, "x") ?? throw new ArgumentException("missing x");
            double y = xyzMm?.Y ?? ArgsHelp.GetDouble(args, "y") ?? throw new ArgumentException("missing y");
            double z = xyzMm?.Z ?? ArgsHelp.GetDouble(args, "z") ?? throw new ArgumentException("missing z");
            var levelName = ArgsHelp.GetString(args, "level");

            // Resolve FamilySymbol by name across all loadable family categories.
            FamilySymbol? symbol = null;
            foreach (FamilySymbol fs in new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>())
            {
                if (string.Equals(fs.Name, familyType, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"{fs.FamilyName} : {fs.Name}", familyType, StringComparison.OrdinalIgnoreCase))
                {
                    symbol = fs;
                    break;
                }
            }
            if (symbol == null)
            {
                // Never a bare "not found". Measured 2026-08-06: the copilot was
                // told a tree type did not exist, had no way to learn what DID,
                // and spiralled — guessing name variants in a loop until the run
                // was killed. An error that lists the real candidates turns
                // guessing into choosing.
                var wanted = familyType.ToLowerInvariant();
                var tokens = wanted.Split(new[] { ' ', '-', '_', ':', '.' },
                                          StringSplitOptions.RemoveEmptyEntries);
                var all = new FilteredElementCollector(doc).WhereElementIsElementType()
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                var near = all
                    .Select(fs => new
                    {
                        Label = $"{fs.FamilyName} : {fs.Name}",
                        Score = tokens.Count(t =>
                            (fs.Name ?? "").ToLowerInvariant().Contains(t)
                            || (fs.FamilyName ?? "").ToLowerInvariant().Contains(t)),
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.Label).Distinct().Take(15).ToList();

                var hint = near.Count > 0
                    ? "Closest types actually in this model: " + string.Join("; ", near)
                    : "Nothing in this model resembles that name. Use list_family_types "
                      + "for the category you want, or search_family_library + load_family "
                      + "to bring one in.";
                throw new ArgumentException(
                    $"family type '{familyType}' is not in this document. {hint}. "
                    + "Pick one of these EXACT names or load a family — do not guess "
                    + "further spellings, they will not appear.");
            }

            // Host-based families (windows, doors, openings, most void-cutters)
            // MUST sit on a host. The unhosted NewFamilyInstance overload below
            // would create an instance whose cutting void intersects nothing —
            // Revit's hard "Instance(s) ... not cutting anything" error (cannot
            // be ignored). Fail fast with guidance so the agent re-routes to a
            // hosted tool instead of leaving broken geometry / a blocked commit.
            var placement = symbol.Family.FamilyPlacementType;
            if (placement == FamilyPlacementType.OneLevelBasedHosted)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"family type '{familyType}' is host-based " +
                                $"(placement={placement}); placed free-standing its cutting void " +
                                "intersects nothing and Revit rejects the commit. Host it on a wall: " +
                                "use place_window / place_door / place_socket_on_wall with " +
                                "host_wall_id (find the wall via find_elements_by_filter / " +
                                "query_geometry first).",
                };

            // Resolve optional level.
            Level? level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"level '{levelName}' not found");
            }

            using var tx = new Transaction(doc, "BinaVibe: place_family_instance");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var pt = new XYZ(x, y, z);
                FamilyInstance fi;
                if (level != null)
                    fi = doc.Create.NewFamilyInstance(pt, symbol, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                else
                    fi = doc.Create.NewFamilyInstance(pt, symbol,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = fi.Id.Value,
                    ["family_type"] = familyType,
                    ["level"] = level?.Name,
                };
            }
            // CommitOrThrow throws AFTER Revit has already rolled back, so only
            // roll back here for a failure mid-build (tx still Started).
            catch { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }
        }

        // ─── load_family ────────────────────────────────────────────────
        // Fetch a family from the BINA cloud library and load it into the
        // open project. Backend enriches the call with download_url (short-
        // lived signed URL), file_type ('rfa'|'rvt'), family_name and
        // source_names. rvt is the container path for SYSTEM families
        // (wall/floor types can't exist as .rfa): background-open the
        // project, EditFamily-load the loadable families and CopyElements
        // the system types named in source_names.
        public static Dictionary<string, object?> LoadFamily(UIApplication app, JsonElement args)
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("no active document");
            var url = ArgsHelp.GetString(args, "download_url") ?? throw new ArgumentException("missing download_url");
            var fileType = ArgsHelp.GetString(args, "file_type") ?? throw new ArgumentException("missing file_type");
            var familyName = ArgsHelp.GetString(args, "family_name") ?? throw new ArgumentException("missing family_name");
            var sourceNames = ArgsHelp.GetStringList(args, "source_names");

            // Idempotent: already loaded → report existing types, no download.
            var existing = FamilyTypesOf(doc, familyName);
            if (existing.Count > 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["family_name"] = familyName,
                    ["loaded_types"] = existing,
                    ["already_loaded"] = true,
                };

            var tempDir = Path.Combine(Path.GetTempPath(), "BinaVibe", "families");
            Directory.CreateDirectory(tempDir);
            var safeName = string.Concat(familyName.Split(Path.GetInvalidFileNameChars()));
            var tempPath = Path.Combine(tempDir, $"{safeName}.{fileType}");
            using (var http = new System.Net.Http.HttpClient())
            {
                var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                File.WriteAllBytes(tempPath, bytes);
            }

            try
            {
                return fileType == "rvt"
                    ? LoadFromRvtContainer(app, doc, tempPath, familyName, sourceNames)
                    : LoadFromRfa(doc, tempPath, familyName);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* temp cleanup only */ }
            }
        }

        private static Dictionary<string, object?> LoadFromRfa(Document doc, string path, string familyName)
        {
            using var tx = new Transaction(doc, "BinaVibe: load_family");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!doc.LoadFamily(path, new OverwriteFamilyLoadOptions(), out var family))
                    throw new InvalidOperationException(
                        $"Revit rejected family file for '{familyName}' (corrupt or newer Revit version?)");
                doc.Regenerate();
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["family_name"] = family.Name,
                    ["loaded_types"] = FamilyTypesOf(doc, family.Name),
                    ["already_loaded"] = false,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        private static Dictionary<string, object?> LoadFromRvtContainer(
            UIApplication app, Document doc, string path, string familyName, List<string> sourceNames)
        {
            var wanted = new HashSet<string>(
                sourceNames.Count > 0 ? sourceNames : new List<string> { familyName },
                StringComparer.OrdinalIgnoreCase);

            var sourceDoc = app.Application.OpenDocumentFile(path);
            var famDocs = new List<Document>();
            try
            {
                // Loadable families → EditFamily BEFORE the target transaction
                // (EditFamily is illegal while any document is modifiable).
                var systemTypeIds = new List<ElementId>();
                foreach (var name in wanted)
                {
                    var fam = new FilteredElementCollector(sourceDoc).OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (fam != null) { famDocs.Add(sourceDoc.EditFamily(fam)); continue; }

                    // Not a loadable family → treat as a system family type
                    // (WallType / FloorType / ...) and copy the type element.
                    var sysType = new FilteredElementCollector(sourceDoc)
                        .WhereElementIsElementType()
                        .Cast<ElementType>()
                        .FirstOrDefault(t =>
                            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.FamilyName, name, StringComparison.OrdinalIgnoreCase));
                    if (sysType != null) systemTypeIds.Add(sysType.Id);
                }
                if (famDocs.Count == 0 && systemTypeIds.Count == 0)
                    throw new ArgumentException(
                        $"none of [{string.Join(", ", wanted)}] found in library container '{Path.GetFileName(path)}'");

                var loaded = new List<string>();
                using var tx = new Transaction(doc, "BinaVibe: load_family");
                TxGuard.StartSwallowing(tx);
                try
                {
                    foreach (var famDoc in famDocs)
                    {
                        var family = famDoc.LoadFamily(doc, new OverwriteFamilyLoadOptions());
                        if (family != null) loaded.AddRange(FamilyTypesOf(doc, family.Name));
                    }
                    if (systemTypeIds.Count > 0)
                    {
                        var copied = ElementTransformUtils.CopyElements(
                            sourceDoc, systemTypeIds, doc, null, new CopyPasteOptions());
                        loaded.AddRange(copied
                            .Select(id => doc.GetElement(id))
                            .OfType<ElementType>()
                            .Select(t => $"{t.FamilyName} : {t.Name}"));
                    }
                    doc.Regenerate();
                    tx.Commit();
                }
                catch { tx.RollBack(); throw; }

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["family_name"] = familyName,
                    ["loaded_types"] = loaded,
                    ["already_loaded"] = false,
                };
            }
            finally
            {
                foreach (var fd in famDocs) { try { fd.Close(false); } catch { } }
                try { sourceDoc.Close(false); } catch { }
            }
        }

        private static List<string> FamilyTypesOf(Document doc, string familyName) =>
            new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => string.Equals(fs.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .Select(fs => $"{fs.FamilyName} : {fs.Name}")
                .ToList();

        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            { overwriteParameterValues = true; return true; }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }

        // ─── move_elements ──────────────────────────────────────────────
        public static Dictionary<string, object?> MoveElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            double dx = ArgsHelp.GetLengthMm(args, "dx_mm", "dx") ?? throw new ArgumentException("missing dx");
            double dy = ArgsHelp.GetLengthMm(args, "dy_mm", "dy") ?? throw new ArgumentException("missing dy");
            double dz = ArgsHelp.GetLengthMm(args, "dz_mm", "dz") ?? throw new ArgumentException("missing dz");

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["moved"] = 0 };

            var elementIds = ids.Select(id => ElemIds.From(id)).ToList();
            var translation = new XYZ(dx, dy, dz);

            using var tx = new Transaction(doc, $"BinaVibe: move_elements ({ids.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                ElementTransformUtils.MoveElements(doc, elementIds, translation);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["moved"] = ids.Count,
            };
        }

        // ─── create_sheet ───────────────────────────────────────────────
        public static Dictionary<string, object?> CreateSheet(Document doc, JsonElement args)
        {
            var number = ArgsHelp.GetString(args, "number") ?? throw new ArgumentException("missing number");
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            var titleblockName = ArgsHelp.GetString(args, "titleblock");

            // Resolve titleblock FamilySymbol — use supplied name, fall back to first available.
            ElementId titleblockId = ElementId.InvalidElementId;
            var titleblocks = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .ToList();

            if (!string.IsNullOrEmpty(titleblockName))
            {
                var match = titleblocks.FirstOrDefault(t =>
                    string.Equals(t.Name, titleblockName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"{t.FamilyName} : {t.Name}", titleblockName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new ArgumentException($"titleblock '{titleblockName}' not found");
                titleblockId = match.Id;
            }
            else if (titleblocks.Count > 0)
            {
                titleblockId = titleblocks[0].Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_sheet");
            TxGuard.StartSwallowing(tx);
            try
            {
                var sheet = ViewSheet.Create(doc, titleblockId);
                sheet.SheetNumber = number;
                sheet.Name = name;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["sheet_id"] = sheet.Id.Value,
                    ["number"] = sheet.SheetNumber,
                    ["name"] = sheet.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── place_view_on_sheet ────────────────────────────────────────
        public static Dictionary<string, object?> PlaceViewOnSheet(Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name") ?? throw new ArgumentException("missing view_name");
            var sheetNumber = ArgsHelp.GetString(args, "sheet_number") ?? throw new ArgumentException("missing sheet_number");
            var pointMm = ArgsHelp.GetPointMm(args, "point_mm");
            double x = pointMm?.X ?? ArgsHelp.GetDouble(args, "x") ?? 0.0;
            double y = pointMm?.Y ?? ArgsHelp.GetDouble(args, "y") ?? 0.0;

            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{viewName}' not found");

            var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"sheet number '{sheetNumber}' not found");

            using var tx = new Transaction(doc, "BinaVibe: place_view_on_sheet");
            TxGuard.StartSwallowing(tx);
            try
            {
                var viewport = Viewport.Create(doc, sheet.Id, view.Id, new XYZ(x, y, 0));
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["viewport_id"] = viewport.Id.Value,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── tag_elements ───────────────────────────────────────────────
        public static Dictionary<string, object?> TagElements(Document doc, UIApplication app, JsonElement args)
        {
            var categoryName = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var viewName = ArgsHelp.GetString(args, "view_name");

            // Resolve target view — named or active.
            View? targetView = null;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                targetView = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            // Resolve BuiltInCategory by friendly or enum name.
            BuiltInCategory bic = BuiltInCategory.INVALID;
            foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, c);
                    if (cat == null) continue;
                    if (string.Equals(cat.Name, categoryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), categoryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), $"OST_{categoryName}", StringComparison.OrdinalIgnoreCase))
                    {
                        bic = c;
                        break;
                    }
                }
                catch { /* some BICs don't have a category in this doc */ }
            }
            if (bic == BuiltInCategory.INVALID)
                throw new ArgumentException($"category '{categoryName}' not recognised");

            // Collect elements of that category visible in the target view.
            var elements = new FilteredElementCollector(doc, targetView.Id)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToList();

            int tagged = 0;
            using var tx = new Transaction(doc, $"BinaVibe: tag_elements ({categoryName})");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Room/Space tags use a different API.
                bool isRoom = bic == BuiltInCategory.OST_Rooms || bic == BuiltInCategory.OST_MEPSpaces;
                foreach (var el in elements)
                {
                    try
                    {
                        if (isRoom && el is SpatialElement spatial)
                        {
                            // Use the midpoint of the room's location as tag origin.
                            var loc = spatial.Location as LocationPoint;
                            if (loc == null) continue;
                            var uv = new UV(loc.Point.X, loc.Point.Y);
                            doc.Create.NewRoomTag(new LinkElementId(el.Id), uv, targetView.Id);
                        }
                        else
                        {
                            var bb = el.get_BoundingBox(targetView);
                            if (bb == null) continue;
                            var mid = (bb.Min + bb.Max) / 2.0;
                            var uv = new UV(mid.X, mid.Y);
                            IndependentTag.Create(doc, targetView.Id, new Reference(el), false,
                                TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                                new XYZ(mid.X, mid.Y, 0));
                        }
                        tagged++;
                    }
                    catch { /* skip elements that cannot be tagged */ }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["tagged"] = tagged,
            };
        }

        // ─── create_level ──────────────────────────────────────────────
        public static Dictionary<string, object?> CreateLevel(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            double elevation = ArgsHelp.GetLengthMm(args, "elevation_mm", "elevation") ?? throw new ArgumentException("missing elevation");

            // Duplicate-name parity with create_levels_batch: report the existing
            // level instead of throwing and rolling back — the single-shot tool is
            // the one the model retries, and a retry after a half-failure used to
            // die here every time.
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["level_id"] = existing.Id.Value,
                    ["name"] = existing.Name,
                    ["elevation"] = existing.Elevation,
                    ["already_existed"] = true,
                };
            }

            using var tx = new Transaction(doc, "BinaVibe: create_level");
            TxGuard.StartSwallowing(tx);
            try
            {
                var level = Level.Create(doc, elevation);
                level.Name = name;
                level.Pinned = true;   // datums pin at birth (field-guide guardrail)
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["level_id"] = level.Id.Value,
                    ["name"] = level.Name,
                    ["elevation"] = elevation,
                    ["pinned"] = true,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── create_grid ────────────────────────────────────────────────
        public static Dictionary<string, object?> CreateGrid(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            var startMm = ArgsHelp.GetPointMm(args, "start_mm");
            var endMm = ArgsHelp.GetPointMm(args, "end_mm");
            double startX = startMm?.X ?? ArgsHelp.GetDouble(args, "start_x") ?? throw new ArgumentException("missing start_x");
            double startY = startMm?.Y ?? ArgsHelp.GetDouble(args, "start_y") ?? throw new ArgumentException("missing start_y");
            double endX = endMm?.X ?? ArgsHelp.GetDouble(args, "end_x") ?? throw new ArgumentException("missing end_x");
            double endY = endMm?.Y ?? ArgsHelp.GetDouble(args, "end_y") ?? throw new ArgumentException("missing end_y");

            var line = Line.CreateBound(new XYZ(startX, startY, 0), new XYZ(endX, endY, 0));

            // Duplicate-name parity with create_levels_batch (same rationale as
            // create_level above): return the existing grid, don't throw.
            var existingGrid = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existingGrid != null)
            {
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["grid_id"] = existingGrid.Id.Value,
                    ["name"] = existingGrid.Name,
                    ["already_existed"] = true,
                };
            }

            using var tx = new Transaction(doc, "BinaVibe: create_grid");
            TxGuard.StartSwallowing(tx);
            try
            {
                var grid = Grid.Create(doc, line);
                grid.Name = name;
                grid.Pinned = true;   // datums pin at birth (field-guide guardrail)
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["grid_id"] = grid.Id.Value,
                    ["name"] = grid.Name,
                    ["pinned"] = true,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── create_room (x,y signature) ────────────────────────────────
        public static Dictionary<string, object?> CreateRoomXY(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var pointMm = ArgsHelp.GetPointMm(args, "point_mm");
            double x = pointMm?.X ?? ArgsHelp.GetDouble(args, "x") ?? throw new ArgumentException("missing x");
            double y = pointMm?.Y ?? ArgsHelp.GetDouble(args, "y") ?? throw new ArgumentException("missing y");
            var name = ArgsHelp.GetString(args, "name");

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            using var tx = new Transaction(doc, "BinaVibe: create_room");
            TxGuard.StartSwallowing(tx);
            try
            {
                var room = doc.Create.NewRoom(level, new UV(x, y));
                if (!string.IsNullOrEmpty(name))
                {
                    var p1 = room.LookupParameter("Name");
                    if (p1 != null && !p1.IsReadOnly) p1.Set(name);
                }
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["room_id"] = room.Id.Value,
                    ["level"] = levelName,
                    ["name"] = name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── color_elements ─────────────────────────────────────────────
        public static Dictionary<string, object?> ColorElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            int r = (int)(ArgsHelp.GetDouble(args, "r") ?? throw new ArgumentException("missing r"));
            int g = (int)(ArgsHelp.GetDouble(args, "g") ?? throw new ArgumentException("missing g"));
            int b = (int)(ArgsHelp.GetDouble(args, "b") ?? throw new ArgumentException("missing b"));
            var viewName = ArgsHelp.GetString(args, "view_name");

            // Resolve target view — named or active.
            View targetView;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                targetView = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            var color = new Color((byte)r, (byte)g, (byte)b);
            var ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetProjectionLineColor(color);

            int colored = 0;
            using var tx = new Transaction(doc, $"BinaVibe: color_elements ({ids.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var id in ids)
                {
                    var eid = ElemIds.From(id);
                    targetView.SetElementOverrides(eid, ogs);
                    colored++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["colored"] = colored,
            };
        }

        // ─── swap_element_type ──────────────────────────────────────────
        public static Dictionary<string, object?> SwapElementType(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var newTypeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");

            // Resolve the target ElementType / FamilySymbol by name.
            var newType = new FilteredElementCollector(doc).WhereElementIsElementType()
                .FirstOrDefault(t => string.Equals(t.Name, newTypeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"type '{newTypeName}' not found in document");

            // Activate FamilySymbol if needed.
            if (newType is FamilySymbol fs && !fs.IsActive)
            {
                using var txActivate = new Transaction(doc, "BinaVibe: activate_symbol");
                TxGuard.StartSwallowing(txActivate);
                fs.Activate();
                doc.Regenerate();
                txActivate.Commit();
            }

            int swapped = 0;
            var failures = new List<object>();
            // ids created by cross-family replaces (fresh instances). For an
            // in-place ChangeTypeId the id is unchanged, so we echo the original.
            // Either way the agent gets the CURRENT id to act on — no hunting.
            var newIds = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: swap_element_type ({ids.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var el = doc.GetElement(ElemIds.From(id));
                        if (el == null) continue;
                        // Cross-family → place + delete (preserve placement).
                        // ChangeTypeId across families misaligns (keeps source
                        // origin/offset).
                        if (el is FamilyInstance fiX && newType is FamilySymbol symX
                            && fiX.Symbol.Family.Id != symX.Family.Id)
                        {
                            var nid = ReplaceCrossFamily(doc, fiX, symX);
                            if (nid != null) { swapped++; newIds.Add(nid.Value); }
                            else failures.Add(new { id, error = "cross-family replace failed (no location point)" });
                            continue;
                        }
                        el.ChangeTypeId(newType.Id);
                        swapped++;
                        newIds.Add(id);   // in-place: id unchanged
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { id, error = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["swapped"] = swapped,
                // Current ids of the swapped elements — use these for follow-up
                // (rotate/verify). NEVER re-search for the swapped elements.
                ["new_ids"] = newIds,
                ["new_type"] = newTypeName,
                ["failures"] = failures,
            };
        }

        // ─── place_text_note ────────────────────────────────────────────
        public static Dictionary<string, object?> PlaceTextNote(Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name") ?? throw new ArgumentException("missing view_name");
            var pointMm = ArgsHelp.GetPointMm(args, "point_mm");
            double x = pointMm?.X ?? ArgsHelp.GetDouble(args, "x") ?? throw new ArgumentException("missing x");
            double y = pointMm?.Y ?? ArgsHelp.GetDouble(args, "y") ?? throw new ArgumentException("missing y");
            var text = ArgsHelp.GetString(args, "text") ?? throw new ArgumentException("missing text");

            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{viewName}' not found");

            // Resolve the default TextNoteType.
            var textNoteTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (textNoteTypeId == ElementId.InvalidElementId)
            {
                var textNoteType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                    .FirstOrDefault();
                if (textNoteType == null)
                    throw new InvalidOperationException("no TextNoteType found in document");
                textNoteTypeId = textNoteType.Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: place_text_note");
            TxGuard.StartSwallowing(tx);
            try
            {
                var note = TextNote.Create(doc, view.Id, new XYZ(x, y, 0), text, textNoteTypeId);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["note_id"] = note.Id.Value,
                    ["view_name"] = viewName,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── hide_isolate_elements ───────────────────────────────────────
        public static Dictionary<string, object?> HideIsolateElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var mode = ArgsHelp.GetString(args, "mode") ?? "hide";
            var viewName = ArgsHelp.GetString(args, "view_name");

            // Resolve target view — named or active.
            View targetView;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                targetView = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            var elementIds = ids.Select(id => ElemIds.From(id)).ToList();

            using var tx = new Transaction(doc, $"BinaVibe: hide_isolate_elements ({mode}, {ids.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (string.Equals(mode, "isolate", StringComparison.OrdinalIgnoreCase))
                    targetView.IsolateElementsTemporary(elementIds);
                else
                    targetView.HideElements(elementIds);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["affected"] = ids.Count,
                ["mode"] = mode,
            };
        }

        // ─── set_category_visibility ─────────────────────────────────────
        /// <summary>Hide / show / isolate whole CATEGORIES in a view (one
        /// SetCategoryHidden op per category — no element enumeration).
        /// mode: hide | show | isolate (isolate = show ONLY the listed cats,
        /// hide every other hideable model category in the view).</summary>
        public static Dictionary<string, object?> SetCategoryVisibility(Document doc, JsonElement args)
        {
            var catNames = ArgsHelp.GetStringList(args, "categories");
            var mode = (ArgsHelp.GetString(args, "mode") ?? "hide").ToLowerInvariant();
            var viewName = ArgsHelp.GetString(args, "view_name");

            if (catNames == null || catNames.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no categories supplied" };

            // Resolve target view — named or active (same rule as HideIsolateElements).
            View view;
            if (!string.IsNullOrEmpty(viewName))
            {
                view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                view = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            // Resolve the requested category names -> BuiltInCategory (reuses the
            // robust resolver the INSPECT tools use). Collect misses to report.
            var targetIds = new HashSet<ElementId>();
            var resolved = new List<string>();
            var unknown = new List<string>();
            foreach (var name in catNames)
            {
                var bic = Inspectors.ResolveCategoryRobust(doc, name);
                if (bic == null) { unknown.Add(name); continue; }
                var cat = Category.GetCategory(doc, bic.Value);
                if (cat == null) { unknown.Add(name); continue; }
                targetIds.Add(cat.Id);
                resolved.Add(cat.Name);
            }
            if (targetIds.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no known categories in [{string.Join(", ", catNames)}]" };

            int changed = 0;
            using var tx = new Transaction(doc, $"BinaVibe: set_category_visibility ({mode}, {resolved.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (mode == "isolate")
                {
                    // Show ONLY the listed categories: walk every hideable model
                    // category in the doc, hide those not requested, show those that are.
                    foreach (Category c in doc.Settings.Categories)
                    {
                        if (c == null || c.CategoryType != CategoryType.Model) continue;
                        if (!view.CanCategoryBeHidden(c.Id)) continue;
                        bool keep = targetIds.Contains(c.Id);
                        if (view.GetCategoryHidden(c.Id) == keep)   // needs to flip
                        {
                            view.SetCategoryHidden(c.Id, !keep);
                            changed++;
                        }
                    }
                }
                else
                {
                    bool hidden = mode != "show";   // hide (default) => true; show => false
                    foreach (var id in targetIds)
                    {
                        if (!view.CanCategoryBeHidden(id)) continue;
                        view.SetCategoryHidden(id, hidden);
                        changed++;
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["mode"] = mode,
                ["categories"] = resolved,
                ["unknown"] = unknown,
                ["changed"] = changed,
                ["view"] = view.Name,
            };
        }

        // ─── rotate_elements ────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], angle_deg:double, axis_x?:double, axis_y?:double }
        /// Rotates elements about a vertical (Z-up) axis through (axis_x, axis_y) by angle_deg degrees.
        /// Uses ElementTransformUtils.RotateElements (Revit 2015+).
        /// </summary>
        public static Dictionary<string, object?> RotateElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            double angleDeg = ArgsHelp.GetDouble(args, "angle_deg") ?? throw new ArgumentException("missing angle_deg");
            double? axisXArg = ArgsHelp.GetLengthMm(args, "axis_x_mm", "axis_x");
            double? axisYArg = ArgsHelp.GetLengthMm(args, "axis_y_mm", "axis_y");

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["rotated"] = 0 };

            var elementIds = ids.Select(id => ElemIds.From(id)).ToList();
            double angleRad = angleDeg * Math.PI / 180.0;

            // Axis: use the caller's (axis_x, axis_y) when given; otherwise spin
            // IN PLACE about the elements' own centre — NEVER default to the
            // project origin (0,0), which would fling them across the model.
            double axisX, axisY;
            if (axisXArg.HasValue || axisYArg.HasValue)
            {
                axisX = axisXArg ?? 0.0;
                axisY = axisYArg ?? 0.0;
            }
            else
            {
                XYZ ctr = ElementsCenter(doc, elementIds);
                axisX = ctr.X; axisY = ctr.Y;
            }

            // Vertical axis through (axisX, axisY): two points along Z.
            var axisLine = Line.CreateBound(
                new XYZ(axisX, axisY, 0),
                new XYZ(axisX, axisY, 10));

            using var tx = new Transaction(doc, $"BinaVibe: rotate_elements ({ids.Count}, {angleDeg}°)");
            TxGuard.StartSwallowing(tx);
            try
            {
                ElementTransformUtils.RotateElements(doc, elementIds, axisLine, angleRad);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["rotated"] = ids.Count,
            };
        }

        // ─── copy_elements ──────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], dx:double, dy:double, dz:double }
        /// Copies elements by translation (dx,dy,dz in feet).
        /// Returns created_ids: the new element ids from the copy operation.
        /// Uses ElementTransformUtils.CopyElements (Revit 2015+).
        /// </summary>
        public static Dictionary<string, object?> CopyElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            double dx = ArgsHelp.GetLengthMm(args, "dx_mm", "dx") ?? throw new ArgumentException("missing dx");
            double dy = ArgsHelp.GetLengthMm(args, "dy_mm", "dy") ?? throw new ArgumentException("missing dy");
            double dz = ArgsHelp.GetLengthMm(args, "dz_mm", "dz") ?? throw new ArgumentException("missing dz");

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["created_ids"] = new List<object>() };

            var elementIds = ids.Select(id => ElemIds.From(id)).ToList();
            var translation = new XYZ(dx, dy, dz);

            using var tx = new Transaction(doc, $"BinaVibe: copy_elements ({ids.Count})");
            TxGuard.StartSwallowing(tx);
            ICollection<ElementId> newIds;
            try
            {
                newIds = ElementTransformUtils.CopyElements(doc, elementIds, translation);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            var createdIds = newIds.Select(eid => (object)eid.Value).ToList<object>();
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created_ids"] = createdIds,
            };
        }

        // ─── mirror_elements ─────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], plane:"x"|"y", origin_x_mm?:double, origin_y_mm?:double, copy?:bool }
        /// Mirrors elements across a vertical plane.
        ///   plane="x" → mirror plane normal along X at x=origin_x_mm  (the YZ plane shifted to origin_x_mm).
        ///   plane="y" → mirror plane normal along Y at y=origin_y_mm  (the XZ plane shifted to origin_y_mm).
        /// copy=true (default) keeps originals; copy=false moves them.
        /// Uses ElementTransformUtils.MirrorElements (Revit 2015+).
        /// </summary>
        public static Dictionary<string, object?> MirrorElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var planeName = ArgsHelp.GetString(args, "plane") ?? "x";
            double originX = ArgsHelp.GetLengthMm(args, "origin_x_mm", "origin_x") ?? 0.0;
            double originY = ArgsHelp.GetLengthMm(args, "origin_y_mm", "origin_y") ?? 0.0;
            bool copy = ArgsHelp.GetBool(args, "copy") ?? true;

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["mirrored"] = 0 };

            var elementIds = ids.Select(id => ElemIds.From(id)).ToList();

            // Build the mirror plane (vertical, Z is the out-of-plane axis).
            Plane mirrorPlane;
            if (planeName.Equals("y", StringComparison.OrdinalIgnoreCase))
                mirrorPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, originY, 0));
            else
                mirrorPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(originX, 0, 0));

            using var tx = new Transaction(doc, $"BinaVibe: mirror_elements ({ids.Count}, plane={planeName})");
            TxGuard.StartSwallowing(tx);
            try
            {
                ElementTransformUtils.MirrorElements(doc, elementIds, mirrorPlane, copy);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["mirrored"] = ids.Count,
            };
        }

        // ─── export_views ────────────────────────────────────────────────
        /// <summary>
        /// args: { view_names:[string], fmt:"pdf"|"dwg", folder?:string }
        /// Exports named views to PDF (Revit 2022+ Document.Export overload with PDFExportOptions)
        /// or DWG (Document.Export with DWGExportOptions). folder defaults to a temp path.
        /// Export methods may not need a Transaction (they are read-only/I-O), but we guard
        /// with try/catch only. Returns files:[] and folder.
        ///
        /// NOTE: PDFExportOptions and the matching Document.Export overload require Revit 2022+.
        /// On older Revit versions this method will throw NotSupportedException at runtime.
        /// DWGExportOptions is available from Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> ExportViews(Document doc, JsonElement args)
        {
            var viewNames = ArgsHelp.GetStringList(args, "view_names");
            var fmt = ArgsHelp.GetString(args, "fmt") ?? "pdf";
            var folder = ArgsHelp.GetString(args, "folder");

            // Default folder: system temp / BinaVibe_exports.
            if (string.IsNullOrEmpty(folder))
                folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BinaVibe_exports");

            System.IO.Directory.CreateDirectory(folder);

            // Resolve views by name → ElementIds.
            var viewIdList = new List<ElementId>();
            var notFound = new List<string>();
            foreach (var vn in viewNames)
            {
                var v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(x => !x.IsTemplate && string.Equals(x.Name, vn, StringComparison.OrdinalIgnoreCase));
                if (v == null) { notFound.Add(vn); continue; }
                viewIdList.Add(v.Id);
            }
            if (viewIdList.Count == 0)
                throw new ArgumentException(
                    $"none of the supplied view names were found: {string.Join(", ", viewNames)}");

            var exportedFiles = new List<object>();

            if (fmt.Equals("dwg", StringComparison.OrdinalIgnoreCase))
            {
                // DWG: export each view individually so we control the file name.
                var dwgOpts = new DWGExportOptions();
                foreach (var viewId in viewIdList)
                {
                    var v = doc.GetElement(viewId) as View;
                    if (v == null) continue;
                    var baseName = SanitizeFileName(v.Name);
                    var single = new List<ElementId> { viewId };
                    try
                    {
                        doc.Export(folder, baseName, single, dwgOpts);
                        exportedFiles.Add((object)System.IO.Path.Combine(folder, baseName + ".dwg"));
                    }
                    catch (Exception ex)
                    {
                        exportedFiles.Add((object)$"ERROR:{v.Name}:{ex.Message}");
                    }
                }
            }
            else
            {
                // PDF: batch export all views in one call (Revit 2022+).
                // PDFExportOptions — available in Revit API 2022+. Compile-time reference
                // is fine if the project targets Revit 2022+ assemblies; throws at runtime
                // on older installs.
                try
                {
                    var pdfOpts = new PDFExportOptions();
                    pdfOpts.FileName = "BinaVibe_export";
                    pdfOpts.Combine = false; // one file per view
                    doc.Export(folder, viewIdList, pdfOpts);
                    // Revit names files as <ViewName>.pdf — report all pdf files created after export.
                    foreach (var viewId in viewIdList)
                    {
                        var v = doc.GetElement(viewId) as View;
                        if (v == null) continue;
                        var expectedFile = System.IO.Path.Combine(folder, SanitizeFileName(v.Name) + ".pdf");
                        exportedFiles.Add((object)expectedFile);
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"PDF export failed (requires Revit 2022+): {ex.Message}", ex);
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["files"] = exportedFiles,
                ["folder"] = folder,
                ["not_found"] = notFound,
            };
        }

        // ─── group_elements ──────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], name?:string }
        /// Groups elements into a model group using Document.Create.NewGroup.
        /// If name is supplied, renames the new group's GroupType (guarding
        /// against duplicate names). Returns {ok, group_id}.
        /// Requires Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> GroupElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var name = ArgsHelp.GetString(args, "name");

            if (ids.Count == 0)
                throw new ArgumentException("element_ids must not be empty");

            var elementIds = ids.Select(id => ElemIds.From(id)).ToList();

            using var tx = new Transaction(doc, "BinaVibe: group_elements");
            TxGuard.StartSwallowing(tx);
            try
            {
                var group = doc.Create.NewGroup(elementIds);

                if (!string.IsNullOrEmpty(name))
                {
                    // Guard duplicate group type names: only rename if not already taken.
                    var existingNames = new FilteredElementCollector(doc)
                        .OfClass(typeof(GroupType))
                        .Select(gt => gt.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (!existingNames.Contains(name))
                        group.GroupType.Name = name;
                }

                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["group_id"] = group.Id.Value,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── pin_elements ────────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], pinned:bool }
        /// Sets element.Pinned for each element in a single Transaction.
        /// pinned=false unpins. Returns {ok, affected}.
        /// Pinned property available Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> PinElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            bool pinned = ArgsHelp.GetBool(args, "pinned") ?? true;

            int affected = 0;
            var failures = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: pin_elements (pinned={pinned}, count={ids.Count})");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var el = doc.GetElement(ElemIds.From(id));
                        if (el == null) continue;
                        el.Pinned = pinned;
                        affected++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { id, error = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["affected"] = affected,
                ["failures"] = failures,
            };
        }

        // ─── join_geometry ───────────────────────────────────────────────
        /// <summary>
        /// args: { element_id_a:long, element_id_b:long }
        /// Joins the geometry of two elements using JoinGeometryUtils.JoinGeometry.
        /// Catches the "already joined" case and returns ok=true so it is idempotent.
        /// Returns {ok}.
        /// JoinGeometryUtils available Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> JoinGeometry(Document doc, JsonElement args)
        {
            var idA = ArgsHelp.GetLong(args, "element_id_a") ?? throw new ArgumentException("missing element_id_a");
            var idB = ArgsHelp.GetLong(args, "element_id_b") ?? throw new ArgumentException("missing element_id_b");

            var elA = doc.GetElement(ElemIds.From(idA)) ?? throw new ArgumentException($"element {idA} not found");
            var elB = doc.GetElement(ElemIds.From(idB)) ?? throw new ArgumentException($"element {idB} not found");

            using var tx = new Transaction(doc, "BinaVibe: join_geometry");
            TxGuard.StartSwallowing(tx);
            try
            {
                // JoinGeometryUtils.AreElementsJoined can tell us if they're already joined;
                // attempting to join again throws, so we check first.
                if (!JoinGeometryUtils.AreElementsJoined(doc, elA, elB))
                    JoinGeometryUtils.JoinGeometry(doc, elA, elB);

                tx.Commit();
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Elements cannot be joined (incompatible category, already joined race, etc.)
                tx.RollBack();
                throw;
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true };
        }

        // ─── renumber_elements ────────────────────────────────────────────
        /// <summary>
        /// args: { category:string, parameter?:string, start?:int, prefix?:string }
        /// Collects all instances of category, orders by ElementId (stable),
        /// then sets LookupParameter(parameter) to prefix+(start+i) for each.
        /// Skips elements where the parameter is missing or read-only.
        /// Returns {ok, renumbered}.
        /// </summary>
        public static Dictionary<string, object?> RenumberElements(Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var parameter = ArgsHelp.GetString(args, "parameter") ?? "Mark";
            int start = (int)(ArgsHelp.GetDouble(args, "start") ?? 1.0);
            var prefix = ArgsHelp.GetString(args, "prefix") ?? "";

            // Resolve BuiltInCategory by friendly or enum name (same as tag_elements).
            BuiltInCategory bic = BuiltInCategory.INVALID;
            foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, c);
                    if (cat == null) continue;
                    if (string.Equals(cat.Name, category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), $"OST_{category}", StringComparison.OrdinalIgnoreCase))
                    {
                        bic = c;
                        break;
                    }
                }
                catch { }
            }
            if (bic == BuiltInCategory.INVALID)
                throw new ArgumentException($"category '{category}' not recognised");

            var level = ArgsHelp.GetString(args, "level");
            var order = ArgsHelp.GetString(args, "order") ?? "id";
            var byRoom = ArgsHelp.GetBool(args, "by_room") ?? false;

            var pool = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Where(el => string.IsNullOrWhiteSpace(level)
                    || string.Equals(doc.GetElement(el.LevelId)?.Name, level, StringComparison.OrdinalIgnoreCase));

            // Plan order = reading order: top-left → bottom-right (−Y then X).
            static XYZ? LocOf(Element el) => el.Location switch
            {
                LocationPoint lp => lp.Point,
                LocationCurve lc => lc.Curve.Evaluate(0.5, true),
                _ => null,
            };
            var elements = string.Equals(order, "position", StringComparison.OrdinalIgnoreCase)
                ? pool.OrderByDescending(el => LocOf(el)?.Y ?? double.MinValue)
                      .ThenBy(el => LocOf(el)?.X ?? double.MaxValue)
                      .ThenBy(el => el.Id.Value).ToList()
                : pool.OrderBy(el => el.Id.Value).ToList();   // stable ordering by ElementId

            int renumbered = 0, skippedNoRoom = 0;
            using var tx = new Transaction(doc, $"BinaVibe: renumber_elements ({category})");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (byRoom)
                {
                    // Door number ikut bilik: Mark = room number, +A/B/C when a
                    // room has several doors (2014-wishlist behavior).
                    var perRoom = new Dictionary<string, int>();
                    foreach (var el in elements)
                    {
                        var fi = el as FamilyInstance;
                        var room = fi?.ToRoom ?? fi?.FromRoom;
                        var roomNumber = room?.Number;
                        if (string.IsNullOrWhiteSpace(roomNumber)) { skippedNoRoom++; continue; }
                        perRoom.TryGetValue(roomNumber!, out var seen);
                        perRoom[roomNumber!] = seen + 1;
                        var suffix = seen == 0 ? "" : ((char)('A' + seen - 1)).ToString();
                        var p = el.LookupParameter(parameter);
                        if (p == null || p.IsReadOnly) continue;
                        try { p.Set(prefix + roomNumber + suffix); renumbered++; }
                        catch { /* skip this element */ }
                    }
                }
                else
                {
                    for (int i = 0; i < elements.Count; i++)
                    {
                        var el = elements[i];
                        var p = el.LookupParameter(parameter);
                        if (p == null || p.IsReadOnly) continue;
                        try
                        {
                            p.Set(prefix + (start + i).ToString());
                            renumbered++;
                        }
                        catch { /* skip this element */ }
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["renumbered"] = renumbered,
            };
            if (byRoom) result["skipped_no_room"] = skippedNoRoom;
            return result;
        }

        // ─── create_view_filter ──────────────────────────────────────────
        /// <summary>
        /// args: { name:string, categories:[string], param:string, op:string, value:string }
        /// Creates a reusable ParameterFilterElement (view/parameter filter) that
        /// selects elements of the given categories where param &lt;op&gt; value.
        /// op: = != &lt; &gt; &lt;= &gt;= contains
        ///
        /// Implementation notes:
        ///   1. categories → List&lt;ElementId&gt; via Category.GetCategory(doc, bic).
        ///   2. param → ElementId by sampling the first instance of the first category
        ///      and calling element.LookupParameter(param).Id.  This is best-effort;
        ///      if no instances exist or the parameter is not found, the call throws.
        ///   3. FilterRule built with ParameterFilterRuleFactory (factory method chosen
        ///      by op and by whether value parses to double).
        ///   4. ParameterFilterElement.Create(doc, name, catIds, epf) in a Transaction.
        ///
        /// Returns {ok, filter_id}.
        /// FLAG: param→ElementId resolution is best-effort (samples first instance).
        /// </summary>
        public static Dictionary<string, object?> CreateViewFilter(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            var categoriesRaw = ArgsHelp.GetStringList(args, "categories");
            var paramName = ArgsHelp.GetString(args, "param") ?? throw new ArgumentException("missing param");
            var op = ArgsHelp.GetString(args, "op") ?? "=";
            var value = ArgsHelp.GetString(args, "value") ?? throw new ArgumentException("missing value");

            if (categoriesRaw.Count == 0)
                throw new ArgumentException("categories must not be empty");

            // 1. Resolve categories → ElementId list.
            var catIds = new List<ElementId>();
            BuiltInCategory? firstBic = null;
            foreach (var catStr in categoriesRaw)
            {
                BuiltInCategory bic;
                if (!TryResolveBuiltInCategory(catStr, out bic))
                    throw new ArgumentException($"category '{catStr}' not recognised");
                catIds.Add(new ElementId(bic));
                firstBic ??= bic;
            }

            // 2. Resolve param → ElementId by sampling first instance of first category.
            ElementId paramId = ElementId.InvalidElementId;
            var sampleEl = firstBic.HasValue
                ? new FilteredElementCollector(doc)
                    .OfCategory(firstBic.Value)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault()
                : null;

            if (sampleEl != null)
            {
                var p = sampleEl.LookupParameter(paramName);
                if (p != null)
                    paramId = p.Id;
            }

            if (paramId == ElementId.InvalidElementId)
            {
                // Fallback: try built-in parameter map for common names.
                paramId = ResolveBuiltInParameterId(doc, paramName)
                    ?? throw new ArgumentException(
                        $"param '{paramName}' not found on any instance of '{categoriesRaw[0]}' and not a known built-in");
            }

            // 3. Build FilterRule from op + value.
            FilterRule rule = BuildFilterRule(paramId, op, value);

            var epf = new ElementParameterFilter(rule);

            // 4. Create ParameterFilterElement.
            using var tx = new Transaction(doc, "BinaVibe: create_view_filter");
            TxGuard.StartSwallowing(tx);
            try
            {
                var pfe = ParameterFilterElement.Create(doc, name, catIds, epf);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["filter_id"] = pfe.Id.Value,
                    ["name"] = pfe.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── apply_view_filter ───────────────────────────────────────────
        /// <summary>
        /// args: { view_name:string, filter_name:string, hide?:bool,
        ///         r?:int, g?:int, b?:int }
        /// Applies an existing ParameterFilterElement to a view.
        ///   hide=true → view.SetFilterVisibility(filter.Id, false)
        ///   r/g/b present → OverrideGraphicSettings with surface + projection color
        ///     (FLAG: solid-fill pattern may be needed for surface color to render in
        ///      3D views; we set surface foreground pattern color but do not force a
        ///      solid-fill pattern — if the element has no fill pattern the color may
        ///      only appear on edges)
        /// Transaction.  Returns {ok}.
        /// </summary>
        public static Dictionary<string, object?> ApplyViewFilter(Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name") ?? throw new ArgumentException("missing view_name");
            var filterName = ArgsHelp.GetString(args, "filter_name") ?? throw new ArgumentException("missing filter_name");
            bool hide = ArgsHelp.GetBool(args, "hide") ?? false;
            double? rRaw = ArgsHelp.GetDouble(args, "r");
            double? gRaw = ArgsHelp.GetDouble(args, "g");
            double? bRaw = ArgsHelp.GetDouble(args, "b");
            bool hasColor = rRaw.HasValue && gRaw.HasValue && bRaw.HasValue;

            // Resolve view by name.
            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate &&
                    string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{viewName}' not found");

            // Resolve filter by name.
            var filter = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(pf => string.Equals(pf.Name, filterName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"filter '{filterName}' not found — create it first with create_view_filter");

            using var tx = new Transaction(doc, $"BinaVibe: apply_view_filter ({filterName})");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Ensure the filter is added to the view first.
                if (!view.GetFilters().Contains(filter.Id))
                    view.AddFilter(filter.Id);

                if (hide)
                {
                    view.SetFilterVisibility(filter.Id, false);
                }
                else if (hasColor)
                {
                    byte r = (byte)RevitWebAppSync.Services.RuntimeCompat.Clamp((int)rRaw!.Value, 0, 255);
                    byte g = (byte)RevitWebAppSync.Services.RuntimeCompat.Clamp((int)gRaw!.Value, 0, 255);
                    byte b = (byte)RevitWebAppSync.Services.RuntimeCompat.Clamp((int)bRaw!.Value, 0, 255);

                    var color = new Color(r, g, b);
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetProjectionLineColor(color);
                    // FLAG: to guarantee surface fill is visible in floor plans the
                    // caller should also ensure a solid-fill pattern is assigned to the
                    // element category's surface pattern (not done here — would require
                    // locating a solid-fill FillPatternElement and assigning it to ogs).
                    view.SetFilterOverrides(filter.Id, ogs);
                }

                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true };
        }

        // ─── create_floor ────────────────────────────────────────────────
        /// <summary>
        /// args: { boundary:[[x,y],...], level:string, type_name?:string }
        /// Creates a floor from a closed 2D boundary (list of [x,y] in feet) on a level.
        ///
        /// API: Floor.Create(doc, IList&lt;CurveLoop&gt;, floorTypeId, levelId)
        ///   Available Revit 2022+.  On older Revit the overload does not exist;
        ///   FLAG: falls back to doc.Create.NewFloor(profile, floorType, level, false)
        ///   (Revit 2015-2021) via reflection-free try/catch structural approach — not
        ///   implemented here; older-Revit users will get an exception.
        ///
        /// Returns {ok, floor_id}.
        /// FLAG: Requires Revit 2022+ for Floor.Create(doc, loops, typeId, levelId).
        /// </summary>
        public static Dictionary<string, object?> CreateFloor(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var typeName = ArgsHelp.GetString(args, "type_name");

            // Parse boundary: mm-preferred [[x,y,z]...] array, legacy feet fallback.
            var pointsMm = ArgsHelp.GetPointListMm(args, "boundary_mm");
            var points = pointsMm.Count > 0 ? pointsMm : ParseBoundary2D(args, "boundary");
            if (points.Count < 3)
                throw new ArgumentException("boundary must have at least 3 points");

            // Build CurveLoop (close it: last point back to first).
            var loop = BuildCurveLoop(points);

            // Resolve level.
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            // Resolve floor type — named (with candidates on miss) or a real
            // floor. FloorType covers structural foundation slabs too, and those
            // often sort first: "first available" once produced an
            // OST_StructuralFoundation the digest correctly counted as zero
            // floors (same trap FindFloorType in DesignSpec guards against).
            var allFloorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
            if (allFloorTypes.Count == 0) throw new InvalidOperationException("no FloorType found in document");
            ElementId floorTypeId;
            if (!string.IsNullOrEmpty(typeName))
            {
                var ft = allFloorTypes
                    .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"floor type '{typeName}' not found; closest: {TypeCandidates.Nearest(allFloorTypes.Select(t => t.Name), typeName!)}");
                floorTypeId = ft.Id;
            }
            else
            {
                var floorsOnly = allFloorTypes.Where(t =>
                    t.Category != null &&
                    t.Category.Id.Value == (long)BuiltInCategory.OST_Floors).ToList();
                floorTypeId = (floorsOnly.Count > 0 ? floorsOnly : allFloorTypes).First().Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_floor");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Revit 2022+ API: Floor.Create(doc, IList<CurveLoop>, ElementId typeId, ElementId levelId)
                var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, level.Id);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["floor_id"] = floor.Id.Value,
                    ["level"] = levelName,
                    ["type_name"] = typeName ?? "(default)",
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── create_roof ────────────────────────────────────────────────
        /// <summary>
        /// args: { boundary_mm:[[x,y],...], level:string, roof_type_name?:string, offset_mm?:number }
        /// Creates a flat (non-sloped) roof from a closed 2D boundary on a level.
        /// Adapted from mcp-servers-for-revit (MIT) — CreateSurfaceElementEventHandler.cs
        /// OST_Roofs case. FLAT footprint roof only: every edge DefinesSlope=false.
        ///
        /// Returns {ok, new_ids, roof_type, level}.
        /// </summary>
        public static Dictionary<string, object?> CreateRoof(Document doc, JsonElement args,
                                                            UIDocument? uidoc = null)
        {
            // Delegates to RoofBuilder so create_roof and build_design share ONE
            // set of strategies. They diverged once already: the extrusion
            // fallback was added here and build_design kept its own
            // footprint-only path, so the tool that matters most still produced
            // roofless buildings.
            var boundary = ArgsHelp.GetPointListMm(args, "boundary_mm");
            if (boundary.Count < 3)
                throw new InvalidOperationException("boundary_mm needs at least 3 [x,y] points (closed loop, mm)");
            if (boundary.Count > 3 && boundary[0].DistanceTo(boundary[boundary.Count - 1]) < 1e-6)
                boundary.RemoveAt(boundary.Count - 1);

            var levelName = ArgsHelp.GetString(args, "level")
                ?? throw new InvalidOperationException("level required");
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"level '{levelName}' not found (use list_levels)");

            var typeName = ArgsHelp.GetString(args, "roof_type_name");
            var roofType = RoofBuilder.FindType(doc, typeName)
                ?? throw new InvalidOperationException("no footprint-capable roof type in this project");

            var slopeDeg = ArgsHelp.GetDouble(args, "slope_deg");
            var slopeEdges = ArgsHelp.GetLongList(args, "slope_edge_indices");
            var offsetFt = ArgsHelp.GetLengthMm(args, "offset_mm") ?? 0;

            // NewFootPrintRoof wants a plan view active. On 2026-08-06 this call
            // was refused three times from a {3D} view and a bungalow shipped
            // with no roof — BuildDesign already guarded this and create_roof
            // did not. Must happen BEFORE any Transaction opens.
            using var viewSwitch = ViewGuard.EnsurePlanView(doc, uidoc);

            // create_roof is level-relative by contract (the drafter names a
            // level and an optional offset), so the absolute bearing elevation
            // RoofBuilder now takes is derived right here — one line, one
            // place, instead of a strategy quietly choosing a different z.
            var res = RoofBuilder.Build(doc, boundary, level, roofType, slopeDeg,
                                        level.Elevation + offsetFt, slopeEdges,
                                        slopeDeg.HasValue ? "gable" : "flat");
            if (!res.Ok)
                throw new InvalidOperationException(
                    "Revit refused to create this roof. Attempts: "
                    + string.Join(" | ", res.Attempts)
                    + $". Context: roof type '{roofType.Name}' (family '{roofType.FamilyName}'), "
                    + $"level '{level.Name}', {boundary.Count} boundary points. "
                    + "Do NOT retry with different arguments — report this to the drafter and "
                    + "offer Architecture > Roof by Footprint, which takes about a minute.");

            if (Math.Abs(offsetFt) > 1e-9 && doc.GetElement(res.Id) is RoofBase rb)
            {
                using var txO = new Transaction(doc, "BINA: roof offset");
                TxGuard.StartSwallowing(txO);
                rb.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(offsetFt);
                TxGuard.CommitOrThrow(txO);
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["new_ids"] = new List<long> { res.Id!.Value },
                ["roof_type"] = roofType.Name, ["level"] = level.Name,
                ["slope_deg"] = slopeDeg, ["sloped_edges"] = res.SlopedEdges,
                ["strategy"] = res.Strategy, ["shape"] = res.Shape,
            };
        }


        // ─── place_window_array ─────────────────────────────────────────
        /// <summary>A row of windows along one wall, placed in ONE transaction
        /// (one undo). Positions are computed here rather than by the model —
        /// spacing arithmetic done in a prompt drifts, and a half-window
        /// hanging off the end of a wall is a silent Revit warning.</summary>
        public static Dictionary<string, object?> PlaceWindowArray(Document doc, JsonElement args)
        {
            var hostId = ArgsHelp.GetLong(args, "host_wall_id")
                ?? throw new ArgumentException("missing host_wall_id");
            var typeName = ArgsHelp.GetString(args, "type_name")
                ?? throw new ArgumentException("missing type_name");
            var sillFt = ArgsHelp.GetLengthMm(args, "sill_mm") ?? (900.0 / 304.8);
            var spacingFt = ArgsHelp.GetLengthMm(args, "spacing_mm");
            var countArg = ArgsHelp.GetLong(args, "count");
            var startFt = ArgsHelp.GetLengthMm(args, "start_offset_mm") ?? 0.0;

            var host = doc.GetElement(ElemIds.From(hostId)) as Wall
                ?? throw new ArgumentException($"host wall {hostId} not found");
            var lc = host.Location as LocationCurve
                ?? throw new InvalidOperationException("host wall has no location curve");
            var curve = lc.Curve;
            var length = curve.Length;
            var usable = length - 2 * startFt;
            if (usable <= 1e-9)
                throw new InvalidOperationException(
                    $"start_offset leaves nothing to place on: wall is {length * 304.8:F0}mm");

            // Two ways to ask: fixed spacing (fit as many as the wall takes) or
            // a count (spread evenly, each window centred in its own bay).
            var offsets = new List<double>();
            if (spacingFt.HasValue && spacingFt.Value > 1e-9)
            {
                var n = (int)Math.Floor(usable / spacingFt.Value) + 1;
                if (countArg.HasValue) n = Math.Min(n, (int)countArg.Value);
                for (int i = 0; i < n; i++) offsets.Add(startFt + i * spacingFt.Value);
            }
            else
            {
                var n = (int)(countArg ?? 0);
                if (n <= 0) throw new ArgumentException("pass spacing_mm or count");
                var step = usable / n;
                for (int i = 0; i < n; i++) offsets.Add(startFt + step * (i + 0.5));
            }

            var symbol = SymbolLookup.Find(doc, BuiltInCategory.OST_Windows, typeName)
                ?? throw new ArgumentException(
                    "no window family is loaded in this project — load one first (load_family).");
            var hostLevel = doc.GetElement(host.LevelId) as Level
                ?? throw new InvalidOperationException("host wall has no level");

            using var tx = new Transaction(doc, "BinaVibe: place_window_array");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var ids = new List<long>();
                foreach (var off in offsets)
                {
                    var p = curve.Evaluate(off / length, true);
                    var fi = doc.Create.NewFamilyInstance(
                        new XYZ(p.X, p.Y, hostLevel.Elevation), symbol, host, hostLevel,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sillFt);
                    ids.Add(fi.Id.Value);
                }
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["new_ids"] = ids, ["placed"] = ids.Count,
                    ["host_wall_id"] = hostId,
                    ["wall_length_mm"] = Math.Round(length * 304.8),
                    ["sill_mm"] = Math.Round(sillFt * 304.8),
                    ["spacing_mm"] = offsets.Count > 1
                        ? Math.Round((offsets[1] - offsets[0]) * 304.8) : (double?)null,
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── create_wall_opening ────────────────────────────────────────
        /// <summary>Rectangular hole in a wall with no family involved — for
        /// service penetrations and pass-throughs.</summary>
        public static Dictionary<string, object?> CreateWallOpening(Document doc, JsonElement args)
        {
            var hostId = ArgsHelp.GetLong(args, "host_wall_id")
                ?? throw new ArgumentException("missing host_wall_id");
            var loc = ArgsHelp.GetPointMm(args, "location_mm")
                ?? throw new ArgumentException("missing location_mm [x,y]");
            var widthFt = ArgsHelp.GetLengthMm(args, "width_mm")
                ?? throw new ArgumentException("missing width_mm");
            var heightFt = ArgsHelp.GetLengthMm(args, "height_mm")
                ?? throw new ArgumentException("missing height_mm");
            var sillFt = ArgsHelp.GetLengthMm(args, "sill_mm") ?? 0.0;

            var host = doc.GetElement(ElemIds.From(hostId)) as Wall
                ?? throw new ArgumentException($"host wall {hostId} not found");
            var lc = host.Location as LocationCurve
                ?? throw new InvalidOperationException("host wall has no location curve");
            var curve = lc.Curve;
            var length = curve.Length;
            var level = doc.GetElement(host.LevelId) as Level
                ?? throw new InvalidOperationException("host wall has no level");

            // The caller gives a plan point; snap it onto the wall so an
            // approximate click still lands a square opening.
            var proj = curve.Project(new XYZ(loc.X, loc.Y, curve.GetEndPoint(0).Z));
            var p0 = curve.GetEndParameter(0);
            var p1r = curve.GetEndParameter(1);
            var mid = Math.Abs(p1r - p0) < 1e-12 ? 0.0 : (proj.Parameter - p0) / (p1r - p0);
            var half = (widthFt / length) / 2.0;
            var lo = Math.Max(0.0, mid - half);
            var hi = Math.Min(1.0, mid + half);
            if (hi - lo < 1e-9)
                throw new InvalidOperationException("opening does not fit on this wall");

            var a = curve.Evaluate(lo, true);
            var b = curve.Evaluate(hi, true);
            var zBase = level.Elevation + sillFt;

            using var tx = new Transaction(doc, "BinaVibe: create_wall_opening");
            TxGuard.StartSwallowing(tx);
            try
            {
                var opening = doc.Create.NewOpening(
                    host,
                    new XYZ(a.X, a.Y, zBase),
                    new XYZ(b.X, b.Y, zBase + heightFt));
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["new_ids"] = new List<long> { opening.Id.Value },
                    ["host_wall_id"] = hostId,
                    ["width_mm"] = Math.Round((hi - lo) * length * 304.8),
                    ["height_mm"] = Math.Round(heightFt * 304.8),
                    ["sill_mm"] = Math.Round(sillFt * 304.8),
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── create_stairs ──────────────────────────────────────────────
        /// <summary>Straight-run stair between two levels. Stairs are authored
        /// through a StairsEditScope, which owns its own transaction — the run
        /// is created in a nested transaction INSIDE the scope, and the scope
        /// is committed after. A Transaction wrapped around the scope would
        /// throw.</summary>
        public static Dictionary<string, object?> CreateStairs(Document doc, JsonElement args)
        {
            var baseName = ArgsHelp.GetString(args, "base_level")
                ?? throw new ArgumentException("missing base_level");
            var topName = ArgsHelp.GetString(args, "top_level")
                ?? throw new ArgumentException("missing top_level");
            var start = ArgsHelp.GetPointMm(args, "location_mm")
                ?? throw new ArgumentException("missing location_mm [x,y] (start of the run)");
            var widthFt = ArgsHelp.GetLengthMm(args, "width_mm") ?? (1200.0 / 304.8);
            var dirDeg = ArgsHelp.GetDouble(args, "direction_deg") ?? 0.0;
            // The backend's grounding layer sizes a run for the REAL rise and
            // ships that budget along. BuildStraightStairRun already refuses —
            // before creating any geometry — when the project's actual
            // StairsType needs more run than the caller reserved; DesignSpec's
            // stairs.main part has always passed it and this tool never did,
            // which is why a too-tight Lane B stair failed halfway built
            // instead of cleanly up front. Null (an ungrounded legacy call)
            // keeps the old unbudgeted behaviour.
            var maxRunFt = ArgsHelp.GetLengthMm(args, "max_run_length_mm");

            Level Find(string n) => new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{n}' not found (use list_levels)");
            var baseLevel = Find(baseName);
            var topLevel = Find(topName);

            var built = BuildStraightStairRun(doc, baseLevel, topLevel,
                new XYZ(start.X, start.Y, 0), dirDeg * Math.PI / 180.0, widthFt,
                maxRunFt);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["new_ids"] = new List<long> { built.StairsId.Value },
                ["run_id"] = built.RunId,
                ["base_level"] = baseLevel.Name, ["top_level"] = topLevel.Name,
                ["risers"] = built.Risers,
                ["width_mm"] = Math.Round(widthFt * 304.8),
            };
        }

        /// <summary>The StairsEditScope core shared by <see cref="CreateStairs"/>
        /// and DesignSpec.cs's <c>stairs.main</c> part: open the scope, create
        /// ONE straight run from <paramref name="start"/> along
        /// <paramref name="dirRad"/>, commit. Neither caller may be inside an
        /// open Transaction when this runs — StairsEditScope owns its own and
        /// throws if nested inside one, which is exactly why `stairs.main`
        /// (DesignSpec.cs) calls this OUTSIDE PartLoop's per-part transaction,
        /// the same way roofs are built after the loop.
        ///
        /// <paramref name="maxRunLenFt"/>, when given, is the caller's own
        /// budget for the run (e.g. the solver's reserved rectangle) — if the
        /// stair type's riser/tread rule needs more than that to cover the
        /// rise, this throws BEFORE any geometry is created rather than
        /// silently clipping the run shorter than the rise it must
        /// serve.</summary>
        private static (ElementId StairsId, long RunId, int? Risers) BuildStraightStairRun(
            Document doc, Level baseLevel, Level topLevel, XYZ start, double dirRad,
            double widthFt, double? maxRunLenFt = null)
        {
            var rise = topLevel.Elevation - baseLevel.Elevation;
            if (rise <= 1e-9)
                throw new InvalidOperationException(
                    $"top level '{topLevel.Name}' is not above base level '{baseLevel.Name}'");

            var scope = new StairsEditScope(doc, "BinaVibe: create_stairs");
            var stairsId = scope.Start(baseLevel.Id, topLevel.Id);
            try
            {
                long runId;
                using (var tx = new Transaction(doc, "BinaVibe: stairs run"))
                {
                    TxGuard.StartSwallowing(tx);
                    var stairs = doc.GetElement(stairsId) as Stairs
                        ?? throw new InvalidOperationException("stairs element not created");
                    // Run length comes from the stair type's riser/tread rule:
                    // treads = risers - 1, so the run spans that many tread depths.
                    var stairsType = doc.GetElement(stairs.GetTypeId()) as StairsType
                        ?? throw new InvalidOperationException("stairs type not found");
                    var maxRiser = stairsType.MaxRiserHeight;
                    var risers = Math.Max(2, (int)Math.Ceiling(rise / Math.Max(maxRiser, 1e-6)));
                    var tread = stairsType.MinTreadDepth;
                    var runLen = Math.Max(tread, tread * (risers - 1));
                    // Slack widened from a flat 50mm to a landing's worth of
                    // room (1000mm — the same `landing_mm` the backend's own
                    // reserve sizing budgets on top of the flight itself,
                    // multistorey_solver._required_run_mm) — final review
                    // 2026-08-13, C1. The backend cannot know this PROJECT's
                    // actual StairsType.MaxRiserHeight/MinTreadDepth (that is
                    // exactly what `stairsType` above reads, at build time);
                    // it sizes the reserved rect for a nominal-and-then-
                    // worst-case riser/tread assumption instead. A real type
                    // stricter than even that worst case eats into the
                    // landing buffer the rect already carries for exactly
                    // this kind of slack — a flat 50mm was tight enough to
                    // refuse a run the reserve was in fact generous enough
                    // for.
                    const double landingSlackFt = 1000.0 / 304.8;
                    if (maxRunLenFt.HasValue && runLen > maxRunLenFt.Value + landingSlackFt)
                        throw new InvalidOperationException(
                            $"a straight run for a {rise * 304.8:F0}mm rise needs "
                            + $"{runLen * 304.8:F0}mm, but the reserved rect only gives "
                            + $"{maxRunLenFt.Value * 304.8:F0}mm along its long axis — "
                            + "rect too small for a run of this rise");

                    var p1 = new XYZ(start.X, start.Y, baseLevel.Elevation);
                    var p2 = new XYZ(start.X + runLen * Math.Cos(dirRad),
                                     start.Y + runLen * Math.Sin(dirRad),
                                     baseLevel.Elevation);
                    var run = StairsRun.CreateStraightRun(
                        doc, stairsId, Line.CreateBound(p1, p2), StairsRunJustification.Center);
                    run.ActualRunWidth = widthFt;
                    runId = run.Id.Value;
                    TxGuard.CommitOrThrow(tx);
                }
                scope.Commit(new SwallowWarnings());
                var made = doc.GetElement(stairsId) as Stairs;
                return (stairsId, runId, made?.ActualRisersNumber);
            }
            catch
            {
                if (scope.IsActive) scope.Cancel();
                throw;
            }
        }

        /// <summary>Entry point for DesignSpec.cs's deferred <c>stairs.main</c>
        /// build — same access level as the rest of Mutators' internals
        /// (DesignSpec is a sibling class in this namespace, not a caller
        /// outside it), kept separate from <see cref="BuildStraightStairRun"/>
        /// so the tolerance/level-name plumbing stays in one place while the
        /// two callers keep their own argument shapes (location+direction for
        /// the tool, a rect for the part).</summary>
        internal static (ElementId StairsId, long RunId, int? Risers) BuildStairsFromRect(
            Document doc, Level baseLevel, Level topLevel,
            double xFt, double yFt, double wFt, double hFt)
        {
            // The run travels the rect's LONG axis, centred on the short one —
            // the solver reserved this rectangle for exactly one straight run
            // (rung 4, task 2); a stair wider than it is long would not fit
            // the space it was given.
            var longIsX = wFt >= hFt;
            var longFt = longIsX ? wFt : hFt;
            var shortFt = longIsX ? hFt : wFt;
            var start = longIsX
                ? new XYZ(xFt, yFt + shortFt / 2, 0)
                : new XYZ(xFt + shortFt / 2, yFt, 0);
            var dirRad = longIsX ? 0.0 : Math.PI / 2.0;
            return BuildStraightStairRun(doc, baseLevel, topLevel, start, dirRad, shortFt, longFt);
        }

        // ─── capture_view_image ─────────────────────────────────────────
        /// <summary>PNG of a view. Data checks catch missing parameters; only a
        /// picture catches a wall-roof gap or a window on the wrong face.</summary>
        public static Dictionary<string, object?> CaptureViewImage(Document doc, JsonElement args)
        {
            var viewId = ArgsHelp.GetLong(args, "view_id");
            var viewName = ArgsHelp.GetString(args, "view_name");
            var px = (int)(ArgsHelp.GetLong(args, "pixel_size") ?? 1600);

            View? view = null;
            if (viewId.HasValue) view = doc.GetElement(ElemIds.From(viewId.Value)) as View;
            else if (!string.IsNullOrWhiteSpace(viewName))
                view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate
                        && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
            else view = doc.ActiveView;
            if (view == null) throw new ArgumentException("view not found (use list_views)");
            if (view.IsTemplate) throw new ArgumentException("cannot export a view template");

            var dir = Path.Combine(Path.GetTempPath(), "bina_views");
            Directory.CreateDirectory(dir);
            var stem = $"view_{view.Id.Value}_{DateTime.Now:yyyyMMdd_HHmmss}";
            var opts = new ImageExportOptions
            {
                FilePath = Path.Combine(dir, stem),
                ExportRange = ExportRange.SetOfViews,
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = px,
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opts);

            // Revit decorates the path with the view name, so the file we asked
            // for is rarely the file on disk — find what it actually wrote.
            var produced = Directory.GetFiles(dir, stem + "*")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
            if (produced == null)
                throw new InvalidOperationException("Revit reported no error but wrote no image");

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["path"] = produced, ["view"] = view.Name,
                ["view_id"] = view.Id.Value,
                ["bytes"] = new FileInfo(produced).Length,
            };
        }

        // ─── set_wall_endpoints ─────────────────────────────────────────
        /// <summary>Stretch/reshape a wall by editing its location line. This is
        /// the only way to resize a footprint without deleting walls — a delete
        /// and recreate destroys every door and window hosted in them, while a
        /// location-curve edit keeps them.</summary>
        public static Dictionary<string, object?> SetWallEndpoints(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new ArgumentException("missing element_id");
            var start = ArgsHelp.GetPointMm(args, "start_mm");
            var end = ArgsHelp.GetPointMm(args, "end_mm");
            if (start == null && end == null)
                throw new ArgumentException("pass start_mm and/or end_mm");

            var wall = doc.GetElement(ElemIds.From(id)) as Wall
                ?? throw new ArgumentException($"wall {id} not found");
            var lc = wall.Location as LocationCurve
                ?? throw new InvalidOperationException("wall has no location curve");
            var cur = lc.Curve;
            var z = cur.GetEndPoint(0).Z;
            var before = cur.Length;
            var p1 = start != null ? new XYZ(start.X, start.Y, z) : cur.GetEndPoint(0);
            var p2 = end != null ? new XYZ(end.X, end.Y, z) : cur.GetEndPoint(1);
            if (p1.DistanceTo(p2) < 1e-6)
                throw new InvalidOperationException("start and end coincide — wall would have zero length");

            using var tx = new Transaction(doc, "BinaVibe: set_wall_endpoints");
            TxGuard.StartSwallowing(tx);
            try
            {
                lc.Curve = Line.CreateBound(p1, p2);
                TxGuard.CommitOrThrow(tx);
                var after = (wall.Location as LocationCurve)!.Curve.Length;
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["element_id"] = id,
                    ["length_before_mm"] = Math.Round(before * 304.8),
                    ["length_after_mm"] = Math.Round(after * 304.8),
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── set_curtain_grid ───────────────────────────────────────────
        /// <summary>Grid spacing on a curtain wall's TYPE, so every panel
        /// updates from one call. Layout is set to Maximum Spacing, which is
        /// what "bays of about 1.5m" means in practice.</summary>
        public static Dictionary<string, object?> SetCurtainGrid(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "wall_id")
                ?? throw new ArgumentException("missing wall_id");
            var vertFt = ArgsHelp.GetLengthMm(args, "vertical_spacing_mm");
            var horizFt = ArgsHelp.GetLengthMm(args, "horizontal_spacing_mm");
            if (vertFt == null && horizFt == null)
                throw new ArgumentException("pass vertical_spacing_mm and/or horizontal_spacing_mm");

            var wall = doc.GetElement(ElemIds.From(id)) as Wall
                ?? throw new ArgumentException($"wall {id} not found");
            var wt = doc.GetElement(wall.GetTypeId()) as WallType
                ?? throw new InvalidOperationException("wall has no type");
            if (wt.Kind != WallKind.Curtain)
                throw new InvalidOperationException(
                    $"wall type '{wt.Name}' is not a curtain wall — create the wall with a curtain type first");

            const int MaximumSpacing = 3;   // CurtainGridLayout
            using var tx = new Transaction(doc, "BinaVibe: set_curtain_grid");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (vertFt.HasValue)
                {
                    wt.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.Set(MaximumSpacing);
                    wt.get_Parameter(BuiltInParameter.SPACING_LENGTH_VERT)?.Set(vertFt.Value);
                }
                if (horizFt.HasValue)
                {
                    wt.get_Parameter(BuiltInParameter.SPACING_LAYOUT_HORIZ)?.Set(MaximumSpacing);
                    wt.get_Parameter(BuiltInParameter.SPACING_LENGTH_HORIZ)?.Set(horizFt.Value);
                }
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["wall_id"] = id, ["wall_type"] = wt.Name,
                    ["vertical_spacing_mm"] = vertFt.HasValue ? Math.Round(vertFt.Value * 304.8) : (double?)null,
                    ["horizontal_spacing_mm"] = horizFt.HasValue ? Math.Round(horizFt.Value * 304.8) : (double?)null,
                    ["note"] = "set on the wall TYPE — every wall of this type updated",
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── set_mullions ───────────────────────────────────────────────
        /// <summary>Assign mullion profiles on a curtain wall TYPE. Reaches BOTH
        /// the vertical and horizontal groups: Revit gives them parameters that
        /// share the display name "Interior Type", so a generic set-parameter
        /// call can only ever write one of them.</summary>
        public static Dictionary<string, object?> SetMullions(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "wall_id")
                ?? throw new ArgumentException("missing wall_id");
            var typeName = ArgsHelp.GetString(args, "mullion_type_name")
                ?? throw new ArgumentException("missing mullion_type_name (use list_family_types)");
            var vertical = ArgsHelp.GetBool(args, "vertical") ?? true;
            var horizontal = ArgsHelp.GetBool(args, "horizontal") ?? true;
            var borders = ArgsHelp.GetBool(args, "borders") ?? true;

            var wall = doc.GetElement(ElemIds.From(id)) as Wall
                ?? throw new ArgumentException($"wall {id} not found");
            var wt = doc.GetElement(wall.GetTypeId()) as WallType
                ?? throw new InvalidOperationException("wall has no type");
            if (wt.Kind != WallKind.Curtain)
                throw new InvalidOperationException(
                    $"wall type '{wt.Name}' is not a curtain wall");

            var mt = new FilteredElementCollector(doc).OfClass(typeof(MullionType)).Cast<MullionType>()
                .FirstOrDefault(m => string.Equals(m.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"mullion type '{typeName}' not found");

            var applied = new List<string>();
            using var tx = new Transaction(doc, "BinaVibe: set_mullions");
            TxGuard.StartSwallowing(tx);
            try
            {
                void Apply(BuiltInParameter bip, string label)
                {
                    var p = wt.get_Parameter(bip);
                    if (p != null && !p.IsReadOnly) { p.Set(mt.Id); applied.Add(label); }
                }
                if (vertical)
                {
                    Apply(BuiltInParameter.AUTO_MULLION_INTERIOR_VERT, "vertical interior");
                    if (borders)
                    {
                        Apply(BuiltInParameter.AUTO_MULLION_BORDER1_VERT, "vertical border 1");
                        Apply(BuiltInParameter.AUTO_MULLION_BORDER2_VERT, "vertical border 2");
                    }
                }
                if (horizontal)
                {
                    Apply(BuiltInParameter.AUTO_MULLION_INTERIOR_HORIZ, "horizontal interior");
                    if (borders)
                    {
                        Apply(BuiltInParameter.AUTO_MULLION_BORDER1_HORIZ, "horizontal border 1");
                        Apply(BuiltInParameter.AUTO_MULLION_BORDER2_HORIZ, "horizontal border 2");
                    }
                }
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["wall_id"] = id, ["wall_type"] = wt.Name,
                    ["mullion_type"] = mt.Name, ["applied"] = applied,
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── create_levels_batch ────────────────────────────────────────
        /// <summary>N levels at a fixed floor-to-floor in ONE transaction —
        /// replaces a 20-call loop for a tower.</summary>
        public static Dictionary<string, object?> CreateLevelsBatch(Document doc, JsonElement args)
        {
            var count = (int)(ArgsHelp.GetLong(args, "count")
                ?? throw new ArgumentException("missing count"));
            if (count <= 0) throw new ArgumentException("count must be positive");
            var f2fFt = ArgsHelp.GetLengthMm(args, "floor_to_floor_mm")
                ?? throw new ArgumentException("missing floor_to_floor_mm");
            var prefix = ArgsHelp.GetString(args, "prefix") ?? "L";
            var startIndex = (int)(ArgsHelp.GetLong(args, "start_index") ?? 1);
            var baseName = ArgsHelp.GetString(args, "base_level");

            double baseElev = 0;
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                var bl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, baseName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"base_level '{baseName}' not found");
                baseElev = bl.Elevation;
            }

            var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var tx = new Transaction(doc, "BinaVibe: create_levels_batch");
            TxGuard.StartSwallowing(tx);
            try
            {
                var made = new List<object>();
                for (int i = 0; i < count; i++)
                {
                    var elev = baseElev + f2fFt * (i + 1);
                    var lvl = Level.Create(doc, elev);
                    lvl.Pinned = true;   // datums pin at birth (field-guide guardrail)
                    // Name collisions throw in Revit, so skip rather than fail the
                    // whole batch — the caller gets told which ones were skipped.
                    var want = $"{prefix}{startIndex + i}";
                    if (!existing.Contains(want))
                    {
                        try { lvl.Name = want; existing.Add(want); } catch { /* keep Revit's default */ }
                    }
                    made.Add(new Dictionary<string, object?>
                    {
                        ["id"] = lvl.Id.Value, ["name"] = lvl.Name,
                        ["elevation_mm"] = Math.Round(elev * 304.8),
                    });
                }
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["created"] = made.Count, ["levels"] = made,
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── create_topography ──────────────────────────────────────────
        /// <summary>Ground surface from boundary points. Uses TopographySurface
        /// rather than Toposolid: Toposolid is Revit 2024+, and this assembly
        /// targets older Revit APIs too, so a Toposolid call would not compile
        /// for every supported version.</summary>
        public static Dictionary<string, object?> CreateTopography(Document doc, JsonElement args)
        {
            var pts = ArgsHelp.GetPointListMm(args, "boundary_mm");
            if (pts.Count < 3)
                throw new InvalidOperationException("boundary_mm needs at least 3 [x,y] points");
            var elevFt = ArgsHelp.GetLengthMm(args, "elevation_mm") ?? 0.0;

            var xyz = pts.Select(p => new XYZ(p.X, p.Y, elevFt)).ToList();

            using var tx = new Transaction(doc, "BinaVibe: create_topography");
            TxGuard.StartSwallowing(tx);
            try
            {
                var topo = TopographySurface.Create(doc, xyz);
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["new_ids"] = new List<long> { topo.Id.Value },
                    ["points"] = xyz.Count,
                    ["elevation_mm"] = Math.Round(elevFt * 304.8),
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── create_ceiling ──────────────────────────────────────────────
        /// <summary>
        /// args: { boundary:[[x,y],...], level:string, type_name?:string }
        /// Creates a ceiling from a closed 2D boundary on a level.
        ///
        /// API: Ceiling.Create(doc, IList&lt;CurveLoop&gt;, ceilingTypeId, levelId)
        /// FLAG: Ceiling.Create is only available in Revit 2022+.  There is no
        ///   equivalent pre-2022 API — this will throw a MissingMethodException on
        ///   older installs (hard requirement).
        ///
        /// Returns {ok, ceiling_id}.
        /// FLAG: Hard requirement — Revit 2022+ only.
        /// </summary>
        public static Dictionary<string, object?> CreateCeiling(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var typeName = ArgsHelp.GetString(args, "type_name");

            var pointsMm = ArgsHelp.GetPointListMm(args, "boundary_mm");
            var points = pointsMm.Count > 0 ? pointsMm : ParseBoundary2D(args, "boundary");
            if (points.Count < 3)
                throw new ArgumentException("boundary must have at least 3 points");

            var loop = BuildCurveLoop(points);

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            // Resolve ceiling type — named or first available.
            ElementId ceilingTypeId;
            if (!string.IsNullOrEmpty(typeName))
            {
                var ct = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>()
                    .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"ceiling type '{typeName}' not found");
                ceilingTypeId = ct.Id;
            }
            else
            {
                var first = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).FirstOrDefault()
                    ?? throw new InvalidOperationException("no CeilingType found in document");
                ceilingTypeId = first.Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_ceiling");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Revit 2022+ API: Ceiling.Create(doc, IList<CurveLoop>, ElementId typeId, ElementId levelId)
                var ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingTypeId, level.Id);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["ceiling_id"] = ceiling.Id.Value,
                    ["level"] = levelName,
                    ["type_name"] = typeName ?? "(default)",
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── filter/boundary helpers ─────────────────────────────────────

        /// <summary>
        /// Parse a JSON array of [x,y] pairs from the args element.
        /// Returns a list of XYZ with z=0.
        /// </summary>
        private static List<XYZ> ParseBoundary2D(JsonElement args, string name)
        {
            var pts = new List<XYZ>();
            if (args.ValueKind != JsonValueKind.Object) return pts;
            if (!args.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return pts;

            foreach (var pt in arr.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array) continue;
                var nums = new List<double>();
                foreach (var n in pt.EnumerateArray())
                {
                    if (n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d))
                        nums.Add(d);
                }
                if (nums.Count >= 2)
                    pts.Add(new XYZ(nums[0], nums[1], 0.0));
            }
            return pts;
        }

        /// <summary>
        /// Build a closed CurveLoop from a list of XYZ points (at least 3).
        /// Connects consecutive points with Line.CreateBound, then closes last→first.
        /// </summary>
        private static CurveLoop BuildCurveLoop(List<XYZ> pts)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                // Skip degenerate zero-length segments.
                if (a.DistanceTo(b) < 1e-6) continue;
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }

        /// <summary>
        /// Resolve a category string (friendly or OST_ enum) to a BuiltInCategory.
        /// Extends the Inspectors version with floors/ceilings/columns.
        /// </summary>
        private static bool TryResolveBuiltInCategory(string category, out BuiltInCategory bic)
        {
            if (category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BuiltInCategory>(category, true, out bic))
                return true;

            bic = category.ToLowerInvariant() switch
            {
                "walls"     => BuiltInCategory.OST_Walls,
                "doors"     => BuiltInCategory.OST_Doors,
                "windows"   => BuiltInCategory.OST_Windows,
                "floors"    => BuiltInCategory.OST_Floors,
                "ceilings"  => BuiltInCategory.OST_Ceilings,
                "rooms"     => BuiltInCategory.OST_Rooms,
                "levels"    => BuiltInCategory.OST_Levels,
                "grids"     => BuiltInCategory.OST_Grids,
                "columns"   => BuiltInCategory.OST_Columns,
                "beams"     => BuiltInCategory.OST_StructuralFraming,
                _           => BuiltInCategory.INVALID,
            };
            return bic != BuiltInCategory.INVALID;
        }

        /// <summary>
        /// Try to resolve a parameter name to an ElementId via common BuiltInParameter
        /// mappings. Returns null when no mapping is found.
        /// FLAG: list is not exhaustive; best-effort.
        /// </summary>
        private static ElementId? ResolveBuiltInParameterId(Document doc, string paramName)
        {
            // Map well-known friendly names → BuiltInParameter.
            BuiltInParameter? bip = paramName.ToLowerInvariant() switch
            {
                "mark"              => BuiltInParameter.ALL_MODEL_MARK,
                "comments"          => BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                "type name"         => BuiltInParameter.ELEM_TYPE_PARAM,
                "level"             => BuiltInParameter.FAMILY_LEVEL_PARAM,
                "base constraint"   => BuiltInParameter.WALL_BASE_CONSTRAINT,
                "top constraint"    => BuiltInParameter.WALL_HEIGHT_TYPE,
                "fire_rating"       => BuiltInParameter.DOOR_FIRE_RATING,
                "fire rating"       => BuiltInParameter.DOOR_FIRE_RATING,
                _                   => null,
            };

            if (!bip.HasValue) return null;
            var id = new ElementId(bip.Value);
            return id == ElementId.InvalidElementId ? null : id;
        }

        /// <summary>
        /// Build a FilterRule for ParameterFilterElement given a parameter ElementId,
        /// comparison op, and string value. Uses ParameterFilterRuleFactory.
        /// Chooses numeric vs string overload based on whether value parses to double.
        /// </summary>
        private static FilterRule BuildFilterRule(ElementId paramId, string op, string value)
        {
            bool isNumeric = double.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double numValue);

            // Tolerance for numeric equality/inequality.
            const double eps = 1e-6;

            if (isNumeric)
            {
                return op switch
                {
                    "="  or "==" => ParameterFilterRuleFactory.CreateEqualsRule(paramId, numValue, eps),
                    "!="         => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, numValue, eps),
                    "<"          => ParameterFilterRuleFactory.CreateLessRule(paramId, numValue, eps),
                    "<="         => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, numValue, eps),
                    ">"          => ParameterFilterRuleFactory.CreateGreaterRule(paramId, numValue, eps),
                    ">="         => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, numValue, eps),
                    // "contains" on a numeric column → treat as string contains.
                    _            => StringRule(paramId, "contains", value),
                };
            }

            return StringRule(paramId, op, value);
        }

        /// <summary>
        /// String FilterRule built directly. Revit 2023+ removed the
        /// ParameterFilterRuleFactory string overloads and the caseSensitive
        /// arg, so we construct FilterStringRule ourselves. Not-equals is the
        /// inverse of equals; string ordering ops fall back to equals.
        /// </summary>
        private static FilterRule StringRule(ElementId paramId, string op, string value)
        {
            var pvp = new ParameterValueProvider(paramId);
            switch (op)
            {
                case "!=":
                    return new FilterInverseRule(
                        new FilterStringRule(pvp, new FilterStringEquals(), value));
                case "contains":
                    return new FilterStringRule(pvp, new FilterStringContains(), value);
                default: // "=", "==", and unsupported string ordering ops
                    return new FilterStringRule(pvp, new FilterStringEquals(), value);
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        }

        // ─── shared place-family-on-wall helper ─────────────────────────
        private static Dictionary<string, object?> PlaceFamilyOnWall(
            Document doc, JsonElement args, BuiltInCategory cat, string label)
        {
            var hostId = ArgsHelp.GetLong(args, "host_wall_id") ?? throw new ArgumentException("missing host_wall_id");
            var typeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");
            var loc = ArgsHelp.GetPointMm(args, "location_mm") ?? ArgsHelp.GetXyz(args, "location") ?? throw new ArgumentException("missing location [x,y,z]");

            var host = doc.GetElement(ElemIds.From(hostId)) as Wall
                ?? throw new ArgumentException($"host wall {hostId} not found");

            // One resolver, shared with build_design (SymbolLookup): the type
            // name, "Family : Type", or a family name all work here now. This
            // used to match the type name and nothing else, so a drafter who
            // pasted what Revit displays got "type not found" with no list of
            // what the project actually has.
            var symbol = SymbolLookup.Find(doc, cat, typeName)
                ?? throw new ArgumentException(
                    $"no {cat} family is loaded in this project, so '{typeName}' cannot be placed. "
                    + "Load a door/window family first (load_family), then retry.");

            using var tx = new Transaction(doc, $"BinaVibe: {label}");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var hostLevel = doc.GetElement(host.LevelId) as Level
                    ?? throw new InvalidOperationException("host wall has no level");
                var fi = doc.Create.NewFamilyInstance(loc, symbol, host, hostLevel,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                // CommitOrThrow, not a bare Commit. A hard Revit error (door wider
                // than its host, "not cutting anything") reaches SwallowWarnings,
                // which returns ProceedWithRollBack — so Commit returns RolledBack
                // and `fi` is dead. A bare Commit ignores that status and reads
                // fi.Id below, which throws on the dead element and lands in the
                // catch, discarding Revit's own message.
                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = fi.Id.Value,
                    ["host_wall_id"] = hostId,
                };
            }
            catch
            {
                // Guarded: when the preprocessor rolled the transaction back, it is
                // no longer Started, and an unguarded RollBack() throws "The
                // transaction has not been started yet (the current status is not
                // 'Started')" which REPLACES the real error. Measured in Revit
                // 2026-07-30 — a 1700mm door on a 1200mm wall reported the
                // transaction complaint instead of the geometry one.
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
        }

        // ─── value helpers ──────────────────────────────────────────────

        // internal, not private: Schedules.Write reuses the exact same
        // storage-type + display-unit handling, so a schedule write and a
        // set_parameter write can never disagree about what "3000" means.
        internal static void SetParamValue(Parameter p, object? value)
        {
            if (value == null) { p.Set(""); return; }
            switch (p.StorageType)
            {
                case StorageType.String: p.Set(value.ToString() ?? ""); break;
                case StorageType.Integer:
                    if (int.TryParse(value.ToString(), out var i)) p.Set(i);
                    else throw new ArgumentException($"value '{value}' is not Integer");
                    break;
                case StorageType.Double:
                    if (double.TryParse(value.ToString(), out var d))
                    {
                        // Measurable params (length/area/...): the caller speaks
                        // PROJECT DISPLAY UNITS (mm on JKR templates) — convert
                        // to internal. Non-measurable Doubles pass through.
                        var pdoc = p.Element?.Document;
                        p.Set(pdoc != null ? Inspectors.ParamUnits.ToInternal(pdoc, p, d) : d);
                    }
                    else throw new ArgumentException($"value '{value}' is not Double");
                    break;
                case StorageType.ElementId:
                    if (long.TryParse(value.ToString(), out var eid)) p.Set(ElemIds.From(eid));
                    else throw new ArgumentException($"value '{value}' is not ElementId");
                    break;
                default: throw new NotSupportedException($"unsupported StorageType {p.StorageType}");
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
                    StorageType.Double => p.AsDouble(),
                    StorageType.ElementId => p.AsElementId().Value,
                    _ => p.AsValueString(),
                };
            }
            catch { return null; }
        }

        // ─── isolate_elements ───────────────────────────────────────────
        public static Dictionary<string, object?> IsolateElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no element ids given" };
            var view = doc.ActiveView ?? throw new InvalidOperationException("no active view");
            var eids = ids.Select(id => ElemIds.From(id)).ToList();
            using var tx = new Transaction(doc, "BinaVibe: isolate_elements");
            TxGuard.StartSwallowing(tx);
            try { view.IsolateElementsTemporary(eids); tx.Commit(); }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?> { ["ok"] = true, ["isolated"] = ids.Count };
        }

        // ─── create_3d_view ─────────────────────────────────────────────
        public static Dictionary<string, object?> Create3dView(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            var name = ArgsHelp.GetString(args, "name");
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("no 3D view family type in this project");

            View3D view;
            using (var tx = new Transaction(doc, "BinaVibe: create_3d_view"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    view = View3D.CreateIsometric(doc, vft.Id);
                    if (!string.IsNullOrWhiteSpace(name)) { try { view.Name = name; } catch { /* dup name */ } }
                    tx.Commit();
                }
                catch { tx.RollBack(); throw; }
            }
            // Open it OUTSIDE the transaction (view activation is not a doc edit).
            try { uidoc.ActiveView = view; } catch { /* best-effort */ }
            return new Dictionary<string, object?> { ["ok"] = true, ["view_id"] = view.Id.Value, ["name"] = view.Name };
        }

        // ─── set_section_box ────────────────────────────────────────────
        // Scope the active 3D view's section box to a level or to elements.
        public static Dictionary<string, object?> SetSectionBox(Document doc, JsonElement args)
        {
            if (!(doc.ActiveView is View3D v3))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "active view is not a 3D view — call create_3d_view first" };

            double marginFt = 1000.0 / 304.8;
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var level = ArgsHelp.GetString(args, "level");

            // Resolve the target element set: explicit ids, or everything on a level.
            List<ElementId> targets;
            string scopedTo;
            if (ids.Count > 0)
            {
                targets = ids.Select(i => ElemIds.From(i)).ToList();
                scopedTo = ids.Count + " element(s)";
            }
            else if (!string.IsNullOrWhiteSpace(level))
            {
                var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => l.Name.IndexOf(level, StringComparison.OrdinalIgnoreCase) >= 0);
                if (lvl == null)
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no level matching '{level}'" };
                targets = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Where(e => e.LevelId != null && e.LevelId == lvl.Id).Select(e => e.Id).ToList();
                scopedTo = lvl.Name;
            }
            else
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "give a level or element_ids" };
            }

            XYZ min = null, max = null;
            foreach (var id in targets)
            {
                var el = doc.GetElement(id);
                var bb = el?.get_BoundingBox(null);
                if (bb == null) continue;
                min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
            }
            if (min == null || max == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no geometry found for the target" };

            var box = new BoundingBoxXYZ
            {
                Min = new XYZ(min.X - marginFt, min.Y - marginFt, min.Z - marginFt),
                Max = new XYZ(max.X + marginFt, max.Y + marginFt, max.Z + marginFt),
            };

            using var tx = new Transaction(doc, "BinaVibe: set_section_box");
            TxGuard.StartSwallowing(tx);
            try { v3.IsSectionBoxActive = true; v3.SetSectionBox(box); tx.Commit(); }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true, ["scoped_to"] = scopedTo };
        }

        // ─── create_project_parameter ───────────────────────────────────
        // Creates a (shared-backed) project parameter and binds it to categories.
        public static Dictionary<string, object?> CreateProjectParameter(UIApplication app, JsonElement args)
        {
            var doc = app.ActiveUIDocument.Document;
            var application = app.Application;
            string name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            string ptype = (ArgsHelp.GetString(args, "param_type") ?? "yesno").ToLowerInvariant();
            bool instance = ArgsHelp.GetBool(args, "instance") ?? true;
            var catNames = ArgsHelp.GetStringList(args, "categories");

            ForgeTypeId spec;
            switch (ptype)
            {
                case "yesno": case "boolean": case "bool": spec = SpecTypeId.Boolean.YesNo; break;
                case "integer": case "int": spec = SpecTypeId.Int.Integer; break;
                case "number": spec = SpecTypeId.Number; break;
                case "length": spec = SpecTypeId.Length; break;
                case "area": spec = SpecTypeId.Area; break;
                case "angle": spec = SpecTypeId.Angle; break;
                default: spec = SpecTypeId.String.Text; break;
            }

            // Build the category set (named categories, else current selection).
            var catSet = application.Create.NewCategorySet();
            if (catNames != null && catNames.Count > 0)
            {
                foreach (var cn in catNames)
                    if (TryResolveCatOrLive(doc, cn, out var bic))
                    {
                        var cat = Category.GetCategory(doc, bic);
                        if (cat != null && cat.AllowsBoundParameters) catSet.Insert(cat);
                    }
            }
            else
            {
                foreach (var id in app.ActiveUIDocument.Selection.GetElementIds())
                {
                    var cat = doc.GetElement(id)?.Category;
                    if (cat != null && cat.AllowsBoundParameters) catSet.Insert(cat);
                }
            }
            if (catSet.IsEmpty)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no bindable categories (pass categories or select elements first)" };

            // Define in a temp shared-parameter file (Revit requires a definition).
            string prevFile = application.SharedParametersFilename;
            try
            {
                string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BINA_SharedParams.txt");
                if (!System.IO.File.Exists(tempPath)) System.IO.File.WriteAllText(tempPath, "");
                application.SharedParametersFilename = tempPath;
                var spFile = application.OpenSharedParameterFile();
                var group = spFile.Groups.get_Item("BINA") ?? spFile.Groups.Create("BINA");
                var def = group.Definitions.get_Item(name) as ExternalDefinition;
                if (def == null)
                {
                    var opt = new ExternalDefinitionCreationOptions(name, spec);
                    def = group.Definitions.Create(opt) as ExternalDefinition;
                }

                using var tx = new Transaction(doc, "BinaVibe: create_project_parameter");
                TxGuard.StartSwallowing(tx);
                try
                {
                    Binding binding = instance
                        ? application.Create.NewInstanceBinding(catSet)
                        : (Binding)application.Create.NewTypeBinding(catSet);
                    if (!doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data))
                        doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Data);
                    tx.Commit();
                }
                catch { tx.RollBack(); throw; }
            }
            finally { if (prevFile != null) application.SharedParametersFilename = prevFile; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["name"] = name, ["param_type"] = ptype,
                ["instance"] = instance, ["categories"] = catSet.Size,
            };
        }

        // ─── place_in_each_room ─────────────────────────────────────────
        public static Dictionary<string, object?> PlaceInEachRoom(Document doc, JsonElement args)
        {
            string familyType = ArgsHelp.GetString(args, "family_type") ?? throw new ArgumentException("missing family_type");
            string roomsNamed = ArgsHelp.GetString(args, "rooms_named");

            var sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Name, familyType, StringComparison.OrdinalIgnoreCase)
                    || (s.FamilyName + ": " + s.Name).IndexOf(familyType, StringComparison.OrdinalIgnoreCase) >= 0
                    || s.FamilyName.IndexOf(familyType, StringComparison.OrdinalIgnoreCase) >= 0);
            if (sym == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no loaded family/type matching '{familyType}'" };

            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>()
                .Where(r => r.Area > 0 && (string.IsNullOrEmpty(roomsNamed) || (r.Name ?? "").IndexOf(roomsNamed, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            int placed = 0, skipped = 0;
            using var tx = new Transaction(doc, "BinaVibe: place_in_each_room");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                foreach (var room in rooms)
                {
                    if (!(room.Location is LocationPoint lp)) { skipped++; continue; }
                    var level = doc.GetElement(room.LevelId) as Level;
                    try
                    {
                        doc.Create.NewFamilyInstance(lp.Point, sym, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        placed++;
                    }
                    catch { skipped++; }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?> { ["ok"] = true, ["placed"] = placed, ["skipped"] = skipped, ["family"] = sym.Name };
        }

        // ─── set_parameter_where (spatial: elements in matching rooms) ───
        public static Dictionary<string, object?> SetParameterWhere(Document doc, JsonElement args)
        {
            string category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            string param = ArgsHelp.GetString(args, "parameter") ?? throw new ArgumentException("missing parameter");
            string value = ArgsHelp.GetString(args, "value") ?? "";
            string inRooms = ArgsHelp.GetString(args, "in_rooms_named") ?? throw new ArgumentException("missing in_rooms_named");
            if (!TryResolveCatOrLive(doc, category, out var bic))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };

            var roomIds = new HashSet<long>(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>()
                .Where(r => r.Area > 0 && (r.Name ?? "").IndexOf(inRooms, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(r => (long)r.Id.Value));
            if (roomIds.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no rooms matching '{inRooms}'" };

            var els = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToList();
            int matched = 0, setCount = 0;
            using var tx = new Transaction(doc, "BinaVibe: set_parameter_where");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var e in els)
                {
                    bool inMatch = false;
                    if (e is FamilyInstance fi)
                    {
                        try { var rm = fi.Room; if (rm != null && roomIds.Contains(rm.Id.Value)) inMatch = true; } catch { }
                        if (!inMatch) { try { var fr = fi.FromRoom; if (fr != null && roomIds.Contains(fr.Id.Value)) inMatch = true; } catch { } }
                        if (!inMatch) { try { var tr = fi.ToRoom; if (tr != null && roomIds.Contains(tr.Id.Value)) inMatch = true; } catch { } }
                    }
                    if (!inMatch && e.Location is LocationPoint lp)
                    {
                        try { var rm = doc.GetRoomAtPoint(lp.Point); if (rm != null && roomIds.Contains(rm.Id.Value)) inMatch = true; } catch { }
                    }
                    if (!inMatch) continue;
                    matched++;
                    var p = e.LookupParameter(param);
                    if (p != null && !p.IsReadOnly && SetParamFromString(p, value)) setCount++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?> { ["ok"] = true, ["set"] = setCount, ["matched"] = matched, ["rooms"] = roomIds.Count };
        }

        // Set a parameter from a string, coercing to its storage type.
        private static bool SetParamFromString(Parameter p, string value)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.Set(value);
                    case StorageType.Integer:
                        if (int.TryParse(value, out var iv)) return p.Set(iv);
                        return p.Set(value.Trim().ToLowerInvariant() == "yes" || value.Trim() == "1" || value.Trim().ToLowerInvariant() == "true" ? 1 : 0);
                    case StorageType.Double:
                        if (double.TryParse(value, out var dv))
                        {
                            var pdoc2 = p.Element?.Document;
                            return p.Set(pdoc2 != null ? Inspectors.ParamUnits.ToInternal(pdoc2, p, dv) : dv);
                        }
                        return false;
                    default: return false;
                }
            }
            catch { return false; }
        }

        // ─── rename_elements ────────────────────────────────────────────
        /// <summary>
        /// args: { category, find, replace, scope?, dry_run? }
        ///
        /// ``scope`` selects WHICH project-browser node to rename in — the reason
        /// this tool grew: "update family door name dari jkrAR18 ke jkrAR25" could
        /// not be served at all. Views/sheets/schedules were covered, but a
        /// category collector with WhereElementIsNotElementType() returns
        /// INSTANCES, and Family elements are not in it at all, so family and type
        /// names — the ones a drafter actually sees under Families in the project
        /// browser — were unreachable. The agent fell back to hand-written C#,
        /// which failed to compile and spent 92s in the repair loop
        /// (Langfuse e90115fc, 2026-07-28).
        ///
        ///   families  Family elements (the "jkrAR18_door" node itself)
        ///   types     ElementType / FamilySymbol names (each type under it)
        ///   instances element instances (previous behaviour)
        ///   auto      families + types when a category is given, else instances
        ///
        /// ``category`` also takes "Groups" / "Model Groups" / "Detail Groups",
        /// which rename the GroupType. A group's name is an Element.Name
        /// property, not a Parameter, so set_parameter cannot reach it and this
        /// is the only path. ``scope`` is ignored there — a group has one node.
        ///
        /// ``dry_run`` returns exactly what WOULD change without opening a
        /// transaction. A find/replace across a project browser is wide and
        /// awkward to undo by hand, so the agent can show the diff and let the
        /// drafter confirm first.
        ///
        /// Names Revit refuses come back as ``skipped`` (count) and ``skips``
        /// ([{id, name, reason}], first 8) — a duplicate name is the normal
        /// failure and the count alone cannot say so.
        /// </summary>
        public static Dictionary<string, object?> RenameElements(Document doc, JsonElement args)
        {
            string category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            string find = ArgsHelp.GetString(args, "find") ?? throw new ArgumentException("missing find");
            string replace = ArgsHelp.GetString(args, "replace") ?? "";
            string scope = (ArgsHelp.GetString(args, "scope") ?? "auto").ToLowerInvariant();
            bool dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;

            var lc = category.ToLowerInvariant();
            List<Element> targets;
            if (lc == "views" || lc == "view")
                targets = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && !(v is ViewSchedule) && v.ViewType != ViewType.DrawingSheet).Cast<Element>().ToList();
            else if (lc == "sheets" || lc == "sheet")
                targets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<Element>().ToList();
            else if (lc == "schedules" || lc == "schedule")
                targets = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate).Cast<Element>().ToList();
            else if (lc == "families" || lc == "family")
                // Every loadable family in the document, regardless of category —
                // "rename jkrAR17 to jkrAR26" is usually a naming-standard sweep
                // that spans categories.
                targets = new FilteredElementCollector(doc).OfClass(typeof(Family)).ToList();
            else if (lc == "groups" || lc == "group"
                  || lc == "model groups" || lc == "model group"
                  || lc == "detail groups" || lc == "detail group")
            {
                // GroupType.Name is a direct Element property, not a Parameter, so
                // set_parameter can never reach it and this tool is the only path.
                // OfCategory on the internal IOS categories does not return
                // GroupType reliably across versions — collect by class and filter
                // on the type's own Category, the shape ListModelGroups uses
                // (Inspectors.cs:1551).
                bool wantModel = !lc.StartsWith("detail");
                bool wantDetail = !lc.StartsWith("model");
                targets = new FilteredElementCollector(doc).OfClass(typeof(GroupType))
                    .Cast<GroupType>()
                    .Where(gt => gt.Category != null
                              && ((wantModel && gt.Category.Id.Value == (long)BuiltInCategory.OST_IOSModelGroups)
                               || (wantDetail && gt.Category.Id.Value == (long)BuiltInCategory.OST_IOSDetailGroups)))
                    .Cast<Element>().ToList();
            }
            else if (TryResolveCatOrLive(doc, category, out var bic))
            {
                targets = new List<Element>();
                bool wantFam = scope == "families" || scope == "family" || scope == "auto";
                bool wantTyp = scope == "types" || scope == "type" || scope == "auto";
                bool wantIns = scope == "instances" || scope == "instance";

                if (wantFam)
                    // Family has no Category filter that behaves consistently across
                    // versions, so filter by the family's own CategoryId.
                    targets.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Family))
                        .Cast<Family>()
                        .Where(f => f.FamilyCategoryId != null && f.FamilyCategoryId.Value == (long)bic)
                        .Cast<Element>());
                if (wantTyp)
                    targets.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(bic).WhereElementIsElementType().ToList());
                if (wantIns)
                    targets.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(bic).WhereElementIsNotElementType().ToList());
            }
            else
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };

            // Preview without a transaction. Reported as would_rename, never
            // renamed, so a caller cannot mistake a preview for a completed edit.
            if (dryRun)
            {
                var preview = new List<object>();
                int would = 0;
                foreach (var e in targets)
                {
                    var nm0 = e.Name;
                    if (string.IsNullOrEmpty(nm0) || !nm0.Contains(find)) continue;
                    var nn0 = nm0.Replace(find, replace);
                    if (nn0 == nm0 || string.IsNullOrWhiteSpace(nn0)) continue;
                    would++;
                    if (preview.Count < 25) preview.Add(new Dictionary<string, object?>
                    { ["id"] = e.Id.Value, ["from"] = nm0, ["to"] = nn0, ["kind"] = KindOf(e) });
                }
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["dry_run"] = true, ["scope"] = scope,
                    ["would_rename"] = would, ["preview"] = preview,
                    ["nothing"] = would == 0,
                    ["headline"] = would + " name(s) would change (nothing renamed yet)",
                };
            }

            int renamed = 0, matched = 0; var examples = new List<object>();
            var skips = new List<object>();
            using var tx = new Transaction(doc, "BinaVibe: rename_elements");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var e in targets)
                {
                    var name = e.Name;
                    if (string.IsNullOrEmpty(name) || !name.Contains(find)) continue;
                    var nn = name.Replace(find, replace);
                    if (nn == name || string.IsNullOrWhiteSpace(nn)) continue;
                    matched++;
                    try { e.Name = nn; renamed++; if (examples.Count < 8) examples.Add(name + " → " + nn); }
                    catch (Exception ex)
                    {
                        // Duplicate or read-only name. A bare count reads as a
                        // mystery on groups, where a name collision is the
                        // normal failure — carry Revit's own message back.
                        if (skips.Count < 8) skips.Add(new Dictionary<string, object?>
                        { ["id"] = e.Id.Value, ["name"] = name, ["reason"] = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["scope"] = scope,
                ["renamed"] = renamed,
                ["matched"] = matched,
                // A duplicate or read-only name throws per element and is skipped;
                // reporting the count stops "renamed 3" reading as "all 40 done".
                ["skipped"] = matched - renamed,
                // …and why, for the first few — a duplicate group-type name is
                // the usual cause and is unguessable from the count alone.
                ["skips"] = skips,
                ["examples"] = examples,
                ["nothing"] = renamed == 0,
                ["headline"] = renamed + " of " + matched + " renamed (" + scope + ")",
            };
        }

        // Which project-browser node an element belongs to — so a preview can say
        // whether a row is a family, a type or an instance.
        private static string KindOf(Element e) =>
            e is Family ? "family" : (e is ElementType ? "type" : "instance");

        // ─── color_by_parameter ─────────────────────────────────────────
        public static Dictionary<string, object?> ColorByParameter(Document doc, JsonElement args)
        {
            string category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            string param = ArgsHelp.GetString(args, "parameter") ?? throw new ArgumentException("missing parameter");
            if (!TryResolveCatOrLive(doc, category, out var bic))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };
            var view = doc.ActiveView ?? throw new InvalidOperationException("no active view");

            var rules = new List<(string match, Color color)>();
            if (args.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var r in arr.EnumerateArray())
                {
                    string m = r.TryGetProperty("match", out var mm) ? mm.GetString() : null;
                    string c = r.TryGetProperty("color", out var cc) ? cc.GetString() : null;
                    var col = ParseColor(c);
                    if (m != null && col != null) rules.Add((m, col));
                }
            if (rules.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no usable rules (need [{match,color}])" };

            var solid = GetSolidFillId(doc);
            var els = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToList();
            var byRule = new Dictionary<string, int>();
            int colored = 0;
            using var tx = new Transaction(doc, "BinaVibe: color_by_parameter");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var e in els)
                {
                    var val = Inspectors.ResolveParamValue(doc, e, param);
                    Color chosen = null; string key = null;
                    foreach (var (m, col) in rules)
                    {
                        bool hit = m == "*"
                            || (m.Equals("empty", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(val))
                            || string.Equals(val, m, StringComparison.OrdinalIgnoreCase);
                        if (hit) { chosen = col; key = m; break; }
                    }
                    if (chosen == null) continue;
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(chosen);
                    ogs.SetSurfaceForegroundPatternColor(chosen);
                    if (solid != null) ogs.SetSurfaceForegroundPatternId(solid);
                    try { view.SetElementOverrides(e.Id, ogs); colored++; byRule[key] = (byRule.TryGetValue(key, out var n) ? n : 0) + 1; }
                    catch { /* some elements can't be overridden in this view */ }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?> { ["ok"] = true, ["colored"] = colored, ["by_rule"] = byRule };
        }

        private static Color ParseColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.Contains(","))
            {
                var p = s.Split(',');
                if (p.Length >= 3 && byte.TryParse(p[0].Trim(), out var r) && byte.TryParse(p[1].Trim(), out var g) && byte.TryParse(p[2].Trim(), out var b))
                    return new Color(r, g, b);
                return null;
            }
            switch (s.ToLowerInvariant())
            {
                case "red": return new Color(220, 40, 40);
                case "green": return new Color(40, 170, 70);
                case "blue": return new Color(40, 90, 220);
                case "yellow": return new Color(240, 210, 40);
                case "orange": return new Color(240, 140, 30);
                case "grey": case "gray": return new Color(150, 150, 150);
                case "purple": return new Color(150, 60, 200);
                case "cyan": return new Color(40, 200, 200);
                default: return null;
            }
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            try
            {
                var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern()?.IsSolidFill == true);
                return solid?.Id;
            }
            catch { return null; }
        }

        // ─── delete_unused_views ────────────────────────────────────────
        public static Dictionary<string, object?> DeleteUnusedViews(Document doc, JsonElement args)
        {
            var active = doc.ActiveView?.Id;
            var placed = new HashSet<long>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placed.Add(vp.ViewId.Value);

            var toDelete = new List<ElementId>();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v.IsTemplate) continue;
                if (v is ViewSchedule) continue;
                if (v.ViewType == ViewType.Legend || v.ViewType == ViewType.DrawingSheet
                    || v.ViewType == ViewType.ProjectBrowser || v.ViewType == ViewType.SystemBrowser
                    || v.ViewType == ViewType.Internal) continue;
                if (active != null && v.Id == active) continue;
                if (placed.Contains(v.Id.Value)) continue;
                toDelete.Add(v.Id);
            }

            int deleted = 0;
            using var tx = new Transaction(doc, "BinaVibe: delete_unused_views");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var id in toDelete)
                {
                    try { if (doc.GetElement(id) != null) { doc.Delete(id); deleted++; } } catch { /* dependents already gone */ }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?> { ["ok"] = true, ["deleted"] = deleted };
        }

        // ─── purge_unused ────────────────────────────────────────────────
        public static Dictionary<string, object?> PurgeUnused(Document doc, JsonElement args)
        {
#if REVIT2023_24
            // Document.GetUnusedElements is Revit 2024+ API; this payload is
            // compiled against 2023 refs (serves 2023+2024) — report the
            // capability honestly instead of half-purging.
            return new Dictionary<string, object?> { ["ok"] = false,
                ["error"] = "purge_unused needs Revit 2025 or newer" };
#else
            int purged = 0;
            using var tx = new Transaction(doc, "BinaVibe: purge_unused");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Document.GetUnusedElements (Revit 2024+): empty input set = all purgeable.
                var unused = doc.GetUnusedElements(new HashSet<ElementId>());
                var ids = unused.Where(id => doc.GetElement(id) != null).ToList();
                if (ids.Count > 0)
                {
                    var del = doc.Delete(ids);
                    purged = del != null ? del.Count : ids.Count;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }
            return new Dictionary<string, object?> { ["ok"] = true, ["purged"] = purged };
#endif
        }

        // ─── crop_view_to_elements ──────────────────────────────────────
        // Turn on the active view's crop and fit it to the combined bounding
        // box of the given elements (+ margin). Handles rotated crop boxes by
        // mapping the world bbox corners into crop-local coordinates.
        public static Dictionary<string, object?> CropViewToElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no element ids given" };
            double marginFt = (ArgsHelp.GetDouble(args, "margin_mm") ?? 1000.0) / 304.8;
            var view = doc.ActiveView ?? throw new InvalidOperationException("no active view");

            // Combined world-space bounding box of the elements.
            XYZ? wMin = null, wMax = null;
            foreach (var id in ids)
            {
                var el = doc.GetElement(ElemIds.From(id));
                if (el == null) continue;
                var bb = el.get_BoundingBox(view) ?? el.get_BoundingBox(null);
                if (bb == null) continue;
                wMin = wMin == null ? bb.Min : new XYZ(Math.Min(wMin.X, bb.Min.X), Math.Min(wMin.Y, bb.Min.Y), Math.Min(wMin.Z, bb.Min.Z));
                wMax = wMax == null ? bb.Max : new XYZ(Math.Max(wMax.X, bb.Max.X), Math.Max(wMax.Y, bb.Max.Y), Math.Max(wMax.Z, bb.Max.Z));
            }
            if (wMin == null || wMax == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "elements have no geometry in this view" };

            var min = new XYZ(wMin.X - marginFt, wMin.Y - marginFt, wMin.Z);
            var max = new XYZ(wMax.X + marginFt, wMax.Y + marginFt, wMax.Z);

            using var tx = new Transaction(doc, "BinaVibe: crop_view_to_elements");
            TxGuard.StartSwallowing(tx);
            try
            {
                view.CropBoxActive = true;
                view.CropBoxVisible = true;
                var cb = view.CropBox;
                var inv = cb.Transform.Inverse;
                var corners = new[]
                {
                    new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z),
                    new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, max.Y, min.Z),
                    new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z),
                    new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, max.Y, max.Z),
                };
                double lminX = double.MaxValue, lminY = double.MaxValue, lmaxX = double.MinValue, lmaxY = double.MinValue;
                foreach (var c in corners)
                {
                    var l = inv.OfPoint(c);
                    lminX = Math.Min(lminX, l.X); lminY = Math.Min(lminY, l.Y);
                    lmaxX = Math.Max(lmaxX, l.X); lmaxY = Math.Max(lmaxY, l.Y);
                }
                cb.Min = new XYZ(lminX, lminY, cb.Min.Z);
                cb.Max = new XYZ(lmaxX, lmaxY, cb.Max.Z);
                view.CropBox = cb;
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true, ["cropped_to"] = ids.Count };
        }

        // ─── tag_all_in_view ────────────────────────────────────────────
        // Auto-tag every UNTAGGED, ungrouped element of each category in the
        // active view. Smart-annotation primitive.
        public static Dictionary<string, object?> TagAllInView(Document doc, JsonElement args)
        {
            var view = doc.ActiveView ?? throw new InvalidOperationException("no active view");
            var cats = new List<string>();
            var single = ArgsHelp.GetString(args, "category");
            if (!string.IsNullOrWhiteSpace(single)) cats.Add(single!);
            else cats.AddRange(new[] { "Doors", "Windows", "Walls", "Rooms" });

            var byCat = new List<object>();
            int totalTagged = 0, totalSkipped = 0;
            using var tx = new Transaction(doc, "BinaVibe: tag_all_in_view");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Element ids that already carry a tag in this view.
                var taggedIds = new HashSet<long>();
                foreach (var tg in new FilteredElementCollector(doc, view.Id)
                            .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                    foreach (var id in tg.GetTaggedLocalElementIds()) taggedIds.Add(id.Value);

                foreach (var catName in cats)
                {
                    if (!TryResolveCatOrLive(doc, catName, out var bic)) continue;
                    bool isRoom = bic == BuiltInCategory.OST_Rooms;
                    int tagged = 0, skipped = 0;
                    var els = new FilteredElementCollector(doc, view.Id).OfCategory(bic)
                        .WhereElementIsNotElementType().ToList();
                    foreach (var el in els)
                    {
                        if (taggedIds.Contains(el.Id.Value)) { skipped++; continue; }
                        if (el.GroupId != null && el.GroupId.Value != ElementId.InvalidElementId.Value) { skipped++; continue; }
                        try
                        {
                            if (isRoom && el is SpatialElement sp)
                            {
                                if (!(sp.Location is LocationPoint lp)) { skipped++; continue; }
                                doc.Create.NewRoomTag(new LinkElementId(el.Id), new UV(lp.Point.X, lp.Point.Y), view.Id);
                            }
                            else
                            {
                                var bb = el.get_BoundingBox(view);
                                if (bb == null) { skipped++; continue; }
                                var mid = (bb.Min + bb.Max) / 2.0;
                                IndependentTag.Create(doc, view.Id, new Reference(el), false,
                                    TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                                    new XYZ(mid.X, mid.Y, 0));
                            }
                            tagged++;
                        }
                        catch { skipped++; }
                    }
                    byCat.Add(new Dictionary<string, object?> { ["category"] = catName, ["tagged"] = tagged, ["skipped"] = skipped });
                    totalTagged += tagged; totalSkipped += skipped;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["tagged"] = totalTagged, ["skipped"] = totalSkipped, ["by_category"] = byCat,
            };
        }

        // ─── create_schedule ────────────────────────────────────────────
        public static Dictionary<string, object?> CreateSchedule(Document doc, JsonElement args)
        {
            var catName = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            if (!TryResolveCatOrLive(doc, catName, out var bic))
                throw new ArgumentException($"category '{catName}' not recognised");
            var fields = ArgsHelp.GetStringList(args, "fields");

            using var tx = new Transaction(doc, "BinaVibe: create_schedule");
            TxGuard.StartSwallowing(tx);
            try
            {
                var sched = ViewSchedule.CreateSchedule(doc, new ElementId(bic));
                var def = sched.Definition;
                var available = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);
                foreach (var sf in def.GetSchedulableFields())
                {
                    try { var n = sf.GetName(doc); if (!string.IsNullOrEmpty(n) && !available.ContainsKey(n)) available[n] = sf; }
                    catch { }
                }
                var wanted = (fields != null && fields.Count > 0) ? fields : DefaultScheduleFields(catName);
                var added = new List<string>();
                foreach (var f in wanted)
                {
                    var key = available.Keys.FirstOrDefault(k => string.Equals(k, f, StringComparison.OrdinalIgnoreCase))
                           ?? available.Keys.FirstOrDefault(k => k.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (key != null) { try { def.AddField(available[key]); added.Add(key); } catch { } }
                }
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["schedule_id"] = sched.Id.Value, ["name"] = sched.Name, ["fields"] = added,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        private static List<string> DefaultScheduleFields(string category)
        {
            switch (category.ToLowerInvariant())
            {
                case "doors":   return new List<string> { "Mark", "Family and Type", "Width", "Height", "Level" };
                case "windows": return new List<string> { "Mark", "Family and Type", "Width", "Height", "Level" };
                case "walls":   return new List<string> { "Family and Type", "Length", "Area", "Volume", "Base Constraint" };
                case "rooms":   return new List<string> { "Number", "Name", "Area", "Level", "Department" };
                default:         return new List<string> { "Family and Type", "Count", "Level" };
            }
        }

        // ─── dimension_grids ────────────────────────────────────────────
        public static Dictionary<string, object?> DimensionGrids(Document doc, JsonElement args)
        {
            var view = doc.ActiveView ?? throw new InvalidOperationException("no active view");
            if (!(view is ViewPlan))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "active view is not a plan view" };

            var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line).ToList();
            var vertical = new List<Grid>();    // run along Y → dimension across X
            var horizontal = new List<Grid>();  // run along X → dimension across Y
            foreach (var g in grids)
            {
                var d = ((Line)g.Curve).Direction;
                if (Math.Abs(d.X) > Math.Abs(d.Y)) horizontal.Add(g); else vertical.Add(g);
            }

            int created = 0;
            using var tx = new Transaction(doc, "BinaVibe: dimension_grids");
            TxGuard.StartSwallowing(tx);
            try
            {
                created += MakeGridDimension(doc, view, vertical, true);
                created += MakeGridDimension(doc, view, horizontal, false);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true, ["dimensions"] = created, ["grids"] = grids.Count };
        }

        private static int MakeGridDimension(Document doc, View view, List<Grid> grids, bool vertical)
        {
            if (grids.Count < 2) return 0;
            double Pos(Grid g) { var o = ((Line)g.Curve).Origin; return vertical ? o.X : o.Y; }
            var ordered = grids.OrderBy(Pos).ToList();
            var refs = new ReferenceArray();
            foreach (var g in ordered) refs.Append(new Reference(g));

            var firstCurve = (Line)ordered.First().Curve;
            double offset = 3.0;  // ft beyond the grid heads
            XYZ p1, p2;
            if (vertical)
            {
                double y = Math.Max(firstCurve.GetEndPoint(0).Y, firstCurve.GetEndPoint(1).Y) + offset;
                p1 = new XYZ(Pos(ordered.First()), y, 0);
                p2 = new XYZ(Pos(ordered.Last()), y, 0);
            }
            else
            {
                double x = Math.Max(firstCurve.GetEndPoint(0).X, firstCurve.GetEndPoint(1).X) + offset;
                p1 = new XYZ(x, Pos(ordered.First()), 0);
                p2 = new XYZ(x, Pos(ordered.Last()), 0);
            }
            if (p1.DistanceTo(p2) < 1e-6) return 0;
            try { doc.Create.NewDimension(view, Line.CreateBound(p1, p2), refs); return 1; }
            catch { return 0; }
        }

        // Resolve a category by friendly name / OST_ enum, falling back to a
        // live Category.Name scan (handles "Plumbing Fixtures", "Furniture"…).
        private static bool TryResolveCatOrLive(Document doc, string category, out BuiltInCategory bic)
        {
            if (TryResolveBuiltInCategory(category, out bic)) return true;
            var compact = "OST_" + category.Replace(" ", "");
            foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, c);
                    if (cat == null) continue;
                    if (string.Equals(cat.Name, category, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ToString(), compact, StringComparison.OrdinalIgnoreCase))
                    { bic = c; return true; }
                }
                catch { }
            }
            bic = BuiltInCategory.INVALID;
            return false;
        }

        // ─── apply_family_naming_fixes ──────────────────────────────────
        /// <summary>
        /// args: { items: [{element_id, new_name, params?: {name: value}}],
        ///         add_missing_params?: bool, dry_run?: bool }
        ///
        /// The mutate half of the backend's deterministic rename pipeline
        /// (suggest_name_fixes composes new_name server-side; this tool NEVER
        /// derives a name). ONE transaction for the whole batch so Ctrl+Z
        /// reverts everything. Per item: rename the element (Family for
        /// loadable rows, ElementType for system rows) and write the naming
        /// parameters back — Family rows write params to EVERY type of the
        /// family (the _jkr_st* set is family-level by convention).
        ///
        /// add_missing_params is ACCEPTED but creating a missing shared
        /// parameter is NOT implemented yet: GUID-correct binding needs the
        /// JKR shared parameter file (a same-name ad-hoc parameter breaks
        /// schedules/tags). Missing params are reported per item under
        /// params_missing so the backend can say exactly what was skipped.
        /// GRAMMAR-BLIND like get_family_naming_facts — scaling to new
        /// standards is a backend data change.
        /// </summary>
        public static Dictionary<string, object?> ApplyFamilyNamingFixes(Document doc, JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty("items", out var itemsEl)
                || itemsEl.ValueKind != JsonValueKind.Array
                || itemsEl.GetArrayLength() == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "items is required — [{element_id, new_name, params}] from suggest_name_fixes" };
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;
            var addMissing = ArgsHelp.GetBool(args, "add_missing_params") ?? true;

            // Write one string param on one type element; returns false when
            // the definition is absent or read-only.
            static bool WriteParam(Element typeEl, string pname, string value)
            {
                foreach (Parameter p in typeEl.Parameters)
                {
                    if (!string.Equals(p.Definition?.Name, pname, StringComparison.Ordinal)) continue;
                    if (p.IsReadOnly) return false;
                    return p.StorageType == StorageType.String ? p.Set(value) : p.SetValueString(value);
                }
                return false;
            }

            var results = new List<object>();
            int renamed = 0, failed = 0;
            // Params absent from their type, kept for the auto-create pass
            // after the rename loop: (row's written/missing lists stay live
            // so a successful retry moves the name between them).
            var pendingMissing = new List<(List<Element> targets, string pname, string value,
                                           List<string> written, List<string> missing)>();
            var paramsCreated = new List<string>();
            var paramsUncreatable = new List<string>();

            Transaction? tx = null;
            if (!dryRun)
            {
                tx = new Transaction(doc, "BinaVibe: apply_family_naming_fixes");
                TxGuard.StartSwallowing(tx);
            }
            try
            {
                foreach (var item in itemsEl.EnumerateArray())
                {
                    var id = ArgsHelp.GetLong(item, "element_id");
                    var newName = ArgsHelp.GetString(item, "new_name");
                    var row = new Dictionary<string, object?> { ["element_id"] = id };
                    results.Add(row);
                    if (id == null || string.IsNullOrWhiteSpace(newName))
                    { row["error"] = "element_id and new_name are required"; failed++; continue; }

                    var el = doc.GetElement(ElemIds.From(id.Value));
                    if (el == null) { row["error"] = $"element {id} not found"; failed++; continue; }

                    // Loadable rows carry the Family id; system rows a type id.
                    // A FamilySymbol id is tolerated by walking up to its
                    // Family — the NAME the convention binds is the family's.
                    Element renameTarget = el;
                    var writeTargets = new List<Element>();
                    if (el is Family fam0 || el is FamilySymbol)
                    {
                        var fam = el as Family ?? (el as FamilySymbol)!.Family;
                        if (fam == null) { row["error"] = "family not resolvable"; failed++; continue; }
                        renameTarget = fam;
                        foreach (ElementId sid in fam.GetFamilySymbolIds())
                        {
                            var sym = doc.GetElement(sid);
                            if (sym != null) writeTargets.Add(sym);
                        }
                    }
                    else if (el is ElementType) writeTargets.Add(el);
                    else { row["error"] = $"element {id} is not a family or type"; failed++; continue; }

                    row["from"] = renameTarget.Name;
                    row["to"] = newName;
                    var written = new List<string>();
                    var missing = new List<string>();

                    if (!dryRun)
                    {
                        try { renameTarget.Name = newName!; }
                        catch (Exception ex)
                        { row["error"] = $"rename failed: {ex.Message}"; failed++; continue; }
                    }
                    row["renamed"] = !dryRun;
                    renamed++;

                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("params", out var paramsEl)
                        && paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var pv in paramsEl.EnumerateObject())
                        {
                            var value = pv.Value.ValueKind == JsonValueKind.String
                                ? pv.Value.GetString() ?? ""
                                : pv.Value.ToString();
                            bool any = false;
                            foreach (var target in writeTargets)
                                if (dryRun || WriteParam(target, pv.Name, value)) any = true;
                            if (any) written.Add(pv.Name);
                            else
                            {
                                missing.Add(pv.Name);
                                pendingMissing.Add((writeTargets, pv.Name, value, written, missing));
                            }
                        }
                    }
                    row["params_written"] = written;
                    row["params_missing"] = missing;
                }

                // ── auto-create missing naming params, GUID-correct only ──
                // The definition must come from the user's CURRENT shared
                // parameter file (Revit: Manage > Shared Parameters, pointed
                // at the JKR .txt) so the GUID matches JKR schedules/tags. A
                // definition that is not there is REPORTED, never ad-hoc
                // created — a same-name ad-hoc parameter breaks schedules and
                // tags silently. Bound as a TYPE binding under Construction
                // per the JKR spec; project bindings surface the parameter on
                // every type of the bound categories, loadable and system
                // alike, so no EditFamily round-trip is needed.
                if (!dryRun && addMissing && pendingMissing.Count > 0)
                {
                    var application = doc.Application;
                    DefinitionFile? spFile = null;
                    try { spFile = application.OpenSharedParameterFile(); } catch { }
                    var defs = new Dictionary<string, ExternalDefinition>(StringComparer.Ordinal);
                    if (spFile != null)
                        foreach (DefinitionGroup g in spFile.Groups)
                            foreach (Definition d in g.Definitions)
                                if (d is ExternalDefinition ed && !defs.ContainsKey(ed.Name))
                                    defs[ed.Name] = ed;

                    foreach (var pname in pendingMissing.Select(p => p.pname).Distinct(StringComparer.Ordinal))
                    {
                        if (!defs.TryGetValue(pname, out var def))
                        { paramsUncreatable.Add(pname); continue; }
                        var catSet = application.Create.NewCategorySet();
                        foreach (var pm in pendingMissing)
                        {
                            if (!string.Equals(pm.pname, pname, StringComparison.Ordinal)) continue;
                            foreach (var t in pm.targets)
                            {
                                var c = t.Category;
                                if (c != null && c.AllowsBoundParameters) catSet.Insert(c);
                            }
                        }
                        if (catSet.IsEmpty) { paramsUncreatable.Add(pname); continue; }
                        var binding = (Binding)application.Create.NewTypeBinding(catSet);
                        if (!doc.ParameterBindings.Insert(def, binding, GroupTypeId.Construction))
                            doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Construction);
                        paramsCreated.Add(pname);
                    }

                    if (paramsCreated.Count > 0)
                    {
                        doc.Regenerate();   // bindings materialize on the types
                        foreach (var pm in pendingMissing)
                        {
                            if (!paramsCreated.Contains(pm.pname)) continue;
                            bool any = false;
                            foreach (var t in pm.targets)
                                if (WriteParam(t, pm.pname, pm.value)) any = true;
                            if (any) { pm.missing.Remove(pm.pname); pm.written.Add(pm.pname); }
                        }
                    }
                }

                if (!dryRun) TxGuard.CommitOrThrow(tx!);
            }
            finally
            {
                if (tx != null) { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); tx.Dispose(); }
            }

            var outDict = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dry_run"] = dryRun,
                ["renamed"] = renamed,
                ["failed"] = failed,
                ["results"] = results,
                ["params_created"] = paramsCreated,
                ["headline"] = dryRun
                    ? renamed + " name(s) would change (nothing renamed yet)"
                    : renamed + " renamed, " + failed + " failed",
            };
            if (paramsUncreatable.Count > 0)
            {
                outDict["params_uncreatable"] = paramsUncreatable;
                outDict["params_uncreatable_note"] =
                    "definition not found in the ACTIVE shared parameter file — point "
                    + "Revit (Manage > Shared Parameters) at the JKR shared parameter "
                    + ".txt and retry; the parameter is never ad-hoc created because a "
                    + "wrong GUID silently breaks JKR schedules and tags";
            }
            return outDict;
        }
    }

    // ─── ArgsHelp — shared JSON arg extraction ──────────────────────────

    internal static class ArgsHelp
    {
        public static string? GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        public static long? GetLong(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        public static double? GetDouble(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        public static bool? GetBool(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        public static List<string> GetStringList(JsonElement el, string name)
        {
            var items = new List<string>();
            if (el.ValueKind != JsonValueKind.Object) return items;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return items;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (s != null) items.Add(s);
                }
            }
            return items;
        }

        public static List<long> GetLongList(JsonElement el, string name)
        {
            var ids = new List<long>();
            if (el.ValueKind != JsonValueKind.Object) return ids;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return ids;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n)) ids.Add(n);
                else if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var s)) ids.Add(s);
            }
            return ids;
        }

        public static XYZ? GetXyz(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var nums = new List<double>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d)) nums.Add(d);
            }
            return nums.Count >= 3 ? new XYZ(nums[0], nums[1], nums[2]) : null;
        }

        public static object? GetValueRaw(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var n) ? (object)n : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => v.GetRawText(),
            };
        }

        private const double MmPerFoot = 304.8;

        // Reads a length arg in mm (preferred) with optional legacy-ft fallback.
        // RETURNS FEET — callers pass the result straight to the Revit API.
        public static double? GetLengthMm(JsonElement el, string mmName, string? legacyFtName = null)
        {
            var mm = GetDouble(el, mmName);
            if (mm.HasValue) return mm.Value / MmPerFoot;
            if (legacyFtName != null)
            {
                var ft = GetDouble(el, legacyFtName);
                if (ft.HasValue) return ft.Value;
            }
            return null;
        }

        // Parses one point given in mm — accepts [x,y,z] array or {x,y,z} object.
        // RETURNS an XYZ in FEET.
        public static XYZ? GetPointMm(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            double? x = null, y = null, z = 0;
            if (v.ValueKind == JsonValueKind.Array)
            {
                var items = new List<double>();
                foreach (var item in v.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d)) items.Add(d);
                if (items.Count >= 2) { x = items[0]; y = items[1]; z = items.Count > 2 ? items[2] : 0; }
            }
            else if (v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("x", out var xv) && xv.ValueKind == JsonValueKind.Number && xv.TryGetDouble(out var xd)) x = xd;
                if (v.TryGetProperty("y", out var yv) && yv.ValueKind == JsonValueKind.Number && yv.TryGetDouble(out var yd)) y = yd;
                if (v.TryGetProperty("z", out var zv) && zv.ValueKind == JsonValueKind.Number && zv.TryGetDouble(out var zd)) z = zd;
            }
            if (!x.HasValue || !y.HasValue) return null;
            return new XYZ(x.Value / MmPerFoot, y.Value / MmPerFoot, (z ?? 0) / MmPerFoot);
        }

        // Parses [[x,y,z], ...] in mm. RETURNS XYZs in FEET.
        public static List<XYZ> GetPointListMm(JsonElement el, string name)
        {
            var pts = new List<XYZ>();
            if (el.ValueKind != JsonValueKind.Object) return pts;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return pts;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var items = new List<double>();
                foreach (var n in item.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d)) items.Add(d);
                if (items.Count >= 2)
                    pts.Add(new XYZ(items[0] / MmPerFoot, items[1] / MmPerFoot,
                                    (items.Count > 2 ? items[2] : 0) / MmPerFoot));
            }
            return pts;
        }
    }
}
