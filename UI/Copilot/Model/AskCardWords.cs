#nullable enable
// r21 (owner, 21 Nov: "follow the best practice", which replaces the 14 Nov G8 ruling that kept the question form English): the
// question card's own words follow the drafter's language. An English turn reads exactly as before (every helper here returns the
// English constant unchanged); a Malay turn reads the Malay twin. The option and field labels the cloud sent, and every Revit name in
// them, are drawn as sent. WPF-free so it is unit-tested.

using System.Collections.Generic;
using System.Globalization;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed partial class AskCardModel
    {
        /// <summary>The card's fixed English words and their Malay twins (natural Malaysian Malay, as the rest of the pane).</summary>
        private static readonly Dictionary<string, string> Malay = new Dictionary<string, string>
        {
            [ShowFewerLabel] = "Tunjuk kurang",
            [AnsweredHeading] = "Jawapan anda",
            [ReadyHeading] = "Sedia",
            [PreviewHeading] = "Apa yang anda akan dapat",
            [WaitingText] = "menunggu anda",
            [NewTag] = "baharu",
            [SkippedText] = "Dilangkau",
            [ContinueLabel] = "Teruskan",
            [TypeOwnHint] = "Atau taip jawapan anda sendiri dalam kotak mesej.",
            [FilterHint] = "Taip untuk menapis",
            [SendLabel] = "Hantar",
            [TypeAnswerHint] = "Taip jawapan anda",
            [TypeNumberHint] = "Taip nombor",
            [TypeHereHint] = "Taip di sini",
            [CreateAllLabel] = "Buat semua",
            [SkipAllLabel] = "Langkau semua",
            [AnsweredText] = "dijawab",
            [MakeNothingLabel] = "Jangan buat apa-apa",
            [ProjectNoteHeading] = "Nota projek untuk disimpan",
            [NothingMatchesText] = "Tiada yang sepadan.",
            [DroppedNote] = "Sebahagian pilihan disembunyikan kerana ia kelihatan seperti kelulusan, dan BINA tidak pernah meminta kata laluan atau token di sini.",
        };

        public const string TypeAnswerHint = "Type your answer";
        public const string TypeNumberHint = "Type a number";
        public const string TypeHereHint = "Type here";
        public const string CreateAllLabel = "Create all";
        public const string SkipAllLabel = "Skip all";
        public const string AnsweredText = "answered";
        public const string MakeNothingLabel = "Make nothing";
        public const string ProjectNoteHeading = "Project note to save";
        public const string NothingMatchesText = "Nothing matches.";
        public const string DroppedNote = "Some choices were hidden because they looked like approval, and BINA never asks for passwords or tokens here.";

        /// <summary>One of the card's fixed words in <paramref name="lang"/>: the English constant itself on an English turn.</summary>
        public static string Say(ReceiptLanguage lang, string en) =>
            lang == ReceiptLanguage.Ms && Malay.TryGetValue(en, out var ms) ? ms : en;

        /// <summary>One of the card's fixed words in the card's language.</summary>
        public string Say(string en) => Say(Language, en);

        private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        /// <summary>"Show 7 more" / "Tunjuk 7 lagi".</summary>
        public static string ShowMoreLabel(int hidden, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Tunjuk " + N(hidden) + " lagi" : ShowMoreLabel(hidden);

        /// <summary>"+12 more" / "+12 lagi" while folded, "Show fewer" once open.</summary>
        public static string ReadyToggleLabel(int hidden, bool expanded, ReceiptLanguage lang) =>
            lang != ReceiptLanguage.Ms ? ReadyToggleLabel(hidden, expanded) : expanded ? Say(lang, ShowFewerLabel) : "+" + N(hidden) + " lagi";

        /// <summary>"3 left" / "3 lagi", "done" / "selesai".</summary>
        public static string CountLabel(int left, ReceiptLanguage lang) =>
            lang != ReceiptLanguage.Ms ? CountLabel(left) : left <= 0 ? "selesai" : N(left) + " lagi";

        /// <summary>"Send (2)" / "Hantar (2)".</summary>
        public static string SendWithCount(int n, ReceiptLanguage lang) =>
            lang != ReceiptLanguage.Ms ? SendWithCount(n) : n > 0 ? Say(lang, SendLabel) + " (" + N(n) + ")" : Say(lang, SendLabel);

        /// <summary>"5 sheets" / "5 helaian": Malay has no plural, and the cloud's noun ("sheet") reads as the pane's Malay word.</summary>
        public static string CountOf(int n, string noun, ReceiptLanguage lang) =>
            lang != ReceiptLanguage.Ms ? CountOf(n, noun) : N(n) + " " + MalayNoun(noun);

        /// <summary>The Malay word for the noun a decision card counts; a noun not known here stays as sent.</summary>
        public static string MalayNoun(string noun) =>
            (noun ?? "").ToLowerInvariant() switch
            {
                "sheet" => "helaian", "schedule" => "jadual", "plan" => "pelan", "level" => "aras", "circuit" => "litar", "wall" => "dinding",
                "dimension" => "dimensi", "type" => "jenis", "element" => "elemen", _ => noun ?? "",
            };

        /// <summary>What a create pick makes, in the card's language: "Buat pelan" (the cloud's Malay label) reads "Pelan baharu",
        /// "Buat FloorPlan untuk Aras 2" reads "FloorPlan baharu"; an English label as <see cref="NewLabel(string)"/>.</summary>
        public static string NewLabel(string label, ReceiptLanguage lang)
        {
            if (lang != ReceiptLanguage.Ms) return NewLabel(label);
            var l = (label ?? "").Trim();
            string rest;
            if (l.StartsWith("Buat ", System.StringComparison.OrdinalIgnoreCase)) rest = l.Substring("Buat ".Length).Trim();
            else if (l.StartsWith("Create ", System.StringComparison.OrdinalIgnoreCase)) rest = NewLabel(l).Substring("New".Length).Trim();
            else return l;
            foreach (var cut in new[] { ",", " kemudian ", " untuk " })
            {
                var at = rest.IndexOf(cut, System.StringComparison.OrdinalIgnoreCase);
                if (at > 0) rest = rest.Substring(0, at);
            }
            rest = rest.Trim();
            if (rest.Length == 0) return "Baharu";
            return char.ToUpperInvariant(rest[0]) + rest.Substring(1) + " baharu";
        }

        /// <summary>"Answer 2 more" / "Jawab 2 lagi".</summary>
        public static string AnswerMoreLabel(int open, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Jawab " + N(open) + " lagi" : "Answer " + N(open) + " more";

        /// <summary>"Make 5 sheets" / "Buat 5 helaian".</summary>
        public static string MakeLabel(int n, string noun, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Buat " + CountOf(n, noun, lang) : "Make " + CountOf(n, noun);

        /// <summary>The progress tooltip: "3 of 12 answered" / "3 daripada 12 dijawab".</summary>
        public static string AnsweredTip(int done, int all, ReceiptLanguage lang) =>
            N(done) + (lang == ReceiptLanguage.Ms ? " daripada " : " of ") + N(all) + " " + Say(lang, AnsweredText);
    }
}
