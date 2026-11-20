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
            "projek", "skor", "tengok", "lagi", "sekali", "hai", "ya", "teruskan",
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

        /// <summary>The language of the pane's own fixed labels on a turn's cards (the question form, the result card, its receipts and its
        /// history line): the drafter's (owner, 21 Nov: "follow the best practice", which replaces the 14 Nov G8 ruling that kept them English).
        /// A Malay chat gets Malay chrome, an English chat the English it always had; Revit names (categories, parameters, families, types,
        /// views, levels, units) and the model's own sentences stay exactly as written. The turn sends the same language to the cloud
        /// (<see cref="Code"/>), so the backend's own cards follow it too.</summary>
        public static ReceiptLanguage Card(ReceiptLanguage turn) => turn;

        /// <summary>r18 (live L15 8f1279fc, a Malay chat): the language of the turn's running chrome, the "Working" card (Step, Stop, its
        /// helper line) and the Steps list ("AGENT ACTIVITY"), is the drafter's, as the step rows themselves have been since r17 (StepLabels
        /// follows the turn's language). Since r21 the cards follow it as well (<see cref="Card"/>).</summary>
        public static ReceiptLanguage Activity(string? prompt) => Detect(prompt);

        // r21: words that say yes, no or "again" in either language: a short message made only of them has no language of its own.
        private static readonly HashSet<string> Neutral = new(StringComparer.OrdinalIgnoreCase)
        {
            "ok", "okay", "okey", "yes", "no", "retry", "proceed", "continue", "go", "ahead", "sure", "done", "thanks", "y", "n",
        };

        /// <summary>r21 (sticky language, audit of 21 Nov): a neutral message ("ok", "retry", "go ahead": four words or fewer, every one a
        /// yes/no/again word) keeps the chat's language, <paramref name="previous"/>, so a Malay chat's "retry" gets Malay chrome; any
        /// other message (and the first of a chat) is read on its own words (<see cref="Detect"/>), so "ya" reads Malay and "ok" in an
        /// English chat stays English. The backend resolves the same case on its side (copilot_lang.chat_lang).</summary>
        public static ReceiptLanguage Follow(string? prompt, ReceiptLanguage? previous)
        {
            if (previous is ReceiptLanguage before && !string.IsNullOrWhiteSpace(prompt))
            {
                var words = Words.Matches(prompt!).Cast<Match>().Select(m => m.Value).ToList();
                if (words.Count > 0 && words.Count <= 4 && words.All(w => Neutral.Contains(w))) return before;
            }
            return Detect(prompt);
        }

        /// <summary>The language the server resolved for the turn (<c>result.lang</c>), else <paramref name="fallback"/>.</summary>
        public static ReceiptLanguage FromServer(string? lang, ReceiptLanguage fallback) =>
            string.Equals(lang, "ms", StringComparison.OrdinalIgnoreCase) ? ReceiptLanguage.Ms
            : string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? ReceiptLanguage.En : fallback;

        /// <summary>The wire code of <c>lang</c> in a turn request.</summary>
        public static string Code(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "ms" : "en";
    }
}
