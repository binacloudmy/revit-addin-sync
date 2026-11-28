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

        private static FoundRow Room(long id, string level, string status) => new FoundRow
        {
            Ref = new BinaVibe.Copilot.Contracts.ElementRef(id, null), Category = "Rooms", Level = level, RoomStatus = status,
            AreaInternal = status == RoomStatus.Placed ? 10 / 0.09290304 : 0,
        };

        private static System.Text.Json.JsonElement RoomsOf(bool inView, int? notPlacedOnLevel)
        {
            Assert.True(QueryParser.TryParse(System.Text.Json.JsonDocument.Parse(inView ? "{\"queries\":[{\"category\":\"Rooms\",\"in_view\":true}]}" : "{\"queries\":[{\"category\":\"Rooms\"}]}").RootElement.Clone(), out var plan, out var error), error);
            var o = new FindOutcome { Sources = { "host" }, Categories = { "Rooms" }, Handle = "h_00000001", AreaUnit = "m²", InView = inView, RoomsNotPlacedOnLevel = notPlacedOnLevel };
            // P13 C2 36f24f81 shape: the view shows rooms of PTBR_Aras 01 and of another level (all redundant there).
            var id = 1L;
            for (var i = 0; i < 3; i++) o.Rows.Add(Room(id++, "PTBR_Aras 01", RoomStatus.Placed));
            for (var i = 0; i < 2; i++) o.Rows.Add(Room(id++, "PTBR_Aras 01", RoomStatus.Redundant));
            o.Rows.Add(Room(id++, "PTBR_Aras 01", RoomStatus.NotEnclosed));
            for (var i = 0; i < 4; i++) o.Rows.Add(Room(id++, "PTBR_Aras 00", RoomStatus.Redundant));
            o.Queries.Add(new QueryOutcome { Count = o.Rows.Count, Filter = "category = Rooms", Subject = "Rooms" });
            return FindResultBuilder.Build(plan!, o).Data!.Value.GetProperty("rooms");
        }

        [Fact]
        public void AnInViewRoomCount_IsSplitByLevel_AndSaysTheUnplacedRoomsOnTheViewsLevel()
        {
            var rooms = RoomsOf(inView: true, notPlacedOnLevel: 8);
            Assert.Equal(3, rooms.GetProperty("placed").GetInt32());
            Assert.Equal(6, rooms.GetProperty("redundant").GetInt32());
            var by = rooms.GetProperty("by_level");
            var a1 = by.GetProperty("PTBR_Aras 01");
            Assert.Equal(3, a1.GetProperty("placed").GetInt32());
            Assert.Equal(2, a1.GetProperty("redundant").GetInt32());
            Assert.Equal(1, a1.GetProperty("not_enclosed").GetInt32());
            var a0 = by.GetProperty("PTBR_Aras 00");
            Assert.Equal(0, a0.GetProperty("placed").GetInt32());
            Assert.Equal(4, a0.GetProperty("redundant").GetInt32());
            Assert.Equal(8, rooms.GetProperty("not_placed_on_level").GetInt32());
            // The view's own level adds up with the level plan: 8 not placed + 3 placed + 2 redundant + 1 not enclosed.
            Assert.Equal(14, 8 + a1.GetProperty("placed").GetInt32() + a1.GetProperty("redundant").GetInt32() + a1.GetProperty("not_enclosed").GetInt32());
        }

        [Fact]
        public void AFindNotInAView_KeepsItsRoomTotalsAsBefore()
        {
            var rooms = RoomsOf(inView: false, notPlacedOnLevel: null);
            Assert.False(rooms.TryGetProperty("by_level", out _));
            Assert.False(rooms.TryGetProperty("not_placed_on_level", out _));
            Assert.Equal(3, rooms.GetProperty("placed").GetInt32());
            // In a view with no level (a 3D view) the unplaced count is not sent, the split still is.
            var noLevel = RoomsOf(inView: true, notPlacedOnLevel: null);
            Assert.True(noLevel.TryGetProperty("by_level", out _));
            Assert.False(noLevel.TryGetProperty("not_placed_on_level", out _));
        }

        [Fact]
        public void TheExecutor_CountsUnplacedRoomsOnTheViewsGenLevel_DocumentWide()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var find = File.ReadAllText(Path.Combine(dir!.FullName, "BinaVibe", "Copilot", "Read", "FindExecutor.cs")).Replace("\r\n", "\n");
            Assert.Contains("outcome.InView = plan.Queries.Any(q => q.InView);", find);
            Assert.Contains("var level = view?.GenLevel;", find);
            Assert.Contains("new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()", find);
            Assert.Contains(".Count(r => r.Location == null && r.Level != null && r.Level.Id == level.Id);", find);
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
