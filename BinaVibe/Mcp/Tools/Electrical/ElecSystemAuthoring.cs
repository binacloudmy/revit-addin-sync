// The two tools that build the electrical settings a project is missing:
//   create_distribution_system   (mutate, project)
//   edit_distribution_system     (mutate, project)
//
// WHY THESE EXIST. A panel only accepts a circuit whose voltage falls inside a
// voltage definition used by the panel's distribution system, and until now
// nothing could CREATE either. list_electrical_settings could report that a
// project defines no 240 V, and set_distribution_system could assign one of
// whatever the template happened to ship — but if the template was a US one
// (120/240 Single, 120/208 Wye, 480/277 Wye), the only move left to the agent
// was to bend the DEVICES down to 120 V to match. It did exactly that in UAT
// 2026-08-04: 124 sockets and a distribution board rewritten to 120 V on a
// Malaysian job, producing circuits that commit clean and carry wrong breaker,
// cable and voltage-drop numbers throughout.
//
// So the missing capability was never "assign harder", it was "author the
// system this project should have had". ElecSystemRules holds the Malaysian
// defaults and the phase/wire table, Revit-free and unit-tested.
//
// SCOPE WARNING, surfaced in the result: editing a distribution system that is
// already in use silently re-rates every panel on it. The tool reports IsInUse
// and the affected panel count so the drafter's Ya/Tidak card is informed —
// same contract as set_connector_electrical_data's instances_affected.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElecSystemAuthoring
    {
        // ─── create_distribution_system ─────────────────────────────────
        public static Dictionary<string, object?> Create(Document doc, JsonElement args)
        {
            // No args at all is the common call: the agent has read
            // list_electrical_settings, found nothing usable, and wants the
            // system this project should have had.
            var phases = (int?)ArgsHelp.GetLong(args, "phases");
            var wires = (int?)ArgsHelp.GetLong(args, "wires");
            var lineToGround = ArgsHelp.GetDouble(args, "voltage_line_to_ground_v");
            var lineToLine = ArgsHelp.GetDouble(args, "voltage_line_to_line_v");
            var name = ArgsHelp.GetString(args, "name");
            var rawConfig = ArgsHelp.GetString(args, "phase_config");

            if (!TryPhaseConfig(rawConfig, out var phaseConfig, out var configRefusal))
                return configRefusal!;

            // Anything the caller left out comes from the Malaysian default for
            // the phase count they asked for (or three-phase, which serves both
            // a 415 V board and its 240 V branches).
            var spec = (phases ?? 3) == 1
                ? ElecSystemRules.MalaysianSinglePhase()
                : ElecSystemRules.MalaysianThreePhase();

            if (phases.HasValue) spec.Phases = phases.Value;
            if (phaseConfig != null) spec.PhaseConfig = phaseConfig;
            if (lineToGround.HasValue) spec.VoltageLineToGroundV = lineToGround.Value;
            if (lineToLine.HasValue) spec.VoltageLineToLineV = lineToLine.Value;
            spec.Wires = wires ?? (phases.HasValue || phaseConfig != null
                ? ElecSystemRules.DefaultWires(spec.Phases, spec.PhaseConfig)
                : spec.Wires);
            if (!string.IsNullOrWhiteSpace(name)) spec.Name = name!;

            var pairError = ElecSystemRules.ValidatePhaseWire(spec.Phases, spec.Wires);
            if (pairError != null) return ToolResult.Fail(pairError);

            if (spec.Phases == 3 && !spec.VoltageLineToLineV.HasValue)
                return ToolResult.Fail("a 3-phase system needs voltage_line_to_line_v");
            if (!spec.VoltageLineToGroundV.HasValue)
                return ToolResult.Fail("voltage_line_to_ground_v is required");

            // Idempotency: a same-named system whose values already match is a
            // success with created:false, so a re-run of the same chain is free.
            // Same name with DIFFERENT values is refused rather than silently
            // forked into a near-duplicate the drafter would have to tell apart.
            var existing = FindSystem(doc, spec.Name, null);
            if (existing != null)
            {
                if (Matches(existing, spec))
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["created"] = false,
                        ["note"] = "'" + spec.Name + "' already exists with these values",
                        ["distribution_system"] = existing.Name,
                        ["distribution_system_id"] = existing.Id.Value,
                    };

                return ToolResult.Fail("'" + spec.Name + "' already exists with different values — " +
                            "change them with edit_distribution_system, or pass a different " +
                            "name. Two systems with near-identical names are worse than one.",
                            extra: new Dictionary<string, object?>
                            {
                                ["distribution_system_id"] = existing.Id.Value,
                                ["existing"] = Describe(existing),
                            });
            }

            var settings = ElectricalSetting.GetElectricalSettings(doc);
            if (settings == null)
                return ToolResult.Fail("this document has no electrical settings — it is probably not a " +
                            "project document");

            DistributionSysType? created = null;
            using (var tx = new Transaction(doc, "BinaVibe: create distribution system"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    // Voltage definitions first — AddDistributionSysType takes
                    // VoltageType objects, not numbers.
                    var vLg = EnsureVoltage(doc, settings, spec.VoltageLineToGroundV!.Value);
                    var vLl = spec.VoltageLineToLineV.HasValue
                        ? EnsureVoltage(doc, settings, spec.VoltageLineToLineV.Value)
                        : null;

                    created = settings.AddDistributionSysType(
                        spec.Name,
                        spec.Phases == 3 ? ElectricalPhase.ThreePhase : ElectricalPhase.SinglePhase,
                        ToConfig(spec.PhaseConfig),
                        spec.Wires,
                        // A single-phase system has no line-to-line voltage.
                        vLl,
                        vLg);
                    TxGuard.CommitOrThrow(tx);
                }
                catch { TxGuard.SafeRollBack(tx); throw; }
            }

            if (created == null)
                return ToolResult.Fail("Revit did not return a distribution system");

            // A new system changes which panels are usable and what phase count
            // a plan should have been built with. Any plan held from before is
            // stale, so drop them rather than let a still-resolving plan_id
            // commit against the old picture.
            ElecPlanCaches.DropAll();

            var result = Describe(created);
            result["ok"] = true;
            result["created"] = true;
            result["plans_invalidated"] = true;
            return result;
        }

        // ─── edit_distribution_system ───────────────────────────────────
        public static Dictionary<string, object?> Edit(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "distribution_system");
            var id = ArgsHelp.GetLong(args, "distribution_system_id");
            if (name == null && !id.HasValue)
                return ToolResult.Fail("pass distribution_system (name) or distribution_system_id — " +
                            "call list_electrical_settings for what this project defines");

            var target = FindSystem(doc, name, id);
            if (target == null)
                return ToolResult.Fail("distribution system '" + (name ?? id!.Value.ToString()) +
                            "' not found in this project",
                            extra: new Dictionary<string, object?>
                            {
                                ["available"] = AllSystems(doc)
                                    .Select(s => (object)s.Name).ToList(),
                            });

            var phases = (int?)ArgsHelp.GetLong(args, "phases");
            var wires = (int?)ArgsHelp.GetLong(args, "wires");
            var lineToGround = ArgsHelp.GetDouble(args, "voltage_line_to_ground_v");
            var lineToLine = ArgsHelp.GetDouble(args, "voltage_line_to_line_v");
            var rawConfig = ArgsHelp.GetString(args, "phase_config");

            if (!TryPhaseConfig(rawConfig, out var phaseConfig, out var configRefusal))
                return configRefusal!;

            if (!phases.HasValue && !wires.HasValue && !lineToGround.HasValue &&
                !lineToLine.HasValue && phaseConfig == null)
                return ToolResult.Fail("nothing to change — pass phases, wires, phase_config, " +
                            "voltage_line_to_ground_v and/or voltage_line_to_line_v");

            // Validate against the POST-edit pair, not the args alone: changing
            // only the phase count on a 4-wire system has to be checked against
            // that 4.
            var effectivePhases = phases ?? SafePhases(target);
            var effectiveWires = wires ?? SafeWires(target) ?? 0;
            var pairError = ElecSystemRules.ValidatePhaseWire(effectivePhases, effectiveWires);
            if (pairError != null) return ToolResult.Fail(pairError);

            // Editing a system in use re-rates every panel on it. Counted BEFORE
            // the write so the number is what the drafter is actually approving.
            bool inUse = SafeInUse(target);
            int panelsAffected = CountPanelsOn(doc, target);

            var settings = ElectricalSetting.GetElectricalSettings(doc);
            if (settings == null)
                return ToolResult.Fail("this document has no electrical settings");

            var changed = new List<object>();
            using (var tx = new Transaction(doc, "BinaVibe: edit distribution system"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    if (phases.HasValue)
                    {
                        target.ElectricalPhase = phases.Value == 3
                            ? ElectricalPhase.ThreePhase : ElectricalPhase.SinglePhase;
                        changed.Add("phases");
                    }
                    if (phaseConfig != null)
                    {
                        target.ElectricalPhaseConfiguration = ToConfig(phaseConfig);
                        changed.Add("phase_config");
                    }
                    if (wires.HasValue)
                    {
                        target.NumWires = wires.Value;
                        changed.Add("wires");
                    }
                    if (lineToGround.HasValue)
                    {
                        target.VoltageLineToGround = EnsureVoltage(doc, settings, lineToGround.Value);
                        changed.Add("voltage_line_to_ground_v");
                    }
                    if (lineToLine.HasValue)
                    {
                        target.VoltageLineToLine = EnsureVoltage(doc, settings, lineToLine.Value);
                        changed.Add("voltage_line_to_line_v");
                    }
                    TxGuard.CommitOrThrow(tx);
                }
                catch { TxGuard.SafeRollBack(tx); throw; }
            }

            // Editing re-rates every panel on this system, so a plan built
            // against the old phase count or voltage is now wrong.
            ElecPlanCaches.DropAll();

            // Read back rather than assume, as set_distribution_system does.
            var after = Describe(target);
            after["ok"] = true;
            after["changed"] = changed;
            after["was_in_use"] = inUse;
            after["panels_affected"] = panelsAffected;
            after["plans_invalidated"] = true;
            if (inUse)
                after["note"] = "this system was already in use — all " + panelsAffected +
                                " panel(s) on it now follow the new values";
            return after;
        }

        // ─── helpers ────────────────────────────────────────────────────

        /// <summary>Reuse a voltage definition whose ACTUAL value matches, else
        /// create one. Matching on value not name is deliberate: a template that
        /// already defines 240 V as "LV" must not gain a second 240 V, because
        /// two overlapping definitions make which one a connector binds to
        /// ambiguous.</summary>
        private static VoltageType EnsureVoltage(Document doc, ElectricalSetting settings,
                                                 double volts)
        {
            var match = new FilteredElementCollector(doc)
                .OfClass(typeof(VoltageType)).Cast<VoltageType>()
                .FirstOrDefault(v => ElecSystemRules.Near(SafeActual(v), volts));
            if (match != null) return match;

            var band = ElecSystemRules.VoltageBand(volts);
            return settings.AddVoltageType(
                ElecSystemRules.VoltageName(volts), volts, band.Min, band.Max);
        }

        /// <summary>Normalise the caller's phase_config, or hand back the
        /// refusal. Omitted is fine — only an unrecognised value is refused.</summary>
        private static bool TryPhaseConfig(
            string? raw, out string? phaseConfig, out Dictionary<string, object?>? refusal)
        {
            phaseConfig = null;
            refusal = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;

            phaseConfig = ElecSystemRules.NormalisePhaseConfig(raw);
            if (phaseConfig != null) return true;

            refusal = ToolResult.Fail("phase_config '" + raw +
                        "' not understood — use wye, delta or undefined");
            return false;
        }

        private static ElectricalPhaseConfiguration ToConfig(string? phaseConfig) =>
            phaseConfig switch
            {
                "wye" => ElectricalPhaseConfiguration.Wye,
                "delta" => ElectricalPhaseConfiguration.Delta,
                _ => ElectricalPhaseConfiguration.Undefined,
            };

        private static List<DistributionSysType> AllSystems(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>()
                .OrderBy(d => d.Id.Value)
                .ToList();

        private static DistributionSysType? FindSystem(Document doc, string? name, long? id)
        {
            var all = AllSystems(doc);
            if (id.HasValue) return all.FirstOrDefault(s => s.Id.Value == id.Value);
            return all.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Matches(DistributionSysType existing, DistSystemSpec spec)
        {
            if (SafePhases(existing) != spec.Phases) return false;
            if ((SafeWires(existing) ?? -1) != spec.Wires) return false;

            var lg = SafeVolts(() => existing.VoltageLineToGround?.ActualValue);
            if (!lg.HasValue || !spec.VoltageLineToGroundV.HasValue ||
                !ElecSystemRules.Near(lg.Value, spec.VoltageLineToGroundV.Value)) return false;

            var ll = SafeVolts(() => existing.VoltageLineToLine?.ActualValue);
            if (spec.VoltageLineToLineV.HasValue)
                return ll.HasValue &&
                       ElecSystemRules.Near(ll.Value, spec.VoltageLineToLineV.Value);
            return !ll.HasValue;
        }

        private static Dictionary<string, object?> Describe(DistributionSysType d) =>
            new Dictionary<string, object?>
            {
                ["distribution_system"] = d.Name,
                ["distribution_system_id"] = d.Id.Value,
                ["phases"] = SafePhases(d),
                ["phase_config"] = SafeConfig(d),
                ["wires"] = SafeWires(d),
                ["voltage_line_to_ground_v"] = Round1(
                    SafeVolts(() => d.VoltageLineToGround?.ActualValue)),
                ["voltage_line_to_line_v"] = Round1(
                    SafeVolts(() => d.VoltageLineToLine?.ActualValue)),
            };

        private static int CountPanelsOn(Document doc, DistributionSysType d)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Count(fi => (fi.MEPModel as ElectricalEquipment)?
                        .DistributionSystem?.Id.Value == d.Id.Value);
            }
            catch { return 0; }
        }
    }
}
