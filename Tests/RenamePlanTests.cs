// Rename planner — Revit-free (bina-ai R2 Task 21, naming pack).
//
// rename_elements' preview must show EXACT old/new pairs and the collisions
// Revit would refuse, BEFORE anything is renamed; apply must skip collisions
// up front (never "try and see"). RenamePlan is the pure core both paths use.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Naming;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RenamePlanTests
    {
        private static List<(long id, string name)> Names(params string[] names) =>
            names.Select((n, i) => ((long)(i + 1), n)).ToList();

        [Fact]
        public void Plan_ProducesExactOldNewPairs_ForMatchesOnly()
        {
            var plan = RenamePlan.Build(Names("jkrAR18-D1", "jkrAR18-D2", "Other"), "jkrAR18", "jkrAR25");
            Assert.Equal(2, plan.Renames.Count);
            Assert.Equal(("jkrAR18-D1", "jkrAR25-D1"), (plan.Renames[0].From, plan.Renames[0].To));
            Assert.Empty(plan.Collisions);
            Assert.Equal(2, plan.WouldRename);
        }

        [Fact]
        public void Collision_WhenTargetNameAlreadyExists_AndIsNotItselfRenamed()
        {
            var plan = RenamePlan.Build(Names("jkrAR18-D1", "jkrAR25-D1"), "jkrAR18", "jkrAR25");
            Assert.Empty(plan.Renames);
            var c = Assert.Single(plan.Collisions);
            Assert.Equal(("jkrAR18-D1", "jkrAR25-D1"), (c.From, c.To));
            Assert.Contains("already exists", c.Reason);
        }

        [Fact]
        public void NoCollision_WhenTheExistingNameIsItselfBeingRenamedAway()
        {
            // A-1 → A-2 while A-2 → A-3: chain is fine, nothing ends up duplicated.
            var plan = RenamePlan.Build(Names("A-1", "A-2"), "A-", "B-");
            Assert.Equal(2, plan.Renames.Count);
            Assert.Empty(plan.Collisions);
        }

        [Fact]
        public void Collision_WhenTwoSourcesMapToTheSameTarget()
        {
            var plan = RenamePlan.Build(Names("X-01", "X01"), "-", "");   // "X-01" → "X01"; "X01" has no '-' so it stays
            Assert.Empty(plan.Renames);                                    // the only candidate collides with the untouched "X01"
            Assert.Single(plan.Collisions);
            Assert.Contains("already exists", plan.Collisions[0].Reason);
            var dup = RenamePlan.Build(Names("A-B-1", "A--B-1"), "-", "");  // both → "AB1"
            Assert.Equal(1, dup.Renames.Count);
            Assert.Single(dup.Collisions);
            Assert.Contains("duplicate", dup.Collisions[0].Reason);
        }

        [Fact]
        public void EmptyOrUnchangedResults_AreSkippedSilently()
        {
            var plan = RenamePlan.Build(Names("jkrAR18", "keep"), "jkrAR18", "");
            Assert.Empty(plan.Renames);       // would become empty → not a rename
            Assert.Empty(plan.Collisions);
        }

        [Fact]
        public void Preview_IsCappedButCountsAreExact()
        {
            var many = Enumerable.Range(0, 300).Select(i => $"jkrAR18-{i}").ToArray();
            var plan = RenamePlan.Build(Names(many), "jkrAR18", "jkrAR25");
            var preview = plan.ToPreview(cap: 200);
            Assert.Equal(300, preview["would_rename"]);
            Assert.Equal(200, ((IEnumerable<object>)preview["preview"]).Count());
            Assert.Equal(true, preview["preview_truncated"]);
            Assert.Empty((IEnumerable<object>)preview["collisions"]);
        }
    }
}
