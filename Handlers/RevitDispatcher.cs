using System;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Runs pane work on Revit's thread, inside an ExternalEvent, and hands the
    /// result back as a Task. WPF pane handlers have no Revit API context, so every
    /// model read (and every write that is not already on its own handler) goes
    /// through here:
    ///
    ///   var title = await App.RevitDispatcher.InvokeAsync(app => app.ActiveUIDocument?.Document?.Title);
    ///
    /// Return plain data only — never Element, Document or other Revit objects.
    /// Writes still need their own Transaction inside the job.
    /// The queueing rules live in <see cref="RevitJobQueue{TContext}"/> (unit-tested).
    /// </summary>
    public sealed class RevitDispatcher : IExternalEventHandler
    {
        private readonly RevitJobQueue<UIApplication> _queue;
        private ExternalEvent _event;

        private RevitDispatcher()
        {
            _queue = new RevitJobQueue<UIApplication>(Raise);
        }

        /// <summary>Must be called in a Revit API context (OnStartup), because
        /// ExternalEvent.Create requires one.</summary>
        public static RevitDispatcher Create()
        {
            var dispatcher = new RevitDispatcher();
            dispatcher._event = ExternalEvent.Create(dispatcher);
            return dispatcher;
        }

        public Task<T> InvokeAsync<T>(Func<UIApplication, T> work) => _queue.Enqueue(work);

        public Task InvokeAsync(Action<UIApplication> work) => _queue.Enqueue(work);

        public void Execute(UIApplication app) => _queue.Drain(app);

        public string GetName() => "BINA Revit dispatcher";

        /// <summary>Cancels queued work and releases the event (add-in shutdown).</summary>
        public void Shutdown()
        {
            _queue.CancelAll();
            try { _event?.Dispose(); } catch { /* Revit is closing */ }
            _event = null;
        }

        private bool Raise()
        {
            var ev = _event;
            if (ev == null) return false;
            var request = ev.Raise();
            return request == ExternalEventRequest.Accepted || request == ExternalEventRequest.Pending;
        }
    }
}
