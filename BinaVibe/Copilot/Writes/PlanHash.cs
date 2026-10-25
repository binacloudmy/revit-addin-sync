#nullable enable
// Plan hash (spec §7.4 step 1; plan §5 W3 Task 1). Revit-free.
//
// The confirm card's Ya is bound to this hash: it covers, per call and in call
// order, the tool, its canonical args, the resolved ids of existing targets,
// the intended values, the expected added/deleted counts per category, the
// code text and whether the call can be previewed. It is computed over
// canonical JSON (object keys sorted ordinally, ids sorted and de-duplicated,
// numbers written one way, invariant culture), so key order or "2700" versus
// "2700.0" never moves it, while any real change to the plan does.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class PlanHash
    {
        public const string Prefix = "sha256:";

        /// <summary>"sha256:" + 64 lowercase hex over <see cref="CanonicalText"/>.</summary>
        public static string Compute(IEnumerable<WritePlan> plans)
        {
            var bytes = Encoding.UTF8.GetBytes(CanonicalText(plans));
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(bytes);
            return Prefix + BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The canonical JSON that <see cref="Compute"/> hashes: an array with one
        /// object per plan, keys in ordinal order.</summary>
        public static string CanonicalText(IEnumerable<WritePlan> plans)
        {
            if (plans == null) throw new ArgumentNullException(nameof(plans));
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = false,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                w.WriteStartArray();
                foreach (var plan in plans)
                {
                    if (plan == null) throw new ArgumentException("a plan in the batch is null", nameof(plans));
                    w.WriteStartObject();

                    w.WritePropertyName("args");
                    WriteArgs(w, plan.CanonicalArgs);

                    w.WritePropertyName("code");
                    if (plan.CodeText == null) w.WriteNullValue();
                    else w.WriteStringValue(plan.CodeText);

                    w.WritePropertyName("expected_added");
                    WriteCounts(w, plan.ExpectedAddedByCategory);

                    w.WritePropertyName("expected_deleted");
                    WriteCounts(w, plan.ExpectedDeletedByCategory);

                    w.WritePropertyName("intended");
                    w.WriteStartObject();
                    foreach (var kv in (plan.IntendedValues ?? new Dictionary<string, string>())
                                 .OrderBy(kv => kv.Key, StringComparer.Ordinal))
                        w.WriteString(kv.Key, kv.Value);
                    w.WriteEndObject();

                    w.WriteBoolean("previewable", plan.Previewable);

                    w.WritePropertyName("targets");
                    w.WriteStartArray();
                    foreach (var id in (plan.TargetIds ?? Array.Empty<long>()).Distinct().OrderBy(id => id))
                        w.WriteNumberValue(id);
                    w.WriteEndArray();

                    w.WriteString("tool", plan.Tool ?? "");

                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteCounts(Utf8JsonWriter w, Dictionary<string, int>? counts)
        {
            w.WriteStartObject();
            foreach (var kv in (counts ?? new Dictionary<string, int>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();
        }

        private static void WriteArgs(Utf8JsonWriter w, string? args)
        {
            if (string.IsNullOrWhiteSpace(args)) { w.WriteNullValue(); return; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(args!); }
            catch (JsonException)
            {
                // Not JSON: hash the text itself, so it still binds.
                w.WriteStringValue(args);
                return;
            }
            using (doc) WriteCanonical(w, doc.RootElement);
        }

        /// <summary>Writes a JSON value with object keys in ordinal order (the last of
        /// duplicate keys wins, as in most parsers) and numbers normalised.</summary>
        internal static void WriteCanonical(Utf8JsonWriter w, JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var props = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject()) props[p.Name] = p.Value;
                    w.WriteStartObject();
                    foreach (var kv in props)
                    {
                        w.WritePropertyName(kv.Key);
                        WriteCanonical(w, kv.Value);
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in e.EnumerateArray()) WriteCanonical(w, item);
                    w.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    w.WriteStringValue(e.GetString());
                    break;
                case JsonValueKind.Number:
                    if (e.TryGetInt64(out var l)) w.WriteNumberValue(l);
                    else if (e.TryGetDecimal(out var m)) w.WriteNumberValue(Normalise(m));
                    else w.WriteNumberValue(e.GetDouble());
                    break;
                case JsonValueKind.True:
                    w.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    w.WriteBooleanValue(false);
                    break;
                default:
                    w.WriteNullValue();
                    break;
            }
        }

        // 2700.0m -> 2700m, 2.50m -> 2.5m (dividing by 1.000…0 drops trailing zeros).
        private static decimal Normalise(decimal value) => value / 1.000000000000000000000000000000000m;
    }
}
