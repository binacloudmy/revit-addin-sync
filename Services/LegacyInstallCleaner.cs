using System;
using System.IO;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Removes manifests of OLD parallel installs of this addin (pre-loader
    /// direct-load .addin files, e.g. the legacy "BINA / Upload to BINA"
    /// App-Store-era build found in C:\ProgramData). A second live copy
    /// double-subscribes events, fights over localhost ports, and can break
    /// this build's startup entirely — the v0.0.1 field failure.
    ///
    /// Only .addin manifests are deleted (a DLL without a manifest is never
    /// loaded); leftover DLLs may be file-locked by the running session
    /// anyway. Deletion takes effect on the NEXT Revit start. Runs as the
    /// user — a deny on ProgramData is logged and skipped, never fatal.
    /// </summary>
    public static class LegacyInstallCleaner
    {
        public static void Purge(UIControlledApplication application)
        {
            var year = application.ControlledApplication.VersionNumber; // "2026"

            string[] addinDirs =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins", year),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Autodesk", "Revit", "Addins", year),
            };

            foreach (var dir in addinDirs)
            {
                if (!Directory.Exists(dir))
                    continue;

                foreach (var manifest in Directory.EnumerateFiles(dir, "*.addin"))
                {
                    try
                    {
                        var content = File.ReadAllText(manifest);

                        // Stale = points straight at the plugin DLL. The loader
                        // manifest (BinaSync.addin → BinaLoader.dll) is the only
                        // legitimate one and never references the plugin.
                        if (content.IndexOf("RevitWebAppSync.dll", StringComparison.OrdinalIgnoreCase) < 0
                            || content.IndexOf("BinaLoader", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        File.Delete(manifest);
                        Log($"deleted stale manifest: {manifest}");
                    }
                    catch (Exception ex)
                    {
                        Log($"could not remove '{manifest}': {ex.Message}");
                    }
                }
            }
        }

        private static void Log(string message)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bina", "RevitSync");
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "updater.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [cleanup] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
