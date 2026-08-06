// One place to drop every electrical propose/commit plan cache.
//
// A held plan_id keeps resolving after the model has moved under it, and the
// commit tools only re-check a subset of what they depend on — so anything that
// regenerates element ids, or changes what a panel IS, must drop all three
// caches or a commit runs against a model that no longer matches the reviewed
// proposal. Calling one fan-out makes that the easy thing to get right.

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElecPlanCaches
    {
        /// <summary>Drop every held socket / lighting / circuit / route plan.
        /// Callers pair this with `plans_invalidated: true` in their result so
        /// the agent re-proposes instead of committing a stale plan_id.</summary>
        public static void DropAll()
        {
            try { CircuitPlanCache.CloseAll(); } catch { }
            try { SocketPlanCache.CloseAll(); } catch { }
            try { LightingPlanCache.CloseAll(); } catch { }
            try { RoutePlanCache.CloseAll(); } catch { }
        }
    }
}
