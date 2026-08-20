// TxOwnership — "did the copilot make this change?", by transaction name.
// PURE, System-only, no Revit types in any signature, so Tests.csproj can
// source-link it. (Linking a file whose signatures mention Revit types makes
// the xUnit runner skip the ENTIRE suite while still exiting green — the trap
// CategoryNames.cs was carved out of CategoryResolve.cs to avoid.)
//
// Two consumers, one answer:
//   * DocVersion — a change that is NOT ours expires the backend's cached
//     model context. Ours must not, or the agent's own writes would
//     invalidate the snapshot it is mid-way through using.
//   * TurnReceiptService — a drafter editing mid-batch must not be claimed on
//     our receipt.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools
{
    internal static class TxOwnership
    {
        /// <summary>Transaction-name prefixes that mean "the copilot did this".
        ///
        /// BOTH spellings are real and both must stay. Tool transactions are
        /// named "BinaVibe: &lt;tool&gt;" (Mutators, BatchLink, the stairs edit
        /// scope) but PartLoop names its per-part transactions "BINA part
        /// &lt;id&gt;" and "BINA undo part &lt;id&gt;". The turn receipt used to
        /// match "BinaVibe" alone, so every build_design part was missing from
        /// its own receipt — and the same omission would have made a build
        /// expire the very context snapshot the next call needs.</summary>
        public static readonly string[] Prefixes = { "BinaVibe", "BINA " };

        /// <summary>True when any transaction name in the commit is ours. A
        /// commit that mixes ours with a drafter's counts as OURS: their edit
        /// rode along with the transaction we are already measuring.</summary>
        public static bool IsOurs(IEnumerable<string> transactionNames)
        {
            if (transactionNames == null) return false;
            foreach (var n in transactionNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                foreach (var p in Prefixes)
                    if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }
    }
}
