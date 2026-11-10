#nullable enable
// Copilot protocol-3 JSON settings (owned by W0, contracts). Revit-free.
//
// Every contract type names its keys with [JsonPropertyName] (snake_case), so
// these options add only the shared wire conventions from
// contracts/copilot/README.md: null fields are omitted, and non-ASCII text
// (BM, "×", "m²") stays readable instead of \u-escaped.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Contracts
{
    public static class CopilotJson
    {
        public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        public static T? Deserialize<T>(JsonElement json) => json.Deserialize<T>(Options);

        public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    }
}
