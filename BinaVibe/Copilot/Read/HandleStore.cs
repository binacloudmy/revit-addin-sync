#nullable enable
// Stub created by W0; body owned by W2
// Revit-free. Session-scoped handles ("h_<8hex>", LRU 200) for result sets,
// so the model can reuse a find result without re-sending ids.
//
// One instance per copilot session: the pane (W5) creates it and hands the
// same instance to ReadExecutor and WriteBatchRunner, so a handle from
// find_elements can target set_parameters. Reading a handle refreshes it; the
// least recently used handle is dropped past the capacity. Refs keep the link
// instance id, because linked element ids collide with host ids (spec §7.9).

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

        private static readonly Regex Shape = new("^h_[0-9a-f]{8}$", RegexOptions.CultureInvariant);

        private sealed class Entry
        {
            public string Handle = "";
            public ElementRef[] Refs = Array.Empty<ElementRef>();
            public string Label = "";
        }

        private readonly object _gate = new();
        private readonly int _capacity;
        private readonly Func<uint> _draw;
        private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new();   // First = most recently used

        public HandleStore()
            : this(DefaultCapacity, null)
        {
        }

        /// <summary>Test seam: a smaller capacity and a deterministic id source.</summary>
        internal HandleStore(int capacity, Func<uint>? draw)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _draw = draw ?? RandomUInt;
        }

        /// <summary>Handles currently held.</summary>
        public int Count
        {
            get { lock (_gate) return _map.Count; }
        }

        /// <summary>True for the contract shape "h_" + 8 lowercase hex digits.</summary>
        public static bool IsHandle(string? s) => s != null && Shape.IsMatch(s);

        public string Put(IReadOnlyList<ElementRef> refs, string label)
        {
            if (refs == null) throw new ArgumentNullException(nameof(refs));
            var entry = new Entry { Refs = refs.ToArray(), Label = label ?? "" };
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

        public IReadOnlyList<ElementRef>? Get(string handle)
        {
            var node = Touch(handle);
            return node?.Value.Refs;
        }

        /// <summary>What the handle holds, in words (e.g. "34 ceilings on Level 2"); null when unknown.</summary>
        public string? LabelOf(string handle) => Touch(handle)?.Value.Label;

        private LinkedListNode<Entry>? Touch(string? handle)
        {
            var key = (handle ?? "").Trim().ToLowerInvariant();
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
