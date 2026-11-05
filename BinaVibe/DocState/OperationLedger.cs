#nullable enable
// BinaVibe.DocState — per-idempotency-key execution ledger (spec §8.5, R1 Task 18;
// lean harness spec §7.4, Review Focus 5).
//
// The backend stamps every write call with an idempotency_key. This ledger is
// the add-in's evidence of what happened to each key, so that:
//   * a re-sent key that already STARTED or COMPLETED is never executed again
//     (TryBegin says no and hands back the cached result, or an "ambiguous"
//     answer if it started but never finished);
//   * reconcile(keys) tells the backend, per key: completed(result) /
//     never_started / failed(error) / ambiguous — so a dropped connection is
//     resolved by evidence, not by guessed replay.
//
// Keyed by (idempotency_key, phase). Only the COMMIT phase is recorded and
// reconciled: a preview changes nothing, may run again, and can never satisfy
// (or block) the commit of the same key. Protocol-3 writes use the envelope
// overloads (TryBegin/Complete/Fail with a WritePhase). The older MCP drainer
// commits directly, so its dictionary overloads are commit-phase records too;
// the two read each other's records through LegacyResultAdapter.
//
// Revit-free; bounded by entries and age. Process-local: if Revit restarted,
// the honest answer for an unknown key is never_started.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;

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
            public string? EnvelopeJson;   // stored serialised: every read hands out a fresh copy
            public string? Error;
        }

        private readonly object _lock = new();
        private readonly Dictionary<(string Key, WritePhase Phase), Entry> _entries = new();
        private readonly LinkedList<(string Key, WritePhase Phase)> _order = new();
        private readonly int _maxEntries;
        private readonly TimeSpan _maxAge;

        public static OperationLedger Instance { get; } = new OperationLedger();

        public OperationLedger(int maxEntries = DefaultMaxEntries, TimeSpan? maxAge = null)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _maxAge = maxAge ?? DefaultMaxAge;
        }

        // ---- protocol 3: envelopes, by phase -----------------------------------

        /// <summary>Ask permission to run <paramref name="key"/> in <paramref name="phase"/> now.
        /// True = go. A preview always goes and is never recorded. A commit becomes
        /// Started; false = do NOT run it again: <paramref name="cached"/> is the
        /// envelope it committed, or a failed envelope when it started and never
        /// finished. An empty key is never tracked.</summary>
        public bool TryBegin(string? key, WritePhase phase, DateTime now, out Envelope? cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(key) || phase != WritePhase.Commit) return true;
            lock (_lock)
            {
                var e = Begin((key!, WritePhase.Commit), now);
                if (e == null) return true;
                cached = e.State == State.Completed ? CachedEnvelope(e) ?? UnrecordedEnvelope() : AmbiguousEnvelope();
                return false;
            }
        }

        /// <summary>Records the committed envelope for a commit-phase key; a preview is ignored.</summary>
        public void Complete(string? key, WritePhase phase, Envelope envelope)
        {
            if (string.IsNullOrEmpty(key) || phase != WritePhase.Commit) return;
            lock (_lock)
            {
                if (_entries.TryGetValue((key!, WritePhase.Commit), out var e))
                {
                    e.State = State.Completed;
                    e.EnvelopeJson = envelope == null ? null : CopilotJson.Serialize(envelope);
                    e.Result = null;
                    e.Error = null;
                }
            }
        }

        /// <summary>Records a failed commit (it may run again); a preview is ignored.</summary>
        public void Fail(string? key, WritePhase phase, string error)
        {
            if (string.IsNullOrEmpty(key) || phase != WritePhase.Commit) return;
            Fail(key, error);
        }

        // ---- legacy MCP drainer: dictionaries, commit phase ---------------------

        /// <summary>Ask permission to execute <paramref name="key"/> now (commit phase).
        /// True = go (state becomes Started). False = do NOT execute; <paramref name="cached"/>
        /// is the completed result (with reconciled=true) or an ambiguous marker.
        /// An empty key is never tracked (legacy frames run as before).</summary>
        public bool TryBegin(string? key, DateTime now, out Dictionary<string, object?>? cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(key)) return true;
            lock (_lock)
            {
                var e = Begin((key!, WritePhase.Commit), now);
                if (e == null) return true;
                if (e.State == State.Completed)
                {
                    if (e.Result != null) cached = new Dictionary<string, object?>(e.Result);
                    else
                    {
                        var envelope = CachedEnvelope(e) ?? UnrecordedEnvelope();
                        cached = new Dictionary<string, object?> { ["ok"] = envelope.Ok, ["envelope"] = envelope };
                    }
                    cached["reconciled"] = true;
                    cached["idempotency_key"] = key;
                }
                else
                {
                    cached = new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["status"] = "ambiguous",
                        ["error"] = "this operation already started and has not finished; reconcile before re-issuing",
                        ["idempotency_key"] = key,
                    };
                }
                return false;
            }
        }

        public void Complete(string? key, Dictionary<string, object?>? result)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue((key!, WritePhase.Commit), out var e))
                {
                    e.State = State.Completed; e.Result = result; e.EnvelopeJson = null; e.Error = null;
                }
            }
        }

        public void Fail(string? key, string error)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (_entries.TryGetValue((key!, WritePhase.Commit), out var e))
                {
                    e.State = State.Failed; e.Error = error; e.Result = null; e.EnvelopeJson = null;
                }
            }
        }

        /// <summary>Per-key commit status for the backend's reconcile call. A key that
        /// only ever previewed is never_started: nothing was committed.</summary>
        public Dictionary<string, Dictionary<string, object?>> Reconcile(IEnumerable<string> keys)
        {
            var outp = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            lock (_lock)
            {
                foreach (var key in keys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!_entries.TryGetValue((key, WritePhase.Commit), out var e))
                    {
                        outp[key] = new() { ["status"] = "never_started" };
                        continue;
                    }
                    outp[key] = e.State switch
                    {
                        State.Completed => new() { ["status"] = "completed", ["result"] = (object?)e.Result ?? CachedEnvelope(e) },
                        State.Failed => new() { ["status"] = "failed", ["error"] = e.Error },
                        _ => new() { ["status"] = "ambiguous" },
                    };
                }
            }
            return outp;
        }

        // Caller holds _lock. Returns the blocking entry (Started or Completed), or
        // null after recording a fresh Started entry (a Failed one is replaced).
        private Entry? Begin((string Key, WritePhase Phase) id, DateTime now)
        {
            Trim(now);
            if (_entries.TryGetValue(id, out var e))
            {
                if (e.State != State.Failed) return e;
                _entries.Remove(id); _order.Remove(id);
            }
            _entries[id] = new Entry { State = State.Started, At = now };
            _order.AddLast(id);
            Trim(now);
            return null;
        }

        private static Envelope? CachedEnvelope(Entry e)
        {
            if (e.EnvelopeJson != null) return CopilotJson.Deserialize<Envelope>(e.EnvelopeJson);
            return e.Result != null ? LegacyResultAdapter.ToEnvelope(e.Result, WritePhase.Commit) : null;
        }

        private static Envelope AmbiguousEnvelope() => EnvelopeBuilder.Fail(ErrorCodes.RuntimeError,
            "This change already started and did not finish, so it may or may not have been applied. Inspect the model before retrying.",
            phase: WritePhase.Commit);

        private static Envelope UnrecordedEnvelope() => EnvelopeBuilder.Fail(ErrorCodes.RuntimeError,
            "This change was already committed, but its result was not recorded. Inspect the model before retrying.",
            phase: WritePhase.Commit);

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
