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

        /// <summary>Screenshots pasted with the prompt (base64 PNG). Omitted from
        /// the JSON when null so un-upgraded backends see an unchanged body.</summary>
        [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Images { get; set; }
    }

    public class ModelContext
    {
        [JsonProperty("projectName")]
        public string ProjectName { get; set; }

        [JsonProperty("levels")]
        public List<string> Levels { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("activeViewName")]
        public string ActiveViewName { get; set; }

        [JsonProperty("activeViewType")]
        public string ActiveViewType { get; set; }

        [JsonProperty("selectedElementIds")]
        public List<int> SelectedElementIds { get; set; }

        /// <summary>Real view list (id + name + type) so the agent resolves
        /// "open Aras 01" to the exact view instead of guessing. Bounded by
        /// BuildContext to avoid dumping thousands of views.</summary>
        [JsonProperty("views")]
        public List<ViewInfo> Views { get; set; }

        [JsonProperty("phases")]
        public List<string> Phases { get; set; }

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }

        /// <summary>
        /// Identifies the backend snapshot namespace for this project.
        /// Must match the {project} segment used by DocumentChangedIndexer
        /// when POSTing to /revit-copilot/snapshot/{tenant}/{project} so the backend
        /// can read the mirror for this specific model.
        /// Serialised as "project_id" to match the backend RevitModelContext field.
        /// </summary>
        [JsonProperty("project_id")]
        public string ProjectId { get; set; }
    }

    public class ViewInfo
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("viewType")] public string ViewType { get; set; }
        /// <summary>The level a plan view belongs to — disambiguates same-named views.</summary>
        [JsonProperty("ownerView")] public string OwnerView { get; set; }
    }
}
