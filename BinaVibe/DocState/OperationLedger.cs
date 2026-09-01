// BinaVibe.DocState — per-idempotency-key execution ledger (spec §8.5, R1 Task 18).
//
// The backend stamps every mutate frame with an idempotency_key. This ledger
// is the add-in's evidence of what happened to each key, so that:
//   * a re-sent key that already STARTED or COMPLETED is never executed again
//     (the drainer answers from here — TryBegin says no and hands back the
//     cached result, or an "ambiguous" marker if it started but never finished);
//   * reconcile(keys) tells the backend, per key: completed(result) /
//     never_started / failed(error) / ambiguous — so a dropped connection is
//     resolved by evidence, not by guessed replay.
// Revit-free; bounded by entries and age. Process-local: if Revit restarted,
// the honest answer for an unknown key is never_started.

using System;
using System.Collections.Generic;

namespace BinaVibe.DocState
{
    public sealed class OperationLedger
    {
        public const int DefaultMaxEntries = 1000;
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(6);

        private enum State { Started, Completed, Failed }

        private sealed class Entry
        {
            public State State;
            public DateTime At;
            public Dictionary<string, object?>? Result;
            public string? Error;
        }

        private readonly object _lock = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new();
        private readonly int _maxEntries;
        private readonly TimeSpan _maxAge;

        public static OperationLedger Instance { get; } = new OperationLedger();

        public OperationLedger(int maxEntries = DefaultMaxEntries, TimeSpan? maxAge = null)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _maxAge = maxAge ?? DefaultMaxAge;
        }

        /// <summary>Ask permission to execute <paramref name="key"/> now.
        /// True = go (state becomes Started). False = do NOT execute; <paramref name="cached"/>
        /// is the completed result (with reconciled=true) or an ambiguous marker.
        /// An empty key is never tracked (legacy frames run as before).</summary>
        public bool TryBegin(string? key, DateTime now, out Dictionary<string, object?>? cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(key)) return true;
            lock (_lock)
            {
                Trim(now);
                if (_entries.TryGetValue(key!, out var e))
                {
                    switch (e.State)
                    {
                        case State.Completed:
                            cached = new Dictionary<string, object?>(e.Result ?? new Dictionary<string, object?>())
                            {
                                ["reconciled"] = true,
                                ["idempotency_key"] = key,
                            };
                            return false;
                        case State.Started:
                            cached = new Dictionary<string, object?>
                            {
                                ["ok"] = false,
                                ["status"] = "ambiguous",
                                ["error"] = "this operation already started and has not finished; reconcile before re-issuing",
                                ["idempotency_key"] = key,
                            };
                            return false;
                        case State.Failed:
                            _entries.Remove(key!); _order.Remove(key!);
                            break;
                    }
                }
                _entries[key!] = new Entry { State = State.Started, At = now };
                _order.AddLast(key!);
                Trim(now);
                return true;
            }
        }

        public void Complete(string? key, Dictionary<string, object?>? result)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(key!, out var e)) { e.State = State.Completed; e.Result = result; e.Error = null; }
            }
        }

        public void Fail(string? key, string error)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue(key!, out var e)) { e.State = State.Failed; e.Error = error; e.Result = null; }
            }
        }

        /// <summary>Per-key status for the backend's reconcile call.</summary>
        public Dictionary<string, Dictionary<string, object?>> Reconcile(IEnumerable<string> keys)
        {
            var outp = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            lock (_lock)
            {
                foreach (var key in keys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!_entries.TryGetValue(key, out var e))
                    {
                        outp[key] = new() { ["status"] = "never_started" };
                        continue;
                    }
                    outp[key] = e.State switch
                    {
                        State.Completed => new() { ["status"] = "completed", ["result"] = e.Result },
                        State.Failed => new() { ["status"] = "failed", ["error"] = e.Error },
                        _ => new() { ["status"] = "ambiguous" },
                    };
                }
            }
            return outp;
        }

        private void Trim(DateTime now)
        {
            while (_order.Count > _maxEntries)
            {
                var k = _order.First!.Value; _order.RemoveFirst(); _entries.Remove(k);
            }
            while (_order.First != null && _entries.TryGetValue(_order.First.Value, out var e) && now - e.At > _maxAge)
            {
                var k = _order.First.Value; _order.RemoveFirst(); _entries.Remove(k);
            }
        }
    }
}
