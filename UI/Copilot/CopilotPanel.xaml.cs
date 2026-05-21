using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// The Revit Copilot dockable-pane body. Replaces the floating AIAssistantWindow
    /// with a right-docked side panel (see docs/superpowers/plans/2026-05-21-copilot-pane-redesign.md).
    /// This Task-1 stub only holds the Revit context; the viewmodel + screens land in later tasks.
    /// </summary>
    public partial class CopilotPanel : Page
    {
        private UIApplication _uiApp;

        public CopilotPanel()
        {
            // CopilotTheme.EnsureLoaded() is called from CopilotPaneHost before this is built,
            // so token/style resources resolve once the chrome (Task 6) references them.
            InitializeComponent();
        }

        /// <summary>
        /// Pushed in by OpenCopilotCommand each time the pane is shown. Stores the live
        /// Revit application/document context the viewmodel and executor wiring will use.
        /// </summary>
        public void SetRevitContext(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }
    }
}
