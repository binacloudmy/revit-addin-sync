// plan_panel_assignment — the read-only pre-flight for "assign a suitable
// panel to the sockets in this room".
//
// Layer 1 composite READ (precedent: get_scene_overview). Not Layer 2:
// MepStepChain is a write concept, and this tool performs ZERO writes — no
// Transaction anywhere in this file. It gathers live facts (RoomScope for
// membership, ConnectorElectricalTools.ReadLiveFacts per device,
// PanelTools.BuildDistSysOptions/PanelSummaryRow per panel) and hands them to
// the pure engine in PanelAssignmentPlan.cs, which owns grouping, verdicts,
// ranking and step synthesis — and is unit-tested there.
//
// Before this existed, "a suitable panel" was discovered by trial mutation:
// build a circuit, fail on assign_panel, read the verdict — and the JKR 0 V
// case got no computed verdict at all, which is where the create/delete-DB
// loop lived (UAT 2026-08-07). One call now answers the whole question before
// anything mutates; the agent executes the returned steps, each individually
// confirmed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class PanelPlanTools
    {
        /// <summary>Read-only. args: { room_id?, room_name?, element_ids?,
        /// category? ("Electrical Fixtures"), default_voltage_v? (240),
        /// default_poles? (1), max_panels? (30) }.</summary>
        public static Dictionary<string, object?> PlanPanelAssignment(Document doc, JsonElement args)
        {
            try
            {
                var explicitIds = ArgsHelp.GetLongList(args, "element_ids");
                var roomId = ArgsHelp.GetLong(args, "room_id");
                var roomName = ArgsHelp.GetString(args, "room_name");
                var categoryArg = ArgsHelp.GetString(args, "category") ?? "Electrical Fixtures";
                var convention = new SupplyConvention
                {
                    VoltageV = ArgsHelp.GetDouble(args, "default_voltage_v") ?? 240,
                    Poles = (int)(ArgsHelp.GetLong(args, "default_poles") ?? 1),
                };
                var maxPanels = (int)(ArgsHelp.GetLong(args, "max_panels") ?? 30);

                // ── devices: explicit ids, or the room's sockets ──────────
                Room? room = null;
                List<Element> deviceEls;
                if (explicitIds.Count > 0)
                {
                    var missing = new List<long>();
                    deviceEls = new List<Element>();
                    foreach (var id in explicitIds)
                    {
                        var el = doc.GetElement(ElemIds.From(id));
                        if (el == null) missing.Add(id); else deviceEls.Add(el);
                    }
                    if (missing.Count > 0)
                        return MepTx.Failure($"element(s) not found: {string.Join(", ", missing)}");
                }
                else
                {
                    room = RoomScope.Resolve(doc, roomId, roomName, out var whyNot);
                    if (room == null)
                        return MepTx.Blocked("room_not_found",
                            whyNot ?? "pass room_id, room_name or element_ids");

                    var bic = CategoryResolve.Resolve(categoryArg);
                    if (bic == null)
                        return MepTx.Failure($"unknown category '{categoryArg}'");
                    deviceEls = RoomScope.ElementsIn(doc, room, bic.Value);
                    if (deviceEls.Count == 0)
                        return MepTx.Blocked("no_sockets_in_room",
                            $"room '{RoomLabel(room)}' contains no {categoryArg}. "
                            + "list_rooms confirms the room; suggest_socket_points proposes "
                            + "placements if the room genuinely has none yet.");
                }

                var devices = deviceEls.Select(DeviceFacts).ToList();

                // ── panels ────────────────────────────────────────────────
                var allPanels = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .ToList();
                var origin = RoomOrigin(room) ?? FirstLocation(deviceEls);
                var panelFacts = allPanels
                    .Select(p => PanelFacts(doc, p, origin))
                    .OrderBy(p => p.DistanceMm ?? double.MaxValue)
                    .ThenBy(p => p.PanelId)
                    .Take(maxPanels)
                    .ToList();

                if (allPanels.Count == 0)
                    // The engine also reports this as a blocker; Blocked here
                    // keeps the top-level shape consistent with the other
                    // nothing-to-do reads.
                    return MepTx.Blocked("no_panels_in_model",
                        "the model contains no electrical equipment to assign to. create_panel "
                        + "(pass distribution_system — names from list_electrical_settings) is "
                        + "the next action.",
                        new Dictionary<string, object?>
                        {
                            ["sockets_found"] = devices.Count,
                        });

                // ── the pure engine ───────────────────────────────────────
                var plan = PanelAssignmentPlan.Build(devices, panelFacts, convention);

                var row = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["sockets"] = new Dictionary<string, object?>
                    {
                        ["count"] = devices.Count,
                        ["element_ids"] = devices.Select(d => d.ElementId).OrderBy(i => i).ToList(),
                        ["groups"] = plan.Groups.Select(GroupRow).ToList(),
                    },
                    ["panels"] = plan.Panels.Select(VerdictRow).ToList(),
                    ["recommended"] = plan.Recommended == null ? null : new Dictionary<string, object?>
                    {
                        ["panel_id"] = plan.Recommended.PanelId,
                        ["name"] = plan.Recommended.Name,
                        ["verdict"] = plan.Recommended.Verdict,
                    },
                    ["blockers"] = plan.Blockers.Select(b => new Dictionary<string, object?>
                    {
                        ["code"] = b.Code, ["detail"] = b.Detail, ["fix"] = b.Fix,
                    }).ToList(),
                    ["summary"] = plan.Summary,
                };
                if (room != null)
                    row["room"] = new Dictionary<string, object?>
                    {
                        ["id"] = room.Id.Value,
                        ["name"] = RoomLabel(room),
                        ["level"] = CircuitDriver.Try(() => room.Level?.Name),
                    };
                if (allPanels.Count > panelFacts.Count)
                    row["panels_truncated"] = $"{allPanels.Count} boards in the model, nearest "
                                            + $"{panelFacts.Count} ranked — raise max_panels to widen.";
                if (plan.Note != null) row["note"] = plan.Note;
                return row;
            }
            catch (Exception ex) { return MepTx.Failure(ex.Message); }
        }

        // ─── fact builders (all guarded reads) ──────────────────────────

        private static DevicePlanFacts DeviceFacts(Element el)
        {
            var (voltage, source, poles, systemType, hasConn) =
                ConnectorElectricalTools.ReadLiveFacts(el);
            return new DevicePlanFacts
            {
                ElementId = el.Id.Value,
                FamilyName = (el as FamilyInstance)?.Symbol?.FamilyName
                             ?? MepElementInfo.SafeName(el),
                VoltageV = voltage,
                VoltageSource = source,
                Poles = poles,
                HasElectricalConnector = hasConn,
            };
        }

        private static PanelPlanFacts PanelFacts(Document doc, Element panel, XYZ? origin)
        {
            var summary = PanelTools.PanelSummaryRow(doc, panel);
            var facts = new PanelPlanFacts
            {
                PanelId = panel.Id.Value,
                Name = summary["name"] as string ?? "",
                DistributionSystem = summary["distribution_system"] as string,
                TotalSlots = summary["total_slots"] as int? ?? 0,
                UsedSlots = summary["used_slots"] as int? ?? 0,
                PanelConnectorPoles = summary["panel_connector_poles"] as int?,
                Level = summary["level"] as string,
                Options = PanelTools.BuildDistSysOptions(doc, PanelTools.AsEquipment(panel)),
            };
            try
            {
                if (origin != null && panel.Location is LocationPoint lp)
                    facts.DistanceMm = Math.Round(lp.Point.DistanceTo(origin) * 304.8, 0);
            }
            catch { }
            return facts;
        }

        private static XYZ? RoomOrigin(Room? room)
        {
            try { return (room?.Location as LocationPoint)?.Point; } catch { return null; }
        }

        private static XYZ? FirstLocation(List<Element> els)
        {
            foreach (var el in els)
                if (el.Location is LocationPoint lp) return lp.Point;
            return null;
        }

        private static string RoomLabel(Room room) =>
            CircuitDriver.Try(() => room.Name) ?? $"room {room.Id.Value}";

        // ─── serialization ──────────────────────────────────────────────

        private static Dictionary<string, object?> GroupRow(DeviceGroupPlan g)
        {
            var row = new Dictionary<string, object?>
            {
                ["family_name"] = g.FamilyName,
                ["element_ids"] = g.ElementIds,
                ["voltage_v"] = g.VoltageV,
                ["poles"] = g.Poles,
                ["demand_source"] = g.DemandSource,
                ["ready"] = g.Ready,
            };
            if (g.ConnectorFix != null) row["connector_fix"] = StepRow(g.ConnectorFix);
            return row;
        }

        private static Dictionary<string, object?> VerdictRow(PanelVerdict v) => new()
        {
            ["panel_id"] = v.PanelId,
            ["name"] = v.Name,
            ["rank"] = v.Rank,
            ["verdict"] = v.Verdict,
            ["reason"] = v.Reason,
            ["free_slots"] = v.FreeSlots,
            ["distance_mm"] = v.DistanceMm,
            ["match"] = new Dictionary<string, object?>
            {
                ["summary"] = v.Match.Summary,
                ["assignable_now"] = v.Match.AssignableNow.Select(FitRow).ToList(),
                ["fits_with_pole_change"] = v.Match.FitsWithPoleChange.Select(FitRow).ToList(),
                ["panel_rejects"] = v.Match.PanelRejects,
                ["create"] = v.Match.Create == null ? null : new Dictionary<string, object?>
                {
                    ["tool"] = "create_distribution_system",
                    ["name"] = v.Match.Create.Name,
                    ["phase"] = v.Match.Create.Phase,
                    ["line_to_ground_v"] = v.Match.Create.LineToGroundV,
                    ["line_to_line_v"] = v.Match.Create.LineToLineV,
                },
            },
            ["steps"] = v.Steps.Select(StepRow).ToList(),
        };

        private static Dictionary<string, object?> FitRow(SystemFit f) => new()
        {
            ["id"] = f.Id,
            ["name"] = f.Name,
            ["poles_required"] = f.PolesRequired,
            ["fits_as_is"] = f.FitsAsIs,
            ["seat_voltage_v"] = f.SeatVoltageV,
        };

        private static Dictionary<string, object?> StepRow(PlannedStep s) => new()
        {
            ["order"] = s.Order,
            ["tool"] = s.Tool,
            ["args"] = s.Args,
            ["mutates"] = s.Mutates,
            ["needs_user_confirm"] = s.NeedsUserConfirm,
            ["is_proposal"] = s.IsProposal,
            ["reason"] = s.Reason,
        };
    }
}
