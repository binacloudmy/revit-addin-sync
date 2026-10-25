#nullable enable
// Revit-free envelope helpers for the read tools (phase "read"). They build the
// contract Envelope directly and check it with EnvelopeInvariants (throws in
// DEBUG, logs in release), so the read shapes are testable without W1's
// EnvelopeBuilder. Values in data go through CopilotJson (snake_case keys come
// from the dictionaries the shapers build; nulls are dropped).
//
// ReadArgs: tolerant readers for the model's JSON arguments.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public static class ReadEnvelope
    {
        public static Envelope Done(
            string summary,
            object? data = null,
            int? count = null,
            string? handle = null,
            SearchedDto? searched = null,
            string? next = null,
            HighlightDto? highlight = null,
            ImageDto? image = null)
        {
            var e = new Envelope
            {
                Ok = true,
                Status = EnvelopeStatus.Done,
                Phase = EnvelopePhase.Read,
                Summary = Clip(string.IsNullOrWhiteSpace(summary) ? "Done." : summary, EnvelopeLimits.SummaryMaxChars),
                Data = data == null ? null : ToElement(data),
                Count = count,
                Handle = string.IsNullOrEmpty(handle) ? null : handle,
                Searched = searched,
                Next = string.IsNullOrWhiteSpace(next) ? null : Clip(next!, EnvelopeLimits.NextMaxChars),
                Highlight = highlight,
                Image = image,
            };
            return Checked(e);
        }

        /// <summary>A failed read. not_found always carries candidates (possibly empty).</summary>
        public static Envelope Fail(string code, string message, IEnumerable<string>? candidates = null) =>
            Checked(EnvelopeFactory.Failed(code, message, candidates?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Take(10).ToArray()));

        public static Envelope InvalidArgs(string message) => Fail(ErrorCodes.InvalidArgs, message);

        public static Envelope Busy(string message)
        {
            var e = Fail(ErrorCodes.RevitBusy, message);
            e.Status = EnvelopeStatus.RevitBusy;
            return e;
        }

        public static Envelope TimedOut(string message)
        {
            var e = Fail(ErrorCodes.Timeout, message);
            e.Status = EnvelopeStatus.Timeout;
            return e;
        }

        internal static JsonElement ToElement(object data) =>
            data is JsonElement je ? je : CopilotJson.ToElement(data);

        /// <summary>At most max code points, ending in "…" when cut.</summary>
        public static string Clip(string s, int max) => EnvelopeFactory.Shorten(s, max);

        private static Envelope Checked(Envelope e)
        {
            var violations = EnvelopeInvariants.Violations(e);
            if (violations.Count > 0)
            {
                var text = "read envelope breaks the contract: " + string.Join("; ", violations);
                Debug.WriteLine("[BinaVibe][copilot] " + text);
#if DEBUG
                throw new InvalidOperationException(text);
#endif
            }
            return e;
        }
    }

    public static class ReadArgs
    {
        public static bool IsObject(JsonElement args) => args.ValueKind == JsonValueKind.Object;

        public static bool Has(JsonElement args, string name) =>
            IsObject(args) && args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

        public static string? String(JsonElement args, string name)
        {
            if (!IsObject(args) || !args.TryGetProperty(name, out var v)) return null;
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            s = s?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public static bool? Bool(JsonElement args, string name)
        {
            if (!IsObject(args) || !args.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
            return null;
        }

        public static long? Long(JsonElement args, string name)
        {
            if (!IsObject(args) || !args.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d))
                return (long)Math.Max(long.MinValue, Math.Min(long.MaxValue, Math.Round(d)));
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        /// <summary>Clamped integer: default when absent, min..max otherwise.</summary>
        public static int Int(JsonElement args, string name, int @default, int min, int max)
        {
            var v = Long(args, name);
            if (!v.HasValue) return @default;
            return (int)Math.Max(min, Math.Min(max, v.Value));
        }

        public static List<string>? Strings(JsonElement args, string name)
        {
            if (!IsObject(args) || !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!.Trim());
            return list;
        }

        public static List<long>? Longs(JsonElement args, string name)
        {
            if (!IsObject(args) || !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var list = new List<long>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n)) list.Add(n);
                else if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var s)) list.Add(s);
            }
            return list;
        }

        public static JsonElement? Object(JsonElement args, string name) =>
            IsObject(args) && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null;
    }
}
