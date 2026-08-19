using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Replaces the open model with a downloaded earlier version (ClickUp 86d3ut47q).
    ///
    /// This is the only document-lifecycle code in the add-in, and the ordering is
    /// forced rather than chosen:
    ///
    ///     1. open the downloaded copy and make it active
    ///        — Revit refuses to close the ACTIVE document, so something else has
    ///          to be active before step 2 is even legal
    ///     2. close the document that used to be active
    ///        — which also releases its file, so step 3 can overwrite it
    ///     3. stamp the rollback marker, BEFORE saving, so the bytes written in
    ///        step 4 already carry it — a marker held only in memory is lost if
    ///        the user closes without saving
    ///     4. save the restored model back over the ORIGINAL path
    ///        — without this the user is working out of a cache directory: their
    ///          real file still holds the old version, the next sync would save
    ///          into %LocalAppData%, and the changed path would make the lineage
    ///          check prompt "new model or new version?" and risk forking the chain
    ///
    /// Runs through an ExternalEvent because the download that produces the file
    /// finishes on a thread-pool thread while every call below is Revit API. The
    /// caller must RAISE THIS WITH NO MODAL DIALOG OPEN: ExternalEvents are
    /// serviced from Revit's Idling loop, which does not run while a modal window
    /// is up, so a picker still inside ShowDialog() would wait forever.
    /// </summary>
    public class RollbackSwapHandler : IExternalEventHandler
    {
        /// <summary>Full path of the downloaded .rvt to open. Set before raising.</summary>
        public string DownloadedPath { get; set; }

        /// <summary>Design id the bytes came from, stamped for the next sync.</summary>
        public int FromDesignId { get; set; }

        /// <summary>Version number the bytes came from — display only.</summary>
        public int FromVersion { get; set; }

        /// <summary>
        /// Called on the Revit thread when the swap ends, successfully or not.
        /// Consumers touching WPF must marshal to their own dispatcher.
        /// </summary>
        public Action<bool, string> OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            // Take and clear everything up front: a handler that throws must not
            // replay this swap the next time anything raises the event.
            string path = DownloadedPath;
            int fromDesignId = FromDesignId;
            int fromVersion = FromVersion;
            var callback = OnCompleted;

            DownloadedPath = null;
            FromDesignId = 0;
            FromVersion = 0;
            OnCompleted = null;

            if (string.IsNullOrEmpty(path))
            {
                Complete(callback, false, "Nothing to restore — the download did not produce a file.");
                return;
            }

            if (!System.IO.File.Exists(path))
            {
                Complete(callback, false, "The downloaded file is no longer on disk:\n" + path);
                return;
            }

            string previousTitle = null;

            try
            {
                var uidoc = app.ActiveUIDocument;
                Document previous = uidoc != null ? uidoc.Document : null;

                if (previous == null)
                {
                    Complete(callback, false, "No active document to replace.");
                    return;
                }

                previousTitle = previous.Title;
                string originalPath = previous.PathName;

                if (string.IsNullOrEmpty(originalPath))
                {
                    Complete(callback, false,
                        "The open model has never been saved, so there is no file to replace.");
                    return;
                }

                // ---- 1. Open the downloaded copy and activate it ------------------
                var opened = app.OpenAndActivateDocument(path);
                if (opened == null || opened.Document == null)
                {
                    Complete(callback, false, "Revit could not open the restored file.");
                    return;
                }

                Document restored = opened.Document;

                // ---- 2. Close the document we are replacing -----------------------
                // Compared by PATH, not reference: Revit does not guarantee one
                // managed Document wrapper per native document, so a reference
                // check can fail to notice we are about to close what we just
                // opened.
                bool sameFile = string.Equals(
                    NormalisePath(restored.PathName), NormalisePath(originalPath),
                    StringComparison.OrdinalIgnoreCase);

                bool closed = false;
                if (!sameFile)
                {
                    try
                    {
                        // false: never save. The command refuses to run on a
                        // modified document, so nothing unsaved is lost — and
                        // saving the model being discarded is the opposite of
                        // the intent.
                        closed = previous.Close(false);
                    }
                    catch
                    {
                        closed = false;
                    }
                }

                // ---- 3. Mark it BEFORE saving --------------------------------------
                // Stamped first so the bytes written in step 4 already contain the
                // marker. Written after the save it would live only in memory: the
                // user closes without saving, or Revit falls over, and the next
                // sync publishes an unlabelled version — which is the one thing
                // this feature exists to prevent.
                string finalNote = null;

                try
                {
                    Services.RollbackMarkerStore.Write(restored, fromDesignId, fromVersion);
                }
                catch (Exception ex)
                {
                    finalNote =
                        "This model could not be marked as a rollback, so the next sync will publish it as an "
                        + "ordinary version. (" + ex.Message + ")";
                }

                // ---- 4. Put the restored bytes back at the original path ----------

                if (sameFile)
                {
                    // The downloaded copy already IS the original path — nothing to
                    // move. Only possible if the cache and the model coincide.
                }
                else if (!closed)
                {
                    // The original file is still open and locked, so it cannot be
                    // overwritten. The restore is real but lives in the cache; say
                    // exactly that rather than implying the model was replaced.
                    finalNote = Append(finalNote,
                        "The previous model could not be closed, so your original file still holds the old version. "
                        + "You are now working in a copy at:\n" + path
                        + "\n\nClose the other model and save this one over your original before syncing.");
                }
                else
                {
                    try
                    {
                        var saveAs = new SaveAsOptions { OverwriteExistingFile = true };
                        restored.SaveAs(originalPath, saveAs);
                    }
                    catch (Exception ex)
                    {
                        finalNote = Append(finalNote,
                            "Restored, but the file could not be written back to its original location, so you are "
                            + "working in a copy at:\n" + path
                            + "\n\nSave it over your original before syncing. (" + ex.Message + ")");
                    }
                }

                // Success means the user's own file now holds the restored version.
                // A degraded restore (original still open, or the write-back failed)
                // reports as a failure with the explanation, so the dialog title
                // never contradicts its own body.
                bool replacedOriginal = sameFile || (closed && string.IsNullOrEmpty(finalNote));
                Complete(callback, replacedOriginal, finalNote);
            }
            catch (Exception ex)
            {
                Complete(callback, false, DescribeFailure(ex, previousTitle));
            }
        }

        /// <summary>Joins notes so one failure never silently erases another.</summary>
        private static string Append(string existing, string addition)
        {
            if (string.IsNullOrEmpty(existing)) return addition;
            if (string.IsNullOrEmpty(addition)) return existing;
            return existing + "\n\n" + addition;
        }

        private static string NormalisePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return System.IO.Path.GetFullPath(path); }
            catch { return path; }
        }

        private static string DescribeFailure(Exception ex, string previousTitle)
        {
            string where = string.IsNullOrEmpty(previousTitle)
                ? "the model"
                : "\"" + previousTitle + "\"";

            return "Could not restore the selected version, and " + where
                 + " is unchanged.\n\n" + ex.Message;
        }

        private static void Complete(Action<bool, string> callback, bool success, string message)
        {
            if (callback == null) return;

            try { callback(success, message); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] rollback completion failed: " + ex.Message);
            }
        }

        public string GetName() { return "BINA rollback document swap"; }
    }
}
