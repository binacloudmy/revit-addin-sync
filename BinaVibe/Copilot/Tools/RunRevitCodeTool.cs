#nullable enable
// run_revit_code (spec §11 #7, §7.8): C# method-body statements against the
// Revit API, behind the deny-by-default policy in Resources/copilot/code-policy.json.
//
// The add-in decides the mode from the code (ModeClassifier), whatever the
// request says. Per rulings R13/R17 the call arrives in a kind:write request:
//   Plan()    resolves nothing in the model (code has no targets); it compiles,
//             checks the policy and sets IntendedValues["mode"]:
//               "write" -> W3's runner previews, shows the confirm card, commits;
//               "read"  -> the runner calls Execute(Commit) once, with no card.
//             Code that cannot run is "read" too, so a failure never reaches a card.
//   Execute() read  -> always-rolled-back TransactionGroup, CallbackKind.Read, phase read;
//             write -> one TxGuard transaction inside the runner's group, phase preview|commit.
//             When the runner already opened the transaction (W3's engine does, inside its
//             own scope), the code runs inside it and the runner records and decorates
//             (RevitCodeHost routing); a transaction opened by anyone else is refused.
// Run() (a read request) runs read-classified code and refuses code that writes:
// a write must reach the drafter's confirm card.

using System;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;

namespace BinaVibe.Copilot.Tools
{
    public sealed class RunRevitCodeTool : IWriteTool
    {
        public string Name => ToolNames.RunRevitCode;

        public ToolKind Kind => ToolKind.Code;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!RunRevitCodeArgs.TryParse(args, out var parsed, out var error)) return RunRevitCodeArgs.Invalid(error);
            var prepared = RevitCodeHost.Prepare(parsed!.Code);
            var failure = CodePlans.Failure(parsed, prepared);
            if (failure != null) return failure.ToEnvelope(EnvelopePhase.Read);

            if (CodePlans.ModeOf(parsed, prepared) == CodeMode.Write)
            {
                var reason = prepared.Analysis.Mode?.Reasons.Count > 0 ? prepared.Analysis.Mode.Reasons[0].ToString() : "it expects changes";
                return EnvelopeBuilder.Fail(ErrorCodes.NotAllowed,
                    "This code changes the model (" + reason + "), so it must go through the drafter's confirm card; it was not run.");
            }
            return Guarded(() => RevitCodeHost.Read(ctx.Doc, ctx.UiDoc, parsed, prepared), EnvelopePhase.Read);
        }

        public WritePlan Plan(ToolContext ctx, JsonElement args)
        {
            if (!RunRevitCodeArgs.TryParse(args, out var parsed, out _))
            {
                // Execute answers invalid_args; "read" keeps it away from the confirm card.
                return new WritePlan
                {
                    Tool = Name,
                    CanonicalArgs = args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText(),
                    IntendedValues = { [CodePlans.ModeKey] = CodePlans.Read, [CodePlans.GateKey] = ErrorCodes.InvalidArgs },
                };
            }
            return CodePlans.Build(parsed!, RevitCodeHost.Prepare(parsed!.Code));
        }

        public Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase)
        {
            if (!RunRevitCodeArgs.TryParse(args, out var parsed, out var error)) return RunRevitCodeArgs.Invalid(error);

            // The runner re-runs the stored batch: these args must be the planned ones.
            if (plan.CodeText != null && !string.Equals(plan.CodeText, parsed!.Code, StringComparison.Ordinal))
                return EnvelopeBuilder.Fail(ErrorCodes.NotAllowed, "run_revit_code: the code differs from the planned code; nothing was run.");

            var prepared = RevitCodeHost.Prepare(parsed!.Code);
            // Code that cannot run never reaches Revit: its failure goes back as a read (no card was shown).
            var cannotRun = CodePlans.Failure(parsed, prepared);
            if (cannotRun != null) return cannotRun.ToEnvelope(EnvelopePhase.Read);
            var mode = CodePlans.ModeOf(parsed, prepared);
            plan.IntendedValues.TryGetValue(CodePlans.ModeKey, out var planned);

            if (mode == CodeMode.Write && planned != CodePlans.Write)
                // A plan that skipped the confirm card can never run a write (fail closed).
                return EnvelopeBuilder.Fail(ErrorCodes.NotAllowed,
                    "This code changes the model but was not planned as a change, so it was not run.");

            if (mode == CodeMode.Read && planned != CodePlans.Write)
                return Guarded(() => RevitCodeHost.Read(ctx.Doc, ctx.UiDoc, parsed, prepared), EnvelopePhase.Read);

            // Planned as a write (the stricter of the two always wins).
            var phaseName = EnvelopePhase.Of(phase);
            return Guarded(() => RevitCodeHost.Write(ctx.Doc, ctx.UiDoc, ctx.Handles, parsed, prepared, phase), phaseName);
        }

        /// <summary>Never throws into the host: anything unexpected is a failed envelope that says what happened.</summary>
        private static Envelope Guarded(Func<Envelope> run, string phase)
        {
            try
            {
                return run();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                var failure = new CodeFailure
                {
                    Code = ErrorCodes.RuntimeError,
                    Summary = "run_revit_code failed in the add-in: " + ex.GetType().Name + ": " + ex.Message,
                    Message = "run_revit_code failed in the add-in (" + ex.GetType().Name + ": " + ex.Message + ")."
                              + (phase == EnvelopePhase.Commit ? " Changes may or may not have been applied; inspect before retrying." : ""),
                    Next = "Inspect the model before retrying.",
                };
                return failure.ToEnvelope(phase);
            }
        }
    }
}
