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

        /// <summary>Lifetime count of prompts this drafter has sent (all
        /// sessions). The rating nudge waits for RatingNudgeMinPrompts — asking
        /// after two exchanges was too early (operator decision 2026-08-30).</summary>
        public int PromptsSent { get; set; }

        public const int RatingNudgeMinPrompts = 20;

        /// <summary>Composer "Reasoning" toggle (2026-08-02 spec) — when false the
        /// client ignores `reasoning` SSE frames entirely (no timeline rendered),
        /// same idea as the theme toggle: persisted, defaults on.</summary>
        public bool ReasoningEnabled { get; set; } = true;

        /// <summary>Stream v2 kill switch (copilot-stream-v2-hermes-parity spec,
        /// "T1-T5 addin behind one internal flag"). Defaults ON — against an old
        /// backend the segment feature-detect keeps rendering legacy anyway, so
        /// the flag exists purely to force-disable segmented rendering if UAT
        /// finds a defect (set false in copilot-prefs.json; no UI).</summary>
        public bool StreamV2Enabled { get; set; } = true;

        /// <summary>Composer "Action Mode" chip (2026-08-02 addendum): false =
        /// Ask first (default — every write batch shows the approval card),
        /// true = Auto (write batches where every call opted out of
        /// confirmation run without a card; anything else still asks, and
        /// codegen always asks regardless of this flag). Per-machine,
        /// persisted the same way as ReasoningEnabled/Dark.</summary>
        public bool AutoApproveWrites { get; set; } = false;

        /// <summary>Saved Commands J1 — the last Mine catalog rows (JSON) + the
        /// catalog ETag, so an offline/signed-out start still lists Mine (A5)
        /// and an unchanged catalog costs a 304.</summary>
        public string SavedCommandsJson { get; set; } = "";
        public string SavedCommandsEtag { get; set; } = "";

        /// <summary>Slash-command tool ids the user pinned (shown first in Quick access).</summary>
        public List<string> PinnedTools { get; set; } = new List<string>();

        /// <summary>Recently used tool ids (most-recent first, capped) — fills out Quick access.</summary>
        public List<string> RecentTools { get; set; } = new List<string>();

        /// <summary>Which near-limit notice the user dismissed, as
        /// "{resets_at}:{band}". Storing the band and the quota period (rather than a
        /// bare bool) means a dismissal sticks for THAT warning only: crossing from
        /// 80% into 95%, or rolling into a new quota month, produces a new key and
        /// the notice returns. A single value is enough — only the current one can
        /// ever be showing.</summary>
        public string UsageNoticeDismissed { get; set; }

        /// <summary>True when this exact notice has already been dismissed.</summary>
        public bool IsUsageNoticeDismissed(string key) =>
            !string.IsNullOrEmpty(key) && UsageNoticeDismissed == key;

        /// <summary>Remember a dismissal and persist.</summary>
        public void DismissUsageNotice(string key)
        {
            UsageNoticeDismissed = key;
            Save();
        }

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
