#nullable enable
// The WritePlan run_revit_code hands W3's batch runner (rulings R13, R17).
// Revit-free.
//
// run_revit_code always arrives in a kind:write request. IntendedValues["mode"]
// tells the runner what to do with it:
//   "write" -> preview (rolled back), the drafter's confirm card, commit;
//   "read"  -> no card: the runner calls Execute(Commit) once and the tool runs
//              the code in an always-rolled-back TransactionGroup (phase read).
// Code that cannot run at all (bad args, compile error, policy rejection) is
// "read" too, so a failure never reaches a confirm card; Execute returns it.
// The plan hash covers the code text, the canonical args and expected counts.

using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Code
{
    public static class CodePlans
    {
        public const string ModeKey = "mode";
        public const string GateKey = "gate";
        public const string IntentKey = "intent";
        public const string CodeHashKey = "code_sha256";
        public const string ReasonKey = "write_reason";

        /// <summary>"true" when the code moves the active view or zoom (RequestViewChange, ShowElements).
        /// Navigation is UI state Revit applies after the callback; a runner that hosts a read itself
        /// should not wrap a navigating read in its always-rolled-back group (W4's own path does not).</summary>
        public const string NavigatesKey = "navigates";
        public const string Read = "read";
        public const string Write = "write";

        /// <summary>Expected counts for code have no category: they are filed under this key.</summary>
        public const string AnyCategory = "*";

        public static WritePlan Build(RunRevitCodeArgs args, PreparedCode prepared)
        {
            var mode = ModeOf(args, prepared);
            var plan = new WritePlan
            {
                Tool = ToolNames.RunRevitCode,
                CanonicalArgs = args.Canonical(),
                CodeText = args.Code,
                Previewable = true,
                IntendedValues = new Dictionary<string, string>
                {
                    [ModeKey] = mode == CodeMode.Write ? Write : Read,
                    [GateKey] = prepared.Failure == null ? "ok" : prepared.Failure.Code,
                    [IntentKey] = args.Intent,
                    [CodeHashKey] = prepared.CodeHash,
                    [NavigatesKey] = prepared.Failure == null && prepared.Analysis.Mode?.Navigates == true ? "true" : "false",
                },
            };
            var decision = prepared.Analysis.Mode;
            if (mode == CodeMode.Write && decision != null && decision.Reasons.Count > 0)
                plan.IntendedValues[ReasonKey] = decision.Reasons[0].ToString();
            if (args.Expected?.Adds is int adds) plan.ExpectedAddedByCategory[AnyCategory] = adds;
            if (args.Expected?.Deletes is int deletes) plan.ExpectedDeletedByCategory[AnyCategory] = deletes;
            return plan;
        }

        /// <summary>Write only when the code can run and either the code or the model's expected counts say so.</summary>
        public static CodeMode ModeOf(RunRevitCodeArgs args, PreparedCode prepared)
        {
            if (prepared.Failure != null || prepared.Analysis.Mode == null) return CodeMode.Read;
            return ModeClassifier.WithExpected(prepared.Analysis.Mode, args.Expected).Mode;
        }
    }
}
