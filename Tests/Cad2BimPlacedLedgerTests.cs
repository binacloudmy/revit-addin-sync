// PlacedLedger: a door, window or room Revit deletes when the transaction commits (a failure
// resolution such as "can't cut instance out of its host") leaves the created ids and becomes
// a skip at its drawing position, with Revit's reason, instead of vanishing from the counts.

using System;
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
            ledger.NoteFailure(7, "Can't cut instance out of its host.", FailureKind.CannotCut);

            ledger.Settle(report, id => false);

            SkipGroup group = Assert.Single(report.SkipGroups());
            Assert.Equal("1 door: Revit removed it: could not cut it out of its wall", group.CountText + ": " + group.ReasonText);
        }

        [Fact]
        public void A_cannot_make_type_failure_reads_as_family_cannot_be_built()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (7, 6339, 14563));
            ledger.NoteFailure(7, "Can't make type \"M_Single-Flush: 0915 x 2134mm\".",
                PlacedLedger.KindOf(new Guid(PlacedLedger.CannotMakeTypeGuid)));

            ledger.Settle(report, id => false);

            SkipGroup group = Assert.Single(report.SkipGroups());
            Assert.Equal("1 door: Revit removed it: its family cannot be built in this wall (try another door type in TYPES)",
                group.CountText + ": " + group.ReasonText);
        }

        [Fact]
        public void A_forgotten_element_leaves_without_a_skip()
        {
            // The door Revit removed is about to be placed again: no skip for the first attempt.
            var (report, ledger) = Placed(BuildCategory.Door, (7, 6339, 14563), (8, 0, 0));
            ledger.NoteFailure(7, "Can't make type.", FailureKind.CannotMakeType);
            report.DoorIds.Remove(7);

            Assert.True(ledger.Forget(7));
            Assert.False(ledger.Forget(7));
            Assert.Null(ledger.FailureText(7));
            Assert.Equal(1, ledger.Count);

            Assert.Equal(0, ledger.Settle(report, id => id == 8));
            Assert.Equal(0, ledger.Settle(report, id => id == 8));
            Assert.Empty(report.Skipped);
            Assert.Equal(new long[] { 8 }, report.DoorIds);
        }

        [Fact]
        public void Gone_with_lists_only_the_removed_elements_of_that_kind_and_category()
        {
            var (report, ledger) = Placed(BuildCategory.Door, (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0));
            ledger.Add(5, BuildCategory.Window, 0, 0);
            ledger.NoteFailure(1, "Can't make type.", FailureKind.CannotMakeType);   // gone: listed
            ledger.NoteFailure(2, "Can't make type.", FailureKind.CannotMakeType);   // still there
            ledger.NoteFailure(3, "Can't cut.", FailureKind.CannotCut);              // gone, other kind
            ledger.NoteFailure(5, "Can't make type.", FailureKind.CannotMakeType);   // a window
            // 4: gone with no note

            Assert.Equal(new long[] { 1 },
                ledger.GoneWith(FailureKind.CannotMakeType, BuildCategory.Door, id => id == 2));
            Assert.Equal(5, ledger.Count);   // nothing settled
            Assert.Empty(ledger.GoneWith(FailureKind.CannotMakeType, BuildCategory.Door, null));
        }

        [Fact]
        public void Failure_kinds_come_from_their_definition_ids()
        {
            Assert.Equal(FailureKind.CannotCut, PlacedLedger.KindOf(new Guid(PlacedLedger.CannotCutGuid)));
            Assert.Equal(FailureKind.CannotMakeType, PlacedLedger.KindOf(new Guid(PlacedLedger.CannotMakeTypeGuid)));
            Assert.Equal(FailureKind.Other, PlacedLedger.KindOf(Guid.NewGuid()));
        }

        [Fact]
        public void Other_Revit_text_passes_through_without_its_full_stop()
        {
            var (report, ledger) = Placed(BuildCategory.Window, (8, 0, 0));
            ledger.NoteFailure(8, "  Instance doesn't fit in its host.  ", FailureKind.Other);

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
            ledger.NoteFailure(7, "Can't cut instance out of its host.", FailureKind.CannotCut);
            ledger.NoteFailure(7, "Something later.", FailureKind.Other);

            ledger.Settle(report, id => false);

            Assert.Equal("could not cut it out of its wall", Assert.Single(report.Skipped).Detail);
            Assert.Equal("Can't cut instance out of its host.", ledger.FailureText(7));
        }

        [Fact]
        public void Two_windows_with_the_same_reason_are_one_row_with_both_positions()
        {
            var (report, ledger) = Placed(BuildCategory.Window, (21, 100, 0), (22, 900, 0), (23, 500, 500));
            ledger.NoteFailure(21, "Can't cut instance out of its host.", FailureKind.CannotCut);
            ledger.NoteFailure(22, "Can't cut instance out of its host.", FailureKind.CannotCut);

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
            ledger.NoteFailure(999, "Can't cut instance out of its host.", FailureKind.CannotCut);   // the host wall, say

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
        [InlineData("Can't cut instance out of its host.", FailureKind.CannotCut, "could not cut it out of its wall")]
        [InlineData("Instance doesn't fit.", FailureKind.Other, "Instance doesn't fit")]
        [InlineData("   ", FailureKind.Other, null)]
        [InlineData(null, FailureKind.Other, null)]
        [InlineData(null, FailureKind.CannotCut, "could not cut it out of its wall")]
        [InlineData("Can't make type.", FailureKind.CannotMakeType, "its family cannot be built in this wall (try another door type in TYPES)")]
        [InlineData(null, FailureKind.CannotMakeType, "its family cannot be built in this wall (try another door type in TYPES)")]
        public void Detail_words(string revitText, FailureKind kind, string expected)
        {
            Assert.Equal(expected, PlacedLedger.Detail(revitText, kind));
        }
    }
}
