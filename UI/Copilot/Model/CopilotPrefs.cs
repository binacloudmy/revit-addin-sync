using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>Slash-command tool ids the user pinned (shown first in Quick access).</summary>
        public List<string> PinnedTools { get; set; } = new List<string>();

        /// <summary>Recently used tool ids (most-recent first, capped) — fills out Quick access.</summary>
        public List<string> RecentTools { get; set; } = new List<string>();

        public bool IsPinned(string id) => PinnedTools != null && PinnedTools.Contains(id);

        /// <summary>Toggle a pin and persist. Returns the new pinned state.</summary>
        public bool TogglePin(string id)
        {
            PinnedTools = PinnedTools ?? new List<string>();
            bool now;
            if (PinnedTools.Contains(id)) { PinnedTools.Remove(id); now = false; }
            else { PinnedTools.Insert(0, id); now = true; }
            Save();
            return now;
        }

        /// <summary>Record a tool as used (most-recent first, max 4) and persist.</summary>
        public void PushRecent(string id)
        {
            RecentTools = (RecentTools ?? new List<string>()).Where(x => x != id).ToList();
            RecentTools.Insert(0, id);
            if (RecentTools.Count > 4) RecentTools = RecentTools.Take(4).ToList();
            Save();
        }

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
