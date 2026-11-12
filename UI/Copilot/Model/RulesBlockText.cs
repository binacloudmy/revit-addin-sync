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

        /// <summary>The label of the Export button; the long text (which table) is only its tooltip.</summary>
        public static string ExportLabel(bool ms) => ms ? "Eksport" : "Export";
    }
}
