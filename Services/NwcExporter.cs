using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services
{
    /// <summary>Why an export could not be produced.</summary>
    public enum NwcExportFailure
    {
        /// <summary>The Autodesk Navisworks Exporter for Revit is not installed.</summary>
        ExporterMissing,
        /// <summary>The chosen output folder does not exist or cannot be written to.</summary>
        FolderNotWritable,
        /// <summary>Revit ran the export and it did not produce a file.</summary>
        ExportFailed
    }

    public sealed class NwcExportException : Exception
    {
        public NwcExportFailure Reason { get; }

        public NwcExportException(NwcExportFailure reason, string message) : base(message)
        {
            Reason = reason;
        }
    }

    public sealed class NwcExportResult
    {
        public string OutputPath { get; set; }
        /// <summary>One line for the outcome view — what was actually done.</summary>
        public string ActionText { get; set; }
    }

    /// <summary>
    /// Runs the coordinator's manual "Revit to NWC" checklist from inside the
    /// add-in (ClickUp 86d49v9ak, both attached PDFs).
    ///
    /// Revit owns the exporter — `Document.Export(folder, name,
    /// NavisworksExportOptions)` — so this drives it rather than reimplementing
    /// anything. What the checklist actually specifies is a clean view and a
    /// particular set of options, and that is what this builds: a temporary 3D
    /// view created, exported from, and then discarded. The user's own views are
    /// never touched, which is the point — the manual procedure had drafters
    /// editing a working view and remembering to put it back.
    ///
    /// Everything here touches the Revit API and must run on the UI thread.
    ///
    /// The decisions live in <see cref="NwcExportPlan"/>; this file is the
    /// mechanical application of them, so the checklist's mapping table can be
    /// tested without a Revit session.
    /// </summary>
    public static class NwcExporter
    {
        /// <summary>
        /// Whether the Navisworks exporter add-on is installed for this Revit.
        ///
        /// It is a separate free download from Autodesk, year-matched to Revit,
        /// and cannot ship inside this add-in. Without it `Document.Export` for
        /// Navisworks does nothing, so every entry point checks this first and
        /// writes nothing when it is false.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                return OptionalFunctionalityUtils.IsNavisworksExporterAvailable();
            }
            catch
            {
                // The check itself is part of the optional functionality API; a
                // host that cannot answer is a host without the exporter.
                return false;
            }
        }

        /// <summary>Autodesk's download page for the exporter, for the year in question.</summary>
        public static string DownloadUrl(string revitVersionNumber) =>
            "https://www.autodesk.com/support/technical/article/caas/tsarticles/ts/"
            + "7OAcZUFfrGXkKGpZvCJQq3.html"
            + (string.IsNullOrEmpty(revitVersionNumber) ? "" : "?revit=" + revitVersionNumber);

        /// <summary>
        /// Export the document to .nwc and hand back where it landed.
        /// </summary>
        /// <param name="doc">Open, saved document. Not modified, purge aside.</param>
        /// <param name="settings">What the user chose in the dialog.</param>
        /// <param name="folder">
        /// Output folder. Null falls back to <paramref name="settings"/>, then to
        /// the document's own folder.
        /// </param>
        public static NwcExportResult Export(Document doc, NwcExportSettings settings, string folder = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!IsAvailable())
                throw new NwcExportException(
                    NwcExportFailure.ExporterMissing,
                    "The Autodesk Navisworks Exporter for Revit is not installed for this version of Revit.");

            string outputFolder = FirstWritableFolder(folder, settings.OutputFolder, DocumentFolder(doc));
            var plan = NwcExportPlan.From(settings, Path.GetFileName(doc.PathName), NwcPurgeSupport.CompiledIn);
            string outputPath = Path.Combine(outputFolder, plan.FileName);

            // Purge first and OUTSIDE the transaction group below: the group is
            // rolled back to undo the temporary view, and a purge inside it would
            // be undone with it. A purge is a real edit the user asked for, so it
            // stays — which also means it survives an export that then fails.
            int purged = 0;
            if (plan.PurgeUnused) purged = PurgeUnused(doc);

            var group = new TransactionGroup(doc, "BINA: export NWC");
            try
            {
                group.Start();

                View3D view;
                using (var t = new Transaction(doc, "BINA: temporary NWC view"))
                {
                    t.Start();
                    view = CreateExportView(doc, plan);
                    t.Commit();
                }

                // Export cannot run inside an open transaction. The group is not a
                // transaction, so the view exists and is committed while nothing
                // is open — which is exactly the state Export needs.
                // The Navisworks overload returns void and reports nothing, so the
                // file on disk is the only evidence the export actually ran.
                doc.Export(outputFolder, plan.FileName, BuildOptions(plan, view.Id));

                if (!File.Exists(outputPath))
                    throw new NwcExportException(
                        NwcExportFailure.ExportFailed,
                        "Revit did not produce an NWC file. The model may have nothing visible to export.");

                return new NwcExportResult
                {
                    OutputPath = outputPath,
                    ActionText = DescribeAction(plan, purged)
                };
            }
            finally
            {
                // Always rolled back, including on the way out of a failure: the
                // temporary view is an implementation detail and must not be left
                // in the user's model. RollBack discards the view creation
                // wholesale, so there is nothing to delete by hand.
                try
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                }
                catch
                {
                    // A group that cannot be rolled back leaves a stray view; not
                    // worth masking the original failure over.
                }

                group.Dispose();
            }
        }

        /// <summary>
        /// The checklist's clean view: isometric, Fine, no section box, model
        /// categories only.
        /// </summary>
        private static View3D CreateExportView(Document doc, NwcExportPlan plan)
        {
            var viewType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewType == null)
                throw new NwcExportException(
                    NwcExportFailure.ExportFailed,
                    "This model has no 3D view type, so no export view could be created.");

            View3D view = View3D.CreateIsometric(doc, viewType.Id);

            // A name already in use throws; the view lives for one export, so the
            // only requirement is that it is unique right now.
            try
            {
                view.Name = "BINA NWC export " + Guid.NewGuid().ToString("N").Substring(0, 6);
            }
            catch
            {
                // An unnamed view exports just as well.
            }

            view.DetailLevel = ViewDetailLevel.Fine;

            // "Section box: off" on the checklist. A fresh isometric has none, but
            // the view template a project defaults to may.
            try
            {
                if (view.IsSectionBoxActive) view.IsSectionBoxActive = false;
            }
            catch
            {
            }

            HideCategoryGroups(doc, view, plan.HiddenCategoryGroups);

            return view;
        }

        /// <summary>
        /// Hide everything the checklist unticks. Filters are deliberately left
        /// alone — a fresh view has none, and a project's view template may add
        /// ones the coordinator relies on.
        /// </summary>
        private static void HideCategoryGroups(
            Document doc,
            View3D view,
            IReadOnlyList<NwcHiddenCategoryGroup> groups)
        {
            var wanted = new HashSet<NwcHiddenCategoryGroup>(groups ?? new NwcHiddenCategoryGroup[0]);

            foreach (Category category in doc.Settings.Categories)
            {
                if (category == null) continue;

                NwcHiddenCategoryGroup? group = GroupOf(category);
                if (!group.HasValue || !wanted.Contains(group.Value)) continue;

                try
                {
                    if (view.CanCategoryBeHidden(category.Id))
                        view.SetCategoryHidden(category.Id, true);
                }
                catch
                {
                    // Some categories refuse per-view control; skipping one is far
                    // better than failing the export over it.
                }
            }
        }

        /// <summary>
        /// Maps a Revit category onto the checklist's tabs. `Internal` is what the
        /// API calls the Imported Categories tab (DWG/DXF styles, raster images).
        /// Revit links are a model category, so they are named explicitly rather
        /// than falling out of the category type.
        /// </summary>
        private static NwcHiddenCategoryGroup? GroupOf(Category category)
        {
            if (IsRevitLinks(category)) return NwcHiddenCategoryGroup.RevitLinks;

            switch (category.CategoryType)
            {
                case CategoryType.Annotation: return NwcHiddenCategoryGroup.Annotation;
                case CategoryType.AnalyticalModel: return NwcHiddenCategoryGroup.AnalyticalModel;
                case CategoryType.Internal: return NwcHiddenCategoryGroup.Imported;
                default: return null;
            }
        }

        private static bool IsRevitLinks(Category category)
        {
            try
            {
                // Compared as ElementIds rather than through the numeric value:
                // `ElementId.IntegerValue` is gone in the Revit 2027 API and
                // `.Value` does not exist in 2023, so either one breaks a target.
                // The BuiltInCategory constructor is in all five years.
                return category.Id == new ElementId(BuiltInCategory.OST_RvtLinks);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The checklist's option set, field for field. Values come from
        /// <see cref="NwcExportPlan"/> so the table is pinned by tests; the only
        /// thing decided here is which Revit enum member each one maps to.
        /// </summary>
        private static NavisworksExportOptions BuildOptions(NwcExportPlan plan, ElementId viewId)
        {
            return new NavisworksExportOptions
            {
                ExportScope = plan.ExportCurrentViewOnly
                    ? NavisworksExportScope.View
                    : NavisworksExportScope.Model,
                ViewId = viewId,
                ExportElementIds = plan.ExportElementIds,
                Coordinates = plan.Coordinates == NwcCoordinates.Shared
                    ? NavisworksCoordinates.Shared
                    : NavisworksCoordinates.Internal,
                ExportLinks = plan.ExportLinks,
                ConvertLinkedCADFormats = plan.ConvertLinkedCADFormats,
                ExportRoomAsAttribute = plan.ExportRoomAsAttribute,
                ExportRoomGeometry = plan.ExportRoomGeometry,
                DivideFileIntoLevels = plan.DivideFileIntoLevels,
                ExportUrls = plan.ExportUrls,
                ExportParts = plan.ExportParts,
                ConvertElementProperties = plan.ConvertElementProperties,
                ConvertLights = plan.ConvertLights,
                FacetingFactor = plan.FacetingFactor,
                FindMissingMaterials = plan.FindMissingMaterials,
                Parameters = NavisworksParameters.All
            };
        }

        /// <summary>
        /// Delete unused elements, where the API allows it.
        ///
        /// `Document.GetUnusedElements` is Revit 2024 API and the net48 payload is
        /// compiled against 2023 references, so the call does not exist there —
        /// the same honest gate `Mutators.PurgeUnused` uses. The dialog disables
        /// the tick on those hosts, so reaching this with the constant set means
        /// the plan already dropped the request.
        /// </summary>
        private static int PurgeUnused(Document doc)
        {
#if REVIT2023_24
            return 0;
#else
            try
            {
                using (var t = new Transaction(doc, "BINA: purge unused before NWC export"))
                {
                    t.Start();

                    // Empty input set = everything purgeable.
                    var unused = doc.GetUnusedElements(new HashSet<ElementId>())
                        .Where(id => doc.GetElement(id) != null)
                        .ToList();

                    if (unused.Count == 0)
                    {
                        t.RollBack();
                        return 0;
                    }

                    var deleted = doc.Delete(unused);
                    t.Commit();

                    return deleted != null ? deleted.Count : unused.Count;
                }
            }
            catch (Exception ex)
            {
                // The export is what the user came for; a purge that cannot run
                // is worth a line in the outcome, not a failure.
                System.Diagnostics.Debug.WriteLine($"[BINA] Purge before NWC export failed (non-fatal): {ex.Message}");
                return 0;
            }
#endif
        }

        private static string DescribeAction(NwcExportPlan plan, int purged)
        {
            string coordinates = plan.Coordinates == NwcCoordinates.Shared
                ? "shared coordinates"
                : "project internal coordinates";

            string text = $"Exported {plan.FileName} from a temporary 3D view using {coordinates}.";

            if (purged > 0) text += $" Purged {purged} unused element(s) first.";
            if (!string.IsNullOrEmpty(plan.PurgeSkippedNote)) text += " " + plan.PurgeSkippedNote;

            return text;
        }

        private static string DocumentFolder(Document doc)
        {
            string path = doc.PathName;
            return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        }

        /// <summary>
        /// First of the candidates that exists and accepts a file. Checked by
        /// writing, not by inspecting permissions: a network path can be listed
        /// and still refuse a new file, and finding that out after a long export
        /// wastes the whole thing.
        /// </summary>
        private static string FirstWritableFolder(params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (IsWritable(candidate)) return candidate;
            }

            throw new NwcExportException(
                NwcExportFailure.FolderNotWritable,
                "The export folder does not exist or cannot be written to. Pick another folder.");
        }

        private static bool IsWritable(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return false;

                string probe = Path.Combine(folder, $".bina_nwc_{Guid.NewGuid():N}.tmp");
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { }
                File.Delete(probe);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
