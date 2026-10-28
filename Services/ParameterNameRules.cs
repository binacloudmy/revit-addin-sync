using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// How a Bina parameter name is matched to a Revit one (14zefhy2nny).
    ///
    /// Revit treats parameter names without regard to case, and Bina stores the name the user typed
    /// (older rows may carry surrounding spaces). Comparing exactly would miss the model's own
    /// parameter and make the pull bind a second shared parameter with a name that already exists - a
    /// duplicate on the next sync. Revit-free so the rule can be tested without a document.
    /// </summary>
    public static class ParameterNameRules
    {
        /// <summary>The name as it is compared: trimmed, never null.</summary>
        public static string Normalize(string name) => (name ?? string.Empty).Trim();

        /// <summary>"Mark", " mark " and "MARK" are one parameter.</summary>
        public static bool Matches(string left, string right) =>
            string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether this name can be looked up at all (blank is rejected by the pull).</summary>
        public static bool IsUsable(string name) => Normalize(name).Length > 0;
    }
}
