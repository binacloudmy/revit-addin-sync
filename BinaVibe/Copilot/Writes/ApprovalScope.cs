#nullable enable
// "Approve this build" (spec §7.5 approval modes; plan §5 W3 Task 3). Revit-free.
//
// Ask each change is the default and the only mode for edits. On the FIRST
// confirm card of a turn whose measured preview only adds new elements, the
// card may offer "Approve this build"; one tap then approves the remaining
// add-only batches of the SAME turn without a card. The add-in enforces the
// scope from each batch's own measured preview, so the cloud cannot widen it:
//   - same turn only (a new turn id ends it);
//   - only additions: no existing element modified, nothing deleted;
//   - previewed (an estimate-only batch never qualifies);
//   - at most 500 new elements in total in the turn.
// Anything outside the scope gets a normal card. Each covered batch still
// previews, verifies, gets a receipt and its own undo entry.

using System;

namespace BinaVibe.Copilot.Writes
{
    public sealed class ApprovalScope
    {
        public const int MaxAddsPerTurn = 500;

        /// <summary>The add-in's one scope; it only ever holds the current turn.</summary>
        public static ApprovalScope Shared { get; } = new ApprovalScope();

        private readonly object _gate = new();
        private string? _turnId;
        private bool _cardShown;
        private bool _granted;
        private int _added;

        /// <summary>The card for this batch may offer "Approve this build".</summary>
        public bool CanOffer(string turnId, PreviewFacts? facts, bool estimateOnly)
        {
            lock (_gate)
            {
                Track(turnId);
                return !_cardShown && !_granted && Qualifies(facts, estimateOnly, alreadyAdded: 0);
            }
        }

        /// <summary>A confirm card was shown in this turn (only the first may offer the scope).</summary>
        public void NoteCardShown(string turnId)
        {
            lock (_gate)
            {
                Track(turnId);
                _cardShown = true;
            }
        }

        /// <summary>The drafter tapped "Approve this build" on a card that offered it.</summary>
        public void Grant(string turnId)
        {
            lock (_gate)
            {
                Track(turnId);
                _granted = true;
            }
        }

        /// <summary>This batch is approved by the turn's build approval, without a card.</summary>
        public bool Covers(string turnId, PreviewFacts? facts, bool estimateOnly)
        {
            lock (_gate)
            {
                Track(turnId);
                return _granted && Qualifies(facts, estimateOnly, _added);
            }
        }

        /// <summary>Counts a committed batch's new elements against the turn's cap.</summary>
        public void Consume(string turnId, int added)
        {
            lock (_gate)
            {
                Track(turnId);
                _added += Math.Max(0, added);
            }
        }

        public bool IsGranted(string turnId)
        {
            lock (_gate) return _turnId == turnId && _granted;
        }

        /// <summary>turn_end: the scope ends with the turn.</summary>
        public void EndTurn(string turnId)
        {
            lock (_gate)
            {
                if (_turnId == turnId) Reset(null);
            }
        }

        private static bool Qualifies(PreviewFacts? facts, bool estimateOnly, int alreadyAdded) =>
            facts != null && !estimateOnly && facts.AddOnly && alreadyAdded + facts.Added <= MaxAddsPerTurn;

        private void Track(string turnId)
        {
            if (!string.Equals(_turnId, turnId, StringComparison.Ordinal)) Reset(turnId);
        }

        private void Reset(string? turnId)
        {
            _turnId = turnId;
            _cardShown = false;
            _granted = false;
            _added = 0;
        }
    }
}
