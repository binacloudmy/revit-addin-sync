using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    /// <summary>
    /// Ribbon "BINA Profile": shows the bina-be account (BeUserName, not the
    /// bina-ai config.UserName) with a sign-out option. Signed out: opens the
    /// normal BINA login.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BinaProfileCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            var config = BinaConfig.Load();
            if (!config.IsBinaCloudLoggedIn())
                return new BinaCloudLoginCommand().Execute(commandData, ref message, elements);

            string who = string.IsNullOrWhiteSpace(config.BeUserName) ? "your BINA account" : config.BeUserName;
            var dialog = new TaskDialog("BINA")
            {
                MainInstruction = "Signed in as " + who,
                MainContent = (string.IsNullOrWhiteSpace(config.Email) ? "" : config.Email + "\n") +
                    "Project: " + (string.IsNullOrWhiteSpace(config.ProjectName) ? "none selected" : config.ProjectName),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Switch project",
                "Pre-selects this project in the sync dialog. You can still change it each time.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sign out from BINA",
                "You'll stay signed in to BINA AI for Copilot, JKR and space planning.");

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    BinaCloudLoginCommand.ShowProjectPicker(config, commandData.Application);
                    break;
                case TaskDialogResult.CommandLink2:
                    BinaCloudLoginCommand.SignOut(config);
                    break;
            }
            return Result.Succeeded;
        }
    }
}
