// Documentation planners — Revit-free (bina-ai R2 Task 25, tags + schedules).

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Documentation;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DocumentationPlanTests
    {
        private static TagRow R(long id, bool tagged = false, bool grouped = false, bool hasLocation = true) =>
            new() { Id = id, AlreadyTagged = tagged, Grouped = grouped, HasLocation = hasLocation };

        [Fact]
        public void TagPlan_CountsUntaggedAndEverySkipReason()
        {
            var plan = TagPlan.Build("Doors", new[] { R(1), R(2, tagged: true), R(3, grouped: true), R(4, hasLocation: false), R(5) }, tagFamilyLoaded: true);
            Assert.Equal(new long[] { 1, 5 }, plan.ToTag);
            Assert.Equal(1, plan.AlreadyTagged); Assert.Equal(1, plan.GroupedSkipped); Assert.Equal(1, plan.NoLocation);
            Assert.Equal(5, plan.Matched);
            Assert.Equal(plan.Matched, plan.ToTag.Count + plan.AlreadyTagged + plan.GroupedSkipped + plan.NoLocation);
            Assert.Empty(plan.Risks);
        }

        [Fact]
        public void TagPlan_MissingTagFamily_IsARisk_NotASilentZero()
        {
            var plan = TagPlan.Build("Doors", new[] { R(1) }, tagFamilyLoaded: false);
            Assert.Single(plan.ToTag);
            Assert.Contains(plan.Risks, r => r.Kind == "no_tag_family");
        }

        [Fact]
        public void TagVerification_ComparesExpectedAgainstTaggedSet()
        {
            var v = TagPlan.Verify(new long[] { 1, 5 }, new HashSet<long> { 1 });
            Assert.Equal(2, v["expected"]); Assert.Equal(1, v["now_tagged"]);
            Assert.Single((List<object>)v["mismatches"]!);
        }

        [Fact]
        public void SchedulePlan_ResolvesCaseInsensitiveThenContains_AndNamesUnresolved()
        {
            var available = new[] { "Mark", "Family and Type", "Width", "Height", "Fire Rating", "Level" };
            var plan = SchedulePlan.Build("Doors", new[] { "mark", "Family", "Fire Ratng", "Height" }, available, existingNames: new[] { "Door Schedule" });
            Assert.Equal(new[] { "Mark", "Family and Type", "Height" }, plan.Resolved);
            Assert.Equal(new[] { "Fire Ratng" }, plan.Unresolved);
            Assert.True(plan.WouldCreate);
        }

        [Fact]
        public void SchedulePlan_DefaultsFields_WhenNoneRequested_AndUniqueName()
        {
            var available = new[] { "Mark", "Family and Type", "Width", "Height", "Level", "Comments" };
            var plan = SchedulePlan.Build("Doors", null, available, existingNames: new[] { "Door Schedule", "Door Schedule 2" });
            Assert.NotEmpty(plan.Resolved);
            Assert.Equal("Door Schedule 3", plan.ProposedName);
            Assert.Equal(true, plan.ToPreview()["would_create"]);
        }
    }
}
