#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>The one place that decides the drafter's language. The pane's receipts
    /// follow it, and it is sent with each turn as <c>lang</c> so the cloud replies in
    /// it. Malay needs a Malay function word (verb, question word, particle) plus one
    /// more Malay word of any kind, or two function words: Malay names of Revit things
    /// alone (Aras 1, Dinding, Pintu) also appear in English prompts. A prompt of at
    /// most four words needs one Malay word.</summary>
    public static class PromptLanguage
    {
        // Malay words that are not Revit names: rare in English prompts.
        private static readonly HashSet<string> Function = new(StringComparer.OrdinalIgnoreCase)
        {
            "yang", "dan", "di", "ke", "dari", "pada", "untuk", "dengan", "ini", "itu", "semua", "setiap", "dalam",
            "tukar", "naikkan", "turunkan", "ubah", "cari", "tunjuk", "tunjukkan", "senaraikan", "kira", "semak",
            "berapa", "banyak", "mana", "apa", "ada", "tiada", "tak", "tidak", "bukan", "saya", "boleh", "nak",
            "tolong", "sila", "buat", "buatkan", "letak", "padam", "tambah", "kenapa", "bagaimana", "jika", "kalau",
            "isi", "isikan", "pilih", "kedua-dua", "pasang", "kat", "ni", "tu", "maksud", "hingga", "jarak",
            "projek", "skor", "tengok", "lagi", "sekali", "hai",
        };

        // Malay names of Revit things, which English-speaking drafters also type.
        private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            "pintu", "tingkap", "dinding", "siling", "lantai", "bumbung", "bilik", "aras", "tandas", "tangga",
            "lajur", "rasuk", "paip", "nombor", "nama", "jenis", "pandangan", "helaian", "jadual", "kalis",
            "lampu", "pilihan", "brosur", "muka", "surat",
        };

        private static readonly Regex Words = new(@"[\p{L}]+(?:-[\p{L}]+)*", RegexOptions.Compiled);

        public static ReceiptLanguage Detect(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return ReceiptLanguage.En;
            var words = Words.Matches(prompt!).Cast<Match>().Select(m => m.Value).ToList();
            var function = words.Count(w => Function.Contains(w));
            var names = words.Count(w => Names.Contains(w));
            var ms = function >= 2 || (function >= 1 && names >= 1) || (function + names == 1 && words.Count <= 4);
            return ms ? ReceiptLanguage.Ms : ReceiptLanguage.En;
        }

        /// <summary>The language of the pane's own fixed labels on a turn's result card, its receipts and its history line:
        /// English, whatever language the drafter typed (the owner's ruling, first for the question form, then for the
        /// result card, 14 Nov). The model's own sentences stay as written, and the turn still sends the drafter's language
        /// to the cloud (<see cref="Code"/>) so the model answers in it. The Malay strings stay in the templates.</summary>
        public static ReceiptLanguage Card(ReceiptLanguage turn) => ReceiptLanguage.En;

        /// <summary>r18 (live L15 8f1279fc, a Malay chat): the language of the turn's running chrome, the "Working" card (Step, Stop, its
        /// helper line) and the Steps list ("AGENT ACTIVITY"), is the drafter's, as the step rows themselves have been since r17 (StepLabels
        /// follows the turn's language). G8 (<see cref="Card"/>) stays the rule for the result card, its receipts and the question form.</summary>
        public static ReceiptLanguage Activity(string? prompt) => Detect(prompt);

        /// <summary>The wire code of <c>lang</c> in a turn request.</summary>
        public static string Code(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "ms" : "en";
    }
}
