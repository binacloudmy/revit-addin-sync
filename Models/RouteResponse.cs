using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Response from POST /api/revit-ai/route — the unified Copilot entry point.
    /// </summary>
    public class RouteResponse
    {
        [JsonProperty("intent")]
        public string Intent { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("reply")]
        public string Reply { get; set; }

        [JsonProperty("needs_clarification")]
        public bool NeedsClarification { get; set; }

        [JsonProperty("clarifying_question")]
        public string ClarifyingQuestion { get; set; }

        [JsonProperty("mentions")]
        public List<RouteMention> Mentions { get; set; } = new List<RouteMention>();

        [JsonProperty("actions")]
        public List<RouteAction> Actions { get; set; } = new List<RouteAction>();

        [JsonProperty("suggestions")]
        public List<RouteSuggestion> Suggestions { get; set; } = new List<RouteSuggestion>();

        [JsonProperty("tokensUsed")]
        public int? TokensUsed { get; set; }

        [JsonProperty("reasoning")]
        public string Reasoning { get; set; }
    }

    public class RouteAction
    {
        [JsonProperty("tool")]
        public string Tool { get; set; }

        /// <summary>open_view | select_elements | execute_code | run_analysis | export | query | none</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("params")]
        public Dictionary<string, object> Params { get; set; } = new Dictionary<string, object>();

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("hint")]
        public string Hint { get; set; }

        [JsonProperty("mentions")]
        public List<RouteMention> Mentions { get; set; }
    }

    public class RouteMention
    {
        [JsonProperty("raw")] public string Raw { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    public class RouteSuggestion
    {
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("action")] public string Action { get; set; }
    }
}
