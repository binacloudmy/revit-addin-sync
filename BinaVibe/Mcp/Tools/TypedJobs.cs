// TypedJobs — one-call typed tools for frequent, well-defined jobs that used
// to loop through generated C# (AI-223 / P3.4):
//   create_grid_system  — S-05 "grid 1 hingga 5 dan A hingga E, jarak 2000"
//   create_plan_sheets  — D-01 "a sheet for every floor plan, plan on its sheet"
// Each is ONE Transaction (= one Revit undo entry), honours dry_run (no
// transaction, no approval card), refuses rather than guesses, and re-reads
// what it made. The planning is Revit-free (BinaVibe.Creation.TypedJobPlans)
// and unit-tested. No doc.Regenerate() after Commit (AI-208): the only
// Regenerate here runs inside the open transaction.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Creation;

namespace BinaVibe.Mcp.Tools
{
    internal static class TypedJobs
    {
        private const double MmPerFoot = 304.8;

        // ─── create_grid_system ─────────────────────────────────────────
        public static Dictionary<string, object?> CreateGridSystem(Document doc, JsonElement args)
        {
            var rangeErrors = new List<string>();
            var vertical = GridSystemPlan.ExpandLabels(LabelsArg(args, "vertical"), rangeErrors);
            var horizontal = GridSystemPlan.ExpandLabels(LabelsArg(args, "horizontal"), rangeErrors);
            double spacing = ArgsHelp.GetDouble(args, "spacing_mm") ?? 0;
            double sx = ArgsHelp.GetDouble(args, "spacing_x_mm") ?? spacing;
            double sy = ArgsHelp.GetDouble(args, "spacing_y_mm") ?? spacing;
            double overhang = ArgsHelp.GetDouble(args, "overhang_mm") ?? 1500;
            double? ox = null, oy = null;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("origin_mm", out var o)
                && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() >= 2
                && o[0].TryGetDouble(out var oxv) && o[1].TryGetDouble(out var oyv))
            { ox = oxv; oy = oyv; }
            bool dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;

            var existing = new List<GridSystemPlan.ExistingGrid>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                try
                {
                    var c = g.Curve;
                    var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
                    existing.Add(new GridSystemPlan.ExistingGrid
                    {
                        Name = g.Name, X1 = a.X * MmPerFoot, Y1 = a.Y * MmPerFoot,
                        X2 = b.X * MmPerFoot, Y2 = b.Y * MmPerFoot,
                    });
                }
                catch { existing.Add(new GridSystemPlan.ExistingGrid { Name = g.Name }); }
            }

            var plan = GridSystemPlan.Build(vertical, horizontal, sx, sy, ox, oy, overhang, existing);
            var preview = new Dictionary<string, object?>
            {
                ["vertical"] = vertical,
                ["horizontal"] = horizontal,
                ["spacing_x_mm"] = sx,
                ["spacing_y_mm"] = sy,
                ["origin_mm"] = new[] { plan.OriginX, plan.OriginY },
                ["origin_auto"] = plan.OriginAuto,
                ["would_create"] = plan.Lines.Count,
                ["existing_grids"] = existing.Count,
            };
            preview["names"] = plan.Lines.Select(l => l.Name).ToList();
            if (rangeErrors.Count > 0)
                return Refuse(string.Join("; ", rangeErrors), preview);
            if (plan.Errors.Count > 0)
                return Refuse(string.Join("; ", plan.Errors), preview);
            if (plan.NamesTaken.Count > 0)
            {
                preview["names_taken"] = plan.NamesTaken;
                return Refuse("grid names already used in this model: " + string.Join(", ", plan.NamesTaken)
                              + ". Ask the drafter for other names (e.g. a prefix) — Revit grid names must be unique.", preview);
            }
            if (dryRun)
            {
                preview["ok"] = true;
                preview["dry_run"] = true;
                preview["lines"] = plan.Lines.Select(l => (object)new Dictionary<string, object?>
                {
                    ["name"] = l.Name, ["direction"] = l.Direction,
                    ["start_mm"] = new[] { l.X1, l.Y1 }, ["end_mm"] = new[] { l.X2, l.Y2 },
                }).ToList();
                if (plan.OriginAuto)
                    preview["note"] = "origin_mm not given: placed 10 m to the right of the existing grids";
                return preview;
            }

