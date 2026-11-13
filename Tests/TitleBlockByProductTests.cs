#nullable enable
// Live R5 (B4): 4 sheets came back as 8 created things, 4 Sheets and 4 Schedules. The schedules were the title
// block family's revision schedules, which Revit adds with every new sheet. RevitWriteEngine.Classify now marks an
// ADDED ViewSchedule whose IsTitleblockRevisionSchedule is true as benign; benign ids are out of data.created /
// created_count, the touched and side-effect counts (so the created summary and the toast). The Revit call
// (doc.GetElement, IsTitleblockRevisionSchedule) can only be checked live; the decision and its effect are here.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Guard;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TitleBlockByProductTests
    {
        [Theory]
        [InlineData("ViewSchedule", true, true)]     // the title block's revision schedule
        [InlineData("ViewSchedule", false, false)]   // a schedule the drafter's code made
        [InlineData("ViewSheet", true, false)]
        [InlineData("FamilyInstance", true, false)]   // the title block instance itself is not this rule's business
        [InlineData(null, true, false)]
        public void OnlyARevisionScheduleOfATitleBlock_IsAByProduct(string? className, bool revisionSchedule, bool expected) =>
            Assert.Equal(expected, ChangeClassifier.IsTitleBlockByProduct(className, revisionSchedule));

        [Fact]
        public void ABenignByProduct_IsInNoCount()
        {
            // B4 in small: sheet 1, its revision schedule 2 (benign), a schedule the code made 3, a viewport 4.
            var all = new ChangeSet
            {
                Added = new List<long> { 1, 2, 3, 4 },
                CategoryOf = new Dictionary<long, string> { [1] = "Sheets", [2] = "Schedules", [3] = "Schedules", [4] = "Viewports" },
            };
            var benign = new long[] { 2 };
            var touched = ChangeClassifier.CodeTouched(all.Added.Where(id => !benign.Contains(id)).ToList(),
                Array.Empty<long>(), Array.Empty<long>(), new HashSet<long> { 1, 3 });
            var (t, side) = ChangeSetMath.Split(all, touched, benign);

            Assert.DoesNotContain(2L, t.Added.Select(r => r.Id));
            Assert.Contains(3L, t.Added.Select(r => r.Id));                       // the drafter's own schedule stays
            Assert.False(side.AddedByCategory.TryGetValue("Schedules", out _));  // not a side effect either
            Assert.Equal(1, side.AddedByCategory["Viewports"]);
        }
    }
}
