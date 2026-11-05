using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Which coordinate system the exported NWC carries.
    ///
    /// The two checklists disagree: the newer, hand-annotated <c>Revit to NWC rev a</c>
    /// says Shared, the older TCDR01 slides say Project Internal. Rev a wins as the
    /// default because a federated Navisworks model only lines up if every discipline
    /// exported in the same system, and that is the checklist the coordinators tick
    /// today. The other choice stays one dropdown away for models that were never
    /// surveyed.
    /// </summary>
    public enum NwcCoordinates
    {
        /// <summary>Shared / survey coordinates — the default.</summary>
        Shared = 0,

        /// <summary>Project internal coordinates.</summary>
        Internal = 1
    }

    /// <summary>
    /// What the temporary export view shows, straight from the checklist.
    ///
    /// Held as data, next to the settings rather than inside the exporter, so a test
    /// can assert the rules without a Revit document — the rules are the part that can
    /// be wrong, and the code that applies them is three lines of Revit API.
    /// </summary>
    public sealed class NwcCategoryRules
    {
        /// <summary>Model categories stay visible: the model is the export.</summary>
        public bool ModelVisible => true;

        /// <summary>Annotation categories hidden — no room tags, no dimensions in Navisworks.</summary>
        public bool HideAnnotationCategories => true;

        /// <summary>Analytical model categories hidden.</summary>
        public bool HideAnalyticalCategories => true;

        /// <summary>Imported categories hidden (imported CAD, not linked).</summary>
        public bool HideImportedCategories => true;

        /// <summary>
        /// Revit links hidden: every discipline exports its own NWC and the coordinator
        /// federates them in Navisworks, so linking them here would double-count models.
        /// </summary>
        public bool HideRevitLinks => true;

        /// <summary>
        /// View filters are left exactly as the user has them — the checklist does not
        /// ask for them to be cleared, and clearing them would change what v1 exports
        /// versus v2 without anyone asking.
        /// </summary>
        public bool LeavesFiltersAlone => true;
    }

    /// <summary>
    /// The NavisworksExportOptions the checklist asks for, as plain values.
    ///
    /// One property per tick in the docs screenshot, deliberately not the Revit type:
    /// this is the policy, and it is unit-tested. <see cref="NwcExporter"/> maps it onto
    /// NavisworksExportOptions one-to-one.
    /// </summary>
    public sealed class NwcOptionsSpec
    {
        /// <summary>docs: Export = Current view. Always a view; the scope is never the whole model.</summary>
        public bool ExportScopeIsView { get; set; }

        /// <summary>docs: the Element IDs tick.</summary>
        public bool ExportElementIds { get; set; }

        public NwcCoordinates Coordinates { get; set; }

        /// <summary>docs: "Convert linked files" unticked.</summary>
        public bool ExportLinks { get; set; }

        /// <summary>Linked CAD still converts — only Revit links are excluded.</summary>
        public bool ConvertLinkedCADFormats { get; set; }

        public bool ExportRoomAsAttribute { get; set; }
        public bool ExportRoomGeometry { get; set; }
        public bool DivideFileIntoLevels { get; set; }
        public bool ExportUrls { get; set; }

        /// <summary>docs: "Convert construction parts" unticked.</summary>
        public bool ExportParts { get; set; }

        public bool ConvertElementProperties { get; set; }
        public bool ConvertLights { get; set; }

        /// <summary>docs: 1. Finer faceting makes a huge NWC for no visible gain.</summary>
        public double FacetingFactor { get; set; }

        public bool FindMissingMaterials { get; set; }

        /// <summary>docs: Parameters = All.</summary>
        public bool ExportAllParameters { get; set; }
    }

    /// <summary>
    /// Everything the NWC export needs beyond the document, and the rules that can be
    /// reasoned about without one (ClickUp 86d49v9ak).
    /// </summary>
    public sealed class NwcExportSettings
    {
        /// <summary>
        /// Name of the scratch view the export runs through. Never shown to the user:
        /// it lives inside a transaction group that is rolled back, so it is gone by
        /// the time the outcome dialog draws.
        /// </summary>
        public const string TempViewName = "BINA NWC Export (temporary)";

        /// <summary>
        /// Where a missing exporter is downloaded from. Autodesk ships it separately
        /// from Revit, one build per Revit year, and the year must match — hence a page
        /// rather than a direct file link: the tab is labelled "Navisworks NWC Export
        /// Utility" (Autodesk support article "How to find the Navisworks Exporters
        /// for Revit").
        /// </summary>
        public const string ExporterDownloadUrl = "https://www.autodesk.com/products/navisworks/3d-viewers";

        /// <summary>Shared coordinates, no purge — the checklist defaults.</summary>
        public static NwcExportSettings Default =>
            new NwcExportSettings { Coordinates = NwcCoordinates.Shared, PurgeUnused = false };

        public NwcCoordinates Coordinates { get; set; }

        /// <summary>
        /// Tick is off by default and the step is optional per the docs. Purging
        /// changes the model that is on screen, so it stays something the user asks for.
        /// </summary>
        public bool PurgeUnused { get; set; }

        /// <summary>The category rules this export applies. Static policy, no per-export state.</summary>
        public NwcCategoryRules CategoryRules => new NwcCategoryRules();

        /// <summary>
        /// Purge needs <c>Document.GetUnusedElements</c>, which is Revit 2024 API — and
        /// this add-in is compiled once against 2023 references to serve 2023 and 2024
        /// (see <c>REVIT2023_24</c>). So the capability is decided by the Revit year at
        /// runtime, not by the compile-time symbol alone, and Revit 2023 gets an
        /// honest "not available" instead of a purge that silently does nothing.
        /// </summary>
        public static bool IsPurgeAvailable(int revitYear) => revitYear >= 2024;

        /// <summary>
        /// The spec the checklist describes, adjusted by the caller's choices.
        /// </summary>
        public NwcOptionsSpec ToOptionsSpec() => new NwcOptionsSpec
        {
            ExportScopeIsView = true,
            ExportElementIds = true,
            Coordinates = Coordinates,
            ExportLinks = false,
            ConvertLinkedCADFormats = true,
            ExportRoomAsAttribute = true,
            ExportRoomGeometry = true,
            DivideFileIntoLevels = true,
            ExportUrls = true,
            ExportParts = false,
            ConvertElementProperties = false,
            ConvertLights = false,
            FacetingFactor = 1,
            FindMissingMaterials = true,
            ExportAllParameters = true
        };

        /// <summary>
        /// The NWC this model exports to: <c>&lt;rvt stem&gt;.nwc</c>.
        ///
        /// Same naming rule as the sync upload name, and the same reason — the version
        /// suffix a Cloud Docs download carries (<c>Model-v3.rvt</c>) is not part of any
        /// model's real name, and an NWC called <c>Model-v3.nwc</c> beside a design
        /// called <c>Model</c> reads as a different model to everyone who federates it.
        /// </summary>
        public static string BuildFileName(string rvtFileNameOrPath)
        {
            string name = string.IsNullOrWhiteSpace(rvtFileNameOrPath) ? "Model" : rvtFileNameOrPath;
            name = System.IO.Path.GetFileName(name);

            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(stem)) stem = name;

            stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_]v\d+$", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return SanitizeFileStem(stem) + ".nwc";
        }

        /// <summary>
        /// Characters a file name may not carry. Listed rather than taken from
        /// <c>Path.GetInvalidFileNameChars()</c>, because that answer depends on the OS
        /// the caller runs on: the plugin runs on Windows, the unit tests run wherever
        /// CI is, and a name that is legal on Linux (<c>Model:2</c>) is not legal on
        /// Windows. The export folder is always a Windows path.
        /// </summary>
        public static string SanitizeFileStem(string stem)
        {
            var invalid = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0' };
            var sb = new System.Text.StringBuilder(stem == null ? 0 : stem.Length);

            foreach (char c in stem ?? string.Empty)
            {
                bool bad = Array.IndexOf(invalid, c) >= 0 || c < ' ';
                sb.Append(bad ? '_' : c);
            }

            // Trailing dots and spaces are stripped by Windows itself, which would make
            // the file the exporter writes differ from the name we hand to the link API.
            string cleaned = sb.ToString().TrimEnd(' ', '.');

            // Nothing readable survived: "???" is "___" and "..." is "". Both mean the
            // same thing — the model's name gave us nothing to work with — and a file
            // called "___" is not a name to hand a coordinator either.
            return HasReadableCharacter(cleaned) ? cleaned : "Model";
        }

        private static bool HasReadableCharacter(string value)
        {
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) return true;
            }

            return false;
        }
    }

    /// <summary>Why an NWC export could not happen — each one has its own user-facing fix.</summary>
    public enum NwcExportFailure
    {
        /// <summary>The Navisworks Exporter for this Revit year is not installed on this PC.</summary>
        ExporterMissing = 0,

        /// <summary>The chosen output folder cannot be written to.</summary>
        FolderNotWritable = 1,

        /// <summary>Revit's own exporter threw, or wrote nothing.</summary>
        ExportFailed = 2
    }

    /// <summary>
    /// A failed export, typed so callers can react (offer the download, ask for another
    /// folder) instead of pattern-matching on English.
    /// </summary>
    public sealed class NwcExportException : Exception
    {
        public NwcExportFailure Kind { get; }

        /// <summary>True when a second attempt could plausibly succeed.</summary>
        public bool IsRetryable => Kind == NwcExportFailure.ExportFailed;

        public NwcExportException(NwcExportFailure kind, string message) : base(message)
        {
            Kind = kind;
        }
    }

    /// <summary>What an export produced, for the outcome dialog.</summary>
    public sealed class NwcExportResult
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }

        /// <summary>
        /// What else happened, in the user's words — the purge count, a warning that the
        /// exporter reported something odd. Null when there is nothing to add.
        /// </summary>
        public string Action { get; set; }
    }
}
