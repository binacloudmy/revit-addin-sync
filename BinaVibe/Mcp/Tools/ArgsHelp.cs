// ArgsHelp — shared JSON arg extraction (the part the lean tools still use).
using System.Text.Json;

namespace BinaVibe.Mcp.Tools
{
    internal static class ArgsHelp
    {
        public static string? GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }
}
