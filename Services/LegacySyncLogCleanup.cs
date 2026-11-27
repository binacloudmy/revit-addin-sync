using System;
using System.Collections.Generic;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// AI-51: the pre-0.0.x sync services appended token-bearing logs to the
    /// user's Desktop (`autodesk_upload_log.txt` / `bina_upload_log.txt`).
    /// Those writers are gone, but the files remain on machines that ran an
    /// older build — and Desktop is commonly synced to OneDrive, so the old
    /// tokens can leave the machine.
    ///
    /// Delete the two known files on every startup — idempotent, and a no-op
    /// with near-zero cost once they are gone. Best-effort: only these exact
    /// names in the Desktop root(s) are touched, a failure is swallowed, and a
    /// missing file is a no-op. Never fatal.
    /// </summary>
    internal static class LegacySyncLogCleanup
    {
        internal static readonly string[] LegacyFileNames =
        {
            "autodesk_upload_log.txt",
            "bina_upload_log.txt",
        };

        internal static void Purge() => Purge(DesktopFolders());

        /// <summary>Test seam: purge an explicit set of folders.</summary>
        internal static void Purge(IEnumerable<string> folders)
        {
            try
            {
                if (folders == null) return;
                foreach (var folder in folders)
                {
                    if (string.IsNullOrEmpty(folder)) continue;
                    foreach (var name in LegacyFileNames)
                    {
                        try
                        {
                            var path = Path.Combine(folder, name);
                            if (File.Exists(path)) File.Delete(path);
                        }
                        catch { /* best-effort per file; never fatal */ }
                    }
                }
            }
            catch { /* best-effort; cleanup must never affect startup */ }
        }

        private static IEnumerable<string> DesktopFolders()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var special in new[]
            {
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory,
            })
            {
                string path = null;
                try { path = Environment.GetFolderPath(special); } catch { }
                if (!string.IsNullOrEmpty(path) && seen.Add(path)) yield return path;
            }
        }
    }
}
