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
            Assert.Equal("Revit tidak menerima perubahan ini: Highlighted walls overlap.",
                         PaneText.FixReason("Revit did not accept this change: Highlighted walls overlap.", PanelStrings.Ms));
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
            Assert.Equal(2, all.BuildFailDetails(20, PanelStrings.En).Split('\n').Length);
        }
    }
}
