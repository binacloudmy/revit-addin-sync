#nullable enable
// "Full privilege", the safe version (tester feedback 20 Nov, item 3). Revit-free.
//
// Every Copilot transaction already runs with SwallowWarnings: warnings are deleted (never a popup) and reported in the
// envelope; errors roll the change back with Revit's own words. Inside a ForceScope (open only while a batch the drafter
// FORCED with a click is previewed or committed) an ERROR is resolved instead when, and only when:
//   - Revit offers a resolution for it and permits its default resolution (FailureMessageAccessor.HasResolutions,
//     FailuresAccessor.IsFailureResolutionPermitted), and
//   - every element the error names is a target of the batch or connected to one (ConnectedSet), and
//   - fewer than MaxRounds resolution rounds ran (a resolution that raises the same error again cannot loop).
// Each resolution is recorded (Resolved) and lands in the envelope as a warning. Anything else still rolls back with the
// exact reason. What this can never do: answer an error that has no resolution (the drafter must decide: "Can't keep
// elements joined" with no fix, a wall below its minimum height), touch elements outside the connected set, or save,
// delete or close anything through a dialog (DialogPolicy).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Guard
{
    public sealed class ForceScope : IDisposable
    {
        public const int MaxRounds = 3;

        [ThreadStatic] private static ForceScope? _current;

        private readonly ForceScope? _outer;
        private readonly HashSet<long> _allowed;
        private readonly List<string> _resolved = new();
        private bool _disposed;

        private ForceScope(ForceScope? outer, IEnumerable<long> allowed)
        {
            _outer = outer;
            _allowed = new HashSet<long>(allowed ?? Enumerable.Empty<long>());
        }

        /// <summary>The scope of the forced batch running on this thread; null outside one.</summary>
        public static ForceScope? Current => _current;

        public static ForceScope Begin(IEnumerable<long> allowedIds)
        {
            var scope = new ForceScope(_current, allowedIds);
            _current = scope;
            return scope;
        }

        public int Rounds { get; private set; }

        /// <summary>What was resolved, in order ("text — resolution caption").</summary>
        public IReadOnlyList<string> Resolved => _resolved;

        /// <summary>May an error naming <paramref name="ids"/> be resolved with Revit's default resolution here?
        /// An error that names no element is never resolved (nothing ties it to the connected set).</summary>
        public bool Allows(IReadOnlyCollection<long> ids, bool hasResolutions, bool permitted) =>
            hasResolutions && permitted && Rounds < MaxRounds && ids != null && ids.Count > 0 && ids.All(_allowed.Contains);

        /// <summary>One preprocessor pass resolved at least one error (Revit then processes the failures again).</summary>
        public void NoteRound() => Rounds++;

        public void Record(string text, string caption)
        {
            var line = (text ?? "").Trim() + (string.IsNullOrWhiteSpace(caption) ? "" : " — " + caption.Trim());
            if (line.Length > 0 && !_resolved.Contains(line)) _resolved.Add(line);
        }

        /// <summary>The envelope's warning for one resolution.</summary>
        public static string WarningText(string resolved) => "Forced: Revit resolved an error with its own default fix: " + resolved;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_current, this)) _current = _outer;
        }
    }
}
