using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    public class AIRequest
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("context")]
        public ModelContext Context { get; set; }

        [JsonProperty("userId")]
        public int? UserId { get; set; }

        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("templateId")]
        public string TemplateId { get; set; }

        /// <summary>Tells bina-ai this addin can run a structured vetted-tool
        /// directive (deterministic Tier-1 synth) instead of generated C#.</summary>
        [JsonProperty("supports_vetted_dispatch")]
        public bool SupportsVettedDispatch { get; set; }
    }

    public class ModelContext
    {
        // Field names match bina-ai's RevitModelContext (snake_case) so the
        // backend actually receives them — previously camelCase fields were
        // silently dropped, leaving the agent/classifier blind to view/schedule
        // names, active view, selection, etc.
        [JsonProperty("project_name")]
        public string ProjectName { get; set; }

        [JsonProperty("levels")]
        public List<string> Levels { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("view_names")]
        public List<string> ViewNames { get; set; }

        [JsonProperty("schedule_names")]
        public List<string> ScheduleNames { get; set; }

        [JsonProperty("active_view_name")]
        public string ActiveViewName { get; set; }

        [JsonProperty("active_view_type")]
        public string ActiveViewType { get; set; }

        [JsonProperty("selected_element_ids")]
        public List<int> SelectedElementIds { get; set; }

        [JsonProperty("phases")]
        public List<string> Phases { get; set; }

        [JsonProperty("revit_version")]
        public string RevitVersion { get; set; }

        /// <summary>
        /// Identifies the backend snapshot namespace for this project.
        /// Must match the {project} segment used by DocumentChangedIndexer
        /// when POSTing to /vibe/snapshot/{tenant}/{project} so the backend
        /// can read the mirror for this specific model.
        /// Serialised as "project_id" to match the backend RevitModelContext field.
        /// </summary>
        [JsonProperty("project_id")]
        public string ProjectId { get; set; }
    }
}
