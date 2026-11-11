#nullable enable
// The Home "in view" card's last read, kept per document and view so a burst of redraws costs one pass over the view
// on Revit's thread, and so a view near the time budget does not make the card come and go: a read that ran out of
// time never replaces a good one, and is itself kept longer than a good read (a retry every few seconds would freeze
// Revit's thread each time). Pure, so a test pins the policy.
using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class SummaryCache
    {
        public TimeSpan Fresh { get; }
        public TimeSpan Incomplete { get; }

        private string? _key;
        private DateTime _at = DateTime.MinValue;
        private ViewSummary? _value;
        private bool _good;
        private bool _lastIncomplete;

        public SummaryCache(TimeSpan? fresh = null, TimeSpan? incomplete = null)
        {
            Fresh = fresh ?? TimeSpan.FromSeconds(10);
            Incomplete = incomplete ?? TimeSpan.FromSeconds(60);
        }

        /// <summary>True when the stored read for this key is still current; <paramref name="value"/> is what to show.</summary>
        public bool TryGet(string key, DateTime now, out ViewSummary? value)
        {
            value = null;
            if (_key != key) return false;
            var keep = _lastIncomplete ? Incomplete : Fresh;
            if (now - _at >= keep) return false;
            value = _value;
            return true;
        }

        /// <summary>The value to show now for this key, even when stale (what is on screen while a re-read is pending).</summary>
        public ViewSummary? Last(string key) => _key == key ? _value : null;

        /// <summary>Store a read. <paramref name="complete"/> is false when it ran out of time or failed: then a good value
        /// already held for the same key stays, and only the time moves on.</summary>
        public ViewSummary? Put(string key, DateTime now, ViewSummary? read, bool complete)
        {
            if (complete)
            {
                _key = key; _at = now; _value = read; _good = true; _lastIncomplete = false;
                return read;
            }
            if (_key == key && _good) { _at = now; _lastIncomplete = true; return _value; }
            _key = key; _at = now; _value = null; _good = false; _lastIncomplete = true;
            return null;
        }
    }
}
