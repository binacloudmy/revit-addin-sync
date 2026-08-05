// The propose/commit handoff shared by the socket, circuit and route plans —
// pure, Revit-free.
//
// A propose tool caches what the drafter is about to review; the confirmation
// carries a plan_id plus small integer indices, never coordinates. Nothing
// computed travels back through the model, so there is no opportunity for
// silent coordinate corruption or mm/ft slippage in transit.
//
// Entries carry a document key and Get() rejects a mismatch — serving a plan
// computed against a different model is actively dangerous. Both refusals name
// the tool that would rebuild the plan, because the drafter reads them.
//
// Audit/AuditModels.cs AuditResultCache is a fourth near-copy of this shape
// with a DIFFERENT miss contract (returns null instead of throwing, and has no
// document guard). Read SocketPlanCache's history before merging it in.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>What every cached plan carries. Fields rather than an
    /// interface so the plan DTOs keep their exact serialized shape.</summary>
    public abstract class PlanBase
    {
        public string PlanId = "";
        /// <summary>doc.PathName (or doc.Title for an unsaved model). Guards
        /// against replaying a plan into the wrong document.</summary>
        public string DocKey = "";
        public DateTime CreatedUtc;
    }

    /// <summary>In-memory plan store: id-prefixed, document-guarded, 2h TTL.
    /// One instance per plan kind, held by that kind's static facade.</summary>
    public sealed class PlanCache<T> where T : PlanBase
    {
        private sealed class Entry
        {
            public T? Plan;
            public DateTime LastUsed;
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

        private readonly string _idPrefix;
        private readonly string _proposeTool;

        /// <param name="idPrefix">Leading segment of the plan id, e.g. "route_plan:".</param>
        /// <param name="proposeTool">The tool that rebuilds a lost plan — named
        /// in both refusals so the drafter knows the way out.</param>
        public PlanCache(string idPrefix, string proposeTool)
        {
            _idPrefix = idPrefix;
            _proposeTool = proposeTool;
        }

        public string Store(T plan, string docKey)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Sweep();
            var id = _idPrefix + Guid.NewGuid().ToString("N");
            plan.PlanId = id;
            plan.DocKey = docKey ?? "";
            plan.CreatedUtc = DateTime.UtcNow;
            _entries[id] = new Entry { Plan = plan, LastUsed = DateTime.UtcNow };
            return id;
        }

        /// <summary>Retrieve a plan for the document it was built against.
        /// Throws — with drafter-readable guidance — on an unknown/expired id
        /// or a document mismatch.</summary>
        public T Get(string planId, string docKey)
        {
            if (string.IsNullOrWhiteSpace(planId) || !_entries.TryGetValue(planId, out var e)
                || e.Plan == null)
                throw new InvalidOperationException(
                    "unknown plan_id " + planId +
                    " — run " + _proposeTool + " again (plans expire after 2 hours)");

            if (!string.Equals(e.Plan.DocKey, docKey ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "plan_id " + planId + " was generated for a different model (" +
                    (string.IsNullOrEmpty(e.Plan.DocKey) ? "<unsaved>" : e.Plan.DocKey) +
                    ") — run " + _proposeTool + " again in this model");

            e.LastUsed = DateTime.UtcNow;
            return e.Plan;
        }

        public void CloseAll() => _entries.Clear();

        private void Sweep()
        {
            var stale = new List<string>();
            foreach (var kv in _entries)
                if (DateTime.UtcNow - kv.Value.LastUsed > Ttl) stale.Add(kv.Key);
            foreach (var key in stale) _entries.Remove(key);
        }
    }
}
