using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    // ── Agent run (POST /agents/jkr_specialist/runs) ──

    public class JkrAgentRequest
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("stream")]
        public bool Stream { get; set; } = false;
    }

    public class JkrAgentResponse
    {
        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("content_type")]
        public string ContentType { get; set; }
    }

    // ── Raw search (POST /v1/jkr/search) ──

    public class JkrSearchRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("top_k")]
        public int TopK { get; set; } = 5;
    }

    public class JkrSearchResult
    {
        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("doc_name")]
        public string DocName { get; set; }

        [JsonProperty("doc_number")]
        public string DocNumber { get; set; }

        [JsonProperty("page")]
        public int Page { get; set; }

        [JsonProperty("filename")]
        public string Filename { get; set; }

        [JsonProperty("score")]
        public double? Score { get; set; }
    }

    public class JkrSearchResponse
    {
        [JsonProperty("results")]
        public List<JkrSearchResult> Results { get; set; }

        [JsonProperty("query")]
        public string Query { get; set; }
    }
}
