using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Backend progress keys → the design's friendly one-line labels
    /// (single-line thinking indicator). Unknown keys are humanised.</summary>
    public static class FriendlyStep
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            ["thinking"] = "Thinking",
            ["understand"] = "Understanding your request",
            ["parse_request"] = "Understanding your request",
            ["retrieve_context"] = "Looking through the model",
            ["search_model"] = "Looking through the model",
            ["read_model"] = "Looking through the model",
            ["plan"] = "Planning the approach",
            ["reason"] = "Reasoning it through",
            ["generate"] = "Putting together a response",
            ["compose"] = "Putting together a response",
            ["build_command"] = "Preparing the command",
            ["validate"] = "Double-checking the result",
            ["verify"] = "Double-checking the result",
        };

        public static string Label(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var key = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[\s-]+", "_");
            if (Map.TryGetValue(key, out var mapped)) return mapped;
            var s = Regex.Replace(raw, "[_-]+", " ");
            s = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant().Trim();
            return s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
