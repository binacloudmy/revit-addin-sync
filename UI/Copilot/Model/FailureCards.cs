#nullable enable
// The words of the attention banner for a turn that could not run (RouteFailure).
// Pure so a unit test can pin them: the banner must say what really happened, and a
// host-policy refusal is not a network failure.
namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class FailureBanner
    {
        public string Title = "";
        public string Body = "";
        public string Origin = "";
    }

    public static class FailureCards
    {
        /// <summary>The banner for a failure the pane can say more about, or null (the failure then renders as a plain reply).</summary>
        public static FailureBanner? For(RouteFailure kind, string? paneNote)
        {
            switch (kind)
            {
                case RouteFailure.Unreachable:
                    return new FailureBanner
                    {
                        Title = "The Copilot couldn't reach BINA",
                        Body = "Check the network and try again. Nothing was changed in the model.",
                        Origin = "copilot · " + Origin(paneNote, "unreachable"),
                    };
                case RouteFailure.HostBlocked:
                    return new FailureBanner
                    {
                        Title = "This add-in build can't use that Copilot address",
                        Body = Sentence(paneNote, "This add-in build only talks to BINA's own servers; the configured copilot address is not one of them.")
                               + " Nothing was sent and nothing was changed in the model.",
                        Origin = "copilot · host not allowed",
                    };
                default:
                    return null;
            }
        }

        private static string Origin(string? note, string fallback) => string.IsNullOrWhiteSpace(note) ? fallback : note!.Trim();

        private static string Sentence(string? note, string fallback)
        {
            var s = string.IsNullOrWhiteSpace(note) ? fallback : note!.Trim();
            return s.EndsWith(".") ? s : s + ".";
        }
    }
}
