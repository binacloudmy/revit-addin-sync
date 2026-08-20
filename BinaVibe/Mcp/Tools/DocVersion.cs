// DocVersion — two monotonic counters so the backend can tell a snapshot that
// is merely OLD from one that is WRONG.
//
// The grounding layer computes stair runs and roof boundaries from a
// `get_model_context` reading cached server-side. That reading has to expire,
// or a drafter who drags a wall between the read and the write gets geometry
// solved against a building that no longer exists.
//
// Expiring on ANY change is useless: the agent's own writes change the
// document, so a two-element edit would invalidate its own context between the
// elements, and `execute_revit_batch` could never hold one at all. So changes
// are ATTRIBUTED:
//
//   Version         bumps on every committed change, ours or not.
//   ForeignVersion  bumps only on changes NO transaction of ours produced.
//
// The backend compares ForeignVersion. Ours moving is normal; a stranger's
// moving means re-read.
//
// No new DocumentChanged subscription: this is fed from the one
// TurnReceiptService already owns (EnsureSubscribed runs at the top of every
// job drain). CostUpdateHandler is a second subscriber already; a third would
// be a leak, and the deprecated indexer stays dead.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace BinaVibe.Mcp.Tools
{
    internal static class DocVersion
    {
        private static readonly object _lock = new object();
        private static long _version;
        private static long _foreignVersion;

        /// <summary>Did WE cause this change? Delegates to TxOwnership, which
        /// is System-only so the prefix rule can actually be unit-tested —
        /// this file cannot be, because its other signatures take Revit
        /// types.</summary>
        public static bool IsOurs(IEnumerable<string>? transactionNames) =>
            TxOwnership.IsOurs(transactionNames);

        /// <summary>Record one DocumentChanged event. Called from
        /// TurnReceiptService's handler BEFORE its own recording gate — a
        /// foreign edit that lands between batches is exactly the change this
        /// counter exists to notice, and that is when it is NOT recording.
        ///
        /// Must never throw: it runs inside a read-only event handler on the
        /// Revit UI thread, where an exception would surface as a failed
        /// commit.</summary>
        public static void Observe(DocumentChangedEventArgs e)
        {
            try
            {
                if (e == null) return;
                var touched = e.GetAddedElementIds().Count
                            + e.GetModifiedElementIds().Count
                            + e.GetDeletedElementIds().Count;
                if (touched == 0) return;

                var ours = IsOurs(e.GetTransactionNames());
                lock (_lock)
                {
                    _version++;
                    if (!ours) _foreignVersion++;
                }
            }
            catch { /* a counter must never break a commit */ }
        }

        public static long Current { get { lock (_lock) return _version; } }

        public static long Foreign { get { lock (_lock) return _foreignVersion; } }

        /// <summary>Stable identity for a document, so a snapshot taken in one
        /// model can never ground a write in another. Mirrors
        /// SocketPlanCache's DocKey rule — serving coordinates computed against
        /// a different model is actively dangerous, not merely wrong.</summary>
        public static string KeyFor(Document? doc)
        {
            if (doc == null) return "";
            var path = doc.PathName;
            return string.IsNullOrWhiteSpace(path) ? (doc.Title ?? "") : path;
        }

        /// <summary>Stamp a tool result with the identity a cached snapshot is
        /// checked against.</summary>
        public static void Stamp(Dictionary<string, object?> result, Document? doc)
        {
            if (result == null) return;
            result["doc_key"] = KeyFor(doc);
            result["doc_version"] = Current;
            result["foreign_version"] = Foreign;
        }
    }
}
