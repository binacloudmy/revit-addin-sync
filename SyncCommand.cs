using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Add-in started executing");
                // Get the current Revit document
                Document doc = commandData.Application.ActiveUIDocument.Document;
                
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                // Check if the document is saved
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Error", "Please save your Revit file before syncing to BINA.");
                    return Result.Failed;
                }

                // Hardcoded credentials for testing
                BinaConfig config = new BinaConfig
                {
                    Email = "ammar@bina.cloud",
                    Password = "Passw0rd"
                };

                // Show progress message
                TaskDialog progressDialog = new TaskDialog("BINA Login");
                progressDialog.MainContent = "Logging in to BINA...";
                progressDialog.CommonButtons = TaskDialogCommonButtons.Ok;
                progressDialog.DefaultButton = TaskDialogResult.Ok;
                
                // Start login in background
                var binaService = new BinaApiService(config.Email, config.Password);
                var loginTask = Task.Run(() => binaService.LoginAsync());
                
                // Show the progress dialog and wait for user to click or login to complete
                progressDialog.Show();
                
                // Wait for login to complete
                try
                {
                    string accessToken = loginTask.Result;
                    binaService.Dispose();

                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        string shortToken = accessToken.Length > 50 ? accessToken.Substring(0, 50) + "..." : accessToken;
                        TaskDialog.Show("Login Success!", $"Successfully logged in to BINA!\n\nAccess Token:\n{shortToken}");
                    }
                    else
                    {
                        TaskDialog.Show("Login Failed", "Failed to login to BINA.\n\nPossible issues:\n- Invalid credentials\n- Network connectivity\n\nCheck the log file on Desktop for more details.");
                    }
                }
                catch (AggregateException aex)
                {
                    var innerEx = aex.InnerException ?? aex;
                    TaskDialog.Show("Error", $"Upload failed: {innerEx.Message}\n\nFull error: {innerEx.GetType().Name}");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Error", $"Upload failed: {ex.Message}\n\nError type: {ex.GetType().Name}");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}