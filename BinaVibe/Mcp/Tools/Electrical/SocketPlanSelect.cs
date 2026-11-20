#nullable enable
// Which points of a stored plan place_socket_points places, and with which family type.
// Pure, so the rules are tested without Revit.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class SocketPlanSelect
    {
        /// <summary>
        /// Explicit indices win, even over excluded points: the drafter has looked at them.
        /// Otherwise every point not excluded, of the given roles (none given: every role).
        /// A rule plan's points carry no role and are all sockets.
        /// </summary>
        public static List<PlannedPoint> Select(SocketPlan plan, IReadOnlyCollection<long> indices,
                                                IReadOnlyCollection<string> roles)
        {
            if (indices.Count > 0)
            {
                var wanted = new HashSet<long>(indices);
                return plan.Points.Where(p => wanted.Contains(p.Index)).ToList();
            }

            var roleSet = new HashSet<string>(roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
                                              StringComparer.OrdinalIgnoreCase);
            return plan.Points
                .Where(p => !p.Excluded)
                .Where(p => roleSet.Count == 0 || roleSet.Contains(RoleOf(p)))
                .ToList();
        }

        /// <summary>How many excluded points of the given roles a default selection leaves out;
        /// none when indices are explicit.</summary>
        public static int SkippedExcluded(SocketPlan plan, IReadOnlyCollection<long> indices,
                                          IReadOnlyCollection<string> roles)
        {
            if (indices.Count > 0) return 0;
            var roleSet = new HashSet<string>(roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
                                              StringComparer.OrdinalIgnoreCase);
            return plan.Points.Count(p => p.Excluded && (roleSet.Count == 0 || roleSet.Contains(RoleOf(p))));
        }

        /// <summary>The point's own family type wins over the argument; null when neither
        /// names one.</summary>
        public static string? FamilyTypeFor(PlannedPoint p, string? argFamilyType)
        {
            if (!string.IsNullOrWhiteSpace(p.FamilyType)) return p.FamilyType;
            return string.IsNullOrWhiteSpace(argFamilyType) ? null : argFamilyType;
        }

        /// <summary>
        /// The runs route_conduit routes: explicit indices win, even over excluded runs;
        /// otherwise every run not excluded.
        /// </summary>
        public static List<PlannedRun> SelectRuns(SocketPlan plan, IReadOnlyCollection<long> runIndices)
        {
            if (runIndices.Count > 0)
            {
                var wanted = new HashSet<long>(runIndices);
                return plan.Runs.Where(r => wanted.Contains(r.Index)).ToList();
            }
            return plan.Runs.Where(r => !r.Excluded).ToList();
        }

        private static string RoleOf(PlannedPoint p) => p.Role.Length == 0 ? "socket" : p.Role;
    }
}
