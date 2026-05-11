using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// A saved Copilot command — a reusable prompt template with optional
    /// {placeholder} variables. Served by GET /api/revit-ai/commands.
    /// </summary>
    public class CommandTemplate
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("prompt_template")]
        public string PromptTemplate { get; set; }

        [JsonProperty("variables")]
        public List<CommandVariable> Variables { get; set; } = new List<CommandVariable>();

        [JsonProperty("scope")]
        public string Scope { get; set; }

        [JsonProperty("owner_user_id")]
        public int? OwnerUserId { get; set; }

        [JsonProperty("org_id")]
        public int? OrgId { get; set; }

        [JsonProperty("usage_count")]
        public int UsageCount { get; set; }

        // --- View helpers (not serialized) ---

        [JsonIgnore]
        public bool HasVariables => Variables != null && Variables.Count > 0;

        /// <summary>"  ·  Category" suffix for list display, empty if no category.</summary>
        [JsonIgnore]
        public string CategoryTag => string.IsNullOrWhiteSpace(Category) ? "" : "  ·  " + Category;
    }

    public class CommandVariable
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; } = "text";   // "text" | "select"

        [JsonProperty("default")]
        public string Default { get; set; }

        [JsonProperty("options")]
        public List<string> Options { get; set; } = new List<string>();

        [JsonIgnore]
        public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label;
    }

    /// <summary>
    /// Body for POST/PUT /api/revit-ai/commands. Field names mirror the backend's
    /// CommandTemplateCreate/Update (note: prompt_template is snake_case, ids are camelCase).
    /// </summary>
    public class CommandSaveRequest
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("prompt_template")]
        public string PromptTemplate { get; set; }

        [JsonProperty("variables")]
        public List<CommandVariable> Variables { get; set; } = new List<CommandVariable>();

        [JsonProperty("scope")]
        public string Scope { get; set; } = "user";   // "user" | "org"

        [JsonProperty("userId")]
        public int? UserId { get; set; }

        [JsonProperty("orgId")]
        public int? OrgId { get; set; }
    }
}
