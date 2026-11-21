// BIM010 cells and the section-and-cell summary the JKR pane and the Borang
// export read.
//
// A cell is one verdict on one Borang row (or one Section D family × column):
//   Pass    — AI-verified comply
//   Fail    — AI-verified not comply
//   Manual  — needs a person (no ✓/✗, never in the %)
// Cells come from two places: fill_audit records (one per form row today —
// the per-cell payload, tech-spec C2, is not built yet) and the backend's v2
// checks, mapped to a row by Bim010Mapping. A row's Borang mark follows the
// prototype: any Fail → X; else any Manual → semak manual; else any Pass → √;
// no cells → not assessed. Section % = Pass / (Pass + Fail) over that
// section's AI cells; Manual cells are counted separately.
// System only — unit-tested.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Audit
{
    public enum Bim010Verdict { Pass, Fail, Manual }

    /// <summary>Section D criteria columns, as printed: Naming, Quality and
    /// integrity, Information, Design geometry.</summary>
    public static class Bim010Column
    {
        public const string None = "";
        public const string Naming = "n";
        public const string Quality = "q";
        public const string Information = "i";
        public const string Geometry = "g";
        public static readonly string[] All = { Naming, Quality, Information, Geometry };
    }

    public sealed class Bim010Cell
    {
        /// <summary>Row key ("A4", "C1.1", "D10").</summary>
        public string Key = "";
        /// <summary>Section D column (Bim010Column), "" elsewhere.</summary>
        public string Column = "";
        public Bim010Verdict Verdict;
        /// <summary>"audit" (fill_audit checker) or "backend" (v2 check).</summary>
        public string Source = "";
        public string Title = "";
        /// <summary>Remark / reason for the Borang Remarks column.</summary>
        public string Note = "";
        public string Section => Bim010Form.SectionOf(Key);
    }

    public sealed class Bim010SectionScore
    {
        public string Section = "";
        /// <summary>BIM010 cells: AI-verified comply. Spec-based (AI-474): checks passed (AI pass + List + Human).</summary>
        public int Pass;
        /// <summary>BIM010 cells: AI-verified not comply. Spec-based: checks NOT passed (fail + partial + unjudged).</summary>
        public int Fail;
        /// <summary>Cells that need a person — shown separately, not in the %.</summary>
        public int Pending;
        /// <summary>Spec-based summary: checks that apply (Fail = not passed: fail + partial + unjudged);
        /// BIM010 cells otherwise.</summary>
        public int AiTotal => Pass + Fail;
        /// <summary>Pass / (Pass + Fail), rounded; null when the section has nothing counted.</summary>
        public int? Pct => AiTotal == 0 ? (int?)null : (int)Math.Round(Pass * 100.0 / AiTotal, MidpointRounding.AwayFromZero);
    }

    /// <summary>The Borang mark for one row: "y" (√), "n" (X), "m" (semak manual)
    /// or "" (not assessed: no cells).</summary>
    public sealed class Bim010RowMark
    {
        public string Key = "";
        public string Mark = "";
        /// <summary>Section D only: mark per column (Bim010Column → mark).</summary>
        public Dictionary<string, string> Columns = new Dictionary<string, string>();
        /// <summary>Failure notes first, then the reasons a person must decide.</summary>
        public List<string> Notes = new List<string>();

        // ── Spec-based summary only (SpecBorang, Bim010Summary.SpecBased) ──
        /// <summary>Pane issues behind the failed spec checks of this row.</summary>
        public List<string> FailIssueIds = new List<string>();
        /// <summary>Pane issues behind what a person decides on this row.</summary>
        public List<string> ManualIssueIds = new List<string>();
        /// <summary>The part of <see cref="Notes"/> that a person must decide.</summary>
        public List<string> ManualNotes = new List<string>();
        /// <summary>"Not yet checked by the scan: A1.4, A1.5." or "" (also the last of <see cref="Notes"/>).</summary>
        public string NotCheckedNote = "";
    }

    public sealed class Bim010Summary
    {
        public List<Bim010SectionScore> Sections = new List<Bim010SectionScore>();
        /// <summary>Spec-based only (AI-474): AI checks the run could not judge (not passed), the reason
        /// (backend | mixed | nothing | rule), and AI checks only partly covered (not passed).</summary>
        public int Unjudged, Partial;
        public string UnjudgedReason = "";
        public Dictionary<string, Bim010RowMark> Rows = new Dictionary<string, Bim010RowMark>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Backend checks with no Borang row ("category · rule"), listed so
        /// they are never silently dropped.</summary>
        public List<string> Unmapped = new List<string>();
        /// <summary>Built from the JKR Model Audit spec report (SpecBorang), not from
        /// BIM010 cells: section counts are the Results screen's, and the rows carry
        /// the issue ids behind their marks.</summary>
        public bool SpecBased;

        /// <summary>See <see cref="Bim010SectionScore.Pass"/>: cells, or (spec-based) checks passed.</summary>
        public int Pass => Sections.Sum(s => s.Pass);
        /// <summary>Cells that fail, or (spec-based) checks not passed.</summary>
        public int Fail => Sections.Sum(s => s.Fail);
        public int Pending => Sections.Sum(s => s.Pending);
        public int AiTotal => Pass + Fail;
        /// <summary>Headline: AI-verified passing cells / AI-verified cells; spec-based: checks passed / checks that apply.</summary>
        public int? Pct => AiTotal == 0 ? (int?)null : (int)Math.Round(Pass * 100.0 / AiTotal, MidpointRounding.AwayFromZero);

        public Bim010SectionScore Section(string s) => Sections.FirstOrDefault(x => x.Section == s);
    }

    public static class AuditSummary
    {
        /// <summary>not_verifiable reasons that mean "nothing of this kind in the
        /// model": not applicable — no cell, no issue (an absent family category
        /// is not a manual review).</summary>
        public static readonly HashSet<string> NotApplicableReasons = new HashSet<string>(StringComparer.Ordinal)
        {
            "category_not_present", "no_types_to_check", "zero_placed_instances",
        };

        public static bool IsHeading(AuditRecord r) =>
            r.Row.RowRef.EndsWith(".0", StringComparison.Ordinal)
            || (r.Evidence != null && r.Evidence.ContainsKey("section_header"));

        public static bool IsNotApplicable(AuditRecord r) =>
            r.Compliance == "not_verifiable" && r.Evidence != null
            && r.Evidence.TryGetValue("not_verifiable_reason", out var why)
            && why is string s && NotApplicableReasons.Contains(s);

        /// <summary>fill_audit compliance → verdict: yes → Pass, no → Fail,
        /// anything else → Manual (never a guessed pass).</summary>
        public static Bim010Verdict VerdictOf(string compliance) =>
            compliance == "yes" ? Bim010Verdict.Pass
            : compliance == "no" ? Bim010Verdict.Fail
            : Bim010Verdict.Manual;

        /// <summary>One cell per scored fill_audit record. Headings and
        /// not-applicable rows give none. Section D records are the Naming column
        /// (the family-category checker tests type naming).</summary>
        public static List<Bim010Cell> CellsFromRecords(IEnumerable<AuditRecord> records)
        {
            var cells = new List<Bim010Cell>();
            foreach (var r in records ?? Enumerable.Empty<AuditRecord>())
            {
                if (r?.Row == null || IsHeading(r) || IsNotApplicable(r)) continue;
                var key = Bim010Form.KeyOf(r.Row.Section, r.Row.RowRef);
                if (Bim010Form.Find(key) == null) continue;
                cells.Add(new Bim010Cell
                {
                    Key = key,
                    Column = r.Row.Section == "D" ? Bim010Column.Naming : Bim010Column.None,
                    Verdict = VerdictOf(r.Compliance),
                    Source = "audit",
                    Title = r.Row.Description,
                    Note = r.Remark ?? "",
                });
            }
            return cells;
        }

        public static Bim010Summary Build(IEnumerable<Bim010Cell> cells, IEnumerable<string> unmapped = null)
        {
            var list = (cells ?? Enumerable.Empty<Bim010Cell>()).Where(c => c != null && Bim010Form.Find(c.Key) != null).ToList();
            var summary = new Bim010Summary
            {
                Unmapped = (unmapped ?? Enumerable.Empty<string>()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
            };

            foreach (var s in Bim010Form.Sections)
            {
                var inSec = list.Where(c => c.Section == s).ToList();
                summary.Sections.Add(new Bim010SectionScore
                {
                    Section = s,
                    Pass = inSec.Count(c => c.Verdict == Bim010Verdict.Pass),
                    Fail = inSec.Count(c => c.Verdict == Bim010Verdict.Fail),
                    Pending = inSec.Count(c => c.Verdict == Bim010Verdict.Manual),
                });
            }

            foreach (var row in Bim010Form.ScoredRows)
            {
                var rc = list.Where(c => string.Equals(c.Key, row.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var mark = new Bim010RowMark { Key = row.Key, Mark = MarkOf(rc) };
                if (row.Section == "D")
                    foreach (var col in Bim010Column.All)
                        mark.Columns[col] = MarkOf(rc.Where(c => c.Column == col));
                mark.Notes.AddRange(rc.Where(c => c.Verdict == Bim010Verdict.Fail).Select(NoteOf).Where(n => n.Length > 0).Distinct());
                mark.Notes.AddRange(rc.Where(c => c.Verdict == Bim010Verdict.Manual).Select(NoteOf).Where(n => n.Length > 0).Distinct());
                summary.Rows[row.Key] = mark;
            }
            return summary;
        }

        /// <summary>Any Fail → "n"; else any Manual → "m"; else any Pass → "y"; else "".</summary>
        public static string MarkOf(IEnumerable<Bim010Cell> cells)
        {
            var l = cells.ToList();
            if (l.Any(c => c.Verdict == Bim010Verdict.Fail)) return "n";
            if (l.Any(c => c.Verdict == Bim010Verdict.Manual)) return "m";
            return l.Count > 0 ? "y" : "";
        }

        private static string NoteOf(Bim010Cell c) =>
            !string.IsNullOrWhiteSpace(c.Note) ? c.Note.Trim() : (c.Title ?? "").Trim();
    }

    /// <summary>
    /// Backend v2 check → BIM010 row (and Section D column). The backend sends
    /// project-level checks under a JKR category (Project Naming, Project Base
    /// Point, Project Information, Levels, Grids) and element checks under the
    /// element's Revit category, with the rule text saying what was tested.
    /// Returns null for checks with no Borang row (e.g. a Revit category that is
    /// not in Section D); the caller lists those as unmapped.
    /// </summary>
    public static class Bim010Mapping
    {
        /// <summary>Revit category name → Section D label as printed.</summary>
        private static readonly Dictionary<string, string> DAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Floors"] = "Floors", ["Walls"] = "Walls", ["Ceilings"] = "Ceilings", ["Roofs"] = "Roofs",
            ["Stairs"] = "Stairs", ["Railings"] = "Railings", ["Top Rails"] = "Railings", ["Handrails"] = "Railings",
            ["Ramps"] = "Ramps", ["Rooms"] = "Room", ["Room"] = "Room",
            ["Curtain Systems"] = "Curtain Systems", ["Curtain Panels"] = "Curtain Systems", ["Curtain Wall Mullions"] = "Curtain Systems",
            ["Doors"] = "Doors", ["Windows"] = "Windows", ["Casework"] = "Caseworks", ["Caseworks"] = "Caseworks",
            ["Furniture"] = "Furniture", ["Plumbing Fixtures"] = "Plumbing Fixtures", ["Furniture Systems"] = "Furniture Systems",
            ["Specialty Equipment"] = "Specialty Equipment", ["Generic Models"] = "Generic Models",
            ["Columns"] = "Columns", ["Parking"] = "Parking", ["Pipes"] = "Pipes",
            ["Structural Columns"] = "Structural Column", ["Structural Column"] = "Structural Column",
            ["Topography"] = "Toposurface", ["Toposurface"] = "Toposurface", ["Toposolid"] = "Toposurface",
            ["Mass"] = "Mass",
        };

        /// <summary>Section D key for a Revit category ("Doors" → "D10"), or null.</summary>
        public static string DKeyForRevitCategory(string revitCategory) =>
            !string.IsNullOrWhiteSpace(revitCategory) && DAliases.TryGetValue(revitCategory.Trim(), out var label)
                ? Bim010Form.DKeyFor(label) : null;

        /// <param name="category">The check's JKR category (tier category) or Revit category.</param>
        /// <param name="revitCategory">The element's Revit category when known.</param>
        public static (string Key, string Column)? Map(string category, string revitCategory, string rule, string domain)
        {
            var cat = (category ?? "").Trim();
            var r = (rule ?? "").ToLowerInvariant();

            // Rule wording that names a Borang row outright wins over the category.
            if (r.Contains("link")) return ("C5.1", "");
            if (r.Contains("federation")) return ("C5.1", "");
            if (r.Contains("template")) return ("C1.1", "");

            switch (cat)
            {
                case "Project Naming": return ("A2", "");
                case "Project Base Point": return ("A4", "");
                case "Grids":
                case "Levels": return ("A5", "");
                case "Project Information": return ("B1", "");
            }

            if (r.Contains("shared parameters loaded") || r.Contains("parameter rules defined")
                || r.Contains("bpep") || r.Contains("compliance target") || r.Contains("phase requirements"))
                return ("B1", "");

            var dKey = DKeyForRevitCategory(revitCategory) ?? DKeyForRevitCategory(cat);
            if (dKey != null) return (dKey, ColumnFor(r));
            return null;
        }

        /// <summary>Section D column from the rule wording: naming rules → Naming;
        /// geometry → Design geometry; overlap/duplicate → Quality; everything
        /// else (parameters, codes, values) → Information.</summary>
        public static string ColumnFor(string rule)
        {
            var r = (rule ?? "").ToLowerInvariant();
            if (r.Contains("geometry") || r.Contains("dimension") || r.Contains("size")) return Bim010Column.Geometry;
            if (r.Contains("overlap") || r.Contains("duplicate") || r.Contains("integrity")) return Bim010Column.Quality;
            if (r.Contains("name") || r.Contains("naming") || r.Contains("subtype") || r.Contains("prefix")
                || r.Contains("code in name") || r.Contains("category code")) return Bim010Column.Naming;
            return Bim010Column.Information;
        }
    }
}
