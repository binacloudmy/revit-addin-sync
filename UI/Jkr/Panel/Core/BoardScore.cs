using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// AI-479 (de-dupe): one problem, one row. A2.1 (file name), A4.1 (base point) and
    /// B1.1 (Project Information) are answered by up to three sources at once: the
    /// backend's legacy rows, the BIM010 checker rows and the add-in's spec evaluator.
    /// The board de-dupes by issue id only, and each source has its own id, so the same
    /// problem showed up to three times, with different tiers. This keeps the rows of
    /// ONE source per such check and gives them ONE tier. WPF-free: the host maps its
    /// issues to <see cref="Row"/> and applies the answer.
    /// </summary>
    public static class SpecDedupe
    {
        /// <summary>Checks that are one problem however many sources answer them.</summary>
        public static readonly IReadOnlyList<string> OneProblemChecks = new[] { "A2.1", "A4.1", "B1.1" };

        /// <summary>The source of a row. Lower wins: the spec evaluator (the one the score and the
        /// Borang follow), then the backend's rows, then the BIM010 checker's.</summary>
        public const int FromModel = 0, FromBackend = 1, FromAudit = 2;

        /// <summary>One board row, as far as the de-dupe needs it.</summary>
        public sealed class Row
        {
            /// <summary>The spec check the row answers ("A2.1"), or "" (a family with no spec id).</summary>
            public string CheckId = "";
            /// <summary><see cref="FromModel"/>, <see cref="FromBackend"/> or <see cref="FromAudit"/>.</summary>
            public int Source;
            /// <summary>Board tier: 0 High, 1 Medium, 2 Low (lower is more severe).</summary>
            public int Tier = 1;
            /// <summary>The row has a fix or a picker: it is never dropped, whatever the source.</summary>
            public bool Fixable;
            /// <summary>AI-506: issue id (a merge keeps the first row of an id) and the caller's object.</summary>
            public string Id = "";
            public object Tag;
            /// <summary>AI-506 tally: <see cref="ScanBoard.Open"/> ... <see cref="ScanBoard.Resolved"/>; <see cref="Extra"/> = no spec id; <see cref="FixKey"/> = one write Fix all makes (null = not in Fix all).</summary>
            public int Bucket;
            public bool Extra;
            public string FixKey;
            /// <summary>AI-506 (Db.1 only). On a backend / checker row: the one parameter it names
            /// ("Kod_Komponen_jkr_stt") and the one element it sits on. Empty / 0 = not a parameter row.</summary>
            public string Param = "";
            public long ElementId;
            /// <summary>AI-506 (Db.1 only). On a spec row: every parameter it reports and every element it
            /// covers. A spec row covers a type, so one row stands for many per-element backend rows.</summary>
            public IReadOnlyCollection<string> Params = new string[0];
            public IReadOnlyCollection<long> ElementIds = new long[0];
        }

        /// <summary>The spec check whose spec rows list parameters per type (AI-506): a backend
        /// "Missing Type Parameter: X" row for an element of that type says the same thing.</summary>
        public const string ParameterCheck = "Db.1";

        /// <summary>"Kod_Komponen_jkr_stt" and "Kod Komponen Jkr Stt" are the same parameter.</summary>
        public static string ParamKey(string name) =>
            string.Join("_", (name ?? "").Trim().ToLowerInvariant().Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>The parameter a backend rule names: "Missing Type Parameter: X" (its raw rule is
        /// "missing type parameter: X"), else the row's Required text. "" when none.</summary>
        public static string ParamOfRule(string rawRule, string required)
        {
            var r = (rawRule ?? "").Trim();
            var k = r.IndexOf(" parameter:", StringComparison.OrdinalIgnoreCase);
            var low = r.ToLowerInvariant();
            if (k > 0 && (low.StartsWith("missing ") || low.StartsWith("empty ")))
                return r.Substring(k + " parameter:".Length).Trim();
            return (required ?? "").Trim();
        }

        /// <summary>The parameters a Db.1 spec row lists in its Actual text: "kosong: A, B; tiada: C" /
        /// "empty: A; not on the type: C".</summary>
        public static IReadOnlyList<string> ParamsOfActual(string actual)
        {
            var list = new List<string>();
            foreach (var part in (actual ?? "").Split(';'))
            {
                var c = part.IndexOf(':');
                if (c < 0) continue;
                foreach (var n in part.Substring(c + 1).Split(','))
                {
                    var k = ParamKey(n);
                    if (k.Length > 0 && !list.Contains(k)) list.Add(k);
                }
            }
            return list;
        }

        public sealed class Outcome
        {
            /// <summary>Per input row: keep it on the board.</summary>
            public bool[] Keep = new bool[0];
            /// <summary>Per input row: the tier to show (the most severe of its check's rows).</summary>
            public int[] Tier = new int[0];
            public int Dropped => Keep.Count(k => !k);
        }

        /// <summary>
        /// AI-506: a Db.1 spec row (one per type) lists the parameters that type lacks or leaves empty,
        /// and the backend reports each of them again per element ("Missing Type Parameter: X"). Drop
        /// the backend / checker row when a spec row names the same parameter on the same element.
        /// A fixable row stays (AI-479 rule); a row no spec row covers stays (never hide a problem).
        /// Order of the input does not change the result.
        /// </summary>
        private static void DropCoveredParameterRows(IReadOnlyList<Row> rows, Outcome o)
        {
            var covered = new HashSet<(string, long)>();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || r.Source != FromModel || !IsCheck(r, ParameterCheck)) continue;
                foreach (var p in r.Params ?? new string[0])
                    foreach (var e in r.ElementIds ?? new long[0])
                        covered.Add((ParamKey(p), e));
            }
            if (covered.Count == 0) return;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || r.Source == FromModel || r.Fixable || !IsCheck(r, ParameterCheck)) continue;
                if (r.ElementId > 0 && !string.IsNullOrEmpty(r.Param) && covered.Contains((ParamKey(r.Param), r.ElementId)))
                    o.Keep[i] = false;
            }
        }

        private static bool IsCheck(Row r, string id) => string.Equals((r.CheckId ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase);

        public static bool IsOneProblem(string checkId) =>
            !string.IsNullOrEmpty(checkId) && OneProblemChecks.Contains(checkId.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per check in <see cref="OneProblemChecks"/> with rows from several sources: keep
        /// the rows of the best source (<see cref="FromModel"/> first) and every fixable
        /// row; drop the rest. All kept rows of the check take the most severe tier of
        /// the check's rows, dropped ones included, so a Must Fix never turns ignorable
        /// because a duplicate is gone. Rows of any other check, or with none, pass through.
        /// </summary>
        public static Outcome Resolve(IReadOnlyList<Row> rows)
        {
            rows = rows ?? new Row[0];
            var o = new Outcome { Keep = Enumerable.Repeat(true, rows.Count).ToArray(), Tier = new int[rows.Count] };
            for (var i = 0; i < rows.Count; i++) o.Tier[i] = rows[i]?.Tier ?? 1;

            foreach (var id in OneProblemChecks)
            {
                var group = Enumerable.Range(0, rows.Count)
                    .Where(i => rows[i] != null && string.Equals((rows[i].CheckId ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (group.Count == 0) continue;
                var best = group.Min(i => rows[i].Source);
                var tier = group.Min(i => rows[i].Tier);
                foreach (var i in group)
                {
                    o.Tier[i] = tier;
                    if (rows[i].Source != best && !rows[i].Fixable) o.Keep[i] = false;
                }
            }
            DropCoveredParameterRows(rows, o);
            return o;
        }
    }

    /// <summary>
    /// AI-479 (one number): what a Fix all moved, on the board and on the score. The board
    /// counts rows, the score counts checks (cells), and the families with no spec id
    /// (Grid, Level, JKR code, element-less rows; Kod_Jenis, Sistem, Sub_Sistem and Bidang map to Db.1 and are in the %) are "extra": fixable
    /// rows on the board outside the %. A fix there moves the board and never the score;
    /// this says so, instead of three numbers that disagree. WPF-free.
    /// </summary>
    public static class FixImpact
    {
        /// <summary>The numbers of one moment: the board's open rows and the score.</summary>
        public sealed class Snapshot
        {
            /// <summary>Open rows on the board (AI rows: the number the Open tab shows).</summary>
            public int Open;
            /// <summary>Of <see cref="Open"/>, the "extra" rows (no spec id).</summary>
            public int OpenExtra;
            /// <summary>Checks passed / checks in the % (<see cref="SpecChecklist.Score(SpecReport, string)"/>); Total 0 = no score.</summary>
            public int Passed;
            public int Total;
            public int? Pct;
        }

        public static Snapshot Take(int open, int openExtra, SpecReport spec)
        {
            var s = new Snapshot { Open = Math.Max(0, open), OpenExtra = Math.Max(0, Math.Min(openExtra, open)) };
            if (spec == null) return s;
            var sc = SpecChecklist.Score(spec);
            s.Passed = sc.Passed;
            s.Total = sc.Total;
            s.Pct = SpecChecklist.Pct(spec);
            return s;
        }

        /// <summary>Rows that left the board (never negative: a re-scan can find new rows).</summary>
        public static int BoardMoved(Snapshot before, Snapshot after) => Math.Max(0, before.Open - after.Open);

        /// <summary>Of the rows that left the board, the extra ones (outside the %).</summary>
        public static int ExtraMoved(Snapshot before, Snapshot after) => Math.Max(0, Math.Min(BoardMoved(before, after), before.OpenExtra - after.OpenExtra));

        /// <summary>Checks that moved onto pass (negative when the re-scan failed some).</summary>
        public static int ScoreMoved(Snapshot before, Snapshot after) => after.Passed - before.Passed;

        /// <summary>
        /// "Board 12 → 5 open (7 fixed, 4 of them extra, outside the %) · Score 61% → 64% (+2 checks)".
        /// With no score (no spec report) only the board part; with an unchanged score and extra
        /// rows moved, says why the % did not move.
        /// </summary>
        public static string Text(Snapshot before, Snapshot after)
        {
            if (before == null || after == null) return "";
            var moved = BoardMoved(before, after);
            var extra = ExtraMoved(before, after);
            var board = "Board " + before.Open + " → " + after.Open + " open";
            if (moved > 0)
            {
                board += " (" + moved + " fixed";
                if (extra > 0)
                    board += (", " + extra + " of them extra, outside the %");
                board += ")";
            }
            if (before.Total <= 0 || after.Total <= 0 || !before.Pct.HasValue || !after.Pct.HasValue) return board;

            var d = ScoreMoved(before, after);
            var score = "Score " + before.Pct + "%" + (before.Pct == after.Pct ? "" : " → " + after.Pct + "%");
            if (d != 0)
                score += " (" + (d > 0 ? "+" : "") + d + (Math.Abs(d) == 1 ? " check" : " checks") + ")";
            else if (extra > 0)
                score += " (unchanged: extra fixes are not in the %)";
            else if (moved > 0)
                score += " (unchanged: those checks do not pass yet)";
            return board + " · " + score;
        }
    }
}

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// AI-506: the scan's merge and the numbers shown from it, as pure code over <see cref="SpecDedupe.Row"/>.
    /// The same input gives the same rows and the same numbers on every scan, whatever the order inside a
    /// source, and the board, Fix all and the export all read ONE <see cref="Tally"/>.
    /// </summary>
    public static class ScanBoard
    {
        public const int Open = 0, Manual = 1, Ignored = 2, Resolved = 3;

        /// <summary>Backend rows, then checker rows, then spec rows; an id already taken is skipped; then one row
        /// per problem (<see cref="SpecDedupe"/>). Each kept row has the tier to show.</summary>
        public static List<SpecDedupe.Row> Merge(IEnumerable<SpecDedupe.Row> backend, IEnumerable<SpecDedupe.Row> audit, IEnumerable<SpecDedupe.Row> model)
        {
            var merged = new List<SpecDedupe.Row>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            // The backend's own rows are all kept (as before AI-506); the other two sources skip a known id.
            foreach (var r in (backend ?? new SpecDedupe.Row[0]).Where(x => x != null)) { merged.Add(r); known.Add(r.Id ?? ""); }
            foreach (var r in (audit ?? new SpecDedupe.Row[0]).Where(x => x != null)) if (known.Add(r.Id ?? "")) merged.Add(r);
            foreach (var r in (model ?? new SpecDedupe.Row[0]).Where(x => x != null && !string.IsNullOrEmpty(x.Id))) if (known.Add(r.Id)) merged.Add(r);
            var o = SpecDedupe.Resolve(merged);
            var kept = new List<SpecDedupe.Row>();
            for (var i = 0; i < merged.Count; i++)
            {
                if (!o.Keep[i]) continue;
                merged[i].Tier = o.Tier[i];
                kept.Add(merged[i]);
            }
            return kept;
        }

        /// <summary>
        /// Every number from one list, so they reconcile: <c>Total = Open + Manual + Ignored + Resolved</c>,
        /// and Total is what an export writes with both options on. <see cref="Extra"/> and
        /// <see cref="Fixable"/> are parts of Open, never added to it.
        /// </summary>
        public sealed class Tally
        {
            /// <summary>The "Isu" number: AI-verified rows still open.</summary>
            public int Open;
            /// <summary>Rows that need a person or that someone marked manual (Manual tab). In the export, not in Open.</summary>
            public int Manual;
            public int Ignored;
            public int Resolved;
            /// <summary>Of Open: rows with no spec id, outside the %.</summary>
            public int Extra;
            /// <summary>Of Open: the changes Fix all makes (rows that ask for the same write are one change).</summary>
            public int Fixable;
            public int Total => Open + Manual + Ignored + Resolved;
        }

        public static Tally Count(IEnumerable<SpecDedupe.Row> rows)
        {
            var t = new Tally();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows ?? new SpecDedupe.Row[0])
            {
                if (r == null) continue;
                switch (r.Bucket)
                {
                    case Open:
                        t.Open++;
                        if (r.Extra) t.Extra++;
                        if (r.FixKey != null && keys.Add(r.FixKey)) t.Fixable++;
                        break;
                    case Manual: t.Manual++; break;
                    case Ignored: t.Ignored++; break;
                    default: t.Resolved++; break;
                }
            }
            return t;
        }

        /// <summary>"943 open + 54 manual review = 997 in the file": what an export writes, by group.</summary>
        public static string Line(Tally t)
        {
            if (t == null) return "";
            var parts = new List<string> { t.Open + " open" };
            if (t.Manual > 0) parts.Add(t.Manual + " manual review");
            if (t.Ignored > 0) parts.Add(t.Ignored + " ignored");
            if (t.Resolved > 0) parts.Add(t.Resolved + " resolved");
            return string.Join(" + ", parts) + " = " + t.Total + " in the file"
                   + (t.Extra > 0 ? " (" + t.Extra + " open are extra, outside the %)" : "");
        }
    }
}
