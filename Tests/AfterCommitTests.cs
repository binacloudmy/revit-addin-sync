#nullable enable
// W3 fix round 3 (ruling R76). UI work a commit-phase tool queues on
// ToolContext.AfterCommit (view change, selection, zoom) runs on Revit's thread
// only after the batch committed and assimilated, in call order, each action
// in its own try/catch: a failing action is a warning, never a failed commit.
// Rolled back, drifted, cancelled or failed batches drop them, and the preview
// never runs them. A no-op batch (nothing changed, nothing to roll back) still
// runs them, so a code call's view or selection request is not lost.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AfterCommitTests
    {
        [Fact]
        public void RunsEveryActionInCallOrderAndTurnsFailuresIntoWarnings()
        {
            var log = new List<string>();
            var perCall = new List<IReadOnlyList<Action>?>
            {
                new Action[] { () => log.Add("c1.a"), () => throw new InvalidOperationException("view is a template"), () => log.Add("c1.c") },
                null,
                new Action[] { () => log.Add("c3.a") },
            };

            var failures = AfterCommitRunner.Run(perCall);

            Assert.Equal(new[] { "c1.a", "c1.c", "c3.a" }, log);
            var f = Assert.Single(failures);
            Assert.Equal(0, f.Call);
            Assert.Contains("view is a template", f.Message);
            Assert.Contains("the change itself is committed", AfterCommitRunner.WarningText(f));
        }

        [Theory]
        [InlineData(CommitKind.Committed, true, true)]
        [InlineData(CommitKind.Committed, false, true)]    // a no-op batch: nothing to roll back, its UI work still runs (W4 m2)
        [InlineData(CommitKind.Drift, true, false)]
        [InlineData(CommitKind.RevisionMoved, true, false)]
        [InlineData(CommitKind.DocChanged, true, false)]
        [InlineData(CommitKind.Failed, true, false)]
        public void RunsOnlyAfterAnAssimilatedCommit(CommitKind kind, bool undoEntry, bool runs)
        {
            Assert.Equal(runs, AfterCommitRunner.ShouldRun(kind, undoEntry));
        }

        [Fact]
        public void NothingRunsWhenNothingWasQueued()
        {
            Assert.Empty(AfterCommitRunner.Run(new List<IReadOnlyList<Action>?>()));
        }
    }
}
