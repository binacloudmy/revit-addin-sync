#nullable enable
// "Fire once, a moment after the last trigger." Pure: the timer is handed in, so a test drives it by hand. The pane's Home
// uses it for Revit's ViewActivated / SelectionChanged storms (a click-drag selection raises dozens): one refresh, 300 ms after the last.
using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class Debounce
    {
        private readonly TimeSpan _delay;
        private readonly Action _fire;
        private readonly Func<TimeSpan, Action, IDisposable> _schedule;
        private IDisposable? _pending;
        private int _generation;
        private readonly object _gate = new object();

        /// <param name="schedule">Runs the action once after the delay and returns a handle that cancels it.</param>
        public Debounce(TimeSpan delay, Action fire, Func<TimeSpan, Action, IDisposable> schedule)
        {
            _delay = delay;
            _fire = fire ?? throw new ArgumentNullException(nameof(fire));
            _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        }

        /// <summary>True while a fire is waiting.</summary>
        public bool IsPending { get { lock (_gate) return _pending != null; } }

        /// <summary>Another trigger: the wait starts again from now.</summary>
        public void Trigger()
        {
            lock (_gate)
            {
                _pending?.Dispose();
                var mine = ++_generation;
                _pending = _schedule(_delay, () => Elapsed(mine));
            }
        }

        /// <summary>Drop a waiting fire (the view is gone).</summary>
        public void Cancel()
        {
            lock (_gate) { _generation++; _pending?.Dispose(); _pending = null; }
        }

        private void Elapsed(int generation)
        {
            lock (_gate)
            {
                if (generation != _generation) return;   // a newer trigger or a cancel came first
                _pending = null;
            }
            _fire();
        }
    }
}