            const string txName = "BinaVibe: create_grid_system";
            var created = new List<object>();
            var ids = new List<ElementId>();
            using (var tx = new Transaction(doc, txName))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    foreach (var l in plan.Lines)
                    {
                        var line = Line.CreateBound(new XYZ(l.X1 / MmPerFoot, l.Y1 / MmPerFoot, 0),
                                                    new XYZ(l.X2 / MmPerFoot, l.Y2 / MmPerFoot, 0));
                        var grid = Grid.Create(doc, line);
                        grid.Name = l.Name;
                        grid.Pinned = true;   // datums pin at birth (field-guide guardrail)
                        ids.Add(grid.Id);
                        created.Add(new Dictionary<string, object?>
                        {
                            ["name"] = l.Name, ["grid_id"] = (long)grid.Id.Value, ["direction"] = l.Direction,
                        });
                    }
                    TxGuard.CommitOrThrow(tx);
                }
                catch { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }
            }

            // Read back what landed: names and the spacing actually built.
            var namesCreated = ids.Select(id => doc.GetElement(id)).OfType<Grid>().Select(g => g.Name).ToList();
            int found = namesCreated.Count;
            bool exact = found == plan.Lines.Count
                         && namesCreated.SequenceEqual(plan.Lines.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, object?>
            {
                ["ok"] = exact,
                // Report these exactly: never claim names that are not in names_created.
                ["created_count"] = found,
                ["names_created"] = namesCreated,
                ["requested_count"] = plan.Lines.Count,
                ["created"] = created,
                ["count"] = found,
                ["origin_mm"] = new[] { plan.OriginX, plan.OriginY },
                ["spacing_x_mm"] = sx,
                ["spacing_y_mm"] = sy,
                ["verified"] = new Dictionary<string, object?>
                {
                    ["grids_found"] = found,
                    ["expected"] = plan.Lines.Count,
                    ["measured_spacing_x_mm"] = MeasuredSpacing(doc, ids, plan, "vertical"),
                    ["measured_spacing_y_mm"] = MeasuredSpacing(doc, ids, plan, "horizontal"),
                },
                ["transactions"] = new List<object> { txName },
            };
            if (plan.OriginAuto) result["note"] = "origin_mm not given: placed 10 m to the right of the existing grids";
            if (!exact)
                result["warning"] = $"created {found} of {plan.Lines.Count} requested grids — report names_created "
                                    + "exactly; do not claim the others";
            return result;
        }

        // ─── create_plan_sheets ─────────────────────────────────────────
        public static Dictionary<string, object?> CreatePlanSheets(Document doc, JsonElement args)
        {
            var viewType = (ArgsHelp.GetString(args, "view_type") ?? "floor").Trim().ToLowerInvariant();
            // A single name ("Level 1") or a list; never split on commas —
            // view names may contain them.
            var wanted = ArgsHelp.GetStringList(args, "views");
            if (wanted.Count == 0 && ArgsHelp.GetString(args, "views") is string one && !string.IsNullOrWhiteSpace(one))
                wanted.Add(one);
            bool viewsGiven = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("views", out var viewsEl)
                              && viewsEl.ValueKind != JsonValueKind.Null && viewsEl.ValueKind != JsonValueKind.Undefined;
            if (viewsGiven && wanted.Count == 0)
                return Refuse("views was given but holds no readable view name — pass a plan name or a list of plan names", null);
            var prefix = ArgsHelp.GetString(args, "number_prefix") ?? "A";
            int start = (int)(ArgsHelp.GetLong(args, "start_number") ?? 101);
            int digits = (int)(ArgsHelp.GetLong(args, "digits") ?? 3);
            var pattern = ArgsHelp.GetString(args, "name_pattern") ?? "{view}";
            var tbName = ArgsHelp.GetString(args, "titleblock");
            bool dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;

            var vt = viewType switch
            {
                "floor" => ViewType.FloorPlan,
                "ceiling" => ViewType.CeilingPlan,
                "structural" => ViewType.EngineeringPlan,
                "area" => ViewType.AreaPlan,
                _ => (ViewType?)null,
            };
            if (vt == null)
                return Refuse($"view_type '{viewType}' not supported — use floor, ceiling, structural or area", null);

            // Which sheet each view already sits on (a view lives on one sheet).
            var onSheet = new Dictionary<long, string>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (doc.GetElement(vp.SheetId) is ViewSheet s && !onSheet.ContainsKey(vp.ViewId.Value))
                    onSheet[vp.ViewId.Value] = s.SheetNumber;

            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == vt.Value && v.CanBePrinted).ToList();
            var missing = new List<string>();
            if (wanted.Count > 0)
            {
                var match = SheetSetPlan.MatchViews(plans.Select(v => (v.Name, (long)v.Id.Value)), wanted);
                if (match.Ambiguous.Count > 0)
                {
                    var byId = plans.ToDictionary(v => (long)v.Id.Value);
                    return Refuse("more than one plan has this name — ask the drafter which one: "
                                  + string.Join("; ", match.Ambiguous.Keys), new Dictionary<string, object?>
                    {
                        ["ambiguous"] = match.Ambiguous.Select(kv => (object)new Dictionary<string, object?>
                        {
                            ["name"] = kv.Key,
                            ["candidates"] = kv.Value.Select(id => (object)new Dictionary<string, object?>
                            {
                                ["view_id"] = id,
                                ["level"] = byId[id].GenLevel?.Name,
                                ["primary"] = byId[id].GetPrimaryViewId() == ElementId.InvalidElementId,
                            }).ToList(),
                        }).ToList(),
                    });
                }
                missing = match.Missing;
                if (match.Ids.Count == 0)
                    return Refuse($"none of the requested {viewType} plans were found: " + string.Join(", ", missing),
                        new Dictionary<string, object?> { ["missing"] = missing });
                var keep = new HashSet<long>(match.Ids);
                plans = plans.Where(v => keep.Contains(v.Id.Value)).ToList();
            }
            else
            {
                // Default set: primary views only — dependents are crops of a
                // plan that usually sits on its own sheet already.
                plans = plans.Where(v => v.GetPrimaryViewId() == ElementId.InvalidElementId).ToList();
            }

            var viewById = new Dictionary<long, ViewPlan>();
            foreach (var v in plans) viewById[v.Id.Value] = v;
            // CanAddViewToSheet needs a sheet; any existing one answers for the
            // view-side rules without opening a transaction.
            var anySheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(sh => !sh.IsPlaceholder);
            string? Unplaceable(ViewPlan v)
            {
                if (onSheet.ContainsKey(v.Id.Value)) return null;   // reported as "already on sheet"
                try
                {
                    if (!new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().Any())
                        return "the view shows nothing — Revit will not place an empty view";
                    if (anySheet != null && !Viewport.CanAddViewToSheet(doc, anySheet.Id, v.Id))
                        return "Revit refuses to place this view on a sheet";
                }
                catch (Exception ex) { return "could not check the view: " + ex.Message; }
                return null;
            }
            var planViews = plans.Select(v => new SheetSetPlan.PlanView
            {
                Name = v.Name, Id = v.Id.Value,
                LevelElevation = v.GenLevel?.Elevation ?? 0,
                OnSheet = onSheet.TryGetValue(v.Id.Value, out var sn) ? sn : null,
                Unplaceable = Unplaceable(v),
            }).ToList();
            var existingNumbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Select(s => s.SheetNumber).ToList();
            var plan = SheetSetPlan.Build(planViews, existingNumbers, prefix, start, digits, pattern);

            var titleblocks = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>().ToList();
            FamilySymbol? tb = null;
            if (!string.IsNullOrWhiteSpace(tbName))
            {
                tb = titleblocks.FirstOrDefault(t =>
                    string.Equals(t.Name, tbName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"{t.FamilyName} : {t.Name}", tbName, StringComparison.OrdinalIgnoreCase));
                if (tb == null)
                    return Refuse($"titleblock '{tbName}' not found", new Dictionary<string, object?>
                    {
                        ["titleblocks"] = titleblocks.Select(t => $"{t.FamilyName} : {t.Name}").Take(20).ToList(),
                    });
            }
            else tb = titleblocks.FirstOrDefault();

            var preview = new Dictionary<string, object?>
            {
                ["view_type"] = viewType,
                ["sheets"] = plan.Rows.Select(r => (object)new Dictionary<string, object?>
                {
                    ["view"] = r.ViewName, ["sheet_number"] = r.SheetNumber, ["sheet_name"] = r.SheetName,
                }).ToList(),
                ["would_create"] = plan.Rows.Count,
                ["skipped"] = SkipList(plan.Skipped, missing),
                ["titleblock"] = tb == null ? null : $"{tb.FamilyName} : {tb.Name}",
                ["titleblocks_available"] = titleblocks.Select(t => $"{t.FamilyName} : {t.Name}").Take(20).ToList(),
            };
            if (string.IsNullOrWhiteSpace(tbName) && titleblocks.Count > 1)
                preview["titleblock_note"] = $"{titleblocks.Count} titleblocks loaded; using the first. "
                                             + "Ask the drafter, or pass titleblock=, if another is wanted.";
            if (plan.Errors.Count > 0) return Refuse(string.Join("; ", plan.Errors), preview);
            if (tb == null) preview["warning"] = "no titleblock loaded — sheets will have no title block";
            if (plan.Rows.Count == 0)
            {
                preview["ok"] = true;
                preview["nothing_to_do"] = true;
                preview["note"] = "every matching plan is already on a sheet or cannot be placed — see skipped";
                return preview;
            }
            if (dryRun)
            {
                preview["ok"] = true;
                preview["dry_run"] = true;
                return preview;
            }

            const string txName = "BinaVibe: create_plan_sheets";
            var made = new List<(SheetSetPlan.Row row, ViewSheet sheet)>();
            var created = new List<object>();
            var placed = new List<Viewport>();
            var failed = new List<object>();
            using (var tx = new Transaction(doc, txName))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    if (tb != null && !tb.IsActive) tb.Activate();
                    foreach (var r in plan.Rows)
                    {
                        var sheet = ViewSheet.Create(doc, tb?.Id ?? ElementId.InvalidElementId);
                        sheet.SheetNumber = r.SheetNumber;
                        sheet.Name = r.SheetName;
                        made.Add((r, sheet));
                    }
                    // Inside the open transaction: the titleblock must be
                    // regenerated before the sheet outline (its centre) is known.
                    doc.Regenerate();
                    foreach (var (r, sheet) in made)
                    {
                        var viewId = viewById[r.ViewId].Id;
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, viewId))
                        {
                            failed.Add(new { view = r.ViewName, reason = "Revit refused to place this view on a sheet" });
                            doc.Delete(sheet.Id);
                            continue;
                        }
                        var ol = sheet.Outline;
                        var centre = ol != null && ol.Max.U > ol.Min.U
                            ? new XYZ((ol.Min.U + ol.Max.U) / 2, (ol.Min.V + ol.Max.V) / 2, 0)
                            : new XYZ(1.0, 0.7, 0);
                        var vp = Viewport.Create(doc, sheet.Id, viewId, centre);
                        if (vp != null) placed.Add(vp);
                        created.Add(new Dictionary<string, object?>
                        {
                            ["sheet_number"] = r.SheetNumber, ["sheet_name"] = r.SheetName,
                            ["sheet_id"] = (long)sheet.Id.Value, ["view"] = r.ViewName,
                            ["viewport_id"] = vp == null ? (long?)null : (long)vp.Id.Value,
                        });
                    }
                    TxGuard.CommitOrThrow(tx);
                }
                catch { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }
            }

            // Read back: each new sheet still exists and holds its view.
            int verified = 0;
            foreach (var vp in placed)
                if (vp.IsValidObject && doc.GetElement(vp.SheetId) is ViewSheet) verified++;
            var skipped = SkipList(plan.Skipped, missing);
            skipped.AddRange(failed);
            return new Dictionary<string, object?>
            {
                ["ok"] = created.Count > 0 && placed.Count == created.Count && verified == placed.Count,
                ["created"] = created,
                ["sheets_created"] = created.Count,
                ["viewports_created"] = placed.Count,
                ["skipped"] = skipped,
                ["titleblock"] = tb == null ? null : $"{tb.FamilyName} : {tb.Name}",
                ["verified"] = new Dictionary<string, object?> { ["viewports_on_sheets"] = verified },
                ["transactions"] = new List<object> { txName },
            };
        }

        // ─── helpers ────────────────────────────────────────────────────
        // A list arg may come as ["1","2"], ["1-5"], "1-5" or "A,B,C".
        private static List<string> LabelsArg(JsonElement args, string name)
        {
            var list = ArgsHelp.GetStringList(args, name);
            if (list.Count > 0) return list;
            var s = ArgsHelp.GetString(args, name);
            return string.IsNullOrWhiteSpace(s) ? new List<string>() : new List<string> { s! };
        }

        private static double? MeasuredSpacing(Document doc, List<ElementId> ids, GridSystemPlan.Result plan, string dir)
        {
            var pos = new List<double>();
            for (int i = 0; i < plan.Lines.Count && i < ids.Count; i++)
            {
                if (plan.Lines[i].Direction != dir || !(doc.GetElement(ids[i]) is Grid g)) continue;
                var p = g.Curve.GetEndPoint(0);
                pos.Add((dir == "vertical" ? p.X : p.Y) * MmPerFoot);
            }
            if (pos.Count < 2) return null;
            return Math.Round(Math.Abs(pos[1] - pos[0]), 1);
        }

        private static List<object> SkipList(List<SheetSetPlan.Skip> skips, List<string> missing)
        {
            var list = skips.Select(s => (object)new { view = s.ViewName, reason = s.Reason }).ToList();
            list.AddRange(missing.Select(m => (object)new { view = m, reason = "no plan view with this name" }));
            return list;
        }

        private static Dictionary<string, object?> Refuse(string error, Dictionary<string, object?>? extra)
        {
            var d = extra ?? new Dictionary<string, object?>();
            d["ok"] = false;
            d["error"] = error;
            return d;
        }
    }
}
