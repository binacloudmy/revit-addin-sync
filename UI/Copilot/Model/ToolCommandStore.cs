#nullable enable
// "Save as command" (lean-harness spec §11 "In the pane"; W5 Task 4). Revit-free.
//
// A successful set_parameters or run_revit_code commit can be kept as
// {tool, args, intent} in the pane's command library (Library tab). A re-run
// goes through CopilotToolHost.RunWritesAsync, so it previews and shows the
// confirm card like any other write, and never involves the model. Each re-run
// is a new write with a fresh idempotency key.
//
// A target given by a session handle ("h_…") is refused: handles expire with
// the session, so the command would find nothing next time.
//
// Stored at %APPDATA%\RevitWebAppSync\copilot-tool-commands.json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class SavedToolCommand
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
        [JsonPropertyName("args")] public JsonElement Args { get; set; }
        [JsonPropertyName("intent")] public string Intent { get; set; } = "";
        [JsonPropertyName("created_utc")] public DateTime CreatedUtc { get; set; }
        [JsonPropertyName("run_count")] public int RunCount { get; set; }
    }

    public static class ToolCommandStore
    {
        public static string DefaultPath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitWebAppSync");
                return Path.Combine(dir, "copilot-tool-commands.json");
            }
        }

        public static bool CanSave(CommittedWrite write, out string reason)
        {
            reason = "";
            if (write == null) { reason = "nothing to save"; return false; }
            if (write.Tool != ToolNames.SetParameters && write.Tool != ToolNames.RunRevitCode)
            {
                reason = "only set_parameters and run_revit_code can be saved";
                return false;
            }
            if (write.Args.ValueKind != JsonValueKind.Object) { reason = "the call has no arguments"; return false; }
            if (write.Tool == ToolNames.SetParameters
                && write.Args.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
                && target.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String)
            {
                reason = "its target is a session handle, which expires; save a run that targets a filter or ids";
                return false;
            }
            return true;
        }

        public static SavedToolCommand Create(CommittedWrite write) => new SavedToolCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NameFor(write),
            Tool = write.Tool,
            Args = write.Args.Clone(),
            Intent = write.Intent ?? "",
            CreatedUtc = DateTime.UtcNow,
        };

        /// <summary>The call to run: same tool and args, a new call id and key.</summary>
        public static ToolCallDto ToCall(SavedToolCommand cmd)
        {
            var id = Guid.NewGuid().ToString("N");
            return new ToolCallDto
            {
                CallId = "cmd_" + id.Substring(0, 12),
                IdempotencyKey = "cmd:" + id,
                Tool = cmd.Tool,
                Args = cmd.Args.Clone(),
            };
        }

        public static List<SavedToolCommand> Load(string? path = null)
        {
            path ??= DefaultPath;
            try
            {
                if (!File.Exists(path)) return new List<SavedToolCommand>();
                return JsonSerializer.Deserialize<List<SavedToolCommand>>(File.ReadAllText(path))
                       ?.Where(c => c != null && !string.IsNullOrEmpty(c.Tool)).ToList()
                       ?? new List<SavedToolCommand>();
            }
            catch (Exception) { return new List<SavedToolCommand>(); }
        }

        public static void Save(IEnumerable<SavedToolCommand> commands, string? path = null)
        {
            path ??= DefaultPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(commands.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string NameFor(CommittedWrite w)
        {
            if (w.Tool == ToolNames.RunRevitCode)
                return !string.IsNullOrWhiteSpace(w.Intent) && w.Intent != w.Tool ? w.Intent.Trim() : "Revit code";
            var param = Str(w.Args, "param");
            var value = w.Args.ValueKind == JsonValueKind.Object && w.Args.TryGetProperty("value", out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
                : null;
            if (!string.IsNullOrEmpty(param))
                return value != null ? "Set " + param + " = " + value : "Set " + param + " from table";
            return "Set parameters";
        }

        private static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
