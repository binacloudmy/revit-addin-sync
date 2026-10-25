#nullable enable
// Post-commit UI work (ruling R76). Revit-free.
//
// A commit-phase tool may queue UI work (view change, selection, zoom) on
// ToolContext.AfterCommit. The engine runs it on Revit's thread only after
// the batch committed and was assimilated into its undo entry, in call order
// and queue order, each action in its own try/catch: a failing action becomes
// a warning on that call's envelope and never fails the commit. A batch that
// rolled back, drifted, was cancelled, failed or changed nothing (no undo
// entry) drops the queue, and the preview never runs it (its contexts are
// thrown away). A read-mode call (one run, nothing to roll back) runs its
// queue once it succeeded.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Writes
{
    public static class AfterCommitRunner
    {
        public sealed class Failure
        {
            public Failure(int call, string message) { Call = call; Message = message; }
            /// <summary>Index of the call (in the batch) whose action failed.</summary>
            public int Call { get; }
            public string Message { get; }
        }

        /// <summary>True only for a batch that committed and left an undo entry.</summary>
        public static bool ShouldRun(CommitKind kind, bool undoEntry) => kind == CommitKind.Committed && undoEntry;

        /// <summary>Runs every call's queued actions, in call order; returns the ones that threw.</summary>
        public static IReadOnlyList<Failure> Run(IReadOnlyList<IReadOnlyList<Action>?> perCall)
        {
            var failures = new List<Failure>();
            if (perCall == null) return failures;
            for (var i = 0; i < perCall.Count; i++)
            {
                var actions = perCall[i];
                if (actions == null) continue;
                foreach (var action in actions)
                {
                    if (action == null) continue;
                    try { action(); }
                    catch (Exception ex) { failures.Add(new Failure(i, ex.GetType().Name + ": " + ex.Message)); }
                }
            }
            return failures;
        }

        public static string WarningText(Failure f) =>
            "A follow-up step after the commit failed (" + f.Message + "); the change itself is committed.";
    }
}
