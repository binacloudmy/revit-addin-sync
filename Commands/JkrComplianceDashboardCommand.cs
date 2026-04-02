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
