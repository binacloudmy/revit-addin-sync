#nullable enable
// check (spec §11 #4, §21): runs args.rule_set_content (rewritten by the cloud) and returns pass / fail / not_verified.
//
// Parses the args into typed DTOs (CheckArgs: invalid_args on bad input),
// runs every rule in scope (CheckRunner), and returns a read envelope whose
// data is the §2.5 check result, with searched listing the sources read and
// every source that could not be (unloaded links, closed worksets). status
// is partial whenever anything is not_verified. check never changes the
// model: no transaction, no selection.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;

namespace BinaVibe.Copilot.Tools
{
    public sealed class CheckTool : ICopilotTool
    {
        public string Name => ToolNames.Check;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!CheckArgs.TryParse(args, out var parsed, out var error))
                return EnvelopeBuilder.Fail(ErrorCodes.InvalidArgs, error ?? "check args are invalid");
            var set = parsed!.RuleSet;

            CheckRunner runner;
            List<RuleRun> runs;
            try
            {
                runner = new CheckRunner(ctx.Doc, ctx.UiDoc, parsed.Scope);
                runs = runner.Run(set, parsed.Unsupported);
            }
            catch (Exception ex)
            {
                return EnvelopeBuilder.Fail(ErrorCodes.RuntimeError, $"check {set.Id} could not run: {ex.GetType().Name}: {ex.Message}");
            }

            if (runs.Count == 0 && parsed.Scope.Category != null)
            {
                var categories = set.Rules.SelectMany(r => r.Selector.Categories).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return EnvelopeBuilder.Fail(ErrorCodes.NotFound,
                    $"No rule in {set.Id} is about '{parsed.Scope.Category}'. Its rules cover: {string.Join(", ", categories)}.",
                    categories);
            }

            var result = CheckReport.Build(set, runs, ctx.Handles);
            var builder = EnvelopeBuilder.Read(CheckReport.Summary(set, result, runner.Skipped))
                .Status(CheckReport.Status(result, runner.Skipped))
                .WithData(result)
                .WithCount(result.Totals.Checked)
                .WithSearched(CheckReport.Searched(runner.Sources, runner.Categories, parsed.Scope, runner.NotSearched));
            var next = CheckReport.Next(result, runner.Skipped);
            if (next != null) builder = builder.WithNext(next);
            return builder.Build();
        }
    }
}
