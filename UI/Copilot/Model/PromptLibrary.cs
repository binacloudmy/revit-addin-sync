using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>A reusable prompt in the library — curated (ships with the
    /// add-in) or user-saved.</summary>
    public class PromptDef
    {
        public string Id { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Prompt library: curated prompts are hard-coded (same source-of-truth
    /// pattern as CopilotCatalog — updates ship with the add-in); user-saved
    /// prompts persist to %APPDATA%\RevitWebAppSync\copilot-prompts.json
    /// (separate file, same pattern as CopilotPrefs).
    /// </summary>
    public static class PromptLibrary
    {
        // ponytail: curated list lives in code; move server-side when prompts need to change between releases
        public static readonly IReadOnlyList<PromptDef> Curated = new List<PromptDef>
        {
            new PromptDef { Id = "cur-audit-warnings",  Category = "Audit",     Title = "Model warnings summary",   Text = "Summarize the warnings in this model grouped by type, and list the worst offenders" },
            new PromptDef { Id = "cur-audit-rooms",     Category = "Audit",     Title = "Unplaced / unbounded rooms", Text = "Find all unplaced, unenclosed or redundant rooms in the model" },
            new PromptDef { Id = "cur-audit-links",     Category = "Audit",     Title = "Check linked files",       Text = "List all linked files and imports, and flag any that are unloaded or not found" },
            new PromptDef { Id = "cur-qa-untagged",     Category = "QA",        Title = "Untagged rooms/doors",     Text = "Find all untagged rooms and doors on the active level" },
            new PromptDef { Id = "cur-qa-mirrored",     Category = "QA",        Title = "Mirrored doors/windows",   Text = "Find all mirrored doors and windows in the model" },
            new PromptDef { Id = "cur-qa-duplicates",   Category = "QA",        Title = "Duplicate elements",       Text = "Find elements that are duplicated in the same place" },
            new PromptDef { Id = "cur-model-walls",     Category = "Modeling",  Title = "Create walls on level",    Text = "Create exterior walls on Level 2 along grid A–F" },
            new PromptDef { Id = "cur-model-tag",       Category = "Modeling",  Title = "Tag rooms on level",       Text = "Tag all rooms on Level 1 with name and number" },
            new PromptDef { Id = "cur-sched-doors",     Category = "Schedules", Title = "Door schedule",            Text = "Generate a door schedule for the whole model with mark, type, level and fire rating" },
            new PromptDef { Id = "cur-sched-areas",     Category = "Schedules", Title = "Room area schedule",       Text = "Generate a room schedule with name, number, level and area" },
        };

        // ─── User-saved prompts ──────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "copilot-prompts.json");
            }
        }

        private static List<PromptDef> _user;

        public static IReadOnlyList<PromptDef> User
        {
            get
            {
                if (_user != null) return _user;
                try
                {
                    if (File.Exists(FilePath))
                        _user = JsonConvert.DeserializeObject<List<PromptDef>>(File.ReadAllText(FilePath));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Prompt library load failed: {ex.Message}");
                }
                return _user = _user ?? new List<PromptDef>();
            }
        }

        /// <summary>Save the composer text as a user prompt (title = first line,
        /// truncated). No-op on blank text or an exact duplicate.</summary>
        public static PromptDef SaveUser(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            var existing = User.FirstOrDefault(p => p.Text == text);
            if (existing != null) return existing;
            var title = text.Split('\n')[0].Trim();
            if (title.Length > 48) title = title.Substring(0, 48) + "…";
            var def = new PromptDef { Id = "usr-" + Guid.NewGuid().ToString("N"), Category = "My prompts", Title = title, Text = text };
            _user.Insert(0, def);
            Persist();
            return def;
        }

        public static void DeleteUser(string id)
        {
            if (_user == null || _user.RemoveAll(p => p.Id == id) == 0) return;
            Persist();
        }

        private static void Persist()
        {
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(_user, Formatting.Indented)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] Prompt library save failed: {ex.Message}"); }
        }
    }
}
