using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Revit refusing a Fix all batch at commit time: the batch is retried without
    /// the steps on the elements Revit named (then step by step), so one bad fix no
    /// longer discards the others. The Revit-side retry is in JkrRenameHandler; these
    /// are its Revit-free parts.
    /// </summary>
    public class JkrBatchRetryTests
    {
        // ── A commit that throws is a refused commit (AI-425) ──

        // Fake applicator: a transaction over the steps whose commit throws when it holds the bad step.
        private static JkrBatchRetry.Attempt<List<long>> FakeRun(List<long> steps, long bad, int[] commits)
        {
            var a = new JkrBatchRetry.Attempt<List<long>>();
            commits[0]++;
            a.Committed = JkrBatchRetry.TryCommit(() =>
            {
                if (steps.Contains(bad)) throw new System.InvalidOperationException("Revit blew up at commit");
                return true;
            }, a.Errors);
            a.Status = a.Committed ? "Committed" : "RolledBack";
            a.Result = a.Committed ? steps : null;
            return a;
        }

        [Fact]
        public void A_throwing_commit_keeps_every_other_step_and_fails_only_the_bad_one()
        {
            var written = new List<long>();
            var failed = new List<(long, string)>();
            var commits = new[] { 0 };
            JkrBatchRetry.Apply(new List<long> { 1, 2, 3, 4 }, s => s,
                ss => FakeRun(ss, 3, commits),
                r => written.AddRange(r),
                (s, why) => failed.Add((s, why)));

            Assert.Equal(new long[] { 1, 2, 4 }, written.OrderBy(x => x));
            Assert.Single(failed);
            Assert.Equal(3L, failed[0].Item1);
            Assert.Equal("Revit did not accept this change: Revit blew up at commit", failed[0].Item2);
        }

        [Fact]
        public void A_throwing_commit_with_no_element_named_goes_step_by_step()
        {
            var commits = new[] { 0 };
            var written = new List<long>();
            JkrBatchRetry.Apply(new List<long> { 1, 2 }, s => s,
                ss => FakeRun(ss, 2, commits), r => written.AddRange(r), (s, why) => { });
            Assert.Equal(new long[] { 1 }, written);
            Assert.Equal(3, commits[0]);   // whole batch, then each step on its own
        }

        [Fact]
        public void TryCommit_passes_a_normal_result_through()
        {
            var errors = new List<string>();
            Assert.True(JkrBatchRetry.TryCommit(() => true, errors));
            Assert.False(JkrBatchRetry.TryCommit(() => false, errors));
            Assert.Empty(errors);
        }

        [Fact]
        public void Steps_on_the_elements_revit_named_are_set_aside()
        {
            var steps = new[] { 10L, 11L, 12L, 11L };
            var (suspects, others) = JkrBatchRetry.Split(steps, s => s, new HashSet<long> { 11, 99 });
            Assert.Equal(new[] { 11L, 11L }, suspects);
            Assert.Equal(new[] { 10L, 12L }, others);
        }

        [Fact]
        public void When_revit_names_no_element_every_step_is_retried_on_its_own()
        {
            var (suspects, others) = JkrBatchRetry.Split(new[] { 1L, 2L }, s => s, new HashSet<long>());
            Assert.Empty(suspects);
            Assert.Equal(new[] { 1L, 2L }, others);       // the handler then goes step by step
        }

        [Fact]
        public void The_reason_is_revits_own_message()
        {
            Assert.Equal("Revit did not accept this change: Highlighted walls overlap. Walls overlap.",
                         JkrBatchRetry.CommitReason(new[] { "Highlighted walls overlap.", " Walls overlap. ", "Highlighted walls overlap." }, "RolledBack"));
            Assert.Equal("Revit did not accept this change (RolledBack).", JkrBatchRetry.CommitReason(new string[0], "RolledBack"));
        }

        [Theory]
        [InlineData("name\r\nParameter name: The type name \"x\" is already in use.", "The type name \"x\" is already in use.")]
        [InlineData("The type name \"x\" is already in use.\r\nParameter name: name", "The type name \"x\" is already in use.")]
        [InlineData("The value is out of range. (Parameter 'value')", "The value is out of range.")]
        [InlineData("Parameter 'Area' is read-only", "Parameter 'Area' is read-only")]
        [InlineData("Error", "Error")]
        public void Revit_argument_errors_read_as_one_sentence(string raw, string clean)
        {
            Assert.Equal(clean, RenameResult.CleanMessage(raw));
            var r = new RenameResult();
            r.RecordFailure("r:1", 1, "Rename 1", raw);
            Assert.Equal(clean, r.ReasonFor("rename_type", 1, null));
        }

        [Fact]
        public void Attempts_add_up_into_one_result()
        {
            var all = new RenameResult();
            var part = new RenameResult { Renamed = 3, ParamFixed = 2 };
            part.RecordFailure("r:5", 5, "Rename 5", "Rename target is empty.");
            all.Absorb(part);
            all.Absorb(new RenameResult { Renamed = 1 });
            all.RecordFailure("p:7:Area", 7, "Area on 7", JkrBatchRetry.CommitReason(new[] { "Can't set Area." }, "RolledBack"));
            Assert.Equal((4, 2, 2), (all.Renamed, all.ParamFixed, all.Failed));
            Assert.Equal("Rename target is empty.", all.ReasonFor("rename_type", 5, null));
            Assert.Equal("Revit did not accept this change: Can't set Area.", all.ReasonFor("set_parameter", 7, "Area"));
            Assert.Equal(2, all.BuildFailDetails(20).Split('\n').Length);
        }
    }
}
