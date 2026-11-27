using System;
using System.IO;

namespace UiHarness
{
    /// <summary>The shot modes flip the theme with the Copilot theme setter, which PERSISTS a manual choice
    /// (copilot-prefs.json: ThemeChosen = true) and so stops the real Revit panes following Revit's
    /// theme. Every shot run is wrapped in Keep: the user's prefs file is put back byte for byte.</summary>
    internal static class PrefsGuard
    {
        private static string PrefsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitWebAppSync", "copilot-prefs.json");

        public static void Keep(Action shots)
        {
            byte[] before = File.Exists(PrefsFile) ? File.ReadAllBytes(PrefsFile) : null;
            try { shots(); }
            finally
            {
                if (before != null) File.WriteAllBytes(PrefsFile, before);
                else if (File.Exists(PrefsFile)) File.Delete(PrefsFile);
            }
        }
    }
}
