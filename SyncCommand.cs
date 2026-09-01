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
                Services.ModelLineage.LineageStamp stamp;
                bool stampReadable = Services.ModelLineage.TryRead(doc, out stamp);
                string lineageId = stamp?.LineageId;

                // ExtensibleStorage travels with SaveAs, so a copy carries the
                // original's identity. That used to be a dialog of its own; the
                // sync dialog now asks the same question better — it also lets
                // the user say WHICH model this is a new version of — so all
                // that is left here is refusing to reuse an inherited GUID for a
                // model syncing under its own name. `lineageKey` is derived from
                // the GUID, so an inherited one collides with the original's row
                // rather than starting a chain of its own.
                bool inheritedFromCopy = Services.ModelLineage.LooksLikeCopy(stamp, docPathName);

                // Null unless the user rolled back and has not published it yet.
                // Read here, on the UI thread, with the rest of the model identity.
                var rollbackMarker = Services.RollbackMarkerStore.Read(doc);

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

                    // Which GUID this sync carries. Joining a chain the user
                    // picked (or one this filename already lands in) means
                    // sending THAT chain's GUID — frequently null, which is
                    // correct: the server then inherits the head's. Sending this
                    // document's own instead would fork `lineageKey`. Nothing
                    // rejects that any more — the unique indexes over it were
                    // dropped so `targetLineageId` could ship — but their
                    // migration's down() refuses to restore them once duplicates
                    // exist, so forking would make the drop permanent.
                    string docGuidToSend;

                    if (options.JoinsExistingLineage)
                    {
                        docGuidToSend = options.LineageDocGuid;
                    }
                    else
                    {
                        // Stamp identity only once the user has committed to
                        // syncing — and only when the stamp was readable in the
                        // first place. A failed read that mints anyway gives one
                        // document two GUIDs over its life, which is the fork
                        // above with extra steps.
                        if (stampReadable && (string.IsNullOrEmpty(lineageId) || inheritedFromCopy))
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
                                // The GUID exists only in memory now. Sending it
                                // would stamp the server with an id this document
                                // will not carry next time, so send none and let
                                // the filename resolve the chain.
                                System.Diagnostics.Debug.WriteLine($"[BINA] Could not stamp lineage: {ex.Message}");
                                lineageId = null;
                            }
                        }

                        docGuidToSend = stampReadable ? lineageId : null;
                    }

                    var request = new Services.SyncRunner.Request
                    {
                        Api = api,
                        UploadPath = prepared.UploadPath,
                        FileName = Path.GetFileName(docPathName),
                        ProjectId = options.SelectedProjectId,
                        ParentId = options.SelectedFolderId,
                        DisciplineType = options.SelectedDiscipline,
                        DocGuid = docGuidToSend,
                        BaseVersion = options.BaseVersion,
                        Comment = options.Comment,
                        ClientInfo = clientInfo,
                        LinkedFiles = linkedFiles,
                        AccessToken = beToken,
                        // The chain the user picked. Null on an ordinary sync,
                        // where the server resolves the lineage from the filename
                        // exactly as before.
                        TargetLineageId = options.TargetLineageId,
                        TargetName = options.TargetName,
                        TargetFileHash = options.TargetFileHash,
                        // Set only when this model was restored by a rollback and
                        // the restore has not been published yet (86d3ut47q).
                        // Dropped when the user aimed this sync at a chain of
                        // their choosing: the marker carries a design id with no
                        // lineage attached, so it cannot be shown to belong to
                        // that chain, and a restore label on the wrong model's
                        // history is worse than no label.
                        RolledBackFromDesignId =
                            rollbackMarker != null && string.IsNullOrEmpty(options.TargetLineageId)
                                ? (int?)rollbackMarker.FromDesignId
                                : null
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

                    // The rollback has been published, so the marker has done its
                    // job. Left in place it would label every later version as a
                    // restore. Cleared only on a real new version: an "unchanged"
                    // result means nothing was published and the marker is still
                    // owed to a future sync.
                    if (rollbackMarker != null && runResult.Succeeded && !runResult.Unchanged)
                    {
                        Services.RollbackMarkerStore.Clear(doc);
                        // Clear opens a transaction, leaving doc dirty. Save so the
                        // next rollback attempt doesn't prompt about unsaved changes.
                        if (doc.IsModified) doc.Save();
                    }

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

            // Name the model whose history this joined — with the chain picked by
            // hand, "is now v8" alone does not say v8 of what.
            string where = string.IsNullOrEmpty(result.TargetName)
                           || string.Equals(result.TargetName, result.FileName, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $" of \"{result.TargetName}\"";

            TaskDialog.Show("Synced",
                $"{prepareAction}\n\n{result.FileName} is now v{result.Version}{where} in BINA.");
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