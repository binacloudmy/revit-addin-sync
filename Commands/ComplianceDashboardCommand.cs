using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ComplianceDashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;

                DockablePane pane = uiApp.GetDockablePane(ComplianceDashboardHost.PaneId);

                if (pane == null)
                {
                    TaskDialog.Show("BINA Fire Compliance", "Compliance panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                    pane.Show();

                if (App.ComplianceDashboardHost?.DashboardPanel != null)
                {
                    App.ComplianceDashboardHost.DashboardPanel.SetRevitApp(uiApp);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Fire Compliance — Error", $"Failed to open panel: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
