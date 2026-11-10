#nullable enable
// Post-commit UI work (ruling R76). Revit-free.
//
// A commit-phase tool may queue UI work (view change, selection, zoom) on
// ToolContext.AfterCommit. The engine runs it on Revit's thread only after
// the batch committed and was assimilated into its undo entry, in call order
// and queue order, each action in its own try/catch: a failing action becomes
// a warning on that call's envelope and never fails the commit. A batch that
// changed nothing (noop: no undo entry, nothing to roll back) runs its queue
// too, so a code call's view or selection request is not lost (W4 re-review
// 2, m2). A batch that rolled back, drifted, was cancelled or failed drops
// the queue, and the preview never runs it (its contexts are thrown away).
// Read-mode UI is W4's alone and never comes through here.

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

        /// <summary>True for a batch that committed — with an undo entry, or a noop that changed
        /// nothing and so has nothing to roll back. Every other outcome drops the queue.</summary>
        public static bool ShouldRun(CommitKind kind, bool undoEntry) => kind == CommitKind.Committed;

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
