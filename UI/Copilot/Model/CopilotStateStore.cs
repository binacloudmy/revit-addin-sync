using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Persisted Copilot state — pinned commands, run history, saved commands, sessions.</summary>
    public class CopilotState
    {
        public List<string> Pinned { get; set; } = new List<string>();
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();
        public List<SavedCommand> SavedCommands { get; set; } = new List<SavedCommand>();
        public List<ChatSession> Sessions { get; set; } = new List<ChatSession>();
    }

    /// <summary>
    /// Loads/saves Copilot state to %APPDATA%\RevitWebAppSync\copilot-state.json.
    /// </summary>
    public static class CopilotStateStore
    {
        private const int MaxHistory = 100;
        private const int MaxSessions = 50;
        private const int MaxSaved = 200;

        // Persist using property serialization (default) AND fields, so the existing public-field
        // model classes (HistoryEntry, ChatMessage, …) round-trip.
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

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
                    var state = JsonConvert.DeserializeObject<CopilotState>(json, _settings);
                    if (state != null)
                    {
                        state.Pinned ??= new List<string>();
                        state.History ??= new List<HistoryEntry>();
                        state.SavedCommands ??= new List<SavedCommand>();
                        state.Sessions ??= new List<ChatSession>();
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Copilot state load failed: {ex.Message}");
            }

            return new CopilotState();
        }

        public static void Save(
            IEnumerable<string> pinned,
            IEnumerable<HistoryEntry> history,
            IEnumerable<SavedCommand> savedCommands = null,
            IEnumerable<ChatSession> sessions = null)
        {
            try
            {
                var state = new CopilotState
                {
                    Pinned = pinned?.ToList() ?? new List<string>(),
                    History = (history ?? Enumerable.Empty<HistoryEntry>()).Take(MaxHistory).ToList(),
                    SavedCommands = (savedCommands ?? Enumerable.Empty<SavedCommand>()).Take(MaxSaved).ToList(),
                    Sessions = (sessions ?? Enumerable.Empty<ChatSession>()).Take(MaxSessions).ToList(),
                };
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(state, _settings));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Copilot state save failed: {ex.Message}");
            }
        }
    }
}
