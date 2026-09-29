using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Revit's own Navisworks exporter, driven the way the coordinator's "Revit to NWC"
    /// checklist describes (ClickUp 86d49v9ak).
    ///
    /// Nothing here reimplements the converter: Revit ships <c>Document.Export(folder,
    /// name, NavisworksExportOptions)</c> and every tick in the checklist is one property
    /// on that options object. What this class adds is the setup the checklist does by
    /// hand — a clean temporary 3D view, the right scope and coordinates, hidden links —
    /// and the discipline to leave the user's document exactly as it was found.
    ///
    /// Two facts shape the whole file:
    ///
    /// 1. The Navisworks Exporter is a separate free Autodesk install, one build per
    ///    Revit year. It cannot ship inside this plugin. So the first question is always
    ///    <see cref="IsAvailable"/>, and the answer when it is missing is a download
    ///    link, never a half-done export.
    ///
    /// 2. Everything Revit-side must happen on the UI thread, inside a transaction.
    ///    The callers do the threading (they are IExternalCommand bodies and the sync
    ///    dialog's UI-thread callback); this class does the transactions.
    /// </summary>
    public static class NwcExporter
    {
        /// <summary>
        /// Is the Navisworks Exporter for THIS Revit year installed?
        ///
        /// Revit's own check, not a file search: the exporter registers itself with
        /// Revit, so Revit's answer is the one that matches whether Export will work.
        /// </summary>
        public static bool IsAvailable() => OptionalFunctionalityUtils.IsNavisworksExporterAvailable();

        /// <summary>
        /// The Revit year in use, for messages that have to name the exact exporter
        /// build to install ("Navisworks Exporter for Revit 2025").
        /// </summary>
        public static int RevitYearFor(Document doc)
        {
            int year;
            if (doc != null && int.TryParse(doc.Application?.VersionNumber, out year)) return year;
            return 0;
        }

        /// <summary>
        /// The message a missing exporter earns. Names the year and the download, because
        /// "exporter not found" is useless to a coordinator who has never heard of a
        /// separate installer.
        /// </summary>
        public static string MissingExporterMessage(int revitYear)
        {
            string year = revitYear > 0 ? revitYear.ToString() : "your Revit year";
            return $"The Navisworks Exporter for Revit {year} is not installed on this PC.\n\n" +
                   $"Autodesk ships it separately from Revit, free, and the year must match — " +
                   $"install \"Navisworks NWC Export Utility\" for Revit {year}, then try again.\n\n" +
                   NwcExportSettings.ExporterDownloadUrl;
        }

        /// <summary>
        /// Export the model to an NWC in <paramref name="outputFolder"/>.
        ///
        /// UI thread and a valid <see cref="Document"/> only. The document is left as it
        /// was found: the temporary view lives inside a transaction group that is rolled
        /// back on every path, so the user's views, worksets and active view are
        /// untouched. The one deliberate exception is the optional purge, which the user
        /// asked for and which therefore commits in its own transaction OUTSIDE the group
        /// — a transaction group rolls back committed transactions too, so keeping it
        /// inside would silently undo it.
        /// </summary>
        public static NwcExportResult Export(Document doc, NwcExportSettings settings, string outputFolder)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            settings = settings ?? NwcExportSettings.Default;

            if (!IsAvailable())
            {
                throw new NwcExportException(
                    NwcExportFailure.ExporterMissing, MissingExporterMessage(RevitYearFor(doc)));
            }

            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                throw new NwcExportException(
                    NwcExportFailure.FolderNotWritable, "Choose a folder to write the NWC to.");
            }

            string fileName = NwcExportSettings.BuildFileName(doc.PathName ?? doc.Title);
            string filePath = Path.Combine(outputFolder, fileName);

            EnsureFolderIsWritable(outputFolder);

            // Any stale file from an earlier attempt would make "did the export write
            // anything?" unanswerable, and the export itself overwrites silently.
            TryDelete(filePath);

            string action = null;

            if (settings.PurgeUnused)
            {
                try
                {
                    action = PurgeUnused(doc);
                }
                catch (Exception ex)
                {
                    // Purge is a convenience, not the deliverable. A model with families
                    // that refuse deletion still exports.
                    action = $"Purge skipped: {ex.Message}";
                }
            }

            using (var group = new TransactionGroup(doc, "BINA: NWC export (temporary view)"))
            {
                group.Start();
                try
                {
                    ElementId viewId;
                    using (var t = new Transaction(doc, "BINA: temporary NWC view"))
                    {
                        t.Start();
                        View3D view = CreateTempView(doc);
                        ApplyCategoryRules(doc, view);
                        viewId = view.Id;
                        t.Commit();
                    }

                    var options = BuildOptions(settings, viewId);

                    try
                    {
                        doc.Export(outputFolder, fileName, options);
                    }
                    catch (Exception ex)
                    {
                        throw new NwcExportException(
                            NwcExportFailure.ExportFailed,
                            $"Revit's Navisworks exporter failed: {ex.Message}");
                    }
                }
                finally
                {
                    // The temp view goes away with the group. Rolled back rather than
                    // deleted, so nothing this export touched can outlive it — including
                    // any view state Revit adjusted while exporting.
                    if (group.HasStarted()) group.RollBack();
                }
            }

            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0)
            {
                throw new NwcExportException(
                    NwcExportFailure.ExportFailed,
                    $"Revit reported a finished export but wrote no NWC to {filePath}.");
            }

            return new NwcExportResult
            {
                FilePath = filePath,
                FileName = fileName,
                FileSize = info.Length,
                Action = action
            };
        }

        /// <summary>
        /// A clean isometric 3D view of the whole model: detail Fine, section box off,
        /// nothing cropped or hidden by the user. Created inside the caller's
        /// transaction, which either commits (the view must exist for Export) or rolls
        /// back with the group.
        /// </summary>
        private static View3D CreateTempView(Document doc)
        {
            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewFamilyType == null)
            {
                throw new NwcExportException(
                    NwcExportFailure.ExportFailed,
                    "This model has no 3D view type, so a temporary view cannot be created.");
            }

            View3D view = View3D.CreateIsometric(doc, viewFamilyType.Id);

            // Revit refuses duplicate view names, and a half-finished earlier export can
            // leave one behind. UniqueViewName keeps this from being the failure.
            view.Name = UniqueViewName(doc, NwcExportSettings.TempViewName);
            view.DetailLevel = ViewDetailLevel.Fine;
            view.IsSectionBoxActive = false;

            return view;
        }

        /// <summary>
        /// The checklist's visibility rules, applied to the temporary view only.
        /// </summary>
        private static void ApplyCategoryRules(Document doc, View view)
        {
            var rules = new NwcCategoryRules();

            view.AreModelCategoriesHidden = !rules.ModelVisible;
            view.AreAnnotationCategoriesHidden = rules.HideAnnotationCategories;
            view.AreAnalyticalModelCategoriesHidden = rules.HideAnalyticalCategories;
            view.AreImportCategoriesHidden = rules.HideImportedCategories;

            if (rules.HideRevitLinks)
            {
                // No view property for this one: the "Revit Links" category is hidden
                // like any other category, which is what the checklist's untick means.
                Category links = Category.GetCategory(doc, BuiltInCategory.OST_RvtLinks);
                if (links != null) view.SetCategoryHidden(links.Id, true);
            }
        }

        /// <summary>
        /// The options object, one property per tick in the docs screenshot. Kept
        /// separate and dumb: the values come from <see cref="NwcExportSettings.ToOptionsSpec"/>
        /// so the policy stays unit-testable without a Revit document.
        /// </summary>
        private static NavisworksExportOptions BuildOptions(NwcExportSettings settings, ElementId viewId)
        {
            NwcOptionsSpec spec = settings.ToOptionsSpec();

            return new NavisworksExportOptions
            {
                // Scope is always a view: "Export = Current view" in the docs, with the
                // view being this export's temporary one rather than whatever the user
                // happens to be looking at.
                ExportScope = NavisworksExportScope.View,
                ViewId = viewId,
                ExportElementIds = spec.ExportElementIds,
                Coordinates = spec.Coordinates == NwcCoordinates.Shared
                    ? NavisworksCoordinates.Shared
                    : NavisworksCoordinates.Internal,
                ExportLinks = spec.ExportLinks,
                ConvertLinkedCADFormats = spec.ConvertLinkedCADFormats,
                ExportRoomAsAttribute = spec.ExportRoomAsAttribute,
                ExportRoomGeometry = spec.ExportRoomGeometry,
                DivideFileIntoLevels = spec.DivideFileIntoLevels,
                ExportUrls = spec.ExportUrls,
                ExportParts = spec.ExportParts,
                ConvertElementProperties = spec.ConvertElementProperties,
                ConvertLights = spec.ConvertLights,
                FacetingFactor = spec.FacetingFactor,
                FindMissingMaterials = spec.FindMissingMaterials,
                Parameters = spec.ExportAllParameters
                    ? NavisworksParameters.All
                    : NavisworksParameters.Elements
            };
        }

        /// <summary>
        /// <c>Document.GetUnusedElements</c> is Revit 2024 API and this assembly is
        /// compiled against 2023 references for the 2023/2024 pair, so the call is
        /// guarded exactly the way <c>Mutators.PurgeUnused</c> guards its own. Returns
        /// the sentence the outcome adds, or null when there was nothing to purge.
        /// </summary>
        private static string PurgeUnused(Document doc)
        {
#if REVIT2023_24
            // Compiled against 2023: the API is not there to call. Report honestly
            // rather than returning "0 purged", which reads as "nothing to purge".
            return "Purge needs Revit 2025 or newer — skipped.";
#else
            var unused = doc.GetUnusedElements(new HashSet<ElementId>());
            var ids = unused.Where(id => doc.GetElement(id) != null).ToList();
            if (ids.Count == 0) return null;

            int purged;
            using (var t = new Transaction(doc, "BINA: purge unused"))
            {
                t.Start();
                try
                {
                    var deleted = doc.Delete(ids);
                    purged = deleted != null ? deleted.Count : ids.Count;
                    t.Commit();
                }
                catch
                {
                    t.RollBack();
                    throw;
                }
            }

            return $"Purged {purged} unused element{(purged == 1 ? "" : "s")} before exporting.";
#endif
        }

        /// <summary>
        /// A name Revit will accept, derived from the one we wanted. Revit appends its
        /// own suffix if this races with something else creating views at the same time,
        /// which is fine — the view is thrown away either way.
        /// </summary>
        private static string UniqueViewName(Document doc, string wanted)
        {
            string name = wanted;
            int n = 1;
            while (new FilteredElementCollector(doc)
                       .OfClass(typeof(View))
                       .Cast<View>()
                       .Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{wanted} {++n}";
            }
            return name;
        }

        /// <summary>
        /// Fail before the export rather than after it: "folder not writable" discovered
        /// by the exporter surfaces as whatever Revit happens to throw, which is how a
        /// permissions problem gets reported as a corrupt-model problem.
        /// </summary>
        private static void EnsureFolderIsWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".bina-nwc-write-test");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new NwcExportException(
                    NwcExportFailure.FolderNotWritable,
                    $"The NWC cannot be written to \"{folder}\": {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* the exporter overwrites; a locked leftover will surface as ExportFailed */ }
        }
    }
}
