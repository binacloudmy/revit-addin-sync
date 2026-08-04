using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.SpacePlanning;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Ribbon command: shows the right-docked Space Planning pane.
    ///
    /// Mirrors OpenCopilotCommand, minus the context push — this pane holds no chat
    /// history and no model name, and its one write path (place_massing_scheme) goes
    /// through McpJobPump, which App.OnStartup already wired.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenSpacePlanningCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;

                // Build places geometry into a document — without one open, the pane
                // would take a brief and then fail at the last step.
                if (uiApp.ActiveUIDocument == null)
                {
                    TaskDialog.Show("BINA Space Planning", "Please open a Revit project first.");
                    return Result.Cancelled;
                }

                DockablePane pane = uiApp.GetDockablePane(SpacePlanningPaneHost.PaneId);
                if (pane == null)
                {
                    TaskDialog.Show("BINA Space Planning",
                        "Space Planning panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                {
                    pane.Show();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Space Planning — Error", $"Failed to open Space Planning: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
