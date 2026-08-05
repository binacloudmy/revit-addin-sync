// create_circuit_routes: the wires, and the connector lookups they need.
//
// Wire.Create builds [startConnector] + vertexPoints + [endConnector], so
// the vertex list must carry the INTERIOR stations only — passing the ends
// as well makes every hop coincident with its own connectors.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class RouteCommit
    {
        /// <summary>One Wire per hop: panel-&gt;dev0, dev0-&gt;dev1, … Runs INSIDE
        /// CommitOne's transaction and opens none of its own.
        ///
        /// A hop that Revit rejects STOPS the chain but does not undo the hops
        /// already made — the transaction still commits, so the circuit is left
        /// partly wired and the counts in the result row say so.</summary>
        private static WireOutcome BuildWires(
            Document doc, PlannedRoute r, ElectricalSystem sys, FamilyInstance panel,
            WireType wireType, ViewPlan wireView, string? preSkipReason)
        {
            var outcome = new WireOutcome { SkipReason = preSkipReason };

            // Each hop names its own two ends; they are NEVER derived from the
            // loop index, because a hop that produced no legs is absent from
            // this list while DeviceIds still holds the full chain.
            foreach (var hop in r.Hops)
            {
                outcome.HopsAttempted++;

                // A Wire is drawn in a PLAN view, so only XY matters. Passing
                // every leg endpoint fed it the rise and drop twice over —
                // consecutive duplicate points, which Wire.Create rejects.
                // Collapse to distinct XY stations first; the Z is the view's,
                // not the route's.
                var stations = new List<(double XMm, double YMm)>();
                for (int i = hop.StartLegIndex; i <= hop.EndLegIndex; i++)
                {
                    var leg = r.Legs[i];
                    if (stations.Count == 0) stations.Add((leg.FromXMm, leg.FromYMm));
                    stations.Add((leg.ToXMm, leg.ToYMm));
                }
                var distinct = WirePath.DistinctStations(stations);

                // Connectors are resolved BEFORE the vertex list, not after:
                // the list Revit wants is defined relative to them. The home run
                // starts at THIS circuit's connector on the panel — a
                // distribution board carries one per circuit, so "the panel's
                // first electrical connector" is the right one only by luck, and
                // Wire.Create accepts a mismatched connector without complaint.
                var startConn = hop.FromDeviceId == 0
                    ? SafeBaseConnector(sys, panel)
                    : DeviceConnector(doc, hop.FromDeviceId);
                var endConn = DeviceConnector(doc, hop.ToDeviceId);

                // Revit builds [startConnector] + vertexPoints + [endConnector]
                // and rejects the result if any pair is coincident in X and Y.
                // Our stations START on the start connector and END on the end
                // connector, so every hop of every circuit handed it a duplicate
                // at both ends — 0 wires on every UAT run to 2026-08-04. Pass
                // the INTERIOR stations only.
                var interior = WirePath.InteriorStations(
                    distinct, ConnXyMm(startConn), ConnXyMm(endConn));
                if (!WirePath.IsDrawable(interior, ConnXyMm(startConn), ConnXyMm(endConn)))
                {
                    // Panel and device share a plan position — there is no line
                    // to draw. Not a failure, and it must not abort the
                    // remaining hops.
                    continue;
                }
                var verts = interior
                    .Select(s => new XYZ(s.XMm / MmPerFoot, s.YMm / MmPerFoot, 0.0))
                    .ToList();

                try
                {
                    var wire = Wire.Create(doc, wireType.Id, wireView.Id,
                                           WiringType.Chamfer, verts, startConn, endConn);
                    outcome.WireIds.Add(wire.Id.Value);
                }
                catch (Exception ex)
                {
                    outcome.SkipReason = "wire_create_failed after " + outcome.WireIds.Count +
                                         " of " + r.Hops.Count + " hop(s): " + ex.Message;
                    // The geometry Revit rejected, so the next run diagnoses
                    // this instead of guessing.
                    outcome.Debug = new Dictionary<string, object?>
                    {
                        ["hop_from_device_id"] = hop.FromDeviceId,
                        ["hop_to_device_id"] = hop.ToDeviceId,
                        ["stations_mm"] = distinct
                            .Select(s => (object)new List<object>
                            {
                                Math.Round(s.XMm, 1), Math.Round(s.YMm, 1),
                            }).ToList(),
                        // What was actually passed, after trimming the stations
                        // that sit on the connectors.
                        ["interior_vertices_mm"] = interior
                            .Select(s => (object)new List<object>
                            {
                                Math.Round(s.XMm, 1), Math.Round(s.YMm, 1),
                            }).ToList(),
                        ["start_connector_mm"] = ConnOriginMm(startConn),
                        ["end_connector_mm"] = ConnOriginMm(endConn),
                        ["start_connector_found"] = startConn != null,
                        ["end_connector_found"] = endConn != null,
                    };
                    break;
                }
            }

            return outcome;
        }

        /// <summary>The board-side connector for this circuit's home run.
        /// Delegates to PanelConnectors so the wire and the circuit path start
        /// can never pick differently — see that file for why
        /// BaseEquipmentConnector alone is not enough (it is logical on a
        /// panel: no Origin, and Wire.Create refuses it with "cannot be
        /// connected to a wire, as it is not an electrical connector").</summary>
        private static Connector? SafeBaseConnector(ElectricalSystem sys, FamilyInstance panel)
            => PanelConnectors.ForCircuit(sys, panel).Connector;

        /// <summary>A connector's PLAN position, which is the only part of it a
        /// Wire sees. Null when there is no connector or its origin cannot be
        /// read — the vertex trimming then leaves the stations alone, because
        /// Revit is not contributing a point for it either.</summary>
        private static (double XMm, double YMm)? ConnXyMm(Connector? c)
        {
            if (c == null) return null;
            try { return (c.Origin.X * MmPerFoot, c.Origin.Y * MmPerFoot); }
            catch { return null; }
        }

        private static object? ConnOriginMm(Connector? c)
        {
            if (c == null) return null;
            try
            {
                return new List<object>
                {
                    Math.Round(c.Origin.X * MmPerFoot, 1),
                    Math.Round(c.Origin.Y * MmPerFoot, 1),
                    Math.Round(c.Origin.Z * MmPerFoot, 1),
                };
            }
            catch { return null; }
        }

        private static Connector? DeviceConnector(Document doc, long deviceId)
            => doc.GetElement(ElemIds.From(deviceId)) is FamilyInstance fi
                ? FirstElectricalConnector(fi)
                : null;

        /// <summary>First electrical connector, or null — Wire.Create accepts
        /// null end connectors, so an unconnectable end degrades to a loose
        /// wire end rather than failing the hop.</summary>
        private static Connector? FirstElectricalConnector(FamilyInstance fi)
            => PanelConnectors.FirstWireCapable(fi);
    }
}
