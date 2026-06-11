// BatchRefResolver — pure resolution of "$<index>.<field>" references in an
// execute_revit_batch step's args against earlier steps' outputs.
//
// Deliberately Revit-free (only System.Text.Json + collections) so the Tests
// project can compile-link it without pulling in the Revit SDK — same pattern
// as AiUrl. BatchExecutor (which needs Revit) calls into this.
using System.Collections.Generic;
using System.Text.Json;

namespace BinaVibe.Mcp.Tools
{
    public static class BatchRefResolver
    {
        /// <summary>Resolve "$<index>.<field>" string values against prior step
        /// outputs; pass everything else through. Returns a flat arg dict.</summary>
        public static Dictionary<string, object?> ResolveRefs(
            JsonElement args, IReadOnlyList<Dictionary<string, object?>> priorResults)
        {
            var outp = new Dictionary<string, object?>();
            if (args.ValueKind != JsonValueKind.Object) return outp;
            foreach (var p in args.EnumerateObject())
                outp[p.Name] = ResolveValue(p.Value, priorResults);
            return outp;
        }

        private static object? ResolveValue(JsonElement v, IReadOnlyList<Dictionary<string, object?>> prior)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    var s = v.GetString() ?? "";
                    if (s.Length > 1 && s[0] == '$' && s.Contains('.'))
                    {
                        var dot = s.IndexOf('.');
                        if (int.TryParse(s.Substring(1, dot - 1), out var idx)
                            && idx >= 0 && idx < prior.Count)
                        {
                            var field = s.Substring(dot + 1);
                            if (prior[idx].TryGetValue(field, out var val)) return val;
                        }
                    }
                    return s;
                case JsonValueKind.Number:
                    return v.TryGetInt64(out var l) ? l : v.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Array:
                    var arr = new List<object?>();
                    foreach (var e in v.EnumerateArray()) arr.Add(ResolveValue(e, prior));
                    return arr;
                default:
                    return null;
            }
        }
    }
}
