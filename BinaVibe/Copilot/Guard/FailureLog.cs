#nullable enable
// Stub created by W0; body owned by W1
// Revit-free. Collects Revit failures (warnings and errors) raised during a
// guarded transaction so envelopes can report them instead of only deleting
// them. Snapshot() dedupes by (definitionId, sorted ids) and caps at 10.
//
// Who writes: TxGuard's SwallowWarnings preprocessor and FailureScope's
// FailuresProcessing backstop, both on Revit's UI thread, into Current.
// Who reads: whoever opened the scope with Begin() (a tool call, a batch),
// via Snapshot() -> envelope warnings[]. Outside any scope Current is an
// inert log, so a stray failure is dropped rather than leaking into the next
// call's envelope.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Guard
{
    public sealed class FailureLog
    {
        /// <summary>Most entries a snapshot returns; envelopes feed the model's context.</summary>
        public const int Cap = 10;

        [ThreadStatic] private static FailureLog? _current;

        private static readonly FailureLog Inert = new FailureLog(inert: true);

        private readonly bool _inert;
        private readonly object _lock = new object();
        // Distinct entries in first-seen order. Never more than Cap of each
        // severity: Snapshot keeps errors first, so more could never show.
        private readonly List<Entry> _entries = new List<Entry>();

        private FailureLog(bool inert) { _inert = inert; }

        /// <summary>The thread-static log of the current scope.</summary>
        public static FailureLog Current => _current ?? Inert;

        /// <summary>Starts a new <see cref="Current"/> for a scope; dispose to end it.</summary>
        public static IDisposable Begin()
        {
            var scope = new Scope(_current, new FailureLog(inert: false));
            _current = scope.Log;
            return scope;
        }

        public void Add(string text, string severity, string? definitionId, IEnumerable<long> ids)
        {
            if (_inert) return;
            var cleanText = (text ?? "").Trim();
            if (cleanText.Length == 0) cleanText = "Revit reported a failure with no description.";
            var cleanSeverity = string.Equals(severity?.Trim(), WarningSeverity.Warning, StringComparison.OrdinalIgnoreCase)
                ? WarningSeverity.Warning
                : WarningSeverity.Error;   // unknown severities are never understated
            var cleanDefinition = string.IsNullOrWhiteSpace(definitionId) ? null : definitionId!.Trim();
            var sortedIds = (ids ?? Enumerable.Empty<long>()).Distinct().OrderBy(i => i).ToList();
            // Without a definition id, the text tells failures apart.
            var key = (cleanDefinition ?? "text:" + cleanText) + "|" + string.Join(",", sortedIds);

            lock (_lock)
            {
                var existing = _entries.FirstOrDefault(e => e.Key == key);
                if (existing != null)
                {
                    if (cleanSeverity == WarningSeverity.Error && existing.Severity != WarningSeverity.Error
                        && CountOf(WarningSeverity.Error) < Cap)
                        existing.Severity = WarningSeverity.Error;
                    return;
                }
                if (CountOf(cleanSeverity) >= Cap) return;
                _entries.Add(new Entry(key, cleanText, cleanSeverity, cleanDefinition, sortedIds));
            }
        }

        /// <summary>Errors first, then warnings, each in first-seen order; at most <see cref="Cap"/>.</summary>
        public IReadOnlyList<WarningDto> Snapshot()
        {
            lock (_lock)
            {
                return _entries.Where(e => e.Severity == WarningSeverity.Error)
                    .Concat(_entries.Where(e => e.Severity != WarningSeverity.Error))
                    .Take(Cap)
                    .Select(e => new WarningDto
                    {
                        Text = e.Text,
                        Severity = e.Severity,
                        DefinitionId = e.DefinitionId,
                        Ids = new List<long>(e.Ids),
                    })
                    .ToList();
            }
        }

        private int CountOf(string severity) => _entries.Count(e => e.Severity == severity);

        private sealed class Entry
        {
            public Entry(string key, string text, string severity, string? definitionId, List<long> ids)
            {
                Key = key; Text = text; Severity = severity; DefinitionId = definitionId; Ids = ids;
            }

            public string Key { get; }
            public string Text { get; }
            public string Severity { get; set; }
            public string? DefinitionId { get; }
            public List<long> Ids { get; }
        }

        private sealed class Scope : IDisposable
        {
            private readonly FailureLog? _outer;
            private bool _disposed;

            public Scope(FailureLog? outer, FailureLog log) { _outer = outer; Log = log; }

            public FailureLog Log { get; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                // Out-of-order dispose leaves a newer scope in place.
                if (ReferenceEquals(_current, Log)) _current = _outer;
            }
        }
    }
}
