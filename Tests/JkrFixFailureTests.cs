using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// What the pane learns about failed fixes after a batch (RenameResult), how an
    /// issue shows a failed fix (IssueVm.FixError), and the affected-element count
    /// behind the "Revit may pause" warning (JkrFixBatch.CountAffected).
    /// </summary>
    public class JkrFixFailureTests
    {
        // ── RenameResult: every failure is recorded with its reason ──

        [Fact]
        public void A_failed_parameter_fix_is_recorded_with_its_reason()
        {
            var r = new RenameResult();
            var key = RenameResult.MakeFixKey("set_parameter", 42, "Kod_Jenis");
            r.RecordFailure(key, 42, "Kod_Jenis on 42", "Parameter 'Kod_Jenis' is read-only");

            Assert.Equal(1, r.Failed);
            Assert.Equal(0, r.Skipped);
            Assert.Contains(key, r.FailedFixKeys);
            Assert.Contains(42L, r.FailedElementIds);
            Assert.Equal("Parameter 'Kod_Jenis' is read-only", r.ReasonFor("set_parameter", 42, "Kod_Jenis"));
        }

        [Fact]
        public void Set_jkr_code_and_set_parameter_share_a_key_so_the_reason_is_found_for_either()
        {
            var r = new RenameResult();
            r.RecordFailure(RenameResult.MakeFixKey("set_parameter", 7, "Sistem"), 7, "Sistem on 7", "read-only");
            Assert.Equal("read-only", r.ReasonFor("set_jkr_code", 7, "Sistem"));
        }

        [Fact]
        public void A_skipped_rename_counts_as_skipped_and_keeps_its_reason()
        {
            var r = new RenameResult();
            r.RecordFailure(RenameResult.MakeFixKey("rename_type", 9, null), 9, "Rename 9", "Element 9 was not found in the model.", skipped: true);

            Assert.Equal(0, r.Failed);
            Assert.Equal(1, r.Skipped);
            Assert.Equal("Element 9 was not found in the model.", r.ReasonFor("rename_type", 9, "ignored-for-renames"));
        }

        [Fact]
        public void A_blank_reason_becomes_a_readable_default()
        {
            var r = new RenameResult();
            r.RecordFailure("p:1:X", 1, "X on 1", "   ");
            Assert.Equal(RenameResult.UnknownFailureReason, r.ReasonFor("set_parameter", 1, "X"));
        }

        [Fact]
        public void A_fix_that_did_not_fail_has_no_reason()
        {
            var r = new RenameResult();
            r.RecordFailure("p:1:X", 1, "X on 1", "read-only");
            Assert.Null(r.ReasonFor("set_parameter", 1, "Y"));
            Assert.Null(r.ReasonFor("rename_type", 1, null));
        }

        [Fact]
        public void One_failure_on_an_element_does_not_mark_its_other_fixes_failed()
        {
            var r = new RenameResult();
            r.RecordFailure(RenameResult.MakeFixKey("set_parameter", 5, "Kod_Jenis"), 5, "Kod_Jenis on 5", "read-only");
            r.Renamed = 1; r.ParamFixed = 1;

            Assert.Null(r.ReasonFor("rename_type", 5, null));
            Assert.Null(r.ReasonFor("set_parameter", 5, "Sistem"));
        }

        [Fact]
        public void Fail_details_list_every_failure_in_run_order()
        {
            var r = new RenameResult();
            r.RecordFailure("p:1:A", 1, "A on 1", "read-only");
            r.RecordFailure("r:2", 2, "Rename 2 → 'x'", "name already used");
            r.RecordFailure("p:3:B", 3, "B on 3", "inside a model group");

            Assert.Equal("A on 1: read-only\nRename 2 → 'x': name already used\nB on 3: inside a model group",
                         r.BuildFailDetails(20));
        }

        [Fact]
        public void Fail_details_beyond_the_limit_end_with_a_count_of_the_rest()
        {
            var r = new RenameResult();
            for (int i = 1; i <= 25; i++) r.RecordFailure($"p:{i}:A", i, $"A on {i}", "read-only");

            var lines = r.BuildFailDetails(20).Split('\n');
            Assert.Equal(21, lines.Length);
            Assert.Equal("A on 20: read-only", lines[19]);
            Assert.Equal("…and 5 more", lines[20]);
        }

        [Fact]
        public void No_failures_means_empty_fail_details()
        {
            Assert.Equal("", new RenameResult().BuildFailDetails(20));
        }

        [Fact]
        public void A_refused_commit_clears_the_applied_counts_but_keeps_the_failures()
        {
            var r = new RenameResult { Renamed = 3, ParamFixed = 4 };
            r.RecordFailure("p:1:A", 1, "A on 1", "read-only");
            r.ClearApplied();

            Assert.Equal(0, r.Renamed + r.ParamFixed);
            Assert.Equal(1, r.Failed);
        }

        // ── IssueVm: a failed fix stays Open and shows why ──

        [Fact]
        public void An_issue_with_a_fix_error_stays_open_and_shows_the_reason()
        {
            var vm = new IssueVm { Status = IssueStatus.Open, AutoFixable = true };
            vm.FixError = "Parameter 'Kod_Jenis' is read-only";

            Assert.True(vm.IsOpen);
            Assert.NotEqual(IssueStatus.ManualFixNeeded, vm.Status);
            Assert.True(vm.HasFixError);
            Assert.Equal("Fix failed: Parameter 'Kod_Jenis' is read-only", vm.FixErrorLabel);
        }

        [Fact]
        public void Clearing_the_fix_error_hides_it()
        {
            var vm = new IssueVm { FixError = "x" };
            vm.FixError = null;
            Assert.False(vm.HasFixError);
            Assert.Equal("", vm.FixErrorLabel);
        }

        [Fact]
        public void Setting_the_fix_error_notifies_bindings()
        {
            var vm = new IssueVm();
            var raised = 0;
            vm.PropertyChanged += (_, __) => raised++;
            vm.FixError = "read-only";
            vm.FixError = "read-only"; // unchanged: no second notification
            Assert.Equal(1, raised);
        }

        // ── CountAffected: instances of renamed types count toward the 500 warning ──

        private static IEnumerable<(long Id, long TypeId)> Instances(int count, long typeId, long firstId = 1000)
        {
            for (int i = 0; i < count; i++) yield return (firstId + i, typeId);
        }

        [Fact]
        public void Without_type_wide_steps_the_count_is_the_distinct_direct_elements()
        {
            var n = JkrFixBatch.CountAffected(new long[] { 1, 2, 2, 3 }, new HashSet<long>(), Instances(10_000, 77), 500);
            Assert.Equal(3, n);
        }

        [Fact]
        public void Without_type_wide_steps_the_model_is_not_walked()
        {
            var walked = 0;
            IEnumerable<(long, long)> Tracking() { foreach (var x in Instances(10, 77)) { walked++; yield return x; } }

            JkrFixBatch.CountAffected(new long[] { 1 }, new HashSet<long>(), Tracking(), 500);
            Assert.Equal(0, walked);
        }

        [Fact]
        public void A_type_rename_counts_every_instance_of_that_type()
        {
            // Renaming type 77 (direct id 77) touches its 120 instances; type 88 is untouched.
            var model = Instances(120, 77).Concat(Instances(300, 88, firstId: 5000));
            var n = JkrFixBatch.CountAffected(new long[] { 77 }, new HashSet<long> { 77 }, model, 500);
            Assert.Equal(121, n);
        }

        [Fact]
        public void An_instance_fixed_directly_and_through_its_type_counts_once()
        {
            var n = JkrFixBatch.CountAffected(new long[] { 77, 1000 }, new HashSet<long> { 77 }, Instances(5, 77), 500);
            Assert.Equal(6, n); // type 77 + instances 1000..1004
        }

        [Fact]
        public void Three_type_renames_of_200_instances_reach_the_pause_warning()
        {
            var model = Instances(200, 1, 1000).Concat(Instances(200, 2, 2000)).Concat(Instances(200, 3, 3000));
            var n = JkrFixBatch.CountAffected(new long[] { 1, 2, 3 }, new HashSet<long> { 1, 2, 3 }, model, 500);
            Assert.True(JkrFixBatch.MayPauseRevit(n));
        }

        [Fact]
        public void Counting_stops_once_the_threshold_is_reached()
        {
            var walked = 0;
            IEnumerable<(long, long)> Tracking() { foreach (var x in Instances(100_000, 77)) { walked++; yield return x; } }

            var n = JkrFixBatch.CountAffected(new long[] { 77 }, new HashSet<long> { 77 }, Tracking(), 500);
            Assert.Equal(500, n);
            Assert.Equal(499, walked);
        }

        [Fact]
        public void Direct_elements_alone_can_reach_the_threshold()
        {
            var ids = Enumerable.Range(1, 600).Select(i => (long)i);
            Assert.Equal(500, JkrFixBatch.CountAffected(ids, new HashSet<long> { 9 }, Instances(10, 9), 500));
        }

        [Fact]
        public void Stop_at_must_be_positive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                JkrFixBatch.CountAffected(new long[0], null, null, 0));
        }

        [Fact]
        public void The_warning_starts_at_500_affected_elements()
        {
            Assert.False(JkrFixBatch.MayPauseRevit(499));
            Assert.True(JkrFixBatch.MayPauseRevit(500));
        }
    }
}
