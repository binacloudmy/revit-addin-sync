#nullable enable
// Revit-free cores of CallbackScope and ExternalRevision (spec §7.4), keyed by
// any object with value equality (a Revit Document overrides Equals and
// GetHashCode), so the scope and counting rules run in the test harness.
//
//   CallbackScopeTable — which of our callbacks (preview, read, look, commit)
//     is running against a document. Scopes nest; the innermost wins; a
//     dispose out of order removes only its own scope.
//   RevisionCounter — commits, undos and redos that happened OUTSIDE our
//     scopes, per document. A rollback changed nothing and never counts. A
//     count is dropped only after its document really closed.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Guard
{
    /// <summary>Which of our own callbacks is running (spec §7.4).</summary>
    public enum CallbackKind { Preview, Read, Look, Commit }

    public sealed class CallbackScopeTable
    {
        private readonly object _lock = new object();
        private readonly List<Scope> _open = new List<Scope>();   // oldest first

        public IDisposable Enter(object doc, CallbackKind kind)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var scope = new Scope(this, doc, kind);
            lock (_lock) _open.Add(scope);
            return scope;
        }

        public CallbackKind? Current(object doc)
        {
            if (doc == null) return null;
            lock (_lock)
            {
                for (int i = _open.Count - 1; i >= 0; i--)
                    if (_open[i].Doc.Equals(doc)) return _open[i].Kind;
            }
            return null;
        }

        private void Close(Scope scope)
        {
            lock (_lock) _open.Remove(scope);
        }

        private sealed class Scope : IDisposable
        {
            private readonly CallbackScopeTable _table;
            private bool _disposed;

            public Scope(CallbackScopeTable table, object doc, CallbackKind kind)
            {
                _table = table; Doc = doc; Kind = kind;
            }

            public object Doc { get; }
            public CallbackKind Kind { get; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _table.Close(this);
            }
        }
    }

    public sealed class RevisionCounter
    {
        private readonly object _lock = new object();
        private readonly Dictionary<object, long> _counts = new Dictionary<object, long>();
        private readonly Dictionary<int, object> _closing = new Dictionary<int, object>();

        /// <param name="modelChanged">A commit, undo or redo (not a rollback).</param>
        /// <param name="insideOurScope">One of our callbacks was running on that document.</param>
        public void Observe(object doc, bool modelChanged, bool insideOurScope)
        {
            if (doc == null || !modelChanged || insideOurScope) return;
            lock (_lock) _counts[doc] = (_counts.TryGetValue(doc, out var n) ? n : 0) + 1;
        }

        public long Get(object doc)
        {
            if (doc == null) return 0;
            lock (_lock) return _counts.TryGetValue(doc, out var n) ? n : 0;
        }

        /// <summary>Drops a closed document so the table holds no reference to it.</summary>
        public void Forget(object doc)
        {
            if (doc == null) return;
            lock (_lock) _counts.Remove(doc);
        }

        /// <summary>A close started (Revit's DocumentClosing). It can still be cancelled,
        /// so nothing is dropped yet; <paramref name="closeId"/> pairs it with Closed.</summary>
        public void Closing(int closeId, object doc)
        {
            if (doc == null) return;
            lock (_lock) _closing[closeId] = doc;
        }

        /// <summary>The close finished (Revit's DocumentClosed): the count is dropped only
        /// when the document really closed.</summary>
        public void Closed(int closeId, bool succeeded)
        {
            object? doc;
            lock (_lock)
            {
                if (!_closing.TryGetValue(closeId, out doc)) return;
                _closing.Remove(closeId);
            }
            if (succeeded) Forget(doc);
        }
    }
}
