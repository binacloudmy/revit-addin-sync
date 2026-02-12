using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a saved command that can be reused
    /// </summary>
    public class SavedCommand
    {
        /// <summary>
        /// Unique identifier for the command
        /// </summary>
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Display name for the command (e.g., "Export Rooms to CSV")
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// Original prompt that generated this command
        /// </summary>
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        /// <summary>
        /// Generated C# code to execute
        /// </summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>
        /// AI explanation of what the code does
        /// </summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>
        /// Category for grouping (e.g., "Export", "Selection", "Modification")
        /// </summary>
        [JsonProperty("category")]
        public string Category { get; set; } = "General";

        /// <summary>
        /// Icon emoji for display
        /// </summary>
        [JsonProperty("icon")]
        public string Icon { get; set; } = "⚡";

        /// <summary>
        /// Whether this is a built-in command (cannot be deleted)
        /// </summary>
        [JsonProperty("isBuiltIn")]
        public bool IsBuiltIn { get; set; } = false;

        /// <summary>
        /// Number of times this command has been used
        /// </summary>
        [JsonProperty("useCount")]
        public int UseCount { get; set; } = 0;

        /// <summary>
        /// When the command was created
        /// </summary>
        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// When the command was last used
        /// </summary>
        [JsonProperty("lastUsedAt")]
        public DateTime? LastUsedAt { get; set; }
    }

    /// <summary>
    /// Container for saved commands library
    /// </summary>
    public class CommandLibrary
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";

        [JsonProperty("commands")]
        public List<SavedCommand> Commands { get; set; } = new List<SavedCommand>();
    }
}
