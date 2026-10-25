#nullable enable
// W2 Task 1: session-scoped handles ("h_<8hex>", LRU 200). Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class HandleStoreTests
    {
        private static IReadOnlyList<ElementRef> Refs(params long[] ids) =>
            ids.Select(i => new ElementRef(i, null)).ToList();

        [Fact]
        public void PutGetAndEvicts()
        {
            var store = new HandleStore();
            var first = store.Put(Refs(1, 2, 3), "first");
            Assert.Matches(new Regex("^h_[0-9a-f]{8}$"), first);
            Assert.Equal(new long[] { 1, 2, 3 }, store.Get(first)!.Select(r => r.Id));

            var second = store.Put(Refs(4), "second");
            // Touch `first` so it is the most recent; `second` becomes the oldest.
            Assert.NotNull(store.Get(first));
            for (var i = 0; i < HandleStore.DefaultCapacity - 1; i++)
                store.Put(Refs(100 + i), "filler " + i);

            Assert.Equal(HandleStore.DefaultCapacity, store.Count);
            Assert.Null(store.Get(second));            // least recently used: evicted
            Assert.NotNull(store.Get(first));           // recently read: kept
        }

        [Fact]
        public void Get_UnknownOrMalformed_ReturnsNull()
        {
            var store = new HandleStore();
            Assert.Null(store.Get("h_00000000"));
            Assert.Null(store.Get(""));
            Assert.Null(store.Get("not a handle"));
        }

        [Fact]
        public void Get_IsCaseAndWhitespaceTolerant()
        {
            var store = new HandleStore();
            var h = store.Put(Refs(7), "x");
            Assert.NotNull(store.Get(" " + h.ToUpperInvariant() + " "));
        }

        [Fact]
        public void Put_CopiesRefs_KeepsLinkAndLabel()
        {
            var store = new HandleStore();
            var input = new List<ElementRef> { new ElementRef(10, null), new ElementRef(20, 900) };
            var h = store.Put(input, "34 ceilings on Level 2");
            input.Add(new ElementRef(30, null));

            var got = store.Get(h)!;
            Assert.Equal(2, got.Count);
            Assert.Equal(900, got[1].Link);
            Assert.Equal("34 ceilings on Level 2", store.LabelOf(h));
        }

        [Fact]
        public void Put_RetriesOnCollision()
        {
            var draws = new Queue<uint>(new uint[] { 0xabcdef01, 0xabcdef01, 0x12345678 });
            var store = new HandleStore(5, () => draws.Dequeue());
            var a = store.Put(Refs(1), "a");
            var b = store.Put(Refs(2), "b");
            Assert.Equal("h_abcdef01", a);
            Assert.Equal("h_12345678", b);
        }

        [Fact]
        public void IsHandle_MatchesOnlyTheContractShape()
        {
            Assert.True(HandleStore.IsHandle("h_7f3a91c2"));
            Assert.False(HandleStore.IsHandle("h_7f3a"));
            Assert.False(HandleStore.IsHandle("x_7f3a91c2"));
            Assert.False(HandleStore.IsHandle(null));
        }
    }
}
