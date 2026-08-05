// Circuit plan cache — pure, Revit-free.
//
// One CircuitPlan per suggest_circuits call, cached under
// "circuit_plan:<guid>" so create_circuits commits the EXACT grouping the
// drafter reviewed. Device ids never travel back through the model: the
// confirmation carries a plan_id plus small integer indices, so a stale or
// re-typed device list cannot slip in between review and commit.
//
// Mechanics are PlanCache.cs; this file is the DTOs plus a named facade.

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
    public sealed class CircuitPlan : PlanBase
    {
        public List<PlannedCircuit> Circuits = new();
        /// <summary>The effective rule values this plan was built with, echoed
        /// back on the wire so any answer is auditable.</summary>
        public Dictionary<string, object?> ParamsUsed = new();
        public double VoltageV;
    }

    public static class CircuitPlanCache
    {
        private static readonly PlanCache<CircuitPlan> _cache =
            new("circuit_plan:", "suggest_circuits");

        public static string Store(CircuitPlan plan, string docKey) => _cache.Store(plan, docKey);
        public static CircuitPlan Get(string planId, string docKey) => _cache.Get(planId, docKey);
        public static void CloseAll() => _cache.CloseAll();
    }
}
