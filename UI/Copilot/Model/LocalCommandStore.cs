#nullable enable
// Saved commands live on this PC, not on the account: one JSON file in the add-in's app-data folder next to
// copilot-prefs.json (per Windows user). Whoever signs in on this PC sees them; another PC does not. The backend is
// never asked to save, update, delete or list them, and old backend-saved commands are neither imported nor touched.
// Revit-free and WPF-free: the file name is a parameter so a test can use a temp folder.
//
// File: { "version": 1, "commands": [ { id, name, template, inputs[], tools_called[], created_utc, edited_utc, run_count } ] }
// A missing, empty or corrupt file is an empty store (a corrupt one is kept beside it as .bad, not overwritten
// silently). Writes go to a temp file first and replace the real one, so a crash never leaves half a file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class LocalCommandInput
    {
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("type")] public string Type = "text";
        [JsonProperty("required")] public bool Required = true;
        [JsonProperty("label")] public string? Label;
    }

    public sealed class LocalCommand
    {
        /// <summary>The slug: what the drafter types after "/" (my-…), unique on this PC.</summary>
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("template")] public string Template = "";
        [JsonProperty("inputs")] public List<LocalCommandInput> Inputs = new List<LocalCommandInput>();
        [JsonProperty("tools_called")] public List<string> ToolsCalled = new List<string>();
        [JsonProperty("created_utc")] public DateTime CreatedUtc;
        [JsonProperty("edited_utc")] public DateTime EditedUtc;
        [JsonProperty("run_count")] public int RunCount;
    }

    internal sealed class LocalCommandFile
    {
        [JsonProperty("version")] public int Version = LocalCommandStore.SchemaVersion;
        [JsonProperty("commands")] public List<LocalCommand> Commands = new List<LocalCommand>();
    }

    public sealed class LocalCommandStore
    {
        public const int SchemaVersion = 1;
        public const int MaxCommands = 200;
        public const int MaxNameLength = 80;
        public const int MaxTemplateLength = 4000;
        public const string FileName = "copilot-commands.json";

        private readonly string _path;
        private readonly Func<IEnumerable<string>> _reserved;
        private readonly object _gate = new object();

        /// <param name="path">The JSON file.</param>
        /// <param name="reservedIds">Ids a saved command may not take (the curated tier's, as the palette knows them).</param>
        public LocalCommandStore(string path, Func<IEnumerable<string>>? reservedIds = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _reserved = reservedIds ?? (() => Enumerable.Empty<string>());
        }

        /// <summary>The store in this Windows user's app-data folder, beside copilot-prefs.json.</summary>
        public static LocalCommandStore Default { get; } = new LocalCommandStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitWebAppSync", FileName),
            () => ToolCatalog.Curated.SelectMany(t => new[] { t.Id, t.BackendId }));

        /// <summary>The file this store reads and writes.</summary>
        public string FilePath => _path;

        // ---- reading ----

        /// <summary>Every saved command, oldest first. Never throws.</summary>
        public IReadOnlyList<LocalCommand> All()
        {
            lock (_gate) return Load();
        }

        public LocalCommand? Find(string? id)
        {
            lock (_gate) return Load().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        }

        // ---- writing ----

        /// <summary>Save a new command from a draft. Null error and the command on success; else a sentence for the sheet.</summary>
        public (LocalCommand? Command, string? Error) Add(SavedCommandDraft draft, DateTime? now = null)
        {
            var problem = Validate(draft);
            if (problem != null) return (null, problem);
            lock (_gate)
            {
                var list = Load();
                if (list.Count >= MaxCommands) return (null, "At most " + MaxCommands + " saved commands on this PC.");
                var stamp = (now ?? DateTime.UtcNow);
                var c = new LocalCommand { Id = UniqueSlug(SavedCommandDraft.SuggestSlug(draft.Name), list.Select(x => x.Id).Concat(_reserved())), CreatedUtc = stamp, EditedUtc = stamp };
                Fill(c, draft);
                list.Add(c);
                var error = Save(list);
                return error == null ? (c, null) : (null, error);
            }
        }

        /// <summary>Change a saved command (its slug stays, so the pins and the history keep pointing at it). Null command when it is gone.</summary>
        public (LocalCommand? Command, string? Error) Update(string id, SavedCommandDraft draft, DateTime? now = null)
        {
            var problem = Validate(draft);
            if (problem != null) return (null, problem);
            lock (_gate)
            {
                var list = Load();
                var c = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                if (c == null) return (null, null);
                Fill(c, draft);
                c.EditedUtc = now ?? DateTime.UtcNow;
                var error = Save(list);
                return error == null ? (c, null) : (null, error);
            }
        }

        /// <summary>False when it was not there.</summary>
        public bool Delete(string id)
        {
            lock (_gate)
            {
                var list = Load();
                var removed = list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                if (removed == 0) return false;
                Save(list);
                return true;
            }
        }

        /// <summary>One more run of this command (the menu's "n runs"). False when it is not a saved command.</summary>
        public bool CountRun(string? id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_gate)
            {
                var list = Load();
                var c = list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                if (c == null) return false;
                c.RunCount++;
                Save(list);
                return true;
            }
        }

        // ---- the catalog's Mine tier ----

        /// <summary>The saved commands as palette / Home / Commands-tab rows.</summary>
        public IReadOnlyList<SlashTool> ToTools() => All().Select(ToTool).ToList();

        public static SlashTool ToTool(LocalCommand c) => new SlashTool
        {
            Id = c.Id, CommandId = c.Id, Category = "Mine",
            Name = c.Name, Subtitle = c.Template,
            Badge = ToolBadge.AiAssisted, IconKey = "ti-user",
            Keywords = c.Name + " " + c.Template,
            Editable = true, PromptTemplate = c.Template, RunCount = c.RunCount,
            Inputs = c.Inputs.Select(i => new SlashInput
            {
                Name = i.Name, Type = i.Type == "number" ? "number" : "text", Required = i.Required,
                Label = string.IsNullOrEmpty(i.Label) ? i.Name : i.Label,
            }).ToList(),
        };

        // ---- slugs ----

        /// <summary>The slug itself when free, else "-2", "-3"… (the base is cut so the whole stays within 40 + "my-").</summary>
        public static string UniqueSlug(string slug, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (!set.Contains(slug)) return slug;
            for (var n = 2; n < 10000; n++)
            {
                var suffix = "-" + n;
                var head = slug.Length + suffix.Length > 43 ? slug.Substring(0, 43 - suffix.Length).TrimEnd('-') : slug;
                var candidate = head + suffix;
                if (!set.Contains(candidate)) return candidate;
            }
            return slug + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // ---- file ----

        private static string? Validate(SavedCommandDraft? d)
        {
            if (d == null) return "Nothing to save.";
            if (string.IsNullOrWhiteSpace(d.Name)) return "Give the command a name.";
            if (d.Name.Trim().Length > MaxNameLength) return "The name can be at most " + MaxNameLength + " characters.";
            if (string.IsNullOrWhiteSpace(d.Template)) return "The command needs a prompt.";
            if (d.Template.Length > MaxTemplateLength) return "The prompt can be at most " + MaxTemplateLength + " characters.";
            if (d.Inputs != null && d.Inputs.Count > SavedCommandDraft.MaxInputs) return "At most " + SavedCommandDraft.MaxInputs + " inputs per command.";
            return null;
        }

        private static void Fill(LocalCommand c, SavedCommandDraft d)
        {
            c.Name = d.Name.Trim();
            c.Template = d.Template;
            c.Inputs = (d.Inputs ?? new List<SlashInput>()).Select(i => new LocalCommandInput
            {
                Name = i.Name ?? "", Type = i.Type == "number" ? "number" : "text", Required = i.Required, Label = i.Label ?? i.Name,
            }).ToList();
            if (d.ToolsCalled != null && d.ToolsCalled.Count > 0) c.ToolsCalled = d.ToolsCalled.ToList();
        }

        private List<LocalCommand> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new List<LocalCommand>();
                var text = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text)) return new List<LocalCommand>();
                var file = JsonConvert.DeserializeObject<LocalCommandFile>(text);
                if (file == null) throw new JsonException("empty document");
                // A file written by a newer add-in is read as far as this one understands it, and kept beside as .bad.
                if (file.Version > SchemaVersion) KeepCorrupt();
                return (file.Commands ?? new List<LocalCommand>())
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name))
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] saved commands unreadable: " + ex.Message);
                KeepCorrupt();
                return new List<LocalCommand>();
            }
        }

        private void KeepCorrupt()
        {
            try { if (File.Exists(_path)) File.Copy(_path, _path + ".bad", overwrite: true); }
            catch (Exception) { /* best effort */ }
        }

        /// <summary>Null on success, else a sentence.</summary>
        private string? Save(List<LocalCommand> list)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(new LocalCommandFile { Commands = list }, Formatting.Indented));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] saved commands not written: " + ex.Message);
                return "Could not save on this PC: " + ex.Message;
            }
        }
    }

    /// <summary>A saved or curated command as the prompt that is sent: its template with the typed inputs filled in
    /// ("protocol 3 has no server-side command ids"), then any free text. Done in the add-in, so a command saved on this
    /// PC runs though the backend has never heard of it.</summary>
    public static class SlashPromptText
    {
        private static readonly Regex Hole = new Regex(@"\{([a-z][a-z0-9_]{0,39})\}", RegexOptions.Compiled);

        public static string Build(string? name, string? template, string? typed, IDictionary<string, object>? inputs)
        {
            if (string.IsNullOrWhiteSpace(template))
                return string.IsNullOrWhiteSpace(typed) ? (name ?? "") : (name ?? "") + ": " + typed;
            // One pass over the template: a typed value that itself looks like {another} is never filled in again.
            var text = Hole.Replace(template!, m => inputs != null && inputs.TryGetValue(m.Groups[1].Value, out var v) ? v?.ToString() ?? "" : m.Value);
            return string.IsNullOrWhiteSpace(typed) ? text : text + "\n" + typed;
        }
    }
}
