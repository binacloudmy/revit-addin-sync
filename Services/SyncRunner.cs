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

                var init = await req.Api.InitAsync(new SyncInitRequest
                {
                    ProjectId = req.ProjectId,
                    DisciplineType = req.DisciplineType,
                    FileName = req.FileName,
                    ParentId = req.ParentId,
                    FileSize = fileInfo.Length,
                    FileHash = fileHash,
                    DocGuid = req.DocGuid,
                    BaseVersion = req.BaseVersion
                }).ConfigureAwait(false);

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
                        FileName = req.FileName
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
                    // Server-issued: the add-in no longer invents object keys.
                    FileKey = init.FileKey,
                    SyncSessionId = syncSessionId,
                    Comment = req.Comment,
                    UrnInBase64 = urn,
                    ClientInfo = req.ClientInfo,
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
                    FileName = commit.Name ?? req.FileName
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
