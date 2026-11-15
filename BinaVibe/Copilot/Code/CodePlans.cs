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
// The plan hash covers the code text, the canonical args, expected counts and
// the Revit UI state the code reads besides the model (whole-branch review I-3):
// the active view (every snippet's `view`) and, for code that uses
// uidoc.Selection, a hash of the selection. W3's commit re-plans every call and
// compares hashes, so a view switch or a new selection while the confirm card
// is open drifts the commit instead of silently re-targeting tags or dimensions.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Code
{
    /// <summary>The Revit UI state a plan is bound to, read by the tool when it plans (Revit-free, so
    /// the binding is tested without Revit).</summary>
    public sealed class CodeBinding
    {
        /// <param name="activeViewId">The view the code gets as <c>view</c>; null when there is none.</param>
        /// <param name="selection">The selection as stable keys (host ids, references of linked picks).
        /// Called only for code that uses the selection.</param>
        public CodeBinding(long? activeViewId, Func<IEnumerable<string>> selection)
        {
            ActiveViewId = activeViewId;
            Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        }

        public long? ActiveViewId { get; }
        public Func<IEnumerable<string>> Selection { get; }
    }

    public static class CodePlans
    {
        public const string ModeKey = "mode";
        public const string GateKey = "gate";
        public const string IntentKey = "intent";
        public const string CodeHashKey = "code_sha256";
        public const string ReasonKey = "write_reason";
        public const string ViewKey = "view";
        public const string SelectionKey = "selection_sha256";

        /// <summary>The view value when Revit has no active view.</summary>
        public const string NoView = "none";

        public const string Read = "read";
        public const string Write = "write";

        /// <summary>Expected counts for code have no category: they are filed under this key.</summary>
        public const string AnyCategory = "*";

        public static WritePlan Build(RunRevitCodeArgs args, PreparedCode prepared, CodeBinding binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var mode = ModeOf(args, prepared);
            var failure = Failure(args, prepared);
            var plan = new WritePlan
            {
                Tool = ToolNames.RunRevitCode,
                CanonicalArgs = args.Canonical(),
                CodeText = args.Code,
                Previewable = true,
                IntendedValues = new Dictionary<string, string>
                {
                    [ModeKey] = mode == CodeMode.Write ? Write : Read,
                    [GateKey] = failure == null ? "ok" : failure.Code,
                    [IntentKey] = args.Intent,
                    [CodeHashKey] = prepared.CodeHash,
                    [ViewKey] = binding.ActiveViewId?.ToString(CultureInfo.InvariantCulture) ?? NoView,
                },
            };
            if (prepared.Analysis.Mode?.UsesSelection == true)
                plan.IntendedValues[SelectionKey] = SelectionHash(binding.Selection());
            var decision = prepared.Analysis.Mode;
            if (mode == CodeMode.Write && decision != null && decision.Reasons.Count > 0)
                plan.IntendedValues[ReasonKey] = decision.Reasons[0].ToString();
            if (args.Expected?.Adds is int adds) plan.ExpectedAddedByCategory[AnyCategory] = adds;
            if (args.Expected?.Deletes is int deletes) plan.ExpectedDeletedByCategory[AnyCategory] = deletes;
            return plan;
        }

        /// <summary>Lowercase hex SHA-256 over the selection's keys, sorted ordinally and de-duplicated.</summary>
        public static string SelectionHash(IEnumerable<string> keys) =>
            CodeGate.Hash(string.Join("\n", (keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)));

        /// <summary>Write only when the code can run and either the code or the model's expected counts say so.
        /// Anything that cannot run is Read, so its failure is handed straight back with no card.</summary>
        public static CodeMode ModeOf(RunRevitCodeArgs args, PreparedCode prepared) =>
            Failure(args, prepared) != null ? CodeMode.Read : RawMode(args, prepared);

        /// <summary>Why this call cannot run, or null: the prepared code's own failure, or, when the model's
        /// expected counts make it a write, a clock, Random or new Guid in it (ruling R60).</summary>
        public static CodeFailure? Failure(RunRevitCodeArgs args, PreparedCode prepared)
        {
            if (prepared.Failure != null) return prepared.Failure;
            var decision = prepared.Analysis.Mode;
            if (decision != null && decision.WriteOnly.Count > 0 && RawMode(args, prepared) == CodeMode.Write)
                return ErrorMapper.FromPolicy(decision.WriteOnly);
            return null;
        }

        private static CodeMode RawMode(RunRevitCodeArgs args, PreparedCode prepared) =>
            prepared.Failure != null || prepared.Analysis.Mode == null
                ? CodeMode.Read
                : ModeClassifier.WithExpected(prepared.Analysis.Mode, args.Expected).Mode;
    }
}
