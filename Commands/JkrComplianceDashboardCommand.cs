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

                // The pane reaches Revit through App.RevitDispatcher, so it no longer
                // needs the command's UIApplication.

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
