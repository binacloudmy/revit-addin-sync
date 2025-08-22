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

                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        string shortToken = accessToken.Length > 50 ? accessToken.Substring(0, 50) + "..." : accessToken;
                        TaskDialog.Show("Login Success!", $"Successfully logged in to BINA!\n\nAccess Token:\n{shortToken}");
                        
                        // Now get presigned URL for the current Revit file
                        var fileParams = binaService.GetFileParameters(doc.PathName);
                        if (fileParams.key != null && fileParams.size > 0)
                        {
                            var presignedUrlTask = Task.Run(() => binaService.GetPresignedUrlAsync(accessToken, fileParams.key, fileParams.size, fileParams.mimeType));
                            string presignedUrl = presignedUrlTask.Result;
                            if (!string.IsNullOrEmpty(presignedUrl))
                            {
                                // Show upload progress dialog
                                TaskDialog uploadDialog = new TaskDialog("BINA Upload");
                                uploadDialog.MainContent = $"Uploading {Path.GetFileName(doc.PathName)} to BINA...";
                                uploadDialog.CommonButtons = TaskDialogCommonButtons.Ok;
                                uploadDialog.DefaultButton = TaskDialogResult.Ok;
                                uploadDialog.Show();
                                
                                // Start upload
                                var uploadTask = Task.Run(() => binaService.UploadFileAsync(presignedUrl, doc.PathName, fileParams.mimeType));
                                bool uploadSuccess = uploadTask.Result;
                                
                                if (uploadSuccess)
                                {
                                    TaskDialog.Show("Upload Success!", $"File uploaded successfully to BINA!\n\nFile: {Path.GetFileName(doc.PathName)}\nLocation: {fileParams.key}\nSize: {fileParams.size} bytes\n\nYour file is now available in the BINA cloud.");
                                }
                                else
                                {
                                    TaskDialog.Show("Upload Failed", "Failed to upload file to BINA.\n\nCheck the log file on Desktop for more details.");
                                }
                            }
                            else
                            {
                                TaskDialog.Show("Presigned URL Failed", "Failed to obtain presigned URL.\n\nCheck the log file on Desktop for more details.");
                            }
                        }
                        else
                        {
                            TaskDialog.Show("File Error", "Failed to calculate file parameters for the Revit file.");
                        }
                    }
                    else
                    {
                        TaskDialog.Show("Login Failed", "Failed to login to BINA.\n\nPossible issues:\n- Invalid credentials\n- Network connectivity\n\nCheck the log file on Desktop for more details.");
                    }
                    
                    binaService.Dispose();
                }
                catch (AggregateException aex)
                {
                    binaService.Dispose();
                    var innerEx = aex.InnerException ?? aex;
                    TaskDialog.Show("Error", $"Upload failed: {innerEx.Message}\n\nFull error: {innerEx.GetType().Name}");
                }
                catch (Exception ex)
                {
                    binaService.Dispose();
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