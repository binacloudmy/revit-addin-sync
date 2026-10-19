using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The pull-and-write core of Sync Parameters, shared by the ribbon button
    /// and by Download Model (ClickUp 14zefhy1rfg). Revit API calls here must
    /// run on the UI thread inside an IExternalCommand.Execute.
    /// </summary>
    public static class ParameterPullService
    {
        /// <summary>Revit Transaction as the AtomicBatch scope.</summary>
        private sealed class RevitBatchScope : IBatchScope
        {
            private readonly Transaction _t;
            public RevitBatchScope(Document doc, string name) { _t = new Transaction(doc, name); }
            public void Start() => _t.Start();
            public void Commit() => _t.Commit();
            public void RollBack() => _t.RollBack();
            public void Dispose() => _t.Dispose();
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
        /// Write the pulled values in one transaction that is kept whole or not
        /// at all: an exception, a cancel, or a batch that wrote nothing rolls
        /// back the values and any shared-parameter bindings, and the shared
        /// parameter file path is put back either way.
        /// </summary>
        public static ParameterWriter.Report Write(
            Document doc, ElementParametersResponse pull, CancellationToken cancel = default)
        {
            var writer = new ParameterWriter(doc);
            var scope = new RevitBatchScope(doc, "BINA: write parameters from BINA Cloud");
            try
            {
                var outcome = AtomicBatch.Run(
                    scope,
                    () => writer.Apply(pull.Parameters, null, cancel),
                    report => report.Applied > 0,
                    writer.RestoreApplicationState,
                    cancel);
                return outcome.Value;
            }
            finally
            {
                scope.Dispose();
            }
        }

        /// <summary>
        /// Fetch the parameters without blocking the Revit UI thread: the
        /// request runs on a worker while a busy window (with Cancel) pumps the
        /// UI. Times out after <see cref="PullPolicy.TimeoutSeconds"/>. Throws
        /// the request's own exception; <paramref name="userCancelled"/> tells
        /// a Cancel click from the timeout.
        /// </summary>
        public static ElementParametersResponse Fetch(
            SyncApiClient api, int designId, UIApplication uiApp, out bool userCancelled)
        {
            userCancelled = false;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(PullPolicy.TimeoutSeconds)))
            {
                var task = Task.Run(() => api.GetElementParametersAsync(designId, "lineage", cts.Token));

                if (!task.IsCompleted)
                {
                    var busy = new ParameterPullBusyWindow(
                        "Pulling parameters from BINA…", () => cts.Cancel());
                    if (uiApp != null) RevitWindowOwner.SetOwner(busy, uiApp);

                    task.ContinueWith(
                        _ => { try { busy.Dispatcher.BeginInvoke(new Action(busy.Close)); } catch { } },
                        TaskScheduler.Default);
                    busy.Loaded += (s, e) => { if (task.IsCompleted) busy.Close(); };
                    busy.ShowDialog();
                    userCancelled = busy.UserCancelled;
                }

                return task.GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// The step Download Model runs after the model opens. Never throws:
        /// the model is already open, so any failure comes back as an outcome
        /// the caller turns into a retry hint.
        /// </summary>
        public static AutoPullOutcome RunAutomatic(
            Document doc, SyncApiClient api, int designId, string name, int? versionNumber,
            UIApplication uiApp = null)
        {
            var outcome = new AutoPullOutcome { ModelName = name, VersionNumber = versionNumber };
            bool userCancelled = false;
            try
            {
                if (doc == null)
                {
                    outcome.Kind = AutoPullKind.NoDocument;
                    return outcome;
                }

                var pull = Fetch(api, designId, uiApp, out userCancelled);

                outcome.Total = pull.Parameters.Count;
                outcome.Truncated = pull.Truncated;

                if (pull.Parameters.Count == 0)
                {
                    outcome.Kind = AutoPullKind.NothingToWrite;
                    return outcome;
                }

                ParameterPullBusyWindow writing = null;
                try
                {
                    writing = new ParameterPullBusyWindow(
                        $"Writing {pull.Parameters.Count} parameter(s) into the model…", null);
                    if (uiApp != null) RevitWindowOwner.SetOwner(writing, uiApp);
                    writing.Show();
                    writing.FlushPaint();
                }
                catch { writing = null; }

                ParameterWriter.Report report;
                try
                {
                    report = Write(doc, pull);
                }
                finally
                {
                    try { writing?.Close(); } catch { }
                }

                // A batch that wrote nothing is rolled back whole (AtomicBatch).
                outcome.RolledBack = report.Applied == 0;

                outcome.Kind = AutoPullKind.Written;
                outcome.Applied = report.Applied;
                outcome.ElementsTouched = report.ElementsTouched;
                outcome.Skipped = report.SkippedCount;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Kind = AutoPullKind.Failed;
                outcome.Failure = PullPolicy.Classify(ex, userCancelled);
                outcome.Error = ex.Message;
                return outcome;
            }
        }
    }
}
