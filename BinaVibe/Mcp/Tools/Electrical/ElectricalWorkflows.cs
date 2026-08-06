// Layer 2 — composite workflows that chain Layer 0/1 writes.
//
// place_and_circuit_device, modify_circuit_workflow, swap_panel_and_recircuit,
// validate_and_repair_system, generate_schedule_report.
//
// Every write here goes through MepStepChain: one TransactionGroup, one
// Transaction per step, one Ctrl+Z, and a failure report that names the step
// and says whether the model was rolled back. Steps call *Core methods, never
// the Layer 0/1 tool entry points — see MepStepChain's header for why that
// distinction is not stylistic.
//
// validate_and_repair_system PROPOSES by default. "Repair" over a model the
// drafter has not looked at is how a validator becomes something people turn
// off; apply:true is an explicit act, and only the repairs that are genuinely
// mechanical are ever offered.
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
    internal static class ElectricalWorkflows
    {
        // ─── place_and_circuit_device ───────────────────────────────────

        /// <summary>Place a device and put it on a circuit in one undo step:
        /// place -> (new circuit | join existing) -> assign panel -> set
        /// properties. Placing and circuiting are Required; the property edits
        /// are BestEffort, because a rejected rating must not undo a correct
        /// circuit.</summary>
        public static Dictionary<string, object?> PlaceAndCircuitDevice(Document doc, JsonElement args)
        {
            var typeName = ArgsHelp.GetString(args, "family_type")
                ?? throw new ArgumentException("missing family_type");
            var point = ArgsHelp.GetPointMm(args, "xyz_mm")
                ?? throw new ArgumentException("missing xyz_mm");
            var hostWallId = ArgsHelp.GetLong(args, "host_wall_id");
            var levelName = ArgsHelp.GetString(args, "level");
            var circuitId = ArgsHelp.GetLong(args, "circuit_id");
            var panelId = ArgsHelp.GetLong(args, "panel_id");

            var symbol = SocketPlacement.ResolveSymbol(doc, typeName)
                ?? throw new ArgumentException(
                    $"family type '{typeName}' not found (use list_family_types)");

            if (circuitId.HasValue && doc.GetElement(ElemIds.From(circuitId.Value)) is not ElectricalSystem)
                return MepTx.Failure($"circuit_id {circuitId} is not an electrical circuit");
            if (panelId.HasValue && doc.GetElement(ElemIds.From(panelId.Value)) is not FamilyInstance)
                return MepTx.Failure($"panel_id {panelId} is not a family instance");

            var level = ResolveLevel(doc, levelName);
            Wall? host = hostWallId.HasValue
                ? doc.GetElement(ElemIds.From(hostWallId.Value)) as Wall
                : null;
            if (hostWallId.HasValue && host == null)
                return MepTx.Failure($"host_wall_id {hostWallId} is not a wall");

            FamilyInstance? placed = null;
            ElectricalSystem? circuit = null;
            var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);

            var steps = new List<ChainStep>
            {
                new ChainStep
                {
                    Name = "place_device",
                    Required = true,
                    Run = () =>
                    {
                        if (!symbol.IsActive) symbol.Activate();
                        placed = host != null && level != null
                            ? doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural)
                            : level != null
                                ? doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                                : doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
                        return new Dictionary<string, object?>
                        {
                            ["created_id"] = placed.Id.Value,
                            ["family_type"] = symbol.Name,
                            ["host_wall_id"] = host?.Id.Value,
                            ["level"] = level?.Name,
                        };
                    },
                },
                new ChainStep
                {
                    Name = circuitId.HasValue ? "add_to_circuit" : "create_circuit",
                    Required = true,
                    Run = () =>
                    {
                        // Revit computes the device's connectors during
                        // regeneration; circuiting a just-placed instance
                        // without this sees no electrical connector at all.
                        doc.Regenerate();

                        if (circuitId.HasValue)
                        {
                            circuit = (ElectricalSystem)doc.GetElement(ElemIds.From(circuitId.Value));
                            MepSystemTools.AddMembersCore(doc, circuit, new[] { placed!.Id });
                        }
                        else
                        {
                            var req = new MepSystemCreateRequest
                            {
                                MemberIds = new[] { placed!.Id },
                                BaseEquipmentId = panelId.HasValue ? ElemIds.From(panelId.Value) : null,
                                SystemTypeName = ArgsHelp.GetString(args, "circuit_type") ?? "power",
                                RawArgs = args,
                            };
                            circuit = (ElectricalSystem)MepSystemTools.CreateCore(doc, driver, req).System;
                        }

                        return new Dictionary<string, object?>
                        {
                            ["circuit_id"] = circuit!.Id.Value,
                            ["label"] = CircuitDriver.CircuitLabel(circuit),
                            ["member_count"] = MepSystemTools.MemberIds(circuit).Count,
                        };
                    },
                },
            };

            // Only when joining an existing circuit does the panel need a
            // separate step — a new one takes it at creation.
            if (panelId.HasValue && circuitId.HasValue)
            {
                steps.Add(new ChainStep
                {
                    Name = "assign_panel",
                    Required = true,
                    Run = () =>
                    {
                        PanelTools.AssignPanelCore(doc, driver, circuit!, panelId);
                        return new Dictionary<string, object?>
                        {
                            ["panel_id"] = panelId,
                            ["panel_name"] = CircuitDriver.Try(() => circuit!.PanelName),
                        };
                    },
                });
            }

            if (HasCircuitEdits(args))
            {
                steps.Add(new ChainStep
                {
                    Name = "set_circuit_properties",
                    Required = false,   // a refused rating must not undo the circuit
                    Run = () =>
                    {
                        var applied = new List<Dictionary<string, object?>>();
                        var refused = new List<Dictionary<string, object?>>();
                        CircuitTools.EditCircuitCore(doc, circuit!, args, applied, refused);
                        return new Dictionary<string, object?>
                        {
                            ["applied"] = applied, ["refused"] = refused,
                        };
                    },
                });
            }

            var result = MepStepChain.Run(doc, "BINA: place_and_circuit_device", steps);
            if (result.TryGetValue("ok", out var ok) && ok is true)
            {
                result["created_id"] = placed?.Id.Value;
                result["circuit_id"] = circuit?.Id.Value;
                result["circuit_label"] = circuit == null ? null : CircuitDriver.CircuitLabel(circuit);
            }
            return result;
        }

        // ─── modify_circuit_workflow ────────────────────────────────────

        /// <summary>Membership, panel and properties in one atomic edit, in
        /// that order — a device must be on the circuit before the panel is
        /// asked to carry its load, and the load must be settled before a
        /// rating is judged. Doing these as three separate tool calls is how a
        /// circuit ends up half-edited when the second one fails.</summary>
        public static Dictionary<string, object?> ModifyCircuitWorkflow(Document doc, JsonElement args)
        {
            var circuitId = ArgsHelp.GetLong(args, "circuit_id")
                ?? throw new ArgumentException("missing circuit_id");
            if (doc.GetElement(ElemIds.From(circuitId)) is not ElectricalSystem circuit)
                return MepTx.Failure($"element {circuitId} is not an electrical circuit");

            var addIds = ArgsHelp.GetLongList(args, "add_element_ids");
            var removeIds = ArgsHelp.GetLongList(args, "remove_element_ids");
            var panelId = ArgsHelp.GetLong(args, "panel_id");
            var detach = ArgsHelp.GetBool(args, "detach_panel") ?? false;
            var hasEdits = HasCircuitEdits(args);

            if (addIds.Count == 0 && removeIds.Count == 0 && !panelId.HasValue && !detach && !hasEdits)
                return MepTx.Failure(
                    "modify_circuit_workflow: nothing to do — pass add_element_ids, remove_element_ids, "
                    + "panel_id, detach_panel, or any of the property fields");

            var overlap = addIds.Intersect(removeIds).ToList();
            if (overlap.Count > 0)
                return MepTx.Failure(
                    $"element(s) {string.Join(", ", overlap)} appear in BOTH add_element_ids and "
                    + "remove_element_ids — the intended outcome is ambiguous, so nothing was changed");

            var before = MepSystemTools.MemberIds(circuit);
            var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);

            // Validation up front, outside the group: a request that cannot
            // succeed should never open a transaction at all.
            var willRemove = removeIds.Where(before.Contains).ToList();
            var willAdd = addIds.Where(i => !before.Contains(i)).ToList();
            if (before.Count > 0 && before.Except(willRemove).Count() + willAdd.Count == 0)
                return MepTx.Failure(
                    "this would leave the circuit with no members, which Revit forbids — "
                    + "use delete_circuit instead (an empty circuit still holds its breaker slot)");

            var missing = willAdd.Concat(willRemove)
                .Where(id => doc.GetElement(ElemIds.From(id)) == null)
                .ToList();
            if (missing.Count > 0)
                return MepTx.Failure($"element(s) not found: {string.Join(", ", missing)}");

            var steps = new List<ChainStep>();

            if (willRemove.Count > 0)
                steps.Add(new ChainStep
                {
                    Name = "remove_members",
                    Required = true,
                    Run = () =>
                    {
                        MepSystemTools.RemoveMembersCore(doc, circuit,
                            willRemove.Select(i => ElemIds.From(i)).ToList());
                        return new Dictionary<string, object?> { ["removed"] = willRemove };
                    },
                });

            if (willAdd.Count > 0)
                steps.Add(new ChainStep
                {
                    Name = "add_members",
                    Required = true,
                    Run = () =>
                    {
                        MepSystemTools.AddMembersCore(doc, circuit,
                            willAdd.Select(i => ElemIds.From(i)).ToList());
                        return new Dictionary<string, object?> { ["added"] = willAdd };
                    },
                });

            if (panelId.HasValue || detach)
                steps.Add(new ChainStep
                {
                    Name = detach ? "detach_panel" : "assign_panel",
                    Required = true,
                    Run = () =>
                    {
                        PanelTools.AssignPanelCore(doc, driver, circuit, detach ? null : panelId);
                        return new Dictionary<string, object?>
                        {
                            ["panel_id"] = MepSystemTools.SafeBaseEquipmentId(circuit),
                            ["panel_name"] = CircuitDriver.Try(() => circuit.PanelName),
                        };
                    },
                });

            if (hasEdits)
                steps.Add(new ChainStep
                {
                    Name = "set_properties",
                    Required = false,
                    Run = () =>
                    {
                        var applied = new List<Dictionary<string, object?>>();
                        var refused = new List<Dictionary<string, object?>>();
                        CircuitTools.EditCircuitCore(doc, circuit, args, applied, refused);
                        return new Dictionary<string, object?> { ["applied"] = applied, ["refused"] = refused };
                    },
                });

            var result = MepStepChain.Run(doc, "BINA: modify_circuit_workflow", steps);
            if (result.TryGetValue("ok", out var ok) && ok is true)
            {
                result["circuit_id"] = circuitId;
                result["label"] = CircuitDriver.CircuitLabel(circuit);
                result["members_before"] = before;
                result["members_after"] = MepSystemTools.MemberIds(circuit);
                result["apparent_load_va"] = CircuitDriver.TryD(() => circuit.ApparentLoad);
            }
            return result;
        }

        // ─── swap_panel_and_recircuit ───────────────────────────────────

        /// <summary>Move every circuit from one panel to another. All or
        /// nothing by default: half a board on each panel is worse than none
        /// moved, because nothing on the drawing says which half.</summary>
        public static Dictionary<string, object?> SwapPanelAndRecircuit(Document doc, JsonElement args)
        {
            var fromId = ArgsHelp.GetLong(args, "from_panel_id")
                ?? throw new ArgumentException("missing from_panel_id");
            var toId = ArgsHelp.GetLong(args, "to_panel_id")
                ?? throw new ArgumentException("missing to_panel_id");
            if (fromId == toId)
                return MepTx.Failure("from_panel_id and to_panel_id are the same panel");

            var from = doc.GetElement(ElemIds.From(fromId))
                ?? throw new ArgumentException($"panel {fromId} not found");
            var to = doc.GetElement(ElemIds.From(toId)) as FamilyInstance
                ?? throw new ArgumentException($"panel {toId} not found or is not a family instance");

            var circuits = CircuitDriver.CircuitsFedBy(doc, from);
            if (circuits.Count == 0)
                return MepTx.Blocked("no_circuits_on_panel",
                    $"panel {fromId} ({MepElementInfo.SafeName(from)}) feeds no circuits — nothing to move");

            var onlyIds = ArgsHelp.GetLongList(args, "circuit_ids");
            if (onlyIds.Count > 0)
                circuits = circuits.Where(c => onlyIds.Contains(c.Id.Value)).ToList();
            if (circuits.Count == 0)
                return MepTx.Failure("none of the given circuit_ids are fed by that panel");

            // Capacity check before anything moves: Revit's "not enough slots"
            // arrives mid-move otherwise, and the rollback message says nothing
            // about which board was short.
            var toSpec = PanelTools.ReadSpec(doc, to);
            var existing = CircuitDriver.CircuitsFedBy(doc, to).Select(PanelTools.ToLoad).ToList();
            var usedSlots = PanelLoad.OccupiedSlots(existing).Count;
            var incomingSlots = circuits.Select(PanelTools.ToLoad).Sum(l => Math.Max(1, l.Poles));
            var allowPartial = ArgsHelp.GetBool(args, "allow_partial") ?? false;

            if (usedSlots + incomingSlots > toSpec.TotalSlots && !allowPartial)
                return MepTx.Blocked("target_panel_too_small",
                    $"panel {toId} has {toSpec.TotalSlots} slot(s), {usedSlots} in use, and these "
                    + $"circuits need {incomingSlots} more. Nothing was moved. Free slots first, or "
                    + "pass allow_partial:true to move what fits and be told what did not.",
                    new Dictionary<string, object?>
                    {
                        ["total_slots"] = toSpec.TotalSlots,
                        ["used_slots"] = usedSlots,
                        ["required_slots"] = incomingSlots,
                    });

            var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);
            var beforeLabels = circuits.ToDictionary(c => (long)c.Id.Value, CircuitDriver.CircuitLabel);

            var steps = circuits.Select(c => new ChainStep
            {
                Name = $"move_{c.Id.Value}",
                // allow_partial makes each move BestEffort, so one refusal
                // leaves the rest moved and is reported by name.
                Required = !allowPartial,
                Run = () =>
                {
                    PanelTools.AssignPanelCore(doc, driver, c, toId);
                    return new Dictionary<string, object?>
                    {
                        ["circuit_id"] = c.Id.Value,
                        ["label_before"] = beforeLabels[c.Id.Value],
                        ["label_after"] = CircuitDriver.CircuitLabel(c),
                        ["circuit_number"] = CircuitDriver.Try(() => c.CircuitNumber),
                    };
                },
            }).ToList();

            var result = MepStepChain.Run(doc, "BINA: swap_panel_and_recircuit", steps);
            result["from_panel_id"] = fromId;
            result["from_panel_name"] = MepElementInfo.SafeName(from);
            result["to_panel_id"] = toId;
            result["to_panel_name"] = MepElementInfo.SafeName(to);
            result["circuit_count"] = circuits.Count;
            result["note"] = "circuit NUMBERS change with the panel — every drawing, schedule and tag "
                           + "that names the old numbers is now out of date.";
            return result;
        }

        // ─── validate_and_repair_system ─────────────────────────────────

        /// <summary>Validate a system, then propose (or apply) the repairs that
        /// are genuinely mechanical. Only two are: dropping members that no
        /// longer exist, and deleting an empty system. Everything else —
        /// disconnected members, overloaded circuits, split networks — is a
        /// design decision and is reported, not "fixed".</summary>
        public static Dictionary<string, object?> ValidateAndRepairSystem(Document doc, JsonElement args)
        {
            var sysId = ArgsHelp.GetLong(args, "system_id")
                ?? throw new ArgumentException("missing system_id");
            if (doc.GetElement(ElemIds.From(sysId)) is not MEPSystem sys)
                return MepTx.Failure($"element {sysId} is not an MEP system");

            var apply = ArgsHelp.GetBool(args, "apply") ?? false;
            var driver = MepSystemDrivers.ForSystem(sys);

            var graph = MepGraphTools.BuildForSystem(doc, sys, maxNodes: 400);
            var report = MepGraphChecks.Validate(graph, new GraphCheckOptions());

            var proposals = new List<Dictionary<string, object?>>();

            var orphans = report.Findings
                .Where(f => f.Code == "orphan_member")
                .SelectMany(f => f.ElementIds)
                .Distinct()
                .ToList();
            if (orphans.Count > 0)
                proposals.Add(new Dictionary<string, object?>
                {
                    ["action"] = "drop_orphan_members",
                    ["element_ids"] = orphans,
                    ["why"] = "these ids are claimed by the system but no longer exist in the model",
                    ["mechanical"] = true,
                });

            var isEmpty = MepSystemTools.MemberIds(sys).Count == 0;
            if (isEmpty)
                proposals.Add(new Dictionary<string, object?>
                {
                    ["action"] = "delete_empty_system",
                    ["system_id"] = sysId,
                    ["why"] = "an empty system still holds its breaker slot or browser entry, and a panel "
                            + "with empty circuits later reports itself full",
                    ["mechanical"] = true,
                });

            // Overload is a circuit-level judgement the graph checks cannot
            // make, so it is added here — and only ever as a report.
            var overloads = new List<Dictionary<string, object?>>();
            if (sys is ElectricalSystem circuit)
            {
                var rating = CircuitDriver.TryD(() => circuit.Rating);
                var current = CircuitDriver.TryD(() => circuit.ApparentCurrent);
                if (rating is > 0 && current is > 0 && current > rating)
                    overloads.Add(new Dictionary<string, object?>
                    {
                        ["circuit_id"] = sysId,
                        ["label"] = CircuitDriver.CircuitLabel(circuit),
                        ["rating_a"] = rating,
                        ["apparent_current_a"] = current,
                        ["over_by_a"] = Math.Round(current.Value - rating.Value, 2),
                    });
            }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["system_id"] = sysId,
                ["domain"] = MepDomains.ToWire(driver.Kind),
                ["valid"] = report.Ok && overloads.Count == 0,
                ["findings"] = report.Findings.Select(MepGraphTools.FindingRow).ToList(),
                ["finding_count"] = report.Findings.Count,
                ["overloaded_circuits"] = overloads,
                ["proposals"] = proposals,
                ["applied"] = false,
                // Said out loud so a clean-looking result is never mistaken for
                // a full repair.
                ["repair_scope"] = "only mechanical repairs are offered (dropping members that no longer "
                                 + "exist, deleting an empty system). Disconnected members, split networks "
                                 + "and overloads are design decisions and are reported, not changed.",
            };

            if (!apply || proposals.Count == 0)
            {
                if (proposals.Count > 0) result["next_step"] = "call again with apply:true to run these";
                return result;
            }

            var steps = new List<ChainStep>();
            if (orphans.Count > 0)
                steps.Add(new ChainStep
                {
                    Name = "drop_orphan_members",
                    Required = false,
                    Run = () =>
                    {
                        // The ids do not resolve, which is the whole problem —
                        // Revit's own Remove needs live elements, so all this
                        // can do is report honestly when it cannot act.
                        var live = orphans
                            .Select(id => ElemIds.From(id))
                            .Where(eid => doc.GetElement(eid) != null)
                            .ToList();
                        if (live.Count == 0)
                            throw new InvalidOperationException(
                                "the orphaned ids no longer resolve to elements, so Revit cannot be asked "
                                + "to remove them from the system — the stale claim clears when the system "
                                + "is next edited, or delete and re-create it");
                        MepSystemTools.RemoveMembersCore(doc, sys, live);
                        return new Dictionary<string, object?>
                        {
                            ["removed"] = live.Select(l => (long)l.Value).ToList(),
                        };
                    },
                });

            if (isEmpty)
                steps.Add(new ChainStep
                {
                    Name = "delete_empty_system",
                    Required = false,
                    Run = () => new Dictionary<string, object?>
                    {
                        ["deleted_ids"] = driver.Delete(doc, sys, deleteDependents: false).ToList(),
                    },
                });

            var chain = MepStepChain.Run(doc, "BINA: validate_and_repair_system", steps);
            result["applied"] = true;
            result["repair"] = chain;
            return result;
        }

        // ─── generate_schedule_report ───────────────────────────────────

        /// <summary>Panel schedules across many panels in one read. No
        /// transaction, no view creation — the same read-only contract as
        /// get_panel_schedule, applied in bulk.</summary>
        public static Dictionary<string, object?> GenerateScheduleReport(Document doc, JsonElement args)
        {
            var panelIds = ArgsHelp.GetLongList(args, "panel_ids");
            var levelName = ArgsHelp.GetString(args, "level");

            List<Element> panels;
            if (panelIds.Count > 0)
            {
                panels = new List<Element>();
                var missing = new List<long>();
                foreach (var id in panelIds)
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el == null) missing.Add(id); else panels.Add(el);
                }
                if (missing.Count > 0)
                    return MepTx.Failure($"panel(s) not found: {string.Join(", ", missing)}");
            }
            else
            {
                panels = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .ToList();
                if (levelName != null)
                    panels = panels
                        .Where(p => doc.GetElement(p.LevelId) is Level lv
                                 && string.Equals(lv.Name, levelName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
            }

            if (panels.Count == 0)
                return MepTx.Blocked("no_panels",
                    levelName == null
                        ? "no electrical equipment in this model"
                        : $"no electrical equipment on level '{levelName}'");

            var rows = new List<Dictionary<string, object?>>();
            var totalCircuits = 0;
            double totalVa = 0;

            foreach (var panel in panels)
            {
                var spec = PanelTools.ReadSpec(doc, panel);
                var circuits = CircuitDriver.CircuitsFedBy(doc, panel);
                var loads = circuits.Select(PanelTools.ToLoad).ToList();
                var phaseVa = PanelLoad.PhaseLoads(loads, spec);
                var used = PanelLoad.OccupiedSlots(loads).Count;
                var connected = loads.Sum(l => l.LoadVa);

                totalCircuits += circuits.Count;
                totalVa += connected;

                rows.Add(new Dictionary<string, object?>
                {
                    ["panel_id"] = panel.Id.Value,
                    ["panel_name"] = MepElementInfo.SafeName(panel),
                    ["level"] = doc.GetElement(panel.LevelId) is Level lv ? lv.Name : null,
                    ["distribution_system"] = PanelTools.DistributionSystemName(panel),
                    ["phase_count"] = spec.PhaseCount,
                    ["circuit_count"] = circuits.Count,
                    ["total_slots"] = spec.TotalSlots,
                    ["used_slots"] = used,
                    ["free_slots"] = Math.Max(0, spec.TotalSlots - used),
                    ["connected_load_va"] = Math.Round(connected, 1),
                    ["imbalance_pct"] = Math.Round(PanelLoad.ImbalancePct(phaseVa), 1),
                    ["has_schedule_view"] = PanelTools.FindScheduleView(doc, panel) != null,
                    ["circuits"] = circuits
                        .OrderBy(c => CircuitDriver.TryI(() => c.StartSlot) ?? int.MaxValue)
                        .Select(c => PanelTools.CircuitRow(c, spec))
                        .ToList(),
                });
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["panel_count"] = rows.Count,
                ["circuit_total"] = totalCircuits,
                ["connected_load_total_va"] = Math.Round(totalVa, 1),
                ["panels"] = rows.OrderBy(r => r["panel_name"] as string).ToList(),
                ["note"] = "read-only: no panel schedule VIEWS were created. `has_schedule_view` says "
                         + "which panels already have one; connected load excludes demand factors.",
            };
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static bool HasCircuitEdits(JsonElement args)
            => ArgsHelp.GetDouble(args, "rating_a").HasValue
            || ArgsHelp.GetString(args, "load_name") != null
            || ArgsHelp.GetString(args, "wire_type") != null
            || ArgsHelp.GetString(args, "circuit_number") != null
            || ArgsHelp.GetLong(args, "runs").HasValue
            || ArgsHelp.GetLong(args, "neutral_conductors").HasValue
            || (args.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object);

        private static Level? ResolveLevel(Document doc, string? levelName)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            return levelName == null
                ? levels.FirstOrDefault()
                : levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
