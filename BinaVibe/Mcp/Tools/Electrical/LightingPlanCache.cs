// Lighting plan cache — pure, Revit-free, MILLIMETRES ONLY.
//
// One LightingPlan per suggest_lighting_points call, cached under
// "lighting_plan:<guid>" so place_lighting_points places the EXACT points the
// drafter reviewed. Mechanics are PlanCache.cs; this file is the DTOs plus a
// named facade, so App.cs and ElecPlanCaches keep one call site each.

using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One reviewable fixture position, already resolved to absolute
    /// project-internal coordinates in mm.</summary>
    public sealed class PlannedLight
    {
        /// <summary>Stable 0-based index — what the drafter confirms with.</summary>
        public int Index;
        public long RoomId;
        public string RoomName = "";
        public string LevelName = "";
        /// <summary>Ceiling to host on; null when no ceiling covers the point,
        /// in which case a host-based family cannot be placed here.</summary>
        public long? HostCeilingId;
        /// <summary>"ceiling" | "unhosted"</summary>
        public string Host = "unhosted";
        public double XMm;
        public double YMm;
        /// <summary>Absolute project-internal Z of the fixture.</summary>
        public double ZMm;
        /// <summary>Height above the room's finished floor. Carried separately
        /// from ZMm so a mount-height override at commit time swaps the
        /// component instead of adding to it.</summary>
        public double MountHeightMm;
    }

    /// <summary>What one room contributed, kept on the plan so the commit and
    /// the report agree on the arithmetic without recomputing it.</summary>
    public sealed class PlannedLightRoom
    {
        public long RoomId;
        public string RoomName = "";
        public string LevelName = "";
        public double AreaM2;
        public double TargetWPerM2;
        public double FixtureW;
        public int RequiredCount;
        public int PlannedCount;
        public double RequiredW;
        public double InstalledW;
    }

    /// <summary>Everything one suggest_lighting_points run produced.</summary>
    public sealed class LightingPlan : PlanBase
    {
        public List<PlannedLight> Points = new();
        public List<PlannedLightRoom> Rooms = new();
        public string FamilyType = "";
        /// <summary>The effective rule values this plan was built with, echoed
        /// back on the wire so any answer is auditable.</summary>
        public Dictionary<string, object?> ParamsUsed = new();
    }

    public static class LightingPlanCache
    {
        private static readonly PlanCache<LightingPlan> _cache =
            new("lighting_plan:", "suggest_lighting_points");

        public static string Store(LightingPlan plan, string docKey) => _cache.Store(plan, docKey);
        public static LightingPlan Get(string planId, string docKey) => _cache.Get(planId, docKey);
        public static void CloseAll() => _cache.CloseAll();
    }
}
