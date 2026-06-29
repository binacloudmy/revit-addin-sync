// McpIdempotencyCache — short-lived map of a backend idempotency_key → the
// McpJob that first handled it.
//
// Why: the backend already stamps every MUTATE with a stable key
// (tool+args+session, see app/agents/revit/copilot/tools.py:_mutate) so a RETRY of the
// same logical mutation can be deduped. Until now the addin threw the key away
// → a retry after a false-timeout re-ran the write and created a DUPLICATE
// element. This cache closes that hole: a repeated key returns the original
// job, so the caller awaits that one result instead of enqueueing a second
// Revit execution. In-flight or already-done, the original is reused.
//
// Bounded (max entries) and time-limited (TTL) so it can never grow without
// limit; expired/oldest entries are pruned on insert.

using System;
using System.Collections.Concurrent;

namespace BinaVibe.Mcp
{
    public sealed class McpIdempotencyCache
    {
        private sealed class Entry
        {
            public McpJob Job = null!;
            public long CreatedTicks;
        }

        private readonly ConcurrentDictionary<string, Entry> _map = new();
        private readonly double _ttlSeconds;
        private readonly int _max;

        public McpIdempotencyCache(TimeSpan? ttl = null, int max = 256)
        {
            _ttlSeconds = (ttl ?? TimeSpan.FromMinutes(10)).TotalSeconds;
            _max = max;
        }

        /// <summary>Returns the job ALREADY associated with <paramref name="key"/>
        /// — a duplicate the caller must NOT enqueue, only await — or
        /// <paramref name="job"/> itself when the key is new/expired (caller
        /// enqueues as normal). An empty/null key bypasses the cache (always
        /// returns <paramref name="job"/>).</summary>
        public McpJob GetOrAdd(string? key, McpJob job)
        {
            if (string.IsNullOrEmpty(key)) return job;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            Prune(now);
            var entry = _map.AddOrUpdate(
                key!,
                _ => new Entry { Job = job, CreatedTicks = now },
                (_, existing) => IsExpired(existing, now)
                    ? new Entry { Job = job, CreatedTicks = now }
                    : existing);
            return entry.Job;
        }

        private bool IsExpired(Entry e, long now)
        {
            double freq = System.Diagnostics.Stopwatch.Frequency;
            return (now - e.CreatedTicks) / freq > _ttlSeconds;
        }

        private void Prune(long now)
        {
            if (_map.Count < _max) return;
            foreach (var kv in _map)
                if (IsExpired(kv.Value, now))
                    _map.TryRemove(kv.Key, out _);
            // Still at the cap after dropping expired? Evict the oldest entry.
            if (_map.Count >= _max)
            {
                string? oldestKey = null;
                long oldest = long.MaxValue;
                foreach (var kv in _map)
                    if (kv.Value.CreatedTicks < oldest)
                    {
                        oldest = kv.Value.CreatedTicks;
                        oldestKey = kv.Key;
                    }
                if (oldestKey != null) _map.TryRemove(oldestKey, out _);
            }
        }
    }
}
