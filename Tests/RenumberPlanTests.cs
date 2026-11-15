// AI-220: renumber_elements as a plan. The approval card (dry run) and the real
// write read the same plan, so these rules are what both do.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.BulkEdit;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RenumberPlanTests
    {
        private static RenumberRow Row(long id, string current = "", bool writable = true, string room = null) =>
            new RenumberRow { Id = id, Current = current, Writable = writable, RoomNumber = room };

        [Fact]
        public void InOrder_NumbersFromStart_WithThePrefix()
        {
            var plan = RenumberPlan.Build(new[] { Row(10), Row(20), Row(30) }, byRoom: false, start: 5, prefix: "D-");
            Assert.Equal(new[] { "D-5", "D-6", "D-7" }, plan.Changes.Select(c => c.To));
            Assert.Equal(new long[] { 10, 20, 30 }, plan.Changes.Select(c => c.Id));
            Assert.Equal(3, plan.Matched);
        }

        [Fact]
        public void AnElementThatCannotBeWritten_KeepsItsPlace_SoOthersDoNotShift()
        {
            var plan = RenumberPlan.Build(new[] { Row(1), Row(2, writable: false), Row(3) }, false, 1, "");
            Assert.Equal(new[] { "1", "3" }, plan.Changes.Select(c => c.To));
            Assert.Equal(1, plan.NotWritable);
            Assert.Equal(3, plan.Matched);
        }

        [Fact]
        public void ByRoom_UsesTheRoomNumber_AndAddsLettersWhenARoomHasSeveral()
        {
            var plan = RenumberPlan.Build(new[]
            {
                Row(1, room: "R1"), Row(2, room: "R1"), Row(3, room: "R2"), Row(4, room: "R1"), Row(5, room: null), Row(6, room: "  "),
            }, byRoom: true, start: 1, prefix: "D");
            Assert.Equal(new[] { "DR1", "DR1A", "DR2", "DR1B" }, plan.Changes.Select(c => c.To));
            Assert.Equal(2, plan.SkippedNoRoom);
            Assert.Equal(6, plan.Matched);
        }

        [Fact]
        public void ByRoom_AnUnwritableElementStillTakesItsRoomSlot()
        {
            var plan = RenumberPlan.Build(new[] { Row(1, writable: false, room: "R1"), Row(2, room: "R1") }, true, 1, "");
            Assert.Single(plan.Changes);
            Assert.Equal("R1A", plan.Changes[0].To);     // same as the write loop before the plan existed
            Assert.Equal(1, plan.NotWritable);
        }

        [Fact]
        public void Preview_CountsWhatReplacesAndWhatStaysTheSame()
        {
            var plan = RenumberPlan.Build(new[] { Row(1, "1"), Row(2, ""), Row(3, "x") }, false, 1, "");
            var p = plan.ToPreview();
            Assert.Equal(true, p["ok"]);
            Assert.Equal(true, p["dry_run"]);
            Assert.Equal(3, p["matched"]);
            Assert.Equal(3, p["would_set"]);
            Assert.Equal(1, p["unchanged"]);              // "1" stays "1"
            Assert.Equal(1, p["replaces_existing"]);      // "x" is overwritten; "" and "1" are not replacements
            Assert.Equal("3", p["last_mark"]);
            Assert.Equal(false, p["nothing"]);
        }

        [Fact]
        public void Preview_ListsOnlyTheFirstFewMarks_AndTheLastOne()
        {
            var plan = RenumberPlan.Build(Enumerable.Range(1, 46).Select(i => Row(i)).ToList(), false, 1, "");
            var p = plan.ToPreview();
            var first = (List<object>)p["first"];
            Assert.Equal(8, first.Count);
            Assert.Equal("1", ((Dictionary<string, object>)first[0])["to"]);
            Assert.Equal("8", ((Dictionary<string, object>)first[7])["to"]);
            Assert.Equal("46", p["last_mark"]);
            Assert.Equal(46, p["would_set"]);
        }

        [Fact]
        public void Nothing_InScope_IsAnEmptyPlan()
        {
            foreach (var rows in new IReadOnlyList<RenumberRow>[] { null, new List<RenumberRow>() })
            {
                var plan = RenumberPlan.Build(rows, false, 1, "");
                var p = plan.ToPreview();
                Assert.Equal(0, plan.Matched);
                Assert.Empty(plan.Changes);
                Assert.Equal(true, p["nothing"]);
                Assert.Null(p["last_mark"]);
            }
        }
    }
}
