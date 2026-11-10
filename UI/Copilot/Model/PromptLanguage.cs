#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>The receipt language follows the prompt: Malay when the prompt
    /// uses at least two common Malay words, English otherwise.</summary>
    public static class PromptLanguage
    {
        private static readonly HashSet<string> Malay = new(StringComparer.OrdinalIgnoreCase)
        {
            "tolong", "sila", "isi", "isikan", "semua", "untuk", "dalam", "dengan", "dan", "pada",
            "jenis", "lampu", "tukar", "buat", "pilih", "tunjuk", "kedua-dua", "ini", "saya", "nak",
        };

        public static ReceiptLanguage Detect(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return ReceiptLanguage.En;
            int hits = 0;
            foreach (Match m in Regex.Matches(prompt, @"[\p{L}\-]+"))
                if (Malay.Contains(m.Value) && ++hits >= 2) return ReceiptLanguage.Ms;
            return ReceiptLanguage.En;
        }
    }
}
