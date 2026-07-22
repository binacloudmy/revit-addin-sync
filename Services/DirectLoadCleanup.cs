using System;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Self-heal for legacy direct-load installs (pre-loader era). Those
    /// machines carry a stale RevitWebAppSync.dll + a manifest pointing at it
    /// directly in Addins\&lt;year&gt;\; the loader-loaded payload then collides
    /// with it ("assembly with same name is already loaded" dialog — fleet
    /// incident 2026-07-20). Revit processes manifests alphabetically, so
    /// BinaSync.addin (loader) always wins the load and the stale DLL always
    /// FAILS to load — which means it is not file-locked and can be deleted
    /// right here at runtime. Dialog is gone from the next Revit start.
    ///
    /// The loader pair (BinaSync.addin + BinaLoader.dll) is never touched.
    /// Orphaned dependency DLLs from the old install are left behind — with
    /// no manifest they are inert, and deleting only what we own keeps this
    /// safe. Every step is best-effort: cleanup must never break startup, and
    /// a locked file simply gets retried on the next boot.
    /// </summary>
    public static class DirectLoadCleanup
    {
        /// <summary>Scan all Addins\&lt;year&gt; folders; returns files removed.</summary>
        public static int Run()
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins");
                return CleanRoot(root);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Testable core: root injected so tests use a temp dir.</summary>
        internal static int CleanRoot(string addinsRoot)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(addinsRoot)) return 0;
                foreach (var yearDir in Directory.GetDirectories(addinsRoot))
                {
                    foreach (var file in Directory.GetFiles(yearDir))
                    {
                        try
                        {
                            if (ShouldRemove(file))
                            {
                                File.Delete(file);
                                removed++;
                            }
                        }
                        catch
                        {
                            // Locked (direct copy won the load on this machine)
                            // or already gone — retry next boot, never throw.
                        }
                    }
                }
            }
            catch
            {
            }
            return removed;
        }

        internal static bool ShouldRemove(string path)
        {
            var name = Path.GetFileName(path);

            // The stale payload binaries themselves (.dll/.pdb/.deps.json/...).
            if (name.StartsWith("RevitWebAppSync.", StringComparison.OrdinalIgnoreCase))
                return true;

            // Any manifest that direct-loads the payload. The loader's own
            // BinaSync.addin points at BinaLoader.dll and never matches, but
            // is name-guarded anyway for safety.
            if (name.EndsWith(".addin", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("BinaSync.addin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return File.ReadAllText(path).IndexOf(
                        "RevitWebAppSync.dll", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
