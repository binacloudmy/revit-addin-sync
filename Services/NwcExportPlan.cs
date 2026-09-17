using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Which coordinate system the NWC is written in (ClickUp 86d49v9ak).
    ///
    /// Shared is the default, from the hand-annotated "Revit to NWC rev a" sheet.
    /// The older TCDR01 deck says Project Internal; the two disagree, rev a is
    /// newer, and the choice stays in the dialog for the jobs that need the other.
    /// </summary>
    public enum NwcCoordinates
    {
        Shared,
        Internal
    }

    /// <summary>What the user chose in the export dialog.</summary>
    public sealed class NwcExportSettings
    {
        public NwcCoordinates Coordinates { get; set; } = NwcCoordinates.Shared;

        /// <summary>
        /// Off by default. Optional per Ammar's comment on the checklist, and it
        /// is the one step here that changes the model rather than just reading
        /// it — see <see cref="NwcPurgeSupport"/> for where it is even possible.
        /// </summary>
        public bool PurgeUnused { get; set; }

        /// <summary>Folder the .nwc is written to. The rvt's own folder by default.</summary>
        public string OutputFolder { get; set; }
    }

    /// <summary>
    /// Category groups the temporary export view hides. Named groups rather than
    /// BuiltInCategory lists because that is the granularity the checklist works
    /// at ("Annotation Categories: untick all"), and because this type has to
    /// stay free of Revit references so the rules are testable.
    /// </summary>
    public enum NwcHiddenCategoryGroup
    {
        Annotation,
        AnalyticalModel,
        Imported,
        RevitLinks
    }

    /// <summary>
    /// The export, decided before any Revit API is touched: every
    /// NavisworksExportOptions value and every view rule, derived from the user's
    /// settings (ClickUp 86d49v9ak).
    ///
    /// Split from <see cref="NwcExporter"/> on purpose. NavisworksExportOptions
    /// and View3D cannot be constructed outside a Revit session — the Tests
    /// project references Revit as metadata only — so a plan that is plain data
    /// is the only way the mapping table from the checklist can be pinned by a
    /// test. NwcExporter's job is then a mechanical field-by-field application of
    /// what is decided here.
    /// </summary>
    public sealed class NwcExportPlan
    {
        /// <summary>Export the temporary view, not the whole model.</summary>
        public bool ExportCurrentViewOnly => true;

        public NwcCoordinates Coordinates { get; private set; }

        /// <summary>Element IDs tick on the checklist — coordinators match clashes back to elements with these.</summary>
        public bool ExportElementIds => true;

        /// <summary>
        /// "Convert linked files" stays unticked, with no option to change it.
        /// Every discipline exports its own NWC and the coordinator federates in
        /// Navisworks; including links here would ship the same geometry in two
        /// files and double-count clashes.
        /// </summary>
        public bool ExportLinks => false;

        public bool ConvertLinkedCADFormats => true;
        public bool ExportRoomAsAttribute => true;
        public bool ExportRoomGeometry => true;
        public bool DivideFileIntoLevels => true;
        public bool ExportUrls => true;

        /// <summary>Construction parts unticked on the checklist.</summary>
        public bool ExportParts => false;

        public bool ConvertElementProperties => false;
        public bool ConvertLights => false;
        public double FacetingFactor => 1.0;
        public bool FindMissingMaterials => true;

        /// <summary>Parameters = All on the checklist.</summary>
        public string Parameters => "All";

        /// <summary>Detail level of the temporary view.</summary>
        public string ViewDetailLevel => "Fine";

        /// <summary>The checklist's view is a plain isometric with no crop.</summary>
        public bool ViewIsIsometric => true;
        public bool ViewSectionBoxEnabled => false;

        /// <summary>
        /// Groups hidden in the temporary view. Model categories are absent
        /// deliberately: they are what the export is for.
        /// </summary>
        public IReadOnlyList<NwcHiddenCategoryGroup> HiddenCategoryGroups { get; private set; }

        /// <summary>Name of the .nwc, without a folder.</summary>
        public string FileName { get; private set; }

        /// <summary>
        /// True when the user asked to purge AND this build can. Kept as one flag
        /// so the exporter never has to re-derive the capability — and so a
        /// request that cannot be honoured is visible here rather than silently
        /// dropped: <see cref="PurgeSkippedNote"/> is the reason.
        /// </summary>
        public bool PurgeUnused { get; private set; }

        /// <summary>Null unless the user asked for a purge this build cannot run.</summary>
        public string PurgeSkippedNote { get; private set; }

        /// <summary>
        /// Build the plan for one export.
        /// </summary>
        /// <param name="modelFileName">The rvt's file name; the .nwc takes its stem.</param>
        /// <param name="purgeSupported">
        /// Whether this build can purge at all — pass
        /// <see cref="NwcPurgeSupport.CompiledIn"/> from the add-in, or an
        /// explicit value from a test.
        /// </param>
        public static NwcExportPlan From(
            NwcExportSettings settings,
            string modelFileName,
            bool purgeSupported)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            bool purge = settings.PurgeUnused && purgeSupported;

            return new NwcExportPlan
            {
                Coordinates = settings.Coordinates,
                FileName = NwcFileName.FromModelName(modelFileName),
                PurgeUnused = purge,
                PurgeSkippedNote = settings.PurgeUnused && !purgeSupported
                    ? NwcPurgeSupport.UnsupportedNote
                    : null,
                HiddenCategoryGroups = new[]
                {
                    NwcHiddenCategoryGroup.Annotation,
                    NwcHiddenCategoryGroup.AnalyticalModel,
                    NwcHiddenCategoryGroup.Imported,
                    NwcHiddenCategoryGroup.RevitLinks
                }
            };
        }
    }

    /// <summary>
    /// The .nwc's name: the model's stem and nothing else.
    ///
    /// Same version-suffix rule as the sync upload name, for the same reason — a
    /// model downloaded from Cloud Docs arrives as "Block-A-v3.rvt", and an
    /// export called "Block-A-v3.nwc" would be linked beside the previous
    /// "Block-A.nwc" instead of replacing it.
    /// </summary>
    public static class NwcFileName
    {
        public static string FromModelName(string modelFileName)
        {
            if (string.IsNullOrWhiteSpace(modelFileName))
                throw new ArgumentException("A model file name is required.", nameof(modelFileName));

            string stem = Path.GetFileNameWithoutExtension(modelFileName);
            if (string.IsNullOrEmpty(stem)) stem = modelFileName;

            stem = StripVersionSuffix(stem);
            stem = Sanitize(stem);

            return stem + ".nwc";
        }

        /// <summary>"Block-A-v3" → "Block-A". Shared with the sync dialog's upload name.</summary>
        public static string StripVersionSuffix(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return stem;

            string stripped = Regex
                .Replace(stem, @"[-_]v\d+$", "", RegexOptions.IgnoreCase)
                .Trim();

            return stripped.Length == 0 ? stem : stripped;
        }

        /// <summary>
        /// Replaces what Windows will not accept in a file name. Revit view and
        /// model names allow characters a path does not, and Document.Export
        /// throws on them rather than sanitising — which would surface as
        /// "export failed" with no clue why.
        /// </summary>
        private static string Sanitize(string stem)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(stem.Length);

            foreach (char c in stem)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            string cleaned = sb.ToString().Trim();
            // Trailing dots and spaces are legal in the string and illegal on disk.
            cleaned = cleaned.TrimEnd('.', ' ');

            return cleaned.Length == 0 ? "model" : cleaned;
        }
    }

    /// <summary>
    /// Whether this build can purge unused elements before exporting.
    ///
    /// `Document.GetUnusedElements` is Revit 2024 API, but the net48 payload is
    /// compiled against 2023 references and serves BOTH 2023 and 2024 — so the
    /// call does not exist to be made there, whatever host it lands in. Purge is
    /// therefore a 2025+ capability in this add-in, exactly as
    /// `Mutators.PurgeUnused` already reports it. Making it work on 2024 would
    /// mean a fourth target compiled against 2024 refs; nothing here can fake it.
    /// </summary>
    public static class NwcPurgeSupport
    {
        /// <summary>True when this assembly was compiled against a Revit API that has the call.</summary>
        public const bool CompiledIn =
#if REVIT2023_24
            false;
#else
            true;
#endif

        public const string UnsupportedNote =
            "Purge unused needs Revit 2025 or newer — the export will run without it.";

        /// <summary>
        /// Whether the host Revit version is one where purge is available. Used
        /// for the dialog's note; the hard gate is <see cref="CompiledIn"/>,
        /// since a 2024 host runs the net48 payload.
        /// </summary>
        public static bool IsSupported(string revitVersionNumber)
        {
            int year;
            if (!int.TryParse((revitVersionNumber ?? "").Trim(), out year)) return false;
            return year >= 2025;
        }
    }
}
