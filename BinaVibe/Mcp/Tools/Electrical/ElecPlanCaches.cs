// One place to drop every electrical propose/commit plan cache.
//
// The three caches (socket, circuit, route) hand a plan_id to the drafter and
// hold element ids plus panel facts behind it. Anything that regenerates
// element ids, or changes what a panel IS, makes every held plan stale — but
// the plan_id keeps resolving, and the commit tools only re-check a subset of
// what they depend on. That combination commits against a model that no longer
// matches the reviewed proposal.
//
// The individual CloseAll()s were being called from one site
// (set_connector_electrical_data) and forgotten at the others, which is exactly
// how the distribution-system tools ended up able to invalidate a plan without
// dropping it. Calling this instead makes the correct behaviour the easy one.

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElecPlanCaches
    {
        /// <summary>Drop every held socket / circuit / route plan. Callers pair
        /// this with `plans_invalidated: true` in their result so the agent
        /// re-proposes instead of committing a stale plan_id.</summary>
        public static void DropAll()
        {
            try { CircuitPlanCache.CloseAll(); } catch { }
            try { SocketPlanCache.CloseAll(); } catch { }
            try { RoutePlanCache.CloseAll(); } catch { }
        }
    }
}
