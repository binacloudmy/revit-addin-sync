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

        /// <summary>
        /// True when a device of the model already stands where this point would go. A device
        /// hosted on the point's ceiling counts when it is within <paramref name="withinMm"/> in
        /// plan, whatever its height reads; any other counts within <paramref name="withinMm"/>
        /// in 3D, at its contact height. A wall point passes no ceiling and gets the 3D rule only,
        /// so a socket on the wall under a light is a different device.
        /// </summary>
        public static bool IsPresent(double xMm, double yMm, double zMm, long? ceilingId,
                                     IReadOnlyList<PresentDevice> existing, double withinMm)
        {
            double limit = withinMm * withinMm;
            foreach (PresentDevice d in existing)
            {
                double dx = d.X - xMm, dy = d.Y - yMm, dz = d.Z - zMm;
                double plan = (dx * dx) + (dy * dy);
                if (ceilingId.HasValue && d.CeilingId == ceilingId && plan <= limit) return true;
                if (plan + (dz * dz) <= limit) return true;
            }
            return false;
        }
    }
}
