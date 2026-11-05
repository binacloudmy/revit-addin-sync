#nullable enable
// Revit-free. Session-scoped handles ("h_<8hex>", LRU 200) for result sets,
// so the model can reuse a find result without re-sending ids.
//
// One instance per copilot session: the pane (W5) creates it and hands the
// same instance to ReadExecutor and WriteBatchRunner, so a handle from
// find_elements can target set_parameters. Reading a handle refreshes it; the
// least recently used handle is dropped past the capacity. Refs keep the link
// instance id, because linked element ids collide with host ids (spec §7.9).
//
// Ruling R80: element ids also collide across projects, so each handle is
// stamped with the document it was made in (the session-nonce fingerprint
// SnapshotService.ActiveDocKey gives, which survives Save As). Get refuses a
// handle from another document: it returns null and notes the refusal for the
// calling thread, so any caller (also through a wrapper such as W5's
// SessionHandles) can answer with NotFoundMessage(handle). With no provider,
// or no document on either side, nothing is refused.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public sealed class HandleStore : IHandleStore
    {
        public const int DefaultCapacity = 200;

        /// <summary>The answer when a handle was made in another document.</summary>
        public const string FromAnotherModel = "That list came from another model; search again here.";

        private static readonly Regex Shape = new("^h_[0-9a-f]{8}$", RegexOptions.CultureInvariant);

        [ThreadStatic] private static string? _lastRefused;

        private sealed class Entry
        {
            public string Handle = "";
            public ElementRef[] Refs = Array.Empty<ElementRef>();
            public string Label = "";
            /// <summary>The document the handle was made in; null when unknown.</summary>
            public string? DocKey;
        }

        private readonly object _gate = new();
        private readonly int _capacity;
        private readonly Func<uint> _draw;
        private readonly Func<string?>? _currentDocKey;
        private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new();   // First = most recently used

        /// <param name="currentDocKey">The active document's identity (SnapshotService.ActiveDocKey);
        /// null keeps handles document-blind (tests, legacy callers).</param>
        public HandleStore(Func<string?>? currentDocKey = null)
            : this(DefaultCapacity, null, currentDocKey)
        {
        }

        /// <summary>Test seam: a smaller capacity and a deterministic id source.</summary>
        internal HandleStore(int capacity, Func<uint>? draw, Func<string?>? currentDocKey = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _draw = draw ?? RandomUInt;
            _currentDocKey = currentDocKey;
        }

        /// <summary>Handles currently held.</summary>
        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        /// <summary>True for the contract shape "h_" + 8 lowercase hex digits.</summary>
        public static bool IsHandle(string? s) => s != null && Shape.IsMatch(s);

        private static string Normalize(string? handle) => (handle ?? "").Trim().ToLowerInvariant();

        /// <summary>True when this thread's last Get refused the handle because it was made in another document.</summary>
        public static bool WasRefusedAsAnotherModel(string? handle) =>
            _lastRefused != null && _lastRefused == Normalize(handle);

        /// <summary>What to tell the model when Get returned null for a handle.</summary>
        public static string NotFoundMessage(string handle) =>
            WasRefusedAsAnotherModel(handle)
                ? FromAnotherModel
                : "Handle " + handle + " is unknown or expired in this session. Run find_elements again.";

        private string? CurrentDocKey()
        {
            try { return _currentDocKey?.Invoke(); }
            catch { return null; }   // unknown: never refuse on a failed read
        }

        public string Put(IReadOnlyList<ElementRef> refs, string label)
        {
            if (refs == null) throw new ArgumentNullException(nameof(refs));
            var entry = new Entry { Refs = refs.ToArray(), Label = label ?? "", DocKey = CurrentDocKey() };
            lock (_gate)
            {
                string handle;
                do handle = "h_" + _draw().ToString("x8");
                while (_map.ContainsKey(handle));

                entry.Handle = handle;
                _map[handle] = _lru.AddFirst(entry);
                while (_map.Count > _capacity)
                {
                    var oldest = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(oldest.Value.Handle);
                }
                return handle;
            }
        }

        public IReadOnlyList<ElementRef>? Get(string handle) => Usable(handle)?.Refs;

        /// <summary>What the handle holds, in words (e.g. "34 ceilings on Level 2"); null when unknown
        /// or made in another document.</summary>
        public string? LabelOf(string handle) => Usable(handle)?.Label;

        private Entry? Usable(string? handle)
        {
            _lastRefused = null;
            var node = Touch(handle);
            if (node == null) return null;
            var made = node.Value.DocKey;
            if (made != null)
            {
                var now = CurrentDocKey();
                if (now != null && !string.Equals(made, now, StringComparison.Ordinal))
                {
                    _lastRefused = node.Value.Handle;
                    return null;
                }
            }
            return node.Value;
        }

        private LinkedListNode<Entry>? Touch(string? handle)
        {
            var key = Normalize(handle);
            if (!IsHandle(key)) return null;
            lock (_gate)
            {
                if (!_map.TryGetValue(key, out var node)) return null;
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node;
            }
        }

        private static uint RandomUInt()
        {
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }
    }
}
