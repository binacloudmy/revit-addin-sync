using System;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Runs "show this issue in the model" on Revit's UI thread (ClickUp 86d3y5jtz).
    ///
    /// The Issues pane is ordinary WPF: it has no Revit API context, and touching
    /// the document from it would throw. The pane queues an issue here and raises
    /// the event; Revit calls Execute when it is safe.
    /// </summary>
    public class IssueShowHandler : IExternalEventHandler
    {
        private BinaIssueDetail _pending;
        private readonly object _gate = new object();

        /// <summary>Called back on the UI thread with the outcome, for the pane to render.</summary>
        public Action<BinaIssueDetail, IssueViewpointApplier.Result, string> OnCompleted { get; set; }

        public void Queue(BinaIssueDetail issue)
        {
            lock (_gate) _pending = issue;
        }

        public void Execute(UIApplication app)
        {
            BinaIssueDetail issue;
            lock (_gate)
            {
                issue = _pending;
                _pending = null;
            }
            if (issue == null) return;

            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null)
                {
                    OnCompleted?.Invoke(issue, null, "No model is open.");
                    return;
                }

                var result = IssueViewpointApplier.Apply(uidoc, issue);
                OnCompleted?.Invoke(issue, result, null);
            }
            catch (Exception ex)
            {
                OnCompleted?.Invoke(issue, null, ex.Message);
            }
        }

        public string GetName() => "BINA: show issue in model";
    }
}
