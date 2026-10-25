#nullable enable
// Stub created by W0; body owned by W1
// Revit-free. The element ids one recording saw added, modified and deleted,
// and the math that splits them into what the call touched versus what Revit
// changed on the side.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Guard
{
    public sealed class ChangeSet
    {
        public List<long> Added = new();
        public List<long> Modified = new();
        public List<long> Deleted = new();
        /// <summary>Element id -> category name.</summary>
        public Dictionary<long, string> CategoryOf = new();
    }

    public static class ChangeSetMath
    {
        public static ChangeSet Subtract(ChangeSet all, ChangeSet reverted) => throw new NotImplementedException("W1");

        public static (ChangesDto touched, SideEffectsDto sideEffects) Split(
            ChangeSet all, IReadOnlyCollection<long> touchedIds, IReadOnlyCollection<long> bookkeepingIds) =>
            throw new NotImplementedException("W1");
    }
}
