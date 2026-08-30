// BinaVibe.DocState — document identity + revision, Revit-free core.
//
// Spec: bina-ai docs/superpowers/specs/2026-08-14-control-plane-rewrite-design.md §8.3/§8.4.
//
// One ledger per open document. The Revit adapter (DocumentRevisionTracker)
// feeds it DocumentChanged events; everything else — the monotonic revision,
// the bounded change history, changes_since, the stale check — lives here so
// it is unit-testable without a Revit process.
//
// Rules (§8.4):
//   * the revision advances EXACTLY ONCE per DocumentChanged event that touched
//     at least one element (added / modified / deleted). An event with no ids
//     (pure UI) does not advance it;
//   * changes_since(from) returns a COMPLETE delta or reset_required — never a
//     partial one. History is bounded by entries AND age (defaults 256 / 15 min);
//   * a mutation frame carries expected_revision + document_fingerprint; the
//     stale check runs BEFORE any transaction is opened.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BinaVibe.DocState
{
    public sealed class ChangesResult
    {
        public long From { get; init; }
        public long To { get; init; }
        public IReadOnlyList<long> Added { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> Modified { get; init; } = Array.Empty<long>();
        public int Deleted { get; init; }
        public bool ResetRequired { get; init; }

        public Dictionary<string, object?> ToDictionary(string fingerprint) => new()
        {
            ["ok"] = !ResetRequired,
            ["status"] = ResetRequired ? "reset_required" : "ok",
            ["document_fingerprint"] = fingerprint,
            ["document_revision"] = To,
            ["from"] = From,
            ["to"] = To,
            ["added"] = Added.ToList(),
            ["modified"] = Modified.ToList(),
            ["deleted"] = Deleted,
            ["reset_required"] = ResetRequired,
            ["error"] = ResetRequired
                ? $"change history for revision {From} has expired — re-inspect instead of trusting a partial delta"
                : null,
        };
    }

    public sealed class RevisionLedger
    {
        public const int DefaultMaxEntries = 256;
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(15);

        private sealed class Entry
        {
            public long Revision;
            public DateTime At;
            public long[] Added = Array.Empty<long>();
            public long[] Modified = Array.Empty<long>();
            public int Deleted;
        }

        private readonly object _lock = new();
        private readonly LinkedList<Entry> _history = new();
        private readonly int _maxEntries;
        private readonly TimeSpan _maxAge;

        public string Fingerprint { get; }
        public long Revision { get; private set; }

        public RevisionLedger(string fingerprint, int maxEntries = DefaultMaxEntries, TimeSpan? maxAge = null)
        {
            Fingerprint = fingerprint ?? "";
            _maxEntries = Math.Max(1, maxEntries);
            _maxAge = maxAge ?? DefaultMaxAge;
        }

        /// <summary>Stable identity for a document: project UniqueId + the path
        /// that names it (central path when workshared). 16 hex chars.</summary>
        public static string ComputeFingerprint(string? projectUniqueId, string? path, bool isWorkshared)
        {
            var material = (projectUniqueId ?? "") + "|" + (isWorkshared ? "central:" : "local:") + (path ?? "");
            using var sha = SHA256.Create();
            var hex = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(material)))
                .Replace("-", "").ToLowerInvariant();
            return hex.Substring(0, 16);
        }

        /// <summary>Record one DocumentChanged event. Returns true when the
        /// revision advanced (at least one id touched), false for an empty event.</summary>
        public bool Record(IReadOnlyCollection<long> added, IReadOnlyCollection<long> modified, int deletedCount, DateTime now)
        {
            if ((added?.Count ?? 0) == 0 && (modified?.Count ?? 0) == 0 && deletedCount <= 0)
                return false;
            lock (_lock)
            {
                Revision++;
                _history.AddLast(new Entry
                {
                    Revision = Revision,
                    At = now,
                    Added = added?.ToArray() ?? Array.Empty<long>(),
                    Modified = modified?.ToArray() ?? Array.Empty<long>(),
                    Deleted = Math.Max(0, deletedCount),
                });
                Trim(now);
                return true;
            }
        }

        private void Trim(DateTime now)
        {
            while (_history.Count > _maxEntries) _history.RemoveFirst();
            while (_history.First != null && now - _history.First.Value.At > _maxAge) _history.RemoveFirst();
        }

        /// <summary>Delta from <paramref name="fromRevision"/> (exclusive) to now.
        /// Complete or reset_required — never partial.</summary>
        public ChangesResult ChangesSince(long fromRevision, DateTime now)
        {
            lock (_lock)
            {
                Trim(now);
                if (fromRevision > Revision || fromRevision < 0)
                    return new ChangesResult { From = fromRevision, To = Revision, ResetRequired = true };
                if (fromRevision == Revision)
                    return new ChangesResult { From = fromRevision, To = Revision };

                // Every revision in (from, Revision] must still be retained.
                var oldestRetained = _history.First?.Value.Revision ?? (Revision + 1);
                if (oldestRetained > fromRevision + 1)
                    return new ChangesResult { From = fromRevision, To = Revision, ResetRequired = true };

                var added = new HashSet<long>();
                var modified = new HashSet<long>();
                int deleted = 0;
                foreach (var e in _history)
                {
                    if (e.Revision <= fromRevision) continue;
                    foreach (var id in e.Added) added.Add(id);
                    foreach (var id in e.Modified) if (!added.Contains(id)) modified.Add(id);
                    deleted += e.Deleted;
                }
                return new ChangesResult
                {
                    From = fromRevision,
                    To = Revision,
                    Added = added.ToList(),
                    Modified = modified.ToList(),
                    Deleted = deleted,
                };
            }
        }

        /// <summary>Typed stale_document error when a frame's expectation does
        /// not match this document/revision; null when the frame may proceed.
        /// Pure read — never advances the revision.</summary>
        public Dictionary<string, object?>? StaleError(long expectedRevision, string? expectedFingerprint)
        {
            lock (_lock)
            {
                var sameDoc = string.IsNullOrEmpty(expectedFingerprint) ||
                              string.Equals(expectedFingerprint, Fingerprint, StringComparison.Ordinal);
                if (sameDoc && expectedRevision == Revision) return null;
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["status"] = "stale_document",
                    ["error"] = sameDoc
                        ? $"stale document: planned against revision {expectedRevision}, model is at {Revision}"
                        : "stale document: planned against a different document",
                    ["expected_revision"] = expectedRevision,
                    ["actual_revision"] = Revision,
                    ["expected_fingerprint"] = expectedFingerprint,
                    ["document_fingerprint"] = Fingerprint,
                    ["document_revision"] = Revision,
                };
            }
        }
    }
}
