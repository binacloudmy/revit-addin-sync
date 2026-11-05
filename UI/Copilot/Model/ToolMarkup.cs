#nullable enable
// Text that is tool-call markup, not an answer (reliability loop, 5 Nov). The model
// sometimes writes a tool call as plain text ("<｜｜DSML｜｜ invoke name=...>",
// "<function_calls>") instead of calling the tool; the pane must never show it.
// The backend should drop it too; this is the pane's own guard, applied to what is
// DISPLAYED (the raw stream is kept as received). Revit-free.

using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ToolMarkup
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // The DSML wrapper uses full-width or ASCII bars around the word: <｜｜DSML｜｜ calls>, <|DSML|invoke ...>.
        private const string Bars = "[｜|]{1,3}";
        private static readonly Regex DsmlBlock = new Regex(
            "<" + Bars + @"\s*DSML\s*" + Bars + @"\s*(?<n>[A-Za-z_]+)[\s\S]*?</" + Bars + @"\s*DSML\s*" + Bars + @"\s*\k<n>\s*>",
            Opt);
        private static readonly Regex DsmlOpenToEnd = new Regex("<" + Bars + @"\s*DSML[\s\S]*$", Opt);
        private static readonly Regex DsmlPartialTail = new Regex("<" + Bars + @"?\s*(D(S(M(L)?)?)?)?$", Opt);
        private static readonly Regex XmlBlock = new Regex(
            @"<(?<n>function_calls|tool_calls?|tool_use|antml:function_calls)\b[^>]*>[\s\S]*?</\k<n>\s*>", Opt);
        private static readonly Regex XmlOpenToEnd = new Regex(
            @"<(function_calls|tool_calls?|tool_use|antml:function_calls)\b[\s\S]*$", Opt);

        /// <summary>True when the text holds tool-call markup.</summary>
        public static bool Contains(string? text) =>
            !string.IsNullOrEmpty(text) && (DsmlOpenToEnd.IsMatch(text!) || XmlOpenToEnd.IsMatch(text!));

        /// <summary>The text without tool-call markup. Unchanged (same reference) when there is none.
        /// An unfinished block (still streaming) is cut to its start, so half a tag never shows.</summary>
        public static string Strip(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var t = text!;
            var hit = Contains(t);
            var tail = DsmlPartialTail.IsMatch(t);
            if (!hit && !tail) return t;
            var s = DsmlBlock.Replace(t, "");
            s = XmlBlock.Replace(s, "");
            s = DsmlOpenToEnd.Replace(s, "");
            s = XmlOpenToEnd.Replace(s, "");
            s = DsmlPartialTail.Replace(s, "");
            return s.TrimEnd();
        }
    }
}
