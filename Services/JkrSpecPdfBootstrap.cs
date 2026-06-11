using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Locate + shell-open JKR BIM spec PDFs shipped alongside the addin DLL at
    /// "&lt;addin-dir&gt;/Resources/bim-reference/*.pdf" so users can deep-link into the
    /// clause a compliance issue cites.
    /// PDFs are deployed as Content (not embedded) — 170MB of embedded PDF would bloat
    /// the addin DLL and slow every build. The build's CopyToOutputDirectory places
    /// them next to the DLL at install time.
    /// </summary>
    public static class JkrSpecPdfBootstrap
    {
        private const string SUBDIR = "bim-reference";

        /// <summary>
        /// Shell-open the PDF matching an IssueVm.Spec.Doc key like "doc03" / "doc09".
        /// Silently no-ops when the key is missing or the PDF hasn't been deployed.
        /// </summary>
        public static void Open(string docKey)
        {
            if (string.IsNullOrEmpty(docKey)) return;
            try
            {
                var path = ResolvePath(docKey);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine($"[BINA] PDF not found for key '{docKey}'");
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BINA] PDF open failed: {ex.Message}");
            }
        }

        private static string ResolvePath(string docKey)
        {
            // "doc03" → "03"; "03" → "03".
            var numeric = docKey.StartsWith("doc", StringComparison.OrdinalIgnoreCase)
                ? docKey.Substring(3)
                : docKey;

            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var pdfDir = Path.Combine(asmDir, "Resources", SUBDIR);
            if (!Directory.Exists(pdfDir))
            {
                // Fallback for install layouts that flatten Resources into the addin root.
                pdfDir = Path.Combine(asmDir, SUBDIR);
                if (!Directory.Exists(pdfDir)) return null;
            }

            // Filenames like "03_BIM_PIAWAIAN JKR.pdf" — match on numeric prefix followed
            // by underscore or space so we don't accidentally pick "03x" or similar.
            return Directory.EnumerateFiles(pdfDir, "*.pdf")
                .FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.StartsWith(numeric + "_", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(numeric + " ", StringComparison.OrdinalIgnoreCase);
                });
        }
    }
}
