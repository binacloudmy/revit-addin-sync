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

        [JsonProperty("phases")]
        public List<string> Phases { get; set; }

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }
    }
}
