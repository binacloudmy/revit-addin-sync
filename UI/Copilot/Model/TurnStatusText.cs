#nullable enable
// The words of the status chip. The state key (RunStatus) stays "Needs input" because the chip's colours key on it; the words follow the card.
// r22 (live M1 2ca7d010, 83e423eb): the words follow the chat's language too ("Perlu jawapan anda", "Selesai"); the keys never change.
namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TurnStatusText
    {
        public const string NeedsAnswer = "Needs your answer";
        public const string NeedsOk = "Needs your OK";
        public const string NeedsAnswerMs = "Perlu jawapan anda";
        public const string NeedsOkMs = "Perlu kelulusan anda";

        /// <summary>The chip's text for a state: "Needs input" reads as <paramref name="needs"/>; every other state is its own words.</summary>
        public static string Of(string? status, string needs) =>
            status == "Needs input" ? (string.IsNullOrEmpty(needs) ? NeedsAnswer : needs) : status ?? "";

        /// <summary>The chip's words in the chat's language: a question's "Needs your answer" (a gate's "Needs your OK") and the states
        /// (Ready, Running, Done, Stopped, Failed) in Malay for a Malay chat; English exactly as before.</summary>
        public static string Of(string? status, bool wide, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            if (status == "Needs input") return wide ? (ms ? NeedsOkMs : NeedsOk) : (ms ? NeedsAnswerMs : NeedsAnswer);
            if (!ms) return status ?? "";
            switch (status)
            {
                case "Ready": return "Sedia";
                case "Running": return "Sedang berjalan";
                case "Done": return "Selesai";
                case "Stopped": return "Dihentikan";
                case "Failed": return "Gagal";
                default: return status ?? "";
            }
        }
    }
}
