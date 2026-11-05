#nullable enable
// Line-level markdown rules shared by MarkdownRenderer (WPF) and the chat's
// streaming view. Pure on purpose (no WPF, no Revit) so Tests can link it.
//
// The streaming half answers one question: how much of a reply that is still
// arriving can be drawn now without showing half a table or half a code block
// as raw text. A table is only complete once a line that is not a table row
// follows it; a code block once its closing fence arrives. Until then that
// trailing block is held back and drawn whole when it is done (or when the
// turn ends and the full reply is rendered).
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Helpers
{
    /// <summary>What the streaming view is holding back at the end of a reply.</summary>
    public enum HeldBlock { None, Table, Code }

    public static class MarkdownBlocks
    {
        private static readonly Regex HeadingRx = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        private static readonly Regex BulletRx = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex NumberedRx = new Regex(@"^(\s*)(\d{1,4})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RuleRx = new Regex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);

        /// <summary>``` or ~~~ fence line (trimmed).</summary>
        public static bool IsFence(string trimmed) =>
            trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

        /// <summary>A row with outer pipes: "| a | b |".</summary>
        public static bool IsPipeRow(string trimmed)
        {
            var t = (trimmed ?? "").Trim();
            if (t.Length < 2 || t[0] != '|' || t[t.Length - 1] != '|') return false;
            int pipes = 0;
            foreach (var c in t) if (c == '|') pipes++;
            return pipes >= 2;
        }

        /// <summary>The header/body divider: "|---|:--:|" or "--- | ---".</summary>
        public static bool IsSeparatorRow(string trimmed)
        {
            var t = (trimmed ?? "").Trim();
            if (t.IndexOf('-') < 0 || t.IndexOf('|') < 0) return false;
            foreach (var c in t)
                if (c != '|' && c != '-' && c != ':' && c != ' ' && c != '\t') return false;
            return true;
        }

        /// <summary>Does a table start at line i? Either a piped row, or a
        /// pipe-separated header followed by a divider (GFM tables without the
        /// outer pipes, which models write often).</summary>
        public static bool TableStartsAt(IReadOnlyList<string> lines, int i)
        {
            if (i < 0 || i >= lines.Count) return false;
            var t = lines[i].Trim();
            if (IsPipeRow(t)) return true;
            return t.IndexOf('|') >= 0 && !IsSeparatorRow(t)
                   && i + 1 < lines.Count && IsSeparatorRow(lines[i + 1]);
        }

        /// <summary>Once a table has started, every following non-blank line with a pipe is a row.</summary>
        public static bool TableContinues(string line) =>
            !string.IsNullOrWhiteSpace(line) && line.IndexOf('|') >= 0 && !IsFence(line.Trim());

        /// <summary>Cells of one row. A pipe inside `code` or written as \| stays in its cell.</summary>
        public static string[] SplitCells(string row)
        {
            var t = (row ?? "").Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal) && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            var cells = new List<string>();
            var cur = new StringBuilder();
            bool inCode = false;
            for (int i = 0; i < t.Length; i++)
            {
                var c = t[i];
                if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; continue; }
                if (c == '`') inCode = !inCode;
                if (c == '|' && !inCode) { cells.Add(cur.ToString().Trim()); cur.Clear(); continue; }
                cur.Append(c);
            }
            cells.Add(cur.ToString().Trim());
            return cells.ToArray();
        }

        /// <summary>"## Title" → 2 and "Title". 0 when the line is not a heading.</summary>
        public static int HeadingLevel(string trimmed, out string text)
        {
            var m = HeadingRx.Match(trimmed ?? "");
            if (!m.Success) { text = ""; return 0; }
            text = m.Groups[2].Value;
            return m.Groups[1].Value.Length;
        }

        /// <summary>"---", "***" or "___" alone on a line.</summary>
        public static bool IsRule(string line) => RuleRx.IsMatch(line ?? "");

        /// <summary>"- x", "* x", "+ x" (any indent). Level = indent / 2, capped at 3.</summary>
        public static bool TryBullet(string line, out int level, out string text)
        {
            var m = BulletRx.Match(line ?? "");
            if (!m.Success || IsRule(line)) { level = 0; text = ""; return false; }
            level = Level(m.Groups[1].Value);
            text = m.Groups[2].Value;
            return true;
        }

        /// <summary>"1. x" or "1) x" (any indent).</summary>
        public static bool TryNumbered(string line, out int level, out string number, out string text)
        {
            var m = NumberedRx.Match(line ?? "");
            if (!m.Success) { level = 0; number = ""; text = ""; return false; }
            level = Level(m.Groups[1].Value);
            number = m.Groups[2].Value;
            text = m.Groups[3].Value;
            return true;
        }

        private static int Level(string indent)
        {
            int n = 0;
            foreach (var c in indent) n += c == '\t' ? 4 : 1;
            return Math.Min(3, n / 2);
        }

        // ------------------------------------------------------------------
        // Streaming
        // ------------------------------------------------------------------

        /// <summary>The part of a still-arriving reply that is safe to draw: cut
        /// before an unclosed code fence, or before a table that has not ended
        /// yet (the last line of the text, finished or not, is still part of it).
        /// A last line holding a pipe is held too: it may be a table header whose
        /// divider has not arrived.</summary>
        public static string StableForStreaming(string? text, out HeldBlock held)
        {
            held = HeldBlock.None;
            if (string.IsNullOrEmpty(text)) return "";
            var s = text!.Replace("\r\n", "\n");
            var lines = s.Split('\n');
            var starts = new int[lines.Length];
            for (int i = 0, pos = 0; i < lines.Length; i++) { starts[i] = pos; pos += lines[i].Length + 1; }

            bool inCode = false;
            int fenceStart = -1;
            int tableStart = -1;   // line index of the open table, -1 when none
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (inCode)
                {
                    if (IsFence(trimmed)) { inCode = false; fenceStart = -1; }
                    continue;
                }
                if (IsFence(trimmed)) { inCode = true; fenceStart = i; tableStart = -1; continue; }
                if (tableStart >= 0)
                {
                    // The empty text after a final newline is not a line yet:
                    // the next row may still be on its way.
                    if (TableContinues(lines[i]) || (i == lines.Length - 1 && lines[i].Length == 0)) continue;
                    tableStart = -1;
                }
                if (TableStartsAt(lines, i)) tableStart = i;
            }

            int cut;
            if (inCode) { held = HeldBlock.Code; cut = fenceStart; }
            else if (tableStart >= 0) { held = HeldBlock.Table; cut = tableStart; }
            else if (lines[lines.Length - 1].IndexOf('|') >= 0) { held = HeldBlock.Table; cut = lines.Length - 1; }
            else return s;

            return s.Substring(0, starts[cut]).TrimEnd();
        }
    }
}
