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

    public enum InlineKind { Text, Image, Code, Bold, Italic, Strike, Link }

    /// <summary>One piece of a line. Text: the literal text. Image: Text = alt, Url =
    /// target. Code: Text = the code. Bold / Italic / Strike: Text = the inside, which
    /// may hold more spans (call TokenizeInline on it). Link: Text = label, Url = target.</summary>
    public sealed class InlineToken
    {
        public InlineKind Kind { get; }
        public string Text { get; }
        public string Url { get; }

        public InlineToken(InlineKind kind, string text, string url = "")
        {
            Kind = kind;
            Text = text;
            Url = url;
        }
    }

    public static class MarkdownBlocks
    {
        private static readonly Regex HeadingRx = new Regex(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
        // - * + and the bullet dots U+2022 and U+00B7 (a model sometimes writes the glyph itself).
        private static readonly Regex BulletRx = new Regex(@"^(\s*)[-*+\u2022\u00B7]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex NumberedRx = new Regex(@"^(\s*)(\d{1,4})[.)]\s+(.*)$", RegexOptions.Compiled);
        // --- *** ___ (three or more of one character, spaces between allowed).
        private static readonly Regex RuleRx = new Regex(@"^([-*_])(?:[ \t]*\1){2,}$", RegexOptions.Compiled);

        // Inline spans, one named group per kind. Image syntax first, so "![alt](url)"
        // never falls through to a link. Code spans before emphasis, so "`a*b*c`" stays
        // code. Emphasis needs a non-space just inside each marker, so "2 * 3 * 4" stays
        // plain text. __bold__ also needs a non-word character outside each pair, so
        // snake_case_names and a__b__c stay literal.
        private static readonly Regex InlineRx = new Regex(
            @"(?<img>!\[(?<alt>[^\]]*)\]\((?<imgurl>[^)\s]+)[^)]*\))" +
            @"|(?<code>`(?<codetext>[^`]+)`)" +
            @"|(?<bold>\*\*(?!\s)(?<boldtext>.+?)(?<!\s)\*\*)" +
            @"|(?<boldu>(?<!\w)__(?!\s)(?<boldutext>.+?)(?<!\s)__(?!\w))" +
            @"|(?<strike>~~(?!\s)(?<striketext>.+?)(?<!\s)~~)" +
            @"|(?<italic>\*(?!\s)(?<italictext>[^*]+?)(?<!\s)\*)" +
            @"|(?<link>\[(?<linktext>[^\]]+)\]\((?<linkurl>[^)]+)\))",
            RegexOptions.Compiled);

        /// <summary>``` fence line (trimmed).</summary>
        public static bool IsFence(string trimmed) => trimmed.StartsWith("```", StringComparison.Ordinal);

        /// <summary>A row with outer pipes: "| a | b |".</summary>
        public static bool IsPipeRow(string trimmed)
        {
            var t = (trimmed ?? "").Trim();
            if (t.Length < 2 || t[0] != '|' || t[t.Length - 1] != '|') return false;
            int pipes = 0;
            foreach (var c in t) if (c == '|') pipes++;
            return pipes >= 2;
        }

        /// <summary>The header/body divider: "|---|:--:|".</summary>
        public static bool IsSeparatorRow(string trimmed)
        {
            var t = (trimmed ?? "").Trim();
            if (t.IndexOf('-') < 0) return false;
            foreach (var c in t)
                if (c != '|' && c != '-' && c != ':' && c != ' ' && c != '\t') return false;
            return true;
        }

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

        /// <summary>"- x" or "* x" (any indent). Level = indent / 2, capped at 3.</summary>
        public static bool TryBullet(string line, out int level, out string text)
        {
            var m = BulletRx.Match(line ?? "");
            if (!m.Success) { level = 0; text = ""; return false; }
            level = Level(m.Groups[1].Value);
            text = m.Groups[2].Value;
            return true;
        }

        /// <summary>"1. x" (any indent).</summary>
        public static bool TryNumbered(string line, out int level, out string number, out string text)
        {
            var m = NumberedRx.Match(line ?? "");
            if (!m.Success) { level = 0; number = ""; text = ""; return false; }
            level = Level(m.Groups[1].Value);
            number = m.Groups[2].Value;
            text = m.Groups[3].Value;
            return true;
        }

        /// <summary>"---", "***" or "___" (three or more of one character) on a line by itself.</summary>
        public static bool IsHorizontalRule(string? line) => RuleRx.IsMatch((line ?? "").Trim());

        /// <summary>A line indented by 2+ spaces (a tab counts as 4) that is not itself a
        /// list marker, heading, quote, rule, table row or fence: when it directly follows
        /// a list item (the caller knows that), it continues that item's text. text is the
        /// trimmed line.</summary>
        public static bool TryListContinuation(string? line, out string text)
        {
            text = "";
            var l = line ?? "";
            var trimmed = l.Trim();
            if (trimmed.Length == 0) return false;
            int indent = 0;
            foreach (var c in l)
            {
                if (c == ' ') indent++;
                else if (c == '\t') indent += 4;
                else break;
            }
            if (indent < 2) return false;
            if (IsFence(trimmed) || IsPipeRow(trimmed) || IsHorizontalRule(trimmed)) return false;
            if (trimmed.StartsWith("> ", StringComparison.Ordinal)) return false;
            if (HeadingLevel(trimmed, out _) > 0) return false;
            if (TryBullet(l, out _, out _) || TryNumbered(l, out _, out _, out _)) return false;
            text = trimmed;
            return true;
        }

        private static int Level(string indent)
        {
            int n = 0;
            foreach (var c in indent) n += c == '\t' ? 4 : 1;
            return Math.Min(3, n / 2);
        }

        // ------------------------------------------------------------------
        // Inline spans
        // ------------------------------------------------------------------

        /// <summary>Splits one line of text into literal runs and inline spans:
        /// ![alt](url), `code`, **bold**, __bold__, ~~strike~~, *italic*, [label](url).</summary>
        public static List<InlineToken> TokenizeInline(string? text)
        {
            var tokens = new List<InlineToken>();
            var s = text ?? "";
            int last = 0;
            foreach (Match m in InlineRx.Matches(s))
            {
                if (m.Index > last) tokens.Add(new InlineToken(InlineKind.Text, s.Substring(last, m.Index - last)));
                if (m.Groups["img"].Success) tokens.Add(new InlineToken(InlineKind.Image, m.Groups["alt"].Value, m.Groups["imgurl"].Value));
                else if (m.Groups["code"].Success) tokens.Add(new InlineToken(InlineKind.Code, m.Groups["codetext"].Value));
                else if (m.Groups["bold"].Success) tokens.Add(new InlineToken(InlineKind.Bold, m.Groups["boldtext"].Value));
                else if (m.Groups["boldu"].Success) tokens.Add(new InlineToken(InlineKind.Bold, m.Groups["boldutext"].Value));
                else if (m.Groups["strike"].Success) tokens.Add(new InlineToken(InlineKind.Strike, m.Groups["striketext"].Value));
                else if (m.Groups["italic"].Success) tokens.Add(new InlineToken(InlineKind.Italic, m.Groups["italictext"].Value));
                else if (m.Groups["link"].Success) tokens.Add(new InlineToken(InlineKind.Link, m.Groups["linktext"].Value, m.Groups["linkurl"].Value));
                last = m.Index + m.Length;
            }
            if (last < s.Length) tokens.Add(new InlineToken(InlineKind.Text, s.Substring(last)));
            return tokens;
        }

        // ------------------------------------------------------------------
        // Streaming
        // ------------------------------------------------------------------

        /// <summary>The part of a still-arriving reply that is safe to draw: cut
        /// before an unclosed code fence, or before a table that has not ended
        /// yet. A last line holding a pipe is held too: it may be a row still
        /// being written.</summary>
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
                if (IsPipeRow(trimmed))
                {
                    if (tableStart < 0) tableStart = i;
                    continue;
                }
                // The empty text after a final newline is not a line yet: the
                // next row may still be on its way.
                if (tableStart >= 0 && !(i == lines.Length - 1 && lines[i].Length == 0)) tableStart = -1;
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
