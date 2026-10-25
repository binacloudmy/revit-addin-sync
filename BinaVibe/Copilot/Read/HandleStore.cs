#nullable enable
// Stub created by W0; body owned by W2
// Revit-free. Session-scoped handles ("h_<8hex>", LRU 200) for result sets,
// so the model can reuse a find result without re-sending ids.

namespace BinaVibe.Copilot.Read
{
    using System;
    using System.Collections.Generic;
    using BinaVibe.Copilot.Contracts;

    public sealed class HandleStore : IHandleStore
    {
        public HandleStore()
        {
        }

        public string Put(IReadOnlyList<ElementRef> refs, string label) => throw new NotImplementedException("W2");

        public IReadOnlyList<ElementRef>? Get(string handle) => throw new NotImplementedException("W2");
    }
}
