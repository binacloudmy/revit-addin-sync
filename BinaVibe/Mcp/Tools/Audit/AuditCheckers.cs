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
//      same rule text ships as RulePattern. "ikut konvensyen" tells a drafter
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
        /// <summary>The rule as applied, e.g. ">=3 segmen dipisah '-' atau '_'".</summary>
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
            total > Cap ? $" Senarai penuh {total} item ada dalam draft_export (element_ids)." : "";

        /// <summary>The rule had nothing to run against. NOT a pass: "0 of 0
        /// failed" is not evidence of compliance.</summary>
        private static CheckOutcome NothingToCheck(string population, string rulePattern,
                                                   string reason = "no_instances_to_check")
        {
            var o = new CheckOutcome
            {
                Compliance = "not_verifiable",
                RulePattern = rulePattern,
                Remark = $"Tiada {population} dalam model, jadi peraturan \"{rulePattern}\" tiada "
                         + "data untuk disemak (0 diperiksa) — semak manual.",
            };
            o.Evidence["checked_count"] = 0;
            o.Evidence["population"] = population;
            o.Evidence["not_verifiable_reason"] = reason;
            return o;
        }

        // ─── evaluators ─────────────────────────────────────────────────

        private static CheckOutcome FileNaming(AuditContext ctx, AuditFormRow row)
        {
            const string rule = ">=3 segmen dipisah '-' atau '_'";
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
                o.Remark = $"Format dijangka: {rule}; dijumpai: \"{title}\" ({segments} segmen) — patuh.";
                return o;
            }
            // Offer a deterministic rename where one is confident; otherwise say
            // so explicitly rather than omitting it.
            var suggested = AuditNaming.Suggest(title, 3);
            o.Evidence["suggested_value"] = suggested;
            if (suggested == null) o.Evidence["no_suggestion_reason"] = "no_confident_transform";
            o.Remark = $"Format dijangka: {rule}; dijumpai: \"{title}\" ({segments} segmen). "
                + (suggested != null
                    ? $"Cadangan nama: \"{suggested}\"."
                    : "Tiada cadangan automatik yang yakin — namakan semula fail secara manual "
                      + "mengikut garis panduan penamaan.");
            return o;
        }

        private static CheckOutcome BasePoint(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'";
            var pbp = ctx.ProjectBasePoint;
            var gridA = ctx.GridNamed("A");
            var grid1 = ctx.GridNamed("1");
            var gridNames = ctx.Grids.Select(g => g.Name).ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["base_point_mm"] = pbp == null ? null : MmArr(pbp);
            o.Evidence["grids_total"] = ctx.Grids.Count;
            o.Evidence["grid_a_found"] = gridA != null;
            o.Evidence["grid_1_found"] = grid1 != null;
            o.Evidence["grid_a_is_line"] = gridA?.IsLine;
            o.Evidence["grid_1_is_line"] = grid1?.IsLine;
            AddNames(o, "grid_names", gridNames);

            if (pbp == null || gridA?.Curve is not Line la || grid1?.Curve is not Line l1)
            {
                // Say WHICH precondition failed, and cite the same grid inventory
                // grids_levels reports so the two rows cannot look contradictory.
                var missingNames = new List<string>();
                if (gridA == null) missingNames.Add("'A'");
                if (grid1 == null) missingNames.Add("'1'");
                var curved = new List<string>();
                if (gridA != null && !gridA.IsLine) curved.Add("'A'");
                if (grid1 != null && !grid1.IsLine) curved.Add("'1'");

                o.Compliance = "not_verifiable";
                o.Remark = $"Semakan perlukan {rule}. "
                    + (pbp == null ? "Project Base Point tidak dijumpai. " : "")
                    + (missingNames.Count > 0
                        ? $"Model ada {ctx.Grids.Count} grid"
                          + (gridNames.Count > 0 ? $" ({string.Join(", ", gridNames.Take(5))}…)" : "")
                          + $" tetapi tiada yang bernama {string.Join(" atau ", missingNames)}. "
                        : "")
                    + (curved.Count > 0
                        ? $"Grid {string.Join(" dan ", curved)} bukan garisan lurus, jadi persilangan "
                          + "tidak boleh dihitung. "
                        : "")
                    + "Semak manual.";
                return o;
            }

            // Plan-view intersection of the two straight grids.
            var inter = IntersectXy(la, l1);
            if (inter == null)
            {
                o.Compliance = "not_verifiable";
                o.Remark = "Grid 'A' dan grid '1' selari pada pelan, jadi tiada persilangan untuk "
                           + "diukur — semak manual.";
                return o;
            }
            var dMm = Math.Round(Math.Sqrt(Math.Pow(inter.X - pbp.X, 2) + Math.Pow(inter.Y - pbp.Y, 2)) * 304.8, 1);
            o.Evidence["grid_a1_intersection_mm"] = MmArr(inter);
            o.Evidence["offset_mm"] = dMm;
            o.Evidence["tolerance_mm"] = 10.0;
            bool ok = dMm <= 10.0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Had dijangka: <=10 mm dari persilangan grid A/1; dijumpai sisihan {dMm} mm — patuh."
                : $"Had dijangka: <=10 mm dari persilangan grid A/1; dijumpai sisihan {dMm} mm. "
                  + "Selaraskan kedudukan model ke grid A dan 1.";
            return o;
        }

        private static CheckOutcome GridsLevels(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "grid > 0 dan aras > 0; setiap grid dan aras ada nama yang unik "
                                + "(tiada nama kosong/berganda); setiap pelan lantai terikat pada satu aras";
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
            int gridLetters = ctx.Grids.Count(g => g.Name.Trim().Length > 0 && g.Name.Trim().All(char.IsLetter));
            int gridNumbers = ctx.Grids.Count(g => g.Name.Trim().Length > 0 && g.Name.Trim().All(char.IsDigit));

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
            o.Evidence["grids_other_named"] = grids - gridLetters - gridNumbers - gridUnnamed.Count;
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
                var empty = NothingToCheck("grid, aras atau pelan lantai", rule);
                empty.SeverityOverride = Severities.Critical;
                return empty;
            }

            var problems = new List<string>();
            if (grids == 0) problems.Add("Tiada grid dalam model");
            if (levels == 0) problems.Add("Tiada aras dalam model");
            if (gridUnnamed.Count > 0) problems.Add($"{gridUnnamed.Count}/{grids} grid tanpa nama");
            if (gridDupes.Count > 0)
                problems.Add($"{gridDupes.Count} nama grid berganda (cth: "
                             + string.Join(", ", gridDupes.Take(3).Select(gr => $"{gr.Key} x{gr.Count()}")) + ")");
            if (levelUnnamed.Count > 0) problems.Add($"{levelUnnamed.Count}/{levels} aras tanpa nama");
            if (levelDupes.Count > 0)
                problems.Add($"{levelDupes.Count} nama aras berganda (cth: "
                             + string.Join(", ", levelDupes.Take(3).Select(gr => $"{gr.Key} x{gr.Count()}")) + ")");
            if (noLevel.Count > 0)
                problems.Add($"{noLevel.Count}/{plans.Count} pelan lantai tanpa aras (cth: "
                             + string.Join(", ", noLevel.Take(3).Select(v => v.Name)) + ")");

            bool ok = problems.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            if (grids == 0 || levels == 0) o.SeverityOverride = Severities.Critical;
            o.Remark = ok
                ? $"Peraturan: {rule}. Dijumpai {grids} grid ({gridLetters} huruf, {gridNumbers} nombor), "
                  + $"{levels} aras, semua bernama unik; semua {plans.Count} pelan lantai terikat pada "
                  + "aras — patuh."
                : $"Peraturan: {rule}. Dijumpai {grids} grid, {levels} aras. "
                  + string.Join(". ", problems) + "." + FullListNote(o.ElementIds.Count);
            return o;
        }

        private static CheckOutcome RoomsDepartment(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "setiap bilik (Room) ada nilai parameter 'Department'";
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
                o.Remark = $"Peraturan: {rule}. Dijumpai 0 bilik yang diletakkan (placed Room) "
                           + "dalam model — letak Room dahulu sebelum kategori jabatan boleh disemak.";
                return o;
            }
            bool ok = missing.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {rooms.Count} bilik ada nilai Department — patuh."
                : $"Peraturan: {rule}. {missing.Count}/{rooms.Count} bilik tiada nilai (cth: "
                  + $"{string.Join(", ", missing.Take(3).Select(r => r.Name))}). Isi parameter "
                  + "Department untuk bilik tersebut." + FullListNote(missing.Count);
            return o;
        }

        private static CheckOutcome MaterialsAssigned(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "setiap elemen Walls/Floors/Ceilings/Roofs ada sekurang-kurangnya satu material";
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
                ? $" Imbasan dihadkan kepada {ElementScanCap} elemen; elemen selebihnya tidak disemak."
                : "";
            if (offenders.Count == 0 && capReached)
            {
                // A clean PARTIAL scan proves nothing about the unscanned rest.
                // Offenders found under the cap are still a real "no".
                o.Compliance = "not_verifiable";
                o.Evidence["not_verifiable_reason"] = "scan_cap_reached_without_offenders";
                o.Remark = $"Peraturan: {rule}. {scanned} elemen pertama semuanya ada material ({breakdown}), "
                           + "tetapi model melebihi had imbasan jadi elemen selebihnya tidak disemak — "
                           + "semak manual." + capNote;
                return o;
            }
            bool ok = offenders.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {scanned} elemen ada material ({breakdown}) — patuh." + capNote
                : $"Peraturan: {rule}. {offenders.Count}/{scanned} elemen tiada material "
                  + $"(tanpa material/diimbas: {breakdown}; cth: "
                  + $"{string.Join(", ", offenders.Take(3).Select(x => x.label + " " + (x.e.Name ?? "") + " id " + x.e.Id.Value))}). "
                  + "Tetapkan material pada jenis elemen berkenaan." + FullListNote(offenders.Count) + capNote;
            return o;
        }

        private static CheckOutcome ProjectInfo(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "Project Name/Number/Address/Client Name/Status/Issue Date terisi "
                                + "dan bukan nilai lalai Revit";
            var info = ctx.Doc.ProjectInformation;
            if (info == null)
            {
                var none = NothingToCheck("elemen Project Information", rule, "no_project_information_element");
                none.Remark = "Dokumen tiada elemen Project Information untuk dibaca — semak manual "
                              + "di Manage > Project Information.";
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
                if (v.Length == 0) empty.Add(name + " (kosong)");
                else if (defaults.Contains(v)) empty.Add(name + $" (\"{v}\" — nilai lalai)");
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
                ? $"Peraturan: {rule}. Keenam-enam medan terisi (Project Name \"{values["Project Name"]}\", "
                  + $"Project Number \"{values["Project Number"]}\") — patuh."
                : $"Peraturan: {rule}. {empty.Count}/{fields.Length} medan belum dikemaskini: "
                  + $"{string.Join(", ", empty)}. Isi di Manage > Project Information.";
            return o;
        }

        private static CheckOutcome ViewsFromTemplate(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "setiap view grafik ada View Template";
            var views = ctx.GraphicalViews;
            if (views.Count == 0) return NothingToCheck("view grafik", rule);

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
                "yes" => $"Peraturan: {rule}. Semua {views.Count} view ada template — patuh.",
                "no" => $"Peraturan: {rule}. {without.Count}/{views.Count} view tiada template (cth: "
                        + $"{string.Join(", ", split.Actionable.Take(3).Select(v => v.Name))}). Sapukan template "
                        + "daripada templat seni bina." + AuditTemplateAvailability.ActionabilityClause(split)
                        + FullListNote(split.Actionable.Count),
                _ => $"Peraturan: {rule}. {views.Count - without.Count}/{views.Count} view ada template; "
                     + $"{without.Count} view tiada template tetapi tiada template jenis tersebut wujud dalam "
                     + $"model ({split.UnactionableTypesText}; cth: "
                     + $"{string.Join(", ", split.Unactionable.Take(3).Select(v => v.Name))}) — tidak boleh "
                     + "tindakan. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.",
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
            string rule = $"sekurang-kurangnya satu view dengan '{token}' dalam namanya";
            var views = ctx.GraphicalViews;
            if (views.Count == 0) return NothingToCheck("view grafik", rule);

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
                ? $"Peraturan: {rule}. Dijumpai {hits.Count} view (cth: "
                  + $"{string.Join(", ", hits.Take(3).Select(v => v.Name))}) — patuh."
                : $"Peraturan: {rule}. Tiada nama view mengandungi '{token}' daripada {views.Count} "
                  + $"view yang disemak. Wujudkan view {token} untuk tujuan tersebut.";
            return o;
        }

        private static CheckOutcome AreaPlans(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "sekurang-kurangnya satu Area Plan untuk analisis ruang/zon";
            var areaPlans = ctx.AreaPlans;
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["area_plans"] = areaPlans.Count;
            AddNames(o, "names", areaPlans.Select(v => v.Name).ToList());
            o.ElementIds = areaPlans.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            bool ok = areaPlans.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Dijumpai {areaPlans.Count} Area Plan ("
                  + $"{string.Join(", ", areaPlans.Take(3).Select(v => v.Name))}) — patuh."
                : $"Peraturan: {rule}. Dijumpai 0 Area Plan dalam model — jana analisis ruang/zon "
                  + "daripada Area Plan.";
            return o;
        }

        private static CheckOutcome Legends(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "sekurang-kurangnya satu view Legend untuk komponen dan nota am";
            var legends = ctx.Legends;
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["legends"] = legends.Count;
            AddNames(o, "names", legends.Select(v => v.Name).ToList());
            o.ElementIds = legends.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            bool ok = legends.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Dijumpai {legends.Count} Legend ("
                  + $"{string.Join(", ", legends.Take(3).Select(v => v.Name))}) — patuh."
                : $"Peraturan: {rule}. Dijumpai 0 view Legend dalam model — sediakan Legend untuk "
                  + "komponen dan nota am.";
            return o;
        }

        private static CheckOutcome SchedulesRequired(AuditContext ctx, AuditFormRow row)
        {
            // Token lists + JKR prefix convention (jkrAR_mto_* / jkrAR_sch_rom_* /
            // jkrAR_sch_mc_*, per-component "<door|wall|…> schedule") live in
            // AuditMatching so the decision is unit-tested without Revit.
            string rule = "nama jadual mengandungi (termasuk awalan JKR): "
                + string.Join(" / ", AuditMatching.RequiredSchedules.Select(r =>
                    r.label + " (" + string.Join("|", r.tokens) + ")"))
                + " / jadual komponen (door|window|wall|floor|ceiling|roof|stair|railing + schedule)";

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
                ? $"Peraturan: {rule}. Ketiga-tiga jadual wajib dijumpai daripada {names.Count} "
                  + "jadual dalam model ("
                  + string.Join("; ", hits.Select(h => $"{h.required}: {h.schedule}")) + ") — patuh."
                : $"Peraturan: {rule}. Tiada nama jadual yang sepadan untuk: "
                  + $"{string.Join(", ", missing)} ({names.Count} jadual dalam model disemak). "
                  + "Jana jadual tersebut.";
            return o;
        }

        private static CheckOutcome SheetsContents(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "setiap Sheet mengandungi sekurang-kurangnya satu view";
            var sheets = ctx.Sheets;
            var emptySheets = sheets
                .Where(s => { try { return s.GetAllPlacedViews().Count == 0; } catch { return false; } })
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
                o.Remark = $"Peraturan: {rule}. Dijumpai 0 Sheet dalam model — sediakan helaian "
                           + "lukisan seni bina.";
                return o;
            }
            bool ok = emptySheets.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {sheets.Count} sheet mengandungi view — patuh."
                : $"Peraturan: {rule}. {emptySheets.Count}/{sheets.Count} sheet kosong (cth: "
                  + $"{string.Join(", ", emptySheets.Take(3).Select(s => s.SheetNumber + " " + s.Name))}). "
                  + "Susun view pada sheet berkenaan." + FullListNote(emptySheets.Count);
            return o;
        }

        private static CheckOutcome TitleblockJkr(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "nama family/type title block mengandungi 'JKR'";
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
                o.Remark = $"Peraturan: {rule}. Dijumpai 0 title block pada mana-mana sheet — "
                           + "gunakan JKR Title Block.";
                return o;
            }
            bool ok = jkr == tbs.Count;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {tbs.Count} title block sepadan — patuh."
                : $"Peraturan: {rule}. {tbs.Count - jkr}/{tbs.Count} title block tidak sepadan "
                  + $"(cth: {string.Join("; ", offendingLabels.Take(3))}). Tukar kepada JKR Title "
                  + "Block." + FullListNote(offendingLabels.Count);
            return o;
        }

        private static CheckOutcome LinksCurrent(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "setiap Revit link berstatus 'Loaded'";
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
                var none = NothingToCheck("Revit link", rule, "no_links_in_model");
                none.Remark = "Tiada Revit link dalam model, jadi tiada status pautan untuk disemak "
                              + "— sahkan secara manual sama ada pautan memang tidak diperlukan.";
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
                ? $"Peraturan: {rule}. Semua {links.Count} link berstatus Loaded — patuh."
                : $"Peraturan: {rule}. {notLoaded.Count}/{links.Count} link bukan Loaded: "
                  + $"{string.Join(", ", notLoaded.Take(3))}. Reload atau betulkan laluan link."
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
                    Remark = "Baris tidak menyatakan set view (BOMBA/PBT) yang dimaksudkan — semak manual.",
                    Evidence = { ["not_verifiable_reason"] = "row_does_not_name_a_view_set" },
                };
            }
            string rule = $"setiap view dengan '{token}' dalam namanya ada View Template";
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
                o.Remark = $"Peraturan: {rule}. Tiada nama view mengandungi '{token}' daripada "
                           + $"{all.Count} view yang disemak — wujudkan view {token} dan sapukan "
                           + "view template.";
                return o;
            }
            o.Compliance = AuditTemplateAvailability.Compliance(split);
            o.Remark = o.Compliance switch
            {
                "yes" => $"Peraturan: {rule}. Semua {views.Count} view {token} ada template — patuh.",
                "no" => $"Peraturan: {rule}. {without.Count}/{views.Count} view {token} tiada template "
                        + $"(cth: {string.Join(", ", split.Actionable.Take(3).Select(v => v.Name))}). Sapukan "
                        + $"template {token}." + AuditTemplateAvailability.ActionabilityClause(split)
                        + FullListNote(split.Actionable.Count),
                _ => $"Peraturan: {rule}. {views.Count - without.Count}/{views.Count} view {token} ada template; "
                     + $"{without.Count} view {token} tiada template tetapi tiada template jenis tersebut wujud "
                     + $"dalam model ({split.UnactionableTypesText}; cth: "
                     + $"{string.Join(", ", split.Unactionable.Take(3).Select(v => v.Name))}) — tidak boleh "
                     + "tindakan. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.",
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
            const string rule = "nama jenis (ElementType) yang digunakan ikut format JKR — "
                                + "kod JKR '(XXnnn)' / awalan 'jkrAR' / nama bahan";
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
                    // a real finding, not "not applicable" — the drafter has to
                    // confirm the omission is deliberate.
                    o.Compliance = "no";
                    o.Evidence["zero_count_expected"] = true;
                    o.SeverityOverride = Severities.Major;
                    o.Remark = $"0 elemen {label} dijumpai dalam model walaupun kategori ini "
                               + "biasanya wajib bagi model seni bina — sahkan sama ada memang "
                               + "tiada dalam skop projek. (Kualiti/maklumat/geometri: semak manual.)";
                    return o;
                }
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "category_not_present";
                o.Remark = $"0 elemen {label} dijumpai dalam model dan kategori ini tidak wajib — "
                           + "baris ini tidak berkenaan, atau semak manual jika sepatutnya ada.";
                return o;
            }

            if (types.Count == 0)
            {
                // Instances but no ElementType (Rooms, for example): the naming
                // rule has nothing to test, so it cannot be a pass.
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "no_types_to_check";
                o.Remark = $"{instances.Count} elemen {label} tetapi 0 jenis (ElementType) untuk "
                           + $"disemak, jadi peraturan \"{rule}\" tidak terpakai di sini — semak "
                           + "manual. (Kualiti/maklumat/geometri: semak manual.)";
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
                o.Remark = $"Peraturan: {rule}. {instances.Count} elemen {label} tetapi tiada satu pun "
                           + $"menggunakan {types.Count} jenis kategori ini (0 contoh diletakkan bagi "
                           + "setiap jenis) — tiada nama jenis yang digunakan untuk dinilai; semak "
                           + "manual. (Kualiti/maklumat/geometri: semak manual.)";
                return o;
            }

            o.Evidence["checked_count"] = usedTypes.Count;
            string unusedNote = unusedTypes.Count > 0
                ? $" {unusedTypes.Count} jenis tanpa contoh diletakkan tidak dikira (lihat unused_types)."
                : "";
            string untypedNote = untypedInstances > 0
                ? $" {untypedInstances} elemen tanpa jenis tidak dapat dinilai."
                : "";
            bool ok = nonConforming.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. {instances.Count} elemen, {usedTypes.Count} jenis digunakan — "
                  + "semua nama jenis yang digunakan patuh." + unusedNote + untypedNote
                  + " (Kualiti/maklumat/geometri: semak manual.)"
                : $"Peraturan: {rule}. {nonConforming.Count}/{usedTypes.Count} jenis yang digunakan "
                  + $"tidak patuh, meliputi {offendingInstances.Count}/{instances.Count} elemen (cth: "
                  + string.Join(", ", nonConforming.Take(3).Select(t =>
                        $"\"{t.Name}\" ×{usedTypes[t.Id].Count}")) + "). "
                  + (suggestedPairs.Count > 0
                      ? "Cadangan: " + string.Join(", ",
                          suggestedPairs.Take(3).Select(p => $"\"{p.current}\" → \"{p.suggested}\"")) + ". "
                      : "")
                  + (noSuggestion > 0
                      ? $"{noSuggestion} nama tiada cadangan automatik yang yakin — namakan semula "
                        + "secara manual mengikut format JKR. "
                      : "")
                  + unusedNote.TrimStart() + untypedNote
                  + " (Kualiti/maklumat/geometri: semak manual.)" + FullListNote(offendingInstances.Count);
            return o;
        }

        /// <summary>Section D "Room": placed rooms must each carry a Number AND a
        /// Name (ROOM_NUMBER / ROOM_NAME). Department is NOT checked here — A6
        /// (RoomsDepartment) owns it, and two rows must not cite the same fact
        /// under different rules. Unplaced rooms (Area 0) are counted for
        /// context only; they have no geometry to audit.</summary>
        private static CheckOutcome EvaluateRoomInstances(AuditContext ctx, bool expected)
        {
            const string rule = "setiap bilik (Room) yang diletakkan ada Number dan Name";
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
                $"[{x.r.Id.Value}] {(x.noNumber ? "(tiada Number)" : x.r.Number)} {(x.noName ? "(tiada Name)" : x.r.Name)}".Trim())
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
                    o.Remark = $"Peraturan: {rule}. Dijumpai 0 bilik yang diletakkan (placed Room) "
                               + (allRooms > 0 ? $"— {allRooms} Room wujud tetapi tidak diletakkan (Area 0). " : "dalam model. ")
                               + "Letak Room dahulu sebelum Number/Name boleh disemak. "
                               + "(Kualiti/maklumat/geometri: semak manual.)";
                    return o;
                }
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "category_not_present";
                o.Remark = $"0 bilik diletakkan dalam model dan kategori ini tidak wajib — baris ini "
                           + "tidak berkenaan, atau semak manual jika sepatutnya ada.";
                return o;
            }

            if (unreadable == rooms.Count)
            {
                o.Compliance = "not_verifiable";
                o.Evidence["checked_count"] = 0;
                o.Evidence["not_verifiable_reason"] = "parameters_unreadable";
                o.Remark = $"Peraturan: {rule}. {rooms.Count} bilik diletakkan tetapi parameter "
                           + "Number/Name tidak dapat dibaca — semak manual.";
                return o;
            }

            int checkedCount = rooms.Count - unreadable;
            o.Evidence["checked_count"] = checkedCount;
            bool ok = offenders.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            string unreadNote = unreadable > 0 ? $" ({unreadable} bilik tidak dapat dibaca, tidak dikira.)" : "";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {checkedCount} bilik yang diletakkan ada Number dan Name — patuh."
                  + unreadNote + " (Kualiti/maklumat/geometri: semak manual.)"
                : $"Peraturan: {rule}. {offenders.Count}/{checkedCount} bilik tidak lengkap "
                  + $"({o.Evidence["without_number"]} tiada Number, {o.Evidence["without_name"]} tiada Name; cth: "
                  + string.Join(", ", offenders.Take(3).Select(x => $"[{x.r.Id.Value}]")) + "). "
                  + "Isi Number dan Name untuk bilik tersebut." + unreadNote
                  + " (Kualiti/maklumat/geometri: semak manual.)" + FullListNote(offenders.Count);
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
                "workset", c => (c.Worksets.Count, c.Worksets)),
            ("phases", new[] { "phase", "phasing", "fasa" },
                "fasa (phase)", c => (c.Phases.Count, c.Phases)),
            ("design_options", new[] { "design option", "pilihan reka" },
                "design option", c => (c.DesignOptions.Count, c.DesignOptions)),
            ("scope_boxes", new[] { "scope box", "kotak skop" },
                "scope box", c => (c.ScopeBoxes.Count, c.ScopeBoxes)),
            ("shared_parameters", new[] { "shared parameter", "parameter", "parameter kongsi" },
                "parameter terikat pada projek", c => (c.SharedParameters.Count, c.SharedParameters)),
            ("families", new[] { "family", "families", "keluarga" },
                "family dimuatkan", c => (c.Families.Count, c.Families)),
            ("levels", new[] { "level", "aras" },
                "aras", c => (c.Levels.Count, c.Levels.Select(l => l.Name ?? "").ToList())),
            ("grids", new[] { "grid", "gridline" },
                "grid", c => (c.Grids.Count, c.Grids.Select(g => g.Name).ToList())),
            ("views", new[] { "view", "views" },
                "view grafik", c => (c.GraphicalViews.Count, c.GraphicalViews.Select(v => v.Name).ToList())),
            ("sheets", new[] { "sheet", "helaian" },
                "sheet", c => (c.Sheets.Count, c.Sheets.Select(s => s.SheetNumber ?? "").ToList())),
            ("schedules", new[] { "schedule", "jadual" },
                "jadual", c => (c.Schedules.Count, c.Schedules.Select(s => s.Name).ToList())),
            // Header/uncheckable rows that name a deliverable but fail a checker's
            // 2-group match (C.2.0 "Legends", C.5.0 "Link Files", area-plan rows):
            // still show the inventory a human would look up first.
            ("legends", new[] { "legend", "legenda" },
                "view Legend", c => (c.Legends.Count, c.Legends.Select(v => v.Name).ToList())),
            ("links", new[] { "link files", "link file", "linked", "link", "pautan" },
                "Revit link", c => (c.LinkTypes.Count, c.LinkTypes.Select(l => l.Name).ToList())),
            ("area_plans", new[] { "area plan", "pelan keluasan", "spatial", "zoning", "zon" },
                "Area Plan", c => (c.AreaPlans.Count, c.AreaPlans.Select(v => v.Name).ToList())),
            ("warnings", new[] { "warning", "error", "amaran", "ralat" },
                "amaran (warning) dalam model", c => (c.Warnings, new List<string>())),
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
            return (evidence, "Konteks model: " + string.Join("; ", parts) + ".");
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
