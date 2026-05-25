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

        /// <summary>
        /// Intent label the bina-ai classifier picked (e.g. "create_view_from_view",
        /// "renumber_elements"). Used by the chat router for the proposal card title
        /// so the user sees the REAL intent, not the catalog-fallback toolId pick.
        /// Optional — older backends omit it.
        /// </summary>
        [JsonProperty("intent")]
        public string Intent { get; set; }
    }
}
