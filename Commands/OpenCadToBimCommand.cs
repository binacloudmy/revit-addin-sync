using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.CadToBim;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Ribbon command: shows the right-docked CAD to BIM pane, asks for a DWG/DXF and
    /// hands the path to the pane's view model, which detects on the thread pool and
    /// draws the preview. Everything after that — corrections, Confirm, the build on
    /// Revit's thread through App.CadToBimBuildEvent — happens inside the pane.
    ///
    /// Zero-document availability (ZeroDocCommandAvailability on the button): "Save as
    /// new .rvt" needs no open project, so the button must work from Revit's empty
    /// ribbon too. The command itself never touches the active document.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenCadToBimCommand : IExternalCommand
    {
        private const string Title = "BINA CAD to BIM";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;

                // GetDockablePane throws (Autodesk.Revit.Exceptions.ArgumentException)
                // rather than returning null when the id was never registered — the
                // catch below turns that into the same "restart Revit" dialog.
                DockablePane pane = uiApp.GetDockablePane(CadToBimPaneHost.PaneId);
                if (pane == null)
                {
                    TaskDialog.Show(Title, "CAD to BIM panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                CadToBimPaneHost host = App.CadToBimPaneHost;
                if (host?.CheckPane != null) return OpenInCheckPane(uiApp, pane, host);   // AI-638, cadToBim.ui = "check"

                CadToBimViewModel viewModel = host?.Panel?.ViewModel;
                if (viewModel == null)
                {
                    // The host swallowed its own init failure and is showing the
                    // error text instead of the panel (same pattern as CopilotPaneHost).
                    var dlg = new TaskDialog(Title)
                    {
                        MainInstruction = "CAD to BIM panel failed to load.",
                        MainContent = string.IsNullOrEmpty(host?.InitError)
                            ? "The pane was never registered. Please restart Revit."
                            : host.InitError,
                        ExpandedContent = host?.InitErrorDetail,
                        CommonButtons = TaskDialogCommonButtons.Close,
                    };
                    dlg.Show();
                    return Result.Failed;
                }

                if (!pane.IsShown())
                {
                    pane.Show();
                }

                string path = PickDrawing();
                if (path == null)
                {
                    // Cancel = nothing happens; the pane stays open with whatever it had.
                    return Result.Cancelled;
                }

                // The level picker reads the Document, which is only legal here (a valid
                // API context) — never from the pane. No document = empty picker.
                host.Panel.RefreshLevels(uiApp.ActiveUIDocument?.Document);

                // Fire-and-forget: detection runs on the thread pool inside OpenAsync
                // and reports through the pane's status line. The command returns at
                // once so Revit stays responsive. A fault that escapes the view model
                // still gets a dialog, marshalled back onto the UI thread.
                Task open = viewModel.OpenAsync(path);
                open.ContinueWith(t =>
                {
                    Exception root = t.Exception?.GetBaseException() ?? t.Exception;
                    host.Dispatcher.BeginInvoke(new Action(() =>
                        TaskDialog.Show(Title + " — Error",
                            $"Could not open {Path.GetFileName(path)}: {root?.Message}")));
                }, TaskContinuationOptions.OnlyOnFaulted);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title + " — Error", $"Failed to open CAD to BIM: {ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>The Check pane path: the document is read here (valid API context), the DWG on the thread pool.</summary>
        private static Result OpenInCheckPane(UIApplication uiApp, DockablePane pane, CadToBimPaneHost host)
        {
            if (!pane.IsShown()) pane.Show();
            string path = PickDrawing();
            if (path == null) return Result.Cancelled;

            Document doc = uiApp.ActiveUIDocument?.Document;
            var target = new UI.CadToBim.Check.TargetInfo { DocTitle = doc?.Title, Workshared = doc?.IsWorkshared ?? false };
            if (doc != null && !doc.IsFamilyDocument)
                foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    target.Levels.Add(new UI.CadToBim.Check.LevelInfo { Name = level.Name, ElevationMm = level.Elevation * 304.8 });

            _ = host.CheckPane.ViewModel.OpenAsync(path, target);   // the view model shows read failures itself
            return Result.Succeeded;
        }

        private static string PickDrawing()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the drawing to convert",
                Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf",
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
