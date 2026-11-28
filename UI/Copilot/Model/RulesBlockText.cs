#nullable enable
// The "Rules checked" block (a rule set's result: model-health, doc-hygiene, ...): which rules failed, which could not
// be verified, how many passed. Pure (no WPF), so a test pins every rule of it.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One rule of a check table.</summary>
    public sealed class RuleRow
    {
        public string Id = "";
        /// <summary>What the rule is in words (the table's cite), whole: the block wraps it, never cuts it.</summary>
        public string Name = "";
        public string Severity = "";
        public int Pass, Fail, NotVerified;
        public string Reason = "";
        /// <summary>The handle that selects the failing elements, or null.</summary>
        public string? Handle;
    }

    public static class RulesBlockText
    {
        private static int N(IReadOnlyList<string> row, int i) =>
            i < row.Count && int.TryParse(row[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static string S(IReadOnlyList<string> row, int i) => i < row.Count ? (row[i] ?? "").Trim() : "";

        /// <summary>The rules of the check tables, in order (columns: Rule, Cite, Severity, Pass, Fail, Not verified, Why not verified).</summary>
        public static IReadOnlyList<RuleRow> Rows(IEnumerable<ResultTable>? tables)
        {
            var rows = new List<RuleRow>();
            foreach (var t in tables ?? Enumerable.Empty<ResultTable>())
            {
                if (t == null || t.Kind != ResultTable.CheckKind) continue;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var r = t.Rows[i];
                    rows.Add(new RuleRow
                    {
                        Id = S(r, 0), Name = S(r, 1), Severity = S(r, 2), Pass = N(r, 3), Fail = N(r, 4), NotVerified = N(r, 5),
                        Reason = S(r, 6), Handle = t.HandleOf(i),
                    });
                }
            }
            return rows;
        }

        /// <summary>A rule that needs a look: it failed on some element or could not be verified.</summary>
        public static bool IsOpen(RuleRow r) => r.Fail > 0 || r.NotVerified > 0;

        public static int Failed(IEnumerable<RuleRow> rows) => rows.Count(r => r.Fail > 0);
        public static int NotVerified(IEnumerable<RuleRow> rows) => rows.Count(r => r.Fail == 0 && r.NotVerified > 0);
        public static int Passed(IEnumerable<RuleRow> rows) => rows.Count(r => !IsOpen(r));

        /// <summary>"Rules checked · 4 failed", or "· 2 not verified", or "· all passed".</summary>
        public static string Title(IEnumerable<RuleRow> rows, bool ms)
        {
            var list = rows.ToList();
            var failed = Failed(list);
            var unverified = NotVerified(list);
            var head = ms ? "Peraturan disemak" : "Rules checked";
            if (failed > 0) return head + " · " + failed.ToString(CultureInfo.InvariantCulture) + (ms ? " gagal" : " failed");
            if (unverified > 0) return head + " · " + unverified.ToString(CultureInfo.InvariantCulture) + (ms ? " tidak disahkan" : " not verified");
            return head + (ms ? " · semua lulus" : " · all passed");
        }

        /// <summary>The one line the passed rules fold into: "3 rules passed".</summary>
        public static string PassedLine(int n, bool ms) =>
            ms ? n.ToString(CultureInfo.InvariantCulture) + " peraturan lulus"
               : n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " rule passed" : " rules passed");

        /// <summary>The count chip of a failing rule: "11 fail".</summary>
        public static string FailChip(int n, bool ms) => n.ToString(CultureInfo.InvariantCulture) + (ms ? " gagal" : " fail");

        public static string NotVerifiedChip(int n, bool ms) => n.ToString(CultureInfo.InvariantCulture) + (ms ? " tidak disahkan" : " not verified");

        /// <summary>The chip is red for an error-severity rule, amber for the rest.</summary>
        public static bool IsError(RuleRow r) => string.Equals(r.Severity, "error", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>A rule's name for its row: the first clause, no citation, at most <paramref name="max"/> characters (cut at a
        /// word with "…"). The catalog text is "Revit Review Warnings (Document.GetWarnings, ...); BINA model-health practice" or
        /// "BIM: Piawaian JKR (Edisi Kedua 2021) ... pdf p.98": the row shows "Revit Review Warnings" / "BIM: Piawaian JKR", the
        /// whole text is its tooltip.</summary>
        public static string ShortName(string? name, int max = 60)
        {
            var text = (name ?? "").Trim();
            if (text.Length == 0) return "";
            var cut = text.Length;
            foreach (var mark in new[] { " (", ";", " — ", " – ", " ... ", " …" })
            {
                var i = text.IndexOf(mark, System.StringComparison.Ordinal);
                if (i > 0 && i < cut) cut = i;
            }
            var first = text.Substring(0, cut).Trim().TrimEnd(',', ':', '.');
            if (first.Length == 0) first = text;
            if (first.Length <= max) return first;
            var space = first.LastIndexOf(' ', max - 1);
            return first.Substring(0, space > max / 2 ? space : max - 1).TrimEnd(',', ' ') + "…";
        }

        /// <summary>A check that only restates a rule the block already lists: its text starts with (or names, as a whole word)
        /// one of the rule ids, and it has no button of its own.</summary>
        public static bool RestatesRule(string? text, bool hasActions, IEnumerable<string>? ruleIds)
        {
            if (hasActions || string.IsNullOrWhiteSpace(text) || ruleIds == null) return false;
            foreach (var id in ruleIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(text!, @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(id.Trim()) + @"(?![A-Za-z0-9])",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The label of the Export button; the long text (which table) is only its tooltip.</summary>
        public static string ExportLabel(bool ms) => ms ? "Eksport" : "Export";
    }
}
