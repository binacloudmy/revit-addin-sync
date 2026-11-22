// AuditCheckers — deterministic checker library for fill_audit.
//
// Each checker owns: an evaluator that reads the LIVE Revit document through
// the shared AuditContext, and a remark template filled ONLY from that
// evaluation's evidence. Which row maps to which checker is decided by
// AuditMatching (keyword groups, EN + BM, pure string logic kept Revit-free so
// it is unit-tested). No LLM anywhere: a row either matches a checker and gets
// an evidence-backed verdict, or it is honestly not_verifiable. Never guess.
//
// Three rules every evaluator obeys:
//   1. Zero denominator is NOT a pass. "0 of 0 broke the rule" says nothing
//      about compliance, so it returns not_verifiable carrying
//      evidence.not_verifiable_reason — except where the population itself is
//      architecturally expected, which is a finding (see CategoryMap.expected).
//   2. Every remark states the rule it applied and the value it found, and the
//      same rule text ships as RulePattern. "follows the convention" tells a drafter
//      nothing they can act on.
//   3. Shared facts come from AuditContext, so two rows about the same thing
//      cannot cite different numbers.
//
// Read-only throughout — no Transactions. Evidence name lists are capped at Cap
// with an explicit *_truncated count; ElementIds carry the FULL offender list up
// to IdCap so draft_export can emit it.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitWebAppSync.Services.JkrSpec;

namespace BinaVibe.Mcp.Tools.Audit
{
    /// <summary>How bad a `no` on this row is. Ordering/triage information for
    /// the reader — it never changes a verdict.</summary>
    public static class Severities
    {
        public const string Critical = "critical";
        public const string Major = "major";
        public const string Minor = "minor";
    }

    public sealed class CheckOutcome
    {
        public string Compliance = "not_verifiable";   // "yes" | "no" | "not_verifiable"
        /// <summary>The rule as applied, e.g. ">=3 segments separated by '-' or '_'".</summary>
        public string RulePattern = "";
        /// <summary>Set when the evaluation itself changes the severity (no grids
        /// at all is worse than a grid naming slip); otherwise the checker's
        /// declared severity is used.</summary>
        public string? SeverityOverride;
        public Dictionary<string, object?> Evidence = new();
        public List<long> ElementIds = new();
        public string Remark = "";
    }

    public sealed class AuditChecker
    {
        public string Id = "";
        public string Severity = Severities.Major;
        /// <summary>An AUTHORITATIVE guideline clause for this check (e.g.
        /// "Appendix B.1.A (a)"), used to backfill a row whose Reference cell the
        /// source form left blank. Seeded empty for every checker today — only a
        /// verified clause string belongs here; nothing is ever synthesised.</summary>
        public string GuidelineRef = "";
        public Func<AuditContext, AuditFormRow, CheckOutcome> Evaluate = (_, _) => new CheckOutcome();
    }

    public static class AuditCheckers
    {
        private const int Cap = 15;              // max names listed in evidence
        private const int IdCap = 500;           // max element ids carried per row
        private const int ElementScanCap = 3000; // material scan bound

        // ─── matching ───────────────────────────────────────────────────

        /// <summary>Checker for a row, or null (→ not_verifiable). The decision
        /// itself is AuditMatching.Match — pure keyword scoring, unit-tested
        /// without Revit; this only resolves the id to the registered checker.</summary>
        public static (AuditChecker checker, string? category)? Match(AuditFormRow row)
        {
            var (id, category) = AuditMatching.Match(row);
            if (id == null) return null;
            var checker = ById(id)
                ?? throw new InvalidOperationException(
                    $"AuditMatching names checker '{id}' but AuditCheckers.All has no such entry");
            return (checker, category);
        }

        // ─── registry ───────────────────────────────────────────────────

        public static readonly List<AuditChecker> All = new()
        {
            new AuditChecker
            {
                Id = "file_naming",
                Severity = Severities.Minor,
                Evaluate = FileNaming,
            },
            new AuditChecker
            {
                Id = "base_point",
                Severity = Severities.Critical,
                Evaluate = BasePoint,
            },
            new AuditChecker
            {
                Id = "rooms_department",
                Evaluate = RoomsDepartment,
            },
            new AuditChecker
            {
                Id = "grids_levels",
                Severity = Severities.Critical,
                Evaluate = GridsLevels,
            },
            new AuditChecker
            {
                Id = "materials_assigned",
                Evaluate = MaterialsAssigned,
            },
            new AuditChecker
            {
                Id = "project_info",
                Evaluate = ProjectInfo,
            },
            new AuditChecker
            {
                Id = "views_template",
                Evaluate = ViewsFromTemplate,
            },
            new AuditChecker
            {
                Id = "views_wip",
                Evaluate = (ctx, row) => ViewBucket(ctx, "WIP"),
            },
            new AuditChecker
            {
                Id = "views_pbt",
                Evaluate = (ctx, row) => ViewBucket(ctx, "PBT"),
            },
            new AuditChecker
            {
                Id = "views_bomba",
                Evaluate = (ctx, row) => ViewBucket(ctx, "BOMBA"),
            },
            new AuditChecker
            {
                Id = "views_dokumen",
                Evaluate = (ctx, row) => ViewBucket(ctx, "Dokumen"),
            },
            new AuditChecker
            {
                Id = "area_plans",
                Evaluate = AreaPlans,
            },
            new AuditChecker
            {
                Id = "legends",
                Evaluate = Legends,
            },
            new AuditChecker
            {
                Id = "schedules_required",
                Evaluate = SchedulesRequired,
            },
            new AuditChecker
            {
                Id = "sheets_contents",
                Evaluate = SheetsContents,
            },
            new AuditChecker
            {
                Id = "titleblock_jkr",
                Severity = Severities.Critical,
                Evaluate = TitleblockJkr,
            },
            new AuditChecker
            {
                Id = "links_current",
                Severity = Severities.Critical,
                Evaluate = LinksCurrent,
            },
            new AuditChecker
            {
                Id = "view_template_applied",
                Evaluate = ViewTemplateApplied,
            },
        };

        // Section D category map: printed row label → Revit category, plus
        // whether an architectural model is EXPECTED to contain it. Expected +
        // zero instances is a finding ("verify this is intentional"), not a
        // shrug — that distinction is the whole point of the flag. Match ORDER
        // lives in AuditMatching.CategoryLabels; every label there must be a
        // key here.
        private static readonly Dictionary<string, (BuiltInCategory bic, bool expected)> CategoryMap = new()
        {
            ["floors"] = (BuiltInCategory.OST_Floors, true),
            ["walls"] = (BuiltInCategory.OST_Walls, true),
            ["ceilings"] = (BuiltInCategory.OST_Ceilings, true),
            ["roofs"] = (BuiltInCategory.OST_Roofs, true),
            ["stairs"] = (BuiltInCategory.OST_Stairs, true),
            ["railings"] = (BuiltInCategory.OST_StairsRailing, true),
            ["ramps"] = (BuiltInCategory.OST_Ramps, false),
            ["room"] = (BuiltInCategory.OST_Rooms, true),
            ["curtain"] = (BuiltInCategory.OST_CurtainWallPanels, false),
            ["doors"] = (BuiltInCategory.OST_Doors, true),
            ["windows"] = (BuiltInCategory.OST_Windows, true),
            ["caseworks"] = (BuiltInCategory.OST_Casework, false),
            ["casework"] = (BuiltInCategory.OST_Casework, false),
            ["furniture systems"] = (BuiltInCategory.OST_FurnitureSystems, false),
            ["furniture"] = (BuiltInCategory.OST_Furniture, false),
            ["plumbing"] = (BuiltInCategory.OST_PlumbingFixtures, false),
            ["specialty"] = (BuiltInCategory.OST_SpecialityEquipment, false),
            ["generic"] = (BuiltInCategory.OST_GenericModel, false),
            ["structural column"] = (BuiltInCategory.OST_StructuralColumns, true),
            ["columns"] = (BuiltInCategory.OST_Columns, true),
            ["parking"] = (BuiltInCategory.OST_Parking, false),
            ["pipes"] = (BuiltInCategory.OST_PipeCurves, false),
            ["toposurface"] = (BuiltInCategory.OST_Topography, false),
            ["mass"] = (BuiltInCategory.OST_Mass, false),
        };

