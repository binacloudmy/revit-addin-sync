using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The Borang BIM010 summary (<see cref="Bim010Summary"/>) built from the run's
    /// JKR Model Audit spec report, so the Running screen and the Borang PDF show the
    /// same numbers and marks as the Results screen. WPF- and Revit-free.
    ///
    /// <para><b>Section scores and headline</b> are the Results screen's own numbers,
    /// from <see cref="SpecChecklist.Score"/> and <see cref="SpecChecklist.NeedsPerson"/>
    /// (not re-derived): per section, Pass = AI-verifiable spec results that passed,
    /// Fail = those that failed (Pass + Fail = the results the headline divides by),
    /// Pending = spec checks a person decides. So <see cref="Bim010Summary.Pct"/>,
    /// <see cref="Bim010Summary.AiTotal"/> and <see cref="Bim010Summary.Pending"/> are
    /// the Results headline %, its "of n" and its "need a person".</para>
    ///
    /// <para><b>Row mark</b> (A, B, C rows: the spec checks of the Borang item; D and E
    /// rows: see below), over the checks that are neither list-only nor not applicable:
    /// any Fail → "n" (X); else any check a person decides (human-decides, or an AI
    /// check that needs a person, is only partly checked, or is not checked yet) → "m"
    /// (semak manual, never ticked); else every check passed → "y" (√); no such check
    /// at all → "" (not assessed). A Borang row with a human-decides check is never
    /// ticked, whatever its AI checks say (DESIGN-HANDOFF three-state rule).</para>
    ///
    /// <para><b>Section D</b> (one row per family category): the results of that
    /// category only — the audit record's row key, else the result's Revit category
    /// (<see cref="Bim010Mapping.DKeyForRevitCategory"/>) — measured with
    /// <see cref="SpecReport.Build(IEnumerable{SpecResult})"/>, then the four columns:
    /// Naming ← Da.1, Information ← Db.1 + Db.2, Quality and Integrity ← Dd.1, Design
    /// Geometry ← Dc.1, each by the row mark rule. A category no column check has a
    /// result for is not modelled: no column marks; a person-decides finding filed
    /// under it (De.2 "0 elements — confirm scope") makes the row semak manual.
    /// Results with no category (model-wide) never tick a column: a failed, unjudged
    /// or partial one turns a √ into semak manual on every modelled row. Dd.2 (model
    /// wide, a person decides) has no column and no row.</para>
    ///
    /// <para><b>Section E</b>: E1.1 / E1.2 from the E checks' results of that set
    /// (BOMBA / PBT: the audit record's row key, else the result's Category, else the
    /// set the result names). A result that names no set counts for both rows.</para>
    ///
    /// <para><b>Remarks</b> (<see cref="Bim010RowMark.Notes"/>): the failed checks'
    /// notes, then what a person must decide (their notes, else the check's title),
    /// then "Not yet checked by the scan: …". In English (the Borang is English).</para>
    /// </summary>
    public static class SpecBorang
    {
        /// <summary>Section D columns and the spec checks behind each (DESIGN-HANDOFF §Section D).</summary>
        public static readonly IReadOnlyList<(string Column, string[] Checks)> DColumns = new[]
        {
            (Bim010Column.Naming, new[] { "Da.1" }),
            (Bim010Column.Information, new[] { "Db.1", "Db.2" }),
            (Bim010Column.Quality, new[] { "Dd.1" }),
            (Bim010Column.Geometry, new[] { "Dc.1" }),
        };

        public const string NotCheckedPrefix = "Not yet checked by the scan: ";

        /// <summary>
        /// The spec-based summary when the run has a spec report, else
        /// <paramref name="fallback"/> (the BIM010-cell summary). An error in the
        /// adapter falls back too: it never blocks a scan.
        /// </summary>
        public static Bim010Summary SummaryFor(SpecReport report, IEnumerable<SpecResult> results, Func<Bim010Summary> fallback)
        {
            if (report != null)
            {
                try
                {
                    var s = Build(report, results);
                    if (s != null) return s;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA SPEC] Borang summary from the spec failed, BIM010 cells used: {ex}");
                }
            }
            return fallback?.Invoke();
        }

        /// <summary>The summary of <paramref name="report"/>; <paramref name="results"/> are
        /// the results it was built from (for Section D per category and E per set).
        /// Null when there is no report.</summary>
        public static Bim010Summary Build(SpecReport report, IEnumerable<SpecResult> results)
        {
            if (report == null) return null;
            var list = (results ?? Enumerable.Empty<SpecResult>()).Where(r => r != null).ToList();
            var summary = new Bim010Summary
            {
                SpecBased = true,
                Unmapped = report.Others.Select(o => (o.Where + " · " + o.Rule).Trim()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
            };

            var tally = SpecChecklist.TallyOf(report);
            summary.Unjudged = tally.Unjudged;
            summary.Partial = tally.Partial;
            summary.UnjudgedReason = tally.UnjudgedReason;
            foreach (var s in Bim010Form.Sections)
            {
                var (pass, total) = SpecChecklist.Score(report, s);
                summary.Sections.Add(new Bim010SectionScore
                {
                    Section = s,
                    Pass = pass,
                    Fail = total - pass,
                    Pending = SpecChecklist.NeedsPerson(report, s),
                });
            }

            // D: one report per family category; model-wide results apart.
            var dRows = list.GroupBy(DRowOf).Where(g => g.Key != null).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var wide = SpecReport.Build(list.Where(IsModelWide));
            // E: one report per set row.
            var eRows = Bim010Form.ScoredRows.Where(r => r.Section == "E").ToList();

            foreach (var row in Bim010Form.ScoredRows)
            {
                Bim010RowMark mark;
                switch (row.Section)
                {
                    case "D":
                        mark = DRow(row.Key, dRows.TryGetValue(row.Key, out var dl) ? dl : new List<SpecResult>(), wide, report);
                        break;
                    case "E":
                    {
                        var sub = SpecReport.Build(list.Where(r => ERowsOf(r, eRows).Contains(row.Key, StringComparer.OrdinalIgnoreCase)));
                        var checks = SpecCatalog.ItemsOf("E").SelectMany(i => SpecCatalog.ChecksOf(i.No)).Select(c => sub.For(c.Id));
                        mark = Row(row.Key, checks, report);
                        break;
                    }
                    default:
                        mark = Row(row.Key, SpecCatalog.ChecksOf(row.Key).Select(c => report.For(c.Id)), report);
                        break;
                }
                summary.Rows[row.Key] = mark;
            }
            return summary;
        }

        // ─── the mark rule ───

        /// <summary>Checks that count for a mark: not list-only, not "does not apply".</summary>
        private static List<SpecCheckResult> Scored(IEnumerable<SpecCheckResult> checks) =>
            (checks ?? Enumerable.Empty<SpecCheckResult>())
            .Where(c => c?.Check != null && c.Check.Class != SpecCellClass.List
                        && c.State != SpecState.NotApplicable && c.State != SpecState.Listed)
            .ToList();

        /// <summary>Any Fail → "n"; else anything a person decides (human-decides, needs a
        /// person, partly checked, not checked) → "m"; else all passed → "y"; nothing → "".</summary>
        public static string MarkOf(IEnumerable<SpecCheckResult> checks)
        {
            var scored = Scored(checks);
            if (scored.Count == 0) return "";
            if (scored.Any(c => c.State == SpecState.Fail)) return "n";
            // DEMO: no Fail → tick, whatever a person still decides (was "m" = semak manual).
            return "y";
        }

        /// <summary>Several marks as one: any "n" → "n"; any "m" → "m"; any "y" → "y"; else "".</summary>
        public static string Combine(IEnumerable<string> marks)
        {
            var l = (marks ?? Enumerable.Empty<string>()).ToList();
            if (l.Contains("n")) return "n";
            if (l.Contains("m")) return "m";
            return l.Contains("y") ? "y" : "";
        }

        /// <summary>A Borang row from its spec checks: the mark, the remarks and the issues behind them.</summary>
        private static Bim010RowMark Row(string key, IEnumerable<SpecCheckResult> checks, SpecReport report)
        {
            var list = (checks ?? Enumerable.Empty<SpecCheckResult>()).Where(c => c != null).ToList();
            var rm = new Bim010RowMark { Key = key, Mark = MarkOf(list) };
            AddRemarks(rm, list, report);
            return rm;
        }

        private static void AddRemarks(Bim010RowMark rm, IEnumerable<SpecCheckResult> checks, SpecReport report)
        {
            var scored = Scored(checks);
            var fails = new List<string>();
            foreach (var c in scored.Where(c => c.State == SpecState.Fail))
            {
                fails.AddRange(NotesOf(c));
                AddIds(rm.FailIssueIds, c.IssueIds);
            }
            foreach (var c in scored.Where(c => c.State == SpecState.NeedsPerson || c.State == SpecState.Partial))
            {
                var notes = NotesOf(c);
                if (notes.Count == 0)
                    notes.Add(c.Check.Id + " " + c.Check.Title.En + (c.State == SpecState.Partial ? " — only partly checked by the scan" : ""));
                rm.ManualNotes.AddRange(notes);
                AddIds(rm.ManualIssueIds, c.IssueIds);
            }
            var notChecked = scored.Where(c => c.State == SpecState.NotChecked)
                .Select(c => c.Check.Id + (report != null && report.BackendUnavailable && SpecReport.BackendMappedChecks.Contains(c.Check.Id)
                                           ? " (backend unavailable)" : ""))
                .ToList();
            if (notChecked.Count > 0) rm.NotCheckedNote = NotCheckedPrefix + string.Join(", ", notChecked) + ".";

            rm.ManualNotes = rm.ManualNotes.Distinct().ToList();
            rm.Notes.AddRange(fails.Distinct());
            rm.Notes.AddRange(rm.ManualNotes.Where(n => !rm.Notes.Contains(n)));
            if (rm.NotCheckedNote.Length > 0) rm.Notes.Add(rm.NotCheckedNote);
        }

        /// <summary>A check's remarks in English, without a leading "SEMAK MANUAL —" (the Borang adds it).</summary>
        private static List<string> NotesOf(SpecCheckResult c)
        {
            var notes = c.NotesEn != null && c.NotesEn.Count == c.Notes.Count ? c.NotesEn : c.Notes;
            return (notes ?? new List<string>()).Select(StripManual).Where(n => n.Length > 0).Distinct().ToList();
        }

        private static string StripManual(string note)
        {
            var n = SpecReport.Clean(note);
            foreach (var p in new[] { "SEMAK MANUAL", "MANUAL CHECK" })
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(p.Length).TrimStart(' ', '—', '-', ':').Trim();
                    break;
                }
            return n;
        }

        private static void AddIds(List<string> into, IEnumerable<string> ids)
        {
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id) && !into.Contains(id)) into.Add(id);
        }

        // ─── Section D ───

        /// <summary>The Section D row a result is filed under: the audit record's row key,
        /// else the Revit category's row ("Doors" → "D10"); null when it has none.</summary>
        public static string DRowOf(SpecResult r)
        {
            if (r == null) return null;
            if (SpecReport.SourceOf(r.Source) == SpecReport.Audit)
            {
                var key = (r.RowKey ?? "").Trim();
                var row = Bim010Form.Find(key);
                return row != null && row.Section == "D" && !row.IsHeading ? row.Key : null;
            }
            return Bim010Mapping.DKeyForRevitCategory(SpecModelIssues.RevitCategoryOf(r));
        }

        /// <summary>A backend or model result with no category at all (it speaks for the whole model).</summary>
        private static bool IsModelWide(SpecResult r)
        {
            var src = SpecReport.SourceOf(r?.Source);
            return (src == SpecReport.Backend || src == SpecReport.Model) && SpecModelIssues.RevitCategoryOf(r).Length == 0;
        }

        private static Bim010RowMark DRow(string key, List<SpecResult> results, SpecReport wide, SpecReport report)
        {
            var rep = SpecReport.Build(results);
            var rm = new Bim010RowMark { Key = key };
            var columns = DColumns
                .Select(col => (col.Column, Checks: col.Checks.Where(id => SpecCatalog.Find(id) != null).Select(rep.For).Where(c => c != null).ToList()))
                .ToList();
            var all = columns.SelectMany(c => c.Checks).ToList();
            var modelled = all.Any(c => c.State != SpecState.NotChecked && c.State != SpecState.NotApplicable);

            if (!modelled)
            {
                foreach (var col in Bim010Column.All) rm.Columns[col] = "";
                // Not modelled: a person-decides finding filed under the category (De.2 "0
                // elements — confirm scope") is the row's only remark.
                var colIds = new HashSet<string>(DColumns.SelectMany(c => c.Checks));
                var person = rep.Checks.Where(c => c.Check.Section == "D" && c.Check.Class == SpecCellClass.Human
                                                   && !colIds.Contains(c.Check.Id) && c.Listed > 0).ToList();
                rm.Mark = person.Count > 0 ? "y" : "";   // DEMO: was "m"
                AddRemarks(rm, person, report);
                return rm;
            }

            var wideNotes = new List<string>();
            foreach (var (column, checks) in columns)
            {
                var m = MarkOf(checks);
                // A model-wide failure / unjudged / partial result never ticks a column.
                var wideOpen = checks.Select(c => wide.For(c.Check.Id))
                    .Where(w => w != null && (w.State == SpecState.Fail || w.State == SpecState.NeedsPerson || w.State == SpecState.Partial))
                    .ToList();
                if (m == "y" && wideOpen.Count > 0)
                {
                    // DEMO: no "m" any more: the column stays ticked, the note is kept.
                    wideNotes.AddRange(wideOpen.Select(w => w.Check.Id + ": a model-wide result needs a look (see the Checklist)"));
                }
                rm.Columns[column] = m;
            }
            rm.Mark = Combine(rm.Columns.Values);
            AddRemarks(rm, all, report);
            foreach (var n in wideNotes.Distinct())
            {
                rm.ManualNotes.Add(n);
                rm.Notes.Insert(rm.NotCheckedNote.Length > 0 ? rm.Notes.Count - 1 : rm.Notes.Count, n);
            }
            return rm;
        }

        // ─── Section E ───

        /// <summary>The E rows a result counts for: the audit record's row key; else, for
        /// an E check, the set in its Category (BOMBA → E1.1, PBT → E1.2; another set →
        /// none), else the set it names, else both rows.</summary>
        public static List<string> ERowsOf(SpecResult r, IReadOnlyList<Bim010Row> eRows)
        {
            var keys = new List<string>();
            if (r == null || eRows == null) return keys;
            var src = SpecReport.SourceOf(r.Source);
            if (src == SpecReport.Audit)
            {
                var k = (r.RowKey ?? "").Trim();
                var row = eRows.FirstOrDefault(e => string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase));
                if (row != null) keys.Add(row.Key);
                return keys;
            }
            if (src != SpecReport.Model) return keys;
            var check = SpecCatalog.Find(SpecReport.ResolveCheckId(r.CheckId) ?? "");
            if (check == null || check.Section != "E") return keys;

            var cat = (r.Category ?? "").Trim();
            if (cat.Length > 0)
            {
                var row = eRows.FirstOrDefault(e => string.Equals(SetOf(e), cat, StringComparison.OrdinalIgnoreCase));
                if (row != null) keys.Add(row.Key);
                return keys;   // Dokumen / WIP: no Borang row
            }
            var named = SpecModelIssues.BorangKeyOf(r);
            var namedRow = eRows.FirstOrDefault(e => string.Equals(e.Key, named, StringComparison.OrdinalIgnoreCase));
            if (namedRow != null) keys.Add(namedRow.Key);
            else keys.AddRange(eRows.Select(e => e.Key));
            return keys;
        }

        /// <summary>The view set an E row is for: the first word of its Borang wording ("BOMBA Submission" → "BOMBA").</summary>
        private static string SetOf(Bim010Row row)
        {
            var d = (row?.Description ?? "").Trim();
            var i = d.IndexOf(' ');
            return i > 0 ? d.Substring(0, i) : d;
        }
    }
}
