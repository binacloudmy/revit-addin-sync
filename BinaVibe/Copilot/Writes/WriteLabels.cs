#nullable enable
// Add-in-owned labels for write batches (spec §7.5: the card shows tool labels
// the add-in owns; §7.6: one named undo entry per batch). Revit-free.
//
// The label comes from the plan's own canonical args, never from the
// assistant's description: set_parameters names its parameter and target
// count; run_revit_code shows its (drafter-visible) intent, trimmed.

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
        private const int IntentChars = 60;

        public static string ForPlan(WritePlan plan)
        {
            var args = Parse(plan.CanonicalArgs);
            switch (plan.Tool)
            {
                case ToolNames.SetParameters:
                    var param = Text(args, "param") ?? "parameter";
                    var n = plan.TargetIds?.Length ?? 0;
                    return "Set " + param + (n > 0 ? " (" + n.ToString(CultureInfo.InvariantCulture) + ")" : "");
                case ToolNames.RunRevitCode:
                    var intent = Text(args, "intent");
                    return string.IsNullOrWhiteSpace(intent) ? "Revit code" : "Revit code: " + Trim(intent!, IntentChars);
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
