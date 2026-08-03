// Circuit plan cache — pure, Revit-free.
//
// One CircuitPlan per suggest_circuits call, cached under
// "circuit_plan:<guid>" so create_circuits commits the EXACT grouping the
// drafter reviewed. Device ids never travel back through the model: the
// confirmation carries a plan_id plus small integer indices, so a stale or
// re-typed device list cannot slip in between review and commit.
//
// Mechanics cloned from SocketPlanCache (doc-key guard, 2h TTL, CloseAll
// wired in App.cs — AuditResultCache's dead CloseAll leak not inherited).

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One reviewable proposed circuit.</summary>
    public sealed class PlannedCircuit
    {
        /// <summary>Stable 0-based index — what the drafter confirms with.</summary>
        public int Index;
        /// <summary>"lighting" | "receptacle"</summary>
        public string LoadClass = "";
        /// <summary>Devices in daisy-chain order (element 0 takes the home
        /// run). Coordinates are kept so route proposing can reuse the
        /// reviewed chain geometry without re-deriving it.</summary>
        public List<ElecDevice> Devices = new();
        public double TotalVa;
        public double CalcAmps;
        public long PanelId;
        public string PanelName = "";
        /// <summary>0-based proposed phase — a proposal only; Revit assigns
        /// the real slot at commit and CircuitCommit reports both.</summary>
        public int ProposedPhase;
        public double BreakerA;
        public bool Feasible = true;
        public List<string> Notes = new();
    }

    /// <summary>Everything one suggest_circuits run produced.</summary>
    public sealed class CircuitPlan
    {
        public string PlanId = "";
        /// <summary>doc.PathName (or doc.Title for an unsaved model). Guards
        /// against replaying a plan into the wrong document.</summary>
        public string DocKey = "";
        public DateTime CreatedUtc;
        public List<PlannedCircuit> Circuits = new();
        /// <summary>The effective rule values this plan was built with, echoed
        /// back on the wire so any answer is auditable.</summary>
        public Dictionary<string, object?> ParamsUsed = new();
        public double VoltageV;
    }

    public static class CircuitPlanCache
    {
        private sealed class Entry
        {
            public CircuitPlan Plan = new();
            public DateTime LastUsed;
        }

        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

        public static string Store(CircuitPlan plan, string docKey)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Sweep();
            var id = "circuit_plan:" + Guid.NewGuid().ToString("N");
            plan.PlanId = id;
            plan.DocKey = docKey ?? "";
            plan.CreatedUtc = DateTime.UtcNow;
            _entries[id] = new Entry { Plan = plan, LastUsed = DateTime.UtcNow };
            return id;
        }

        /// <summary>Retrieve a plan for the document it was built against.
        /// Throws — with drafter-readable guidance — on an unknown/expired id
        /// or a document mismatch.</summary>
        public static CircuitPlan Get(string planId, string docKey)
        {
            if (string.IsNullOrWhiteSpace(planId) || !_entries.TryGetValue(planId, out var e))
                throw new InvalidOperationException(
                    "unknown plan_id " + planId +
                    " — run suggest_circuits again (plans expire after 2 hours)");

            if (!string.Equals(e.Plan.DocKey, docKey ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "plan_id " + planId + " was generated for a different model (" +
                    (string.IsNullOrEmpty(e.Plan.DocKey) ? "<unsaved>" : e.Plan.DocKey) +
                    ") — run suggest_circuits again in this model");

            e.LastUsed = DateTime.UtcNow;
            return e.Plan;
        }

        public static void CloseAll() => _entries.Clear();

        private static void Sweep()
        {
            var stale = new List<string>();
            foreach (var kv in _entries)
                if (DateTime.UtcNow - kv.Value.LastUsed > Ttl) stale.Add(kv.Key);
            foreach (var key in stale) _entries.Remove(key);
        }
    }
}
