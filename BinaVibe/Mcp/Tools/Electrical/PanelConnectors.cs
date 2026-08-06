// Which connector on the board a circuit leaves from — one answer, because two
// callers answering separately can disagree and Revit accepts both silently
// (Wire.Create takes a mismatched connector; SetCircuitPath checks only the
// first node's position).
//
// MEPSystem.BaseEquipmentConnector names THIS circuit's connector, but on a
// panel it is LOGICAL: no Origin (reading it throws) and Wire.Create refuses it
// with "not an electrical connector". So it identifies the right connector and
// cannot BE it. A physical connector can carry the wire, but "the first one on
// the panel" is right only by luck on a board serving several circuits, and
// nothing links the logical connector to its physical twin — so this is not
// solvable by picking harder. Ambiguous says so instead.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal sealed class PanelConnectorPick
    {
        /// <summary>The connector to use, or null when the panel has none a
        /// wire can attach to.</summary>
        public Connector? Connector;
        /// <summary>"circuit_connector" when BaseEquipmentConnector was usable,
        /// "panel_connector" when a physical one was chosen instead, "none"
        /// when nothing usable exists.</summary>
        public string Source = "none";
        /// <summary>True when the board offers more than one physical
        /// electrical connector and nothing identifies which belongs to this
        /// circuit. The pick is then a guess, and callers must report it.</summary>
        public bool Ambiguous;
        /// <summary>How many physical electrical connectors the panel has.</summary>
        public int PhysicalCount;
    }

    internal static class PanelConnectors
    {
        /// <summary>The connector this circuit should leave the board from.
        /// One answer, so the wire and the circuit path cannot disagree.</summary>
        public static PanelConnectorPick ForCircuit(ElectricalSystem sys, FamilyInstance panel)
        {
            var pick = new PanelConnectorPick();

            var physical = new List<Connector>();
            try
            {
                var cm = panel.MEPModel?.ConnectorManager;
                if (cm != null)
                    foreach (Connector c in cm.Connectors)
                        if (WireCapable(c) != null)
                            physical.Add(c);
            }
            catch { /* leave the list as it stands */ }
            pick.PhysicalCount = physical.Count;

            // BaseEquipmentConnector first — it is the only thing that names
            // this circuit's own connector. Usable only when it is physical.
            try
            {
                var baseConn = WireCapable(sys.BaseEquipmentConnector);
                if (baseConn != null)
                {
                    pick.Connector = baseConn;
                    pick.Source = "circuit_connector";
                    return pick;
                }
            }
            catch { /* logical, or unreadable — fall through */ }

            if (physical.Count > 0)
            {
                pick.Connector = physical[0];
                pick.Source = "panel_connector";
                // Only a guess once the board has more than one to choose from.
                pick.Ambiguous = physical.Count > 1;
            }
            return pick;
        }

        /// <summary>The connector unchanged when a wire can attach to it, else
        /// null: electrical domain, physical type, and an origin Revit will
        /// hand over (a logical connector throws on Origin).</summary>
        public static Connector? WireCapable(Connector? c)
        {
            if (c == null) return null;
            try
            {
                if (c.Domain != Domain.DomainElectrical) return null;
                if (c.ConnectorType != ConnectorType.Physical) return null;
                _ = c.Origin;
                return c;
            }
            catch { return null; }
        }

        /// <summary>First wire-capable electrical connector on an instance, or
        /// null. For DEVICES, where there is only ever one to find.</summary>
        public static Connector? FirstWireCapable(FamilyInstance fi)
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) return null;
            foreach (Connector c in cm.Connectors)
            {
                var ok = WireCapable(c);
                if (ok != null) return ok;
            }
            return null;
        }
    }
}
