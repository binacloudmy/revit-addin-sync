#nullable enable
// The turn's Stop, as the running code sees it (AI-327). Revit-free.
//
// RevitThread runs a queued callback on Revit's thread with the CancellationToken it was queued with (the turn's). It enters this
// scope around the callback, and a LoopBudget created inside picks the token up: when the drafter presses Stop, the next loop step of
// the running code throws, the runner rolls back, and Revit's thread is free. Before this a Stop only unbound the turn: the code kept
// running on Revit's thread until its step or time budget ended.
//
// Only a loop back-edge (or a lambda entry) checks it. A single long API call, or straight-line code, cannot be interrupted: nothing
// can abort a call already inside Revit.
using System;
using System.Threading;

namespace BinaVibe.Copilot.Code
{
    public static class CodeCancel
    {
        [ThreadStatic] private static CancellationToken _current;

        /// <summary>The token of the callback running on this thread; None outside one.</summary>
        public static CancellationToken Current => _current;

        /// <summary>Make <paramref name="token"/> the current one until the scope is disposed (then the previous one returns).</summary>
        public static IDisposable Enter(CancellationToken token)
        {
            var previous = _current;
            _current = token;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly CancellationToken _previous;
            private bool _done;
            public Scope(CancellationToken previous) { _previous = previous; }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _current = _previous;
            }
        }
    }
}
