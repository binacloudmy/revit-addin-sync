// AuditCheckers — deterministic checker library for fill_audit.
//
// Each checker owns: keyword groups that match a checklist row's wording
// (EN + BM), an evaluator that reads the LIVE Revit document through the shared
// AuditContext, and a remark template filled ONLY from that evaluation's
// evidence. No LLM anywhere: a row either matches a checker (≥ MinGroups
// keyword groups hit) and gets an evidence-backed verdict, or it is honestly
// not_verifiable. Never guess.
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
        /// <summary>Row matches when at least MinGroups groups each have ≥1
        /// keyword present in the row's description+reference (lowercased).</summary>
        public string[][] KeywordGroups = Array.Empty<string[]>();
        public int MinGroups = 2;
        public string Severity = Severities.Major;
        /// <summary>An AUTHORITATIVE guideline clause for this check (e.g.
        /// "Appendix B.1.A (a)"), used to backfill a row whose Reference cell the
        /// source form left blank. Seeded empty for every checker today — only a
        /// verified clause string belongs here; nothing is ever synthesised.</summary>
        public string GuidelineRef = "";
        public Func<AuditContext, AuditFormRow, CheckOutcome> Evaluate = (_, _) => new CheckOutcome();

        public int MatchScore(string text)
        {
            int hit = 0;
            foreach (var group in KeywordGroups)
                if (group.Any(k => text.Contains(k))) hit++;
            return hit >= MinGroups ? hit : 0;
        }
    }

    public static class AuditCheckers
    {
        private const int Cap = 15;              // max names listed in evidence
        private const int IdCap = 500;           // max element ids carried per row
        private const int ElementScanCap = 3000; // material scan bound

        // ─── matching ───────────────────────────────────────────────────

        /// <summary>Best-scoring checker for a row, or null (→ not_verifiable).
        /// Section D rows resolve through the category map instead — their
        /// "description" is just a category name.</summary>
        public static (AuditChecker checker, string? category)? Match(AuditFormRow row)
        {
            if (row.Section == "D")
            {
                var cat = MatchCategory(row.Description);
                return cat == null ? null : (FamilyCategoryChecker, cat);
            }
            var text = (row.Description + " " + row.GuidelineRef).ToLowerInvariant();
            AuditChecker? best = null;
            int bestScore = 0;
            foreach (var c in All)
            {
                var s = c.MatchScore(text);
                if (s > bestScore) { best = c; bestScore = s; }
            }
            return best == null ? null : (best, null);
        }

        // ─── registry ───────────────────────────────────────────────────

        public static readonly List<AuditChecker> All = new()
        {
            new AuditChecker
            {
                Id = "file_naming",
                Severity = Severities.Minor,
                KeywordGroups = new[]
                {
                    new[] { "files are named", "file name", "named according", "penamaan fail", "nama fail" },
                    new[] { "guidelines", "standards", "garis panduan", "piawaian" },
                },
                Evaluate = FileNaming,
            },
            new AuditChecker
            {
                Id = "base_point",
                Severity = Severities.Critical,
                KeywordGroups = new[]
                {
                    // "project base" too: a wrapped cell can split "Base Point"
                    // across two text lines.
                    new[] { "base point", "project base", "titik asas" },
                    new[] { "grid", "positioned", "kedudukan" },
                },
                Evaluate = BasePoint,
            },
            // rooms_department sits BEFORE grids_levels so that a row mentioning
            // both wins for rooms on a tie — a row that says "rooms" is a rooms
            // row. (Historically this compensated for parser line bleed; the
            // banding fix removed the bleed, the tie order is kept because it is
            // the correct precedence regardless.)
            new AuditChecker
            {
                Id = "rooms_department",
                KeywordGroups = new[]
                {
                    new[] { "rooms", "room", "bilik", "ruang" },
                    new[] { "department", "catagorised", "categorised", "categorized", "jabatan" },
                },
                Evaluate = RoomsDepartment,
            },
            new AuditChecker
            {
                Id = "grids_levels",
                Severity = Severities.Critical,
                KeywordGroups = new[]
                {
                    new[] { "gridlines and levels", "gridlines", "grid", "grid dan aras" },
                    new[] { "organised", "organized", "floor plan", "levels", "aras", "tersusun" },
                },
                Evaluate = GridsLevels,
            },
            new AuditChecker
            {
                Id = "materials_assigned",
                KeywordGroups = new[]
                {
                    new[] { "materials", "bahan" },
                    new[] { "included in model", "model elements", "assigned", "elemen model" },
                },
                Evaluate = MaterialsAssigned,
            },
            new AuditChecker
            {
                Id = "project_info",
                KeywordGroups = new[]
                {
                    new[] { "project information", "maklumat projek" },
                    new[] { "updated", "dikemaskini", "kemas kini" },
                },
                Evaluate = ProjectInfo,
            },
            new AuditChecker
            {
                Id = "views_template",
                KeywordGroups = new[]
                {
                    new[] { "views" },
                    new[] { "template", "templat" },
                    new[] { "created based", "architectural" },
                },
                MinGroups = 3,
                Evaluate = ViewsFromTemplate,
            },
            new AuditChecker
            {
                Id = "views_wip",
                KeywordGroups = new[] { new[] { "wip" }, new[] { "views", "view" } },
                Evaluate = (ctx, row) => ViewBucket(ctx, "WIP"),
            },
            // "submission" alone is deliberately NOT in group 2: section E's
            // "BOMBA/PBT Submission" rows must fall through to
            // view_template_applied, not the view-existence buckets.
            new AuditChecker
            {
                Id = "views_pbt",
                KeywordGroups = new[] { new[] { "pbt" }, new[] { "views", "view" } },
                Evaluate = (ctx, row) => ViewBucket(ctx, "PBT"),
            },
            new AuditChecker
            {
                Id = "views_bomba",
                KeywordGroups = new[] { new[] { "bomba" }, new[] { "views", "view" } },
                Evaluate = (ctx, row) => ViewBucket(ctx, "BOMBA"),
            },
            new AuditChecker
            {
                Id = "views_dokumen",
                KeywordGroups = new[]
                {
                    new[] { "dokumen" },
                    new[] { "views", "view", "documentation", "contract" },
                },
                Evaluate = (ctx, row) => ViewBucket(ctx, "Dokumen"),
            },
            new AuditChecker
            {
                Id = "area_plans",
                KeywordGroups = new[]
                {
                    new[] { "spatial", "zoning", "zon" },
                    new[] { "area plan", "pelan keluasan" },
                },
                Evaluate = AreaPlans,
            },
            new AuditChecker
            {
                Id = "legends",
                KeywordGroups = new[]
                {
                    new[] { "legends", "legend", "legenda" },
                    new[] { "components", "general notes", "managed", "contents" },
                },
                Evaluate = Legends,
            },
            new AuditChecker
            {
                Id = "schedules_required",
                KeywordGroups = new[]
                {
                    new[] { "schedules", "schedule", "jadual" },
                    new[] { "accomodation", "accommodation", "component", "takeoff", "quantities" },
                },
                Evaluate = SchedulesRequired,
            },
            new AuditChecker
            {
                Id = "sheets_contents",
                KeywordGroups = new[]
                {
                    new[] { "sheets", "sheet", "helaian" },
                    new[] { "drawings", "managed", "title block", "plans", "elevations" },
                },
                Evaluate = SheetsContents,
            },
            new AuditChecker
            {
                Id = "titleblock_jkr",
                Severity = Severities.Critical,
                KeywordGroups = new[]
                {
                    new[] { "jkr title block", "title block" },
                    new[] { "jkr", "furnished" },
                },
                Evaluate = TitleblockJkr,
            },
            new AuditChecker
            {
                Id = "links_current",
                Severity = Severities.Critical,
                KeywordGroups = new[]
                {
                    new[] { "link", "linked", "pautan" },
                    new[] { "up-to-date", "correct", "models linked", "terkini" },
                },
                Evaluate = LinksCurrent,
            },
            new AuditChecker
            {
                Id = "view_template_applied",
                KeywordGroups = new[]
                {
                    new[] { "bomba submission", "pbt submission", "view template", "templat" },
                    new[] { "applied", "submission", "correctly" },
                },
                Evaluate = ViewTemplateApplied,
            },
        };

        // Section D category map: printed row label → Revit category, plus
        // whether an architectural model is EXPECTED to contain it. Expected +
        // zero instances is a finding ("verify this is intentional"), not a
        // shrug — that distinction is the whole point of the flag.
        private static readonly (string label, BuiltInCategory bic, bool expected)[] CategoryMap =
        {
            ("floors", BuiltInCategory.OST_Floors, true),
            ("walls", BuiltInCategory.OST_Walls, true),
            ("ceilings", BuiltInCategory.OST_Ceilings, true),
            ("roofs", BuiltInCategory.OST_Roofs, true),
            ("stairs", BuiltInCategory.OST_Stairs, true),
            ("railings", BuiltInCategory.OST_StairsRailing, true),
            ("ramps", BuiltInCategory.OST_Ramps, false),
            ("room", BuiltInCategory.OST_Rooms, true),
            ("curtain", BuiltInCategory.OST_CurtainWallPanels, false),
            ("doors", BuiltInCategory.OST_Doors, true),
            ("windows", BuiltInCategory.OST_Windows, true),
            ("caseworks", BuiltInCategory.OST_Casework, false),
            ("casework", BuiltInCategory.OST_Casework, false),
            ("furniture systems", BuiltInCategory.OST_FurnitureSystems, false),
            ("furniture", BuiltInCategory.OST_Furniture, false),
            ("plumbing", BuiltInCategory.OST_PlumbingFixtures, false),
            ("specialty", BuiltInCategory.OST_SpecialityEquipment, false),
            ("generic", BuiltInCategory.OST_GenericModel, false),
            ("structural column", BuiltInCategory.OST_StructuralColumns, true),
            ("columns", BuiltInCategory.OST_Columns, true),
            ("parking", BuiltInCategory.OST_Parking, false),
            ("pipes", BuiltInCategory.OST_PipeCurves, false),
            ("toposurface", BuiltInCategory.OST_Topography, false),
            ("mass", BuiltInCategory.OST_Mass, false),
        };

        private static string? MatchCategory(string description)
        {
            var d = description.ToLowerInvariant();
            foreach (var (label, _, _) in CategoryMap)
                if (d.Contains(label)) return label;
            return null;
        }

        private static readonly AuditChecker FamilyCategoryChecker = new()
        {
            Id = "family_category",
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
            const string rule = "grid > 0, aras > 0, dan setiap pelan lantai terikat pada satu aras";
            int grids = ctx.Grids.Count;
            int levels = ctx.Levels.Count;
            var plans = ctx.FloorPlans;
            var noLevel = plans.Where(v => v.GenLevel == null).Select(v => v.Name).ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["grids"] = grids;
            o.Evidence["levels"] = levels;
            o.Evidence["floor_plans"] = plans.Count;
            // Same inventory base_point cites, so rows about grids agree.
            AddNames(o, "grid_names", ctx.Grids.Select(g => g.Name).ToList());
            AddNames(o, "floor_plans_without_level", noLevel);
            o.ElementIds = plans.Where(v => v.GenLevel == null).Take(IdCap)
                                .Select(v => (long)v.Id.Value).ToList();

            if (grids == 0 && levels == 0 && plans.Count == 0)
            {
                var empty = NothingToCheck("grid, aras atau pelan lantai", rule);
                empty.SeverityOverride = Severities.Critical;
                return empty;
            }

            bool ok = grids > 0 && levels > 0 && noLevel.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            if (grids == 0 || levels == 0) o.SeverityOverride = Severities.Critical;
            o.Remark = ok
                ? $"Peraturan: {rule}. Dijumpai {grids} grid, {levels} aras; semua {plans.Count} "
                  + "pelan lantai terikat pada aras — patuh."
                : $"Peraturan: {rule}. Dijumpai {grids} grid, {levels} aras. "
                  + (grids == 0 ? "Tiada grid dalam model. " : "")
                  + (levels == 0 ? "Tiada aras dalam model. " : "")
                  + (noLevel.Count > 0
                      ? $"{noLevel.Count}/{plans.Count} pelan lantai tanpa aras (cth: "
                        + $"{string.Join(", ", noLevel.Take(3))})." + FullListNote(noLevel.Count)
                      : "");
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
            var cats = new[]
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs,
            };
            int scanned = 0;
            var offenders = new List<Element>();
            foreach (var bic in cats)
            {
                foreach (var e in new FilteredElementCollector(ctx.Doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (++scanned > ElementScanCap) break;
                    ICollection<ElementId> mats;
                    try { mats = e.GetMaterialIds(false); } catch { continue; }
                    if (mats == null || mats.Count == 0) offenders.Add(e);
                }
            }

            if (scanned == 0) return NothingToCheck("elemen Walls/Floors/Ceilings/Roofs", rule);

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["elements_scanned"] = scanned;
            o.Evidence["scan_cap_reached"] = scanned > ElementScanCap;
            o.Evidence["without_material"] = offenders.Count;
            o.Evidence["scanned_categories"] = new List<object> { "Walls", "Floors", "Ceilings", "Roofs" };
            AddNames(o, "examples", offenders.Select(e => e.Name ?? "").ToList());
            o.ElementIds = offenders.Take(IdCap).Select(e => (long)e.Id.Value).ToList();

            bool ok = offenders.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {scanned} elemen ada material — patuh."
                : $"Peraturan: {rule}. {offenders.Count}/{scanned} elemen tiada material (cth id: "
                  + $"{string.Join(", ", o.ElementIds.Take(3))}). Tetapkan material pada jenis "
                  + "elemen berkenaan." + FullListNote(offenders.Count);
            return o;
        }

        private static CheckOutcome ProjectInfo(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "Project Name/Number/Address/Client Name/Status terisi dan bukan nilai lalai Revit";
            var info = ctx.Doc.ProjectInformation;
            var fields = new (string name, string? val)[]
            {
                ("Project Name", info?.Name),
                ("Project Number", info?.Number),
                ("Address", info?.Address),
                ("Client Name", info?.ClientName),
                ("Status", info?.Status),
            };
            var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "project name", "project number", "enter address here", "project address",
                "owner", "project status",
            };
            var empty = fields
                .Where(f => string.IsNullOrWhiteSpace(f.val) || defaults.Contains(f.val!.Trim()))
                .Select(f => string.IsNullOrWhiteSpace(f.val)
                    ? f.name + " (kosong)"
                    : f.name + $" (\"{f.val!.Trim()}\" — nilai lalai)")
                .ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["fields_checked"] = fields.Select(f => f.name).ToList();
            o.Evidence["empty_or_default"] = empty;
            bool ok = empty.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Kelima-lima medan terisi — patuh."
                : $"Peraturan: {rule}. Belum dikemaskini: {string.Join(", ", empty)}. "
                  + "Isi di Manage > Project Information.";
            return o;
        }

        private static CheckOutcome ViewsFromTemplate(AuditContext ctx, AuditFormRow row)
        {
            const string rule = "setiap view grafik ada View Template";
            var views = ctx.GraphicalViews;
            if (views.Count == 0) return NothingToCheck("view grafik", rule);

            var without = views.Where(v => v.ViewTemplateId == ElementId.InvalidElementId).ToList();
            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["views"] = views.Count;
            o.Evidence["without_view_template"] = without.Count;
            AddNames(o, "examples", without.Select(v => v.Name).ToList());
            o.ElementIds = without.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            bool ok = without.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {views.Count} view ada template — patuh."
                : $"Peraturan: {rule}. {without.Count}/{views.Count} view tiada template (cth: "
                  + $"{string.Join(", ", without.Take(3).Select(v => v.Name))}). Sapukan template "
                  + "daripada templat seni bina." + FullListNote(without.Count);
            return o;
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
            var required = new (string label, string[] tokens)[]
            {
                ("Schedule of Accommodation", new[] { "accommodation", "accomodation", "soa" }),
                ("Building Component Schedule", new[] { "component" }),
                ("Material Takeoff Schedule", new[] { "takeoff", "take off", "material" }),
            };
            string rule = "nama jadual mengandungi: "
                + string.Join(" / ", required.Select(r => r.label + " (" + string.Join("|", r.tokens) + ")"));

            var names = ctx.Schedules.Select(v => v.Name).ToList();
            var found = new List<object>();
            var missing = new List<string>();
            foreach (var (label, tokens) in required)
            {
                var hit = names.FirstOrDefault(n => tokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
                if (hit != null) found.Add(new Dictionary<string, object?> { ["required"] = label, ["schedule"] = hit });
                else missing.Add(label);
            }
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
                  + "jadual dalam model — patuh."
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
            var without = views.Where(v => v.ViewTemplateId == ElementId.InvalidElementId).ToList();

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["token"] = token;
            o.Evidence["views"] = views.Count;
            o.Evidence["total_views"] = all.Count;
            AddNames(o, "without_template", without.Select(v => v.Name).ToList());
            o.ElementIds = without.Take(IdCap).Select(v => (long)v.Id.Value).ToList();

            if (views.Count == 0)
            {
                o.Compliance = "no";
                o.Evidence["zero_count_expected"] = true;
                o.Remark = $"Peraturan: {rule}. Tiada nama view mengandungi '{token}' daripada "
                           + $"{all.Count} view yang disemak — wujudkan view {token} dan sapukan "
                           + "view template.";
                return o;
            }
            bool ok = without.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Peraturan: {rule}. Semua {views.Count} view {token} ada template — patuh."
                : $"Peraturan: {rule}. {without.Count}/{views.Count} view {token} tiada template "
                  + $"(cth: {string.Join(", ", without.Take(3).Select(v => v.Name))}). Sapukan "
                  + $"template {token}." + FullListNote(without.Count);
            return o;
        }

        /// <summary>Section D: per-category presence + type-naming structure.
        /// Only the "Standard component file naming" column is automatable;
        /// Quality/Information/Geometry stay manual and the remark says so.</summary>
        public static CheckOutcome EvaluateCategory(AuditContext ctx, string label)
        {
            const string rule = ">=2 segmen dipisah '-' atau '_' pada nama jenis (ElementType)";
            var entry = CategoryMap.First(c => c.label == label);
            var instances = new FilteredElementCollector(ctx.Doc).OfCategory(entry.bic)
                .WhereElementIsNotElementType().ToList();
            var types = new FilteredElementCollector(ctx.Doc).OfCategory(entry.bic)
                .WhereElementIsElementType().Cast<ElementType>().ToList();
            var nonConforming = types
                .Where(t => (t.Name ?? "").Split('-', '_').Count(s => s.Trim().Length > 0) < 2)
                .ToList();

            // Best-effort rename per offending type name; null where no confident
            // transform exists (a single-word type name has nothing to split).
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
                    });
            }

            var o = new CheckOutcome { RulePattern = rule };
            o.Evidence["rule"] = rule;
            o.Evidence["category"] = label;
            o.Evidence["category_expected"] = entry.expected;
            o.Evidence["instances"] = instances.Count;
            o.Evidence["types"] = types.Count;
            AddNames(o, "types_nonconforming_naming", nonConforming.Select(t => t.Name ?? "").ToList());
            o.Evidence["naming_suggestions"] = suggestions;
            o.Evidence["naming_suggestions_truncated"] = Math.Max(0, nonConforming.Count - Cap);
            o.Evidence["suggested_count"] = suggestedPairs.Count;
            o.Evidence["no_suggestion_count"] = noSuggestion;
            o.Evidence["automated_scope"] = "standard naming only; quality/information/geometry manual";
            o.ElementIds = nonConforming.Take(IdCap).Select(t => (long)t.Id.Value).ToList();

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

            bool ok = nonConforming.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Format dijangka: {rule}. {instances.Count} elemen, {types.Count} jenis — "
                  + "semua nama jenis sepadan. (Kualiti/maklumat/geometri: semak manual.)"
                : $"Format dijangka: {rule}. {instances.Count} elemen; "
                  + $"{nonConforming.Count}/{types.Count} nama jenis tidak sepadan. "
                  + (suggestedPairs.Count > 0
                      ? "Cadangan: " + string.Join(", ",
                          suggestedPairs.Take(3).Select(p => $"\"{p.current}\" → \"{p.suggested}\"")) + ". "
                      : "")
                  + (noSuggestion > 0
                      ? $"{noSuggestion} nama tiada cadangan automatik yang yakin — namakan semula "
                        + "secara manual mengikut format itu. "
                      : "")
                  + "(Kualiti/maklumat/geometri: semak manual.)" + FullListNote(nonConforming.Count);
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
