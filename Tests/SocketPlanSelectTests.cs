#nullable enable
// SocketPlanSelect: which plan points a placement takes, and which family type each uses.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class SocketPlanSelectTests
    {
        private static SocketPlan Plan()
        {
            var plan = new SocketPlan { Source = "cad" };
            plan.Points.Add(new PlannedPoint { Index = 0, Role = "socket", FamilyType = "Socket Outlet : 13A Single" });
            plan.Points.Add(new PlannedPoint { Index = 1, Role = "socket", Excluded = true, Flags = { "already_present" } });
            plan.Points.Add(new PlannedPoint { Index = 2, Role = "switch", FamilyType = "Switch : 1 Gang" });
            plan.Points.Add(new PlannedPoint { Index = 3, Role = "light", Excluded = true, Flags = { "no_ceiling" } });
            return plan;
        }

        private static int[] Indices(IEnumerable<PlannedPoint> points) => points.Select(p => p.Index).ToArray();

        [Fact]
        public void Default_selection_skips_excluded_points()
        {
            Assert.Equal(new[] { 0, 2 }, Indices(SocketPlanSelect.Select(Plan(), new long[0], new string[0])));
        }

        [Fact]
        public void Indices_override_exclusion()
        {
            Assert.Equal(new[] { 1, 2 }, Indices(SocketPlanSelect.Select(Plan(), new long[] { 2, 1, 9 }, new[] { "light" })));
        }

        [Fact]
        public void Roles_filter_applies()
        {
            Assert.Equal(new[] { 2 }, Indices(SocketPlanSelect.Select(Plan(), new long[0], new[] { "SWITCH" })));
            Assert.Empty(SocketPlanSelect.Select(Plan(), new long[0], new[] { "light" }));
        }

        [Fact]
        public void Skipped_excluded_counts_the_roles_asked_for_and_not_explicit_indices()
        {
            Assert.Equal(2, SocketPlanSelect.SkippedExcluded(Plan(), new long[0], new string[0]));
            Assert.Equal(1, SocketPlanSelect.SkippedExcluded(Plan(), new long[0], new[] { "socket" }));
            Assert.Equal(0, SocketPlanSelect.SkippedExcluded(Plan(), new long[] { 0 }, new string[0]));
        }

        [Fact]
        public void A_rule_plans_points_are_sockets()
        {
            var plan = new SocketPlan();
            plan.Points.Add(new PlannedPoint { Index = 0 });

            Assert.Single(SocketPlanSelect.Select(plan, new long[0], new[] { "socket" }));
            Assert.Empty(SocketPlanSelect.Select(plan, new long[0], new[] { "switch" }));
        }

        [Fact]
        public void Point_family_type_wins_over_the_argument()
        {
            SocketPlan plan = Plan();
            Assert.Equal("Socket Outlet : 13A Single", SocketPlanSelect.FamilyTypeFor(plan.Points[0], "Socket Outlet : 13A Twin"));
            Assert.Equal("Socket Outlet : 13A Twin", SocketPlanSelect.FamilyTypeFor(plan.Points[1], "Socket Outlet : 13A Twin"));
        }

        [Fact]
        public void No_type_anywhere_is_null()
        {
            Assert.Null(SocketPlanSelect.FamilyTypeFor(Plan().Points[1], null));
            Assert.Null(SocketPlanSelect.FamilyTypeFor(Plan().Points[1], "  "));
        }
    }
}
