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
        public void Height_is_level_plus_mount_height()
        {
            var room = new RoomHit { Id = 13, Name = "Office", FloorZMm = 3000 };

            PlannedPoint p = Run(2000, 120, roomAt: (x, y) => y > 75 ? room : null, levelZ: 3000);

            Assert.Equal(3300, p.ZMm, 6);
            Assert.Equal(300, p.MountHeightMm, 6);
            Assert.Equal(13, p.RoomId);
        }

        [Fact]
        public void A_room_lower_offset_does_not_move_the_height()
        {
            // The room's floor sits 100 mm under its level; legend heights are from the level.
            var sunk = new RoomHit { Id = 13, Name = "Office", FloorZMm = 2900 };

            PlannedPoint p = Run(2000, 120, roomAt: (x, y) => y > 75 ? sunk : null, levelZ: 3000);

            Assert.Equal(3300, p.ZMm, 6);
            Assert.Equal("Office", p.RoomName);
        }

        [Fact]
        public void A_room_on_another_level_still_uses_the_target_level()
        {
            // e.g. a linked model's room whose floor is at a different elevation.
            var other = new RoomHit { Id = 13, Name = "Office", FloorZMm = 6450, Linked = true };

            PlannedPoint p = Run(2000, 120, roomAt: (x, y) => y > 75 ? other : null, levelZ: 3000);

            Assert.Equal(3300, p.ZMm, 6);
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
        public void A_point_over_the_top_of_its_wall_is_flagged_and_kept()
        {
            var lowWall = new List<ModelWall> { new ModelWall { Id = 101, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, ThicknessMm = 150, TopZMm = 1200 } };

            PlannedPoint high = Run(2000, 120, walls: lowWall, mount: 1450);
            Assert.Contains("above_wall_top", high.Flags);
            Assert.False(high.Excluded);
            Assert.Equal(101, high.HostWallId);

            PlannedPoint low = Run(2000, 120, walls: lowWall, mount: 300);
            Assert.DoesNotContain("above_wall_top", low.Flags);
            // A wall whose top was not read never flags it.
            Assert.DoesNotContain("above_wall_top", Run(2000, 120, mount: 2300).Flags);
        }

        [Fact]
        public void A_point_on_a_linked_wall_is_never_above_its_top()
        {
            var linked = new List<ModelWall> { new ModelWall { Id = 101, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, ThicknessMm = 150, TopZMm = 1200, Linked = true } };
            Assert.DoesNotContain("above_wall_top", Run(2000, 120, walls: linked, mount: 2300).Flags);
        }

        [Fact]
        public void Already_present_is_within_150_in_plan_and_in_z()
        {
            // 140 in plan and 140 in Z: 198 mm apart in 3D, the same device.
            PlannedPoint p = Run(2000, 120, existing: new List<(double X, double Y, double Z)> { (2140, 75, 440) });
            Assert.Contains("already_present", p.Flags);
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
                new[] { "above_wall_top", "linked_wall", "no_room", "provisional_alignment", "side_ambiguous", "wet_room" },
                CadSymbolResolve.FlagExcludes.Where(f => !f.Value).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        }

        // ---------------------------------------------------------------- structural walls

        /// <summary>A 200 mm core wall along X with a 20 mm finish wall on its +Y face, and three
        /// more 200 mm walls well away from it.</summary>
        private static List<ModelWall> CoreAndFinish() => new List<ModelWall>
        {
            new ModelWall { Id = 201, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, ThicknessMm = 200 },
            new ModelWall { Id = 202, X1 = 0, Y1 = 110, X2 = 5000, Y2 = 110, ThicknessMm = 20 },
            new ModelWall { Id = 203, X1 = 0, Y1 = 0, X2 = 0, Y2 = -6000, ThicknessMm = 200 },
            new ModelWall { Id = 204, X1 = 5000, Y1 = 0, X2 = 5000, Y2 = -6000, ThicknessMm = 200 },
            new ModelWall { Id = 205, X1 = 0, Y1 = -6000, X2 = 5000, Y2 = -6000, ThicknessMm = 200 },
        };

        [Fact]
        public void Membership_is_by_footprint_not_cluster()
        {
            // Inside the footprint: in the plan, even from another cluster (stacked or split plans).
            Assert.Equal("plan", CadSymbolResolve.Membership(3, 0, true));
            Assert.Equal("plan", CadSymbolResolve.Membership(0, 0, true));
            // Outside: in the chosen cluster it is counted; elsewhere it is another plan's.
            Assert.Equal("outside_model_footprint", CadSymbolResolve.Membership(0, 0, false));
            Assert.Equal("other_plan", CadSymbolResolve.Membership(2, 0, false));
            Assert.Equal("other_plan", CadSymbolResolve.Membership(-1, 0, false));
        }

        [Fact]
        public void Thin_walls_are_not_structural()
        {
            List<ModelWall> walls = CadSymbolResolve.Structural(CoreAndFinish());

            Assert.Equal(new long[] { 201, 203, 204, 205 }, walls.Select(w => w.Id));
            Assert.Equal(50, CadSymbolResolve.MinStructuralWallMm);
            Assert.Equal(5, CadSymbolResolve.Structural(CoreAndFinish(), 10).Count);
        }

        [Fact]
        public void A_level_of_only_thin_walls_keeps_them()
        {
            List<ModelWall> thin = CoreAndFinish().Select(w => new ModelWall
            {
                Id = w.Id, X1 = w.X1, Y1 = w.Y1, X2 = w.X2, Y2 = w.Y2, ThicknessMm = 30,
            }).ToList();
            thin[0].ThicknessMm = 200;

            Assert.Equal(5, CadSymbolResolve.Structural(thin).Count);
            Assert.Empty(CadSymbolResolve.Structural(new List<ModelWall>()));
        }

        [Fact]
        public void Symbol_at_a_finish_face_hosts_on_the_core_wall_and_faces_the_room()
        {
            // Drawn at the finish's surface (y = 120): nearest the finish's centreline, 10 mm off.
            var rooms = new Func<double, double, RoomHit?>((x, y) => y > 120 ? Office : y < -100 && y > -5900 ? Store : null);
            List<ModelWall> walls = CadSymbolResolve.Structural(CoreAndFinish());

            PlannedPoint p = Run(2500, 120, walls, rooms);

            Assert.Equal(201, p.HostWallId);
            Assert.Equal(100, p.YMm, 6);
            Assert.Equal(1, p.FacingDy, 6);
            Assert.Equal("Office", p.RoomName);
            Assert.DoesNotContain("side_ambiguous", p.Flags);
        }

        // ─── ceiling and unhosted fittings ─────────────────────────────
        // "Level 1" at 3000 mm; a ceiling (id 501) with its underside at level + 2700 over
        // 0 <= x <= 4000, 100 <= y <= 3000, and none elsewhere.

        private static CeilingHit? CeilingOver(double x, double y) =>
            x >= 0 && x <= 4000 && y >= 100 && y <= 3000 ? new CeilingHit { Id = 501, UndersideZMm = 5700 } : null;

        private static PlannedPoint Fitting(double x, double y, string host, double? mount,
                                            List<(double X, double Y, double Z)>? existing = null, string role = "light")
        {
            return CadSymbolResolve.Resolve(
                new ResolveInput { XMm = x, YMm = y, Role = role, Host = host, MountHeightMm = mount },
                Wall(), CadSymbolResolve.Index(Wall()), OfficeAbove, 3000,
                existing ?? new List<(double X, double Y, double Z)>(), new ResolveOptions(), CeilingOver);
        }

        [Fact]
        public void Ceiling_host_takes_the_ceiling_underside()
        {
            PlannedPoint p = Fitting(2000, 1200, "ceiling", 2400);

            Assert.Equal(501, p.HostCeilingId);
            Assert.Equal("ceiling", p.Host);
            Assert.Equal(5700, p.ZMm, 6);
            Assert.Equal(2700, p.MountHeightMm, 6);
            Assert.Equal(2000, p.XMm, 6);
            Assert.Equal(1200, p.YMm, 6);
            Assert.Null(p.HostWallId);
            Assert.Equal(0, p.FacingDx, 6);
            Assert.Equal(0, p.FacingDy, 6);
            Assert.Equal("Office", p.RoomName);
            Assert.Empty(p.Flags);
            Assert.False(p.Excluded);
            // A mount height override does not take it off its ceiling.
            Assert.Equal(5700, CadSymbolResolve.TargetZMm(p, 300), 6);
            Assert.False(CadSymbolResolve.NoCeiling(p, "ceiling", "ceiling"));
        }

        [Fact]
        public void Ceiling_host_without_a_mount_height_still_takes_the_underside()
        {
            PlannedPoint p = Fitting(2000, 1200, "ceiling", null);

            Assert.Equal(5700, p.ZMm, 6);
            Assert.DoesNotContain("no_height", p.Flags);
            Assert.False(p.Excluded);
        }

        [Fact]
        public void No_ceiling_with_mount_height_uses_level_plus_height()
        {
            PlannedPoint p = Fitting(6000, 1200, "ceiling", 2600);

            Assert.Null(p.HostCeilingId);
            Assert.Equal("unhosted", p.Host);
            Assert.Equal(5600, p.ZMm, 6);
            Assert.Equal(2600, p.MountHeightMm, 6);
            Assert.DoesNotContain("no_height", p.Flags);
            Assert.False(p.Excluded);
            Assert.Equal(5900, CadSymbolResolve.TargetZMm(p, 2900), 6);

            // Only a family that cannot stand without its ceiling makes that no_ceiling.
            Assert.True(CadSymbolResolve.NoCeiling(p, "ceiling", "ceiling"));
            Assert.True(CadSymbolResolve.NoCeiling(p, "ceiling", "hosted"));
            Assert.False(CadSymbolResolve.NoCeiling(p, "ceiling", "face"));
            Assert.False(CadSymbolResolve.NoCeiling(p, "ceiling", "work_plane"));
            Assert.False(CadSymbolResolve.NoCeiling(p, "ceiling", "none"));
            Assert.False(CadSymbolResolve.NoCeiling(p, "ceiling", null));
            Assert.False(CadSymbolResolve.NoCeiling(p, "none", "ceiling"));

            CadSymbolResolve.AddFlag(p, "no_ceiling");
            Assert.True(p.Excluded);
        }

        [Fact]
        public void No_ceiling_and_no_height_is_no_height_excluded()
        {
            PlannedPoint p = Fitting(6000, 1200, "ceiling", null);

            Assert.Null(p.HostCeilingId);
            Assert.Equal(3000, p.ZMm, 6);
            Assert.Contains("no_height", p.Flags);
            Assert.True(p.Excluded);
        }

        [Fact]
        public void Unhosted_uses_level_plus_height()
        {
            // Under the ceiling, but host "none" never looks for it.
            PlannedPoint p = Fitting(2000, 1200, "none", 2500, role: "fan");

            Assert.Null(p.HostCeilingId);
            Assert.Equal("unhosted", p.Host);
            Assert.Equal(5500, p.ZMm, 6);
            Assert.Equal(2000, p.XMm, 6);
            Assert.Equal(1200, p.YMm, 6);
            Assert.Empty(p.Flags);

            PlannedPoint none = Fitting(2000, 1200, "none", null, role: "fan");
            Assert.Contains("no_height", none.Flags);
            Assert.True(none.Excluded);
        }

        [Fact]
        public void Ceiling_item_already_present_in_3d()
        {
            // A light already on the ceiling at the point: present.
            PlannedPoint on = Fitting(2000, 1200, "ceiling", null,
                new List<(double X, double Y, double Z)> { (2050, 1230, 5700) });
            Assert.Contains("already_present", on.Flags);
            Assert.True(on.Excluded);

            // A socket on the wall straight under it is another device.
            PlannedPoint under = Fitting(2000, 1200, "ceiling", null,
                new List<(double X, double Y, double Z)> { (2000, 1200, 3300) });
            Assert.DoesNotContain("already_present", under.Flags);

            // The placement-time check reads the same Z.
            Assert.True(CadSymbolResolve.IsPresent(2000, 1200, CadSymbolResolve.TargetZMm(on, 300),
                new List<(double X, double Y, double Z)> { (2000, 1200, 5650) }, new ResolveOptions().ExistingMm));
        }

        [Fact]
        public void Light_role_defaults_to_ceiling_host()
        {
            Assert.Equal("ceiling", CadElectricalMapping.DefaultHost("light"));
            Assert.Equal("ceiling", CadElectricalMapping.DefaultHost("fan"));
            Assert.Equal("wall", CadElectricalMapping.DefaultHost("socket"));
        }

        [Fact]
        public void A_ceiling_family_on_a_wall_and_a_wall_family_on_a_ceiling_mismatch()
        {
            Assert.True(CadSymbolResolve.HostMismatch("wall", "ceiling"));
            Assert.True(CadSymbolResolve.HostMismatch("none", "ceiling"));
            Assert.True(CadSymbolResolve.HostMismatch("ceiling", "wall"));
            Assert.False(CadSymbolResolve.HostMismatch("ceiling", "ceiling"));
            Assert.False(CadSymbolResolve.HostMismatch("ceiling", "face"));
            Assert.False(CadSymbolResolve.HostMismatch("ceiling", "work_plane"));
            Assert.False(CadSymbolResolve.HostMismatch("ceiling", "none"));
        }
    }
}
