using System.IO;
using Newtonsoft.Json;

namespace BinaConnector
{
    /// <summary>User preferences (default discipline, confirm-before-upload).</summary>
    public class Settings
    {
        /// <summary>"Ask" / "Architecture" / "Structure" / "HVAC" / "Electrical" / "MainFile"</summary>
        public string DefaultDiscipline { get; set; } = "Ask";

        public bool ConfirmBeforeUploading { get; set; } = false;
    }

    public static class SettingsStore
    {
        public static Settings Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    string json = File.ReadAllText(Paths.SettingsFile);
                    return JsonConvert.DeserializeObject<Settings>(json) ?? new Settings();
                }
            }
            catch { /* fall through to defaults */ }
            return new Settings();
        }

        public static void Save(Settings settings)
        {
            try
            {
                Paths.EnsureDirectories();
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(Paths.SettingsFile, json);
            }
            catch { /* persistence failures are non-fatal */ }
        }
    }
}
