using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Small persisted UI preferences for the Copilot panel — kept separate from
    /// CopilotStateStore (pinned/history) so toggling a preference never rewrites,
    /// or is clobbered by, the pinned/history file. Stored at
    /// %APPDATA%\RevitWebAppSync\copilot-prefs.json.
    /// </summary>
    public class CopilotPrefs
    {
        /// <summary>Dark ("Slate") theme when true; light otherwise.</summary>
        public bool Dark { get; set; }

        /// <summary>Set once the user submits a star rating — the inline rating
        /// nudge never appears again after this.</summary>
        public bool RatingSubmitted { get; set; }

        /// <summary>Set once the user dismisses the 80%-usage note — it never
        /// reappears after that (the 95% banner is separate and non-dismissible).</summary>
        public bool Warn80Dismissed { get; set; }

        private static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "copilot-prefs.json");
            }
        }

        private static CopilotPrefs _cached;

        public static CopilotPrefs Load()
        {
            if (_cached != null) return _cached;
            try
            {
                if (File.Exists(FilePath))
                {
                    var p = JsonConvert.DeserializeObject<CopilotPrefs>(File.ReadAllText(FilePath));
                    if (p != null) return _cached = p;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Copilot prefs load failed: {ex.Message}");
            }
            return _cached = new CopilotPrefs();
        }

        public void Save()
        {
            _cached = this;
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] Copilot prefs save failed: {ex.Message}"); }
        }
    }
}
