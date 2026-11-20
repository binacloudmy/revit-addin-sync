using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-427: a row is Resolved only because the rule re-passed, never because we
    /// wrote it. JkrRecheck.Reconcile is the Revit-free rule behind the one
    /// write → re-check mechanism (after a write, and on Ctrl+Z / Ctrl+Y).
    /// </summary>
    public class JkrRecheckTests
    {
        private sealed class Row : IRecheckRow
        {
            public string Id { get; set; }
            public long RevitElementId { get; set; }
            public RecheckState RecheckState { get; set; }
            public bool FixVerified { get; set; }
        }

        private const RecheckState Open = RecheckState.Open, Fixed = RecheckState.Fixed, Other = RecheckState.Other;

        private static Row R(string id, long element, RecheckState state = RecheckState.Open)
            => new Row { Id = id, RevitElementId = element, RecheckState = state };

        [Fact]
        public void Undo_of_a_fix_reopens_the_resolved_row_with_the_fresh_one()
        {
            var fixedRow = R("a", 10, Fixed);
            fixedRow.FixVerified = true;
            var fresh = R("a", 10);

            var o = JkrRecheck.Reconcile(new[] { fixedRow }, new long[] { 10 }, new[] { fresh }, true);

            Assert.Single(o.Issues);
            Assert.Same(fresh, o.Issues[0]);
            Assert.Equal(Open, o.Issues[0].RecheckState);
            Assert.Equal(new[] { "a" }, o.Reopened.Select(i => i.Id));
        }

        [Fact]
        public void Redo_that_makes_the_rule_pass_drops_the_open_row()
        {
            var open = R("a", 10);

            var o = JkrRecheck.Reconcile(new[] { open }, new long[] { 10 }, new List<Row>(), true);

            Assert.Empty(o.Issues);
            Assert.Equal(new[] { "a" }, o.Passed.Select(i => i.Id));
        }

        [Fact]
        public void Fixed_row_that_still_passes_stays_resolved_and_is_verified()
        {
            var fixedRow = R("a", 10, Fixed);

            var o = JkrRecheck.Reconcile(new[] { fixedRow }, new long[] { 10 }, new List<Row>(), true);

            Assert.Same(fixedRow, o.Issues.Single());
            Assert.True(fixedRow.FixVerified);
        }

        [Fact]
        public void An_audit_reinjected_Fixed_row_in_the_fresh_list_is_a_pass_not_a_failure()
        {
            var fixedRow = R("a", 10, Fixed);
            var reinjected = R("a", 10, Fixed);

            var o = JkrRecheck.Reconcile(new[] { fixedRow }, new long[] { 10 }, new[] { reinjected }, true);

            Assert.Same(fixedRow, o.Issues.Single());
            Assert.Empty(o.Reopened);
        }

        [Fact]
        public void Partial_scan_proves_nothing_about_passing_but_still_reopens_a_failure()
        {
            var fixedPass = R("a", 10, Fixed);
            var fixedFail = R("b", 11, Fixed);
            var open = R("c", 12);
            var freshB = R("b", 11);

            var o = JkrRecheck.Reconcile(new[] { fixedPass, fixedFail, open }, new long[] { 10, 11, 12 }, new[] { freshB }, false);

            Assert.Contains(fixedPass, o.Issues);
            Assert.False(fixedPass.FixVerified);
            Assert.Contains(open, o.Issues);          // not dropped: absence proves nothing
            Assert.Contains(freshB, o.Issues);
            Assert.DoesNotContain(fixedFail, o.Issues);
        }

        [Fact]
        public void Rows_on_other_elements_are_untouched()
        {
            var other = R("x", 99, Fixed);
            var otherOpen = R("y", 98);

            var o = JkrRecheck.Reconcile(new[] { other, otherOpen }, new long[] { 10 }, new[] { R("x", 99) }, true);

            Assert.Equal(new[] { other, otherOpen }, o.Issues);
        }

        [Fact]
        public void A_failure_that_is_new_on_an_affected_element_is_added()
        {
            var fresh = R("n", 10);
            var o = JkrRecheck.Reconcile(new List<Row>(), new long[] { 10 }, new[] { fresh }, true);
            Assert.Same(fresh, o.Issues.Single());
        }

        [Fact]
        public void Ignored_and_manual_rows_keep_the_users_decision()
        {
            var ign = R("i", 10, Other);
            var man = R("m", 10, Other);

            var o = JkrRecheck.Reconcile(new[] { ign, man }, new long[] { 10 },
                new[] { R("i", 10, Other), R("m", 10, Other) }, true);

            Assert.Equal(new[] { ign, man }, o.Issues);
        }

        [Fact]
        public void Just_written_row_that_passes_is_reported_verified_and_left_for_the_caller_to_resolve()
        {
            var written = R("a", 10);

            var o = JkrRecheck.Reconcile(new[] { written }, new long[] { 10 }, new List<Row>(), true, new[] { written });

            Assert.Same(written, o.Issues.Single());
            Assert.Equal(Open, written.RecheckState);
            Assert.Equal(new[] { written }, o.Verified);
            Assert.Empty(o.StillFailing);
        }

        [Fact]
        public void Just_written_row_that_still_fails_is_not_resolved()
        {
            var written = R("a", 10);
            var fresh = R("a", 10);

            var o = JkrRecheck.Reconcile(new[] { written }, new long[] { 10 }, new[] { fresh }, true, new[] { written });

            Assert.Equal(new[] { written }, o.StillFailing);
            Assert.Empty(o.Verified);
            Assert.Same(written, o.Issues.Single());
        }

        [Fact]
        public void Just_written_row_on_a_partial_scan_is_neither_verified_nor_failing()
        {
            var written = R("a", 10);

            var o = JkrRecheck.Reconcile(new[] { written }, new long[] { 10 }, new List<Row>(), false, new[] { written });

            Assert.Empty(o.Verified);
            Assert.Empty(o.StillFailing);
            Assert.Same(written, o.Issues.Single());
        }
    }
}
