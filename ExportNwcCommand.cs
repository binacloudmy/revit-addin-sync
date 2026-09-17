using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// "Export NWC" on the BINA CDE panel (ClickUp 86d49v9ak).
    ///
    /// Saves locally and nothing else — no sign-in, no upload. The NWC reaches
    /// Cloud Docs through the Sync dialog's checkbox instead, because a cache is
    /// only useful there once it is attached to a version, and this button has no
    /// version to attach it to. Coordinators who just want the file for their own
    /// Navisworks session get it without touching BINA at all.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportNwcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Export NWC", "No active Revit document found.");
                    return Result.Failed;
                }

                // The export names itself from the model's file name, and a folder
                // to write into has to come from somewhere.
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Export NWC", "Save your Revit file once before exporting an NWC.");
                    return Result.Failed;
                }

                string revitVersion = doc.Application?.VersionNumber;

                if (!NwcExporter.IsAvailable())
                {
                    ShowExporterMissing(revitVersion);
                    return Result.Cancelled;
                }

                var options = new NwcOptionsWindow(
                    Path.GetFileName(doc.PathName),
                    Path.GetDirectoryName(doc.PathName),
                    NwcPurgeSupport.CompiledIn);

                RevitWindowOwner.SetOwner(options, commandData.Application);

                if (options.ShowDialog() != true) return Result.Cancelled;

                NwcExportResult exported;
                try
                {
                    exported = NwcExporter.Export(doc, options.Settings);
                }
                catch (NwcExportException ex)
                {
                    if (ex.Reason == NwcExportFailure.ExporterMissing)
                    {
                        // Installed between opening the dialog and clicking Export
                        // is not a real sequence, but uninstalled mid-session is.
                        ShowExporterMissing(revitVersion);
                        return Result.Cancelled;
                    }

                    TaskDialog.Show("Export NWC", ex.Message);
                    return Result.Failed;
                }

                ShowExported(exported);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Export NWC", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// The exporter is a separate Autodesk download and cannot be bundled, so
        /// the only useful thing to do is name it and link it. Nothing is written.
        /// </summary>
        private static void ShowExporterMissing(string revitVersion)
        {
            string year = string.IsNullOrEmpty(revitVersion) ? "your version of Revit" : "Revit " + revitVersion;

            var dialog = new TaskDialog("Navisworks Exporter not installed")
            {
                MainInstruction = "This needs the Autodesk Navisworks Exporter for Revit",
                MainContent =
                    $"The exporter is a free Autodesk add-on and is not part of Revit. Install the one that "
                    + $"matches {year}, restart Revit, and try again.\n\n"
                    + "Nothing has been exported or uploaded.",
                FooterText =
                    $"<a href=\"{NwcExporter.DownloadUrl(revitVersion)}\">Download the Navisworks Exporter from Autodesk</a>"
            };

            dialog.Show();
        }

        private static void ShowExported(NwcExportResult exported)
        {
            var dialog = new TaskDialog("Export NWC")
            {
                MainInstruction = "NWC exported",
                MainContent = exported.OutputPath + "\n\n" + exported.ActionText
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open folder");
            dialog.CommonButtons = TaskDialogCommonButtons.Close;
            dialog.DefaultButton = TaskDialogResult.Close;

            if (dialog.Show() != TaskDialogResult.CommandLink1) return;

            try
            {
                // Selects the file rather than just opening the folder — a
                // coordinator's export folder holds a lot of caches.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exported.OutputPath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Could not open the export folder: {ex.Message}");
            }
        }
    }
}
