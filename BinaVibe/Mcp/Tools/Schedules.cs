// Schedules — read_schedule (INSPECT) and write_schedule (MUTATE).
//
// Before these, the agent could name a schedule (list_schedules) and dump one
// to disk (export_schedule_to_excel), but never SEE its rows: the export
// returns a file card, so the cells stayed on the drafter's Desktop. Anything
// the schedule VIEW adds over the raw elements — filters, sort/group,
// calculated columns, itemize-off — was invisible.
//
// read_schedule returns both halves and keeps them honest about each other:
//   rows[]     — GetCellText, exactly what the drafter sees (formulas,
//                units, formatting, group headers and all)
//   elements[] — the elements the view collects, each with its id, so a
//                follow-up write has something unambiguous to target
//   row_element_mapping — whether those two line up 1:1 at all
//
// A schedule cell is not itself writable in the Revit API (there is no
// "set cell" for a parameter column). write_schedule therefore writes element
// PARAMETERS, validated against the schedule's own field list.
//
// Both methods MUST run on Revit's main thread. Read opens no Transaction.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class Schedules
    {
        // ─── shared helpers (also used by Inspectors.ExportScheduleToExcel) ──

        /// <summary>Exact name first, then partial/case-insensitive — so
        /// "Door Schedule" wins over "Door Schedule - Level 2" when both
        /// exist, while "door" still finds either.</summary>
        internal static ViewSchedule? Resolve(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate).ToList();
            return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => s.Name != null &&
                       s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Body cells as text. Row 0 is the header row. maxRows &lt;= 0
        /// means no cap (the Excel export path wants everything).</summary>
        internal static (List<string> Headers, List<List<string>> Rows, int TotalRows, bool Truncated)
            ReadBody(ViewSchedule sched, int maxRows)
        {
            var body = sched.GetTableData().GetSectionData(SectionType.Body);
            int nCols = body.NumberOfColumns, nRows = body.NumberOfRows;

            var headers = new List<string>();
            for (int c = 0; c < nCols; c++)
                headers.Add(sched.GetCellText(SectionType.Body, 0, c) ?? "");

            var (start, count, total, truncated) = ScheduleLogic.RowWindow(nRows, maxRows);
            var rows = new List<List<string>>();
            for (int r = start; r < start + count; r++)
            {
                var row = new List<string>();
                for (int c = 0; c < nCols; c++)
                    row.Add(sched.GetCellText(SectionType.Body, r, c) ?? "");
                rows.Add(row);
            }
            return (headers, rows, total, truncated);
        }

        // ─── read_schedule ──────────────────────────────────────────────
        public static Dictionary<string, object?> Read(Document doc, JsonElement args)
        {
            string name = ArgsHelp.GetString(args, "name") ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no schedule name given" };

            var sched = Resolve(doc, name);
            if (sched == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no schedule matching '{name}'" };

            int maxRows = (int)(ArgsHelp.GetLong(args, "max_rows") ?? ScheduleLogic.DefaultMaxRows);
            bool includeElements = ArgsHelp.GetBool(args, "include_elements") ?? true;

            var (headers, rows, totalRows, truncated) = ReadBody(sched, maxRows);

            var def = sched.Definition;
            var fields = FieldMap(def, out var unmapped);

            var elements = new List<object>();
            int elementCount = 0;
            foreach (var el in new FilteredElementCollector(doc, sched.Id).WhereElementIsNotElementType())
            {
                elementCount++;
                if (!includeElements) continue;
                if (maxRows > 0 && elements.Count >= maxRows) continue;

                var cells = new Dictionary<string, object?>();
                foreach (var f in fields)
                {
                    var p = ParamFor(doc, el, f.Name, f.ParamId);
                    cells[f.Name] = p == null ? null : SafeValue(p);
                }
                elements.Add(new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["cells"] = cells,
                });
            }

            bool grouped;
            try
            {
                grouped = def.GetSortGroupFields()
                    .Any(s => s.ShowHeader || s.ShowFooter || s.ShowBlankLine);
            }
            catch { grouped = false; }

            bool grandTotal;
            try { grandTotal = def.ShowGrandTotal; }
            catch { grandTotal = false; }

            var (verdict, note) = ScheduleLogic.RowMapping(
                def.IsItemized, grouped, grandTotal, totalRows, elementCount);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["schedule_id"] = sched.Id.Value,
                ["schedule_name"] = sched.Name,
                ["headers"] = headers,
                ["rows"] = rows,
                ["total_rows"] = totalRows,
                ["truncated"] = truncated,
                ["element_count"] = elementCount,
                ["elements"] = elements,
                ["fields"] = fields.Select(f => f.Name).ToList(),
                ["unmapped_fields"] = unmapped,
                ["row_element_mapping"] = verdict,
            };
            if (note != null) result["note"] = note;
            return result;
        }

        // ─── write_schedule ─────────────────────────────────────────────
        // Writes element parameters, not cells. One Transaction, so the whole
        // batch is a single undo for the drafter.
        public static Dictionary<string, object?> Write(Document doc, JsonElement args)
        {
            var (parsed, malformed) = ScheduleLogic.ParseUpdates(args);
            if (parsed.Count == 0 && malformed.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "no updates given — expected updates:[{element_id, field, value}, ...]",
                };

            // A schedule name is optional but strongly preferred: it is what
            // lets us reject a field the schedule does not actually have,
            // instead of writing to some same-named parameter elsewhere.
            string? name = ArgsHelp.GetString(args, "name");
            ViewSchedule? sched = null;
            List<string>? allowed = null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                sched = Resolve(doc, name!);
                if (sched == null)
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no schedule matching '{name}'" };
                allowed = FieldMap(sched.Definition, out _).Select(f => f.Name).ToList();
            }

            var (updates, rejected) = ScheduleLogic.ValidateFields(parsed, allowed);
            rejected.InsertRange(0, malformed);
            if (rejected.Count > 0 && allowed != null)
                foreach (var r in rejected) r["valid_fields"] = allowed;

            bool allowTypeParams = ArgsHelp.GetBool(args, "allow_type_params") ?? false;

            int updated = 0, skippedReadOnly = 0, skippedMissing = 0, skippedGroups = 0, typeParams = 0;
            var failures = new List<object>();
            var changed = new List<object>();

            using var tx = new Transaction(doc, "BinaVibe: write_schedule");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var u in updates)
                {
                    var el = doc.GetElement(ElemIds.From(u.ElementId));
                    if (el == null) { skippedMissing++; continue; }
                    // Group members reject parameter edits outright — same
                    // guard as set_parameter_bulk.
                    if (el.GroupId.Value != ElementId.InvalidElementId.Value) { skippedGroups++; continue; }

                    var p = el.LookupParameter(u.Field);
                    bool onType = false;
                    if (p == null || p.IsReadOnly)
                    {
                        var typeEl = doc.GetElement(el.GetTypeId());
                        var tp = typeEl?.LookupParameter(u.Field);
                        if (tp != null && !tp.IsReadOnly)
                        {
                            // A type parameter is shared by every instance of
                            // the type — never a silent side effect of "fix
                            // this row".
                            if (!allowTypeParams)
                            {
                                failures.Add(new Dictionary<string, object?>
                                {
                                    ["element_id"] = u.ElementId,
                                    ["field"] = u.Field,
                                    ["error"] = "only a TYPE parameter — writing it changes every instance " +
                                                "of this type. Re-call with allow_type_params:true if that is intended.",
                                });
                                continue;
                            }
                            p = tp; onType = true;
                        }
                    }

                    if (p == null) { skippedMissing++; continue; }
                    // Calculated/formula/count columns land here — read-only by
                    // construction, so they are refused rather than faked.
                    if (p.IsReadOnly) { skippedReadOnly++; continue; }

                    try
                    {
                        Mutators.SetParamValue(p, u.Value);
                        updated++;
                        if (onType) typeParams++;
                        changed.Add(new Dictionary<string, object?>
                        {
                            ["element_id"] = u.ElementId,
                            ["field"] = u.Field,
                            ["on_type"] = onType,
                        });
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new Dictionary<string, object?>
                        {
                            ["element_id"] = u.ElementId, ["field"] = u.Field, ["error"] = ex.Message,
                        });
                    }
                }
                tx.Commit();
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["schedule_name"] = sched?.Name,
                ["updated"] = updated,
                ["updated_type_params"] = typeParams,
                ["skipped_groups"] = skippedGroups,
                ["skipped_readonly"] = skippedReadOnly,
                ["skipped_missing"] = skippedMissing,
                ["rejected"] = rejected,
                ["failures"] = failures,
                ["changed"] = changed,
            };
        }

        // ─── field / parameter plumbing ─────────────────────────────────

        internal readonly struct SchedField
        {
            public readonly string Name;
            public readonly ElementId ParamId;
            public SchedField(string name, ElementId paramId) { Name = name; ParamId = paramId; }
        }

        /// <summary>Visible schedule columns in display order. Columns with no
        /// backing parameter (count, percentage, formula, combined) come back
        /// through <paramref name="unmapped"/> — they can be read off the cell
        /// grid but never written.</summary>
        private static List<SchedField> FieldMap(ScheduleDefinition def, out List<string> unmapped)
        {
            var mapped = new List<SchedField>();
            unmapped = new List<string>();
            IList<ScheduleFieldId> order;
            try { order = def.GetFieldOrder(); }
            catch { return mapped; }

            foreach (var fid in order)
            {
                ScheduleField f;
                try { f = def.GetField(fid); } catch { continue; }
                if (f.IsHidden) continue;

                string fname;
                try { fname = f.GetName(); } catch { continue; }
                if (string.IsNullOrWhiteSpace(fname)) continue;

                var pid = f.ParameterId;
                if (pid == null || pid.Value == ElementId.InvalidElementId.Value) unmapped.Add(fname);
                else mapped.Add(new SchedField(fname, pid));
            }
            return mapped;
        }

        private static Parameter? ParamFor(Document doc, Element el, string name, ElementId paramId)
        {
            Parameter? p = null;
            // Negative ids are BuiltInParameter — the reliable route; shared
            // and project parameters fall through to name lookup.
            if (paramId.Value < 0)
            {
                try { p = el.get_Parameter((BuiltInParameter)paramId.Value); } catch { }
            }
            p ??= el.LookupParameter(name);
            if (p != null) return p;

            var typeEl = doc.GetElement(el.GetTypeId());
            if (typeEl == null) return null;
            if (paramId.Value < 0)
            {
                try { p = typeEl.get_Parameter((BuiltInParameter)paramId.Value); } catch { }
            }
            return p ?? typeEl.LookupParameter(name);
        }

        /// <summary>Doubles come back as their DISPLAY string (mm on JKR
        /// templates, with units) so a cell value here can be compared with
        /// the same value in rows[] without a unit conversion in between.</summary>
        private static object? SafeValue(Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer: return p.AsInteger();
                    case StorageType.Double: return p.AsValueString() ?? (object)p.AsDouble();
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        if (id == null || id.Value == ElementId.InvalidElementId.Value) return null;
                        var doc = p.Element?.Document;
                        var target = doc?.GetElement(id);
                        return target?.Name ?? (object)id.Value;
                    default: return p.AsValueString();
                }
            }
            catch { return null; }
        }
    }
}
