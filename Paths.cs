using System;
using System.IO;

namespace BinaConnector
{
    /// <summary>
    /// Centralized paths under %APPDATA%\BINA\BinaConnector\.
    /// All persistent files (config, settings, EULA acceptance, logs) live here.
    /// </summary>
    internal static class Paths
    {
        private static readonly string AppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BINA", "BinaConnector");

        public static string ConfigFile => Path.Combine(AppDataRoot, "config.json");
        public static string SettingsFile => Path.Combine(AppDataRoot, "settings.json");
        public static string EulaAcceptedFile => Path.Combine(AppDataRoot, "eula-accepted.json");
        public static string LogDirectory => Path.Combine(AppDataRoot, "logs");

        public static void EnsureDirectories()
        {
            if (!Directory.Exists(AppDataRoot)) Directory.CreateDirectory(AppDataRoot);
            if (!Directory.Exists(LogDirectory)) Directory.CreateDirectory(LogDirectory);
        }
    }
}
