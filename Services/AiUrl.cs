namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure, dependency-free builder for bina-ai endpoint URLs.
    /// Kept in its own file so the Tests project can compile-link it
    /// without pulling in Revit SDK / Newtonsoft / Models (AIService deps).
    /// </summary>
    internal static class AiUrl
    {
        internal const string RevitAiPath = "/agents/revit-ai";

        internal static string Build(string baseUrl, string endpoint) =>
            $"{baseUrl}{RevitAiPath}/{endpoint}";
    }
}
