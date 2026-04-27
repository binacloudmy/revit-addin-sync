using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaConnector.Commands
{
    /// <summary>"Project Settings" ribbon command. Active project + upload preferences.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var config = BinaConfig.Load();
                if (!config.IsLoggedIn())
                {
                    TaskDialog.Show("Sign in required",
                        "Please sign in to BINA Cloud first using the 'Sign In / Account' button.");
                    return Result.Cancelled;
                }

                var settings = SettingsStore.Load();
                var window = new ProjectSettingsWindow(config, settings);
                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA", $"Could not open settings: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
