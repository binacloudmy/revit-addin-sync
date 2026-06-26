using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LoginCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var config = BinaConfig.Load();

                if (config.IsLoggedIn())
                {
                    // Show current user info
                    var userInfoWindow = new UserInfoWindow(config);
                    var result = userInfoWindow.ShowDialog();

                    if (result == true)
                    {
                        if (userInfoWindow.LoggedOut)
                        {
                            // Clear session and save
                            config.ClearSession();
                            config.Save();
                            TaskDialog.Show("Logged Out", "You have been logged out successfully.");
                        }
                        else if (userInfoWindow.SwitchProject)
                        {
                            // Show project picker with existing token
                            ShowProjectPicker(config);
                        }
                    }
                }
                else
                {
                    // Show login dialog
                    var loginWindow = new LoginWindow(config.Email);
                    var loginResult = loginWindow.ShowDialog();

                    if (loginResult == true)
                    {
                        // Update config with login info
                        config.Email = loginWindow.Email;
                        config.Password = loginWindow.Password;
                        config.AccessToken = loginWindow.AccessToken;
                        config.RefreshToken = loginWindow.RefreshToken;
                        config.TokenExpiry = loginWindow.TokenExpiry;
                        config.UserId = loginWindow.UserId;
                        if (loginWindow.OrgId.HasValue) config.OrgId = loginWindow.OrgId;
                        config.UserName = loginWindow.Email; // Use email as username for now

                        // Show project picker
                        var projectPicker = new ProjectPickerWindow(loginWindow.AccessToken);
                        var projectResult = projectPicker.ShowDialog();

                        if (projectResult == true)
                        {
                            config.ProjectId = projectPicker.SelectedProjectId;
                            config.ProjectName = projectPicker.SelectedProjectName;
                            config.Save();

                            // Reconnect the WSS tunnel with the real JWT so the
                            // backend can attribute AI usage to this user.
                            App.RestartVibeTunnel(config.AccessToken);

                            TaskDialog.Show("Login Successful",
                                $"Logged in as: {config.UserName}\nProject: {config.ProjectName}");
                        }
                        else
                        {
                            // User cancelled project selection, don't save
                            TaskDialog.Show("Login Cancelled", "Login was successful but no project was selected.");
                        }
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Login failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void ShowProjectPicker(BinaConfig config)
        {
            var projectPicker = new ProjectPickerWindow(config.AccessToken);
            var result = projectPicker.ShowDialog();

            if (result == true)
            {
                config.ProjectId = projectPicker.SelectedProjectId;
                config.ProjectName = projectPicker.SelectedProjectName;
                config.Save();

                TaskDialog.Show("Project Changed",
                    $"Switched to project: {config.ProjectName}");
            }
        }
    }
}
