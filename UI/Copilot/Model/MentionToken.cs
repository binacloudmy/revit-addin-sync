using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Pure @-mention token logic split out of MentionInput so it compiles in
    /// the Tests project (no WPF). Owns the two rules the picker lives by:
    /// where the open token starts, and whether an item matches the query.
    /// </summary>
    public static class MentionToken
    {
        // Longest query the picker keeps filtering on. Item names are short
        // ("Current selection · 12 elements" ≈ 31 chars); past this the user is
        // writing prose, and every keystroke would otherwise re-query the
        // Revit-backed provider for the rest of the message.
        private const int MaxQueryLength = 40;

        /// <summary>
        /// Index of the "@" that opens the mention token ending at <paramref name="caret"/>,
        /// with the query text between them, or -1 / null when no token is open.
        /// Never throws: caret 0 with text starting "@" (a programmatic Text set
        /// resets the caret to 0 before the caller restores it) must yield -1,
        /// not a negative-length Substring.
        /// </summary>
        public static int Find(string text, int caret, out string query)
        {
            query = null;
            if (string.IsNullOrEmpty(text) || caret < 1) return -1;
            if (caret > text.Length) caret = text.Length;

            int at = text.LastIndexOf('@', caret - 1);
            if (at < 0) return -1;

            string q = text.Substring(at + 1, caret - at - 1);
            if (q.Length > MaxQueryLength) return -1;
            if (q.IndexOf('\n') >= 0) return -1;
            // "@" then space = dismiss (and "@ foo" never means a mention).
            if (q.Length > 0 && q[0] == ' ') return -1;

            query = q;
            return at;
        }

        /// <summary>Case-insensitive substring match — spaces in the query are
        /// significant, so "Aras 01" finds the level named "Aras 01".</summary>
        public static bool Matches(string item, string query) =>
            !string.IsNullOrEmpty(item) &&
            item.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
