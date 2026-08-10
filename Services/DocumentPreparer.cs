using System;
using System.IO;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Gets the bytes on disk to match what the user sees on screen before a sync
    /// reads them (ClickUp 86d3x42mz).
    ///
    /// The add-in previously uploaded whatever happened to be on disk: there was
    /// no Save, no SynchronizeWithCentral, no SaveAs anywhere in the codebase. A
    /// user who had been modelling for an hour without saving would sync an hour
    /// out of date and the version record would claim otherwise.
    ///
    /// For a workshared model the live central must not be uploaded directly —
    /// it is a shared file other people are writing to. Sync-with-central first,
    /// then upload a detached copy.
    ///
    /// Everything here touches the Revit API and must run on the UI thread.
    /// </summary>
    public static class DocumentPreparer
    {
        public sealed class PreparedDocument
        {
            /// <summary>Path the bytes should be read from — may be a temp detached copy.</summary>
            public string UploadPath { get; set; }
            /// <summary>True when UploadPath is a temp file the caller should delete.</summary>
            public bool IsTemporary { get; set; }
            /// <summary>What was done, for the results window.</summary>
            public string Action { get; set; }
        }

        public static PreparedDocument Prepare(Document doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrEmpty(doc.PathName))
                throw new InvalidOperationException("Save the model before syncing to BINA.");

            if (doc.IsWorkshared)
            {
                // Push local edits to central so the uploaded copy reflects the
                // coordinated state, not just this user's local.
                var syncOptions = new SynchronizeWithCentralOptions();
                var relinquish = new RelinquishOptions(false)
                {
                    StandardWorksets = true,
                    ViewWorksets = true,
                    FamilyWorksets = true,
                    UserWorksets = true,
                    CheckedOutElements = true
                };
                syncOptions.SetRelinquishOptions(relinquish);
                syncOptions.Comment = "BINA sync";

                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOptions);

                // Upload a detached copy: the central file is shared and may be
                // written to mid-read, and its worksharing state is meaningless
                // to anyone downloading it.
                string tempPath = Path.Combine(
                    Path.GetTempPath(),
                    $"bina_sync_{Guid.NewGuid():N}{Path.GetExtension(doc.PathName)}");

                var saveAs = new SaveAsOptions { OverwriteExistingFile = true };
                var worksharing = new WorksharingSaveAsOptions
                {
                    SaveAsCentral = false,
                    OpenWorksetsDefault = SimpleWorksetConfiguration.AllWorksets
                };
                saveAs.SetWorksharingOptions(worksharing);

                doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(tempPath), saveAs);

                return new PreparedDocument
                {
                    UploadPath = tempPath,
                    IsTemporary = true,
                    Action = "Synchronised with central and uploaded a detached copy."
                };
            }

            // Non-workshared: a plain save is enough to make disk match screen.
            doc.Save();

            return new PreparedDocument
            {
                UploadPath = doc.PathName,
                IsTemporary = false,
                Action = "Saved the model before uploading."
            };
        }

        /// <summary>Revit build + add-in version + worksharing state, stored on the version.</summary>
        public static SyncClientInfo DescribeClient(Document doc)
        {
            var app = doc?.Application;
            string workset = null;
            try
            {
                if (doc != null && doc.IsWorkshared)
                {
                    var active = doc.GetWorksetTable().GetActiveWorksetId();
                    workset = doc.GetWorksetTable().GetWorkset(active)?.Name;
                }
            }
            catch
            {
                // Worksharing details are nice to have, never worth failing a sync.
            }

            return new SyncClientInfo
            {
                RevitVersion = app?.VersionNumber,
                RevitBuild = app?.VersionBuild,
                AddinVersion = typeof(DocumentPreparer).Assembly.GetName().Version?.ToString(),
                IsWorkshared = doc?.IsWorkshared ?? false,
                ActiveWorkset = workset,
                MachineName = Environment.MachineName
            };
        }
    }
}
