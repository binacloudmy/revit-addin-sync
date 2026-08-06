// Circuit CRUD — Layer 1, built on the Layer 0 seam.
//
// create_circuit, get_circuit_details, get_circuit_by_element,
// edit_circuit_properties, delete_circuit, add_element_to_circuit,
// remove_element_from_circuit.
//
// Creation, membership and deletion all route through MepSystemTools /
// CircuitDriver rather than calling ElectricalSystem directly, so the
// already-circuited refusal, the 0 V guard, the null-return check and the
// "cannot empty a system" rule exist in exactly one place and Mechanical gets
// them for free when it lands.
//
// WHAT edit_circuit_properties CAN AND CANNOT SET (verified against
// RevitAPI.xml): ElectricalSystem exposes setters for a short whitelist —
// LoadName, Rating, TrueLoad, PathOffset, Frame, NeutralConductorsNumber,
// CircuitPathMode, CircuitConnectionType — and NOT for CircuitNumber,
// PanelName, Voltage, PolesNumber or SystemType. Wire type moves through
// RBS_ELEC_CIRCUIT_WIRE_TYPE_PARAM because the WireType PROPERTY was removed
// in the 2027 API. Anything outside that set is attempted as a named parameter
// and, failing that, refused by name rather than silently dropped.
//
// UNITS: amps, volts and VA are Revit's own; length is reported in mm.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CircuitTools
    {
        // ─── create_circuit ─────────────────────────────────────────────

        public static Dictionary<string, object?> CreateCircuit(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var ids = ArgsHelp.GetLongList(args, "element_ids");
                if (ids.Count == 0)
                    return MepTx.Failure("create_circuit: element_ids is required and must not be empty");

                var members = new List<ElementId>();
                var missing = new List<long>();
                foreach (var id in ids)
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el == null) missing.Add(id); else members.Add(el.Id);
                }
                if (missing.Count > 0)
                    return MepTx.Failure($"element(s) not found: {string.Join(", ", missing)}");

                var panelId = ArgsHelp.GetLong(args, "panel_id");
                if (panelId.HasValue && doc.GetElement(ElemIds.From(panelId.Value)) is not FamilyInstance)
                    return MepTx.Failure($"panel_id {panelId} is not a family instance");

                var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);
                var req = new MepSystemCreateRequest
                {
                    MemberIds = members,
                    BaseEquipmentId = panelId.HasValue ? ElemIds.From(panelId.Value) : null,
                    SystemTypeName = ArgsHelp.GetString(args, "circuit_type") ?? "power",
                    RawArgs = args,
                };

                tx = new Transaction(doc, "BINA: create_circuit");
                TxGuard.StartSwallowing(tx);

                var created = MepSystemTools.CreateCore(doc, driver, req);
                var row = MepSystemTools.Describe(created.System, driver);
                row["ok"] = true;
                row["warnings"] = created.Warnings;
                // A circuit with no panel is a legitimate intermediate state, but
                // it carries no breaker and no number — say so rather than
                // letting a null circuit_number read as a failure.
                if (!panelId.HasValue)
                    row["note"] = "no panel assigned — the circuit exists but has no breaker or number "
                                + "until assign_panel is called";

                TxGuard.CommitOrThrow(tx);
                return row;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── get_circuit_details ────────────────────────────────────────

        public static Dictionary<string, object?> GetCircuitDetails(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "circuit_id")
                ?? throw new ArgumentException("missing circuit_id");
            if (doc.GetElement(ElemIds.From(id)) is not ElectricalSystem c)
                return MepTx.Failure($"element {id} is not an electrical circuit "
                                   + "(use get_circuit_by_element or list_circuits_on_panel to find one)");

            var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);
            var row = MepSystemTools.Describe(c, driver);
            row["ok"] = true;
            row["label"] = CircuitDriver.CircuitLabel(c);
            row["panel_id"] = MepSystemTools.SafeBaseEquipmentId(c);
            row["runs"] = CircuitDriver.TryI(() => c.RunsNumber);
            row["hot_conductors"] = CircuitDriver.TryI(() => c.HotConductorsNumber);
            row["neutral_conductors"] = CircuitDriver.TryI(() => c.NeutralConductorsNumber);
            row["ground_conductors"] = CircuitDriver.TryI(() => c.GroundConductorsNumber);
            // ApparentCurrent / PowerFactor / RunsNumber are documented to
            // THROW on a non-power circuit ("This property only available when
            // System Type is Power!"), which is why every read here is wrapped.
            row["power_factor"] = CircuitDriver.TryD(() => c.PowerFactor);
            row["apparent_current_a"] = CircuitDriver.TryD(() => c.ApparentCurrent);
            // Voltage drop via the parameter: the ElectricalSystem.VoltageDrop
            // PROPERTY was removed in the 2027 API, and this addin also targets
            // 2023-2026. RBS_ELEC_VOLTAGE_DROP_PARAM exists in all of them.
            row["voltage_drop"] = CircuitDriver.ParamString(c, BuiltInParameter.RBS_ELEC_VOLTAGE_DROP_PARAM);
            // BalancedLoad is a FLAG ("reports whether the BalancedLoad is on
            // or off"), not a VA figure — do not report it as a load.
            row["balanced_load_on"] = CircuitDriver.TryB(() => c.BalancedLoad);
            row["load_classifications"] = CircuitDriver.Try(() => c.LoadClassifications);
            row["load_classification_abbreviations"] =
                CircuitDriver.Try(() => c.LoadClassificationAbbreviations);

            var members = new List<Dictionary<string, object?>>();
            foreach (var memberId in MepSystemTools.MemberIds(c))
            {
                var m = doc.GetElement(ElemIds.From(memberId));
                members.Add(new Dictionary<string, object?>
                {
                    ["element_id"] = memberId,
                    ["name"] = m == null ? null : MepElementInfo.SafeName(m),
                    ["category"] = m?.Category?.Name,
                    ["missing"] = m == null,
                });
            }
            row["members"] = members;
            row["member_count"] = members.Count;
            return row;
        }

        // ─── get_circuit_by_element ─────────────────────────────────────

        /// <summary>Which circuit(s) a device is on. A device can carry several
        /// (power plus fire alarm), so this returns a LIST — reporting only the
        /// first is how a fire-alarm circuit gets silently edited as if it were
        /// the power one.</summary>
        public static Dictionary<string, object?> GetCircuitByElement(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new ArgumentException("missing element_id");
            var el = doc.GetElement(ElemIds.From(id))
                ?? throw new ArgumentException($"element {id} not found");

            var circuits = CircuitDriver.CircuitsOf(el);
            if (circuits.Count == 0)
            {
                // Not a failure: "this socket is not circuited yet" is the
                // answer, and ok:false would send the agent into a retry loop.
                var fed = CircuitDriver.CircuitsFedBy(doc, el);
                if (fed.Count > 0)
                    return MepTx.Blocked("element_is_a_panel",
                        $"element {id} is not ON a circuit — it FEEDS {fed.Count}. "
                        + "Use list_circuits_on_panel for those.",
                        new Dictionary<string, object?> { ["circuit_ids"] = fed.Select(c => c.Id.Value).ToList() });

                return MepTx.Blocked("not_circuited",
                    $"element {id} is not on any circuit — create_circuit will put it on one");
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id"] = id,
                ["count"] = circuits.Count,
                ["circuits"] = circuits.Select(c => new Dictionary<string, object?>
                {
                    ["circuit_id"] = c.Id.Value,
                    ["label"] = CircuitDriver.CircuitLabel(c),
                    ["circuit_number"] = CircuitDriver.Try(() => c.CircuitNumber),
                    ["circuit_type"] = CircuitDriver.Try(() => c.SystemType.ToString()),
                    ["panel_name"] = CircuitDriver.Try(() => c.PanelName),
                    ["panel_id"] = MepSystemTools.SafeBaseEquipmentId(c),
                    ["rating_a"] = CircuitDriver.TryD(() => c.Rating),
                }).ToList(),
            };
        }

        // ─── edit_circuit_properties ────────────────────────────────────

        public static Dictionary<string, object?> EditCircuitProperties(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var id = ArgsHelp.GetLong(args, "circuit_id")
                    ?? throw new ArgumentException("missing circuit_id");
                if (doc.GetElement(ElemIds.From(id)) is not ElectricalSystem c)
                    return MepTx.Failure($"element {id} is not an electrical circuit");

                var applied = new List<Dictionary<string, object?>>();
                var refused = new List<Dictionary<string, object?>>();

                tx = new Transaction(doc, "BINA: edit_circuit_properties");
                TxGuard.StartSwallowing(tx);

                EditCircuitCore(doc, c, args, applied, refused);

                if (applied.Count == 0 && refused.Count == 0)
                {
                    MepTx.SafeRollback(tx);
                    return MepTx.Failure(
                        "edit_circuit_properties: nothing to change — pass at least one of "
                        + "rating_a, wire_type, load_name, circuit_number, runs, "
                        + "neutral_conductors, or parameters:{}");
                }

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["circuit_id"] = id,
                    ["label"] = CircuitDriver.CircuitLabel(c),
                    ["applied"] = applied,
                    ["refused"] = refused,
                    ["applied_count"] = applied.Count,
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

        /// <summary>No transaction, records outcomes per field. Layer 2 calls
        /// this so a circuit edit can join a longer chain.</summary>
        internal static void EditCircuitCore(Document doc, ElectricalSystem c, JsonElement args,
            List<Dictionary<string, object?>> applied, List<Dictionary<string, object?>> refused)
        {
            void Ok(string field, object? value) =>
                applied.Add(new Dictionary<string, object?> { ["field"] = field, ["value"] = value });
            void No(string field, string reason) =>
                refused.Add(new Dictionary<string, object?> { ["field"] = field, ["reason"] = reason });

            // Real property setters, verified present in the API.
            var rating = ArgsHelp.GetDouble(args, "rating_a");
            if (rating.HasValue)
            {
                try { c.Rating = rating.Value; Ok("rating_a", rating.Value); }
                catch (Exception ex) { No("rating_a", ex.Message); }
            }

            var loadName = ArgsHelp.GetString(args, "load_name");
            if (loadName != null)
            {
                try { c.LoadName = loadName; Ok("load_name", loadName); }
                catch (Exception ex) { No("load_name", ex.Message); }
            }

            // Runs and neutral count go through parameters, not properties:
            // RunsNumber is read-only on every target API, and
            // NeutralConductorsNumber lost its setter in 2027 while this addin
            // still targets 2023-2026. The BuiltInParameters exist in all of
            // them, so one code path covers every payload.
            var runs = ArgsHelp.GetLong(args, "runs");
            if (runs.HasValue)
                SetBip(c, BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_NUM_RUNS_PARAM,
                       (int)runs.Value, Ok, No, "runs");

            var neutrals = ArgsHelp.GetLong(args, "neutral_conductors");
            if (neutrals.HasValue)
                SetBip(c, BuiltInParameter.RBS_ELEC_CIRCUIT_WIRE_NUM_NEUTRALS_PARAM,
                       (int)neutrals.Value, Ok, No, "neutral_conductors");

            // Wire type goes through the parameter, not the property: the
            // ElectricalSystem.WireType PROPERTY was removed in the 2027 API.
            var wireType = ArgsHelp.GetString(args, "wire_type");
            if (wireType != null)
            {
                try
                {
                    if (CircuitDriver.TrySetWireType(doc, c, wireType)) Ok("wire_type", wireType);
                    else No("wire_type",
                        $"no wire type named '{wireType}' in this project — available: "
                        + string.Join(", ", CircuitDriver.WireTypeNames(doc).Take(20)));
                }
                catch (Exception ex) { No("wire_type", ex.Message); }
            }

            // CircuitNumber has NO setter on ElectricalSystem. It is editable as
            // a parameter on some templates and read-only on others, so try the
            // parameter and refuse by name rather than pretending either way.
            var number = ArgsHelp.GetString(args, "circuit_number");
            if (number != null) SetNamedParam(c, "Circuit Number", number, Ok, No, "circuit_number");

            if (args.TryGetProperty("parameters", out var extra) && extra.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in extra.EnumerateObject())
                    SetNamedParam(c, prop.Name, JsonValue(prop.Value), Ok, No, prop.Name);
            }
        }

        private static void SetBip(Element el, BuiltInParameter bip, int value,
            Action<string, object?> ok, Action<string, string> no, string field)
        {
            try
            {
                var p = el.get_Parameter(bip);
                if (p == null) { no(field, "this circuit has no such parameter"); return; }
                if (p.IsReadOnly) { no(field, "read-only on this circuit"); return; }
                p.Set(value);
                ok(field, value);
            }
            catch (Exception ex) { no(field, ex.Message); }
        }

        private static void SetNamedParam(Element el, string paramName, object? value,
            Action<string, object?> ok, Action<string, string> no, string field)
        {
            var p = el.LookupParameter(paramName);
            if (p == null)
            {
                no(field, $"no parameter named '{paramName}' on this circuit");
                return;
            }
            if (p.IsReadOnly)
            {
                no(field, $"'{paramName}' is read-only on this circuit — Revit derives it "
                        + "(circuit number and panel name come from the panel assignment)");
                return;
            }
            try { Mutators.SetParamValue(p, value); ok(field, value); }
            catch (Exception ex) { no(field, ex.Message); }
        }

        private static object? JsonValue(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (object)v.GetDouble(),
            JsonValueKind.Null => null,
            _ => v.ToString(),
        };

        // ─── delete_circuit ─────────────────────────────────────────────

        /// <summary>Delete the circuit. Members are NOT deleted — they become
        /// uncircuited. Wires are, because a wire is totally dependent on its
        /// circuit; the result reports Revit's own deleted-id set rather than
        /// asserting what that covered.</summary>
        public static Dictionary<string, object?> DeleteCircuit(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var id = ArgsHelp.GetLong(args, "circuit_id")
                    ?? throw new ArgumentException("missing circuit_id");
                if (doc.GetElement(ElemIds.From(id)) is not ElectricalSystem c)
                    return MepTx.Failure($"element {id} is not an electrical circuit");

                var driver = MepSystemDrivers.Resolve(MepDomainKind.Electrical);
                var members = MepSystemTools.MemberIds(c);
                var label = CircuitDriver.CircuitLabel(c);

                tx = new Transaction(doc, "BINA: delete_circuit");
                TxGuard.StartSwallowing(tx);

                var deleted = driver.Delete(doc, c, deleteDependents: false).ToList();

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["circuit_id"] = id,
                    ["label"] = label,
                    ["deleted_ids"] = deleted,
                    ["members_before"] = members,
                    ["members_kept"] = members.Where(m => !deleted.Contains(m)).ToList(),
                    ["deleted_member_count"] = members.Count(m => deleted.Contains(m)),
                    ["note"] = "the devices are now uncircuited; wires belonging to this circuit "
                             + "are in deleted_ids. Conduit is NOT removed — no API links a circuit "
                             + "to conduit, so any conduit run stays and must be deleted by hand.",
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

        // ─── add_element_to_circuit / remove_element_from_circuit ───────

        public static Dictionary<string, object?> AddElementToCircuit(Document doc, JsonElement args)
            => ChangeMembership(doc, args, adding: true);

        public static Dictionary<string, object?> RemoveElementFromCircuit(Document doc, JsonElement args)
            => ChangeMembership(doc, args, adding: false);

        private static Dictionary<string, object?> ChangeMembership(Document doc, JsonElement args, bool adding)
        {
            var toolName = adding ? "add_element_to_circuit" : "remove_element_from_circuit";
            Transaction? tx = null;
            try
            {
                var circuitId = ArgsHelp.GetLong(args, "circuit_id")
                    ?? throw new ArgumentException("missing circuit_id");
                if (doc.GetElement(ElemIds.From(circuitId)) is not ElectricalSystem c)
                    return MepTx.Failure($"element {circuitId} is not an electrical circuit");

                // Accept both spellings: one device is the common case, a list
                // is what the composite tools pass.
                var ids = ArgsHelp.GetLongList(args, "element_ids");
                var single = ArgsHelp.GetLong(args, "element_id");
                if (single.HasValue && !ids.Contains(single.Value)) ids.Add(single.Value);
                if (ids.Count == 0)
                    return MepTx.Failure($"{toolName}: pass element_id or element_ids");

                var members = new List<ElementId>();
                var missing = new List<long>();
                foreach (var id in ids)
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el == null) missing.Add(id); else members.Add(el.Id);
                }
                if (missing.Count > 0)
                    return MepTx.Failure($"element(s) not found: {string.Join(", ", missing)}");

                var before = MepSystemTools.MemberIds(c);

                if (adding)
                {
                    var already = ids.Where(before.Contains).ToList();
                    if (already.Count == ids.Count)
                        return MepTx.Blocked("already_on_circuit",
                            $"element(s) {string.Join(", ", already)} are already on "
                            + $"circuit {CircuitDriver.CircuitLabel(c)}");
                    members = members.Where(m => !before.Contains(m.Value)).ToList();
                }
                else
                {
                    var notOn = ids.Where(i => !before.Contains(i)).ToList();
                    if (notOn.Count == ids.Count)
                        return MepTx.Blocked("not_on_circuit",
                            $"element(s) {string.Join(", ", notOn)} are not on "
                            + $"circuit {CircuitDriver.CircuitLabel(c)} — "
                            + "get_circuit_by_element shows which circuit they are on");
                    members = members.Where(m => before.Contains(m.Value)).ToList();
                }

                tx = new Transaction(doc, $"BINA: {toolName}");
                TxGuard.StartSwallowing(tx);

                if (adding) MepSystemTools.AddMembersCore(doc, c, members);
                else MepSystemTools.RemoveMembersCore(doc, c, members);

                var after = MepSystemTools.MemberIds(c);
                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["circuit_id"] = circuitId,
                    ["label"] = CircuitDriver.CircuitLabel(c),
                    ["changed"] = members.Select(m => (long)m.Value).ToList(),
                    ["members_before"] = before,
                    ["members_after"] = after,
                    ["member_count"] = after.Count,
                    ["apparent_load_va"] = CircuitDriver.TryD(() => c.ApparentLoad),
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
    }
}
