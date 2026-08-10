// The plan engine behind plan_panel_assignment — pure, Revit-free, linked
// into Tests. Composes the three existing pure pieces (DistributionSystemMatch
// for seating arithmetic, AssignmentBlocker for findings, slot counts from
// PanelLoad's world) into ONE computed answer for "assign a suitable panel to
// these sockets": which panels work, in what order of preference, and the
// exact ordered tool calls that get there.
//
// WHY. Before this, "a suitable panel" was a model judgement made by trial
// mutation: pick a board, build a circuit, fail on assign, read the verdict —
// and when the sockets were the JKR 0 V case the verdict carried no computed
// next action at all, which is where the create/delete-DB loop lived. This
// file answers the whole question read-only, before anything mutates.
//
// RULES (same as the rest of the suite):
//   * Never a prohibition — every verdict carries its steps, every blocker a
//     fix. A refusal with no next action is what produced the loop.
//   * Convention-derived values are PROPOSALS: is_proposal + needs_user_confirm
//     stay true, the caller shows them for confirmation (dry_run first).
//   * Unknown is never treated as failing (slots, poles) — mirrors
//     PanelAssignmentDiagnosis.
//   * No Revit type may appear in any signature or field: one leak makes the
//     xUnit runner silently skip the whole assembly (see
//     ConnectorElectricalSpec.cs header).
//
// UNITS: volts and mm throughout.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>Supply convention used ONLY to propose values for devices that
    /// carry none. Malaysian LV default. Every value derived from it is
    /// flagged is_proposal.</summary>
    public sealed class SupplyConvention
    {
        public double VoltageV = 240;
        public int Poles = 1;
    }

    /// <summary>One device as the planner sees it — built by the Revit-facing
    /// caller from live connector reads, never inside this file.</summary>
    public sealed class DevicePlanFacts
    {
        public long ElementId;
        public string FamilyName = "";
        /// <summary>Null or &lt;=0 = no usable voltage (the JKR case).</summary>
        public double? VoltageV;
        /// <summary>Echoes get_connector_electrical_data's voltage_source.</summary>
        public string VoltageSource = "";
        public int? Poles;
        public bool HasElectricalConnector = true;
    }

    /// <summary>One candidate panel: identity + slots + the DistSysOption list
    /// PanelTools.BuildDistSysOptions already builds (PanelAccepts included —
    /// Revit's own verdict, per-panel).</summary>
    public sealed class PanelPlanFacts
    {
        public long PanelId;
        public string Name = "";
        /// <summary>Currently assigned system name, null = none.</summary>
        public string? DistributionSystem;
        /// <summary>0 = unknown; unknown is never "full".</summary>
        public int TotalSlots;
        public int UsedSlots;
        public int? PanelConnectorPoles;
        public string? Level;
        /// <summary>Optional tie-break; null sorts last.</summary>
        public double? DistanceMm;
        public List<DistSysOption> Options = new List<DistSysOption>();
    }

    /// <summary>One tool call, complete. The agent executes these in order —
    /// the steps ARE the plan.</summary>
    public sealed class PlannedStep
    {
        public int Order;
        public string Tool = "";
        public Dictionary<string, object?> Args = new Dictionary<string, object?>();
        public bool Mutates;
        public bool NeedsUserConfirm;
        /// <summary>True when a value came from SupplyConvention rather than
        /// the model — confirm before applying.</summary>
        public bool IsProposal;
        public string Reason = "";
    }

    public static class PanelPlanVerdicts
    {
        public const string AssignableNow = "assignable_now";
        public const string NeedsDistributionSystemSet = "needs_distribution_system_set";
        public const string NeedsDistributionSystemCreated = "needs_distribution_system_created";
        public const string FitsWithPoleChange = "fits_with_pole_change";
        public const string PanelFull = "panel_full";
        public const string PanelRejectsAll = "panel_rejects_all";

        /// <summary>Preference order, best first.</summary>
        public static readonly string[] Order =
        {
            AssignableNow, NeedsDistributionSystemSet, NeedsDistributionSystemCreated,
            FitsWithPoleChange, PanelFull, PanelRejectsAll,
        };
    }

    public sealed class PanelVerdict
    {
        public long PanelId;
        public string Name = "";
        public string Verdict = "";
        /// <summary>1 = best. Deterministic: verdict order, then free slots,
        /// then distance, then id.</summary>
        public int Rank;
        public int? FreeSlots;
        /// <summary>Copied from the facts for the distance tie-break.</summary>
        public double? DistanceMm;
        public MatchResult Match = new MatchResult();
        public List<PlannedStep> Steps = new List<PlannedStep>();
        public string Reason = "";
    }

    /// <summary>One proposed circuit: devices sharing (voltage, poles). A
    /// group with no usable voltage carries the connector fix that makes it
    /// circuitable.</summary>
    public sealed class DeviceGroupPlan
    {
        public string FamilyName = "";
        public List<long> ElementIds = new List<long>();
        /// <summary>The demand this group presents — measured, or the
        /// convention when the family carries nothing.</summary>
        public double VoltageV;
        public int Poles;
        /// <summary>"measured" | "convention_proposal"</summary>
        public string DemandSource = "";
        public bool Ready;
        public PlannedStep? ConnectorFix;
    }

    public sealed class PanelPlanResult
    {
        public List<DeviceGroupPlan> Groups = new List<DeviceGroupPlan>();
        /// <summary>Ranked against the PRIMARY group (most devices). A room
        /// with several distinct demands gets a note: re-plan per group with
        /// element_ids.</summary>
        public List<PanelVerdict> Panels = new List<PanelVerdict>();
        public PanelVerdict? Recommended;
        public List<AssignmentBlocker> Blockers = new List<AssignmentBlocker>();
        public string Summary = "";
        public string? Note;
    }

    public static class PanelAssignmentPlan
    {
        public static PanelPlanResult Build(
            IReadOnlyList<DevicePlanFacts> devices,
            IReadOnlyList<PanelPlanFacts> panels,
            SupplyConvention? convention)
        {
            var conv = convention ?? new SupplyConvention();
            var result = new PanelPlanResult();

            var usable = (devices ?? new List<DevicePlanFacts>()).Where(d => d != null).ToList();
            if (usable.Count == 0)
            {
                result.Summary = "no devices to plan for.";
                return result;
            }

            BuildGroups(usable, conv, result);

            var primary = result.Groups.OrderByDescending(g => g.ElementIds.Count)
                                       .ThenBy(g => g.ElementIds.FirstOrDefault())
                                       .FirstOrDefault();
            if (primary == null)
            {
                // Every device lacked a connector — the blockers carry it.
                result.Summary = "none of these devices has an electrical connector, so none can "
                               + "be circuited. That is family authoring; the blockers name each family.";
                return result;
            }

            var candidates = (panels ?? new List<PanelPlanFacts>()).Where(p => p != null).ToList();
            if (candidates.Count == 0)
            {
                result.Blockers.Add(new AssignmentBlocker
                {
                    Code = "no_panels_in_model",
                    Detail = "the model contains no electrical equipment to assign to.",
                    Fix = "create_panel — pass distribution_system (names from "
                        + "list_electrical_settings) so the new board can accept circuits.",
                });
                result.Summary = BuildSummaryNoPanels(primary);
                return result;
            }

            foreach (var p in candidates)
                result.Panels.Add(Judge(p, primary, conv));

            RankInPlace(result.Panels);
            result.Recommended = result.Panels.FirstOrDefault(v =>
                v.Verdict != PanelPlanVerdicts.PanelFull &&
                v.Verdict != PanelPlanVerdicts.PanelRejectsAll);

            result.Summary = BuildSummary(result, primary);
            if (result.Groups.Count > 1)
            {
                var demands = result.Groups.Select(g => $"{Fmt(g.VoltageV)} V {g.Poles}-pole").Distinct().ToList();
                if (demands.Count > 1)
                    result.Note = "this room presents more than one demand (" + string.Join(", ", demands)
                                + "). The ranking above is for the largest group; plan the other group(s) "
                                + "separately by calling plan_panel_assignment with their element_ids — "
                                + "they may legitimately land on a different board.";
            }
            return result;
        }

        // ─── grouping ───────────────────────────────────────────────────

        private static void BuildGroups(List<DevicePlanFacts> devices, SupplyConvention conv,
                                        PanelPlanResult result)
        {
            foreach (var noConn in devices.Where(d => !d.HasElectricalConnector)
                                          .GroupBy(d => d.FamilyName ?? "")
                                          .OrderBy(g => g.Key))
            {
                result.Blockers.Add(new AssignmentBlocker
                {
                    Code = "no_electrical_connector",
                    Detail = $"family '{noConn.Key}' ({noConn.Count()} instance(s): "
                           + string.Join(", ", noConn.Select(d => d.ElementId).OrderBy(i => i)) + ") "
                           + "has no electrical connector, so it can never be circuited.",
                    Fix = "the family needs a power connector authored into it — "
                        + "set_connector_electrical_data cannot add one. Confirm with "
                        + "get_connector_electrical_data(family_name), then this is a family "
                        + "authoring task for the drafter.",
                });
            }

            var connected = devices.Where(d => d.HasElectricalConnector).ToList();

            // Measured demand: group by (rounded voltage, poles) — one circuit
            // per group, matching create_circuit's one-voltage-per-circuit shape.
            foreach (var g in connected.Where(d => d.VoltageV is > 0)
                                       .GroupBy(d => (V: Math.Round(d.VoltageV!.Value, 0),
                                                      P: d.Poles ?? 1))
                                       .OrderBy(g => g.Key.V).ThenBy(g => g.Key.P))
            {
                result.Groups.Add(new DeviceGroupPlan
                {
                    FamilyName = string.Join(", ", g.Select(d => d.FamilyName).Distinct().OrderBy(n => n)),
                    ElementIds = g.Select(d => d.ElementId).OrderBy(i => i).ToList(),
                    VoltageV = g.Key.V,
                    Poles = g.Key.P,
                    DemandSource = "measured",
                    Ready = true,
                });
            }

            // No usable voltage: one fix group per FAMILY, because the fix is
            // a family edit — set_connector_electrical_data operates on the
            // whole family, and its blast radius is why the step is gated.
            foreach (var g in connected.Where(d => d.VoltageV is not > 0)
                                       .GroupBy(d => d.FamilyName ?? "")
                                       .OrderBy(g => g.Key))
            {
                var group = new DeviceGroupPlan
                {
                    FamilyName = g.Key,
                    ElementIds = g.Select(d => d.ElementId).OrderBy(i => i).ToList(),
                    VoltageV = conv.VoltageV,
                    Poles = conv.Poles,
                    DemandSource = "convention_proposal",
                    Ready = false,
                    ConnectorFix = new PlannedStep
                    {
                        Order = 0,
                        Tool = "set_connector_electrical_data",
                        Args = new Dictionary<string, object?>
                        {
                            ["family_name"] = g.Key,
                            ["voltage_v"] = conv.VoltageV,
                            ["poles"] = conv.Poles,
                            ["system_type"] = conv.Poles >= 3 ? "power_balanced" : "power_unbalanced",
                        },
                        Mutates = true,
                        NeedsUserConfirm = true,
                        IsProposal = true,
                        Reason = $"the connectors of '{g.Key}' carry no voltage "
                               + $"(voltage_source: {FirstSource(g)}), so no panel can seat their "
                               + $"circuit. {Fmt(conv.VoltageV)} V {conv.Poles}-pole is the supplied "
                               + "convention — confirm it with the drafter, and run with dry_run:true "
                               + "first to show the instance/circuit impact. This reloads the family "
                               + "and changes every instance in the model.",
                    },
                };
                result.Groups.Add(group);
            }
        }

        private static string FirstSource(IEnumerable<DevicePlanFacts> g) =>
            g.Select(d => d.VoltageSource).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "absent";

        // ─── judging one panel against the primary demand ───────────────

        private static PanelVerdict Judge(PanelPlanFacts p, DeviceGroupPlan demandGroup,
                                          SupplyConvention conv)
        {
            var demand = new CircuitDemand
            {
                VoltageV = demandGroup.VoltageV,
                Poles = demandGroup.Poles,
                PanelConnectorPoles = p.PanelConnectorPoles,
                DefaultVoltageV = conv.VoltageV,
                DefaultPoles = conv.Poles,
            };
            var match = DistributionSystemMatch.Solve(demand, p.Options);

            var v = new PanelVerdict
            {
                PanelId = p.PanelId,
                Name = p.Name,
                Match = match,
                FreeSlots = p.TotalSlots > 0 ? p.TotalSlots - p.UsedSlots : (int?)null,
                DistanceMm = p.DistanceMm,
            };

            // Unknown slot count is not "full" — same rule as the diagnosis.
            var full = p.TotalSlots > 0 && p.UsedSlots + demandGroup.Poles > p.TotalSlots;
            if (full)
            {
                v.Verdict = PanelPlanVerdicts.PanelFull;
                v.Reason = $"{p.UsedSlots}/{p.TotalSlots} slots used — a {demandGroup.Poles}-pole "
                         + "breaker does not fit. The one case where another existing board is the "
                         + "genuine answer; delete_circuit on an empty circuit also frees a slot.";
                return v;
            }

            if (match.AssignableNow.Count > 0)
            {
                var current = match.AssignableNow.FirstOrDefault(f =>
                    string.Equals(f.Name, p.DistributionSystem, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    v.Verdict = PanelPlanVerdicts.AssignableNow;
                    v.Reason = $"its system '{current.Name}' seats {Fmt(demandGroup.VoltageV)} V at "
                             + $"{demandGroup.Poles} pole(s) as-is.";
                    v.Steps = StepsFor(v, demandGroup, null, null);
                }
                else
                {
                    var best = match.AssignableNow[0];
                    v.Verdict = PanelPlanVerdicts.NeedsDistributionSystemSet;
                    v.Reason = p.DistributionSystem == null
                        ? $"the panel has NO distribution system (why it refuses everything); "
                        + $"'{best.Name}' exists in the model, the panel accepts it, and it seats "
                        + "this circuit."
                        : $"its current system '{p.DistributionSystem}' does not seat this circuit, "
                        + $"but '{best.Name}' does and the panel accepts it.";
                    v.Steps = StepsFor(v, demandGroup, best.Name, null);
                }
                return v;
            }

            if (match.Create != null && match.PanelRejects.Count < Math.Max(1, p.Options.Count))
            {
                v.Verdict = PanelPlanVerdicts.NeedsDistributionSystemCreated;
                v.Reason = p.Options.Count == 0
                    ? "the model defines no distribution systems at all; `create` holds the spec."
                    : "nothing existing seats this circuit on this panel; `create` holds the spec.";
                v.Steps = StepsFor(v, demandGroup, match.Create.Name, match.Create);
                return v;
            }

            if (match.FitsWithPoleChange.Count > 0)
            {
                var f = match.FitsWithPoleChange[0];
                v.Verdict = PanelPlanVerdicts.FitsWithPoleChange;
                v.Reason = $"'{f.Name}' seats {Fmt(demandGroup.VoltageV)} V only at "
                         + $"{f.PolesRequired} pole(s), not the {demandGroup.Poles} the devices have. "
                         + "A pole change is a family edit (set_connector_electrical_data) — ranked "
                         + "below boards that need no family change.";
                return v;
            }

            v.Verdict = PanelPlanVerdicts.PanelRejectsAll;
            v.Reason = match.PanelRejects.Count > 0
                ? "this panel rejects every distribution system that could seat the circuit "
                + "(Revit's own IsValidDistributionSystem)."
                : "no distribution system, existing or creatable, seats this circuit here.";
            return v;
        }

        /// <summary>The ordered tool calls for a verdict. The connector fix
        /// (when the group needs one) is ALWAYS first — everything downstream
        /// reads the voltage it writes.</summary>
        private static List<PlannedStep> StepsFor(PanelVerdict v, DeviceGroupPlan group,
                                                  string? systemName, SystemToCreate? create)
        {
            var steps = new List<PlannedStep>();

            if (group.ConnectorFix != null)
                steps.Add(group.ConnectorFix);

            if (create != null)
                steps.Add(new PlannedStep
                {
                    Tool = "create_distribution_system",
                    Args = new Dictionary<string, object?>
                    {
                        ["name"] = create.Name,
                        ["phase"] = create.Phase,
                        ["line_to_ground_v"] = create.LineToGroundV,
                        ["line_to_line_v"] = create.LineToLineV,
                    },
                    Mutates = true,
                    NeedsUserConfirm = true,
                    IsProposal = group.DemandSource == "convention_proposal",
                    Reason = "no existing distribution system seats this circuit; this spec does. "
                           + "Skip if an equivalent system already exists under another name.",
                });

            if (systemName != null)
                steps.Add(new PlannedStep
                {
                    Tool = "set_distribution_system",
                    Args = new Dictionary<string, object?>
                    {
                        ["panel_id"] = v.PanelId,
                        ["distribution_system"] = systemName,
                    },
                    Mutates = true,
                    NeedsUserConfirm = true,
                    IsProposal = false,
                    Reason = $"points '{v.Name}' at '{systemName}' so it can accept the circuit.",
                });

            steps.Add(new PlannedStep
            {
                Tool = "create_circuit",
                Args = new Dictionary<string, object?>
                {
                    ["element_ids"] = group.ElementIds,
                    ["panel_id"] = v.PanelId,
                    ["circuit_type"] = "power",
                },
                Mutates = true,
                NeedsUserConfirm = true,
                IsProposal = false,
                Reason = "one circuit for the whole group, seated on the panel at creation — "
                       + "no separate assign_panel needed for a new circuit.",
            });

            for (int i = 0; i < steps.Count; i++) steps[i].Order = i + 1;
            return steps;
        }

        // ─── ranking ────────────────────────────────────────────────────

        private static void RankInPlace(List<PanelVerdict> panels)
        {
            var order = PanelPlanVerdicts.Order.ToList();
            var sorted = panels
                .OrderBy(v => { var i = order.IndexOf(v.Verdict); return i < 0 ? order.Count : i; })
                .ThenByDescending(v => v.FreeSlots ?? -1)
                .ThenBy(v => v.DistanceMm ?? double.MaxValue)
                .ThenBy(v => v.PanelId)
                .ToList();
            panels.Clear();
            panels.AddRange(sorted);
            for (int i = 0; i < panels.Count; i++) panels[i].Rank = i + 1;
        }

        // ─── summaries ──────────────────────────────────────────────────

        private static string BuildSummary(PanelPlanResult r, DeviceGroupPlan primary)
        {
            var rec = r.Recommended;
            var demand = $"{Fmt(primary.VoltageV)} V {primary.Poles}-pole"
                       + (primary.DemandSource == "convention_proposal"
                          ? " (a supply CONVENTION — confirm before applying)" : "");
            if (rec == null)
                return $"no panel can currently take the {demand} circuit — every board is full or "
                     + "rejects every seating system. The per-panel reasons say which; panel_full is "
                     + "the one case where a new board is the genuine answer.";
            return $"'{rec.Name}' (rank 1, {rec.Verdict}) is the computed choice for the {demand} "
                 + $"circuit of {primary.ElementIds.Count} device(s). Execute its steps in order; "
                 + "every mutating step shows for confirmation.";
        }

        private static string BuildSummaryNoPanels(DeviceGroupPlan primary) =>
            $"the model has no electrical equipment at all, so the {Fmt(primary.VoltageV)} V "
          + $"{primary.Poles}-pole circuit has nowhere to land. create_panel (with a "
          + "distribution_system) is the next action — see blockers.";

        private static string Fmt(double v) =>
            Math.Round(v, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
