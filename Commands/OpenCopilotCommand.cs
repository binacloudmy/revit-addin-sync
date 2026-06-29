using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Ribbon command: shows the right-docked Revit Copilot pane and pushes the live
    /// Revit context into it. Replaces OpenAssistantCommand (the floating window).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenCopilotCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;

                if (uiApp.ActiveUIDocument == null)
                {
                    TaskDialog.Show("BINA AI Copilot", "Please open a Revit project first.");
                    return Result.Cancelled;
                }

                DockablePane pane = uiApp.GetDockablePane(CopilotPaneHost.PaneId);
                if (pane == null)
                {
                    TaskDialog.Show("BINA AI Copilot", "Copilot panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                {
                    pane.Show();
                }

                App.CopilotPaneHost?.Panel?.SetRevitContext(uiApp);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA AI Copilot — Error", $"Failed to open Copilot: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
