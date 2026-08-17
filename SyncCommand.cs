using System;
using System.Collections.Generic;
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
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Error", "Save your Revit file once before syncing to BINA.");
                    return Result.Failed;
                }

                BinaConfig config = BinaConfig.Load();

                // Sync targets bina-be, which only accepts tokens it issued itself —
                // a bina-ai session from the "Login" button is rejected there.
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before syncing.\n\n" +
                        "This is a separate sign-in from the Login button used by Copilot, JKR and space planning.");
                    return Result.Cancelled;
                }

                // ---- Model identity (Revit API — UI thread only) ------------------
                string docPathName = doc.PathName;
                var stamp = Services.ModelLineage.Read(doc);
                string lineageId = stamp?.LineageId;

                if (Services.ModelLineage.LooksLikeCopy(stamp, docPathName))
                {
                    // ExtensibleStorage travels with SaveAs, so a copy carries the
                    // original's identity. Filing it as the next version would
                    // deactivate a different building's current model.
                    var copyDialog = new TaskDialog("New model or new version?")
                    {
                        MainInstruction = "This model was saved from another BINA-synced model.",
                        MainContent =
                            $"It still carries the identity of:\n{stamp.OriginPath}\n\n" +
                            "Sync it as a NEW model, or as the next version of that one?",
                        CommonButtons = TaskDialogCommonButtons.Cancel
                    };
                    copyDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "New model",
                        "Start its own version history. Recommended for a SaveAs copy.");
                    copyDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "New version of the original",
                        "Continue the original model's history.");

                    switch (copyDialog.Show())
                    {
                        case TaskDialogResult.CommandLink1:
                            lineageId = null;   // minted fresh below
                            break;
                        case TaskDialogResult.CommandLink2:
                            break;              // keep the inherited id
                        default:
                            return Result.Cancelled;
                    }
                }

                var clientInfo = Services.DocumentPreparer.DescribeClient(doc);
                List<LinkedFileInfo> linkedFiles = ExtractRevitLinks(doc);

                // ---- Make disk match screen (Revit API — UI thread only) ----------
                Services.DocumentPreparer.PreparedDocument prepared;
                try
                {
                    prepared = Services.DocumentPreparer.Prepare(doc);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Could not prepare the model", ex.Message);
                    return Result.Failed;
                }

                // ---- Confirm destination ------------------------------------------
                // Refresh up front if the token is near expiry, and again on any
                // 401 mid-sync — a large upload can outlive its token.
                string beToken = Services.BinaCloudSession.EnsureValidTokenAsync(config)
                    .GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(beToken))
                {
                    TaskDialog.Show("Session Expired",
                        "Your Cloud Docs session has expired. Click 'Login to Cloud Docs' and try again.");
                    return Result.Cancelled;
                }

                using (var api = new Services.SyncApiClient(
                    config.ResolvedApiBaseUrl,
                    beToken,
                    http: null,
                    refreshToken: () => Services.BinaCloudSession.RefreshAsync(config)))
                {
                    var options = new SyncOptionsWindow(
                        api,
                        Path.GetFileName(docPathName),
                        lineageId,
                        config.ProjectId,
                        config.ProjectName,
                        GetDisciplineTypeFromFileName(Path.GetFileName(docPathName)));

                    Services.RevitWindowOwner.SetOwner(options, commandData.Application);

                    if (options.ShowDialog() != true)
                    {
                        CleanupTemp(prepared);
                        return Result.Cancelled;
                    }

                    // Remember the project so the next sync defaults to it.
                    if (options.SelectedProjectId != config.ProjectId)
                    {
                        config.ProjectId = options.SelectedProjectId;
                        config.ProjectName = options.SelectedProjectName;
                        config.Save();
                    }

                    // Stamp identity only once the user has committed to syncing.
                    if (string.IsNullOrEmpty(lineageId))
                    {
                        lineageId = Services.ModelLineage.NewLineageId();
                        try
                        {
                            using (var t = new Transaction(doc, "BINA: stamp model identity"))
                            {
                                t.Start();
                                Services.ModelLineage.Write(doc, lineageId, docPathName);
                                t.Commit();
                            }

                            // The stamp is a document change, and it lands after
                            // Prepare has already saved. Left unsaved, the bytes we
                            // upload would not contain it, and the next sync would
                            // have to save — producing different bytes and a new
                            // version even though the user changed nothing. Save
                            // again so what we hash is what is on disk.
                            if (!prepared.IsTemporary && doc.IsModified)
                                doc.Save();
                        }
                        catch (Exception ex)
                        {
                            // Not fatal: the server falls back to filename lineage.
                            System.Diagnostics.Debug.WriteLine($"[BINA] Could not stamp lineage: {ex.Message}");
                        }
                    }

                    var request = new Services.SyncRunner.Request
                    {
                        Api = api,
                        UploadPath = prepared.UploadPath,
                        FileName = Path.GetFileName(docPathName),
                        ProjectId = options.SelectedProjectId,
                        ParentId = options.SelectedFolderId,
                        DisciplineType = options.SelectedDiscipline,
                        DocGuid = lineageId,
                        BaseVersion = options.BaseVersion,
                        Comment = options.Comment,
                        ClientInfo = clientInfo,
                        LinkedFiles = linkedFiles,
                        AccessToken = beToken
                    };

                    // Blocks the UI thread. The upload itself touches no Revit API,
                    // which is the part that matters for stability; a modeless
                    // progress window is tracked separately.
                    Services.SyncRunner.Result runResult;
                    try
                    {
                        runResult = Task.Run(() => Services.SyncRunner.RunAsync(request)).Result;
                    }
                    catch (AggregateException aex)
                    {
                        var inner = aex.InnerException ?? aex;
                        TaskDialog.Show("Sync failed", inner.Message);
                        CleanupTemp(prepared);
                        return Result.Failed;
                    }

                    CleanupTemp(prepared);
                    ShowOutcome(runResult, prepared.Action);
                    return runResult.Succeeded ? Result.Succeeded : Result.Failed;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        private static void CleanupTemp(Services.DocumentPreparer.PreparedDocument prepared)
        {
            if (prepared == null || !prepared.IsTemporary) return;
            try { if (File.Exists(prepared.UploadPath)) File.Delete(prepared.UploadPath); }
            catch { /* a leftover temp file is not worth surfacing */ }
        }

        private static void ShowOutcome(Services.SyncRunner.Result result, string prepareAction)
        {
            if (result.Conflict != null)
            {
                string who = result.Conflict.UploadedAt.HasValue
                    ? result.Conflict.UploadedAt.Value.ToLocalTime().ToString("d MMM HH:mm")
                    : "recently";
                TaskDialog.Show("Someone else synced first",
                    $"BINA now has v{result.Conflict.Version}, uploaded {who}.\n\n" +
                    "Download the latest version before syncing again, so their changes are not lost.");
                return;
            }

            if (!result.Succeeded)
            {
                TaskDialog.Show("Sync failed", result.Message ?? "Unknown error.");
                return;
            }

            if (result.Unchanged)
            {
                TaskDialog.Show("Nothing to sync",
                    $"This model is identical to v{result.Version} already in BINA, so no new version was created.");
                return;
            }

            TaskDialog.Show("Synced",
                $"{prepareAction}\n\n{result.FileName} is now v{result.Version} in BINA.");
        }


        private static string GetDisciplineTypeFromFileName(string fileName)
            => Services.DisciplineTypes.FromFileName(fileName);

        private static List<LinkedFileInfo> ExtractRevitLinks(Document doc)
        {
            var linkedFiles = new List<LinkedFileInfo>();
            
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Extracting Revit links...");
                
                // Get all RevitLinkTypes (the link definitions)
                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                
                foreach (RevitLinkType linkType in collector)
                {
                    try
                    {
                        string linkName = linkType.Name;
                        System.Diagnostics.Debug.WriteLine($"[BINA] Found link: {linkName}");
                        
                        // Get the external file reference to get path information
                        ExternalFileReference extRef = linkType.GetExternalFileReference();
                        if (extRef != null)
                        {
                            ModelPath modelPath = extRef.GetPath();
                            string absolutePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                            
                            // Extract just the filename 
                            string fileName = !string.IsNullOrEmpty(absolutePath) 
                                ? Path.GetFileName(absolutePath) 
                                : linkName;
                                
                            // For relative path, try to get the stored relative path or fallback to filename
                            string relPath = fileName; // Default to filename
                            
                            // Try to get relative path from the stored path information
                            try
                            {
                                // Check if the path is relative by examining the converted path
                                if (!string.IsNullOrEmpty(absolutePath) && !absolutePath.Contains(":\\"))
                                {
                                    // Likely a relative path
                                    relPath = absolutePath;
                                }
                                else if (!string.IsNullOrEmpty(absolutePath))
                                {
                                    // It's an absolute path, use just the filename
                                    relPath = fileName;
                                }
                            }
                            catch
                            {
                                relPath = fileName; // Fallback to filename if any error
                            }
                            
                            linkedFiles.Add(new LinkedFileInfo
                            {
                                FileName = fileName,
                                RelativePath = relPath,
                                DisciplineType = GetDisciplineTypeFromFileName(fileName)
                            });
                            
                            System.Diagnostics.Debug.WriteLine($"[BINA] Link added - FileName: {fileName}, RelativePath: {relPath}");
                        }
                        else
                        {
                            // If no external reference, just use the name
                            linkedFiles.Add(new LinkedFileInfo
                            {
                                FileName = linkName,
                                RelativePath = linkName,
                                DisciplineType = GetDisciplineTypeFromFileName(linkName)
                            });
                            
                            System.Diagnostics.Debug.WriteLine($"[BINA] Link added (no external ref) - FileName: {linkName}");
                        }
                    }
                    catch (Exception linkEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] Error processing link {linkType.Name}: {linkEx.Message}");
                        // Continue processing other links
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[BINA] Total links extracted: {linkedFiles.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Error extracting Revit links: {ex.Message}");
            }
            
            return linkedFiles;
        }
    }
}