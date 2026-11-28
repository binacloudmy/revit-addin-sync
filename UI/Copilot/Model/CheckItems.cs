#nullable enable
// The server's own result of a turn (turn_end.result): the things to look at, each with the buttons
// that answer it, and the next steps. The card is built from the envelopes; these ride beside it,
// and a button is only ever offered when the contract says how to run it. Pure, so a test pins it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class CheckAction
    {
        public string Label = "";
        /// <summary>One of <see cref="ResultActionKinds"/>.</summary>
        public string Kind = "";
        public JsonElement? Args;
    }

    public sealed class CheckItem
    {
        public string Severity = "";
        public string Text = "";
        public List<CheckAction> Actions = new List<CheckAction>();
    }

    public static class CheckItems
    {
        /// <summary>The checks of a server result, keeping only buttons the pane can run (a known kind; args as the
        /// kind needs), at most <see cref="TurnResultLimits.MaxActions"/> per check.</summary>
        public static List<CheckItem> From(TurnResultDto? result)
        {
            var list = new List<CheckItem>();
            foreach (var c in result?.Checks ?? new List<ResultCheckDto>())
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Text)) continue;
                var item = new CheckItem { Severity = c.Severity ?? "", Text = c.Text.Trim() };
                foreach (var a in (c.Actions ?? new List<ResultActionDto>()).Take(TurnResultLimits.MaxActions))
                {
                    if (a == null || string.IsNullOrWhiteSpace(a.Label)) continue;
                    var action = new CheckAction { Label = a.Label.Trim(), Kind = a.Kind ?? "", Args = a.Args };
                    if (Vet(action) == null) item.Actions.Add(action);
                }
                list.Add(item);
            }
            return list;
        }

        /// <summary>The next steps of a server result: at most three, trimmed, none empty.</summary>
        public static List<string> NextOf(TurnResultDto? result) =>
            (result?.Next ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Take(TurnResultLimits.MaxNext).ToList();

        /// <summary>Why the pane will not run this button, or null when it can.</summary>
        public static string? Vet(CheckAction a)
        {
            if (a == null) return "no action";
            switch (a.Kind)
            {
                case ResultActionKinds.Dismiss:
                    return null;
                case ResultActionKinds.Prompt:
                    return PromptText(a) == null ? "a prompt button carries text" : null;
                case ResultActionKinds.SetParameters:
                    if (a.Args is not JsonElement args || args.ValueKind != JsonValueKind.Object) return "a set_parameters button carries args";
                    // A table is the drafter's attachment: a button never writes from one.
                    if (args.TryGetProperty("from_table", out _)) return "a button never writes from a table";
                    return null;
                default:
                    return "unknown button kind '" + a.Kind + "'";
            }
        }

        /// <summary>The text a prompt button sends, or null.</summary>
        public static string? PromptText(CheckAction a)
        {
            if (a?.Args is not JsonElement args || args.ValueKind != JsonValueKind.Object) return null;
            if (!args.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return null;
            var s = t.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        }

        /// <summary>The set_parameters call a button runs: its args exactly as the server sent them, under a card id.</summary>
        public static ToolCallDto? SetParametersCall(CheckAction a)
        {
            if (a == null || a.Kind != ResultActionKinds.SetParameters || Vet(a) != null) return null;
            return new ToolCallDto
            {
                CallId = "card_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                IdempotencyKey = "card:" + Guid.NewGuid().ToString("N"),
                Tool = ToolNames.SetParameters,
                Args = a.Args!.Value.Clone(),
            };
        }
    }
}
