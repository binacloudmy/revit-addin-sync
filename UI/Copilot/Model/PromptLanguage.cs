#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>The one place that decides the drafter's language. The pane's receipts
    /// follow it, and it is sent with each turn as <c>lang</c> so the cloud replies in
    /// it: Malay when the prompt has at least two Malay words (one in a prompt of at
    /// most four words), English otherwise.</summary>
    public static class PromptLanguage
    {
        // Words common in drafters' Malay requests and rare in English ones.
        private static readonly HashSet<string> Malay = new(StringComparer.OrdinalIgnoreCase)
        {
            "yang", "dan", "di", "ke", "dari", "pada", "untuk", "dengan", "ini", "itu", "semua", "setiap",
            "tukar", "naikkan", "turunkan", "ubah", "cari", "tunjuk", "tunjukkan", "senaraikan", "kira", "semak",
            "berapa", "banyak", "mana", "apa", "ada", "tiada", "tak", "tidak", "bukan", "saya", "boleh", "nak",
            "tolong", "sila", "buat", "letak", "padam", "tambah", "kenapa", "bagaimana", "jika", "kalau",
            "pintu", "tingkap", "dinding", "siling", "lantai", "bumbung", "bilik", "aras", "tandas", "tangga",
            "lajur", "rasuk", "paip", "nombor", "nama", "jenis", "pandangan", "helaian", "jadual", "kalis",
            "isi", "isikan", "pilih", "lampu", "kedua-dua", "pasang", "pilihan", "brosur", "muka", "surat",
        };

        private static readonly Regex Words = new(@"[\p{L}\-]+", RegexOptions.Compiled);

        public static ReceiptLanguage Detect(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return ReceiptLanguage.En;
            var words = Words.Matches(prompt!).Cast<Match>().Select(m => m.Value).Where(w => w.Trim('-').Length > 0).ToList();
            var hits = words.Count(w => Malay.Contains(w));
            return hits >= 2 || (hits == 1 && words.Count <= 4) ? ReceiptLanguage.Ms : ReceiptLanguage.En;
        }

        /// <summary>The wire code of <c>lang</c> in a turn request.</summary>
        public static string Code(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "ms" : "en";
    }
}
