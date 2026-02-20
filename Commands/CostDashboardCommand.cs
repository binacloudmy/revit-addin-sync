using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class CostDashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;

                // Get or show the dockable pane
                DockablePane pane = uiApp.GetDockablePane(CostDashboardHost.PaneId);

                if (pane == null)
                {
                    TaskDialog.Show("BINA Cost", "Cost Tracker panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                // Show the pane
                if (!pane.IsShown())
                {
                    pane.Show();
                }

                // Get the dashboard panel and refresh
                if (App.CostDashboardHost?.DashboardPanel != null)
                {
                    App.CostDashboardHost.DashboardPanel.SetRevitApp(uiApp);
                    App.CostDashboardHost.DashboardPanel.RefreshData();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Cost — Error", $"Failed to open dashboard: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
