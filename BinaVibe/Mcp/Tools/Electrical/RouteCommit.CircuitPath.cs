// create_circuit_routes: the circuit's own path polyline.
//
// SetCircuitPath is what makes Revit's circuit length the ROUTED length
// rather than the straight line, and voltage-drop checks read that length.

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
        /// <summary>Give the circuit its own polyline so Revit's circuit length
        /// is the ROUTED length — which is what voltage drop reads. Runs INSIDE
        /// CommitOne's transaction and opens none of its own.
        ///
        /// Never fatal: the conduit and wires above are worth keeping even when
        /// Revit refuses the path.</summary>
        private static PathOutcome SetPath(
            ElectricalSystem sys, PlannedRoute r, bool allowFlatPath)
        {
            var outcome = new PathOutcome();
            try
            {
                // The ELECTRICAL path through the devices, not the conduit
                // trunk — since the trunk stays up and takes one drop per
                // device, the leg list is no longer a single polyline and
                // walking it would hand Revit a jump from device height back to
                // routing height.
                var pathVerts = r.PathVerticesMm
                    .Select(p => new XYZ(p.X / MmPerFoot, p.Y / MmPerFoot, p.Z / MmPerFoot))
                    .ToList();
                if (pathVerts.Count < 2)
                    throw new InvalidOperationException(
                        "route has no circuit-path polyline (re-run suggest_circuit_routes)");

                // NO `CircuitPathMode = Custom` before this. The setter throws
                // on any circuit still in default mode — i.e. always, on a
                // freshly created one — and SetCircuitPath switches the mode to
                // Custom ITSELF on success. Re-adding the assignment costs every
                // circuit its routed length.
                sys.SetCircuitPath(pathVerts);
                outcome.Set = true;
                outcome.Shape = "dive";
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;

                // Second shape, no dive-and-return. The dive path revisits an
                // identical point three nodes later, and by round 6 of UAT every
                // segment was axis-aligned and Revit still refused — the
                // doubling back is the last condition left in its message.
                // Whichever shape lands is reported, so the next reader knows
                // which one Revit takes instead of inferring it.
                if (!allowFlatPath)
                {
                    outcome.Error += "  |  a no-dive path shape is available and NOT tried: " +
                                     "it omits the per-device drops, so Revit's circuit " +
                                     "length would come out shorter than the conductor runs " +
                                     "and check_circuit_loads cannot tell the two shapes " +
                                     "apart. Pass allow_flat_circuit_path=true to accept an " +
                                     "approximate routed length. Nodes are in " +
                                     "circuit_path_flat_nodes_mm";
                }
                else
                {
                    try
                    {
                        var flat = r.PathVerticesFlatMm
                            .Select(p => new XYZ(p.X / MmPerFoot, p.Y / MmPerFoot, p.Z / MmPerFoot))
                            .ToList();
                        if (flat.Count >= 2)
                        {
                            sys.SetCircuitPath(flat);
                            outcome.Set = true;
                            outcome.Shape = "flat";
                            // The drops are not in this path, so its length is
                            // SHORTER than the conduit run.
                            outcome.Error += "  |  fell back to the no-dive path shape: " +
                                             "circuit length now EXCLUDES the per-device " +
                                             "drops, so voltage drop is computed on a " +
                                             "shorter run than the conduit";
                        }
                    }
                    catch (Exception ex2)
                    {
                        outcome.Error += "  |  no-dive shape also refused: " + ex2.Message;
                    }
                }

                // Revit's rejection lists FIVE conditions at once and never says
                // which one fired, so the refused polyline ships with it.
                // Report the shape that FAILED — plus the flat one when neither
                // took, because then both are evidence.
                outcome.Nodes = NodeRows(r.PathVerticesMm);
                if (!outcome.Set) outcome.FlatNodes = NodeRows(r.PathVerticesFlatMm);
            }

            return outcome;
        }

        private static List<object> NodeRows(IEnumerable<Pt3Mm> pts)
            => pts.Select(p => (object)new List<object>
            {
                Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1),
            }).ToList();
    }
}
