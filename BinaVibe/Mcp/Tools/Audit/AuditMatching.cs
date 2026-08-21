// AuditMatching — the row → checker decision, as pure string logic.
//
// Split out of AuditCheckers so the decision is testable without Revit: the
// checker registry's static initialiser touches Autodesk.Revit.DB types, so it
// cannot even be loaded in the test host, while the matching itself is only
// keyword scoring over Description + GuidelineRef. AuditCoverageTests pins the
// set of rows that fall through to not_verifiable against this file.
//
// Every entry here has a counterpart in AuditCheckers: each CheckerKeywords.Id
// is an AuditChecker.Id in AuditCheckers.All, and each CategoryLabels entry is a
// key of AuditCheckers.CategoryMap (label → Revit category). Drift is a dev
// error surfaced by AuditCheckers.Match / EvaluateCategory, never a verdict.
//
// No Revit, no PdfPig — System + AuditModels only.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Audit
{
    /// <summary>Keyword spec for one checker. A row matches when at least
    /// MinGroups groups each have ≥1 keyword present in the row's
    /// description+reference (lowercased). Higher hit count wins; ties go to
    /// the earlier entry.</summary>
    public sealed class CheckerKeywords
    {
        public string Id = "";
        public string[][] Groups = Array.Empty<string[]>();
        public int MinGroups = 2;

        public int MatchScore(string text)
        {
            int hit = 0;
            foreach (var group in Groups)
                if (group.Any(k => text.Contains(k))) hit++;
            return hit >= MinGroups ? hit : 0;
        }
    }

    public static class AuditMatching
    {
        /// <summary>Checker id used for every Section D row.</summary>
        public const string FamilyCategoryId = "family_category";

        /// <summary>Ordered: on a tie the earlier entry wins.</summary>
        public static readonly CheckerKeywords[] Checkers =
        {
            new()
            {
                Id = "file_naming",
                Groups = new[]
                {
                    new[] { "files are named", "file name", "named according", "penamaan fail", "nama fail" },
                    new[] { "guidelines", "standards", "garis panduan", "piawaian" },
                },
            },
            new()
            {
                Id = "base_point",
                Groups = new[]
                {
                    // "project base" too: a wrapped cell can split "Base Point"
                    // across two text lines.
                    new[] { "base point", "project base", "titik asas" },
                    new[] { "grid", "positioned", "kedudukan" },
                },
            },
            // rooms_department sits BEFORE grids_levels so that a row mentioning
            // both wins for rooms on a tie — a row that says "rooms" is a rooms
            // row. (Historically this compensated for parser line bleed; the
            // banding fix removed the bleed, the tie order is kept because it is
            // the correct precedence regardless.)
            new()
            {
                Id = "rooms_department",
                Groups = new[]
                {
                    new[] { "rooms", "room", "bilik", "ruang" },
                    new[] { "department", "catagorised", "categorised", "categorized", "jabatan" },
                },
            },
            new()
            {
                Id = "grids_levels",
                Groups = new[]
                {
                    new[] { "gridlines and levels", "gridlines", "grid", "grid dan aras" },
                    new[] { "organised", "organized", "floor plan", "levels", "aras", "tersusun" },
                },
            },
            new()
            {
                Id = "materials_assigned",
                Groups = new[]
                {
                    new[] { "materials", "bahan" },
                    new[] { "included in model", "model elements", "assigned", "elemen model" },
                },
            },
            new()
            {
                Id = "project_info",
                Groups = new[]
                {
                    new[] { "project information", "maklumat projek" },
                    new[] { "updated", "dikemaskini", "kemas kini" },
                },
            },
            new()
            {
                Id = "views_template",
                Groups = new[]
                {
                    new[] { "views" },
                    new[] { "template", "templat" },
                    new[] { "created based", "architectural" },
                },
                MinGroups = 3,
            },
            new() { Id = "views_wip", Groups = new[] { new[] { "wip" }, new[] { "views", "view" } } },
            // "submission" alone is deliberately NOT in group 2: section E's
            // "BOMBA/PBT Submission" rows must fall through to
            // view_template_applied, not the view-existence buckets.
            new() { Id = "views_pbt", Groups = new[] { new[] { "pbt" }, new[] { "views", "view" } } },
            new() { Id = "views_bomba", Groups = new[] { new[] { "bomba" }, new[] { "views", "view" } } },
            new()
            {
                Id = "views_dokumen",
                Groups = new[]
                {
                    new[] { "dokumen" },
                    new[] { "views", "view", "documentation", "contract" },
                },
            },
            new()
            {
                Id = "area_plans",
                Groups = new[]
                {
                    new[] { "spatial", "zoning", "zon" },
                    new[] { "area plan", "pelan keluasan" },
                },
            },
            new()
            {
                Id = "legends",
                Groups = new[]
                {
                    new[] { "legends", "legend", "legenda" },
                    new[] { "components", "general notes", "managed", "contents" },
                },
            },
            new()
            {
                Id = "schedules_required",
                Groups = new[]
                {
                    new[] { "schedules", "schedule", "jadual" },
                    new[] { "accomodation", "accommodation", "component", "takeoff", "quantities" },
                },
            },
            new()
            {
                Id = "sheets_contents",
                Groups = new[]
                {
                    new[] { "sheets", "sheet", "helaian" },
                    new[] { "drawings", "managed", "title block", "plans", "elevations" },
                },
            },
            new()
            {
                Id = "titleblock_jkr",
                Groups = new[]
                {
                    new[] { "jkr title block", "title block" },
                    new[] { "jkr", "furnished" },
                },
            },
            new()
            {
                Id = "links_current",
                Groups = new[]
                {
                    new[] { "link", "linked", "pautan" },
                    new[] { "up-to-date", "correct", "models linked", "terkini" },
                },
            },
            new()
            {
                Id = "view_template_applied",
                Groups = new[]
                {
                    new[] { "bomba submission", "pbt submission", "view template", "templat" },
                    new[] { "applied", "submission", "correctly" },
                },
            },
        };

        /// <summary>Section D row labels, in match order (first substring hit
        /// wins, so "furniture systems" precedes "furniture" and "structural
        /// column" precedes "columns"). Each is a key of AuditCheckers.CategoryMap.</summary>
        public static readonly string[] CategoryLabels =
        {
            "floors", "walls", "ceilings", "roofs", "stairs", "railings", "ramps", "room",
            "curtain", "doors", "windows", "caseworks", "casework", "furniture systems",
            "furniture", "plumbing", "specialty", "generic", "structural column", "columns",
            "parking", "pipes", "toposurface", "mass",
        };

        public static string? MatchCategory(string description)
        {
            var d = description.ToLowerInvariant();
            foreach (var label in CategoryLabels)
                if (d.Contains(label)) return label;
            return null;
        }

        /// <summary>Best-scoring checker id for a non-Section-D row, or null.</summary>
        public static string? MatchCheckerId(AuditFormRow row)
        {
            var text = (row.Description + " " + row.GuidelineRef).ToLowerInvariant();
            string? best = null;
            int bestScore = 0;
            foreach (var c in Checkers)
            {
                var s = c.MatchScore(text);
                if (s > bestScore) { best = c.Id; bestScore = s; }
            }
            return best;
        }

        /// <summary>The whole decision: (checker id, category label) — both
        /// null when the row is not_verifiable. Section D rows resolve through
        /// the category labels instead; their "description" is just a category
        /// name.</summary>
        public static (string? checkerId, string? category) Match(AuditFormRow row)
        {
            // Section headers ("C 3.0 Schedules/Quantities", "E 1.0 View
            // Template") carry no criterion — never a checker, never a verdict.
            if (IsSectionHeader(row)) return (null, null);
            if (row.Section == "D")
            {
                var cat = MatchCategory(row.Description);
                return cat == null ? (null, null) : (FamilyCategoryId, cat);
            }
            return (MatchCheckerId(row), null);
        }

        /// <summary>True for "x.0" rows (C 1.0 … C 5.0, E 1.0): the form's
        /// sub-section headings. They are reported as headers, not scored.</summary>
        public static bool IsSectionHeader(AuditFormRow row) =>
            row.RowRef.EndsWith(".0", StringComparison.Ordinal);

        // ─── C 3.1 required schedules ───────────────────────────────────

        /// <summary>The three schedules BIM 010 C 3.1 requires, with the name
        /// tokens that identify each function. Includes JKR's own prefix
        /// convention (jkrAR_mto_* / jkrAR_sch_rom_* / jkrAR_sch_mc_*) so a
        /// model that follows the JKR naming standard is recognised. Matching
        /// is substring over the lowercased schedule name — a name must contain
        /// one of these tokens; nothing is inferred from anything else.</summary>
        public static readonly (string label, string[] tokens)[] RequiredSchedules =
        {
            ("Schedule of Accommodation", new[]
            {
                "accommodation", "accomodation", "soa",
                "jkrar_sch_rom_", "room schedule", "room key", "area schedule", "jkrar_sch_are_",
            }),
            ("Building Component Schedule", new[]
            {
                "component",
                "jkrar_sch_mc_", "multi-category schedule",
            }),
            ("Material Takeoff Schedule", new[]
            {
                "takeoff", "take off", "take-off", "material",
                "jkrar_mto_",
            }),
        };

        /// <summary>Per-component schedules ("Door Schedule", "Wall Schedule")
        /// count as Building Component Schedules: the name must contain both a
        /// component word and "schedule".</summary>
        private static readonly string[] ComponentWords =
        {
            "door", "window", "wall", "floor", "ceiling", "roof", "stair", "railing",
        };

        /// <summary>Which required schedule a schedule name fulfils, or null.
        /// Conservative: "Test" or "Schedule 1" match nothing.</summary>
        public static string? ClassifySchedule(string name)
        {
            var n = name.ToLowerInvariant();
            foreach (var (label, tokens) in RequiredSchedules)
                if (tokens.Any(t => n.Contains(t))) return label;
            if (n.Contains("schedule") && ComponentWords.Any(w => n.Contains(w)))
                return "Building Component Schedule";
            return null;
        }

        /// <summary>Resolve the model's schedule names against RequiredSchedules.
        /// found: label → first matching schedule name; missing: labels with no
        /// match (in RequiredSchedules order).</summary>
        public static (List<(string required, string schedule)> found, List<string> missing)
            MatchRequiredSchedules(IEnumerable<string> scheduleNames)
        {
            var names = scheduleNames.ToList();
            var found = new List<(string, string)>();
            var missing = new List<string>();
            foreach (var (label, _) in RequiredSchedules)
            {
                var hit = names.FirstOrDefault(n => ClassifySchedule(n) == label);
                if (hit != null) found.Add((label, hit));
                else missing.Add(label);
            }
            return (found, missing);
        }
    }
}
