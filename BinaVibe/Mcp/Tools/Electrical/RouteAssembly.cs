// How a circuit's legs are laid out — Revit-free, so the topology is
// unit-tested rather than discovered in UAT.
//
// TRUNK + DROPS, not point-to-point. The trunk stays UP: rise once at the
// panel, run at routing elevation through each device's XY in chain order, and
// take ONE drop off it per device. A device station is therefore a TEE (two
// runs plus the branch drop), not two elbows — see RouteCommit's joint pass.
//
// Planning hop-by-hop instead puts two conduits on the SAME LINE at every
// intermediate device (down onto it, straight back up), which NewElbowFitting
// rejects outright — its valid range is roughly 2 to 95 degrees — and which
// double-counts the drop in TotalLengthMm, inflating every voltage-drop number
// derived from it. Do not reintroduce it.
//
// CIRCUIT PATH IS A SEPARATE POLYLINE. Revit's SetCircuitPath wants the
// electrical path THROUGH the devices (panel, down to device 1, up, along, down
// to device 2, ...), which is no longer the shape of the conduit. Assembling
// both here keeps them consistent by construction.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One leg of the assembled route, in mm, before any Revit call.</summary>
    internal sealed class AssembledLeg
    {
        public Pt3Mm A;
        public Pt3Mm B;
        /// <summary>"run" (horizontal at routing elevation) | "rise" | "drop".</summary>
        public string Kind = "run";
        /// <summary>Element id this leg drops onto, or 0. Only ever set on a
        /// drop, and it is what makes the trunk station identifiable without
        /// re-deriving it from coordinates.</summary>
        public long DropsToDeviceId;
    }

    internal sealed class AssembledRoute
    {
        public List<AssembledLeg> Legs = new();
        /// <summary>Per device, the inclusive leg range whose XY the wire for
        /// that hop follows. Parallel to the device order handed in; a device
        /// whose runs collapsed to nothing has an empty range (Start &gt; End).</summary>
        public List<(long DeviceId, int StartLegIndex, int EndLegIndex)> HopRanges = new();
        /// <summary>The polyline for ElectricalSystem.SetCircuitPath — through
        /// the devices, NOT along the conduit trunk.</summary>
        public List<Pt3Mm> PathVertices = new();
        /// <summary>Fallback path shape: no dive-and-return, so no node is
        /// ever revisited. Shorter than <see cref="PathVertices"/> by the
        /// per-device drops.</summary>
        public List<Pt3Mm> PathVerticesFlat = new();
        public List<string> Notes = new();
    }

    internal static class RouteAssembly
    {
        /// <summary>Assemble the trunk, the per-device drops, the hop ranges and
        /// the circuit-path polyline.
        ///
        /// <paramref name="panelStart"/> must be the panel CONNECTOR position,
        /// not the instance origin — SetCircuitPath rejects the origin
        /// explicitly, and the conduit should leave from the connector anyway.
        /// <paramref name="planRuns"/> plans the horizontal travel between two
        /// points already at routing elevation; it is the pluggable strategy, so
        /// A* drops in here unchanged.</summary>
        public static AssembledRoute Build(
            Pt3Mm panelStart,
            double routingElevationMm,
            IReadOnlyList<(long Id, Pt3Mm At)> chain,
            Func<Pt3Mm, Pt3Mm, string?, IReadOnlyList<Pt3Mm>> planRuns)
        {
            if (planRuns == null) throw new ArgumentNullException(nameof(planRuns));
            var res = new AssembledRoute();
            if (chain == null || chain.Count == 0) return res;

            var trunk = new Pt3Mm(panelStart.X, panelStart.Y, routingElevationMm);

            // Rise off the panel connector. Skipped when the connector already
            // sits at routing elevation — a zero-length conduit is worse than
            // no conduit.
            if (Math.Abs(panelStart.Z - routingElevationMm) >= ManhattanRouteStrategy.JoinTolMm)
                res.Legs.Add(new AssembledLeg
                {
                    A = panelStart, B = trunk,
                    Kind = routingElevationMm > panelStart.Z ? "rise" : "drop",
                });

            res.PathVertices.Add(panelStart);
            res.PathVertices.Add(trunk);

            // The axis the trunk last travelled on. Handed to the strategy so
            // the next stretch LEAVES on the axis it arrived on, which keeps
            // the device station a straight-through tee instead of a corner —
            // and Revit has no fitting that both turns and branches.
            string? arrivalAxis = null;

            foreach (var (id, at) in chain)
            {
                int startLeg = res.Legs.Count;

                var above = new Pt3Mm(at.X, at.Y, routingElevationMm);
                foreach (var seg in Segments(planRuns(trunk, above, arrivalAxis)))
                {
                    res.Legs.Add(new AssembledLeg { A = seg.A, B = seg.B, Kind = "run" });
                    arrivalAxis = AxisOf(seg.A, seg.B) ?? arrivalAxis;

                    // The circuit path has to TURN wherever the trunk turns.
                    // Only `above` used to be added, so a Manhattan L between
                    // two devices reached SetCircuitPath as one plan DIAGONAL
                    // — and Revit requires every segment horizontal or
                    // vertical ("...should be in the same level or on the same
                    // vertical line"). UAT 2026-08-05: nodes went
                    // [28956,5791.2,2700] straight to [25616.7,9042.4,2700],
                    // skipping the corner at [25616.7,5791.2,2700] that the
                    // conduit legs did have.
                    res.PathVertices.Add(seg.B);
                }

                // One drop per device, off the trunk. The trunk itself carries
                // on from `above` to the next device — that is the whole point.
                bool dropped = Math.Abs(above.Z - at.Z) >= ManhattanRouteStrategy.JoinTolMm;
                if (dropped)
                    res.Legs.Add(new AssembledLeg
                    {
                        A = above, B = at, Kind = "drop", DropsToDeviceId = id,
                    });

                // The wire for this hop follows the runs AND the drop in plan;
                // the drop contributes no XY, but including it keeps the range
                // contiguous with what the conduit built.
                res.HopRanges.Add((id, startLeg, res.Legs.Count - 1));

                // Circuit path dives to the device and climbs back — that is
                // the electrical path, and it is deliberately NOT the conduit.
                if (dropped)
                {
                    res.PathVertices.Add(above);
                    res.PathVertices.Add(at);
                    res.PathVertices.Add(above);
                }
                else
                {
                    res.PathVertices.Add(above);
                }

                trunk = above;
            }

            // Duplicates only. NOT ManhattanRouteStrategy.Collapse — that also
            // merges collinear middles, and every device dive here is exactly
            // that shape (above, device, above), so it would erase the devices
            // from the electrical path and leave a duplicated trunk vertex
            // behind.
            res.PathVertices = DedupeConsecutive(res.PathVertices);
            res.PathVerticesFlat = BuildFlatPath(panelStart, chain, planRuns);
            return res;
        }

        /// <summary>The same electrical path with NO dive-and-return: it
        /// travels at each device's own elevation and passes straight through
        /// the devices.
        ///
        /// The dive shape (above, device, above) revisits an identical point
        /// three nodes later, and Revit has never accepted it — UAT 2026-08-05
        /// round 6 had every segment axis-aligned and SetCircuitPath still
        /// refused, leaving the doubling back as the only condition left in
        /// its message. Revit's own circuit paths are simple chains, so this
        /// is the shape to fall back to.
        ///
        /// Length differs: this omits the per-device drops the conductor
        /// really makes, so a circuit whose path came from here reports a
        /// SHORTER routed length than the conduit run. RouteCommit says which
        /// shape was accepted so voltage drop is never read off an unknown
        /// one.</summary>
        private static List<Pt3Mm> BuildFlatPath(
            Pt3Mm panelStart,
            IReadOnlyList<(long Id, Pt3Mm At)> chain,
            Func<Pt3Mm, Pt3Mm, string?, IReadOnlyList<Pt3Mm>> planRuns)
        {
            var verts = new List<Pt3Mm> { panelStart };
            var cur = panelStart;
            string? arrivalAxis = null;

            foreach (var (_, at) in chain)
            {
                // Change elevation on the spot, so the horizontal travel below
                // stays horizontal — Revit wants every segment on one axis.
                if (Math.Abs(cur.Z - at.Z) >= ManhattanRouteStrategy.JoinTolMm)
                {
                    cur = new Pt3Mm(cur.X, cur.Y, at.Z);
                    verts.Add(cur);
                    arrivalAxis = null;
                }

                foreach (var seg in Segments(planRuns(cur, at, arrivalAxis)))
                {
                    verts.Add(seg.B);
                    arrivalAxis = AxisOf(seg.A, seg.B) ?? arrivalAxis;
                    cur = seg.B;
                }
                cur = at;
            }

            return DedupeConsecutive(verts);
        }

        /// <summary>"x" / "y" for a horizontal segment, null for a vertical one
        /// (a rise or drop imposes no preference on the next run).</summary>
        internal static string? AxisOf(Pt3Mm a, Pt3Mm b)
        {
            double dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
            if (dx < ManhattanRouteStrategy.JoinTolMm && dy < ManhattanRouteStrategy.JoinTolMm)
                return null;
            return dx >= dy ? "x" : "y";
        }

        private static List<Pt3Mm> DedupeConsecutive(IReadOnlyList<Pt3Mm> verts)
        {
            var outv = new List<Pt3Mm>();
            foreach (var v in verts)
            {
                if (outv.Count > 0)
                {
                    var p = outv[outv.Count - 1];
                    double dx = p.X - v.X, dy = p.Y - v.Y, dz = p.Z - v.Z;
                    if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < ManhattanRouteStrategy.JoinTolMm)
                        continue;
                }
                outv.Add(v);
            }
            return outv;
        }

        /// <summary>Consecutive vertex pairs, skipping any that collapsed to a
        /// point. A strategy that returns fewer than two vertices contributes
        /// no legs — the two stations coincide in plan.</summary>
        private static IEnumerable<(Pt3Mm A, Pt3Mm B)> Segments(IReadOnlyList<Pt3Mm> verts)
        {
            if (verts == null) yield break;
            for (int i = 1; i < verts.Count; i++)
            {
                var a = verts[i - 1];
                var b = verts[i];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < ManhattanRouteStrategy.JoinTolMm)
                    continue;
                yield return (a, b);
            }
        }
    }
}
