// CircuitDriver — the electrical implementation of the Layer 0 seam.
//
// This file hosts NO tools. It is what makes create_mep_system,
// delete_mep_system, get_system_by_id, list_systems_in_project and
// is_system_graph_valid work for circuits without any of them knowing what a
// circuit is. Mechanical and Plumbing arrive later as sibling files exactly
// like this one.
//
// THE CAPABILITY THAT MATTERS: RequiresPhysicalConnection is FALSE. Sockets on
// a circuit are not joined to each other and never will be — a circuit is a
// logical label over a set of loads, anchored to a panel. Setting this flag
// true would make is_system_graph_valid report every correctly-wired circuit
// as broken.
//
// API FACTS THIS FILE DEPENDS ON (RevitAPI.xml, identical across 2023/2025/2027):
//   * ElectricalSystem.Create RETURNS NULL on failure — it does not reliably
//     throw. Every call site needs an explicit null check.
//   * The id-list overload documents no "already in another system" clause,
//     unlike NewMechanicalSystem which states it outright. Behaviour for an
//     already-circuited device is therefore UNVERIFIED, so this driver
//     pre-checks MEPModel.GetElectricalSystems and refuses unless the caller
//     passes reassign:true. An undocumented behaviour becomes a documented
//     refusal.
//   * There is no settable BaseEquipment; the panel is attached with
//     SelectPanel(FamilyInstance) and detached with DisconnectPanel().
//   * Membership changes only through AddToCircuit/RemoveFromCircuit(ElementSet).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal sealed class CircuitDriver : IMepSystemDriver
    {
        public MepDomainKind Kind => MepDomainKind.Electrical;

        public IReadOnlyCollection<string> SystemTypeAliases { get; } = new[]
        {
            "power", "power_circuit", "power_balanced", "power_unbalanced",
            "data", "telephone", "security", "fire_alarm", "nurse_call",
            "controls", "communication",
        };

        public MepDriverCapabilities Capabilities { get; } = new()
        {
            // A circuit is a LABEL, not a network. See the header.
            RequiresPhysicalConnection = false,
            RequiresFreeConnector = false,
            // MEPSystem.DivideSystem: "This function only works for Hvac and
            // Piping system."
            SupportsDivide = false,
            // Deliberately unrestricted: any family with an electrical
            // connector can be circuited, and enumerating the categories here
            // would reject valid ones (specialty equipment, casework with a
            // connector) for no gain. Revit rejects what it cannot take.
            ValidMemberCategories = Array.Empty<BuiltInCategory>(),
        };

        public FilteredElementCollector Collect(Document doc)
            => new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem));

        // ─── create ─────────────────────────────────────────────────────

        public MepSystemCreateResult Create(Document doc, in MepSystemCreateRequest req)
        {
            var warnings = new List<string>();
            var type = ParseSystemType(req.SystemTypeName)
                ?? throw new ArgumentException(
                    $"unknown circuit type '{req.SystemTypeName}' — use one of: "
                    + string.Join(", ", SystemTypeAliases));

            var reassign = ArgsHelp.GetBool(req.RawArgs, "reassign") ?? false;
            var already = new List<string>();
            foreach (var id in req.MemberIds)
            {
                var existing = CircuitsOf(doc.GetElement(id));
                if (existing.Count == 0) continue;
                already.Add($"{id.Value} (on {string.Join(", ", existing.Select(CircuitLabel))})");
            }
            if (already.Count > 0 && !reassign)
                throw new InvalidOperationException(
                    "these elements are already circuited: " + string.Join("; ", already)
                    + ". Revit's behaviour for re-circuiting is undocumented, so this tool refuses "
                    + "by default — pass reassign:true to go ahead, or remove_element_from_circuit first.");
            if (already.Count > 0)
                warnings.Add($"{already.Count} element(s) were already circuited and were re-circuited "
                           + "on the caller's instruction (reassign:true)");

            var zeroVolt = req.MemberIds
                .Where(id => AffirmativeZeroVoltage(doc, id))
                .Select(id => id.Value)
                .ToList();
            if (zeroVolt.Count > 0)
                throw new InvalidOperationException(
                    $"element(s) {string.Join(", ", zeroVolt)} report 0 V. Revit builds the circuit "
                    + "voltage from the DEVICE connector, so a 0 V circuit is one no panel will accept "
                    + "and the resulting error reads like a panel mismatch. Fix the family's connector "
                    + "voltage (set_connector_electrical_data, or the Family Editor) before circuiting.");

            var sys = ElectricalSystem.Create(doc, req.MemberIds.ToList(), type);
            // Documented to return null rather than throw.
            if (sys == null)
                throw new InvalidOperationException(
                    $"Revit declined to create a {type} circuit from these elements. Most often none of "
                    + "them carry an electrical connector of that type — get_element_mep_info shows "
                    + "each element's domains and connectors.");

            return new MepSystemCreateResult { System = sys, Warnings = warnings };
        }

        // ─── membership ─────────────────────────────────────────────────

        public void AddMembers(Document doc, MEPSystem system, IReadOnlyList<ElementId> ids)
        {
            var circuit = AsCircuit(system);
            var set = ToElementSet(doc, ids);
            if (set.Size == 0) throw new ArgumentException("no valid elements to add");
            circuit.AddToCircuit(set);
        }

        public void RemoveMembers(Document doc, MEPSystem system, IReadOnlyList<ElementId> ids)
        {
            var circuit = AsCircuit(system);
            var current = MepSystemTools.MemberIds(system);
            var removing = ids.Select(i => (long)i.Value).Distinct().ToList();
            var remaining = current.Where(id => !removing.Contains(id)).ToList();

            // MEPSystem.Remove: "It is forbidden to remove all terminal elements
            // from system". Refusing here with a next step beats Revit's bare
            // exception — and an emptied circuit would keep its breaker slot,
            // so the panel later reports "full" for a circuit feeding nothing.
            if (remaining.Count == 0 && current.Count > 0)
                throw new InvalidOperationException(
                    $"removing these would leave circuit {CircuitLabel(circuit)} with no members, which "
                    + "Revit forbids. Use delete_circuit instead — an empty circuit still occupies its "
                    + "breaker slot.");

            var set = ToElementSet(doc, ids);
            if (set.Size == 0) throw new ArgumentException("no valid elements to remove");
            circuit.RemoveFromCircuit(set);
        }

        // ─── base equipment (the panel) ─────────────────────────────────

        public void SetBaseEquipment(Document doc, MEPSystem system, ElementId? equipmentId, int? connectorIndex)
        {
            var circuit = AsCircuit(system);

            if (equipmentId == null)
            {
                // Detaching leaves an unassigned circuit, which is a legitimate
                // intermediate state — the caller asked for it explicitly.
                circuit.DisconnectPanel();
                return;
            }

            var el = doc.GetElement(equipmentId);
            if (el is not FamilyInstance panel)
                throw new ArgumentException(
                    $"element {equipmentId.Value} is not a family instance and cannot be a panel");

            circuit.SelectPanel(panel);
        }

        // ─── delete ─────────────────────────────────────────────────────

        public IReadOnlyList<long> Delete(Document doc, MEPSystem system, bool deleteDependents)
        {
            // Nothing special: doc.Delete removes the circuit and its wires
            // (totally dependent) and leaves the devices. The caller compares
            // the returned set against the pre-delete membership rather than
            // trusting that description.
            var deleted = doc.Delete(system.Id);
            return deleted == null
                ? Array.Empty<long>()
                : deleted.Select(e => (long)e.Value).ToList();
        }

        // ─── describe ───────────────────────────────────────────────────

        public void Describe(MEPSystem system, IDictionary<string, object?> row)
        {
            if (system is not ElectricalSystem c) return;

            row["circuit_number"] = Try(() => c.CircuitNumber);
            row["circuit_type"] = Try(() => c.SystemType.ToString());
            row["panel_name"] = Try(() => c.PanelName);
            row["load_name"] = Try(() => c.LoadName);
            row["rating_a"] = TryD(() => c.Rating);
            row["voltage_v"] = TryD(() => c.Voltage);
            row["apparent_load_va"] = TryD(() => c.ApparentLoad);
            row["true_load_va"] = TryD(() => c.TrueLoad);
            row["poles"] = TryI(() => c.PolesNumber);
            row["phase"] = Try(() => c.PhaseLabel);
            row["start_slot"] = TryI(() => c.StartSlot);
            row["wire_type"] = WireTypeName(c);
            row["wire_size"] = ParamString(c, BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_SIZE_PARAM);
            row["length_mm"] = TryD(() => c.Length * MepConnectors.MmPerFoot);
        }

        // ─── shared helpers (used by CircuitTools too) ──────────────────

        internal static ElectricalSystem AsCircuit(MEPSystem system)
            => system as ElectricalSystem
               ?? throw new ArgumentException($"system {system.Id.Value} is not an electrical circuit");

        /// <summary>Circuits a device already belongs to. Empty for anything
        /// that is not a family instance with an MEPModel.</summary>
        internal static List<ElectricalSystem> CircuitsOf(Element? el)
        {
            var list = new List<ElectricalSystem>();
            if (el is not FamilyInstance fi || fi.MEPModel == null) return list;
            try
            {
                var systems = fi.MEPModel.GetElectricalSystems();
                if (systems != null) list.AddRange(systems);
            }
            catch { }
            return list;
        }

        /// <summary>Circuits this element FEEDS as base equipment — i.e. the
        /// circuits on a panel, as opposed to the circuit a device sits on.</summary>
        internal static List<ElectricalSystem> CircuitsFedBy(Document doc, Element panel)
        {
            var fed = new List<ElectricalSystem>();
            foreach (var el in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)))
            {
                if (el is not ElectricalSystem c) continue;
                long? baseId;
                try { baseId = c.BaseEquipment?.Id.Value; }
                catch { continue; }
                if (baseId == panel.Id.Value) fed.Add(c);
            }
            return fed;
        }

        internal static string CircuitLabel(ElectricalSystem c)
        {
            var number = Try(() => c.CircuitNumber);
            var panel = Try(() => c.PanelName);
            if (!string.IsNullOrWhiteSpace(panel) && !string.IsNullOrWhiteSpace(number))
                return $"{panel}-{number}";
            return !string.IsNullOrWhiteSpace(number) ? number! : c.Id.Value.ToString();
        }

        internal static ElectricalSystemType? ParseSystemType(string? word)
        {
            if (string.IsNullOrWhiteSpace(word)) return ElectricalSystemType.PowerCircuit;
            switch (word!.Trim().ToLowerInvariant().Replace(" ", "_"))
            {
                case "power": case "power_circuit": return ElectricalSystemType.PowerCircuit;
                case "power_balanced": return ElectricalSystemType.PowerBalanced;
                case "power_unbalanced": case "power_unbalanced_": return ElectricalSystemType.PowerUnBalanced;
                case "data": return ElectricalSystemType.Data;
                case "telephone": return ElectricalSystemType.Telephone;
                case "security": return ElectricalSystemType.Security;
                case "fire_alarm": case "firealarm": return ElectricalSystemType.FireAlarm;
                case "nurse_call": case "nursecall": return ElectricalSystemType.NurseCall;
                case "controls": return ElectricalSystemType.Controls;
                case "communication": return ElectricalSystemType.Communication;
                default: return null;
            }
        }

        internal static ElementSet ToElementSet(Document doc, IEnumerable<ElementId> ids)
        {
            var set = new ElementSet();
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el != null) set.Insert(el);
            }
            return set;
        }

        /// <summary>True only when the element AFFIRMATIVELY reports zero volts.
        /// Unknown passes: Connector.Voltage does not exist in the API, the
        /// value comes off RBS_ELEC_VOLTAGE on the instance or its symbol, and
        /// plenty of valid families simply do not answer.</summary>
        internal static bool AffirmativeZeroVoltage(Document doc, ElementId id)
        {
            var el = doc.GetElement(id);
            if (el == null) return false;

            var v = ReadVoltage(el);
            if (v == null && el is FamilyInstance fi && fi.Symbol != null) v = ReadVoltage(fi.Symbol);
            return v.HasValue && Math.Abs(v.Value) < 1e-9;
        }

        private static double? ReadVoltage(Element el)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (p == null || !p.HasValue) return null;
                return p.AsDouble();
            }
            catch { return null; }
        }

        // ─── wire type: parameters, not properties ──────────────────────
        //
        // ElectricalSystem.WireType and .WireSizeString EXIST in the 2023 and
        // 2025 APIs and were REMOVED in 2027 — this addin multi-targets net48
        // (2023/24), net8 (2025/26) and net10 (2027), so touching those
        // properties fails to compile on the newest payload. The
        // RBS_ELEC_CIRCUIT_WIRE_TYPE_PARAM / _WIRE_SIZE_PARAM BuiltInParameters
        // are present in ALL THREE, so every wire read and write goes through
        // them and no #if is needed.

        internal static string? WireTypeName(Element circuit)
        {
            try
            {
                var p = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_TYPE_PARAM);
                if (p == null || !p.HasValue) return null;
                if (p.StorageType == StorageType.ElementId)
                    return circuit.Document.GetElement(p.AsElementId())?.Name;
                return p.AsValueString() ?? p.AsString();
            }
            catch { return null; }
        }

        /// <summary>Point the circuit at a WireType by name. Returns false when
        /// no such type exists, so the caller can list what does.</summary>
        internal static bool TrySetWireType(Document doc, ElectricalSystem circuit, string wireTypeName)
        {
            var wt = new FilteredElementCollector(doc)
                .OfClass(typeof(WireType))
                .FirstOrDefault(w => string.Equals(w.Name, wireTypeName, StringComparison.OrdinalIgnoreCase));
            if (wt == null) return false;

            var p = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_TYPE_PARAM);
            if (p == null || p.IsReadOnly) return false;
            p.Set(wt.Id);
            return true;
        }

        internal static IEnumerable<string> WireTypeNames(Document doc)
            => new FilteredElementCollector(doc).OfClass(typeof(WireType))
                .Select(w => w.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        internal static string? ParamString(Element el, BuiltInParameter bip)
        {
            try
            {
                var p = el.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return p.AsValueString() ?? p.AsString();
            }
            catch { return null; }
        }

        internal static string? Try(Func<string?> f)
        {
            try { return f(); }
            catch { return null; }
        }

        internal static double? TryD(Func<double> f)
        {
            try { return f(); }
            catch { return null; }
        }

        internal static int? TryI(Func<int> f)
        {
            try { return f(); }
            catch { return null; }
        }

        internal static bool? TryB(Func<bool> f)
        {
            try { return f(); }
            catch { return null; }
        }
    }

    internal static class CircuitDriverRegistration
    {
        // Runs before any other code in this assembly. The constructor makes no
        // Revit API calls, so it is safe at assembly-load time. See
        // MepSystemDrivers for the fallback if this ever proves fragile in the
        // Revit addin loader.
        [ModuleInitializer]
        internal static void Init() => MepSystemDrivers.Register(new CircuitDriver());
    }
}
