// Route plan cache — pure, Revit-free, MILLIMETRES ONLY.
//
// One RoutePlan per suggest_circuit_routes call, cached under
// "route_plan:<guid>" so create_circuit_routes builds the EXACT legs the
// drafter reviewed (including which legs were flagged obstructed).
// Coordinates never travel back through the model — plan_id plus indices
// only. Mechanics are PlanCache.cs; this file is the DTOs plus a facade.
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
        /// <summary>Device this leg drops onto, or 0. Set only on the single
        /// branch drop per device, so the commit can tell a trunk station that
        /// needs a TEE from an end-of-run that needs an elbow.</summary>
        public long DropsToDeviceId;
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
        /// <summary>One entry per hop that actually produced legs. Wire
        /// creation needs per-hop vertex runs; conduit creation just walks all
        /// legs.</summary>
        public List<RouteHop> Hops = new();
        public List<RouteLeg> Legs = new();
        /// <summary>Polyline for ElectricalSystem.SetCircuitPath: the
        /// ELECTRICAL path through the devices (panel connector, up, along,
        /// down to each device and back up), which since the trunk rework is no
        /// longer the shape of the conduit. Empty = nothing to set.</summary>
        internal List<Pt3Mm> PathVerticesMm = new();
        /// <summary>Same path without the dive-and-return at each device —
        /// tried when Revit refuses the shape above. Omits the drops, so its
        /// length is shorter than the conduit run.</summary>
        internal List<Pt3Mm> PathVerticesFlatMm = new();
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
    public sealed class RoutePlan : PlanBase
    {
        public List<PlannedRoute> Routes = new();
        public Dictionary<string, object?> ParamsUsed = new();
        public double RoutingElevationMm;
    }

    public static class RoutePlanCache
    {
        private static readonly PlanCache<RoutePlan> _cache =
            new("route_plan:", "suggest_circuit_routes");

        public static string Store(RoutePlan plan, string docKey) => _cache.Store(plan, docKey);
        public static RoutePlan Get(string planId, string docKey) => _cache.Get(planId, docKey);
        public static void CloseAll() => _cache.CloseAll();
    }
}