        private static readonly AuditChecker FamilyCategoryChecker = new()
        {
            Id = AuditMatching.FamilyCategoryId,
            Severity = Severities.Minor,
            Evaluate = (_, _) => new CheckOutcome(),   // real call goes through EvaluateCategory
        };

        public static string SeverityOf(AuditChecker checker, CheckOutcome outcome) =>
            outcome.SeverityOverride ?? checker.Severity;

        private static Dictionary<string, AuditChecker>? _byId;
        /// <summary>Checker by id (including the Section D family_category
        /// checker), for the Reference backfill to reach a matched checker's
        /// declared GuidelineRef.</summary>
        public static AuditChecker? ById(string id)
        {
            _byId ??= All.Concat(new[] { FamilyCategoryChecker })
                         .ToDictionary(c => c.Id, StringComparer.Ordinal);
            return _byId.TryGetValue(id, out var c) ? c : null;
        }

        // ─── evidence helpers ───────────────────────────────────────────

        /// <summary>Cap a name list into evidence and record how many were cut,
        /// so a reader can tell "3 offenders" from "3 shown of 340".</summary>
        private static void AddNames(CheckOutcome o, string key, IReadOnlyList<string> all)
        {
            o.Evidence[key] = all.Take(Cap).ToList();
            o.Evidence[key + "_truncated"] = Math.Max(0, all.Count - Cap);
        }

        /// <summary>Tail sentence for a remark whose example list was cut — the
        /// complete offender list lives in element_ids, which draft_export
        /// writes out in full.</summary>
        private static string FullListNote(int total) =>
            total > Cap ? $" The full list of {total} items is in draft_export (element_ids)." : "";

        /// <summary>The rule had nothing to run against. NOT a pass: "0 of 0
        /// failed" is not evidence of compliance.</summary>
        private static CheckOutcome NothingToCheck(string population, string rulePattern,
                                                   string reason = "no_instances_to_check")
        {
            var o = new CheckOutcome
            {
                Compliance = "not_verifiable",
                RulePattern = rulePattern,
                Remark = $"No {population} in the model, so the rule \"{rulePattern}\" has no "
                         + "data to check (0 checked) — check manually.",
            };
            o.Evidence["checked_count"] = 0;
            o.Evidence["population"] = population;
            o.Evidence["not_verifiable_reason"] = reason;
            return o;
        }

        // ─── evaluators ─────────────────────────────────────────────────

        private static CheckOutcome FileNaming(AuditContext ctx, AuditFormRow row)
        {
            const string rule = ">=3 segments separated by '-' or '_'";
            var title = ctx.Doc.Title ?? "";
            // Structured multi-segment name (PRJ-DISC-ZONE-... style). The exact
            // PWD grammar varies per project, so the check is structural; the
            // remark always shows the rule and the actual name.
            var segments = title.Split('-', '_').Where(s => s.Trim().Length > 0).Count();
            bool structured = segments >= 3;
            var o = new CheckOutcome
            {
                Compliance = structured ? "yes" : "no",
                RulePattern = rule,
                Evidence =
                {
                    ["file_title"] = title,
                    ["segments"] = segments,
                    ["rule"] = rule,
                },
            };
            if (structured)
            {
                o.Remark = $"Expected format: {rule}; found: \"{title}\" ({segments} segments) — compliant.";
                return o;
            }
            // Offer a deterministic rename where one is confident; otherwise say
            // so explicitly rather than omitting it.
            var suggested = AuditNaming.Suggest(title, 3);
            o.Evidence["suggested_value"] = suggested;
            if (suggested == null) o.Evidence["no_suggestion_reason"] = "no_confident_transform";
            o.Remark = $"Expected format: {rule}; found: \"{title}\" ({segments} segments). "
                + (suggested != null
                    ? $"Suggested name: \"{suggested}\"."
                    : "No confident automatic suggestion — rename the file manually "
                      + "following the naming guideline.");
            return o;
        }

        private static CheckOutcome BasePoint(AuditContext ctx, AuditFormRow row)
        {
            // AI-504: JKR gives no tolerance (the old 10 mm was this tool's own earlier output), so the
            // documented position is compared exactly and any other offset is for a person.
            const string rule = "Project Base Point is exactly at the crossing of grids 'A' and '1'";
            var pbp = ctx.ProjectBasePoint;
            var gridNames = ctx.Grids.Select(g => g.Name).ToList();
            // "A" and "1" through the spec grid parser: only a plain A with a plain 1 is Grid A/1
            // (AI-504, spec #12: no JKR document reads PT-A × PT1 as Grid A/1; the A4.1 spec
            // evaluator lists such grids for a person). Several pairs → the nearest crossing.
            var labels = SpecA.ParseGrids(gridNames);
            var pairs = SpecA.BaseGridPairs(labels);
            GridInfo? gridA = null, grid1 = null;
            XYZ? inter = null;
            double bestFt = double.MaxValue;
            foreach (var (ia, i1) in pairs)
            {
                var ga = ctx.Grids[ia];
                var g1 = ctx.Grids[i1];
                if (gridA == null) { gridA = ga; grid1 = g1; }
                if (pbp == null || ga.Curve is not Line pa || g1.Curve is not Line p1) continue;
                var x = IntersectXy(pa, p1);
                if (x == null) continue;
                var dFt = Math.Sqrt(Math.Pow(x.X - pbp.X, 2) + Math.Pow(x.Y - pbp.Y, 2));
                if (dFt < bestFt) { bestFt = dFt; gridA = ga; grid1 = g1; inter = x; }
            }
            var pairName = gridA != null && grid1 != null ? $"{gridA.Name.Trim()}/{grid1.Name.Trim()}" : "A/1";

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["base_point_mm"] = pbp == null ? null : MmArr(pbp);
            o.Evidence["grids_total"] = ctx.Grids.Count;
            o.Evidence["grid_a_found"] = gridA != null;
            o.Evidence["grid_1_found"] = grid1 != null;
            o.Evidence["grid_a_name"] = gridA?.Name;
            o.Evidence["grid_1_name"] = grid1?.Name;
            o.Evidence["grid_prefix"] = SpecA.ProjectGridPrefix(gridNames);
            o.Evidence["grid_a_is_line"] = gridA?.IsLine;
            o.Evidence["grid_1_is_line"] = grid1?.IsLine;
            AddNames(o, "grid_names", gridNames);

            if (pbp == null || gridA?.Curve is not Line || grid1?.Curve is not Line)
            {
                // Say WHICH precondition failed, and cite the same grid inventory
                // grids_levels reports so the two rows cannot look contradictory.
                var missingNames = new List<string>();
                if (!labels.Any(l => l.Kind == SpecGridKind.Letter && l.Main == 1)) missingNames.Add("'A'");
                if (!labels.Any(l => l.Kind == SpecGridKind.Number && l.Main == 1)) missingNames.Add("'1'");
                var curved = new List<string>();
                if (gridA != null && !gridA.IsLine) curved.Add($"'{gridA.Name.Trim()}'");
                if (grid1 != null && !grid1.IsLine) curved.Add($"'{grid1.Name.Trim()}'");

                o.Compliance = "not_verifiable";
                o.Remark = $"The check needs: {rule}. "
                    + (pbp == null ? "Project Base Point not found. " : "")
                    + (missingNames.Count > 0
                        ? $"The model has {ctx.Grids.Count} grids"
                          + (gridNames.Count > 0 ? $" ({string.Join(", ", gridNames.Take(5))}…)" : "")
                          + $" but none named {string.Join(" or ", missingNames)}"
                          + ". Project-prefixed grids (e.g. 'PT-A' / 'PT1') are not read as Grid A/1 — a person decides. "
                        : "")
                    + (curved.Count > 0
                        ? $"Grid {string.Join(" and ", curved)} is not a straight line, so the crossing "
                          + "cannot be computed. "
                        : "")
                    + "Check manually.";
                return o;
            }

            // Plan-view intersection of the two straight grids (the nearest pair above).
            if (inter == null)
            {
                o.Compliance = "not_verifiable";
                o.Remark = $"Grid '{gridA.Name.Trim()}' and grid '{grid1.Name.Trim()}' are parallel on plan, so there is no "
                           + "crossing to measure — check manually.";
                return o;
            }
            var dMm = Math.Round(bestFt * 304.8, 1);
            o.Evidence["grid_a1_intersection_mm"] = MmArr(inter);
            o.Evidence["offset_mm"] = dMm;
            // Float noise only (feet → mm), not a tolerance: 1e-6 mm.
            bool ok = Math.Round(bestFt * 304.8, 6) == 0;
            var used = pairName == "A/1" ? "grid A/1" : $"grid {pairName} (Grid A/1)";
            if (ok)
            {
                o.Compliance = "yes";
                o.Remark = $"Project Base Point is exactly at the crossing of {used} — compliant.";
                return o;
            }
            // Not exactly there: a person decides (Piawaian says "preferably"; no offset limit).
            o.Compliance = "not_verifiable";
            o.Evidence["not_verifiable_reason"] = "person_decides";
            o.Remark = $"Project Base Point is {dMm} mm from the crossing of {used}. JKR gives no offset limit — a person decides.";
            return o;
        }

