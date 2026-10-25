#nullable enable
// Stub created by W0; body owned by W1
// Revit-free. Collects Revit failures (warnings and errors) raised during a
// guarded transaction so envelopes can report them instead of only deleting
// them. Snapshot() dedupes by (definitionId, sorted ids) and caps at 10.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Guard
{
    public sealed class FailureLog
    {
        /// <summary>The thread-static log of the current scope.</summary>
        public static FailureLog Current => throw new NotImplementedException("W1");

        /// <summary>Starts a new <see cref="Current"/> for a scope; dispose to end it.</summary>
        public static IDisposable Begin() => throw new NotImplementedException("W1");

        public void Add(string text, string severity, string? definitionId, IEnumerable<long> ids) =>
            throw new NotImplementedException("W1");

        public IReadOnlyList<WarningDto> Snapshot() => throw new NotImplementedException("W1");
    }
}
