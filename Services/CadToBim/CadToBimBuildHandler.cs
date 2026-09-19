// The one place CAD to BIM touches the Revit API (spec 2026-09-08 §5 "Threading").
//
// The pane runs detection on the thread pool and never holds a Revit object. When the
// drafter confirms, it sets Request and raises the ExternalEvent App.OnStartup created
// for this handler; Revit calls Execute on its main thread at the next opportunity,
// which is the only context Transaction/Wall.Create are legal in. Same pattern as
// BombaAutoFixHandler and CodeExecutionHandler.
//
// One request at a time: the view model refuses Confirm while Request is non-null.

using System;
using System.Diagnostics;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class CadToBimBuildHandler : IExternalEventHandler, IBuildRequestSink
    {
        /// <summary>Set by the pane immediately before ExternalEvent.Raise(). Cleared by
        /// Execute whatever happens, so a failed build never wedges the next Confirm.</summary>
        public BuildRequest Request { get; set; }

        /// <summary>Raised on Revit's main thread with the finished report (Ok or Error).
        /// Revit's main thread is the WPF UI thread for a dockable pane, but the view
        /// model still marshals through its Dispatcher before touching bound state.</summary>
        public event Action<BuildReport> Completed;

        public void Execute(UIApplication app)
        {
            BuildRequest request = Request;
            try
            {
                if (request == null) return;   // raised with nothing to do (double Raise)

                BuildReport report;
                try
                {
                    report = Cad2BimBuilder.Build(app, request);
                }
                catch (Exception ex)
                {
                    // Build() catches its own failures; this is the belt for the braces.
                    report = new BuildReport { Error = ex.Message };
                }

                try
                {
                    Completed?.Invoke(report);
                }
                catch (Exception ex)
                {
                    // A listener that throws must not surface as a Revit error dialog.
                    Debug.WriteLine("[CadToBim] Completed listener threw: " + ex.Message);
                }
            }
            finally
            {
                Request = null;
            }
        }

        public string GetName() => "BINA CAD to BIM build";
    }
}
