#nullable enable
// Stub created by W0; body owned by W1
// Revit-free. Turns the existing BinaVibe.Mcp tools' Dictionary results into
// contract envelopes, so the new tools can reuse them: ok:false / error /
// status:not_implemented become failed, counts become count, the raw
// dictionary stays in data.
//
// Mapping (phase null = a read):
//   failure (ResultSemantics.IsFailure) -> failed, error.code from the legacy
//       "code"/"error_code" when it is a contract code, not_implemented for
//       status not_implemented, doc_changed for status stale_document, else
//       runtime_error; near matches from candidates / suggestions / available*.
//   count  <- the first non-negative integer of count, total, matched, found.
//   summary <- legacy summary or message, else a plain sentence.
//   commit -> verified from the legacy "verified" block when it is one of the
//       two shapes the old tools write: WriteVerification's {checked, matches,
//       mismatches[{id, expected, actual}]} (param_readback) or {exists: bool}
//       (exists). A mismatch makes it partial. Without either, the commit is
//       NOT ok (partial, verified.method none): the old tool did not check.
//   an empty read says its scope was not reported instead of claiming one.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Envelopes
{
    public static class LegacyResultAdapter
    {
        private static readonly string[] CountKeys = { "count", "total", "matched", "found" };
        private static readonly string[] CandidateKeys = { "candidates", "suggestions", "did_you_mean" };

        public static Envelope ToEnvelope(Dictionary<string, object?> legacy, WritePhase? phase)
        {
            if (legacy == null)
                return EnvelopeBuilder.Failure(ErrorCodes.RuntimeError, "The tool returned no result.", null, phase).Build();

            var count = CountOf(legacy);
            EnvelopeBuilder b;
            if (ResultSemantics.IsFailure(legacy))
            {
                var code = ErrorCodeOf(legacy);
                // A legacy not_found that lists no near matches offered none.
                var candidates = CandidatesOf(legacy) ?? (code == ErrorCodes.NotFound ? new List<string>() : null);
                b = EnvelopeBuilder.Failure(code, ResultSemantics.Reason(legacy), candidates, phase);
            }
            else
            {
                b = phase.HasValue
                    ? EnvelopeBuilder.Write(phase.Value, SummaryOf(legacy, phase, count))
                    : EnvelopeBuilder.Read(SummaryOf(legacy, phase, count));
                if (phase == WritePhase.Commit) Verify(b, legacy);
                if (!phase.HasValue && count == 0)
                    b.WithSearched(new SearchedDto { NotSearched = { EnvelopeBuilder.ScopeNotReported } });
            }

            if (count.HasValue) b.WithCount(count.Value);
            b.WithData(legacy);
            return b.Build();
        }

        private static void Verify(EnvelopeBuilder b, Dictionary<string, object?> legacy)
        {
            var verified = VerifiedOf(legacy.TryGetValue("verified", out var v) ? v : null);
            if (verified == null)
            {
                b.WithVerified(new VerifiedDto { Method = VerifyMethods.None })
                 .Fault(ErrorCodes.RuntimeError,
                     "The change was committed but not verified: this tool does not read its result back. Inspect the elements before relying on it.",
                     EnvelopeStatus.Partial);
                return;
            }
            b.WithVerified(verified);
            if (verified.Mismatches.Count > 0) b.Status(EnvelopeStatus.Partial);
        }

        private static VerifiedDto? VerifiedOf(object? raw)
        {
            var block = Values.AsMap(raw);
            if (block == null) return null;

            if (block.TryGetValue("checked", out var checkedRaw) && Values.AsInt(checkedRaw) is int checkedCount
                && block.TryGetValue("mismatches", out var mismatchesRaw) && Values.AsList(mismatchesRaw) is { } rows)
            {
                var mismatches = new List<MismatchDto>();
                foreach (var row in rows)
                {
                    var m = Values.AsMap(row);
                    if (m == null) continue;
                    mismatches.Add(new MismatchDto
                    {
                        Id = m.TryGetValue("id", out var id) ? Values.AsLong(id) ?? 0 : 0,
                        Field = m.TryGetValue("field", out var f) ? Values.AsText(f) ?? "" : "",
                        Expected = m.TryGetValue("expected", out var ex) ? Values.AsText(ex) : null,
                        Actual = m.TryGetValue("actual", out var ac) ? Values.AsText(ac) : null,
                    });
                }
                if (mismatches.Count == 0 && block.TryGetValue("matches", out var matchesRaw)
                    && Values.AsInt(matchesRaw) is int matches && matches < checkedCount)
                    mismatches.Add(new MismatchDto { Field = "", Expected = checkedCount + " read back", Actual = matches + " read back" });
                return new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = Math.Max(0, checkedCount), Mismatches = mismatches };
            }

            if (block.TryGetValue("exists", out var existsRaw) && Values.AsBool(existsRaw) is bool exists)
            {
                var result = new VerifiedDto { Method = VerifyMethods.Exists, Checked = 1 };
                if (!exists) result.Mismatches.Add(new MismatchDto { Field = "exists", Expected = "true", Actual = "false" });
                return result;
            }
            return null;
        }

        private static string ErrorCodeOf(Dictionary<string, object?> legacy)
        {
            var status = legacy.TryGetValue("status", out var s) ? Values.AsText(s) : null;
            if (status == "not_implemented") return ErrorCodes.NotImplemented;
            foreach (var key in new[] { "code", "error_code" })
                if (legacy.TryGetValue(key, out var c) && Values.AsText(c) is { } code && ErrorCodes.All.Contains(code))
                    return code;
            if (status == "stale_document") return ErrorCodes.DocChanged;
            return ErrorCodes.RuntimeError;
        }

        private static List<string>? CandidatesOf(Dictionary<string, object?> legacy)
        {
            var keys = CandidateKeys.Where(legacy.ContainsKey)
                .Concat(legacy.Keys.Where(k => k.StartsWith("available", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal));
            foreach (var key in keys)
            {
                var list = Values.AsList(legacy[key]);
                if (list == null) continue;
                var names = list.Select(NameOf).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
                if (names.Count > 0) return names;
            }
            return null;
        }

        // A near match is a name: a string, or an object's "name". Numbers are not names.
        private static string? NameOf(object? item) => item switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => Values.AsMap(item) is { } m && m.TryGetValue("name", out var n) ? Values.AsText(n) : null,
        };

        private static int? CountOf(Dictionary<string, object?> legacy)
        {
            foreach (var key in CountKeys)
                if (legacy.TryGetValue(key, out var raw) && Values.AsInt(raw) is int n && n >= 0)
                    return n;
            return null;
        }

        private static string SummaryOf(Dictionary<string, object?> legacy, WritePhase? phase, int? count)
        {
            foreach (var key in new[] { "summary", "message" })
                if (legacy.TryGetValue(key, out var raw) && Values.AsText(raw) is { } text && !string.IsNullOrWhiteSpace(text))
                    return EnvelopeFactory.Shorten(text.Trim(), EnvelopeLimits.SummaryMaxChars);
            var counted = count.HasValue ? " (" + count.Value.ToString(CultureInfo.InvariantCulture) + ")" : "";
            return phase switch
            {
                WritePhase.Preview => "Preview ready" + counted + "; nothing has changed yet.",
                WritePhase.Commit => "Change committed" + counted + ".",
                _ => "Read completed" + counted + ".",
            };
        }
    }

    public static class ResultSemantics
    {
        private static readonly string[] FailedStatuses = { "not_implemented", "failed", "error", "ambiguous", "stale_document" };

        /// <summary>True when a legacy tool result reports a failure without throwing:
        /// ok is false; or status is not_implemented / failed / error / ambiguous /
        /// stale_document; or there is a non-empty error and no explicit ok:true.
        /// A missing result is a failure. Values may be CLR or JsonElement.</summary>
        public static bool IsFailure(Dictionary<string, object?> legacy)
        {
            if (legacy == null) return true;
            var ok = legacy.TryGetValue("ok", out var okRaw) ? Values.AsBool(okRaw) : null;
            if (ok == false) return true;
            if (legacy.TryGetValue("status", out var s) && Values.AsText(s) is { } status && FailedStatuses.Contains(status))
                return true;
            if (ok == true) return false;
            return legacy.TryGetValue("error", out var e) && !string.IsNullOrWhiteSpace(Values.AsText(e));
        }

        /// <summary>The failure text: error, else message, else a plain sentence.</summary>
        internal static string Reason(Dictionary<string, object?> legacy)
        {
            foreach (var key in new[] { "error", "message", "reason" })
                if (legacy.TryGetValue(key, out var raw) && Values.AsText(raw) is { } text && !string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            if (legacy.TryGetValue("status", out var s) && Values.AsText(s) == "not_implemented")
                return "This tool is not implemented in this add-in build.";
            return "The tool reported a failure without a reason.";
        }
    }

    /// <summary>Reads legacy values that are CLR objects or JsonElements alike.</summary>
    internal static class Values
    {
        public static bool? AsBool(object? v) => v switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };

        public static string? AsText(object? v) => v switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetRawText(),
            JsonElement => null,
            IConvertible c => c.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        public static long? AsLong(object? v)
        {
            switch (v)
            {
                case int i: return i;
                case long l: return l;
                case short sh: return sh;
                case uint ui: return ui;
                case double d when d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 9e15: return (long)d;
                case float f when f == Math.Floor(f) && !float.IsInfinity(f) && Math.Abs(f) < 9e15: return (long)f;
                case decimal m when m == decimal.Floor(m): return (long)m;
                case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt64(out var n): return n;
                default: return null;
            }
        }

        public static int? AsInt(object? v) => AsLong(v) is long l && l >= int.MinValue && l <= int.MaxValue ? (int)l : (int?)null;

        public static Dictionary<string, object?>? AsMap(object? v)
        {
            switch (v)
            {
                case Dictionary<string, object?> d: return d;
                case JsonElement { ValueKind: JsonValueKind.Object } je:
                    return je.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
                case IDictionary dict:
                    var o = new Dictionary<string, object?>();
                    foreach (DictionaryEntry kv in dict) o[Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? ""] = kv.Value;
                    return o;
                default: return null;
            }
        }

        public static List<object?>? AsList(object? v)
        {
            switch (v)
            {
                case null: return null;
                case string: return null;
                case JsonElement { ValueKind: JsonValueKind.Array } je: return je.EnumerateArray().Select(x => (object?)x).ToList();
                case JsonElement: return null;
                case IDictionary: return null;
                case IEnumerable seq: return seq.Cast<object?>().ToList();
                default: return null;
            }
        }
    }
}
