#nullable enable
// Copilot typed turn result (AI-294). Revit-free mirror of
// contracts/copilot/protocol3.schema.json#/$defs/turn_result: the optional
// "result" of a turn_end event. The pane renders a result card from it and
// shows the streamed prose when it is absent or unreadable.
//
// Readers ignore unknown keys (System.Text.Json skips them), so a newer
// server never breaks this add-in. TurnEndDto.Result is kept as raw JSON so a
// malformed result can never lose the turn_end itself: TurnResultReader.TryRead
// returns null for one it cannot read and the turn ends as it always did.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Contracts
{
    public static class TurnResultKinds
    {
        public const string Changes = "changes";
        public const string Created = "created";
        public const string Findings = "findings";
        public const string Selection = "selection";

        public static IReadOnlyList<string> All { get; } = new[] { Changes, Created, Findings, Selection };
    }

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

    public static class ResultActionKinds
    {
        /// <summary>Args are the set_parameters call's args; the answer is a write.</summary>
        public const string SetParameters = "set_parameters";
        /// <summary>Args are {"text": ...}: sent as the next turn.</summary>
        public const string Prompt = "prompt";
        public const string Dismiss = "dismiss";

        public static IReadOnlyList<string> All { get; } = new[] { SetParameters, Prompt, Dismiss };
    }

    public static class TurnResultLimits
    {
        public const int ReplyMaxChars = 300;
        public const int QuoteMaxChars = 300;
        public const int ActionLabelMaxChars = 60;
        public const int MaxValues = 500;
        public const int MaxChecks = 20;
        public const int MaxActions = 4;
        public const int MaxNext = 3;
        public const int NextMaxChars = 60;
    }

    /// <summary>The typed result of a turn.</summary>
    public sealed class TurnResultDto
    {
        /// <summary>One sentence.</summary>
        [JsonPropertyName("reply")] public string Reply { get; set; } = "";
        /// <summary>ISO 639 code of reply and check texts ("en", "ms").</summary>
        [JsonPropertyName("lang")] public string? Lang { get; set; }
        /// <summary>One of <see cref="TurnResultKinds"/>.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("counts")] public ResultCountsDto? Counts { get; set; }
        [JsonPropertyName("values")] public List<ResultValueDto>? Values { get; set; }
        [JsonPropertyName("checks")] public List<ResultCheckDto>? Checks { get; set; }
        /// <summary>What the turn did not look at, one line each.</summary>
        [JsonPropertyName("not_searched")] public List<string>? NotSearched { get; set; }
        /// <summary>Up to three short next steps the drafter can pick (the "Next" chips).</summary>
        [JsonPropertyName("next")] public List<string>? Next { get; set; }
    }

    /// <summary>Counts the server read from the tool envelopes.</summary>
    public sealed class ResultCountsDto
    {
        [JsonPropertyName("elements")] public int? Elements { get; set; }
        [JsonPropertyName("types")] public int? Types { get; set; }
        [JsonPropertyName("values")] public int? Values { get; set; }
        [JsonPropertyName("written")] public int? Written { get; set; }
        [JsonPropertyName("reverted")] public int? Reverted { get; set; }
        [JsonPropertyName("skipped")] public int? Skipped { get; set; }
        [JsonPropertyName("failed")] public int? Failed { get; set; }
        [JsonPropertyName("created")] public int? Created { get; set; }
        [JsonPropertyName("findings")] public int? Findings { get; set; }
    }

    /// <summary>One written (or found) value, in display units.</summary>
    public sealed class ResultValueDto
    {
        [JsonPropertyName("param")] public string Param { get; set; } = "";
        [JsonPropertyName("target")] public ResultTargetDto? Target { get; set; }
        /// <summary>A JSON string, number or boolean.</summary>
        [JsonPropertyName("before")] public JsonElement? Before { get; set; }
        /// <summary>A JSON string, number or boolean.</summary>
        [JsonPropertyName("after")] public JsonElement? After { get; set; }
        [JsonPropertyName("source")] public ResultSourceDto? Source { get; set; }
        /// <summary>True when Revit read the written value back and it matched.</summary>
        [JsonPropertyName("verified")] public bool? Verified { get; set; }
    }

    public sealed class ResultTargetDto
    {
        /// <summary>One of <see cref="ResultTargetScopes"/>.</summary>
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("family")] public string? Family { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }
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

    public sealed class ResultCheckDto
    {
        /// <summary>One of <see cref="RuleSeverity"/>.</summary>
        [JsonPropertyName("severity")] public string Severity { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("actions")] public List<ResultActionDto>? Actions { get; set; }
    }

    /// <summary>A button on a check. An answer is a write.</summary>
    public sealed class ResultActionDto
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        /// <summary>One of <see cref="ResultActionKinds"/>.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("args")] public JsonElement? Args { get; set; }
    }

    public static class TurnResultReader
    {
        /// <summary>The turn_end's result, or null when it is absent, null or unreadable
        /// (not an object, a field of the wrong type, no reply or no kind). Never throws:
        /// a bad result must not lose the turn, the pane shows the prose instead.
        /// Unknown keys and unknown enum strings are kept, not rejected; the pane decides
        /// what to show. <see cref="TurnResultInvariants"/> lists contract breaks.</summary>
        public static TurnResultDto? TryRead(JsonElement? raw)
        {
            try { return Read(raw); }
            catch (Exception) { return null; }   // a converter or a disposed document must not lose the turn_end
        }

        private static TurnResultDto? Read(JsonElement? raw)
        {
            if (raw is not JsonElement element || element.ValueKind != JsonValueKind.Object) return null;
            var result = CopilotJson.Deserialize<TurnResultDto>(element);
            if (result == null || string.IsNullOrWhiteSpace(result.Reply) || string.IsNullOrEmpty(result.Kind)) return null;

            // A JSON null inside a list deserializes to a null item: drop it.
            if (result.Values != null) result.Values = result.Values.Where(v => v != null).ToList();
            if (result.Checks != null)
            {
                result.Checks = result.Checks.Where(c => c != null).ToList();
                foreach (var check in result.Checks)
                    if (check.Actions != null) check.Actions = check.Actions.Where(a => a != null).ToList();
            }
            if (result.NotSearched != null) result.NotSearched = result.NotSearched.Where(s => s != null).ToList();
            if (result.Next != null) result.Next = result.Next.Where(s => s != null).ToList();
            return result;
        }
    }

    /// <summary>Every way a result breaks protocol3.schema.json#/$defs/turn_result that
    /// the C# types cannot rule out themselves (an empty list means it is valid; unknown
    /// keys are not detectable and are ignored by design).</summary>
    public static class TurnResultInvariants
    {
        public static IReadOnlyList<string> Violations(TurnResultDto r)
        {
            var v = new List<string>();
            var reply = EnvelopeInvariants.CodePoints(r.Reply ?? "");
            if (reply < 1 || reply > TurnResultLimits.ReplyMaxChars) v.Add($"reply must be 1-{TurnResultLimits.ReplyMaxChars} characters (has {reply})");
            if (!TurnResultKinds.All.Contains(r.Kind)) v.Add($"kind '{r.Kind}' is not one of {string.Join("|", TurnResultKinds.All)}");
            if (r.Lang != null && !System.Text.RegularExpressions.Regex.IsMatch(r.Lang, "^[a-z]{2,3}$")) v.Add($"lang '{r.Lang}' is not a lowercase ISO 639 code");

            if (r.Counts != null)
                foreach (var (name, n) in CountsOf(r.Counts))
                    if (n is < 0) v.Add($"counts.{name} must be >= 0");

            if (r.Values != null)
            {
                if (r.Values.Count > TurnResultLimits.MaxValues) v.Add($"values has more than {TurnResultLimits.MaxValues} rows");
                for (var i = 0; i < r.Values.Count; i++) ValueViolations(r.Values[i], $"values[{i}]", v);
            }

            if (r.Checks != null)
            {
                if (r.Checks.Count > TurnResultLimits.MaxChecks) v.Add($"checks has more than {TurnResultLimits.MaxChecks} items");
                for (var i = 0; i < r.Checks.Count; i++) CheckViolations(r.Checks[i], $"checks[{i}]", v);
            }

            if (r.NotSearched != null)
                for (var i = 0; i < r.NotSearched.Count; i++)
                    if (string.IsNullOrEmpty(r.NotSearched[i])) v.Add($"not_searched[{i}] must not be empty");

            if (r.Next != null)
            {
                if (r.Next.Count > TurnResultLimits.MaxNext) v.Add($"next has more than {TurnResultLimits.MaxNext} items");
                for (var i = 0; i < r.Next.Count; i++)
                {
                    var n = EnvelopeInvariants.CodePoints(r.Next[i] ?? "");
                    if (n < 1 || n > TurnResultLimits.NextMaxChars) v.Add($"next[{i}] must be 1-{TurnResultLimits.NextMaxChars} characters (has {n})");
                }
            }
            return v;
        }

        private static IEnumerable<(string, int?)> CountsOf(ResultCountsDto c)
        {
            yield return ("elements", c.Elements); yield return ("types", c.Types); yield return ("values", c.Values);
            yield return ("written", c.Written); yield return ("reverted", c.Reverted); yield return ("skipped", c.Skipped);
            yield return ("failed", c.Failed); yield return ("created", c.Created); yield return ("findings", c.Findings);
        }

        private static void ValueViolations(ResultValueDto row, string at, List<string> v)
        {
            if (string.IsNullOrEmpty(row.Param)) v.Add($"{at}.param must not be empty");
            if (row.Target == null) v.Add($"{at}.target is required");
            else
            {
                if (!ResultTargetScopes.All.Contains(row.Target.Scope)) v.Add($"{at}.target.scope '{row.Target.Scope}' is not one of {string.Join("|", ResultTargetScopes.All)}");
                if (row.Target.Count is < 1) v.Add($"{at}.target.count must be >= 1");
            }
            foreach (var (name, cell) in new[] { ("before", row.Before), ("after", row.After) })
                if (cell is JsonElement e && e.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    v.Add($"{at}.{name} must be a string, number or boolean");
            if (row.Source != null) SourceViolations(row.Source, at + ".source", v);
        }

        private static void SourceViolations(ResultSourceDto s, string at, List<string> v)
        {
            if (!ResultSourceKinds.All.Contains(s.Kind)) v.Add($"{at}.kind '{s.Kind}' is not one of {string.Join("|", ResultSourceKinds.All)}");
            if (s.Kind == ResultSourceKinds.Document && string.IsNullOrEmpty(s.Attachment)) v.Add($"{at}: a document source names its attachment");
            if (s.Kind == ResultSourceKinds.Excel && (string.IsNullOrEmpty(s.Attachment) || s.Row == null)) v.Add($"{at}: an excel source names its attachment and row");
            if (s.Page is < 1) v.Add($"{at}.page must be >= 1");
            if (s.Row is < 1) v.Add($"{at}.row must be >= 1");
            if (s.Attachment != null && s.Attachment.Length == 0) v.Add($"{at}.attachment must not be empty");
            if (s.Quote != null && (s.Quote.Length == 0 || EnvelopeInvariants.CodePoints(s.Quote) > TurnResultLimits.QuoteMaxChars))
                v.Add($"{at}.quote must be 1-{TurnResultLimits.QuoteMaxChars} characters");
        }

        private static void CheckViolations(ResultCheckDto c, string at, List<string> v)
        {
            if (!RuleSeverity.All.Contains(c.Severity)) v.Add($"{at}.severity '{c.Severity}' is not one of {string.Join("|", RuleSeverity.All)}");
            if (string.IsNullOrEmpty(c.Text)) v.Add($"{at}.text must not be empty");
            if (c.Actions == null) return;
            if (c.Actions.Count > TurnResultLimits.MaxActions) v.Add($"{at}.actions has more than {TurnResultLimits.MaxActions} items");
            for (var i = 0; i < c.Actions.Count; i++)
            {
                var a = c.Actions[i];
                var aAt = $"{at}.actions[{i}]";
                if (string.IsNullOrEmpty(a.Label) || EnvelopeInvariants.CodePoints(a.Label) > TurnResultLimits.ActionLabelMaxChars)
                    v.Add($"{aAt}.label must be 1-{TurnResultLimits.ActionLabelMaxChars} characters");
                if (!ResultActionKinds.All.Contains(a.Kind)) v.Add($"{aAt}.kind '{a.Kind}' is not one of {string.Join("|", ResultActionKinds.All)}");
                else if (a.Kind != ResultActionKinds.Dismiss && (a.Args is not JsonElement args || args.ValueKind != JsonValueKind.Object))
                    v.Add($"{aAt}: a {a.Kind} action carries args");
            }
        }
    }
}
