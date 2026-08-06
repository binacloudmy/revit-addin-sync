// Panel CRUD — Layer 1.
//
// create_panel, get_panel_schedule, get_panel_load_summary,
// list_circuits_on_panel, assign_panel, rebalance_panel_loads, delete_panel.
//
// READS ARE READ-ONLY BY DESIGN. The full slot layout (spares, spaces, locked
// and grouped slots) only exists on a PanelScheduleView, and getting one may
// mean CREATING a view — a document write. Firing a Ya/Tidak confirm card on
// what the drafter asked as a QUESTION trains them to reflex-tap Ya, so the
// default path enumerates the panel's circuits through
// ElectricalSystem.BaseEquipment and touches no transaction. Pass
// use_schedule_view:true to read an EXISTING panel schedule; if none exists
// the tool says so rather than quietly creating one.
//
// rebalance_panel_loads is the exception that needs a view: there is no
// settable ElectricalSystem.StartSlot, so a slot move can only go through
// PanelScheduleView.MoveSlotTo. It therefore proposes by default
// (apply:false) and only writes when explicitly told to.
//
// All the arithmetic — which phase a slot is on, whether a breaker fits,
// which move helps — is PanelLoad.cs, which is Revit-free and unit-tested.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class PanelTools
    {
        // ─── create_panel ───────────────────────────────────────────────

        /// <summary>Place an electrical equipment instance and, when asked,
        /// point it at a distribution system.
        ///
        /// The distribution system matters more than it looks: a panel without
        /// one accepts no circuit, and Revit's rejection message reads like a
        /// panel mismatch — which historically sent the agent swapping panels
        /// in a loop instead of fixing the board. So this tool sets it at
        /// creation when it can, and says plainly when it could not.</summary>
        public static Dictionary<string, object?> CreatePanel(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var typeName = ArgsHelp.GetString(args, "family_type")
                    ?? throw new ArgumentException("missing family_type "
                        + "(use list_family_types(\"OST_ElectricalEquipment\"))");
                var point = ArgsHelp.GetPointMm(args, "xyz_mm")
                    ?? throw new ArgumentException("missing xyz_mm");

                var symbol = SocketPlacement.ResolveSymbol(doc, typeName, BuiltInCategory.OST_ElectricalEquipment)
                    ?? SocketPlacement.ResolveSymbol(doc, typeName)
                    ?? throw new ArgumentException(
                        $"family type '{typeName}' not found "
                        + "(use list_family_types(\"OST_ElectricalEquipment\"))");

                var levelName = ArgsHelp.GetString(args, "level");
                var level = ResolveLevel(doc, levelName);
                if (levelName != null && level == null)
                    return MepTx.Failure($"level '{levelName}' not found (use list_levels)");

                var distName = ArgsHelp.GetString(args, "distribution_system");

                tx = new Transaction(doc, "BINA: create_panel");
                TxGuard.StartSwallowing(tx);

                if (!symbol.IsActive) symbol.Activate();

                var panel = level != null
                    ? doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);

                var row = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = panel.Id.Value,
                    ["family_type"] = symbol.Name,
                    ["level"] = level?.Name,
                };

                if (distName != null)
                {
                    // Regenerate first: the ElectricalEquipment wrapper reads
                    // state Revit has not computed for a just-placed instance.
                    doc.Regenerate();
                    var applied = TrySetDistributionSystem(doc, panel, distName, out var reason);
                    row["distribution_system"] = applied ? distName : null;
                    if (!applied) row["distribution_system_error"] = reason;
                }
                else
                {
                    row["note"] = "no distribution_system set — a panel without one accepts no circuit, "
                                + "and Revit's refusal reads like a panel mismatch. "
                                + "Use list_electrical_settings to see the options, then set it.";
                }

                TxGuard.CommitOrThrow(tx);
                return row;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── get_panel_schedule ─────────────────────────────────────────

        public static Dictionary<string, object?> GetPanelSchedule(Document doc, JsonElement args)
        {
            var panelId = ArgsHelp.GetLong(args, "panel_id")
                ?? throw new ArgumentException("missing panel_id");
            var panel = doc.GetElement(ElemIds.From(panelId))
                ?? throw new ArgumentException($"panel {panelId} not found");

            var circuits = CircuitDriver.CircuitsFedBy(doc, panel);
            var spec = ReadSpec(doc, panel);
            var loads = circuits.Select(ToLoad).ToList();
            var phaseVa = PanelLoad.PhaseLoads(loads, spec);

            var row = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["panel_id"] = panelId,
                ["panel_name"] = MepElementInfo.SafeName(panel),
                ["distribution_system"] = DistributionSystemName(panel),
                ["phase_count"] = spec.PhaseCount,
                ["total_slots"] = spec.TotalSlots,
                ["circuit_count"] = circuits.Count,
                ["used_slots"] = PanelLoad.OccupiedSlots(loads).Count,
                ["phase_load_va"] = PhaseRow(phaseVa),
                ["imbalance_pct"] = Math.Round(PanelLoad.ImbalancePct(phaseVa), 1),
                ["circuits"] = circuits.Select(c => CircuitRow(c, spec)).ToList(),
                ["source"] = "circuits",
            };

            if (ArgsHelp.GetBool(args, "use_schedule_view") == true)
            {
                var view = FindScheduleView(doc, panel);
                if (view == null)
                {
                    // Deliberately NOT creating one: a read must not write.
                    row["schedule_view"] = null;
                    row["schedule_view_note"] =
                        "no panel schedule view exists for this panel, and this tool will not create one "
                        + "(that would be a model change on a read). Create it in Revit, or work from the "
                        + "circuit list above — it has everything except spares, spaces and locked slots.";
                }
                else
                {
                    row["schedule_view"] = ScheduleViewRow(view, spec);
                    row["source"] = "panel_schedule_view";
                }
            }

            return row;
        }

        // ─── get_panel_load_summary ─────────────────────────────────────

        public static Dictionary<string, object?> GetPanelLoadSummary(Document doc, JsonElement args)
        {
            var panelId = ArgsHelp.GetLong(args, "panel_id")
                ?? throw new ArgumentException("missing panel_id");
            var panel = doc.GetElement(ElemIds.From(panelId))
                ?? throw new ArgumentException($"panel {panelId} not found");

            var circuits = CircuitDriver.CircuitsFedBy(doc, panel);
            var spec = ReadSpec(doc, panel);
            var loads = circuits.Select(ToLoad).ToList();
            var phaseVa = PanelLoad.PhaseLoads(loads, spec);
            var totalVa = loads.Sum(l => l.LoadVa);
            var usedSlots = PanelLoad.OccupiedSlots(loads).Count;

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["panel_id"] = panelId,
                ["panel_name"] = MepElementInfo.SafeName(panel),
                ["distribution_system"] = DistributionSystemName(panel),
                ["phase_count"] = spec.PhaseCount,
                ["circuit_count"] = circuits.Count,
                ["connected_load_va"] = Math.Round(totalVa, 1),
                ["phase_load_va"] = PhaseRow(phaseVa),
                ["imbalance_pct"] = Math.Round(PanelLoad.ImbalancePct(phaseVa), 1),
                ["total_slots"] = spec.TotalSlots,
                ["used_slots"] = usedSlots,
                ["free_slots"] = Math.Max(0, spec.TotalSlots - usedSlots),
                // Said plainly rather than implied by a percentage: a panel's
                // rated capacity is not on the API, so this is CONNECTED load,
                // not demand load, and it is not a compliance check.
                ["note"] = "connected load only — no demand factors applied, and the panel's rated "
                         + "capacity is not exposed by the Revit API, so this is not a capacity check.",
            };
        }

        // ─── list_circuits_on_panel ─────────────────────────────────────

        public static Dictionary<string, object?> ListCircuitsOnPanel(Document doc, JsonElement args)
        {
            var panelId = ArgsHelp.GetLong(args, "panel_id")
                ?? throw new ArgumentException("missing panel_id");
            var panel = doc.GetElement(ElemIds.From(panelId))
                ?? throw new ArgumentException($"panel {panelId} not found");

            var spec = ReadSpec(doc, panel);
            var circuits = CircuitDriver.CircuitsFedBy(doc, panel);
            if (circuits.Count == 0)
                return MepTx.Blocked("no_circuits_on_panel",
                    $"panel {panelId} ({MepElementInfo.SafeName(panel)}) feeds no circuits yet");

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["panel_id"] = panelId,
                ["panel_name"] = MepElementInfo.SafeName(panel),
                ["count"] = circuits.Count,
                ["circuits"] = circuits
                    .OrderBy(c => CircuitDriver.TryI(() => c.StartSlot) ?? int.MaxValue)
                    .Select(c => CircuitRow(c, spec))
                    .ToList(),
            };
        }

        // ─── assign_panel ───────────────────────────────────────────────

        /// <summary>Move a circuit to a different panel (or detach it).
        /// Detaching is explicit: pass panel_id:null with detach:true, so a
        /// missing argument can never silently orphan a circuit.</summary>
        public static Dictionary<string, object?> AssignPanel(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var circuitId = ArgsHelp.GetLong(args, "circuit_id")
                    ?? throw new ArgumentException("missing circuit_id");
                if (doc.GetElement(ElemIds.From(circuitId)) is not ElectricalSystem c)
                    return MepTx.Failure($"element {circuitId} is not an electrical circuit");

                var panelId = ArgsHelp.GetLong(args, "panel_id");
                var detach = ArgsHelp.GetBool(args, "detach") ?? false;
                if (!panelId.HasValue && !detach)
                    return MepTx.Failure(
                        "assign_panel: pass panel_id, or detach:true to leave the circuit unassigned");

                var before = CircuitDriver.Try(() => c.PanelName);
                var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);

                tx = new Transaction(doc, "BINA: assign_panel");
                TxGuard.StartSwallowing(tx);

                AssignPanelCore(doc, driver, c, panelId);

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["circuit_id"] = circuitId,
                    ["panel_before"] = before,
                    ["panel_after"] = CircuitDriver.Try(() => c.PanelName),
                    ["panel_id"] = MepSystemTools.SafeBaseEquipmentId(c),
                    ["circuit_number"] = CircuitDriver.Try(() => c.CircuitNumber),
                    ["start_slot"] = CircuitDriver.TryI(() => c.StartSlot),
                };
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        /// <summary>No transaction, throws. Layer 2 calls this.</summary>
        internal static void AssignPanelCore(Document doc, IMepSystemDriver driver,
            ElectricalSystem circuit, long? panelId)
        {
            driver.SetBaseEquipment(doc, circuit,
                panelId.HasValue ? ElemIds.From(panelId.Value) : null, null);
        }

        // ─── rebalance_panel_loads ──────────────────────────────────────

        /// <summary>Propose (and optionally apply) slot moves that even out the
        /// phase loads. Proposes by default — a rebalance renumbers circuits,
        /// which shows up on every drawing that references them.</summary>
        public static Dictionary<string, object?> RebalancePanelLoads(Document doc, JsonElement args)
        {
            var panelId = ArgsHelp.GetLong(args, "panel_id")
                ?? throw new ArgumentException("missing panel_id");
            var panel = doc.GetElement(ElemIds.From(panelId))
                ?? throw new ArgumentException($"panel {panelId} not found");
            var apply = ArgsHelp.GetBool(args, "apply") ?? false;
            var maxMoves = (int)(ArgsHelp.GetLong(args, "max_moves") ?? 12);

            var spec = ReadSpec(doc, panel);
            var circuits = CircuitDriver.CircuitsFedBy(doc, panel);
            if (circuits.Count == 0)
                return MepTx.Blocked("no_circuits_on_panel",
                    $"panel {panelId} feeds no circuits — nothing to balance");

            var view = FindScheduleView(doc, panel);
            var loads = circuits.Select(c => ToLoad(c, view)).ToList();
            var plan = PanelLoad.Plan(loads, spec, maxMoves);

            // (long) is load-bearing for net48: ElementId.Value is int on the
            // 2024- API and long on 2025+, so without the cast the key type
            // will not match SlotMove.CircuitId.
            var byId = circuits.ToDictionary(c => (long)c.Id.Value, c => c);
            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["panel_id"] = panelId,
                ["panel_name"] = MepElementInfo.SafeName(panel),
                ["phase_count"] = spec.PhaseCount,
                ["before_phase_va"] = PhaseRow(plan.BeforeVa),
                ["after_phase_va"] = PhaseRow(plan.AfterVa),
                ["before_imbalance_pct"] = Math.Round(plan.BeforeImbalancePct, 1),
                ["after_imbalance_pct"] = Math.Round(plan.AfterImbalancePct, 1),
                ["moves"] = plan.Moves.Select(m => new Dictionary<string, object?>
                {
                    ["circuit_id"] = m.CircuitId,
                    ["label"] = byId.TryGetValue(m.CircuitId, out var c) ? CircuitDriver.CircuitLabel(c) : m.Name,
                    ["from_slot"] = m.FromSlot,
                    ["to_slot"] = m.ToSlot,
                    ["from_phase"] = PanelLoad.PhaseLabel(m.FromPhase),
                    ["to_phase"] = PanelLoad.PhaseLabel(m.ToPhase),
                }).ToList(),
                ["move_count"] = plan.Moves.Count,
                ["skipped"] = plan.Skipped.Select(s => new Dictionary<string, object?>
                {
                    ["circuit_id"] = s.Id,
                    ["reason"] = s.Reason,
                }).ToList(),
                ["applied"] = false,
            };
            if (plan.Note != null) result["note"] = plan.Note;

            if (!apply || plan.Moves.Count == 0)
            {
                if (plan.Moves.Count > 0)
                    result["next_step"] = "call again with apply:true to make these moves";
                return result;
            }

            if (view == null)
            {
                // MoveSlotTo lives on PanelScheduleView and there is no settable
                // ElectricalSystem.StartSlot — without a schedule the moves
                // cannot be applied, and creating one here would be a surprise
                // model change on top of the reslotting.
                result["apply_error"] =
                    "no panel schedule view exists for this panel. Slot moves go through "
                    + "PanelScheduleView.MoveSlotTo and there is no settable StartSlot on a circuit, "
                    + "so create the panel schedule in Revit first, then call again with apply:true.";
                return result;
            }

            Transaction? tx = null;
            try
            {
                tx = new Transaction(doc, "BINA: rebalance_panel_loads");
                TxGuard.StartSwallowing(tx);

                var done = new List<Dictionary<string, object?>>();
                var refused = new List<Dictionary<string, object?>>();
                foreach (var m in plan.Moves)
                {
                    try
                    {
                        if (!view.CanMoveSlotTo(m.FromSlot, 0, m.ToSlot, 0))
                        {
                            refused.Add(new Dictionary<string, object?>
                            {
                                ["circuit_id"] = m.CircuitId,
                                ["reason"] = $"Revit refuses slot {m.FromSlot} -> {m.ToSlot} "
                                           + "(locked, grouped, or occupied)",
                            });
                            continue;
                        }
                        view.MoveSlotTo(m.FromSlot, 0, m.ToSlot, 0);
                        done.Add(new Dictionary<string, object?>
                        {
                            ["circuit_id"] = m.CircuitId,
                            ["from_slot"] = m.FromSlot,
                            ["to_slot"] = m.ToSlot,
                        });
                    }
                    catch (Exception ex)
                    {
                        refused.Add(new Dictionary<string, object?>
                        {
                            ["circuit_id"] = m.CircuitId, ["reason"] = ex.Message,
                        });
                    }
                }

                result["applied"] = true;
                result["moved"] = done;
                result["move_refused"] = refused;
                TxGuard.CommitOrThrow(tx);

                // Read the board back rather than reporting the prediction: the
                // plan is arithmetic, this is what the model actually holds.
                var after = CircuitDriver.CircuitsFedBy(doc, panel).Select(c => ToLoad(c, view)).ToList();
                var actual = PanelLoad.PhaseLoads(after, spec);
                result["actual_phase_va"] = PhaseRow(actual);
                result["actual_imbalance_pct"] = Math.Round(PanelLoad.ImbalancePct(actual), 1);
                return result;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── delete_panel ───────────────────────────────────────────────

        /// <summary>Delete a panel. Refuses while circuits are still fed by it
        /// unless cascade:true — deleting a live panel takes its circuits with
        /// it, and the drafter finds out from a drawing, not from us.</summary>
        public static Dictionary<string, object?> DeletePanel(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var panelId = ArgsHelp.GetLong(args, "panel_id")
                    ?? throw new ArgumentException("missing panel_id");
                var panel = doc.GetElement(ElemIds.From(panelId))
                    ?? throw new ArgumentException($"panel {panelId} not found");
                var cascade = ArgsHelp.GetBool(args, "cascade") ?? false;

                var circuits = CircuitDriver.CircuitsFedBy(doc, panel);
                if (circuits.Count > 0 && !cascade)
                    return MepTx.Blocked("panel_has_circuits",
                        $"panel {panelId} still feeds {circuits.Count} circuit(s). Reassign them "
                        + "(swap_panel_and_recircuit or assign_panel), or pass cascade:true to delete "
                        + "the panel and let its circuits go with it.",
                        new Dictionary<string, object?>
                        {
                            ["circuit_ids"] = circuits.Select(c => c.Id.Value).ToList(),
                            ["circuit_labels"] = circuits.Select(CircuitDriver.CircuitLabel).ToList(),
                        });

                var name = MepElementInfo.SafeName(panel);
                var circuitIds = circuits.Select(c => c.Id.Value).ToList();

                tx = new Transaction(doc, "BINA: delete_panel");
                TxGuard.StartSwallowing(tx);

                var deleted = doc.Delete(panel.Id);
                var deletedIds = deleted?.Select(e => (long)e.Value).ToList() ?? new List<long>();

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["panel_id"] = panelId,
                    ["panel_name"] = name,
                    ["cascade"] = cascade,
                    ["circuits_before"] = circuitIds,
                    ["deleted_ids"] = deletedIds,
                    ["circuits_deleted"] = circuitIds.Count(id => deletedIds.Contains(id)),
                };
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── shared helpers ─────────────────────────────────────────────

        internal static CircuitLoad ToLoad(ElectricalSystem c) => ToLoad(c, null);

        internal static CircuitLoad ToLoad(ElectricalSystem c, PanelScheduleView? view)
        {
            var slot = CircuitDriver.TryI(() => c.StartSlot) ?? 0;
            var locked = false;
            if (view != null && slot >= 1)
            {
                // A locked or grouped slot is one Revit will refuse to move, so
                // proposing it wastes the drafter's attention. IsSlotGrouped
                // returns an INT (the group size/id), not a bool — treat any
                // non-zero as grouped.
                try { locked = view.IsSlotLocked(slot, 0) || view.IsSlotGrouped(slot, 0) != 0; }
                catch { locked = false; }
            }
            return new CircuitLoad
            {
                Id = c.Id.Value,
                Name = CircuitDriver.CircuitLabel(c),
                StartSlot = slot,
                Poles = CircuitDriver.TryI(() => c.PolesNumber) ?? 1,
                LoadVa = CircuitDriver.TryD(() => c.ApparentLoad) ?? 0,
                Locked = locked,
            };
        }

        internal static Dictionary<string, object?> CircuitRow(ElectricalSystem c, PanelSpec spec)
        {
            var slot = CircuitDriver.TryI(() => c.StartSlot) ?? 0;
            return new Dictionary<string, object?>
            {
                ["circuit_id"] = c.Id.Value,
                ["label"] = CircuitDriver.CircuitLabel(c),
                ["circuit_number"] = CircuitDriver.Try(() => c.CircuitNumber),
                ["circuit_type"] = CircuitDriver.Try(() => c.SystemType.ToString()),
                ["load_name"] = CircuitDriver.Try(() => c.LoadName),
                ["start_slot"] = slot,
                ["poles"] = CircuitDriver.TryI(() => c.PolesNumber),
                // Computed from the slot rather than read from PhaseLabel:
                // PhaseLabel is blank on plenty of templates, and the slot is
                // what actually determines the phase.
                ["phase"] = PanelLoad.PhaseLabel(PanelLoad.PhaseOfSlot(slot, spec.PhaseCount)),
                ["phase_label_revit"] = CircuitDriver.Try(() => c.PhaseLabel),
                ["rating_a"] = CircuitDriver.TryD(() => c.Rating),
                ["voltage_v"] = CircuitDriver.TryD(() => c.Voltage),
                ["apparent_load_va"] = CircuitDriver.TryD(() => c.ApparentLoad),
                ["wire_type"] = CircuitDriver.WireTypeName(c),
                ["member_count"] = MepSystemTools.MemberIds(c).Count,
            };
        }

        private static Dictionary<string, object?> PhaseRow(double[] va)
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < va.Length; i++)
                row[PanelLoad.PhaseLabel(i)] = Math.Round(va[i], 1);
            return row;
        }

        /// <summary>Slots and phase count off the panel. Both fall back to the
        /// common case rather than failing — a wrong-but-stated default beats
        /// refusing to report anything.</summary>
        internal static PanelSpec ReadSpec(Document doc, Element panel)
        {
            var spec = new PanelSpec();

            var maxCircuits = TryMaxCircuits(panel);
            if (maxCircuits is > 0) spec.TotalSlots = maxCircuits.Value;

            var wires = ParamInt(panel, BuiltInParameter.RBS_ELEC_PANEL_NUMWIRES_PARAM);
            var dist = DistributionSystemOf(panel);
            if (dist != null)
            {
                try { spec.PhaseCount = dist.ElectricalPhase == ElectricalPhase.ThreePhase ? 3 : 1; }
                catch { }
            }
            else if (wires is >= 4)
            {
                spec.PhaseCount = 3;
            }
            return spec;
        }

        /// <summary>ElectricalEquipment is reached through the instance's
        /// MEPModel, NOT constructed. RevitAPI.xml lists a
        /// ctor(FamilyInstance) but it is inaccessible in the reference
        /// assembly — `new ElectricalEquipment(fi)` does not compile on any of
        /// the three payloads. ElectricalEquipment IS the MEPModel of an
        /// electrical-equipment family, so a cast is the supported route.
        /// Returns null for anything that is not one, which is also how this
        /// code tells a panel from a socket.</summary>
        internal static ElectricalEquipment? AsEquipment(Element? panel)
        {
            if (panel is not FamilyInstance fi) return null;
            try { return fi.MEPModel as ElectricalEquipment; }
            catch { return null; }
        }

        private static int? TryMaxCircuits(Element panel)
        {
            try { return AsEquipment(panel)?.MaxNumberOfCircuits; }
            catch { return null; }
        }

        internal static DistributionSysType? DistributionSystemOf(Element panel)
        {
            try { return AsEquipment(panel)?.DistributionSystem; }
            catch { }
            return null;
        }

        internal static string? DistributionSystemName(Element panel)
        {
            var d = DistributionSystemOf(panel);
            if (d != null) { try { return d.Name; } catch { } }
            return CircuitDriver.ParamString(panel,
                BuiltInParameter.RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM);
        }

        internal static bool TrySetDistributionSystem(Document doc, Element panel, string name, out string reason)
        {
            reason = "";
            if (panel is not FamilyInstance fi)
            {
                reason = "not a family instance";
                return false;
            }

            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(DistributionSysType))
                .Cast<DistributionSysType>()
                .ToList();
            var target = candidates.FirstOrDefault(d =>
                string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                reason = $"no distribution system named '{name}' — available: "
                       + string.Join(", ", candidates.Select(d => d.Name));
                return false;
            }

            try
            {
                var eq = AsEquipment(fi);
                if (eq == null)
                {
                    reason = "this family instance is not electrical equipment "
                           + "(its MEPModel is not an ElectricalEquipment), so it has no distribution system";
                    return false;
                }
                // IsValidDistributionSystem first: assigning an incompatible one
                // throws a message that says nothing about which side is wrong.
                if (!eq.IsValidDistributionSystem(target))
                {
                    reason = $"'{name}' is not valid for this panel family "
                           + "(its connector's voltage and phase must match the distribution system)";
                    return false;
                }
                eq.DistributionSystem = target;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        /// <summary>An EXISTING panel schedule for this panel, or null. Never
        /// creates one — see this file's header.</summary>
        internal static PanelScheduleView? FindScheduleView(Document doc, Element panel)
        {
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(PanelScheduleView)))
            {
                if (v is not PanelScheduleView psv) continue;
                try
                {
                    if (psv.IsPanelScheduleTemplate()) continue;
                    if (psv.GetPanel()?.Value == panel.Id.Value) return psv;
                }
                catch { }
            }
            return null;
        }

        private static Dictionary<string, object?> ScheduleViewRow(PanelScheduleView view, PanelSpec spec)
        {
            var spares = new List<int>();
            var spaces = new List<int>();
            var locked = new List<int>();
            for (int slot = 1; slot <= spec.TotalSlots; slot++)
            {
                try { if (view.IsSpare(slot, 0)) spares.Add(slot); } catch { }
                try { if (view.IsSpace(slot, 0)) spaces.Add(slot); } catch { }
                try { if (view.IsSlotLocked(slot, 0)) locked.Add(slot); } catch { }
            }
            return new Dictionary<string, object?>
            {
                ["view_id"] = view.Id.Value,
                ["view_name"] = MepElementInfo.SafeName(view),
                ["spare_slots"] = spares,
                ["space_slots"] = spaces,
                ["locked_slots"] = locked,
            };
        }

        private static int? ParamInt(Element el, BuiltInParameter bip)
        {
            try
            {
                var p = el.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return p.AsInteger();
            }
            catch { return null; }
        }

        private static Level? ResolveLevel(Document doc, string? levelName)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            return levelName == null
                ? levels.FirstOrDefault()
                : levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
