// suggest_circuits: which placed devices can be circuited, and at what
// load. THE ft<->mm BOUNDARY — everything leaving here is mm/VA.
//
// A device is skipped with a REASON, never dropped silently: the reasons
// are what CircuitBlockers branches on when nothing survives.

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
        // ─── devices ────────────────────────────────────────────────────

        /// <summary>Instance level, then the host's level, then the schedule-level
        /// parameter. Mirrors SocketPlacement.ResolveLevel — a hosted family
        /// carries no LevelId of its own. Shared with CircuitInventory so the
        /// wall-hosted-socket bug documented in CollectDevices cannot be
        /// re-introduced by a second copy of this walk.</summary>
        internal static string DeviceLevelName(Document d, Element fi)
        {
            var byId = (d.GetElement(fi.LevelId) as Level)?.Name;
            if (!string.IsNullOrEmpty(byId)) return byId!;

            if (fi is FamilyInstance inst && inst.Host != null)
            {
                var hostLevel = (d.GetElement(inst.Host.LevelId) as Level)?.Name;
                if (!string.IsNullOrEmpty(hostLevel)) return hostLevel!;
            }

            var p = fi.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (p != null && p.HasValue)
            {
                var byParam = (d.GetElement(p.AsElementId()) as Level)?.Name;
                if (!string.IsNullOrEmpty(byParam)) return byParam!;
            }
            return "";
        }

        private static readonly Dictionary<string, BuiltInCategory> CategoryWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["electrical_fixtures"] = BuiltInCategory.OST_ElectricalFixtures,
            ["lighting_fixtures"] = BuiltInCategory.OST_LightingFixtures,
            ["lighting_devices"] = BuiltInCategory.OST_LightingDevices,
        };

        private static readonly HashSet<BuiltInCategory> LightingCategories = new()
        {
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_LightingDevices,
        };

        private static (List<ElecDevice> Devices, List<object> Skipped,
                        List<CircuitedDevice> Circuited) CollectDevices(
            Document doc, JsonElement args, double vaSocket, double vaLight)
        {
            var deviceIds = ArgsHelp.GetLongList(args, "device_ids");
            var levelFilter = ArgsHelp.GetString(args, "level");
            var catWords = ArgsHelp.GetStringList(args, "categories");

            var cats = new List<BuiltInCategory>();
            foreach (var w in catWords)
            {
                if (!CategoryWords.TryGetValue(w.Trim(), out var bic))
                    throw new ArgumentException(
                        "unknown category '" + w + "' — supported: " +
                        string.Join(", ", CategoryWords.Keys));
                cats.Add(bic);
            }
            if (cats.Count == 0)
                cats.AddRange(new[]
                {
                    BuiltInCategory.OST_ElectricalFixtures,
                    BuiltInCategory.OST_LightingFixtures,
                });

            var candidates = new List<FamilyInstance>();
            if (deviceIds.Count > 0)
            {
                foreach (var id in deviceIds)
                {
                    if (doc.GetElement(ElemIds.From(id)) is FamilyInstance fi)
                        candidates.Add(fi);
                }
            }
            else
            {
                foreach (var bic in cats)
                    candidates.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .OfType<FamilyInstance>());
            }

            var devices = new List<ElecDevice>();
            var skipped = new List<object>();
            var circuited = new List<CircuitedDevice>();
            void Skip(long id, string reason) => skipped.Add(new Dictionary<string, object?>
            {
                ["id"] = id, ["reason"] = reason,
            });

            foreach (var fi in candidates.OrderBy(fi => fi.Id.Value))
            {
                long id = fi.Id.Value;

                // Explicitly requested ids must exist.
                if (fi.Category == null) { Skip(id, "no_category"); continue; }

                // fi.LevelId alone is NOT enough: a wall-hosted socket — which is
                // every socket place_socket_points creates — reports
                // InvalidElementId, so the name comes back "" and matches no filter.
                // Read it the way SocketPlacement.ResolveLevel does, or every device
                // is dropped below WITHOUT a skipped_devices row.
                var levelName = DeviceLevelName(doc, fi);
                if (levelFilter != null &&
                    !string.Equals(levelName, levelFilter, StringComparison.OrdinalIgnoreCase))
                {
                    // A reported skip, not a silent drop: the caller asked for a
                    // level, and "your filter excluded these" is a different
                    // answer from "there is nothing here".
                    Skip(id, "level_mismatch:" +
                             (string.IsNullOrEmpty(levelName) ? "unknown" : levelName));
                    continue;
                }

                // Electrical connector required — ElectricalSystem.Create
                // rejects members without one, better to say so up front.
                var cm = fi.MEPModel?.ConnectorManager;
                bool hasElec = false;
                if (cm != null)
                    foreach (Connector c in cm.Connectors)
                        if (c.Domain == Domain.DomainElectrical) { hasElec = true; break; }
                if (!hasElec) { Skip(id, "no_electrical_connector"); continue; }

                // A 0 V device makes a 0 V circuit no distribution system serves,
                // and SelectPanel rejects it with wording that READS like a panel
                // problem. Voltage is RBS_ELEC_VOLTAGE on the instance or its type —
                // Connector exposes none in the API. Skip only on an AFFIRMATIVE
                // zero: a family with no voltage parameter is unknown, not broken.
                var voltParam = fi.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (voltParam == null || !voltParam.HasValue)
                    voltParam = fi.Symbol?.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (voltParam != null && voltParam.HasValue &&
                    UnitUtils.ConvertFromInternalUnits(
                        voltParam.AsDouble(), UnitTypeId.Volts) <= 1e-9)
                {
                    Skip(id, "connector_voltage_unset");
                    continue;
                }

                // Already on a power circuit: a device's power connector can
                // only belong to one, and GetElectricalSystems() lists every
                // system this instance is a member of. The owning circuit is
                // captured, not discarded — it is what lets the caller answer
                // "they are already on P1/3" and hand remove_from_circuit the
                // exact ids instead of making the agent hunt for them.
                var systems = fi.MEPModel?.GetElectricalSystems();
                var owner = systems?.FirstOrDefault(
                    s => s.SystemType == ElectricalSystemType.PowerCircuit);
                if (owner != null)
                {
                    circuited.Add(new CircuitedDevice
                    {
                        DeviceId = id,
                        CircuitId = owner.Id.Value,
                        CircuitNumber = SafeCircuitNumber(owner),
                        PanelId = owner.BaseEquipment?.Id.Value,
                        PanelName = owner.BaseEquipment?.Name ?? "",
                    });
                    Skip(id, "already_circuited");
                    continue;
                }

                if (fi.Location is not LocationPoint lp)
                {
                    Skip(id, "no_point_location");
                    continue;
                }

                var bic = (BuiltInCategory)fi.Category.Id.Value;
                bool isLighting = LightingCategories.Contains(bic);

                var declaredVa = ApparentLoadVa(fi);
                double va = declaredVa ?? (isLighting ? vaLight : vaSocket);
                string loadSource = declaredVa.HasValue ? "parameter" : "default_arg";

                devices.Add(new ElecDevice
                {
                    Id = id,
                    XMm = lp.Point.X * MmPerFoot,
                    YMm = lp.Point.Y * MmPerFoot,
                    ZMm = lp.Point.Z * MmPerFoot,
                    Va = va,
                    LoadSource = loadSource,
                    LoadClass = isLighting ? "lighting" : "receptacle",
                    LevelName = levelName,
                });
            }

            // Explicit ids that resolved to nothing at all.
            foreach (var id in deviceIds)
                if (!devices.Any(d => d.Id == id) &&
                    !skipped.OfType<Dictionary<string, object?>>().Any(s => Equals(s["id"], id)))
                    Skip(id, "not_found_or_not_a_family_instance");

            return (devices, skipped, circuited);
        }


    }
}
