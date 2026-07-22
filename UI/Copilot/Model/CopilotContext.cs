using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Version strings auto-attached to feedback payloads and context rows.
    /// Thin adapter over <see cref="RevitWebAppSync.AppInfo"/> — the single source
    /// of truth — so the sheet chips, the kebab version line and the bug payload
    /// all agree.</summary>
    public static class CopilotContext
    {
        /// <summary>"Copilot {assembly version, 3 parts}".</summary>
        public static string AddinVersion => RevitWebAppSync.AppInfo.AddinLabel;

        /// <summary>Set from the Revit host at pane init ("Revit 2024.2"); default
        /// stub for the harness. Delegates to the shared AppInfo value.</summary>
        public static string RevitVersion
        {
            get => RevitWebAppSync.AppInfo.RevitVersion;
            set => RevitWebAppSync.AppInfo.RevitVersion = value;
        }

        public static string ShortLabel => RevitWebAppSync.AppInfo.ShortLabel;

        public static string ContextLabel(string commandName = null)
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(commandName)) bits.Add(commandName);
            bits.Add(RevitWebAppSync.AppInfo.AddinLabel);
            bits.Add(RevitWebAppSync.AppInfo.RevitVersion);
            return "Auto-attached · " + string.Join(" · ", bits);
        }
    }
}
