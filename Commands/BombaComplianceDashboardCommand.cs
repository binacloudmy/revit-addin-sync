using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class BombaComplianceDashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;

                DockablePane pane = uiApp.GetDockablePane(BombaComplianceDashboardHost.PaneId);

                if (pane == null)
                {
                    TaskDialog.Show("BINA Bomba Compliance", "Bomba Compliance panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                    pane.Show();

                if (App.BombaComplianceDashboardHost != null &&
                    App.BombaComplianceDashboardHost.DashboardPanel != null)
                {
                    App.BombaComplianceDashboardHost.DashboardPanel.SetRevitApp(uiApp);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Bomba Compliance — Error", "Failed to open panel: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
