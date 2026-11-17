#nullable enable
// The words of the status chip. The state key (RunStatus) stays "Needs input" because the chip's colours key on it; the words follow the card.
namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TurnStatusText
    {
        public const string NeedsAnswer = "Needs your answer";
        public const string NeedsOk = "Needs your OK";

        /// <summary>The chip's text for a state: "Needs input" reads as <paramref name="needs"/>; every other state is its own words.</summary>
        public static string Of(string? status, string needs) =>
            status == "Needs input" ? (string.IsNullOrEmpty(needs) ? NeedsAnswer : needs) : status ?? "";
    }
}
