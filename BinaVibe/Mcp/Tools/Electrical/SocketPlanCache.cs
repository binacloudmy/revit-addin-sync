// Socket plan cache — pure, Revit-free, MILLIMETRES ONLY.
//
// One SocketPlan per suggest_socket_points call, cached under
// "socket_plan:<guid>" so place_socket_points places the EXACT points the
// drafter reviewed. Mechanics are PlanCache.cs; this file is the DTOs plus a
// named facade, so App.cs and ElecPlanCaches keep one call site each.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One reviewable point, already resolved to absolute
    /// project-internal coordinates in mm.</summary>
    public sealed class PlannedPoint
    {
        /// <summary>Stable 0-based index — what the drafter confirms with.</summary>
        public int Index;
        public long RoomId;
        public string RoomName = "";
        public string LevelName = "";
        /// <summary>Local wall to host on; null when the boundary came from a
        /// Revit link and there is nothing local to host against.</summary>
        public long? HostWallId;
        /// <summary>"wall" | "unhosted"</summary>
        public string Host = "unhosted";
        public double XMm;
        public double YMm;
        /// <summary>Absolute project-internal Z, i.e. level elevation + room
        /// base offset + mount height.</summary>
        public double ZMm;
        /// <summary>Mount height above the room's finished floor. Carried
        /// separately from ZMm because hosted placement may have to set it as a
        /// parameter rather than through the insertion point.</summary>
        public double MountHeightMm;
        /// <summary>Unit vector pointing into the room.</summary>
        public double FacingDx;
        public double FacingDy;
        public double StationMm;
        public double WallLengthMm;
        public int LoopIndex;
    }

    /// <summary>Everything one suggest_socket_points run produced.</summary>
    public sealed class SocketPlan : PlanBase
    {
        public List<PlannedPoint> Points = new();
        /// <summary>The effective rule values this plan was built with, echoed
        /// back on the wire so any answer is auditable.</summary>
        public Dictionary<string, object?> ParamsUsed = new();
    }

    public static class SocketPlanCache
    {
        private static readonly PlanCache<SocketPlan> _cache =
            new("socket_plan:", "suggest_socket_points");

        public static string Store(SocketPlan plan, string docKey) => _cache.Store(plan, docKey);
        public static SocketPlan Get(string planId, string docKey) => _cache.Get(planId, docKey);
        public static void CloseAll() => _cache.CloseAll();
    }
}
