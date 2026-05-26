using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    public class AIResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("explanation")]
        public string Explanation { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("tokensUsed")]
        public int? TokensUsed { get; set; }

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; }

        /// <summary>Server-detected: code is a pure read + SetResult report
        /// (no Transaction, no Selection.SetElementIds, no Delete/Set/Create).
        /// When true the Copilot pane auto-runs and renders the result as a
        /// chat-text bubble instead of the Save/Copy/Undo proposal card.</summary>
        [JsonProperty("is_query")]
        public bool IsQuery { get; set; }
    }
}
