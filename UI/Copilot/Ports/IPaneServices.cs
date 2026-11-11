using System;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Ports
{
    /// <summary>
    /// The pane's non-Revit environment: config, prefs, file dialogs and the UI
    /// dispatcher. The view model holds this port instead of reaching statics.
    /// </summary>
    public interface IPaneServices
    {
        /// <summary>Current BINA Cloud config (may be null).</summary>
        BinaConfig LoadConfig();
        CopilotPrefs LoadPrefs();

        /// <summary>Run on the UI thread now (inline when already on it, or when there is no dispatcher).</summary>
        void RunOnUi(Action action);
        /// <summary>Queue on the UI thread; no-op when there is no dispatcher.</summary>
        void BeginOnUi(Action action);
        /// <summary>True when there is no dispatcher, or the caller is on its thread.</summary>
        bool IsOnUi { get; }

        /// <summary>Save-file picker; null when cancelled.</summary>
        string PickSaveFile(string title, string filter, string defaultFileName);
    }
}
