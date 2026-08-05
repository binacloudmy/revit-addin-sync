// Hop records for a routed circuit — pure, Revit-free.
//
// A "hop" is one wire's worth of route: panel->dev0, dev0->dev1, ... Each hop
// owns BOTH its leg range and the two element ids its wire must connect.
//
// It used to own only the leg range (RoutePlan.HopStartLegIndex), and
// RouteCommit paired hop h with DeviceIds[h-1]/DeviceIds[h] positionally. That
// held only while every hop produced legs. A degenerate hop — two devices at
// the same point, which is ordinary in a socket chain — was skipped without
// appending a leg index, while DeviceIds kept the full chain. From that point
// on every wire was created against the WRONG device connectors, and because
// Wire.Create accepts mismatched connectors (see RouteCommit's note on loose
// ends) it failed silently: no failed[] row, no note, wrong model.
//
// Carrying the ids on the hop makes that class of desync unrepresentable, and
// the builder below is Revit-free so the invariant is unit-tested.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>The plan-view polyline for one hop's wire.
    ///
    /// A Wire is hosted in a PLAN VIEW, so it is drawn from XY only. The routed
    /// legs are 3D: every hop begins with a rise off the device and ends with a
    /// drop onto the next one, and those legs share their XY with the run they
    /// join. Feeding all leg endpoints to Wire.Create therefore handed it
    /// consecutive duplicate points, which it rejects — so conduit was built
    /// and NO wire was, on every circuit with a vertical leg (i.e. all of
    /// them). Collapsing the run to its distinct XY stations is the whole
    /// difference.</summary>
    public static class WirePath
    {
        /// <summary>Two points within this distance are the same station to a
        /// plan view. Revit's own short-curve limit is about 1 mm.</summary>
        public const double CoincidentMm = 1.0;

        /// <summary>Drop consecutive points that share an XY station. Returns
        /// the distinct stations in order; a run that collapses to fewer than
        /// two has no drawable wire.</summary>
        public static List<(double XMm, double YMm)> DistinctStations(
            IReadOnlyList<(double XMm, double YMm)> points)
        {
            var outp = new List<(double XMm, double YMm)>();
            foreach (var p in points ?? Array.Empty<(double, double)>())
            {
                if (outp.Count > 0 && Coincident(outp[outp.Count - 1], p)) continue;
                outp.Add(p);
            }
            return outp;
        }

        public static bool Coincident((double XMm, double YMm) a, (double XMm, double YMm) b)
        {
            double dx = a.XMm - b.XMm, dy = a.YMm - b.YMm;
            return Math.Sqrt(dx * dx + dy * dy) < CoincidentMm;
        }

        /// <summary>The vertices Wire.Create actually wants: the stations
        /// BETWEEN the two connectors.
        ///
        /// Revit assembles its own list as [startConnector] + vertexPoints +
        /// [endConnector] — that is what its rejection means by "or there are
        /// not at least two points including the connectors". Our stations
        /// began ON the start connector and ended ON the end connector, so
        /// every hop handed it a coincident pair at BOTH ends. Every hop of
        /// every circuit begins and ends on a connector, which is why the
        /// failure was total (UAT 2026-08-05: stations
        /// [[28956,5791.2],[25616.7,5791.2],[25616.7,9042.4]] against
        /// start [28956,5791.2] and end [25616.7,9042.4] — the two outer
        /// stations were the connectors themselves).
        ///
        /// A connector whose position could not be read is passed as null and
        /// trims nothing: Revit is then not adding a point for it either.</summary>
        public static List<(double XMm, double YMm)> InteriorStations(
            IReadOnlyList<(double XMm, double YMm)> stations,
            (double XMm, double YMm)? start,
            (double XMm, double YMm)? end)
        {
            var outp = new List<(double XMm, double YMm)>(
                stations ?? (IReadOnlyList<(double, double)>)Array.Empty<(double, double)>());
            if (start.HasValue)
                while (outp.Count > 0 && Coincident(outp[0], start.Value))
                    outp.RemoveAt(0);
            if (end.HasValue)
                while (outp.Count > 0 && Coincident(outp[outp.Count - 1], end.Value))
                    outp.RemoveAt(outp.Count - 1);
            return outp;
        }

        /// <summary>Whether the points Revit will end up with — the connectors
        /// plus these interior vertices — describe a drawable line. Two
        /// connectors alone are enough, but not when they share a plan
        /// position: that is the ordinary panel-above-device case, and it is a
        /// skipped hop, not a failure.</summary>
        public static bool IsDrawable(
            IReadOnlyList<(double XMm, double YMm)> interior,
            (double XMm, double YMm)? start,
            (double XMm, double YMm)? end)
        {
            var full = new List<(double XMm, double YMm)>();
            if (start.HasValue) full.Add(start.Value);
            if (interior != null) full.AddRange(interior);
            if (end.HasValue) full.Add(end.Value);
            return DistinctStations(full).Count >= 2;
        }
    }

    /// <summary>One wire's worth of route: a leg range plus the two ends the
    /// wire connects.</summary>
    public sealed class RouteHop
    {
        /// <summary>Index of this hop's first leg in PlannedRoute.Legs.</summary>
        public int StartLegIndex;
        /// <summary>Index of this hop's last leg, inclusive.</summary>
        public int EndLegIndex;
        /// <summary>Element id this hop starts at, or 0 for the panel (the home
        /// run). Never inferred from position.</summary>
        public long FromDeviceId;
        /// <summary>Element id this hop ends at.</summary>
        public long ToDeviceId;
    }

    /// <summary>Accumulates hops while the planner walks the device chain.
    /// Tracks the chain position itself, so a hop that produces no legs still
    /// advances the "from" end without emitting a wire.</summary>
    public sealed class RouteHopBuilder
    {
        /// <summary>0 = the panel: the first hop is always the home run.</summary>
        private long _fromDeviceId;

        public List<RouteHop> Hops { get; } = new();

        /// <summary>Record a hop that produced legs [startLegIndex, endLegIndex]
        /// inclusive, ending at <paramref name="toDeviceId"/>.</summary>
        public void AddHop(long toDeviceId, int startLegIndex, int endLegIndex)
        {
            Hops.Add(new RouteHop
            {
                StartLegIndex = startLegIndex,
                EndLegIndex = endLegIndex,
                FromDeviceId = _fromDeviceId,
                ToDeviceId = toDeviceId,
            });
            _fromDeviceId = toDeviceId;
        }

        /// <summary>Record a hop that produced no legs — coincident points, so
        /// there is nothing to build. The chain still advances: the NEXT hop
        /// starts at this device, and its wire must say so.</summary>
        public void SkipHop(long toDeviceId) => _fromDeviceId = toDeviceId;
    }
}
