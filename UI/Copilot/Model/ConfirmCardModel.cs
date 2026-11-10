#nullable enable
// Labels the add-in owns for the tools that may write. The confirm card is gone
// (writes auto-apply, AI-290); these two members are still used by the library,
// the result card and the change-name mapping.

using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ConfirmCardModel
    {
        /// <summary>Labels the add-in owns for the tools that may write.</summary>
        public static string ToolLabelOf(string? tool, bool ms) => tool switch
        {
            ToolNames.SetParameters => ms ? "Tetapkan parameter" : "Set parameters",
            ToolNames.RunRevitCode => ms ? "Jalankan kod Revit" : "Run Revit code",
            _ => tool ?? "",
        };

        /// <summary>Key prefix W3 uses for an element with no category: its class name.</summary>
        public const string ClassKeyPrefix = "class:";
    }
}
