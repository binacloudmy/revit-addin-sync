#nullable enable
// r23 (AI-463, live 1eb9fd16 / a4777a83 on MRSM1, PTBR_Aras 01): "Tolong kira semua elemen mengikut kategori" counted rooms as placed 0 /
// redundant 90 / not enclosed 10, while the delete card (backend rooms plan, data/copilot/creates/rooms-plan.cs on fix/copilot-r23-cards)
// splits as area-tables-osc does: not placed (no Location) → placed (Area > 0) → redundant (RoomsInSameRegion / RoomsInSameRegionRooms
// warning) → not enclosed. RoomStatus.Classify now uses that exact order, so step 1 and the delete card agree.
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RoomClassesR23Tests
    {
        // The rooms plan's own rule, line 15 of rooms-plan.cs, written out for the comparison.
        private static string Plan(bool located, double area, bool sameRegion) =>
            !located ? "not placed" : area > 0 ? "placed" : sameRegion ? "redundant" : "not enclosed";

        [Fact]
        public void EveryCombination_MatchesTheBackendsRoomsPlan()
        {
            foreach (var located in new[] { false, true })
            foreach (var area in new[] { 0.0, -1.0, 0.0001, 12.5 })
            foreach (var redundant in new[] { false, true })
            foreach (var open in new[] { false, true })
                Assert.Equal(Plan(located, area, redundant), RoomStatus.Classify(located, area, redundant, open));
        }

        [Fact]
        public void APlacedRoomWithAnArea_IsNeverCountedRedundant_SoTheCountsAddUp()
        {
            // 100 rooms on the level: 30 with an area (some also named by a same-region warning), 53 redundant with no area, 10 not
            // enclosed, 7 not placed. Every room falls in exactly one class and the classes add up to the total.
            var rooms = Enumerable.Range(0, 100).Select(i =>
                i < 7 ? (located: false, area: 0.0, redundant: false)
                : i < 37 ? (located: true, area: 10.0 + i, redundant: i % 3 == 0)
                : i < 90 ? (located: true, area: 0.0, redundant: true)
                : (located: true, area: 0.0, redundant: false)).ToList();
            var counts = rooms.GroupBy(r => RoomStatus.Classify(r.located, r.area, r.redundant, notEnclosedWarning: !r.redundant && r.area <= 0))
                              .ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(7, counts[RoomStatus.NotPlaced]);
            Assert.Equal(30, counts[RoomStatus.Placed]);
            Assert.Equal(53, counts[RoomStatus.Redundant]);
            Assert.Equal(10, counts[RoomStatus.NotEnclosed]);
            Assert.Equal(100, counts.Values.Sum());
        }

        [Fact]
        public void BothSameRegionWarnings_MarkARoomRedundant_AsInTheRoomsPlan()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var find = File.ReadAllText(Path.Combine(dir!.FullName, "BinaVibe", "Copilot", "Read", "FindExecutor.cs"));
            Assert.Contains("BuiltInFailures.RoomFailures.RoomsInSameRegionRooms", find);
            Assert.Contains("BuiltInFailures.RoomFailures.RoomsInSameRegion;", find);
            Assert.Contains("id == sameRegion || id == sameRegionToo ? redundant", find);
        }
    }
}
