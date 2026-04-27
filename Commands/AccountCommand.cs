using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaConnector.Commands
{
    /// <summary>"Sign In / Account" ribbon command. Shows account info if signed in, otherwise the login dialog.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AccountCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var config = BinaConfig.Load();

                if (config.IsLoggedIn())
                {
                    var accountWindow = new UserInfoWindow(config);
                    if (accountWindow.ShowDialog() == true)
                    {
                        if (accountWindow.LoggedOut)
                        {
                            config.ClearSession();
                            config.Save();
                            TaskDialog.Show("Signed out", "You have been signed out of BINA Cloud.");
                        }
                        else if (accountWindow.SwitchProject)
                        {
                            ShowProjectPicker(config);
                        }
                    }
                }
                else
                {
                    var loginWindow = new LoginWindow(config.UserName);
                    if (loginWindow.ShowDialog() == true)
                    {
                        config.AccessToken = loginWindow.AccessToken;
                        config.TokenExpiry = loginWindow.TokenExpiry;
                        config.UserId = loginWindow.UserId;
                        config.UserName = loginWindow.Email;
                        config.SetRefreshToken(loginWindow.RefreshToken);

                        var projectPicker = new ProjectPickerWindow(loginWindow.AccessToken);
                        if (projectPicker.ShowDialog() == true)
                        {
                            config.ProjectId = projectPicker.SelectedProjectId;
                            config.ProjectName = projectPicker.SelectedProjectName;
                            config.Save();
                            TaskDialog.Show("Signed in",
                                $"Signed in as {config.UserName}\nProject: {config.ProjectName}");
                        }
                        else
                        {
                            TaskDialog.Show("Sign in",
                                "Sign-in succeeded but no project was selected. Use 'Project Settings' to choose one.");
                        }
                    }
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA", $"Sign-in failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void ShowProjectPicker(BinaConfig config)
        {
            var projectPicker = new ProjectPickerWindow(config.AccessToken);
            if (projectPicker.ShowDialog() == true)
            {
                config.ProjectId = projectPicker.SelectedProjectId;
                config.ProjectName = projectPicker.SelectedProjectName;
                config.Save();
                TaskDialog.Show("Project changed", $"Active project: {config.ProjectName}");
            }
        }
    }
}
