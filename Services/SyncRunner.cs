using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Drives one sync attempt: hash, init, upload, commit (ClickUp 86d3x42mz).
    ///
    /// Runs entirely off the Revit UI thread and touches no Revit API — every
    /// document-derived value is passed in. Failures come back as data for the
    /// caller to display, because opening a Revit dialog from here is exactly
    /// the crash risk the thread-affinity fix removed.
    /// </summary>
    public static class SyncRunner
    {
        public sealed class Request
        {
            public SyncApiClient Api { get; set; }
            public string AccessToken { get; set; }
            public string UploadPath { get; set; }
            public string FileName { get; set; }
            public int ProjectId { get; set; }
            public int? ParentId { get; set; }
            public string DisciplineType { get; set; }
            public string DocGuid { get; set; }
            public int? BaseVersion { get; set; }
            public string Comment { get; set; }
            public SyncClientInfo ClientInfo { get; set; }
            public List<LinkedFileInfo> LinkedFiles { get; set; }

            /// <summary>
            /// Chain the user picked in the sync dialog, or null for an ordinary
            /// sync. When set, the server must confirm it before a byte moves —
            /// see <see cref="RunAsync"/>.
            /// </summary>
            public string TargetLineageId { get; set; }

            /// <summary>
            /// Head file hash of the chain this sync is joining, when known.
            /// Lets an identical re-sync be answered without an upload even
            /// while bina-be's own unchanged check is switched off.
            /// </summary>
            public string TargetFileHash { get; set; }

            /// <summary>Name the picked chain currently goes by, for the outcome text.</summary>
            public string TargetName { get; set; }

            /// <summary>
            /// Design id this model was restored from, when the user rolled back
            /// and has not yet published the result (86d3ut47q). Null otherwise.
            /// </summary>
            public int? RolledBackFromDesignId { get; set; }
        }

        public sealed class Result
        {
            public bool Succeeded { get; set; }
            public bool Unchanged { get; set; }
            public int? Version { get; set; }
            public int? DesignId { get; set; }
            public string FileName { get; set; }
            public string Message { get; set; }
            /// <summary>Set when the server rejected the sync because someone else got there first.</summary>
            public SyncHead Conflict { get; set; }
            /// <summary>Chain the version landed in, when the server reported one.</summary>
            public string LineageId { get; set; }
            /// <summary>Name of the chain the user targeted, echoed for the outcome dialog.</summary>
            public string TargetName { get; set; }
        }

        public static async Task<Result> RunAsync(Request req)
        {
            // One id for this attempt, reused if we retry the commit. The unique
            // index on it is what stops a dropped connection from producing two
            // versions of the same upload.
            string syncSessionId = Guid.NewGuid().ToString("N");

            try
            {
                var fileInfo = new System.IO.FileInfo(req.UploadPath);
                string fileHash = SyncApiClient.ComputeFileHash(req.UploadPath);

                // The server's own unchanged check is currently switched off, so
                // an identical re-sync would presign, re-upload the whole central
                // and create a version indistinguishable from the last one. The
                // hash we already computed answers that here, for free.
                if (!string.IsNullOrEmpty(req.TargetFileHash)
                    && string.Equals(fileHash, req.TargetFileHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new Result
                    {
                        Succeeded = true,
                        Unchanged = true,
                        Version = req.BaseVersion,
                        FileName = req.FileName,
                        TargetName = req.TargetName,
                        LineageId = req.TargetLineageId
                    };
                }

                var init = await req.Api.InitAsync(new SyncInitRequest
                {
                    ProjectId = req.ProjectId,
                    DisciplineType = req.DisciplineType,
                    FileName = req.FileName,
                    ParentId = req.ParentId,
                    FileSize = fileInfo.Length,
                    FileHash = fileHash,
                    DocGuid = req.DocGuid,
                    BaseVersion = req.BaseVersion,
                    TargetLineageId = req.TargetLineageId
                }).ConfigureAwait(false);

                // Targeting a chain is the one case where getting it wrong is
                // expensive: the version would land under this file's own name as
                // a brand-new model, and the user would find out in Cloud Docs.
                // init writes no row, so refusing here costs nothing.
                if (!string.IsNullOrEmpty(req.TargetLineageId)
                    && !string.Equals(init.LineageId, req.TargetLineageId, StringComparison.OrdinalIgnoreCase))
                {
                    return new Result
                    {
                        Succeeded = false,
                        FileName = req.FileName,
                        TargetName = req.TargetName,
                        Message = string.IsNullOrEmpty(init.LineageId)
                            ? "BINA did not confirm which model this version belongs to, so nothing was uploaded. " +
                              "This server does not yet support syncing into a model you pick — sync under this " +
                              "file's own name instead, or ask for the BINA Cloud update."
                            : "BINA filed this sync against a different model than the one you picked, so nothing " +
                              "was uploaded. Reopen the sync dialog and choose the model again."
                    };
                }

                // The server already holds these exact bytes. Uploading a
                // multi-gigabyte central again to create an identical version is
                // the single most wasteful thing this command could do.
                if (init.Unchanged)
                {
                    return new Result
                    {
                        Succeeded = true,
                        Unchanged = true,
                        DesignId = init.DesignId,
                        Version = init.Head?.Version,
                        FileName = req.FileName,
                        TargetName = req.TargetName,
                        LineageId = init.LineageId
                    };
                }

                bool uploaded = await req.Api.UploadAsync(init.UploadUrl, req.UploadPath).ConfigureAwait(false);
                if (!uploaded)
                {
                    return new Result
                    {
                        Succeeded = false,
                        FileName = req.FileName,
                        Message = "The model could not be uploaded to BINA storage."
                    };
                }

                // Autodesk OSS gives the viewer URN. Translation itself stays
                // manual (users trigger it from the Design module, where it is
                // metered against the org's daily quota) — but bina-be refuses to
                // translate a design with no URN, so a sync that skipped this
                // would quietly disable that button for every model it uploaded.
                // Best-effort: a viewer that cannot be prepared must not fail the
                // sync, which is about the model bytes.
                string urn = null;
                try
                {
                    // AutodeskApiService is not IDisposable; it owns its own client.
                    var autodesk = new AutodeskApiService();
                    var autodeskResult = await autodesk
                        .UploadFileAsync(req.AccessToken, req.UploadPath, req.DisciplineType)
                        .ConfigureAwait(false);
                    urn = autodeskResult?.UrnInBase64;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Autodesk upload failed (non-fatal): {ex.Message}");
                }

                var commit = await req.Api.CommitAsync(new SyncCommitRequest
                {
                    ProjectId = req.ProjectId,
                    DisciplineType = req.DisciplineType,
                    FileName = req.FileName,
                    ParentId = req.ParentId,
                    FileSize = fileInfo.Length,
                    FileHash = fileHash,
                    DocGuid = req.DocGuid,
                    BaseVersion = req.BaseVersion,
                    // Repeated on commit: init and commit resolve the lineage
                    // independently, so omitting it here would file the bytes we
                    // just uploaded into a chain chosen by filename after all.
                    TargetLineageId = req.TargetLineageId,
                    // Server-issued: the add-in no longer invents object keys.
                    FileKey = init.FileKey,
                    SyncSessionId = syncSessionId,
                    Comment = req.Comment,
                    UrnInBase64 = urn,
                    ClientInfo = req.ClientInfo,
                    // Present only on the first sync after a rollback; the server
                    // labels the version it creates and the caller then clears the
                    // marker from the model (86d3ut47q).
                    RolledBackFromDesignId = req.RolledBackFromDesignId,
                    Metadata = req.LinkedFiles == null
                        ? null
                        : (object)new { linkedFiles = req.LinkedFiles }
                }).ConfigureAwait(false);

                return new Result
                {
                    Succeeded = true,
                    Unchanged = commit.Status == "unchanged",
                    Version = commit.Version,
                    DesignId = commit.DesignId,
                    FileName = commit.Name ?? req.FileName,
                    TargetName = req.TargetName,
                    LineageId = commit.LineageId ?? init.LineageId
                };
            }
            catch (SyncConflictException conflict)
            {
                return new Result
                {
                    Succeeded = false,
                    FileName = req.FileName,
                    Conflict = conflict.Head,
                    Message = conflict.Message
                };
            }
            catch (Exception ex)
            {
                return new Result
                {
                    Succeeded = false,
                    FileName = req.FileName,
                    Message = ex.Message
                };
            }
        }
    }
}
