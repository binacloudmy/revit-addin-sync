#nullable enable
// One handle store per copilot chat session (lean-harness spec §7.2; W2 report
// §7a). ReadExecutor and WriteBatchRunner are built once per pane and both hold
// this one object, so a handle a read returns is the handle a write resolves.
// A new chat (or continuing another one) starts a fresh store: the old chat's
// handles are not in the new conversation, so nothing can resolve them by
// accident. Revit-free.

using System;
using System.Collections.Generic;
using System.Threading;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class SessionHandles : IHandleStore
    {
        private readonly Func<IHandleStore> _make;
        private IHandleStore _current;

        public SessionHandles(Func<IHandleStore> make)
        {
            _make = make ?? throw new ArgumentNullException(nameof(make));
            _current = make() ?? throw new InvalidOperationException("The handle store factory returned null.");
        }

        /// <summary>The store the current chat session uses.</summary>
        public IHandleStore Current => Volatile.Read(ref _current);

        /// <summary>A new chat session: later reads and writes use a fresh, empty store.</summary>
        public void NewSession()
        {
            var next = _make() ?? throw new InvalidOperationException("The handle store factory returned null.");
            Volatile.Write(ref _current, next);
        }

        public string Put(IReadOnlyList<ElementRef> refs, string label) => Current.Put(refs, label);

        public IReadOnlyList<ElementRef>? Get(string handle) => Current.Get(handle);
    }
}
