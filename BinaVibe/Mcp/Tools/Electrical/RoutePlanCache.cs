// Route plan cache — pure, Revit-free, MILLIMETRES ONLY.
//
// One RoutePlan per suggest_circuit_routes call, cached under
// "route_plan:<guid>" so create_circuit_routes builds the EXACT legs the
// drafter reviewed (including which legs were flagged obstructed).
// Coordinates never travel back through the model — plan_id plus indices
// only, same rationale as SocketPlanCache/CircuitPlanCache.
//
// Legs carry raw mm doubles rather than Pt3Mm because these classes are
// public wire-adjacent DTOs while Pt3Mm is internal to GeomMm.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One straight routed leg, project-internal mm.</summary>
    public sealed class RouteLeg
    {
        public double FromXMm, FromYMm, FromZMm;
        public double ToXMm, ToYMm, ToZMm;
        public double LengthMm;
        /// <summary>"run" (horizontal at routing elevation) | "rise" | "drop".</summary>
        public string Kind = "run";
        /// <summary>Obstruction rows from the corridor scan, wire-shaped.
        /// Empty = probed clear or not probed (see PlannedRoute.Probed).</summary>
        public List<Dictionary<string, object?>> Obstructions = new();
    }

    /// <summary>One reviewable routed circuit.</summary>
    public sealed class PlannedRoute
    {
        /// <summary>Stable 0-based index — what the drafter confirms with.</summary>
        public int Index;
        /// <summary>ElectricalSystem element id.</summary>
        public long CircuitId;
        public string CircuitNumber = "";
        public long PanelId;
        /// <summary>Device element ids in the chain order the legs visit.</summary>
        public List<long> DeviceIds = new();
        /// <summary>Hop boundaries: HopStartLegIndex[i] is the first leg of
        /// hop i (panel->dev0, dev0->dev1, ...). Wire creation needs per-hop
        /// vertex runs; conduit creation just walks all legs.</summary>
        public List<int> HopStartLegIndex = new();
        public List<RouteLeg> Legs = new();
        public double TotalLengthMm;
        public double CalcAmps;
        public double? WireCsaMm2;
        public double? ConduitDiameterMm;
        public double? MvPerAM;
        /// <summary>"no_adequate_size" when the sizing table had no row big
        /// enough; null when sizing succeeded.</summary>
        public string? SizingError;
        public int ObstructedLegCount;
        public bool ThreePhase;
        public List<string> Notes = new();
    }

    /// <summary>Everything one suggest_circuit_routes run produced.</summary>
    public sealed class RoutePlan
    {
        public string PlanId = "";
        public string DocKey = "";
        public DateTime CreatedUtc;
        public List<PlannedRoute> Routes = new();
        public Dictionary<string, object?> ParamsUsed = new();
        public double RoutingElevationMm;
    }

    public static class RoutePlanCache
    {
        private sealed class Entry
        {
            public RoutePlan Plan = new();
            public DateTime LastUsed;
        }

        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

        public static string Store(RoutePlan plan, string docKey)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Sweep();
            var id = "route_plan:" + Guid.NewGuid().ToString("N");
            plan.PlanId = id;
            plan.DocKey = docKey ?? "";
            plan.CreatedUtc = DateTime.UtcNow;
            _entries[id] = new Entry { Plan = plan, LastUsed = DateTime.UtcNow };
            return id;
        }

        /// <summary>Retrieve a plan for the document it was built against.
        /// Throws — with drafter-readable guidance — on an unknown/expired id
        /// or a document mismatch.</summary>
        public static RoutePlan Get(string planId, string docKey)
        {
            if (string.IsNullOrWhiteSpace(planId) || !_entries.TryGetValue(planId, out var e))
                throw new InvalidOperationException(
                    "unknown plan_id " + planId +
                    " — run suggest_circuit_routes again (plans expire after 2 hours)");

            if (!string.Equals(e.Plan.DocKey, docKey ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "plan_id " + planId + " was generated for a different model (" +
                    (string.IsNullOrEmpty(e.Plan.DocKey) ? "<unsaved>" : e.Plan.DocKey) +
                    ") — run suggest_circuit_routes again in this model");

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
