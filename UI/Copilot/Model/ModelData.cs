using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Supplies live model data to vetted-tool forms (so dropdowns show the real document,
    /// not static placeholders). The Revit-backed implementation is set on
    /// <see cref="CopilotModelData.Current"/> by the panel.
    /// </summary>
    public interface IModelData
    {
        /// <summary>Real view names of the given type ("Floor Plan" / "3D" / "Section" /
        /// "Elevation" / "Drafting"); all views when type is null/empty.</summary>
        List<string> Views(string viewType);

        /// <summary>View names matching a free-text query, for open-view
        /// disambiguation. A case-insensitive EXACT match returns alone
        /// (unambiguous); otherwise every token-match is returned (numbers match
        /// with or without leading zeros, e.g. "aras 1" matches "Aras 01") so the
        /// caller can ask the user which one.</summary>
        List<string> MatchViews(string query);

        /// <summary>Real schedule names in the active document (no templates, no special schedules).</summary>
        List<string> Schedules();
    }

    public static class CopilotModelData
    {
        public static IModelData Current;
    }
}
