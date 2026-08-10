// list_electrical_settings, create_distribution_system,
// set_distribution_system — Layer 1.
//
// WHY THESE EXIST. create_panel takes distribution_system BY NAME and a panel
// without one accepts no circuit, but nothing told the agent which names are
// valid or let it make one when the template ships none. Observed: the drafter
// asks to "load a single phase distribution system from the family library",
// the agent reaches for search_family_library / load_family, finds nothing —
// because a distribution system is NOT a family — and falls to codegen.
//
// A DistributionSysType is a project-level electrical SETTING (Manage -> MEP
// Settings -> Electrical Settings). There is no .rfa behind it and no
// DistributionSysType.Create. The only constructor is
// ElectricalSetting.AddDistributionSysType, and it needs VoltageType objects
// that must themselves already exist — so a single-phase system is two
// settings deep, which is exactly why the agent could never get there alone.
//
// UNITS: volts. Revit's internal unit for electrical potential IS the volt, so
// VoltageType.ActualValue/MinValue/MaxValue and AddVoltageType's arguments are
// all plain volts with no conversion. Do not route these through ArgsHelp's
// length helpers, which return feet.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElectricalSettingsTools
    {
        /// <summary>How far a voltage may sit from a named type before it stops
        /// counting as "the same voltage". Revit validates a distribution
        /// system's voltages against each type's own min/max, so this is only
        /// used to REUSE an existing type rather than mint a duplicate.</summary>
        private const double VoltageMatchTolerance = 1.0;

        // ─── list_electrical_settings ───────────────────────────────────

        /// <summary>Read-only. The tool that makes create_panel's
        /// distribution_system argument guessable.</summary>
        public static Dictionary<string, object?> ListElectricalSettings(Document doc, JsonElement args)
        {
            var settings = ElectricalSetting.GetElectricalSettings(doc);
            if (settings == null)
                return MepTx.Blocked("no_electrical_settings",
                    "this document has no electrical settings — it is probably not an MEP template");

            var voltages = new List<Dictionary<string, object?>>();
            try
            {
                foreach (VoltageType v in settings.VoltageTypes)
                    voltages.Add(new Dictionary<string, object?>
                    {
                        ["id"] = v.Id.Value,
                        ["name"] = MepElementInfo.SafeName(v),
                        // Already volts — no conversion.
                        ["actual_v"] = CircuitDriver.TryD(() => v.ActualValue),
                        ["min_v"] = CircuitDriver.TryD(() => v.MinValue),
                        ["max_v"] = CircuitDriver.TryD(() => v.MaxValue),
                        ["in_use"] = CircuitDriver.TryB(() => v.IsInUse),
                    });
            }
            catch (Exception ex) { voltages.Add(new Dictionary<string, object?> { ["error"] = ex.Message }); }

            var systems = new List<Dictionary<string, object?>>();
            try
            {
                foreach (DistributionSysType d in settings.DistributionSysTypes)
                    systems.Add(DescribeDistSys(d));
            }
            catch (Exception ex) { systems.Add(new Dictionary<string, object?> { ["error"] = ex.Message }); }

            var wireTypes = new List<string>();
            try { wireTypes.AddRange(CircuitDriver.WireTypeNames(doc)); }
            catch { }

            // Full shortlisting rows (voltages, phase, slots, connector
            // poles) — the thin id/name/system rows forced per-panel probing
            // or a provoked assign_panel failure to learn whether a board
            // could seat 240 V at all. Shared builder with
            // plan_panel_assignment so the numbers never disagree.
            var panels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .Select(p => PanelTools.PanelSummaryRow(doc, p))
                .ToList();

            var row = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["voltage_types"] = voltages,
                ["distribution_systems"] = systems,
                ["wire_types"] = wireTypes,
                ["panels"] = panels,
                ["distribution_system_count"] = systems.Count,
            };

            if (systems.Count == 0)
                // Not a failure — an empty list IS the answer, and it is the
                // one that explains why create_panel keeps refusing.
                row["note"] = "this model defines NO distribution systems, so no panel in it can accept "
                            + "a circuit. A distribution system is a project setting, not a loadable "
                            + "family — there is nothing to fetch from the family library. Create one "
                            + "with create_distribution_system.";
            return row;
        }

        // ─── create_distribution_system ─────────────────────────────────

        /// <summary>Create a distribution system, minting the VoltageType(s)
        /// it needs when they are absent.</summary>
        public static Dictionary<string, object?> CreateDistributionSystem(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var name = ArgsHelp.GetString(args, "name")
                    ?? throw new ArgumentException("missing name (e.g. \"240/415V Wye\")");

                var phaseWord = ArgsHelp.GetString(args, "phase") ?? "single";
                var phase = ParsePhase(phaseWord)
                    ?? throw new ArgumentException(
                        $"unknown phase '{phaseWord}' — use \"single\" or \"three\"");

                var configWord = ArgsHelp.GetString(args, "configuration");
                var config = ParseConfig(configWord, phase)
                    ?? throw new ArgumentException(
                        $"unknown configuration '{configWord}' — use \"wye\", \"delta\", or omit it");

                var lineToGroundV = ArgsHelp.GetDouble(args, "line_to_ground_v");
                var lineToLineV = ArgsHelp.GetDouble(args, "line_to_line_v");
                if (lineToGroundV == null && lineToLineV == null)
                    return MepTx.Failure(
                        "create_distribution_system needs line_to_ground_v and/or line_to_line_v, in VOLTS "
                        + "(Malaysian LV is typically 240 line-to-ground and 415 line-to-line)");

                // Wire count is a real electrical decision, so it is defaulted
                // from the phase rather than invented per call: single-phase
                // live+neutral+earth = 3, three-phase + neutral + earth = 5.
                var wires = (int)(ArgsHelp.GetLong(args, "wires")
                    ?? (phase == ElectricalPhase.ThreePhase ? 5 : 3));

                var settings = ElectricalSetting.GetElectricalSettings(doc)
                    ?? throw new InvalidOperationException(
                        "this document has no electrical settings — it is probably not an MEP template");

                if (FindDistSys(settings, name) != null)
                    return MepTx.Blocked("already_exists",
                        $"a distribution system named '{name}' already exists — "
                        + "use it directly in create_panel or set_distribution_system");

                tx = new Transaction(doc, "BINA: create_distribution_system");
                TxGuard.StartSwallowing(tx);

                var notes = new List<string>();
                var ltg = lineToGroundV.HasValue
                    ? EnsureVoltageType(settings, lineToGroundV.Value, notes)
                    : null;
                var ltl = lineToLineV.HasValue
                    ? EnsureVoltageType(settings, lineToLineV.Value, notes)
                    : null;

                var created = settings.AddDistributionSysType(name, phase, config, wires, ltg, ltl)
                    ?? throw new InvalidOperationException(
                        "Revit declined to create the distribution system and gave no reason — "
                        + "check that the voltages suit the phase and configuration");

                var row = DescribeDistSys(created);
                row["ok"] = true;
                row["notes"] = notes;
                TxGuard.CommitOrThrow(tx);
                return row;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── set_distribution_system ────────────────────────────────────

        /// <summary>Point an existing panel at a distribution system.</summary>
        public static Dictionary<string, object?> SetDistributionSystem(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var panelId = ArgsHelp.GetLong(args, "panel_id")
                    ?? throw new ArgumentException("missing panel_id");
                var name = ArgsHelp.GetString(args, "distribution_system")
                    ?? throw new ArgumentException("missing distribution_system (a name)");

                var panel = doc.GetElement(ElemIds.From(panelId))
                    ?? throw new ArgumentException($"panel {panelId} not found");

                var before = PanelTools.DistributionSystemName(panel);

                tx = new Transaction(doc, "BINA: set_distribution_system");
                TxGuard.StartSwallowing(tx);

                if (!PanelTools.TrySetDistributionSystem(doc, panel, name, out var reason))
                {
                    MepTx.SafeRollback(tx);
                    // A bare "cannot be assigned" is what makes an agent guess
                    // its way through every system in the model and then start
                    // making panels. Say which ones this panel DOES accept, and
                    // which of those would seat the circuit it is being set up
                    // for, so there is a next action instead of a dead end.
                    var row = MepTx.Failure(reason, "distribution_system_rejected");
                    row["resolution"] = PanelTools.SolveResolution(
                        doc,
                        doc.GetElement(ElemIds.From(ArgsHelp.GetLong(args, "circuit_id") ?? 0))
                            as ElectricalSystem,
                        panelId);
                    return row;
                }

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["panel_id"] = panelId,
                    ["panel_name"] = MepElementInfo.SafeName(panel),
                    ["distribution_system_before"] = before,
                    ["distribution_system"] = name,
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

        // ─── helpers ────────────────────────────────────────────────────

        /// <summary>Reuse a voltage type within tolerance, otherwise mint one.
        /// Minting a near-duplicate ("240 V" next to "240V") is how an
        /// electrical settings list becomes unusable, so matching comes first.
        ///
        /// The new type's min/max bracket the nominal by 10%, which is what
        /// makes Revit accept a device whose connector voltage is not exactly
        /// the nominal figure.</summary>
        private static VoltageType EnsureVoltageType(
            ElectricalSetting settings, double volts, List<string> notes)
        {
            foreach (VoltageType v in settings.VoltageTypes)
            {
                double actual;
                try { actual = v.ActualValue; }
                catch { continue; }
                if (Math.Abs(actual - volts) <= VoltageMatchTolerance) return v;
            }

            var name = $"{volts:0.##} V";
            var min = volts * 0.9;
            var max = volts * 1.1;
            var created = settings.AddVoltageType(name, volts, min, max)
                ?? throw new InvalidOperationException(
                    $"Revit declined to create a {volts:0.##} V voltage type");
            notes.Add($"created voltage type '{name}' ({min:0.##}-{max:0.##} V) — none matched {volts:0.##} V");
            return created;
        }

        private static DistributionSysType? FindDistSys(ElectricalSetting settings, string name)
        {
            try
            {
                foreach (DistributionSysType d in settings.DistributionSysTypes)
                    if (string.Equals(MepElementInfo.SafeName(d), name, StringComparison.OrdinalIgnoreCase))
                        return d;
            }
            catch { }
            return null;
        }

        internal static Dictionary<string, object?> DescribeDistSys(DistributionSysType d)
        {
            // VoltageLineToGround / VoltageLineToLine return VoltageType
            // OBJECTS (not ElementIds) and can throw on a single-phase system,
            // which is why every read here is individually wrapped.
            return new Dictionary<string, object?>
            {
                ["id"] = d.Id.Value,
                ["name"] = MepElementInfo.SafeName(d),
                ["phase"] = CircuitDriver.Try(() => d.ElectricalPhase.ToString()),
                ["configuration"] = CircuitDriver.Try(() => d.ElectricalPhaseConfiguration.ToString()),
                ["wires"] = CircuitDriver.TryI(() => d.NumWires),
                ["line_to_ground_v"] = SafeVolts(() => d.VoltageLineToGround),
                ["line_to_line_v"] = SafeVolts(() => d.VoltageLineToLine),
                ["in_use"] = CircuitDriver.TryB(() => d.IsInUse),
            };
        }

        private static double? SafeVolts(Func<VoltageType> f)
        {
            try { return f()?.ActualValue; }
            catch { return null; }
        }

        internal static ElectricalPhase? ParsePhase(string? word) =>
            (word ?? "").Trim().ToLowerInvariant() switch
            {
                "single" or "single_phase" or "singlephase" or "1" or "1p" or "satu fasa"
                    => ElectricalPhase.SinglePhase,
                "three" or "three_phase" or "threephase" or "3" or "3p" or "tiga fasa"
                    => ElectricalPhase.ThreePhase,
                _ => null,
            };

        /// <summary>Wye/Delta describe how three phases are wired, so a
        /// single-phase system takes Undefined — passing Wye there is a common
        /// way to get an unexplained rejection from Revit.</summary>
        internal static ElectricalPhaseConfiguration? ParseConfig(string? word, ElectricalPhase phase)
        {
            if (string.IsNullOrWhiteSpace(word))
                return phase == ElectricalPhase.ThreePhase
                    ? ElectricalPhaseConfiguration.Wye
                    : ElectricalPhaseConfiguration.Undefined;

            return word!.Trim().ToLowerInvariant() switch
            {
                "wye" or "star" or "y" => ElectricalPhaseConfiguration.Wye,
                "delta" => ElectricalPhaseConfiguration.Delta,
                "undefined" or "none" => ElectricalPhaseConfiguration.Undefined,
                _ => null,
            };
        }
    }
}
