using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class JkrComplianceDashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            // Auth gate: JKR Compliance needs a signed-in BINA Cloud session — same
            // policy as the Copilot. Prompt for login instead of opening the pane.
            var config = BinaConfig.Load();
            if (config == null || !config.IsLoggedIn())
            {
                TaskDialog.Show("BINA JKR Compliance",
                    "Please sign in to use JKR Compliance — click BINA Cloud → Login in the ribbon, then try again.");
                return Result.Cancelled;
            }

            try
            {
                UIApplication uiApp = commandData.Application;

                DockablePane pane = uiApp.GetDockablePane(JkrComplianceDashboardHost.PaneId);

                if (pane == null)
                {
                    TaskDialog.Show("BINA JKR Compliance", "JKR Compliance panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                    pane.Show();

                if (App.JkrComplianceDashboardHost?.DashboardPanel != null)
                {
                    App.JkrComplianceDashboardHost.DashboardPanel.SetRevitApp(uiApp);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA JKR Compliance — Error", $"Failed to open panel: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