        private static CheckOutcome GridsLevels(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "grids > 0 and levels > 0; every grid and level has a unique name "
                                + "(no empty or duplicate names); every floor plan is bound to one level";
            int grids = ctx.Grids.Count;
            int levels = ctx.Levels.Count;
            var plans = ctx.FloorPlans;
            var noLevel = plans.Where(v => v.GenLevel == null).ToList();

            // Naming: a grid or level the drafter cannot refer to by a unique
            // name is not "organised". Empty names and duplicates are objective;
            // the letter/number split is reported as context only (prime grids
            // like A' or A1 are legitimate), never as a verdict.
            var gridUnnamed = ctx.Grids.Where(g => string.IsNullOrWhiteSpace(g.Name)).ToList();
            var gridDupes = ctx.Grids.Where(g => !string.IsNullOrWhiteSpace(g.Name))
                .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(gr => gr.Count() > 1).ToList();
            // Letter / number / sub-grid split through the spec grid parser, so a project
            // prefix counts (PT-A is a letter grid, PT1 a number grid, PT4A a sub-grid; real
            // model, 16 Nov 2026: "1 huruf, 4 nombor" for 24 grids, 15 of them PT mains).
            var gridLabels = SpecA.ParseGrids(ctx.Grids.Select(g => g.Name));
            var gridPrefix = SpecA.ProjectGridPrefix(ctx.Grids.Select(g => g.Name));
            int gridLetters = gridLabels.Count(l => l.Kind == SpecGridKind.Letter);
            int gridNumbers = gridLabels.Count(l => l.Kind == SpecGridKind.Number);
            int gridSubs = gridLabels.Count(l => l.Kind == SpecGridKind.NumberSub || l.Kind == SpecGridKind.LetterSub);
            int gridPrefixed = gridLabels.Count(l => l.Prefix.Length > 0);

            var levelInfo = ctx.Levels.Select(l =>
            {
                string name; double elevMm;
                try { name = l.Name ?? ""; } catch { name = ""; }
                try { elevMm = Math.Round(l.Elevation * 304.8, 1); } catch { elevMm = double.NaN; }
                return (id: (long)l.Id.Value, name, elevMm);
            }).ToList();
            var levelUnnamed = levelInfo.Where(l => string.IsNullOrWhiteSpace(l.name)).ToList();
            var levelDupes = levelInfo.Where(l => !string.IsNullOrWhiteSpace(l.name))
                .GroupBy(l => l.name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(gr => gr.Count() > 1).ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["grids"] = grids;
            o.Evidence["levels"] = levels;
            o.Evidence["floor_plans"] = plans.Count;
            // Same inventory base_point cites, so rows about grids agree.
            AddNames(o, "grid_names", ctx.Grids.Select(g => g.Name).ToList());
            o.Evidence["grids_letter_named"] = gridLetters;
            o.Evidence["grids_number_named"] = gridNumbers;
            o.Evidence["grids_sub_named"] = gridSubs;
            o.Evidence["grids_other_named"] = grids - gridLetters - gridNumbers - gridSubs - gridUnnamed.Count;
            o.Evidence["grid_prefix"] = gridPrefix;
            o.Evidence["grids_prefixed"] = gridPrefixed;
            o.Evidence["grids_unnamed"] = gridUnnamed.Count;
            o.Evidence["grids_duplicate_names"] = gridDupes.Count;
            AddNames(o, "grid_duplicate_examples", gridDupes.Select(gr => $"{gr.Key} x{gr.Count()}").ToList());
            AddNames(o, "level_names", levelInfo.Select(l =>
                double.IsNaN(l.elevMm) ? l.name : $"{l.name} ({l.elevMm} mm)").ToList());
            o.Evidence["levels_unnamed"] = levelUnnamed.Count;
            o.Evidence["levels_duplicate_names"] = levelDupes.Count;
            AddNames(o, "level_duplicate_examples", levelDupes.Select(gr => $"{gr.Key} x{gr.Count()}").ToList());
            AddNames(o, "floor_plans_without_level", noLevel.Select(v => v.Name).ToList());
            o.ElementIds = gridUnnamed.Select(g => g.Id)
                .Concat(gridDupes.SelectMany(gr => gr).Select(g => g.Id))
                .Concat(levelUnnamed.Select(l => l.id))
                .Concat(levelDupes.SelectMany(gr => gr).Select(l => l.id))
                .Concat(noLevel.Select(v => (long)v.Id.Value))
                .Distinct().Take(IdCap).ToList();

            if (grids == 0 && levels == 0 && plans.Count == 0)
            {
                var empty = NothingToCheck("grids, levels or floor plans", rule);
                empty.SeverityOverride = Severities.Critical;
                return empty;
            }

            var problems = new List<string>();
            if (grids == 0) problems.Add("No grids in the model");
            if (levels == 0) problems.Add("No levels in the model");
            if (gridUnnamed.Count > 0) problems.Add($"{gridUnnamed.Count}/{grids} grids without a name");
            if (gridDupes.Count > 0)
                problems.Add($"{gridDupes.Count} duplicate grid names (e.g. "
                             + string.Join(", ", gridDupes.Take(3).Select(gr => $"{gr.Key} x{gr.Count()}")) + ")");
            if (levelUnnamed.Count > 0) problems.Add($"{levelUnnamed.Count}/{levels} levels without a name");
            if (levelDupes.Count > 0)
                problems.Add($"{levelDupes.Count} duplicate level names (e.g. "
                             + string.Join(", ", levelDupes.Take(3).Select(gr => $"{gr.Key} x{gr.Count()}")) + ")");
            if (noLevel.Count > 0)
                problems.Add($"{noLevel.Count}/{plans.Count} floor plans without a level (e.g. "
                             + string.Join(", ", noLevel.Take(3).Select(v => v.Name)) + ")");

            bool ok = problems.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            if (grids == 0 || levels == 0) o.SeverityOverride = Severities.Critical;
            var split = $"{gridLetters} letters, {gridNumbers} numbers"
                        + (gridSubs > 0 ? $", {gridSubs} sub-grids" : "")
                        + (gridPrefix.Length > 0 ? $"; project prefix '{gridPrefix}' on {gridPrefixed} grids" : "");
            o.Remark = ok
                ? $"Rule: {rule}. Found {grids} grids ({split}), "
                  + $"{levels} levels, all uniquely named; all {plans.Count} floor plans are bound to a "
                  + "level — compliant."
                : $"Rule: {rule}. Found {grids} grids, {levels} levels. "
                  + string.Join(". ", problems) + "." + FullListNote(o.ElementIds.Count);
            return o;
        }

        private static CheckOutcome RoomsDepartment(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "every room (Room) has a value in the 'Department' parameter";
            var rooms = ctx.Rooms;
            var missing = rooms.Where(r =>
                string.IsNullOrWhiteSpace(
                    r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString())).ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["rooms"] = rooms.Count;
            o.Evidence["without_department"] = missing.Count;
            AddNames(o, "examples", missing.Select(r => r.Name).ToList());
            o.ElementIds = missing.Take(IdCap).Select(r => (long)r.Id.Value).ToList();

            if (rooms.Count == 0)
            {
                // Rooms are expected in an architectural model, so zero rooms is
                // a finding — not a vacuous pass and not "not applicable".
                o.Compliance = "no";
                o.Evidence["zero_count_expected"] = true;
                o.Remark = $"Rule: {rule}. Found 0 placed rooms (placed Room) "
                           + "in the model — place a Room first before the department category can be checked.";
                return o;
            }
            bool ok = missing.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All {rooms.Count} rooms have a Department value — compliant."
                : $"Rule: {rule}. {missing.Count}/{rooms.Count} rooms have no value (e.g. "
                  + $"{string.Join(", ", missing.Take(3).Select(r => r.Name))}). Fill the Department "
                  + "parameter for those rooms." + FullListNote(missing.Count);
            return o;
        }

        private static CheckOutcome MaterialsAssigned(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "every Walls/Floors/Ceilings/Roofs element has at least one material";
            var cats = new (string label, BuiltInCategory bic)[]
            {
                ("Walls", BuiltInCategory.OST_Walls), ("Floors", BuiltInCategory.OST_Floors),
                ("Ceilings", BuiltInCategory.OST_Ceilings), ("Roofs", BuiltInCategory.OST_Roofs),
            };
            int scanned = 0;
            bool capReached = false;
            var offenders = new List<(Element e, string label)>();
            var perCategory = new List<object>();
            foreach (var (label, bic) in cats)
            {
                int catScanned = 0, catWithout = 0;
                foreach (var e in new FilteredElementCollector(ctx.Doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (scanned >= ElementScanCap) { capReached = true; break; }
                    // A curtain wall's materials sit on its panels and mullions (other
                    // categories), not on the wall: skip it rather than report "no material".
                    // (Real model, 16 Nov 2026: a glazed curtain wall was reported bare.)
                    if (e is Wall cw && cw.WallType != null && cw.WallType.Kind == WallKind.Curtain) continue;
                    scanned++; catScanned++;
                    ICollection<ElementId> mats;
                    try { mats = e.GetMaterialIds(false); } catch { continue; }
                    if (mats == null || mats.Count == 0) { offenders.Add((e, label)); catWithout++; }
                }
                perCategory.Add(new Dictionary<string, object?>
                {
                    ["category"] = label, ["scanned"] = catScanned, ["without_material"] = catWithout,
                });
                if (capReached) break;
            }

            if (scanned == 0) return NothingToCheck("elemen Walls/Floors/Ceilings/Roofs", rule);

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["elements_scanned"] = scanned;
            o.Evidence["scan_cap"] = ElementScanCap;
            o.Evidence["scan_cap_reached"] = capReached;
            o.Evidence["without_material"] = offenders.Count;
            o.Evidence["per_category"] = perCategory;
            // Type name alone ("Generic - 200mm") does not say which category it
            // is; prefix it so the drafter can find the element.
            AddNames(o, "examples", offenders.Select(x => x.label + ": " + (x.e.Name ?? "") + " [" + x.e.Id.Value + "]").ToList());
            o.ElementIds = offenders.Take(IdCap).Select(x => (long)x.e.Id.Value).ToList();

            string breakdown = string.Join(", ", perCategory.Cast<Dictionary<string, object?>>()
                .Select(d => $"{d["category"]} {d["without_material"]}/{d["scanned"]}"));
            string capNote = capReached
                ? $" The scan was limited to {ElementScanCap} elements; the remaining elements were not checked."
                : "";
            if (offenders.Count == 0 && capReached)
            {
                // A clean PARTIAL scan proves nothing about the unscanned rest.
                // Offenders found under the cap are still a real "no".
                o.Compliance = "not_verifiable";
                o.Evidence["not_verifiable_reason"] = "scan_cap_reached_without_offenders";
                o.Remark = $"Rule: {rule}. The first {scanned} elements all have a material ({breakdown}), "
                           + "but the model exceeds the scan limit so the remaining elements were not checked — "
                           + "check manually." + capNote;
                return o;
            }
            bool ok = offenders.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All {scanned} elements have a material ({breakdown}) — compliant." + capNote
                : $"Rule: {rule}. {offenders.Count}/{scanned} elements have no material "
                  + $"(without material/scanned: {breakdown}; e.g. "
                  + $"{string.Join(", ", offenders.Take(3).Select(x => x.label + " " + (x.e.Name ?? "") + " id " + x.e.Id.Value))}). "
                  + "Set a material on the element types concerned." + FullListNote(offenders.Count) + capNote;
            return o;
        }

        private static CheckOutcome ProjectInfo(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "Project Name/Number/Address/Client Name/Status/Issue Date are filled "
                                + "and not Revit default values";
            var info = ctx.Doc.ProjectInformation;
            if (info == null)
            {
                var none = NothingToCheck("Project Information element", rule, "no_project_information_element");
                none.Remark = "The document has no Project Information element to read — check manually "
                              + "in Manage > Project Information.";
                return none;
            }

            // Typed wrapper first; the BuiltInParameter is the same storage and
            // catches a wrapper that throws on an odd document.
            static string? Read(ProjectInfo pi, Func<string?> typed, BuiltInParameter bip)
            {
                try { var v = typed(); if (v != null) return v; } catch { /* fall through */ }
                try { return pi.get_Parameter(bip)?.AsString(); } catch { return null; }
            }
            var fields = new (string name, string? val)[]
            {
                ("Project Name", Read(info, () => info.Name, BuiltInParameter.PROJECT_NAME)),
                ("Project Number", Read(info, () => info.Number, BuiltInParameter.PROJECT_NUMBER)),
                ("Address", Read(info, () => info.Address, BuiltInParameter.PROJECT_ADDRESS)),
                ("Client Name", Read(info, () => info.ClientName, BuiltInParameter.CLIENT_NAME)),
                ("Status", Read(info, () => info.Status, BuiltInParameter.PROJECT_STATUS)),
                ("Issue Date", Read(info, () => info.IssueDate, BuiltInParameter.PROJECT_ISSUE_DATE)),
            };
            // What a fresh Revit template ships in each box.
            var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "project name", "project number", "enter address here", "project address",
                "owner", "project status", "issue date",
            };
            var empty = new List<string>();
            var values = new Dictionary<string, object?>();
            foreach (var (name, val) in fields)
            {
                var v = (val ?? "").Trim();
                values[name] = v;
                if (v.Length == 0) empty.Add(name + " (empty)");
                else if (defaults.Contains(v)) empty.Add(name + $" (\"{v}\" — default value)");
            }

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["fields_checked"] = fields.Select(f => f.name).ToList();
            o.Evidence["values"] = values;
            o.Evidence["filled"] = fields.Length - empty.Count;
            o.Evidence["empty_or_default"] = empty;
            bool ok = empty.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All six fields are filled (Project Name \"{values["Project Name"]}\", "
                  + $"Project Number \"{values["Project Number"]}\") — compliant."
                : $"Rule: {rule}. {empty.Count}/{fields.Length} fields not updated: "
                  + $"{string.Join(", ", empty)}. Fill them in Manage > Project Information.";
            return o;
        }

        private static CheckOutcome ViewsFromTemplate(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "every graphical view has a View Template";
            var views = ctx.GraphicalViews;
            if (views.Count == 0) return NothingToCheck("graphical views", rule);

            var without = ctx.UntemplatedOf(views);
            var split = AuditTemplateAvailability.Split(without, ctx.ViewTypesWithTemplates);
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["views"] = views.Count;
            o.Evidence["without_view_template"] = without.Count;
            AddTemplateAvailability(o, ctx, split);
            AddNames(o, "examples", split.Actionable.Select(v => v.Name).ToList());
            o.ElementIds = split.Actionable.Take(IdCap).Select(v => v.Id).ToList();

            o.Compliance = AuditTemplateAvailability.Compliance(split);
            o.Remark = o.Compliance switch
            {
                "yes" => $"Rule: {rule}. All {views.Count} views have a template — compliant.",
                "no" => $"Rule: {rule}. {without.Count}/{views.Count} views have no template (e.g. "
                        + $"{string.Join(", ", split.Actionable.Take(3).Select(v => v.Name))}). Apply the template "
                        + "from the architectural template." + AuditTemplateAvailability.ActionabilityClause(split)
                        + FullListNote(split.Actionable.Count),
                _ => $"Rule: {rule}. {views.Count - without.Count}/{views.Count} views have a template; "
                     + $"{without.Count} views have no template but no template of their view type exists in the "
                     + $"model ({split.UnactionableTypesText}; e.g. "
                     + $"{string.Join(", ", split.Unactionable.Take(3).Select(v => v.Name))}) — not actionable. "
                     + "Create a template of that type first if wanted, then re-check.",
            };
            if (o.Compliance == "not_verifiable")
                o.Evidence["not_verifiable_reason"] = "no_template_of_view_type_in_model";
            return o;
        }

        /// <summary>Template-inventory evidence shared by the view-template
        /// rows: how many templates exist per ViewType, and the split of
        /// offenders into actionable vs unactionable (with names/ids of the
        /// unactionable set kept separately so the main lists stay actionable).</summary>
        private static void AddTemplateAvailability(CheckOutcome o, AuditContext ctx, TemplateAvailabilitySplit split)
        {
            o.Evidence["templates_in_model"] = ctx.TemplateCount;
            o.Evidence["templates_by_view_type"] = ctx.TemplatesByViewType
                .ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value.Count);
            o.Evidence["actionable"] = split.Actionable.Count;
            o.Evidence["unactionable_no_template_of_type"] = split.Unactionable.Count;
            o.Evidence["unactionable_by_view_type"] = split.UnactionableByType
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            AddNames(o, "unactionable_examples", split.Unactionable.Select(v => v.Name).ToList());
            o.Evidence["unactionable_element_ids"] = split.Unactionable.Take(IdCap).Select(v => v.Id).ToList();
        }

        private static CheckOutcome ViewBucket(AuditContext ctx, string token)
        {
            string rule = $"at least one view with '{token}' in its name";
            var views = ctx.GraphicalViews;
            if (views.Count == 0) return NothingToCheck("graphical views", rule);

            var hits = views.Where(v => v.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["token"] = token;
            o.Evidence["matching_views"] = hits.Count;
            o.Evidence["total_views"] = views.Count;
            AddNames(o, "examples", hits.Select(v => v.Name).ToList());
            o.ElementIds = hits.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            bool ok = hits.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. Found {hits.Count} views (e.g. "
                  + $"{string.Join(", ", hits.Take(3).Select(v => v.Name))}) — compliant."
                : $"Rule: {rule}. No view name contains '{token}' among the {views.Count} "
                  + $"views checked. Create a {token} view for that purpose.";
            return o;
        }

        private static CheckOutcome AreaPlans(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "at least one Area Plan for space / zone analysis";
            var areaPlans = ctx.AreaPlans;
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["area_plans"] = areaPlans.Count;
            AddNames(o, "names", areaPlans.Select(v => v.Name).ToList());
            o.ElementIds = areaPlans.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            bool ok = areaPlans.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. Found {areaPlans.Count} Area Plans ("
                  + $"{string.Join(", ", areaPlans.Take(3).Select(v => v.Name))}) — compliant."
                : $"Rule: {rule}. Found 0 Area Plans in the model — generate the space / zone analysis "
                  + "from an Area Plan.";
            return o;
        }

        private static CheckOutcome Legends(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "at least one Legend view for components and general notes";
            var legends = ctx.Legends;
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["legends"] = legends.Count;
            AddNames(o, "names", legends.Select(v => v.Name).ToList());
            o.ElementIds = legends.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            bool ok = legends.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. Found {legends.Count} Legends ("
                  + $"{string.Join(", ", legends.Take(3).Select(v => v.Name))}) — compliant."
                : $"Rule: {rule}. Found 0 Legend views in the model — provide a Legend for "
                  + "components and general notes.";
            return o;
        }

        private static CheckOutcome SchedulesRequired(AuditContext ctx, AuditFormRow row)
        {
            // Token lists + JKR prefix convention (jkrAR_mto_* / jkrAR_sch_rom_* /
            // jkrAR_sch_mc_*, per-component "<door|wall|…> schedule") live in
            // AuditMatching so the decision is unit-tested without Revit.
            string rule = "a schedule name contains (including the JKR prefix): "
                + string.Join(" / ", AuditMatching.RequiredSchedules.Select(r =>
                    r.label + " (" + string.Join("|", r.tokens) + ")"))
                + " / a component schedule (door|window|wall|floor|ceiling|roof|stair|railing + schedule)";

            var names = ctx.Schedules.Select(v => v.Name).ToList();
            var (hits, missing) = AuditMatching.MatchRequiredSchedules(names);
            var found = hits
                .Select(h => (object)new Dictionary<string, object?> { ["required"] = h.required, ["schedule"] = h.schedule })
                .ToList();
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["schedules_in_model"] = names.Count;
            o.Evidence["found"] = found;
            o.Evidence["missing"] = missing;
            AddNames(o, "schedule_names", names);

            bool ok = missing.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All three required schedules were found among {names.Count} "
                  + "schedules in the model ("
                  + string.Join("; ", hits.Select(h => $"{h.required}: {h.schedule}")) + ") — compliant."
                : $"Rule: {rule}. No schedule name matches: "
                  + $"{string.Join(", ", missing)} ({names.Count} schedules in the model checked). "
                  + "Generate those schedules.";
            return o;
        }

        private static CheckOutcome SheetsContents(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "every Sheet contains at least one view";
            var sheets = ctx.Sheets;
            // A sheet holding only a schedule (a room schedule sheet) is not empty;
            // GetAllPlacedViews leaves schedules out. Revision schedules inside the
            // title block do not count. (Real model, 16 Nov 2026: 3 such sheets were
            // reported empty.)
            var withSchedule = new HashSet<long>();
            try
            {
                foreach (var si in new FilteredElementCollector(ctx.Doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                    if (!si.IsTitleblockRevisionSchedule) withSchedule.Add((long)si.OwnerViewId.Value);
            }
            catch { /* no schedules readable: judge by placed views only */ }
            var emptySheets = sheets
                .Where(s => { try { return s.GetAllPlacedViews().Count == 0 && !withSchedule.Contains((long)s.Id.Value); } catch { return false; } })
                .ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["sheets"] = sheets.Count;
            o.Evidence["empty_count"] = emptySheets.Count;
            AddNames(o, "empty_sheets", emptySheets.Select(s => s.SheetNumber + " " + s.Name).ToList());
            o.ElementIds = emptySheets.Take(IdCap).Select(s => (long)s.Id.Value).ToList();

            if (sheets.Count == 0)
            {
                // Sheets are expected deliverables — zero is a finding.
                o.Compliance = "no";
                o.Evidence["zero_count_expected"] = true;
                o.Remark = $"Rule: {rule}. Found 0 Sheets in the model — provide the "
                           + "architectural drawing sheets.";
                return o;
            }
            bool ok = emptySheets.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All {sheets.Count} sheets contain a view — compliant."
                : $"Rule: {rule}. {emptySheets.Count}/{sheets.Count} sheets are empty (e.g. "
                  + $"{string.Join(", ", emptySheets.Take(3).Select(s => s.SheetNumber + " " + s.Name))}). "
                  + "Place views on those sheets." + FullListNote(emptySheets.Count);
            return o;
        }

        private static CheckOutcome TitleblockJkr(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "the title block family/type name contains 'JKR'";
            var tbs = ctx.TitleBlocks;
            var offending = new List<FamilyInstance>();
            var offendingLabels = new List<string>();
            int jkr = 0;
            foreach (var tb in tbs)
            {
                var famName = tb.Symbol?.FamilyName ?? "";
                var typeName = tb.Symbol?.Name ?? "";
                if ((famName + " " + typeName).IndexOf("JKR", StringComparison.OrdinalIgnoreCase) >= 0) jkr++;
                else
                {
                    var sheet = ctx.Doc.GetElement(tb.OwnerViewId) as ViewSheet;
                    offending.Add(tb);
                    offendingLabels.Add((sheet?.SheetNumber ?? "?") + ": " + famName);
                }
            }
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["title_blocks"] = tbs.Count;
            o.Evidence["jkr_named"] = jkr;
            AddNames(o, "non_jkr", offendingLabels);
            o.ElementIds = offending.Take(IdCap).Select(tb => (long)tb.Id.Value).ToList();

            if (tbs.Count == 0)
            {
                o.Compliance = "no";
                o.Evidence["zero_count_expected"] = true;
                o.Remark = $"Rule: {rule}. Found 0 title blocks on any sheet — "
                           + "use the JKR Title Block.";
                return o;
            }
            bool ok = jkr == tbs.Count;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All {tbs.Count} title blocks match — compliant."
                : $"Rule: {rule}. {tbs.Count - jkr}/{tbs.Count} title blocks do not match "
                  + $"(e.g. {string.Join("; ", offendingLabels.Take(3))}). Change to the JKR Title "
                  + "Block." + FullListNote(offendingLabels.Count);
            return o;
        }

        private static CheckOutcome LinksCurrent(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "every Revit link has the status 'Loaded'";
            var links = ctx.LinkTypes;
            var rows = new List<object>();
            var notLoaded = new List<string>();
            var notLoadedIds = new List<long>();
            foreach (var lt in links)
            {
                string status;
                try { status = lt.GetLinkedFileStatus().ToString(); } catch { status = "Unknown"; }
                rows.Add(new Dictionary<string, object?> { ["name"] = lt.Name, ["status"] = status });
                if (!string.Equals(status, "Loaded", StringComparison.OrdinalIgnoreCase))
                {
                    notLoaded.Add(lt.Name + " (" + status + ")");
                    notLoadedIds.Add((long)lt.Id.Value);
                }
            }
            if (links.Count == 0)
            {
                var none = NothingToCheck("Revit links", rule, "no_links_in_model");
                none.Remark = "There are no Revit links in the model, so there is no link status to check "
                              + "— confirm manually whether links are really not needed.";
                return none;
            }

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["links"] = rows;
            o.Evidence["count"] = links.Count;
            AddNames(o, "not_loaded", notLoaded);
            o.ElementIds = notLoadedIds.Take(IdCap).ToList();

            bool ok = notLoaded.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. All {links.Count} links have the status Loaded — compliant."
                : $"Rule: {rule}. {notLoaded.Count}/{links.Count} links are not Loaded: "
                  + $"{string.Join(", ", notLoaded.Take(3))}. Reload them or fix the link path."
                  + FullListNote(notLoaded.Count);
            return o;
        }

        private static CheckOutcome ViewTemplateApplied(AuditContext ctx, AuditFormRow row)
        {
            // Row wording carries which submission set it means (BOMBA / PBT).
            var text = row.Description.ToLowerInvariant();
            string token = text.Contains("bomba") ? "BOMBA" : text.Contains("pbt") ? "PBT" : "";
            if (token.Length == 0)
            {
                return new CheckOutcome
                {
                    Compliance = "not_verifiable",
                    Remark = "The row does not say which view set (BOMBA/PBT) is meant — check manually.",
                    Evidence = { ["not_verifiable_reason"] = "row_does_not_name_a_view_set" },
                };
            }
            string rule = $"every view with '{token}' in its name has a View Template";
            var all = ctx.GraphicalViews;
            var views = all.Where(v => v.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var without = ctx.UntemplatedOf(views);
            var split = AuditTemplateAvailability.Split(without, ctx.ViewTypesWithTemplates);

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["token"] = token;
            o.Evidence["views"] = views.Count;
            o.Evidence["total_views"] = all.Count;
            o.Evidence["without_view_template"] = without.Count;
            AddTemplateAvailability(o, ctx, split);
            AddNames(o, "without_template", split.Actionable.Select(v => v.Name).ToList());
            o.ElementIds = split.Actionable.Take(IdCap).Select(v => v.Id).ToList();

            if (views.Count == 0)
            {
                o.Compliance = "no";
                o.Evidence["zero_count_expected"] = true;
                o.Remark = $"Rule: {rule}. No view name contains '{token}' among the "
                           + $"{all.Count} views checked — create a {token} view and apply the "
                           + "view template.";
                return o;
            }
            o.Compliance = AuditTemplateAvailability.Compliance(split);
            o.Remark = o.Compliance switch
            {
                "yes" => $"Rule: {rule}. All {views.Count} {token} views have a template — compliant.",
                "no" => $"Rule: {rule}. {without.Count}/{views.Count} {token} views have no template "
                        + $"(e.g. {string.Join(", ", split.Actionable.Take(3).Select(v => v.Name))}). Apply the "
                        + $"{token} template." + AuditTemplateAvailability.ActionabilityClause(split)
                        + FullListNote(split.Actionable.Count),
                _ => $"Rule: {rule}. {views.Count - without.Count}/{views.Count} {token} views have a template; "
                     + $"{without.Count} {token} views have no template but no template of their view type exists "
                     + $"in the model ({split.UnactionableTypesText}; e.g. "
                     + $"{string.Join(", ", split.Unactionable.Take(3).Select(v => v.Name))}) — not actionable. "
                     + "Create a template of that type first if wanted, then re-check.",
            };
            if (o.Compliance == "not_verifiable")
                o.Evidence["not_verifiable_reason"] = "no_template_of_view_type_in_model";
            return o;
        }

        /// <summary>Section D: per-category presence + type-naming structure.
        /// Only the "Standard component file naming" column is automatable;
        /// Quality/Information/Geometry stay manual and the remark says so.
        ///
        /// The rule is JKR-naming-aware (AuditNaming.IsSectionDCompliant):
        /// "(TKh281a) 600 x 1800 s900 @T3", "jkrAR_…" or a bare material
        /// ("UPVC") pass; Revit defaults ("Curtain Wall", "Generic") fail.
        /// Judged per INSTANCE: only types with ≥1 placed instance count
        /// toward the verdict, and ElementIds are the offending instances.
        /// Types nobody placed are listed as <c>unused_types</c> context —
        /// unused library junk is not a model defect.</summary>
        public static CheckOutcome EvaluateCategory(AuditContext ctx, string label)
        {
            const string rule = "the names of the types in use (ElementType) follow the JKR format — "
                                + "JKR code '(XXnnn)' / prefix 'jkrAR' / material name";
            if (!CategoryMap.TryGetValue(label, out var entry))
                throw new InvalidOperationException(
                    $"AuditMatching.CategoryLabels has '{label}' but AuditCheckers.CategoryMap does not");
            // Rooms have no ElementType, so the type-naming rule below can never
            // apply; they get an instance-level check instead. Every other
            // category keeps the naming path unchanged.
            if (label == "room") return EvaluateRoomInstances(ctx, entry.expected);
            var instances = new FilteredElementCollector(ctx.Doc).OfCategory(entry.bic)
                .WhereElementIsNotElementType().ToList();
            var types = new FilteredElementCollector(ctx.Doc).OfCategory(entry.bic)
                .WhereElementIsElementType().Cast<ElementType>().ToList();
            var typeById = types.ToDictionary(t => t.Id, t => t);

            // Usage per type: which instances sit on which type. An instance whose
            // GetTypeId() does not resolve to one of this category's types (or
            // throws) is counted as untyped and cannot be judged.
            var usedTypes = new Dictionary<ElementId, List<Element>>();
            int untypedInstances = 0;
            foreach (var inst in instances)
            {
                ElementId tid;
                try { tid = inst.GetTypeId(); }
                catch { untypedInstances++; continue; }
                if (tid == null || tid == ElementId.InvalidElementId || !typeById.ContainsKey(tid))
                {
                    untypedInstances++;
                    continue;
                }
                if (!usedTypes.TryGetValue(tid, out var list)) usedTypes[tid] = list = new List<Element>();
                list.Add(inst);
            }
            var unusedTypes = types.Where(t => !usedTypes.ContainsKey(t.Id)).ToList();
            var nonConforming = usedTypes.Keys
                .Select(id => typeById[id])
                .Where(t => !AuditNaming.IsSectionDCompliant(t.Name))
                .OrderByDescending(t => usedTypes[t.Id].Count)
                .ToList();
            var offendingInstances = nonConforming.SelectMany(t => usedTypes[t.Id]).ToList();

            // Best-effort rename per offending USED type name; null where no
            // confident transform exists (a single-word name has nothing to split).
            var suggestions = new List<object>();
            int noSuggestion = 0;
            var suggestedPairs = new List<(string current, string suggested)>();
            foreach (var t in nonConforming)
            {
                var name = t.Name ?? "";
                var suggested = AuditNaming.Suggest(name, 2);
                if (suggested != null) suggestedPairs.Add((name, suggested));
                else noSuggestion++;
                if (suggestions.Count < Cap)
                    suggestions.Add(new Dictionary<string, object?>
                    {
                        ["current"] = name,
                        ["suggested"] = suggested,
                        ["instances"] = usedTypes[t.Id].Count,
                    });
            }

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["category"] = label;
            o.Evidence["category_expected"] = entry.expected;
            o.Evidence["instances"] = instances.Count;
            o.Evidence["types"] = types.Count;
            o.Evidence["used_types"] = usedTypes.Count;
            o.Evidence["untyped_instances"] = untypedInstances;
            o.Evidence["nonconforming_used_types"] = nonConforming.Count;
            o.Evidence["nonconforming_instances"] = offendingInstances.Count;
            AddNames(o, "types_nonconforming_naming", nonConforming.Select(t => t.Name ?? "").ToList());
            AddNames(o, "examples", offendingInstances.Select(i =>
                $"[{i.Id.Value}] {typeById[i.GetTypeId()].Name}").ToList());
            // Context only — never part of the verdict.
            AddNames(o, "unused_types", unusedTypes.Select(t => t.Name ?? "").ToList());
            o.Evidence["naming_suggestions"] = suggestions;
            o.Evidence["naming_suggestions_truncated"] = Math.Max(0, nonConforming.Count - Cap);
            o.Evidence["suggested_count"] = suggestedPairs.Count;
            o.Evidence["no_suggestion_count"] = noSuggestion;
            o.Evidence["automated_scope"] = "standard naming only (used types); quality/information/geometry manual";
            o.ElementIds = offendingInstances.Take(IdCap).Select(i => (long)i.Id.Value).ToList();

            if (instances.Count == 0)
            {
                if (entry.expected)
                {
                    // An architecturally expected category with nothing in it is
                    // a real finding, not "not applicable" — but it is a person's
                    // call, not a fail: the JKR spec (D-e, revision 2) says
                    // "0 elemen: sahkan skop". Marking it "no" put a ✗ on Borang
                    // row 18 "Columns" for a model with no columns (16 Nov).
                    o.Compliance = "not_verifiable";
                    o.Evidence["not_verifiable_reason"] = "zero_count_expected";
                    o.Evidence["zero_count_expected"] = true;
                    o.SeverityOverride = Severities.Major;
                    o.Remark = $"0 {label} elements found in the model although this category "
                               + "is normally required in an architectural model — confirm whether it is really "
                               + "outside the project scope. (Quality / information / geometry: check manually.)";
                    return o;
                }
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "category_not_present";
                o.Remark = $"0 {label} elements found in the model and this category is not required — "
                           + "this row does not apply, or check manually if there should be some.";
                return o;
            }

            if (types.Count == 0)
            {
                // Instances but no ElementType (Rooms, for example): the naming
                // rule has nothing to test, so it cannot be a pass.
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "no_types_to_check";
                o.Remark = $"{instances.Count} {label} elements but 0 types (ElementType) to "
                           + $"check, so the rule \"{rule}\" does not apply here — check "
                           + "manually. (Quality / information / geometry: check manually.)";
                return o;
            }

            if (usedTypes.Count == 0)
            {
                // Instances and types both exist but no instance resolves to one
                // of this category's types: every type has zero placed
                // instances, so there is no USED name to judge. Not a pass, not
                // a fail — nothing placed can be audited.
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "zero_placed_instances";
                o.Remark = $"Rule: {rule}. {instances.Count} {label} elements but none of them uses "
                           + $"any of the {types.Count} types of this category (0 instances placed for "
                           + "each type) — no type name in use to judge; check "
                           + "manually. (Quality / information / geometry: check manually.)";
                return o;
            }

            o.Evidence["checked_count"] = usedTypes.Count;
            string unusedNote = unusedTypes.Count > 0
                ? $" {unusedTypes.Count} types with no placed instance are not counted (see unused_types)."
                : "";
            string untypedNote = untypedInstances > 0
                ? $" {untypedInstances} elements without a type cannot be judged."
                : "";
            bool ok = nonConforming.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Rule: {rule}. {instances.Count} elements, {usedTypes.Count} types in use — "
                  + "all type names in use are compliant." + unusedNote + untypedNote
                  + " (Quality / information / geometry: check manually.)"
                : $"Rule: {rule}. {nonConforming.Count}/{usedTypes.Count} types in use "
                  + $"are not compliant, covering {offendingInstances.Count}/{instances.Count} elements (e.g. "
                  + string.Join(", ", nonConforming.Take(3).Select(t =>
                        $"\"{t.Name}\" ×{usedTypes[t.Id].Count}")) + "). "
                  + (suggestedPairs.Count > 0
                      ? "Suggestion: " + string.Join(", ",
                          suggestedPairs.Take(3).Select(p => $"\"{p.current}\" → \"{p.suggested}\"")) + ". "
                      : "")
                  + (noSuggestion > 0
                      ? $"{noSuggestion} names have no confident automatic suggestion — rename them "
                        + "manually following the JKR format. "
                      : "")
                  + unusedNote.TrimStart() + untypedNote
                  + " (Quality / information / geometry: check manually.)" + FullListNote(offendingInstances.Count);
            return o;
        }

        /// <summary>Section D "Room": placed rooms must each carry a Number AND a
        /// Name (ROOM_NUMBER / ROOM_NAME). Department is NOT checked here — A6
        /// (RoomsDepartment) owns it, and two rows must not cite the same fact
        /// under different rules. Unplaced rooms (Area 0) are counted for
        /// context only; they have no geometry to audit.</summary>
        private static CheckOutcome EvaluateRoomInstances(AuditContext ctx, bool expected)
        {
            const string rule = "every placed room (Room) has a Number and a Name";
            var rooms = ctx.Rooms;
            int allRooms = new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().GetElementCount();

            var offenders = new List<(Room r, bool noNumber, bool noName)>();
            int unreadable = 0;
            foreach (var r in rooms)
            {
                string? number, name;
                try
                {
                    number = r.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                    name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                }
                catch { unreadable++; continue; }
                bool noNumber = string.IsNullOrWhiteSpace(number);
                bool noName = string.IsNullOrWhiteSpace(name);
                if (noNumber || noName) offenders.Add((r, noNumber, noName));
            }

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["category"] = "room";
            o.Evidence["category_expected"] = expected;
            o.Evidence["rooms_placed"] = rooms.Count;
            o.Evidence["rooms_unplaced"] = Math.Max(0, allRooms - rooms.Count);
            o.Evidence["without_number"] = offenders.Count(x => x.noNumber);
            o.Evidence["without_name"] = offenders.Count(x => x.noName);
            o.Evidence["unreadable"] = unreadable;
            o.Evidence["automated_scope"] = "number + name presence only; department in A6; quality/information/geometry manual";
            AddNames(o, "examples", offenders.Select(x =>
                $"[{x.r.Id.Value}] {(x.noNumber ? "(no Number)" : x.r.Number)} {(x.noName ? "(no Name)" : x.r.Name)}".Trim())
                .ToList());
            o.ElementIds = offenders.Take(IdCap).Select(x => (long)x.r.Id.Value).ToList();

            if (rooms.Count == 0)
            {
                if (expected)
                {
                    // Same stance as A6: rooms are expected in an architectural
                    // model, so zero placed rooms is a finding, not a vacuous pass.
                    o.Compliance = "no";
                    o.Evidence["zero_count_expected"] = true;
                    o.SeverityOverride = Severities.Major;
                    o.Remark = $"Rule: {rule}. Found 0 placed rooms (placed Room) "
                               + (allRooms > 0 ? $"— {allRooms} Rooms exist but are not placed (Area 0). " : "in the model. ")
                               + "Place a Room first before the Number/Name can be checked. "
                               + "(Quality / information / geometry: check manually.)";
                    return o;
                }
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "category_not_present";
                o.Remark = $"0 rooms placed in the model and this category is not required — this row "
                           + "does not apply, or check manually if there should be some.";
                return o;
            }

            if (unreadable == rooms.Count)
            {
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "parameters_unreadable";
                o.Remark = $"Rule: {rule}. {rooms.Count} rooms are placed but the Number/Name "
                           + "parameters cannot be read — check manually.";
                return o;
            }

            int checkedCount = rooms.Count - unreadable;
            o.Evidence["checked_count"] = checkedCount;
            bool ok = offenders.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            string unreadNote = unreadable > 0 ? $" ({unreadable} rooms could not be read and are not counted.)" : "";
            o.Remark = ok
                ? $"Rule: {rule}. All {checkedCount} placed rooms have a Number and a Name — compliant."
                  + unreadNote + " (Quality / information / geometry: check manually.)"
                : $"Rule: {rule}. {offenders.Count}/{checkedCount} rooms are incomplete "
                  + $"({o.Evidence["without_number"]} without Number, {o.Evidence["without_name"]} without Name; e.g. "
                  + string.Join(", ", offenders.Take(3).Select(x => $"[{x.r.Id.Value}]")) + "). "
                  + "Fill the Number and Name for those rooms." + unreadNote
                  + " (Quality / information / geometry: check manually.)" + FullListNote(offenders.Count);
            return o;
        }

        // ─── context for rows with no checker (item 9) ───────────────────

        // A row nobody can evaluate still deserves the facts a human would look
        // up first. These providers are keyword-triggered inventories — counts
        // and names, never a verdict.
        private static readonly (string key, string[] keywords, string label,
                                 Func<AuditContext, (int count, List<string> names)> read)[] ContextProviders =
        {
            // "block/tower/podium/structured/managed according" covers the A.3
            // row ("structured according to rules … managed according to block,
            // tower and podium") — worksets are the model fact a human checks it
            // against, and none of A.3's words hit any other provider.
            ("worksets", new[]
                {
                    "workset", "worksharing", "work sharing", "kerja bersama",
                    "block", "tower", "podium", "structured", "managed according",
                },
                "worksets", c => (c.Worksets.Count, c.Worksets)),
            ("phases", new[] { "phase", "phasing", "fasa" },
                "phases", c => (c.Phases.Count, c.Phases)),
            ("design_options", new[] { "design option", "pilihan reka" },
                "design options", c => (c.DesignOptions.Count, c.DesignOptions)),
            ("scope_boxes", new[] { "scope box", "kotak skop" },
                "scope boxes", c => (c.ScopeBoxes.Count, c.ScopeBoxes)),
            ("shared_parameters", new[] { "shared parameter", "parameter", "parameter kongsi" },
                "parameters bound to the project", c => (c.SharedParameters.Count, c.SharedParameters)),
            ("families", new[] { "family", "families", "keluarga" },
                "loaded families", c => (c.Families.Count, c.Families)),
            ("levels", new[] { "level", "aras" },
                "levels", c => (c.Levels.Count, c.Levels.Select(l => l.Name ?? "").ToList())),
            ("grids", new[] { "grid", "gridline" },
                "grids", c => (c.Grids.Count, c.Grids.Select(g => g.Name).ToList())),
            ("views", new[] { "view", "views" },
                "graphical views", c => (c.GraphicalViews.Count, c.GraphicalViews.Select(v => v.Name).ToList())),
            ("sheets", new[] { "sheet", "helaian" },
                "sheets", c => (c.Sheets.Count, c.Sheets.Select(s => s.SheetNumber ?? "").ToList())),
            ("schedules", new[] { "schedule", "jadual" },
                "schedules", c => (c.Schedules.Count, c.Schedules.Select(s => s.Name).ToList())),
            // Header/uncheckable rows that name a deliverable but fail a checker's
            // 2-group match (C.2.0 "Legends", C.5.0 "Link Files", area-plan rows):
            // still show the inventory a human would look up first.
            ("legends", new[] { "legend", "legenda" },
                "Legend views", c => (c.Legends.Count, c.Legends.Select(v => v.Name).ToList())),
            ("links", new[] { "link files", "link file", "linked", "link", "pautan" },
                "Revit links", c => (c.LinkTypes.Count, c.LinkTypes.Select(l => l.Name).ToList())),
            ("area_plans", new[] { "area plan", "pelan keluasan", "spatial", "zoning", "zon" },
                "Area Plans", c => (c.AreaPlans.Count, c.AreaPlans.Select(v => v.Name).ToList())),
            ("warnings", new[] { "warning", "error", "amaran", "ralat" },
                "warnings in the model", c => (c.Warnings, new List<string>())),
        };

        /// <summary>Inventories relevant to a row NO checker matched. Returns
        /// (evidence, sentence) — both empty when no keyword hits. This is
        /// context, never a verdict: the row stays not_verifiable.</summary>
        public static (Dictionary<string, object?>? evidence, string note) UnmatchedContext(
            AuditContext ctx, AuditFormRow row)
        {
            var text = (row.Description + " " + row.GuidelineRef).ToLowerInvariant();
            var evidence = new Dictionary<string, object?>();
            var parts = new List<string>();
            foreach (var (key, keywords, label, read) in ContextProviders)
            {
                if (!keywords.Any(k => text.Contains(k))) continue;
                int count;
                List<string> names;
                try { (count, names) = read(ctx); } catch { continue; }
                var entry = new Dictionary<string, object?> { ["count"] = count };
                if (names.Count > 0)
                {
                    entry["names"] = names.Take(Cap).ToList();
                    entry["names_truncated"] = Math.Max(0, names.Count - Cap);
                }
                evidence[key] = entry;
                parts.Add(names.Count > 0
                    ? $"{count} {label} ({string.Join(", ", names.Take(3))})"
                    : $"{count} {label}");
            }
            if (parts.Count == 0) return (null, "");
            return (evidence, "Model context: " + string.Join("; ", parts) + ".");
        }

        // ─── shared ─────────────────────────────────────────────────────

        private static double[] MmArr(XYZ p) => new[]
        {
            Math.Round(p.X * 304.8, 1), Math.Round(p.Y * 304.8, 1), Math.Round(p.Z * 304.8, 1),
        };

        /// <summary>XY-plane intersection of two lines (infinite extension).</summary>
        private static XYZ? IntersectXy(Line a, Line b)
        {
            var p = a.GetEndPoint(0); var r = a.Direction;
            var q = b.GetEndPoint(0); var s = b.Direction;
            double denom = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denom) < 1e-9) return null;   // parallel in plan
            double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / denom;
            return new XYZ(p.X + t * r.X, p.Y + t * r.Y, 0);
        }
    }
}
