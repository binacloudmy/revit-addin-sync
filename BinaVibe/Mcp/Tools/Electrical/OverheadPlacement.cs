#nullable enable
// OverheadPlacement - the pure rules behind placing a ceiling device (a light, a fan) and
// finding it again: which of a ceiling's bottom faces to put it on, when a placed height is
// close enough, and when a device already in the model is the same one.
//
// A work-plane-based family's LocationPoint need not lie on the plane it is hosted on: the
// family's origin can sit off its work plane, by a different amount per family. So a placed
// device's height is its CONTACT height (where it meets its host: the host face under its
// origin, or the level plus its offset), never the raw LocationPoint Z. SocketPlacement and
// CadElectricalResolver.ContactHeight measure it; these rules decide on the numbers.
//
// Millimetres, project-internal coordinates. System only; must compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>A device already in the model, at its contact height, mm, with the id of the
    /// ceiling it is hosted on (null when it is not on a ceiling).</summary>
    internal sealed class PresentDevice
    {
        public double X, Y, Z;
        public long? CeilingId;
        /// <summary>The device's element id; 0 when not read.</summary>
        public long Id;
        /// <summary>False when Z is not a contact height that can be trusted (a work-plane-based
        /// family read at its origin, which can sit off its work plane): it is then matched in
        /// plan on the level band instead.</summary>
        public bool ZKnown = true;
        /// <summary>The raw LocationPoint Z, mm; null when not read.</summary>
        public double? OriginZ;
        /// <summary>The device's category id; null when not read.</summary>
        public long? CategoryId;
        /// <summary>The elevation of the device's own level, mm; null when it has none.</summary>
        public double? LevelZMm;
    }

    /// <summary>Where a point would put a device, mm, for OverheadPlacement.Matches.</summary>
    internal sealed class PresenceQuery
    {
        public double X, Y, Z;
        /// <summary>The ceiling the point is on; null for a wall or free-standing point.</summary>
        public long? CeilingId;
        /// <summary>The category of the family the point would place; null when not known.</summary>
        public long? CategoryId;
        /// <summary>The point's level band: its level's elevation up to the next level's (null
        /// high: no level above). Null low: no band known.</summary>
        public double? BandLowMm, BandHighMm;
    }

    internal static class OverheadPlacement
    {
        /// <summary>A placed contact height this far off the target is a wrong placement.</summary>
        public const double HeightTolMm = 20;
        /// <summary>A face crossing the vertical within this of the target holds the target.</summary>
        public const double OnPlaneMm = 5;
        /// <summary>A face whose normal Z is below this looks down (a sloped underside still does).</summary>
        public const double DownNormalZ = -0.1;
        /// <summary>A device's geometry this close to its contact height meets its host.</summary>
        public const double TouchMm = 50;

        /// <summary>
        /// The order to try a ceiling's bottom faces in, as indices into <paramref name="faces"/>:
        /// each face's normal Z at the point and where a vertical through the point crosses it
        /// (null when it misses). Only faces the vertical crosses that look down: the one holding
        /// the target (within <see cref="OnPlaneMm"/>) first, then the rest nearest the target
        /// first. A face that looks up or sideways is never the underside and is left out.
        /// </summary>
        public static List<int> FaceOrder(IReadOnlyList<(double NormalZ, double? ZMm)> faces, double targetZMm)
        {
            return Enumerable.Range(0, faces.Count)
                .Where(i => faces[i].ZMm.HasValue && faces[i].NormalZ < DownNormalZ)
                .OrderBy(i => Math.Abs(faces[i].ZMm!.Value - targetZMm) <= OnPlaneMm ? 0 : 1)
                .ThenBy(i => Math.Abs(faces[i].ZMm!.Value - targetZMm))
                .ThenBy(i => i)
                .ToList();
        }

        /// <summary>True when a placed contact height is within <see cref="HeightTolMm"/> of the target.</summary>
        public static bool HeightOk(double contactZMm, double targetZMm) =>
            Math.Abs(contactZMm - targetZMm) <= HeightTolMm;

        /// <summary>A set parameter read back within this has taken.</summary>
        public const double ReadBackTolMm = 1;

        /// <summary>
        /// The contact height of a device placed on a level (or a horizontal work plane): the
        /// plane's Z plus the first offset parameter that reads a value, in the order that applies
        /// to the family. Null when none does. The LocationPoint plays no part: a work-plane-based
        /// family's origin can sit off its work plane.
        /// </summary>
        public static double? LevelContactMm(double planeZMm, IReadOnlyList<double?> offsetsMm)
        {
            foreach (double? o in offsetsMm)
                if (o.HasValue) return planeZMm + o.Value;
            return null;
        }

        /// <summary>
        /// The contact height of a device on a level, checked against its origin, mm, and how it
        /// was read. The level reading (<paramref name="levelContactMm"/>: the plane under it at
        /// <paramref name="baseMm"/> plus <paramref name="offsetMm"/>) stands unless it is more
        /// than <see cref="HeightTolMm"/> off the origin and one of:
        ///   the offset alone is the origin's Z (within <see cref="ReadBackTolMm"/>) on a level
        ///     that is not at 0: the offset is measured from elevation 0, not from that level,
        ///     and the level reading counts the level twice ("offset_absolute");
        ///   the origin is within <see cref="ReadBackTolMm"/> of the Z this tool placed it at
        ///     (<paramref name="targetMm"/>, null when not known): "origin_verified".
        /// Otherwise "level_offset": a work-plane-based family's origin can sit well off its
        /// plane (a live test read 2044 mm), so a disagreement alone never moves the height.
        /// </summary>
        public static (double ZMm, string Via) LevelOrOrigin(double levelContactMm, double baseMm, double? offsetMm,
                                                              double originMm, double? targetMm)
        {
            if (Math.Abs(levelContactMm - originMm) <= HeightTolMm) return (levelContactMm, "level_offset");
            if (offsetMm.HasValue && Math.Abs(baseMm) > SameLevelMm && Math.Abs(offsetMm.Value - originMm) <= ReadBackTolMm)
                return (originMm, "offset_absolute");
            if (targetMm.HasValue && Math.Abs(originMm - targetMm.Value) <= ReadBackTolMm)
                return (originMm, "origin_verified");
            return (levelContactMm, "level_offset");
        }

        /// <summary>True when a group of placements created something and is kept as one undo
        /// step. When nothing was created it is rolled back, so it leaves no undo entry.</summary>
        public static bool KeepGroup(int devicesCreated, int runsCreated = 0) => devicesCreated > 0 || runsCreated > 0;

        /// <summary>True when a parameter read back after a Set holds the value set.</summary>
        public static bool ReadBackOk(double setMm, double readMm) => Math.Abs(readMm - setMm) <= ReadBackTolMm;

        /// <summary>Z of a plane (a point on it and its normal) over (x, y); null for a vertical plane.</summary>
        public static double? PlaneZAt(double ox, double oy, double oz, double nx, double ny, double nz,
                                       double x, double y)
        {
            if (Math.Abs(nz) < 1e-9) return null;
            return oz - ((nx * (x - ox)) + (ny * (y - oy))) / nz;
        }

        /// <summary>True when a device's geometry, from <paramref name="minZMm"/> to
        /// <paramref name="maxZMm"/>, comes within <see cref="TouchMm"/> of its contact height: a
        /// recessed fitting spans it, a surface or pendant one hangs from it. False means the
        /// family draws its fitting well away from where it is hosted.</summary>
        public static bool GeometryTouches(double minZMm, double maxZMm, double contactZMm) =>
            contactZMm >= minZMm - TouchMm && contactZMm <= maxZMm + TouchMm;

        /// <summary>True when a device of the model already stands where this point would go,
        /// with no category and no level band known (<see cref="Matches"/>).</summary>
        public static bool IsPresent(double xMm, double yMm, double zMm, long? ceilingId,
                                     IReadOnlyList<PresentDevice> existing, double withinMm) =>
            IsPresent(new PresenceQuery { X = xMm, Y = yMm, Z = zMm, CeilingId = ceilingId }, existing, withinMm);

        /// <summary>True when any existing device <see cref="Matches"/> the point.</summary>
        public static bool IsPresent(PresenceQuery q, IReadOnlyList<PresentDevice> existing, double withinMm) =>
            existing.Any(d => Matches(q, d, withinMm));

        /// <summary>A level elevation this close to the band's foot is the band's level.</summary>
        public const double SameLevelMm = 1;

        /// <summary>
        /// True when an existing device is the one this point would place. Never across
        /// categories when both are known. Then it must be within <paramref name="withinMm"/> in
        /// plan, and one of:
        ///   hosted on the point's own ceiling, whatever its height reads;
        ///   its contact height known and within <paramref name="withinMm"/> of the point's Z, so
        ///     a socket on the wall under a light is a different device;
        ///   of the point's category (both known) with its origin Z within
        ///     <paramref name="withinMm"/>: an instance whose origin is at the point is the one
        ///     placed there, whatever its host reports;
        ///   its contact height not known, and on the point's level band (its own level the
        ///     band's level, else its origin in the band); with no band known, plan alone.
        /// </summary>
        public static bool Matches(PresenceQuery q, PresentDevice d, double withinMm)
        {
            bool bothCategories = q.CategoryId.HasValue && d.CategoryId.HasValue;
            if (bothCategories && q.CategoryId != d.CategoryId) return false;

            double dx = d.X - q.X, dy = d.Y - q.Y;
            if ((dx * dx) + (dy * dy) > withinMm * withinMm) return false;

            if (q.CeilingId.HasValue && d.CeilingId == q.CeilingId) return true;
            if (d.ZKnown && Math.Abs(d.Z - q.Z) <= withinMm) return true;
            if (bothCategories && d.OriginZ.HasValue && Math.Abs(d.OriginZ.Value - q.Z) <= withinMm) return true;
            return !d.ZKnown && OnBand(q, d);
        }

        private static bool OnBand(PresenceQuery q, PresentDevice d)
        {
            if (!q.BandLowMm.HasValue) return true;
            if (d.LevelZMm.HasValue) return Math.Abs(d.LevelZMm.Value - q.BandLowMm.Value) <= SameLevelMm;
            if (!d.OriginZ.HasValue) return false;
            double high = q.BandHighMm ?? double.PositiveInfinity;
            return d.OriginZ.Value >= q.BandLowMm.Value && d.OriginZ.Value < high;
        }
    }
}
