#nullable enable
// Add-in-owned labels for write batches (spec §7.5: the card shows tool labels
// the add-in owns; §7.6: one named undo entry per batch). Revit-free.
//
// Ruling R42: the label and the undo entry name are the add-in's own words —
// "Set parameter <param>", "Revit code" — never model text. run_revit_code's
// intent (written by the model) is exposed separately by IntentOf and goes on
// the card's "Assistant's description" line only (AssistantDescription).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Undo;

namespace BinaVibe.Copilot.Writes
{
    public static class WriteLabels
    {
        private const int IntentChars = 120;
        private const int ParamChars = 60;

        public static string ForPlan(WritePlan plan)
        {
            var args = Parse(plan.CanonicalArgs);
            switch (plan.Tool)
            {
                case ToolNames.SetParameters:
                    // The parameter name is the model's choice but names a real parameter
                    // the plan resolved; it is trimmed and never carries instructions.
                    return "Set parameter " + Trim(Text(args, "param") ?? "value", ParamChars);
                case ToolNames.RunRevitCode:
                    return "Revit code";
                default:
                    return string.IsNullOrEmpty(plan.Tool) ? "change" : plan.Tool;
            }
        }

        /// <summary>"BINA: &lt;first label&gt;" plus "(+N more)" for a multi-call batch.</summary>
        public static string GroupName(IReadOnlyList<WritePlan> plans)
        {
            if (plans == null || plans.Count == 0) return BinaTransactionNames.For("change");
            var first = ForPlan(plans[0]);
            var more = plans.Count - 1;
            return BinaTransactionNames.For(more > 0 ? first + " (+" + more.ToString(CultureInfo.InvariantCulture) + " more)" : first);
        }

        /// <summary>The model's stated intent of a run_revit_code plan, trimmed; null otherwise.</summary>
        public static string? IntentOf(WritePlan plan)
        {
            if (plan == null || plan.Tool != ToolNames.RunRevitCode) return null;
            var intent = Text(Parse(plan.CanonicalArgs), "intent");
            return string.IsNullOrWhiteSpace(intent) ? null : Trim(intent!, IntentChars);
        }

        /// <summary>The card's "Assistant's description" line: the request's description plus
        /// each code call's intent ("Code: …"). Null when there is none.</summary>
        public static string? AssistantDescription(string? description, IEnumerable<WritePlan> plans)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(description)) lines.Add(description!.Trim());
            foreach (var p in plans ?? Enumerable.Empty<WritePlan>())
            {
                var intent = IntentOf(p);
                if (intent != null) lines.Add("Code: " + intent);
            }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private static string Trim(string s, int max)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json!);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { return null; }
        }

        private static string? Text(JsonElement? args, string name) =>
            args is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
