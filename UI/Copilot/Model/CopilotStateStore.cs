using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Persisted Copilot state — pinned commands + run history.</summary>
    public class CopilotState
    {
        public List<string> Pinned { get; set; } = new List<string>();
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
    }

    /// <summary>
    /// Loads/saves Copilot pinned commands + history to %APPDATA%\RevitWebAppSync\copilot-state.json.
    /// First run seeds from the catalog (count-doors pinned, SEED_HISTORY).
    /// </summary>
    public static class CopilotStateStore
    {
        private const int MaxHistory = 100;

        private static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "copilot-state.json");
            }
        }

        public static CopilotState Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var state = JsonConvert.DeserializeObject<CopilotState>(json);
                    if (state != null) return state;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Copilot state load failed: {ex.Message}");
            }

            // First run — seed defaults.
            return new CopilotState
            {
                Pinned = new List<string> { "count-doors" },
                History = CopilotCatalog.SeedHistory.ToList(),
            };
        }

        public static void Save(IEnumerable<string> pinned, IEnumerable<HistoryEntry> history)
        {
            try
            {
                var state = new CopilotState
                {
                    Pinned = pinned?.ToList() ?? new List<string>(),
                    History = (history ?? Enumerable.Empty<HistoryEntry>()).Take(MaxHistory).ToList(),
                };
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Copilot state save failed: {ex.Message}");
            }
        }
    }
}
