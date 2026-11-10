#nullable enable
// FailuresProcessing backstop for a write batch (spec §7.3.2, W1 Task 2).
//
// TxGuard.StartSwallowing attaches SwallowWarnings to OUR transactions. A batch
// can still raise failures through a transaction that bypasses it (generated
// code that opens its own, an updater reacting to our change). While a scope is
// open, Application.FailuresProcessing runs the same SwallowWarnings.Process:
// every failure is logged into FailureLog.Current, warnings are deleted and
// errors roll back, so no modal dialog appears and nothing is reported silently.
//
//   using (FailureLog.Begin())
//   using (FailureScope.Begin(app)) { ... run the batch ... }
//
// Revit-bound; open and dispose on Revit's UI thread.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Guard
{
    public sealed class FailureScope : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _app;
        private bool _disposed;

        private FailureScope(Autodesk.Revit.ApplicationServices.Application app)
        {
            _app = app;
            _app.FailuresProcessing += OnFailuresProcessing;
        }

        /// <summary>Subscribes the backstop until <see cref="Dispose"/>.</summary>
        public static FailureScope Begin(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            return new FailureScope(app);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _app.FailuresProcessing -= OnFailuresProcessing;
        }

        private static void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
        {
            var accessor = e.GetFailuresAccessor();
            if (accessor.GetFailureMessages().Count == 0) return;
            var result = SwallowWarnings.Process(accessor);
            if (result != FailureProcessingResult.Continue) e.SetProcessingResult(result);
        }
    }
}
