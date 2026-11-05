// FailureLog (W1 Task 2, spec §7.3.2): Revit failures raised during a guarded
// transaction are collected for the envelope's warnings[] before TxGuard's
// preprocessor deletes them. Revit-free.

using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FailureLogTests
    {
        [Fact]
        public void DedupesAndCaps()
        {
            using (FailureLog.Begin())
            {
                var log = FailureLog.Current;
                // Same definition + same ids in any order: one entry.
                log.Add("Walls overlap.", "warning", "def-overlap", new long[] { 7, 3 });
                log.Add("Walls overlap.", "warning", "def-overlap", new long[] { 3, 7, 3 });
                // Same definition, other ids: a separate entry.
                log.Add("Walls overlap.", "warning", "def-overlap", new long[] { 9 });
                for (int i = 0; i < 20; i++)
                    log.Add($"Room {i} is not enclosed.", "warning", $"def-room-{i}", new long[] { 100 + i });

                var snap = log.Snapshot();
                Assert.Equal(10, snap.Count);
                Assert.Equal("Walls overlap.", snap[0].Text);
                Assert.Equal(new long[] { 3, 7 }, snap[0].Ids);
                Assert.Equal(new long[] { 9 }, snap[1].Ids);
                Assert.Single(snap, w => w.DefinitionId == "def-overlap" && w.Ids.SequenceEqual(new long[] { 3, 7 }));
            }
        }

        [Fact]
        public void ErrorsAreKeptFirst_WhenTheCapCutsWarnings()
        {
            using (FailureLog.Begin())
            {
                for (int i = 0; i < 12; i++)
                    FailureLog.Current.Add($"warning {i}", "warning", $"w{i}", new long[] { i });
                FailureLog.Current.Add("Instance not cutting anything.", "error", "e1", new long[] { 50 });

                var snap = FailureLog.Current.Snapshot();
                Assert.Equal(10, snap.Count);
                Assert.Equal(WarningSeverity.Error, snap[0].Severity);
                Assert.Equal("Instance not cutting anything.", snap[0].Text);
                Assert.All(snap.Skip(1), w => Assert.Equal(WarningSeverity.Warning, w.Severity));
            }
        }

        [Fact]
        public void RepeatAsError_EscalatesTheEntry()
        {
            using (FailureLog.Begin())
            {
                FailureLog.Current.Add("Duplicate Mark.", "warning", "def-mark", new long[] { 1, 2 });
                FailureLog.Current.Add("Duplicate Mark.", "ERROR", "def-mark", new long[] { 2, 1 });
                var w = Assert.Single(FailureLog.Current.Snapshot());
                Assert.Equal(WarningSeverity.Error, w.Severity);
            }
        }

        [Fact]
        public void NoDefinitionId_DedupesByText()
        {
            using (FailureLog.Begin())
            {
                FailureLog.Current.Add("A", "warning", null, new long[] { 1 });
                FailureLog.Current.Add("B", "warning", null, new long[] { 1 });
                FailureLog.Current.Add("A", "warning", null, new long[] { 1 });
                Assert.Equal(new[] { "A", "B" }, FailureLog.Current.Snapshot().Select(w => w.Text));
            }
        }

        [Fact]
        public void OutsideAScope_NothingIsKept()
        {
            FailureLog.Current.Add("stray", "warning", "x", new long[] { 1 });
            Assert.Empty(FailureLog.Current.Snapshot());
            using (FailureLog.Begin())
                Assert.Empty(FailureLog.Current.Snapshot());
        }

        [Fact]
        public void Begin_StartsFresh_AndDisposeRestoresTheOuterScope()
        {
            using (FailureLog.Begin())
            {
                FailureLog.Current.Add("outer", "warning", "o", new long[] { 1 });
                var outer = FailureLog.Current;
                using (FailureLog.Begin())
                {
                    Assert.NotSame(outer, FailureLog.Current);
                    Assert.Empty(FailureLog.Current.Snapshot());
                    FailureLog.Current.Add("inner", "warning", "i", new long[] { 2 });
                }
                Assert.Same(outer, FailureLog.Current);
                Assert.Equal(new[] { "outer" }, FailureLog.Current.Snapshot().Select(w => w.Text));
            }
            Assert.Empty(FailureLog.Current.Snapshot());
        }

        [Fact]
        public void Snapshot_IsACopy_AndNormalisesInput()
        {
            using (FailureLog.Begin())
            {
                FailureLog.Current.Add("  Overlap.  ", "Warning", "d", null!);
                var first = FailureLog.Current.Snapshot();
                first[0].Ids.Add(99);
                var again = Assert.Single(FailureLog.Current.Snapshot());
                Assert.Equal("Overlap.", again.Text);
                Assert.Equal(WarningSeverity.Warning, again.Severity);
                Assert.Empty(again.Ids);
            }
        }
    }
}
