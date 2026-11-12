// Opens the CAD to BIM pane on a given drawing from outside the ribbon (AI-344 §7: the Copilot
// chat hands whole-plan jobs to the pane). Showing a dockable pane and reading the levels need
// a valid Revit API context, so the request goes through an ExternalEvent App.OnStartup made.
// The pane then does exactly what OpenCadToBimCommand does after its file dialog.

using System;
using System.IO;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.CadToBim
{
    public sealed class CadToBimOpenHandler : IExternalEventHandler
    {
        /// <summary>The drawing to open; set just before the event is raised.</summary>
        public string Path { get; set; }

        public void Execute(UIApplication app)
        {
            string path = Path;
            Path = null;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                DockablePane pane = app.GetDockablePane(CadToBimPaneHost.PaneId);
                CadToBimPaneHost host = App.CadToBimPaneHost;
                CadToBimViewModel viewModel = host?.Panel?.ViewModel;
                if (pane == null || viewModel == null)
                {
                    TaskDialog.Show("BINA CAD to BIM",
                        string.IsNullOrEmpty(host?.InitError) ? "CAD to BIM panel not found. Please restart Revit." : host.InitError);
                    return;
                }

                if (!pane.IsShown()) pane.Show();
                host.Panel.RefreshLevels(app.ActiveUIDocument?.Document);
                var _ = viewModel.OpenAsync(path);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA CAD to BIM — Error", "Could not open " + System.IO.Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        public string GetName() => "BINA CAD to BIM open";
    }

    /// <summary>Entry point for other panes (the Copilot attachment chip).</summary>
    public static class CadToBimLauncher
    {
        /// <summary>True when Revit accepted the request. False when the file is missing or the
        /// add-in never registered the pane (the caller tells the drafter to use the ribbon).</summary>
        public static bool Open(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            CadToBimOpenHandler handler = App.CadToBimOpenHandler;
            ExternalEvent openEvent = App.CadToBimOpenEvent;
            if (handler == null || openEvent == null) return false;

            handler.Path = path;
            try
            {
                return openEvent.Raise() == ExternalEventRequest.Accepted;
            }
            catch
            {
                return false;
            }
        }
    }
}
