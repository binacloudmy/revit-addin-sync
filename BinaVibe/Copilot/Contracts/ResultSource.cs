#nullable enable
// Where one written value came from. Revit-free mirror of
// contracts/copilot/protocol3.schema.json#/$defs/result_source. A write call
// carries it per value, and the result card shows it next to the value.
//
// Readers ignore unknown keys (System.Text.Json skips them), so a newer
// server never breaks this add-in.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Contracts
{
    public static class ResultSourceKinds
    {
        public const string Document = "document";
        public const string Prompt = "prompt";
        public const string Rule = "rule";
        /// <summary>Copilot's own choice or derivation.</summary>
        public const string Calculated = "calculated";
        public const string Excel = "excel";

        public static IReadOnlyList<string> All { get; } = new[] { Document, Prompt, Rule, Calculated, Excel };
    }

    public static class ResultTargetScopes
    {
        public const string Type = "type";
        public const string Instance = "instance";

        public static IReadOnlyList<string> All { get; } = new[] { Type, Instance };
    }

    public static class ResultSourceLimits
    {
        public const int QuoteMaxChars = 300;
    }

    /// <summary>Where one written value came from.</summary>
    public sealed class ResultSourceDto
    {
        /// <summary>One of <see cref="ResultSourceKinds"/>.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("attachment")] public string? Attachment { get; set; }
        /// <summary>1-based page of a document attachment.</summary>
        [JsonPropertyName("page")] public int? Page { get; set; }
        /// <summary>1-based row of a table attachment, not counting the header.</summary>
        [JsonPropertyName("row")] public int? Row { get; set; }
        [JsonPropertyName("quote")] public string? Quote { get; set; }
    }
}
