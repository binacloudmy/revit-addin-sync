using System;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The pull-and-write core of Sync Parameters, shared by the ribbon button
    /// and by Download Model (ClickUp 14zefhy1rfg). Revit API calls here must
    /// run on the UI thread inside an IExternalCommand.Execute.
    /// </summary>
    public static class ParameterPullService
    {
        /// <summary>Write the pulled values in one transaction.</summary>
        public static ParameterWriter.Report Write(Document doc, ElementParametersResponse pull)
        {
            var writer = new ParameterWriter(doc);
            ParameterWriter.Report report;
            using (var t = new Transaction(doc, "BINA: write parameters from BINA Cloud"))
            {
                t.Start();
                report = writer.Apply(pull.Parameters);
                t.Commit();
            }
            return report;
        }

        /// <summary>
        /// Records the BINA document GUID inside a model that was just
        /// downloaded, so Sync, Sync Parameters and Issues recognise it as a
        /// version of that model. Non-fatal by design: it changes the user's
        /// file, so any failure leaves the model as it was. Never replaces a
        /// stamp that holds a different GUID.
        /// </summary>
        public static StampAction StampDownloaded(
            Document doc, string rowDocGuid, string originPath, out bool failed)
        {
            failed = false;
            try
            {
                bool readable = ModelLineage.TryRead(doc, out var stamp);
                var action = DownloadStampPolicy.Decide(readable, stamp?.LineageId, rowDocGuid);
                if (action != StampAction.Write) return action;

                using (var t = new Transaction(doc, "BINA: record model identity"))
                {
                    t.Start();
                    ModelLineage.Write(doc, rowDocGuid.Trim(), originPath);
                    t.Commit();
                }
                return action;
            }
            catch
            {
                failed = true;
                return StampAction.SkipUnreadable;
            }
        }

        /// <summary>
        /// The step Download Model runs after the model opens. Never throws:
        /// the model is already open, so any failure comes back as an outcome
        /// the caller turns into a retry hint.
        /// </summary>
        public static AutoPullOutcome RunAutomatic(
            Document doc, SyncApiClient api, int designId, string name, int? versionNumber)
        {
            var outcome = new AutoPullOutcome { ModelName = name, VersionNumber = versionNumber };
            try
            {
                if (doc == null)
                {
                    outcome.Kind = AutoPullKind.NoDocument;
                    return outcome;
                }

                ElementParametersResponse pull;
                try
                {
                    pull = Task.Run(() => api.GetElementParametersAsync(designId)).Result;
                }
                catch (AggregateException aex)
                {
                    throw aex.InnerException ?? aex;
                }

                outcome.Total = pull.Parameters.Count;
                outcome.Truncated = pull.Truncated;

                if (pull.Parameters.Count == 0)
                {
                    outcome.Kind = AutoPullKind.NothingToWrite;
                    return outcome;
                }

                var report = Write(doc, pull);
                outcome.Kind = AutoPullKind.Written;
                outcome.Applied = report.Applied;
                outcome.ElementsTouched = report.ElementsTouched;
                outcome.Skipped = report.SkippedCount;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Kind = AutoPullKind.Failed;
                outcome.Error = ex.Message;
                return outcome;
            }
        }
    }
}
