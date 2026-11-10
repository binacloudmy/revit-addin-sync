#nullable enable
// W3 Task 3 (spec §7.5 approval modes). "Approve this build" is offered only
// on the first card of a turn whose measured preview only adds, and then
// covers later add-only batches of the same turn, up to 500 new elements.
// Edits, deletions, estimate-only batches and other turns always get a card.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ApprovalScopeTests
    {
        private static PreviewFacts Adds(int n) => new PreviewFacts(n, 0, 0);

        [Fact]
        public void OffersOnlyOnTheFirstCardOfATurn()
        {
            var s = new ApprovalScope();
            Assert.True(s.CanOffer("t1", Adds(10), estimateOnly: false));
            s.NoteCardShown("t1");
            Assert.False(s.CanOffer("t1", Adds(10), estimateOnly: false));
        }

        [Fact]
        public void OffersOnlyForPreviewedAddOnlyBatchesWithinTheCap()
        {
            var s = new ApprovalScope();
            Assert.False(s.CanOffer("t1", new PreviewFacts(10, 1, 0), false));   // edits an existing element
            Assert.False(s.CanOffer("t1", new PreviewFacts(10, 0, 1), false));   // deletes
            Assert.False(s.CanOffer("t1", new PreviewFacts(0, 0, 0), false));    // adds nothing
            Assert.False(s.CanOffer("t1", Adds(10), estimateOnly: true));        // never measured
            Assert.False(s.CanOffer("t1", Adds(ApprovalScope.MaxAddsPerTurn + 1), false));
            Assert.False(s.CanOffer("t1", null, false));
            Assert.True(s.CanOffer("t1", Adds(ApprovalScope.MaxAddsPerTurn), false));
        }

        [Fact]
        public void CoversLaterAddOnlyBatchesOfTheSameTurnOnly()
        {
            var s = new ApprovalScope();
            Assert.False(s.Covers("t1", Adds(5), false));   // nothing granted yet
            s.NoteCardShown("t1");
            s.Grant("t1");
            s.Consume("t1", 5);

            Assert.True(s.Covers("t1", Adds(20), false));
            Assert.False(s.Covers("t2", Adds(20), false));   // another turn: scope gone
            Assert.False(s.Covers("t1", Adds(20), false));   // and it does not come back
        }

        [Fact]
        public void NeverCoversEditsDeletionsOrEstimates()
        {
            var s = new ApprovalScope();
            s.Grant("t1");
            Assert.False(s.Covers("t1", new PreviewFacts(3, 1, 0), false));
            Assert.False(s.Covers("t1", new PreviewFacts(3, 0, 1), false));
            Assert.False(s.Covers("t1", new PreviewFacts(0, 0, 0), false));
            Assert.False(s.Covers("t1", Adds(3), estimateOnly: true));
            Assert.True(s.Covers("t1", Adds(3), false));
        }

        [Fact]
        public void CapsTheTurnAtFiveHundredNewElements()
        {
            var s = new ApprovalScope();
            s.Grant("t1");
            s.Consume("t1", 400);
            Assert.True(s.Covers("t1", Adds(100), false));
            Assert.False(s.Covers("t1", Adds(101), false));
            s.Consume("t1", 100);
            Assert.False(s.Covers("t1", Adds(1), false));
        }

        [Fact]
        public void EndTurnEndsTheScope()
        {
            var s = new ApprovalScope();
            s.NoteCardShown("t1");
            s.Grant("t1");
            Assert.True(s.IsGranted("t1"));
            s.EndTurn("t1");
            Assert.False(s.IsGranted("t1"));
            Assert.False(s.Covers("t1", Adds(1), false));
            // A fresh turn with the same id starts over: its first card may offer again.
            Assert.True(s.CanOffer("t1", Adds(1), false));
        }
    }
}
