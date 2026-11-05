#nullable enable
// The evidence strip under a reply, shortened (AI-269 item 1 and 3): one line
// for everything the turn read, the names behind "Details", one "not searched"
// line for the whole turn instead of one per read, one Export per table.
// Revit-free; every sentence is a template over EvidenceItem / ResultTable.
//
// Nothing here rewrites the model's reply. It only shortens text the add-in
// owns (the evidence lines) and never prints a query filter, a GUID or an
// element handle to the drafter.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class EvidenceSummary
    {
        private const string Sep = " · ";
        private static readonly Regex Guid = new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
        // "type ~ '(PTh300a) 750 x 2100'" is the query's regex match; the drafter reads: type "(PTh300a) 750 x 2100".
        private static readonly Regex Tilde = new Regex(@"\s*~\s*'([^']*)'", RegexOptions.Compiled);
        private static readonly Regex And = new Regex(@"\s+AND\s+", RegexOptions.Compiled);

        /// <summary>A query filter in plain words: no regex operator, no GUID, "and" for AND.</summary>
        public static string Plain(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var s = Guid.Replace(text!, "…");
            s = Tilde.Replace(s, " matches “$1”");
            s = And.Replace(s, " and ");
            return s.Trim();
        }

        private static IEnumerable<EvidenceItem> Reads(IEnumerable<EvidenceItem> items) =>
            items.Where(e => e.FailedReason == null);

        /// <summary>One line for every read of the turn. One read: "62 found · host · Doors · 62 selected,
        /// 0 visible in view". Several: "3 searches · 62, 3, 24 found · host". A failed read says so.</summary>
        public static string Line(IReadOnlyList<EvidenceItem> items, ReceiptLanguage lang)
        {
            if (items == null || items.Count == 0) return "";
            var ms = lang == ReceiptLanguage.Ms;
            var ok = Reads(items).ToList();
            var failed = items.Count - ok.Count;
            var parts = new List<string>();
            if (ok.Count == 1)
            {
                var one = ok[0];
                if (one.Count is int n) parts.Add(n + (ms ? " dijumpai" : " found"));
                if (one.Sources.Count > 0) parts.Add((ms ? "dicari " : "searched ") + string.Join(", ", one.Sources));
                if (one.Categories.Count > 0) parts.Add(string.Join(", ", one.Categories));
                if (one.Selected is int sel)
                    parts.Add(one.VisibleInView is int vis
                        ? (ms ? sel + " dipilih, " + vis + " kelihatan dalam pandangan" : sel + " selected, " + vis + " visible in view")
                        : (ms ? sel + " dipilih" : sel + " selected"));
            }
            else if (ok.Count > 1)
            {
                parts.Add(ok.Count + (ms ? " carian" : " searches"));
                var counts = ok.Where(e => e.Count != null).Select(e => e.Count!.Value.ToString()).ToList();
                if (counts.Count > 0) parts.Add(string.Join(", ", counts) + (ms ? " dijumpai" : " found"));
                var sources = ok.SelectMany(e => e.Sources).Distinct().ToList();
                if (sources.Count > 0) parts.Add((ms ? "dicari " : "searched ") + string.Join(", ", sources));
                // The last read that selected something: its counts, as for one read.
                var picked = ok.LastOrDefault(e => e.Selected != null);
                if (picked?.Selected is int psel)
                    parts.Add(picked.VisibleInView is int pvis
                        ? (ms ? psel + " dipilih, " + pvis + " kelihatan dalam pandangan" : psel + " selected, " + pvis + " visible in view")
                        : (ms ? psel + " dipilih" : psel + " selected"));
            }
            if (failed > 0)
                parts.Add(failed + (ms ? " bacaan gagal" : failed == 1 ? " read failed" : " reads failed"));
            return string.Join(Sep, parts);
        }

        /// <summary>Everything not searched in the turn, once (several reads repeat the same unloaded links).</summary>
        public static List<string> NotSearched(IReadOnlyList<EvidenceItem> items) =>
            (items ?? new List<EvidenceItem>()).SelectMany(e => e.NotSearched).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>"6 linked models not searched (unloaded)"; the names go behind Details. Empty when all searched.</summary>
        public static string NotSearchedLine(IReadOnlyList<EvidenceItem> items, ReceiptLanguage lang)
        {
            var all = NotSearched(items);
            if (all.Count == 0) return "";
            var ms = lang == ReceiptLanguage.Ms;
            var links = all.Where(s => s.StartsWith("link:", StringComparison.OrdinalIgnoreCase)).ToList();
            var reasons = all.Select(s => Regex.Match(s, @"\(([^()]+)\)\s*$")).Where(m => m.Success).Select(m => m.Groups[1].Value.Trim().ToLowerInvariant()).Distinct().ToList();
            var why = reasons.Count == 1 && all.All(s => Regex.IsMatch(s, @"\([^()]+\)\s*$")) ? " (" + Reason(reasons[0], ms) + ")" : "";
            if (links.Count == all.Count)
                return all.Count + (ms ? " model terpaut tidak dicari" : all.Count == 1 ? " linked model not searched" : " linked models not searched") + why;
            return all.Count + (ms ? " sumber tidak dicari" : (all.Count == 1 ? " source not searched" : " sources not searched")) + why;
        }

        private static string Reason(string r, bool ms) =>
            ms && r == "unloaded" ? "tidak dimuatkan" : r;

        /// <summary>The lines behind Details: each distinct read once (filter in plain words), then the names not searched.</summary>
        public static List<string> Details(IReadOnlyList<EvidenceItem> items, ReceiptLanguage lang)
        {
            var lines = new List<string>();
            foreach (var e in items ?? new List<EvidenceItem>())
            {
                var line = ReceiptTemplates.EvidenceLine(e, lang);
                var plain = Plain(line);
                if (plain.Length > 0 && !lines.Contains(plain)) lines.Add(plain);
            }
            var ms = lang == ReceiptLanguage.Ms;
            foreach (var n in NotSearched(items ?? new List<EvidenceItem>()))
            {
                var name = Regex.Replace(n, @"^link:\s*", "", RegexOptions.IgnoreCase);
                var line = (ms ? "Tidak dicari: " : "Not searched: ") + name;
                if (!lines.Contains(line)) lines.Add(line);
            }
            return lines;
        }

        // ---- exports -----------------------------------------------------

        /// <summary>A markdown table in the reply (it has its own "Download CSV"): its data rows, first header
        /// cell and first data cell, to tell it from a different table of the same size.</summary>
        public sealed class ReplyTable
        {
            public int Rows;
            public string Header = "";
            public string FirstCell = "";
        }

        private static string FirstCellOf(string line)
        {
            var cells = line.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            return cells.Count > 0 ? cells[0] : "";
        }

        /// <summary>The markdown tables in a reply.</summary>
        public static List<ReplyTable> ReplyTables(string? reply)
        {
            var tables = new List<ReplyTable>();
            if (string.IsNullOrEmpty(reply)) return tables;
            var lines = reply!.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)) continue;
                var j = i;
                while (j < lines.Length && lines[j].TrimStart().StartsWith("|", StringComparison.Ordinal)) j++;
                var block = lines.Skip(i).Take(j - i).ToList();
                if (block.Count >= 2 && Regex.IsMatch(block[1], @"^\s*\|?\s*:?-{3,}"))
                    tables.Add(new ReplyTable { Rows = block.Count - 2, Header = FirstCellOf(block[0]), FirstCell = block.Count > 2 ? FirstCellOf(block[2]) : "" });
                i = j;
            }
            return tables;
        }

        private static bool SameCell(string a, string b) =>
            a.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>The reply's table shows this evidence table: the same number of rows and the same first
        /// header or the same first cell. Two different tables that happen to be the same size are not one.</summary>
        public static bool ReplyShows(ResultTable t, IEnumerable<ReplyTable>? replyTables)
        {
            if (replyTables == null) return false;
            var header = t.Columns.Count > 0 ? t.Columns[0] : "";
            var first = t.Rows.Count > 0 && t.Rows[0].Count > 0 ? t.Rows[0][0] : "";
            return replyTables.Any(r => r.Rows == t.Rows.Count && (SameCell(r.Header, header) || SameCell(r.FirstCell, first)));
        }

        /// <summary>The tables that get an Export chip: each distinct table once (same title, columns and
        /// rows are one table), and none the reply already exports through its own table.</summary>
        public static List<ResultTable> ExportTables(IReadOnlyList<ResultTable> tables, IReadOnlyCollection<ReplyTable>? replyTables, int max)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ResultTable>();
            foreach (var t in tables ?? new List<ResultTable>())
            {
                if (t.Kind == ResultTable.CheckKind) { result.Add(t); continue; }
                if (t.Rows.Count == 0) continue;
                if (ReplyShows(t, replyTables)) continue;
                var mid = t.Rows[t.Rows.Count / 2];
                var key = t.Title + "\u0001" + string.Join("\u0002", t.Columns) + "\u0001" + t.Rows.Count + "\u0001"
                          + string.Join("\u0002", t.Rows[0]) + "\u0001" + string.Join("\u0002", mid) + "\u0001" + string.Join("\u0002", t.Rows[t.Rows.Count - 1]);
                if (seen.Add(key)) result.Add(t);
            }
            return result.Take(max).ToList();
        }

        /// <summary>A short chip label for a table: its title up to the first colon, no trailing dots.</summary>
        public static string ExportName(ResultTable t)
        {
            var title = (t.Title ?? "").Trim();
            var cut = title.IndexOf(':');
            if (cut > 0) title = title.Substring(0, cut);
            title = title.TrimEnd('.', '…', ' ');
            return title.Length <= 28 ? title : title.Substring(0, 27) + "…";
        }
    }
}
