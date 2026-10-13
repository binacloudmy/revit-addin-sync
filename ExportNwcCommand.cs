using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    /// <summary>
    /// The ribbon's standalone "Export NWC" (ClickUp 86d49v9ak): writes the open model to
    /// an NWC on disk, the way the coordinator's "Revit to NWC" checklist does by hand.
    ///
    /// Local only, and deliberately so — no Cloud Docs sign-in, nothing server-side. The
    /// export that ends up attached to a synced version is the checkbox in the sync
    /// dialog, because that is the only place with a version to attach it to.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportNwcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                if (string.IsNullOrEmpty(doc.PathName))
                {
                    // The NWC takes its name from the file and its default folder from
                    // the file's own location, so a never-saved document has neither.
                    TaskDialog.Show("Save the model first",
                        "Save your Revit file once before exporting an NWC — the export takes its name and " +
                        "its folder from the saved file.");
                    return Result.Failed;
                }

                int revitYear = Services.NwcExporter.RevitYearFor(doc);

                // The exporter is a separate free Autodesk install, one per Revit year,
                // and cannot ship inside the plugin — so it is the first thing that can
                // be missing, and the one failure with a download to point at.
                if (!Services.NwcExporter.IsAvailable())
                {
                    TaskDialog.Show("Navisworks Exporter not installed",
                        Services.NwcExporter.MissingExporterMessage(revitYear));
                    return Result.Cancelled;
                }

                var window = new NwcOptionsWindow(doc.PathName, revitYear);
                Services.RevitWindowOwner.SetOwner(window, commandData.Application);

                // The delegate runs inside the dialog's pump, on the UI thread this
                // command was called on — which is what the Revit API requires. The
                // window owns the choices; the command owns the document.
                window.ExportWork = () => Services.NwcExporter.Export(doc, window.Settings, window.OutputFolder);

                window.ShowDialog();

                return window.ExportSucceeded ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
