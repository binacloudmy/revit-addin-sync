using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaConnector.Commands
{
    /// <summary>"Upload to BINA" ribbon command. Uploads the active document to BINA Cloud.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class UploadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!EnsureEulaAccepted()) return Result.Cancelled;

                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("BINA", "No active Revit document found.");
                    return Result.Failed;
                }
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("BINA", "Please save your Revit file before uploading to BINA.");
                    return Result.Failed;
                }

                BinaConfig config = BinaConfig.Load();
                if (!config.IsLoggedIn())
                {
                    TaskDialog.Show("Sign in required",
                        "Please sign in to BINA Cloud first using the 'Sign In / Account' button.");
                    return Result.Cancelled;
                }

                Settings settings = SettingsStore.Load();

                // Resolve discipline: from settings if a default is set, otherwise prompt the user.
                string selectedDiscipline = settings.DefaultDiscipline;
                if (string.IsNullOrEmpty(selectedDiscipline) || selectedDiscipline == "Ask")
                {
                    selectedDiscipline = PromptDiscipline(doc);
                    if (selectedDiscipline == null) return Result.Cancelled;
                }

                if (settings.ConfirmBeforeUploading && !ConfirmUpload(doc, config, selectedDiscipline))
                {
                    return Result.Cancelled;
                }

                using var binaService = new BinaApiService();
                SyncResultData resultData;
                try
                {
                    resultData = Task.Run(() =>
                        UploadToMultiplePlatforms(doc, config.AccessToken, binaService, selectedDiscipline, config))
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Upload failed",
                        "Could not complete the upload to BINA Cloud.\n\n" +
                        "Check your internet connection and try again. If the problem continues, " +
                        $"contact support.\n\nDetails: {ex.Message}");
                    return Result.Failed;
                }

                if (resultData != null)
                {
                    try { new SyncResultsWindow(resultData).ShowDialog(); }
                    catch
                    {
                        TaskDialog.Show("Upload Results",
                            $"File: {resultData.FileName}\nDiscipline: {resultData.DisciplineType}\n" +
                            $"BINA Storage: {(resultData.BinaObsSuccess ? "Success" : "Failed")}\n" +
                            $"Autodesk Viewer: {(resultData.AutodeskOssSuccess ? "Ready" : "Failed")}\n" +
                            $"Registration: {(resultData.RegistrationSuccess ? "Saved" : "Failed")}");
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA", $"An unexpected error occurred: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static bool EnsureEulaAccepted()
        {
            if (EulaService.HasAccepted()) return true;
            var dlg = new EulaWindow();
            bool? result = dlg.ShowDialog();
            if (result == true && dlg.Accepted)
            {
                EulaService.RecordAcceptance();
                return true;
            }
            return false;
        }

        private static bool ConfirmUpload(Document doc, BinaConfig config, string discipline)
        {
            var dialog = new TaskDialog("Confirm upload")
            {
                MainInstruction = $"Upload {Path.GetFileName(doc.PathName)} to BINA?",
                MainContent = $"Project: {config.ProjectName}\nDiscipline: {discipline}",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            return dialog.Show() == TaskDialogResult.Yes;
        }

        private static string PromptDiscipline(Document doc)
        {
            var dialog = new TaskDialog("Select discipline")
            {
                MainInstruction = "Which discipline does this file belong to?",
                MainContent = $"File: {Path.GetFileName(doc.PathName)}\n\nClick 'OK' for MainFile / general model.",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Architecture", "Walls, doors, windows, layouts.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Structure", "Beams, columns, foundations.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "HVAC", "Mechanical systems.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Electrical", "Electrical, lighting, power.");

            return dialog.Show() switch
            {
                TaskDialogResult.CommandLink1 => "Architecture",
                TaskDialogResult.CommandLink2 => "Structure",
                TaskDialogResult.CommandLink3 => "HVAC",
                TaskDialogResult.CommandLink4 => "Electrical",
                TaskDialogResult.Ok => "MainFile",
                _ => null
            };
        }

        private static async Task<SyncResultData> UploadToMultiplePlatforms(
            Document doc, string binaAccessToken, BinaApiService binaService, string disciplineType, BinaConfig config)
        {
            using var autodeskService = new AutodeskApiService();

            var fileParams = binaService.GetFileParameters(doc.PathName);
            if (string.IsNullOrEmpty(fileParams.key))
            {
                TaskDialog.Show("Upload failed", "Could not read the file to upload.");
                return null;
            }

            string presignedUrl = await binaService.GetPresignedUrlAsync(
                binaAccessToken, fileParams.key, fileParams.size, fileParams.mimeType);
            if (string.IsNullOrEmpty(presignedUrl))
            {
                TaskDialog.Show("Upload failed", "BINA Cloud did not return an upload URL. Please try again.");
                return null;
            }

            bool obsUploadSuccess = await binaService.UploadFileAsync(presignedUrl, doc.PathName, fileParams.mimeType);
            if (!obsUploadSuccess)
            {
                TaskDialog.Show("Upload failed", "Could not upload the file to BINA storage.");
                return null;
            }

            var autodeskUploadResult = await autodeskService.UploadFileAsync(
                binaAccessToken, doc.PathName, disciplineType, _ => { });

            string cleanFileUrl = presignedUrl.Split('?')[0].Replace(":443", "");
            var saveFileDto = new SaveFederatedFileDto
            {
                ProjectId = config.ProjectId,
                Name = Path.GetFileName(doc.PathName),
                FileUrl = cleanFileUrl,
                FileKey = fileParams.key,
                FileSize = fileParams.size,
                FileType = "rvt",
                UploadedBy = config.UserId,
                UrnInBase64 = autodeskUploadResult?.UrnInBase64,
                DisciplineType = disciplineType,
                Metadata = new FederatedFileMetadata { LinkedFiles = ExtractRevitLinks(doc) }
            };

            var saveResult = await binaService.SaveFederatedFileAsync(binaAccessToken, saveFileDto);

            return new SyncResultData
            {
                FileName = Path.GetFileName(doc.PathName),
                DisciplineType = disciplineType,
                FileSize = fileParams.size,
                Version = saveResult.Data?.Version,
                BinaObsSuccess = obsUploadSuccess,
                BinaLocation = fileParams.key,
                AutodeskOssSuccess = autodeskUploadResult != null,
                AutodeskUrn = autodeskUploadResult?.UrnInBase64,
                RegistrationSuccess = saveResult.Success,
                LinkedFiles = ExtractRevitLinks(doc),
                ErrorMessage = GetErrorMessage(autodeskUploadResult, saveResult)
            };
        }

        private static string GetErrorMessage(AutodeskUploadResult autodeskResult, SaveFederatedFileResponseDto saveResult)
        {
            var errors = new List<string>();
            if (autodeskResult == null) errors.Add("Autodesk Viewer functionality will be limited (OSS upload failed).");
            if (!saveResult.Success) errors.Add($"Backend registration failed: {saveResult.Message}");
            return errors.Count > 0 ? string.Join("\n\n", errors) : null;
        }

        private static string GetDisciplineFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "MainFile";
            string upper = fileName.ToUpper();
            if (upper.StartsWith("ARCHITECTURE")) return "Architecture";
            if (upper.StartsWith("STRUCTURE")) return "Structure";
            if (upper.StartsWith("HVAC")) return "HVAC";
            if (upper.StartsWith("ELECTRICAL")) return "Electrical";
            return "MainFile";
        }

        private static List<LinkedFileInfo> ExtractRevitLinks(Document doc)
        {
            var linkedFiles = new List<LinkedFileInfo>();
            try
            {
                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                foreach (RevitLinkType linkType in collector)
                {
                    try
                    {
                        string linkName = linkType.Name;
                        ExternalFileReference extRef = linkType.GetExternalFileReference();
                        if (extRef != null)
                        {
                            string absolutePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetPath());
                            string fileName = !string.IsNullOrEmpty(absolutePath) ? Path.GetFileName(absolutePath) : linkName;
                            string relPath = (!string.IsNullOrEmpty(absolutePath) && !absolutePath.Contains(":\\"))
                                ? absolutePath
                                : fileName;
                            linkedFiles.Add(new LinkedFileInfo
                            {
                                FileName = fileName,
                                RelativePath = relPath,
                                DisciplineType = GetDisciplineFromFileName(fileName)
                            });
                        }
                        else
                        {
                            linkedFiles.Add(new LinkedFileInfo
                            {
                                FileName = linkName,
                                RelativePath = linkName,
                                DisciplineType = GetDisciplineFromFileName(linkName)
                            });
                        }
                    }
                    catch { /* skip individual link errors */ }
                }
            }
            catch { /* skip extraction errors entirely */ }
            return linkedFiles;
        }
    }
}
