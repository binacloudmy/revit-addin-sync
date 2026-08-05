// suggest_circuits: what the model offers as a distribution board.
//
// A panel is USABLE only when it carries a distribution system. Shared
// with CircuitCommit for commit-time re-verification, and with
// ElecValidation / ElecSettings, which read PanelFacts' fields straight
// into their own result rows — so those field names are a wire contract.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class CircuitCandidates
    {
        /// <summary>Every Electrical Equipment instance, classified usable
        /// (has a distribution system) or skipped-with-reason. Shared with
        /// CircuitCommit for commit-time re-verification.</summary>
        internal static List<PanelFacts> FindPanels(Document doc)
        {
            var outRows = new List<PanelFacts>();
            var instances = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .OrderBy(fi => fi.Id.Value);

            foreach (var fi in instances)
            {
                var f = new PanelFacts();
                f.Info.Id = fi.Id.Value;
                f.Info.Name = fi.Symbol != null ? fi.Symbol.FamilyName + " : " + fi.Name : fi.Name;

                if (fi.Location is LocationPoint lp)
                {
                    f.XMm = lp.Point.X * MmPerFoot;
                    f.YMm = lp.Point.Y * MmPerFoot;
                    f.ZMm = lp.Point.Z * MmPerFoot;
                }

                var eq = fi.MEPModel as ElectricalEquipment;
                var dist = eq?.DistributionSystem;
                if (eq == null || dist == null)
                {
                    f.Usable = false;
                    f.SkipReason = "no_distribution_system";
                    outRows.Add(f);
                    continue;
                }

                f.DistSystem = dist.Name;
                f.Info.Phases = dist.ElectricalPhase == ElectricalPhase.ThreePhase ? 3 : 1;
                f.Info.PhaseVa = new double[f.Info.Phases];   // per-phase split of
                // existing load is not derivable without slot surgery — treated
                // as balanced; the commit reports actual slots so nothing hides.

                var mains = fi.get_Parameter(BuiltInParameter.RBS_ELEC_MAINS);
                if (mains != null && mains.HasValue && mains.AsDouble() > 1e-9)
                    f.Info.MainsA = UnitUtils.ConvertFromInternalUnits(
                        mains.AsDouble(), UnitTypeId.Amperes);

                double connectedVa = 0;
                var assigned = fi.MEPModel?.GetAssignedElectricalSystems();
                if (assigned != null)
                    foreach (var sys in assigned)
                    {
                        var load = sys.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
                        if (load != null && load.HasValue)
                            connectedVa += UnitUtils.ConvertFromInternalUnits(
                                load.AsDouble(), UnitTypeId.VoltAmperes);
                    }
                f.Info.ConnectedVa = connectedVa;
                f.Usable = true;
                outRows.Add(f);
            }
            return outRows;
        }
    }
}
