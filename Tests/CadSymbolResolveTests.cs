#nullable enable
// CadSymbolResolve: one drawn symbol, in model coordinates, onto a wall face. The model is a
// 150 mm wall along X from (0,0) to (5000,0) with a room on +Y, unless a test says otherwise.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class CadSymbolResolveTests
    {
        private static readonly RoomHit Office = new RoomHit { Id = 11, Name = "Office", FloorZMm = 0 };
        private static readonly RoomHit Store = new RoomHit { Id = 12, Name = "Store", FloorZMm = 0 };

        private static List<ModelWall> Wall() => new List<ModelWall>
        {
            new ModelWall { Id = 101, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, ThicknessMm = 150 },
        };

        private static RoomHit? OfficeAbove(double x, double y) => y > 75 ? Office : null;

        private static RoomHit? Both(double x, double y) => y > 75 ? Office : y < -75 ? Store : null;

        private static PlannedPoint Run(double x, double y, List<ModelWall>? walls = null,
                                        Func<double, double, RoomHit?>? roomAt = null, double? mount = 300,
                                        List<(double X, double Y, double Z)>? existing = null, double levelZ = 0)
        {
            walls = walls ?? Wall();
            return CadSymbolResolve.Resolve(
                new ResolveInput { XMm = x, YMm = y, Role = "socket", Host = "wall", MountHeightMm = mount },
                walls, CadSymbolResolve.Index(walls), roomAt ?? OfficeAbove, levelZ,
                existing ?? new List<(double X, double Y, double Z)>(), new ResolveOptions());
        }

        [Fact]
        public void Snaps_to_the_room_side_face_and_faces_into_the_room()
        {
            PlannedPoint p = Run(2000, 120);

            Assert.Equal(2000, p.XMm, 6);
            Assert.Equal(75, p.YMm, 6);
            Assert.Equal(0, p.FacingDx, 6);
            Assert.Equal(1, p.FacingDy, 6);
            Assert.Equal(101, p.HostWallId);
            Assert.Equal("wall", p.Host);
            Assert.Equal("Office", p.RoomName);
            Assert.Equal(11, p.RoomId);
            Assert.Equal(2000, p.StationMm, 6);
            Assert.Equal(5000, p.WallLengthMm, 6);
            Assert.Equal("cad", p.Source);
            Assert.Empty(p.Flags);
            Assert.False(p.Excluded);
        }

        [Fact]
        public void Symbol_on_the_other_side_faces_the_other_way()
        {
            PlannedPoint p = Run(2000, -120, roomAt: Both);

            Assert.Equal(-75, p.YMm, 6);
            Assert.Equal(-1, p.FacingDy, 6);
            Assert.Equal("Store", p.RoomName);
            Assert.Empty(p.Flags);
        }

        [Fact]
        public void Centre_on_the_centreline_uses_the_side_that_has_a_room()
        {
            var below = new Func<double, double, RoomHit?>((x, y) => y < -75 ? Store : null);

            PlannedPoint p = Run(2000, 5, roomAt: below);

            Assert.Equal(-75, p.YMm, 6);
            Assert.Equal(-1, p.FacingDy, 6);
            Assert.Equal("Store", p.RoomName);
            Assert.DoesNotContain("side_ambiguous", p.Flags);
        }

        [Fact]
        public void Rooms_on_both_sides_flag_side_ambiguous()
        {
            PlannedPoint p = Run(2000, -5, roomAt: Both);

            Assert.Contains("side_ambiguous", p.Flags);
            Assert.False(p.Excluded);
            Assert.Equal(75, p.YMm, 6);
            Assert.Equal(1, p.FacingDy, 6);
        }

        [Fact]
        public void Nearest_of_two_parallel_walls_wins()
        {
            List<ModelWall> walls = Wall();
            walls.Add(new ModelWall { Id = 102, X1 = 0, Y1 = 500, X2 = 5000, Y2 = 500, ThicknessMm = 150 });

            PlannedPoint p = Run(2000, 200, walls, (x, y) => y > 75 && y < 425 ? Office : null);

            Assert.Equal(101, p.HostWallId);
            Assert.Equal(75, p.YMm, 6);
        }

        [Fact]
        public void Past_the_wall_end_beyond_150mm_is_no_wall_in_reach()
        {
            PlannedPoint near = Run(5100, 120);
            Assert.Equal(101, near.HostWallId);
            Assert.Equal(5000, near.XMm, 6);

            PlannedPoint far = Run(5200, 120);
            Assert.Contains("no_wall_in_reach", far.Flags);
            Assert.Null(far.HostWallId);
        }

        [Fact]
        public void Beyond_reach_is_no_wall_in_reach_and_excluded()
        {
            PlannedPoint p = Run(2000, 600);

            Assert.Contains("no_wall_in_reach", p.Flags);
            Assert.True(p.Excluded);
            Assert.Equal("unhosted", p.Host);
            Assert.Equal(2000, p.XMm, 6);
            Assert.Equal(600, p.YMm, 6);
        }

        [Fact]
        public void Height_is_room_floor_plus_mount_height()
        {
            var raised = new RoomHit { Id = 13, Name = "Stage", FloorZMm = 3450 };

            PlannedPoint p = Run(2000, 120, roomAt: (x, y) => y > 75 ? raised : null, levelZ: 3000);

            Assert.Equal(3750, p.ZMm, 6);
            Assert.Equal(300, p.MountHeightMm, 6);
        }

        [Fact]
        public void No_room_uses_level_elevation_and_is_kept()
        {
            PlannedPoint p = Run(2000, 120, roomAt: (x, y) => null, levelZ: 3000);

            Assert.Contains("no_room", p.Flags);
            Assert.False(p.Excluded);
            Assert.Equal(3300, p.ZMm, 6);
            Assert.Equal(101, p.HostWallId);
        }

        [Fact]
        public void Wet_room_is_flagged_and_kept()
        {
            var toilet = new RoomHit { Id = 14, Name = "Toilet 1" };

            PlannedPoint p = Run(2000, 120, roomAt: (x, y) => y > 75 ? toilet : null);

            Assert.Contains("wet_room", p.Flags);
            Assert.False(p.Excluded);
        }

        [Fact]
        public void Existing_device_within_150mm_is_excluded()
        {
            PlannedPoint p = Run(2000, 120, existing: new List<(double X, double Y, double Z)> { (2100, 75, 300) });

            Assert.Contains("already_present", p.Flags);
            Assert.True(p.Excluded);
        }

        [Fact]
        public void Device_at_another_height_is_not_already_present()
        {
            PlannedPoint p = Run(2000, 120, existing: new List<(double X, double Y, double Z)> { (2000, 75, 1300) });

            Assert.DoesNotContain("already_present", p.Flags);
        }

        [Fact]
        public void Linked_wall_gives_an_unhosted_point()
        {
            List<ModelWall> walls = Wall();
            walls[0].Linked = true;

            PlannedPoint p = Run(2000, 120, walls);

            Assert.Contains("linked_wall", p.Flags);
            Assert.False(p.Excluded);
            Assert.Null(p.HostWallId);
            Assert.Equal("unhosted", p.Host);
            Assert.Equal(75, p.YMm, 6);
            Assert.Equal(1, p.FacingDy, 6);
        }

        [Fact]
        public void A_point_with_no_height_is_excluded()
        {
            PlannedPoint p = Run(2000, 120, mount: null);

            Assert.Contains("no_height", p.Flags);
            Assert.True(p.Excluded);
        }

        [Fact]
        public void A_host_none_device_stays_at_its_drawn_centre()
        {
            PlannedPoint p = CadSymbolResolve.Resolve(
                new ResolveInput { XMm = 2000, YMm = 1200, Role = "light", Host = "none", MountHeightMm = 2400 },
                Wall(), CadSymbolResolve.Index(Wall()), OfficeAbove, 0,
                new List<(double X, double Y, double Z)>(), new ResolveOptions());

            Assert.Equal(2000, p.XMm, 6);
            Assert.Equal(1200, p.YMm, 6);
            Assert.Equal(2400, p.ZMm, 6);
            Assert.Equal("unhosted", p.Host);
            Assert.Equal("Office", p.RoomName);
        }

        [Fact]
        public void Same_role_within_20mm_is_a_duplicate_and_excluded()
        {
            var points = new List<PlannedPoint>
            {
                new PlannedPoint { Role = "socket", XMm = 0, YMm = 0 },
                new PlannedPoint { Role = "socket", XMm = 15, YMm = 0 },
                new PlannedPoint { Role = "switch", XMm = 5, YMm = 0 },
                new PlannedPoint { Role = "socket", XMm = 300, YMm = 0 },
            };

            CadSymbolResolve.MarkDuplicates(points, new ResolveOptions());

            Assert.Equal(new[] { false, true, false, false }, points.Select(p => p.Flags.Contains("duplicate_in_drawing")));
            Assert.True(points[1].Excluded);
        }

        [Fact]
        public void A_symbol_inside_a_mapped_symbol_is_nested_in_mapped()
        {
            var symbols = new List<ScanSymbol>
            {
                new ScanSymbol { Key = "SOCKET-PAIR" },
                new ScanSymbol { Key = "SOCKET", Parent = 0 },
                new ScanSymbol { Key = "SOCKET" },
            };

            Assert.True(CadSymbolResolve.NestedInMapped(symbols, 1, key => key == "SOCKET-PAIR"));
            Assert.False(CadSymbolResolve.NestedInMapped(symbols, 1, key => key == "SOCKET"));
            Assert.False(CadSymbolResolve.NestedInMapped(symbols, 2, key => true));
        }

        [Fact]
        public void Every_flag_in_the_table_has_its_default()
        {
            Assert.Equal(
                new[] { "already_present", "duplicate_in_drawing", "family_host_mismatch", "no_ceiling", "no_height", "no_wall_in_reach" },
                CadSymbolResolve.FlagExcludes.Where(f => f.Value).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(
                new[] { "linked_wall", "no_room", "provisional_alignment", "side_ambiguous", "wet_room" },
                CadSymbolResolve.FlagExcludes.Where(f => !f.Value).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
