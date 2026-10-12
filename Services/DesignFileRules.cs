using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// What the Download dialog and the rollback picker say about a model
    /// version, and whether Revit can take it (14zefhy1rfc). Shared by both
    /// windows, and pure, so Tests/DesignFileRulesTests.cs pins it without Revit
    /// or WPF.
    /// </summary>
    public static class DesignFileRules
    {
        /// <summary>
        /// The types Revit opens as a document: a project, a family and a
        /// template. Everything else in a BIM folder (DWG, NWC, NWD, IFC…) is
        /// listed, so the drafter can see it exists, but not downloaded into
        /// Revit — the add-in opens what it downloads, and Revit cannot open those.
        /// </summary>
        private static readonly string[] RevitExtensions = { "rvt", "rfa", "rte" };

        public const string NotRevitReason =
            "Only Revit files (.rvt, .rfa, .rte) can be downloaded into Revit. Open this one in BINA on the web.";

        /// <summary>
        /// Whether the version can be opened in the BINA web viewer. A label, not a
        /// download blocker: a Revit file downloads whatever its viewer state.
        ///
        /// odaViewerState is bina-be's one derived answer for the new converter, so
        /// the add-in does not re-derive it from the raw conversion columns. An
        /// older bina-be does not send it; there the Autodesk URN is the only
        /// signal there is.
        /// </summary>
        public static string DescribeViewerState(string odaViewerState, string urnInBase64)
        {
            if (string.IsNullOrEmpty(odaViewerState))
                return string.IsNullOrEmpty(urnInBase64) ? "not converted" : "viewable";

            switch (odaViewerState)
            {
                case "ready": return "viewable";
                case "partial": return "partly viewable";
                case "converting": return "converting";
                case "needsGlb": return "preparing viewer";
                case "failed": return "conversion failed";
                case "notConverted": return "not converted";
                // A state this build does not know yet: say nothing rather than guess.
                default: return "";
            }
        }

        /// <summary>
        /// Lower-case extension without the dot, or null when there is none:
        /// bina-be's fileExtension when it sends one, else the one in the name.
        /// Read by hand rather than with Path.GetExtension, which throws on
        /// .NET Framework for names with characters a path may not hold.
        /// </summary>
        public static string ExtensionOf(string fileExtension, string name)
        {
            string ext = fileExtension;
            if (string.IsNullOrWhiteSpace(ext))
            {
                if (string.IsNullOrEmpty(name)) return null;
                int dot = name.LastIndexOf('.');
                // No dot, or only a leading one (".env"): no extension — bina-be's rule.
                if (dot <= 0) return null;
                ext = name.Substring(dot + 1);
            }

            ext = ext.Trim().TrimStart('.').ToLowerInvariant();
            return ext.Length == 0 ? null : ext;
        }

        /// <summary>
        /// True for a Revit file. An unknown extension counts as one: every model
        /// the add-in synced is an .rvt, and refusing a file only because an older
        /// server sent no extension would take away a download that worked.
        /// </summary>
        public static bool IsRevitFile(string extension) =>
            extension == null || Array.IndexOf(RevitExtensions, extension) >= 0;

        /// <summary>The extension to save under, with the dot; .rvt when unknown.</summary>
        public static string SaveExtension(string extension) => "." + (extension ?? "rvt");
    }
}
