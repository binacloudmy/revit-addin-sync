// PlacedLedger: a door, window or room Revit deletes when the transaction commits (a failure
// resolution such as "can't cut instance out of its host") leaves the created ids and becomes
// a skip at its drawing position, with Revit's reason, instead of vanishing from the counts.

using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimPlacedLedgerTests
    {
        private static (BuildReport Report, PlacedLedger Ledger) Placed(BuildCategory category, params (long Id, double X, double Y)[] items)
        {
            var report = new BuildReport();
            var ledger = new PlacedLedger();
            foreach ((long id, double x, double y) in items)
            {
                List<long> ids = category == BuildCategory.Door ? report.DoorIds
                    : category == BuildCategory.Window ? report.WindowIds
                    : report.RoomIds;
                ids.Add(id);
                ledger.Add(id, category, x, y);
            }
            return (report, ledger);
        }

        [Fact]
        public void A_removed_door_becomes_a_skip_at_its_position_and_leaves_the_door_ids()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (1, 100, 200), (2, 6339, 14563), (3, 300, 400));
            var gone = new HashSet<long> { 2 };

            int removed = ledger.Settle(report, id => !gone.Contains(id));

            Assert.Equal(1, removed);
            Assert.Equal(new long[] { 1, 3 }, report.DoorIds);
            SkippedItem skip = Assert.Single(report.Skipped);
            Assert.Equal(BuildCategory.Door, skip.Category);
            Assert.Equal(SkipReason.RemovedAfterPlacing, skip.Reason);
            Assert.Equal(6339, skip.X);
            Assert.Equal(14563, skip.Y);
            Assert.Equal(1, report.SkippedOpenings);
        }

        [Theory]
        [InlineData(BuildCategory.Door)]
        [InlineData(BuildCategory.Window)]
        [InlineData(BuildCategory.Room)]
        public void Placed_equals_created_plus_removed(BuildCategory category)
        {
            var (report, ledger) = Placed(category, (10, 0, 0), (11, 1, 1), (12, 2, 2), (13, 3, 3));
            var gone = new HashSet<long> { 11, 13 };

            ledger.Settle(report, id => !gone.Contains(id));

            Assert.Equal(new long[] { 10, 12 }, report.IdsOf(category));
            Assert.Equal(4, report.IdsOf(category).Count + report.Skipped.Count(s => s.Category == category));
            Assert.All(report.Skipped, s => Assert.Equal(SkipReason.RemovedAfterPlacing, s.Reason));
            if (category == BuildCategory.Room)
            {
                Assert.Equal(2, report.SkippedRooms);
                Assert.Equal(0, report.SkippedOpenings);
            }
            else
            {
                Assert.Equal(2, report.SkippedOpenings);
                Assert.Equal(0, report.SkippedRooms);
            }
        }

        [Fact]
        public void A_cut_failure_reads_as_could_not_cut_it_out_of_its_wall()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (7, 6339, 14563));
            ledger.NoteFailure(7, "Can't cut instance out of its host.", cannotCut: true);

            ledger.Settle(report, id => false);

            SkipGroup group = Assert.Single(report.SkipGroups());
            Assert.Equal("1 door: Revit removed it: could not cut it out of its wall", group.CountText + ": " + group.ReasonText);
        }

        [Fact]
        public void Other_Revit_text_passes_through_without_its_full_stop()
        {
            var (report, ledger) = Placed(BuildCategory.Window, (8, 0, 0));
            ledger.NoteFailure(8, "  Instance doesn't fit in its host.  ", cannotCut: false);

            ledger.Settle(report, id => false);

            Assert.Equal("Revit removed it: Instance doesn't fit in its host", Assert.Single(report.SkipGroups()).ReasonText);
        }

        [Fact]
        public void No_note_reads_as_removed_after_placing()
        {
            var (report, ledger) = Placed(BuildCategory.Room, (9, 0, 0));

            ledger.Settle(report, id => false);

            SkipGroup group = Assert.Single(report.SkipGroups());
            Assert.Null(group.Detail);
            Assert.Equal("1 room: Revit removed it after placing", group.CountText + ": " + group.ReasonText);
        }

        [Fact]
        public void The_first_note_for_an_element_wins()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (7, 0, 0));
            ledger.NoteFailure(7, "Can't cut instance out of its host.", cannotCut: true);
            ledger.NoteFailure(7, "Something later.", cannotCut: false);

            ledger.Settle(report, id => false);

            Assert.Equal("could not cut it out of its wall", Assert.Single(report.Skipped).Detail);
            Assert.Equal("Can't cut instance out of its host.", ledger.FailureText(7));
        }

        [Fact]
        public void Two_windows_with_the_same_reason_are_one_row_with_both_positions()
        {
            var (report, ledger) = Placed(BuildCategory.Window, (21, 100, 0), (22, 900, 0), (23, 500, 500));
            ledger.NoteFailure(21, "Can't cut instance out of its host.", true);
            ledger.NoteFailure(22, "Can't cut instance out of its host.", true);

            ledger.Settle(report, id => id == 23);

            SkipGroup group = Assert.Single(report.SkipGroups());
            Assert.Equal("2 windows", group.CountText);
            Assert.Equal(new[] { 100.0, 900.0 }, group.Items.Select(i => i.X).OrderBy(x => x));
            Assert.Equal(new long[] { 23 }, report.WindowIds);
        }

        [Fact]
        public void A_second_settle_adds_nothing()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (1, 0, 0), (2, 0, 0));

            Assert.Equal(1, ledger.Settle(report, id => id == 1));
            Assert.Equal(0, ledger.Settle(report, id => id == 1));

            Assert.Single(report.Skipped);
            Assert.Equal(1, report.SkippedOpenings);
            Assert.Equal(new long[] { 1 }, report.DoorIds);
        }

        [Fact]
        public void A_note_for_an_element_not_placed_is_ignored()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (1, 0, 0));
            ledger.NoteFailure(999, "Can't cut instance out of its host.", true);   // the host wall, say

            Assert.Equal(0, ledger.Settle(report, id => id != 999));

            Assert.Empty(report.Skipped);
            Assert.Equal(new long[] { 1 }, report.DoorIds);
        }

        [Fact]
        public void Survivors_are_reported_to_no_one()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (1, 0, 0), (2, 0, 0));
            var heard = new List<long>();

            ledger.Settle(report, id => id == 1, (id, category, text) => heard.Add(id));

            Assert.Equal(new long[] { 2 }, heard);
        }

        [Theory]
        [InlineData("Can't cut instance out of its host.", true, "could not cut it out of its wall")]
        [InlineData("Instance doesn't fit.", false, "Instance doesn't fit")]
        [InlineData("   ", false, null)]
        [InlineData(null, false, null)]
        [InlineData(null, true, "could not cut it out of its wall")]
        public void Detail_words(string revitText, bool cannotCut, string expected)
        {
            Assert.Equal(expected, PlacedLedger.Detail(revitText, cannotCut));
        }
    }
}
