// Document revision + bounded change history (bina-ai spec §8.4, R1 Task 16).
//
// RevisionLedger is the Revit-free core behind DocumentRevisionTracker: one
// ledger per open document. It advances the revision EXACTLY ONCE per
// DocumentChanged event that touched at least one element, keeps a bounded
// ring of deltas, and answers changes_since with either a COMPLETE delta or
// reset_required — never a partial one.

using System;
using System.Linq;
using BinaVibe.DocState;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RevisionLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 29, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Fingerprint_IsStableForSameDocument_AndDiffersAcrossDocuments()
        {
            var a1 = RevisionLedger.ComputeFingerprint("uid-1", "C:/models/a.rvt", isWorkshared: false);
            var a2 = RevisionLedger.ComputeFingerprint("uid-1", "C:/models/a.rvt", isWorkshared: false);
            var b = RevisionLedger.ComputeFingerprint("uid-2", "C:/models/b.rvt", isWorkshared: false);
            var aCentral = RevisionLedger.ComputeFingerprint("uid-1", "//server/central/a.rvt", isWorkshared: true);
            Assert.Equal(a1, a2);
            Assert.NotEqual(a1, b);
            Assert.NotEqual(a1, aCentral);
            Assert.Equal(16, a1.Length);
        }

        [Fact]
        public void RelevantChange_AdvancesRevisionExactlyOnce()
        {
            var l = new RevisionLedger("fp");
            Assert.Equal(0, l.Revision);
            Assert.True(l.Record(new long[] { 10, 11 }, new long[] { 5 }, deletedCount: 0, now: T0));
            Assert.Equal(1, l.Revision);
            Assert.True(l.Record(Array.Empty<long>(), Array.Empty<long>(), deletedCount: 2, now: T0));
            Assert.Equal(2, l.Revision);
        }

        [Fact]
        public void EmptyEvent_DoesNotAdvance()
        {
            var l = new RevisionLedger("fp");
            Assert.False(l.Record(Array.Empty<long>(), Array.Empty<long>(), 0, T0));
            Assert.Equal(0, l.Revision);
        }

        [Fact]
        public void ChangesSince_ReturnsCompleteDeltaInsideWindow()
        {
            var l = new RevisionLedger("fp");
            l.Record(new long[] { 1 }, Array.Empty<long>(), 0, T0);                 // rev 1
            l.Record(new long[] { 2 }, new long[] { 1 }, 0, T0.AddSeconds(1));      // rev 2
            l.Record(Array.Empty<long>(), new long[] { 2 }, 1, T0.AddSeconds(2));   // rev 3
            var d = l.ChangesSince(1, T0.AddSeconds(3));
            Assert.False(d.ResetRequired);
            Assert.Equal(1, d.From); Assert.Equal(3, d.To);
            Assert.Equal(new long[] { 2 }, d.Added.OrderBy(x => x));
            // element 2 was added inside the window, so it reports as added only —
            // the delta never lists the same id as both added and modified
            Assert.Equal(new long[] { 1 }, d.Modified.OrderBy(x => x));
            Assert.Equal(1, d.Deleted);
        }

        [Fact]
        public void ChangesSince_AtCurrentRevision_IsEmptyNotReset()
        {
            var l = new RevisionLedger("fp");
            l.Record(new long[] { 1 }, Array.Empty<long>(), 0, T0);
            var d = l.ChangesSince(1, T0);
            Assert.False(d.ResetRequired);
            Assert.Empty(d.Added); Assert.Empty(d.Modified); Assert.Equal(0, d.Deleted);
        }

        [Fact]
        public void ChangesSince_BeyondEntryWindow_ReturnsResetRequired_NoPartialDelta()
        {
            var l = new RevisionLedger("fp", maxEntries: 4, maxAge: TimeSpan.FromMinutes(15));
            for (int i = 1; i <= 6; i++) l.Record(new long[] { i }, Array.Empty<long>(), 0, T0.AddSeconds(i));
            var d = l.ChangesSince(1, T0.AddSeconds(7));   // revs 2..6 needed; only 3..6 retained
            Assert.True(d.ResetRequired);
            Assert.Empty(d.Added); Assert.Empty(d.Modified); Assert.Equal(0, d.Deleted);
            Assert.Equal(6, d.To);
            Assert.False(l.ChangesSince(2, T0.AddSeconds(7)).ResetRequired);
        }

        [Fact]
        public void ChangesSince_BeyondAgeWindow_ReturnsResetRequired()
        {
            var l = new RevisionLedger("fp", maxEntries: 256, maxAge: TimeSpan.FromMinutes(15));
            l.Record(new long[] { 1 }, Array.Empty<long>(), 0, T0);
            l.Record(new long[] { 2 }, Array.Empty<long>(), 0, T0.AddMinutes(20));
            Assert.True(l.ChangesSince(0, T0.AddMinutes(21)).ResetRequired);
            Assert.False(l.ChangesSince(1, T0.AddMinutes(21)).ResetRequired);
        }

        [Fact]
        public void ChangesSince_FutureRevision_IsResetRequired()
        {
            var l = new RevisionLedger("fp");
            Assert.True(l.ChangesSince(5, T0).ResetRequired);
        }

        [Fact]
        public void StaleCheck_MismatchProducesTypedError_WithoutTouchingRevision()
        {
            var l = new RevisionLedger("fp");
            l.Record(new long[] { 1 }, Array.Empty<long>(), 0, T0);   // rev 1
            Assert.Null(l.StaleError(expectedRevision: 1, expectedFingerprint: "fp"));
            var err = l.StaleError(expectedRevision: 0, expectedFingerprint: "fp");
            Assert.NotNull(err);
            Assert.Equal("stale_document", err!["status"]);
            Assert.Equal(false, err["ok"]);
            Assert.Equal(0L, err["expected_revision"]);
            Assert.Equal(1L, err["actual_revision"]);
            Assert.Equal(1, l.Revision);
            // a frame planned against ANOTHER document is stale too
            Assert.NotNull(l.StaleError(expectedRevision: 1, expectedFingerprint: "other"));
        }

        [Fact]
        public void PendingToolCall_ParsesRevisionFields_AndDefaultsToNull()
        {
            var v2 = System.Text.Json.JsonSerializer.Deserialize<PendingToolCall>(
                "{\"tool_call_id\":\"c1\",\"tool\":\"create_wall\",\"args\":{},\"mutate\":true," +
                "\"expected_revision\":12,\"document_fingerprint\":\"fp\"}")!;
            Assert.Equal(12L, v2.ExpectedRevision);
            Assert.Equal("fp", v2.DocumentFingerprint);
            var legacy = System.Text.Json.JsonSerializer.Deserialize<PendingToolCall>(
                "{\"tool_call_id\":\"c1\",\"tool\":\"create_wall\",\"args\":{},\"mutate\":true}")!;
            Assert.Null(legacy.ExpectedRevision);
            Assert.Null(legacy.DocumentFingerprint);
        }
    }
}
