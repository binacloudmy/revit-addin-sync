using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Ribbon command: shows which BINA Sync version is running and lets the
    /// drafter check for and install an update without waiting for the next
    /// Revit start. Once an update is installed, the window's last button
    /// closes Revit so it can apply. Needs no open model.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: with a forced update pending this opens the update
            // window instead, which already shows the version and the way out.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                var window = new UI.AboutWindow();
                Services.RevitWindowOwner.SetOwner(window, commandData.Application);
                window.ShowDialog();

                // "Close Revit now", after an update was installed from this
                // window. Posted once the dialog is gone; Revit still asks to
                // save open models.
                if (window.CloseRevitRequested && !App.PostExitRevit())
                    TaskDialog.Show("BINA Sync", "Close Revit and open it again to finish the update.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
