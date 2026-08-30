// Operation receipt assembly — Revit-free (bina-ai spec §8.3/§8.5, R1 Task 17).
//
// TurnReceiptService measures what Revit changed; this file turns that
// measurement into the immutable receipt shape both the pane card and the
// backend store read. Kept free of Revit types so the shape is unit-tested.
//
// One approved mutation pack = one operation = one Undo group. The group's
// size is the number of BinaVibe transactions the pack committed, so [Undo]
// posts exactly that many undo commands (+1 when our highlight tint added a
// step). Legacy (no transactions recorded) still posts one.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    public static class ReceiptShape
    {
        /// <summary>Cap on element ids carried per list; counts stay exact.</summary>
        public const int MaxIds = 500;

        public static Dictionary<string, object> Build(
            string operationId, string jobId, string documentFingerprint,
            long preRevision, long postRevision,
            IReadOnlyCollection<long> added, IReadOnlyCollection<long> modified, int deleted,
            IReadOnlyList<string> txNames, string status)
        {
            var addedSet = new HashSet<long>(added ?? Array.Empty<long>());
            // An element both added and modified in the pack is "added".
            var modifiedList = (modified ?? Array.Empty<long>()).Where(id => !addedSet.Contains(id)).Distinct().ToList();
            var addedList = addedSet.ToList();
            var tx = (txNames ?? Array.Empty<string>()).ToList();
            bool truncated = addedList.Count > MaxIds || modifiedList.Count > MaxIds;

            return new Dictionary<string, object>
            {
                ["receipt_id"] = Guid.NewGuid().ToString("N"),
                ["operation_id"] = operationId ?? "",
                ["job_id"] = jobId ?? "",
                ["document_fingerprint"] = documentFingerprint ?? "",
                ["pre_revision"] = preRevision,
                ["post_revision"] = postRevision,
                ["added_ids"] = addedList.Take(MaxIds).ToList(),
                ["modified_ids"] = modifiedList.Take(MaxIds).ToList(),
                ["ids_truncated"] = truncated,
                // Legacy count keys — the existing receipt card reads these.
                ["added"] = addedList.Count,
                ["modified"] = modifiedList.Count,
                ["deleted"] = Math.Max(0, deleted),
                ["transactions"] = tx,
                ["status"] = status ?? "completed",
                ["undo_group"] = new Dictionary<string, object>
                {
                    ["operation_id"] = operationId ?? "",
                    ["tx_count"] = tx.Count,
                },
            };
        }

        /// <summary>How many Undo commands restore the whole pack.</summary>
        public static int UndoSteps(int txCount, bool hadTint) =>
            Math.Max(1, txCount) + (hadTint ? 1 : 0);
    }
}
