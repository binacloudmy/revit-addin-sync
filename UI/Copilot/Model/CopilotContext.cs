using System.Collections.Generic;
using System.Reflection;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Version strings auto-attached to feedback payloads and context rows.</summary>
    public static class CopilotContext
    {
        /// <summary>"Copilot {assembly version, 3 parts}".</summary>
        public static string AddinVersion { get; } = "Copilot " +
            (typeof(CopilotContext).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        /// <summary>Set from the Revit host at pane init ("Revit 2024.2"); default for harness.</summary>
        public static string RevitVersion { get; set; } = "Revit";

        public static string ShortLabel => AddinVersion + " · " + RevitVersion;

        public static string ContextLabel(string commandName = null)
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(commandName)) bits.Add(commandName);
            bits.Add(AddinVersion);
            bits.Add(RevitVersion);
            return "Auto-attached · " + string.Join(" · ", bits);
        }
    }
}
