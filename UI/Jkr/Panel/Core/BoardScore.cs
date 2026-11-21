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
        }

        public sealed class Outcome
        {
            /// <summary>Per input row: keep it on the board.</summary>
            public bool[] Keep = new bool[0];
            /// <summary>Per input row: the tier to show (the most severe of its check's rows).</summary>
            public int[] Tier = new int[0];
            public int Dropped => Keep.Count(k => !k);
        }

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
            return o;
        }
    }

    /// <summary>
    /// AI-479 (one number): what a Fix all moved, on the board and on the score. The board
    /// counts rows, the score counts checks (cells), and the families with no spec id
    /// (Kod_Jenis, Sistem, Sub_Sistem, Bidang, JKR code, Grid, Level) are "extra": fixable
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
        public static string Text(Snapshot before, Snapshot after, string lang = null)
        {
            if (before == null || after == null) return "";
            var en = PaneText.IsEn(lang);
            string L(string ms, string e) => en ? e : ms;

            var moved = BoardMoved(before, after);
            var extra = ExtraMoved(before, after);
            var board = L("Papan ", "Board ") + before.Open + " → " + after.Open + L(" terbuka", " open");
            if (moved > 0)
            {
                board += " (" + moved + L(" dibaiki", " fixed");
                if (extra > 0)
                    board += L(", " + extra + " daripadanya tambahan, di luar %", ", " + extra + " of them extra, outside the %");
                board += ")";
            }
            if (before.Total <= 0 || after.Total <= 0 || !before.Pct.HasValue || !after.Pct.HasValue) return board;

            var d = ScoreMoved(before, after);
            var score = L("Skor ", "Score ") + before.Pct + "%" + (before.Pct == after.Pct ? "" : " → " + after.Pct + "%");
            if (d != 0)
                score += " (" + (d > 0 ? "+" : "") + d + L(" semakan", Math.Abs(d) == 1 ? " check" : " checks") + ")";
            else if (extra > 0)
                score += L(" (tiada perubahan: pembaikan tambahan tidak dikira dalam %)", " (unchanged: extra fixes are not in the %)");
            else if (moved > 0)
                score += L(" (tiada perubahan: semakan itu belum lulus sepenuhnya)", " (unchanged: those checks do not pass yet)");
            return board + " · " + score;
        }
    }
}
