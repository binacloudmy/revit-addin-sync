#nullable enable
// Revit-free envelope helpers for the read tools (phase "read"), built with
// W1's EnvelopeBuilder (which enforces the §2.1 invariants: throws in DEBUG,
// logs and repairs in release). Data goes through CopilotJson first, so null
// fields are dropped and BM text stays readable.
//
// ArgReader: strict readers for the model's JSON arguments (ruling R14). A
// wrong type or an unknown key is an error the tool returns as invalid_args;
// nothing is silently dropped (a dropped filter would widen the search).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;

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
            ImageDto? image = null,
            string status = EnvelopeStatus.Done)
        {
            var b = EnvelopeBuilder.Read(Clip(string.IsNullOrWhiteSpace(summary) ? "Done." : summary, EnvelopeLimits.SummaryMaxChars));
            if (status != EnvelopeStatus.Done) b.Status(status);
            if (data != null) b.WithData(ToElement(data));
            if (count.HasValue) b.WithCount(count.Value);
            if (!string.IsNullOrEmpty(handle)) b.WithHandle(handle!);
            if (searched != null) b.WithSearched(searched);
            if (!string.IsNullOrWhiteSpace(next)) b.WithNext(Clip(next!, EnvelopeLimits.NextMaxChars));
            if (highlight != null) b.WithHighlight(highlight.Selected, highlight.VisibleInView);
            if (image != null) b.WithImage(image);
            return b.Build();
        }

        /// <summary>A failed read. not_found always carries candidates (possibly empty).
        /// revit_busy and timeout get their own status.</summary>
        public static Envelope Fail(string code, string message, IEnumerable<string>? candidates = null)
        {
            var list = candidates?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Take(10).ToList()
                       ?? (code == ErrorCodes.NotFound ? new List<string>() : null);
            return EnvelopeBuilder.Fail(code, message, list);
        }

        public static Envelope InvalidArgs(string message) => Fail(ErrorCodes.InvalidArgs, message);

        public static Envelope Busy(string message) => Fail(ErrorCodes.RevitBusy, message);

        public static Envelope TimedOut(string message) => Fail(ErrorCodes.Timeout, message);

        internal static JsonElement ToElement(object data) =>
            data is JsonElement je ? je : CopilotJson.ToElement(data);

        /// <summary>At most max code points, ending in "…" when cut.</summary>
        public static string Clip(string s, int max) => EnvelopeFactory.Shorten(s, max);
    }

    /// <summary>Strict argument reader: collects every type error instead of guessing.</summary>
    public sealed class ArgReader
    {
        private readonly List<string> _errors = new();

        public bool Ok => _errors.Count == 0;

        public string Error => string.Join(" ", _errors.Take(3)) + (_errors.Count > 3 ? " (" + (_errors.Count - 3) + " more)" : "");

        public void Fail(string message) => _errors.Add(message);

        private static string P(string path, string name) => path.Length == 0 ? name : path + "." + name;

        private static bool TryGet(JsonElement obj, string name, out JsonElement v)
        {
            v = default;
            return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out v)
                   && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;
        }

        public bool IsObject(JsonElement e, string what)
        {
            if (e.ValueKind == JsonValueKind.Object) return true;
            Fail(what + " must be a JSON object.");
            return false;
        }

        /// <summary>Unknown keys are errors (the tool schemas are closed).</summary>
        public void Known(JsonElement obj, string path, params string[] allowed)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            foreach (var p in obj.EnumerateObject())
                if (!allowed.Contains(p.Name))
                    Fail("Unknown argument '" + P(path, p.Name) + "'; allowed: " + string.Join(", ", allowed) + ".");
        }

        public string? Str(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) { Fail(P(path, name) + " must be a string."); return null; }
            var s = v.GetString()?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>A string, number or boolean as text (parameter values).</summary>
        public string? Scalar(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: var s = v.GetString()?.Trim(); return string.IsNullOrEmpty(s) ? null : s;
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: Fail(P(path, name) + " must be a string, number or boolean."); return null;
            }
        }

        public bool? Bool(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            Fail(P(path, name) + " must be true or false.");
            return null;
        }

        private static bool Integer(JsonElement v, out long n)
        {
            n = 0;
            if (v.ValueKind != JsonValueKind.Number) return false;
            if (v.TryGetInt64(out n)) return true;
            if (v.TryGetDouble(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 9e15) { n = (long)Math.Round(d); return true; }
            return false;
        }

        public long? Long(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            if (Integer(v, out var n)) return n;
            Fail(P(path, name) + " must be an integer.");
            return null;
        }

        /// <summary>Clamped integer: default when absent, min..max otherwise.</summary>
        public int Int(JsonElement obj, string name, int @default, int min, int max, string path = "")
        {
            var v = Long(obj, name, path);
            if (!v.HasValue) return @default;
            return (int)Math.Max(min, Math.Min(max, v.Value));
        }

        public List<string>? Strs(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) { Fail(P(path, name) + " must be a list of strings."); return null; }
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { Fail(P(path, name) + " must be a list of strings."); return null; }
                var s = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) list.Add(s!);
            }
            return list;
        }

        public List<long>? Longs(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) { Fail(P(path, name) + " must be a list of integers."); return null; }
            var list = new List<long>();
            foreach (var item in v.EnumerateArray())
            {
                if (!Integer(item, out var n)) { Fail(P(path, name) + " must be a list of integers."); return null; }
                list.Add(n);
            }
            return list;
        }

        public JsonElement? Obj(JsonElement obj, string name, string path = "")
        {
            if (!TryGet(obj, name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Object) { Fail(P(path, name) + " must be an object."); return null; }
            return v;
        }
    }
}
