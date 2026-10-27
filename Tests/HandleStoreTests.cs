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
        // ── ruling R80: a handle belongs to the document it was made in ──

        private sealed class ActiveDoc
        {
            public string? Nonce;
            public string? Key() => Nonce == null ? null : DocIdentity.Fingerprint(Nonce);
        }

        [Fact]
        public void SameDocument_GetsItsHandle()
        {
            var active = new ActiveDoc { Nonce = "n_aaaaaaaaaaaa" };
            var store = new HandleStore(active.Key);
            var h = store.Put(Refs(1, 2), "doors");
            Assert.Equal(new long[] { 1, 2 }, store.Get(h)!.Select(r => r.Id));
        }

        [Fact]
        public void AnotherDocument_IsRefused_WithTheAnotherModelMessage()
        {
            // Find in project A, switch to B in the same chat: ids collide across projects.
            var active = new ActiveDoc { Nonce = "n_aaaaaaaaaaaa" };
            var store = new HandleStore(active.Key);
            var h = store.Put(Refs(312045), "ceilings in A");
            active.Nonce = "n_bbbbbbbbbbbb";
            Assert.Null(store.Get(h));
            Assert.True(HandleStore.WasRefusedAsAnotherModel(h));
            Assert.Equal("That list came from another model; search again here.", HandleStore.NotFoundMessage(h));
            Assert.Null(store.LabelOf(h));

            active.Nonce = "n_aaaaaaaaaaaa";                  // back in A: valid again
            Assert.NotNull(store.Get(h));
            Assert.False(HandleStore.WasRefusedAsAnotherModel(h));
        }

        [Fact]
        public void SaveAs_KeepsTheSessionNonce_SoHandlesStayValid()
        {
            // The key is the session nonce's fingerprint, not the path: Save As does not change it.
            var active = new ActiveDoc { Nonce = "n_cccccccccccc" };
            var store = new HandleStore(active.Key);
            var h = store.Put(Refs(7), "before save as");
            var keyAfterSaveAs = DocIdentity.Fingerprint("n_cccccccccccc");
            Assert.Equal(active.Key(), keyAfterSaveAs);
            Assert.NotNull(store.Get(h));
        }

        [Fact]
        public void NoProvider_OrNoDocument_KeepsTodaysBehaviour()
        {
            var plain = new HandleStore();
            var h = plain.Put(Refs(1), "x");
            Assert.NotNull(plain.Get(h));

            var active = new ActiveDoc { Nonce = null };       // no document when the handle was made
            var store = new HandleStore(active.Key);
            var h2 = store.Put(Refs(2), "y");
            active.Nonce = "n_dddddddddddd";
            Assert.NotNull(store.Get(h2));
            var h3 = store.Put(Refs(3), "z");
            active.Nonce = null;                               // no document now: not refused either
            Assert.NotNull(store.Get(h3));

            var throwing = new HandleStore(() => throw new System.InvalidOperationException("off thread"));
            var h4 = throwing.Put(Refs(4), "w");
            Assert.NotNull(throwing.Get(h4));
        }

        [Fact]
        public void NotFoundMessage_ForAnUnknownHandle_SaysUnknownOrExpired()
        {
            var store = new HandleStore();
            Assert.Null(store.Get("h_00000000"));
            Assert.False(HandleStore.WasRefusedAsAnotherModel("h_00000000"));
            Assert.Contains("unknown or expired", HandleStore.NotFoundMessage("h_00000000"));
        }
        private sealed class Wrapper : IHandleStore
        {
            private readonly IHandleStore _inner;
            public Wrapper(IHandleStore inner) { _inner = inner; }
            public string Put(IReadOnlyList<ElementRef> refs, string label) => _inner.Put(refs, label);
            public IReadOnlyList<ElementRef>? Get(string handle) => _inner.Get(handle);
        }

        [Fact]
        public void Refusal_IsVisibleThroughAWrapper_LikeTheSessionStore()
        {
            // W5's SessionHandles wraps the store; callers only see IHandleStore.Get.
            var active = new ActiveDoc { Nonce = "n_aaaaaaaaaaaa" };
            IHandleStore handles = new Wrapper(new HandleStore(active.Key));
            var h = handles.Put(Refs(1), "x");
            active.Nonce = "n_bbbbbbbbbbbb";
            Assert.Null(handles.Get(h));
            Assert.Equal(HandleStore.FromAnotherModel, HandleStore.NotFoundMessage(h));
        }
    }
}
