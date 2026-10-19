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

        /// <summary>
        /// File types a BIM folder holds that Revit cannot open. The refusal is
        /// keyed on this list, not on "anything that is not a Revit extension":
        /// a model named "Tower 1.5" has the "extension" 5 by the last-dot rule,
        /// and refusing it would take a download away from a real Revit model.
        /// An extension in neither list is treated as part of the name.
        /// </summary>
        private static readonly string[] NonRevitExtensions =
        {
            "dwg", "dxf", "dwf", "dwfx", "dgn", "nwc", "nwd", "nwf", "ifc", "ifczip", "ifcxml",
            "pdf", "skp", "3dm", "fbx", "obj", "stl", "step", "stp", "igs", "iges", "rcp", "rcs",
            "e57", "las", "laz", "jpg", "jpeg", "png", "tif", "tiff", "doc", "docx", "xls", "xlsx",
            "ppt", "pptx", "csv", "txt", "zip",
        };

        public const string NotRevitReason =
            "Only Revit files (.rvt, .rfa, .rte) can be downloaded into Revit. Open this one in BINA on the web.";

        /// <summary>
        /// Whether the version can be opened in the BINA web viewer. A label, not a
        /// download blocker: a Revit file downloads whatever its viewer state.
        ///
        /// odaViewerState is bina-be's one derived answer for the new converter, so
        /// the add-in does not re-derive it from the raw conversion columns. An
        /// older bina-be does not send it; there the old rule applies: an Autodesk
        /// URN or a completed xkt conversion means viewable (the same rule the
        /// Civil 3D add-in keeps, so both label one version the same way).
        /// </summary>
        public static string DescribeViewerState(string odaViewerState, string urnInBase64, string xktConversionStatus = null)
        {
            if (string.IsNullOrEmpty(odaViewerState))
            {
                if (!string.IsNullOrEmpty(urnInBase64)) return "viewable";
                if (string.Equals(xktConversionStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                    return "viewable";
                return "not converted";
            }

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
        /// True for a file Revit may take. Only a known non-Revit type (DWG, NWC,
        /// IFC…) is refused. No extension, or one that is not a known file type
        /// ("Tower 1.5" → 5), counts as a Revit file: every model the add-in
        /// synced is an .rvt, and refusing a file only because an older server sent
        /// no extension, or because the name has a dot, would take away a download
        /// that worked.
        /// </summary>
        public static bool IsRevitFile(string extension)
        {
            if (extension == null) return true;
            string ext = extension.ToLowerInvariant();
            return Array.IndexOf(RevitExtensions, ext) >= 0
                || Array.IndexOf(NonRevitExtensions, ext) < 0;
        }

        /// <summary>
        /// The extension to save under, with the dot. Whitelisted: rvt, rfa or rte
        /// as sent, else .rvt. The server's fileExtension is untrusted text and
        /// ends up in a path, so it is never used as is.
        /// </summary>
        public static string SaveExtension(string extension)
        {
            string ext = extension == null ? null : extension.ToLowerInvariant();
            return "." + (Array.IndexOf(RevitExtensions, ext) >= 0 ? ext : "rvt");
        }

        /// <summary>
        /// The model name without its file extension, for the save path. Only a
        /// known file type is stripped ("Tower 1.5" stays whole), and it is read by
        /// hand because Path.GetFileNameWithoutExtension throws on .NET Framework
        /// for names holding " &lt; &gt; | or control characters. Trailing dots and
        /// spaces go too, which Windows would drop from a folder name anyway.
        /// "model" when nothing is left.
        /// </summary>
        public static string StemOf(string name)
        {
            string stem = (name ?? "").Trim();
            int dot = stem.LastIndexOf('.');
            if (dot > 0)
            {
                string ext = stem.Substring(dot + 1).ToLowerInvariant();
                if (Array.IndexOf(RevitExtensions, ext) >= 0 || Array.IndexOf(NonRevitExtensions, ext) >= 0)
                    stem = stem.Substring(0, dot);
            }

            stem = stem.TrimEnd('.', ' ');
            return stem.Length == 0 ? "model" : stem;
        }
    }
}
