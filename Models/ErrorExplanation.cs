using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Response from POST /api/revit-ai/explain-error — a plain-English read on
    /// why generated code failed, plus a short list of fix options.
    /// </summary>
    public class ErrorExplanation
    {
        [JsonProperty("explanation")]
        public string Explanation { get; set; }

        [JsonProperty("root_cause")]
        public string RootCause { get; set; }

        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("fixes")]
        public List<ErrorFix> Fixes { get; set; } = new List<ErrorFix>();

        [JsonProperty("retry_recommended")]
        public bool RetryRecommended { get; set; }

        [JsonProperty("retry_available")]
        public bool RetryAvailable { get; set; }
    }

    public class ErrorFix
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("recommended")]
        public bool Recommended { get; set; }

        // True when applying this fix means regenerating the C# (vs. a manual step).
        [JsonProperty("code_fix")]
        public bool CodeFix { get; set; }
    }
}
