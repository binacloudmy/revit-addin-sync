namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Pure "/" command-token rule split out of MentionInput so it compiles in the Tests project (no WPF), as
    /// <see cref="MentionToken"/> is for "@".
    /// r23 (AI-460, Z8): the "/" button on an EMPTY composer set the text "/" while the caret was still 0, so this rule
    /// found no token and the tools list never opened. The button now seeds "/" with the caret after it and then asks
    /// this rule (MentionInput.SeedSlash).
    /// </summary>
    public static class SlashTrigger
    {
        /// <summary>Index of the "/" that starts the command token ending at the caret, or -1.
        /// The slash must sit at the start of the input or right after whitespace (so "and/or", URLs and paths don't
        /// trigger the palette), and the run from it to the caret must be a single unbroken token.</summary>
        public static int Find(string text, int caret)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            if (caret > text.Length) caret = text.Length;
            for (int i = caret - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '/')
                    return (i == 0 || char.IsWhiteSpace(text[i - 1])) ? i : -1;
                if (char.IsWhiteSpace(c)) return -1;   // token broken before a slash
            }
            return -1;
        }

        /// <summary>The "/query" a palette pick clears: from the "/" to the caret (the end of the text when the caret is outside
        /// it), or null when no typed "/" opened the palette (slashIndex -1: opened over a draft) or the index is stale.
        /// Callers read slashIndex before they edit the text: the edit re-runs detection, which resets it to -1.</summary>
        public static (int Start, int Length)? PickSpan(int textLength, int slashIndex, int caret)
        {
            if (slashIndex < 0 || slashIndex >= textLength) return null;
            if (caret < slashIndex || caret > textLength) caret = textLength;
            return (slashIndex, caret - slashIndex);
        }
    }
}
