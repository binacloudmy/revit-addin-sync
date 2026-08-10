// get_connector_electrical_data / set_connector_electrical_data — Layer 1.
//
// WHY THIS EXISTS. A socket family whose connector carries no voltage builds a
// 0 V circuit that no panel accepts, and Revit's refusal reads like a panel
// mismatch. CircuitDriver has warned about that since the suite landed, but it
// named `set_connector_electrical_data` as the fix and no such tool existed —
// so the agent could not act on its own advice. It improvised instead, telling
// a drafter to set a JKR `Kadaran_*` parameter that has no bearing on the
// connector at all. That is the failure this file closes.
//
// THE THING EVERYONE GETS WRONG: a family parameter named after voltage does
// NOT drive the connector. Revit reads BuiltInParameter.RBS_ELEC_VOLTAGE off
// the ConnectorElement. A `Kadaran_Voltan`-style parameter is schedule
// metadata and stays metadata until the connector's Voltage is ASSOCIATED to
// it in the family editor. Direct write is therefore the default here, and
// `associate_to_parameter` is the opt-in that builds the real binding.
//
// TRANSACTIONS. Document.EditFamily is illegal while ANY document is
// modifiable, so it runs before a transaction is opened — the same ordering
// Mutators.LoadFromRvtContainer already relies on. The family document gets
// its own transaction; the host document gets a second one for the reload.
//
// SCOPE OF A RELOAD. Reloading touches EVERY instance of the family in the
// model, not the elements the drafter had in mind, and can invalidate circuits
// elsewhere. Nothing about that is undoable in one step. The impact is counted
// BEFORE the write and returned either way, and `dry_run` exists so the count
// can be read without committing to it.
//
// UNITS: voltage_v in volts and apparent_load_va in volt-amperes on the wire;
// UnitUtils converts both to internal units at the parameter. Every value
// returned is converted back, never a raw internal double.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ConnectorElectricalTools
    {
        // ─── get_connector_electrical_data ──────────────────────────────

        /// <summary>Read the electrical data every connector on an element (or
        /// every connector authored in a family) actually carries. The one
        /// reading that matters is <c>voltage_source: "absent"</c> — no
        /// voltage parameter at all, which the 0 V guard in CircuitDriver
        /// deliberately lets through as "unknown".</summary>
        public static Dictionary<string, object?> GetConnectorElectricalData(Document doc, JsonElement args)
        {
            try
            {
                var elementId = ArgsHelp.GetLong(args, "element_id");
                var familyName = ArgsHelp.GetString(args, "family_name");
                if (elementId == null && familyName == null)
                    return MepTx.Failure("pass element_id (a placed instance) or family_name");

                if (elementId != null)
                {
                    var el = doc.GetElement(ElemIds.From(elementId.Value));
                    if (el == null) return MepTx.Failure($"element {elementId.Value} not found");

                    var conns = MepConnectors.TryConnectorsOf(el)
                        .Where(c => c.Domain == Domain.DomainElectrical)
                        .ToList();

                    var symbol = (el as FamilyInstance)?.Symbol;
                    var rows = new List<Dictionary<string, object?>>();
                    for (var i = 0; i < conns.Count; i++)
                        rows.Add(LiveConnectorRow(conns[i], i, el, symbol));

                    var result = new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["element_id"] = el.Id.Value,
                        ["name"] = MepElementInfo.SafeName(el),
                        ["family_name"] = symbol?.FamilyName,
                        ["type_name"] = symbol?.Name,
                        ["electrical_connector_count"] = conns.Count,
                        ["connectors"] = rows,
                    };
                    if (conns.Count == 0)
                        result["blocker"] = new Dictionary<string, object?>
                        {
                            ["code"] = "no_electrical_connector",
                            ["detail"] = "this element has no electrical connector, so it can never be "
                                       + "circuited. That is a family authoring question, not a panel one.",
                        };
                    return result;
                }

                // Family route: read the authored ConnectorElements without
                // needing an instance placed in the model.
                var family = FindFamily(doc, familyName!);
                if (family == null)
                    return MepTx.Failure($"family '{familyName}' is not loaded in this model");
                var guard = FamilyEditGuard(family);
                if (guard != null) return guard;

                Document? fdoc = null;
                try
                {
                    fdoc = doc.EditFamily(family);
                    var conns = ElectricalConnectorElements(fdoc);
                    var rows = new List<Dictionary<string, object?>>();
                    for (var i = 0; i < conns.Count; i++)
                        rows.Add(AuthoredConnectorRow(fdoc, conns[i], i));

                    return new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["family_name"] = family.Name,
                        ["read_from"] = "family_document",
                        ["electrical_connector_count"] = conns.Count,
                        ["connectors"] = rows,
                        ["instances_in_model"] = InstanceIdsOf(doc, family).Count,
                    };
                }
                finally { TryClose(fdoc); }
            }
            catch (Exception ex) { return MepTx.Failure(ex.Message); }
        }

        // ─── set_connector_electrical_data ──────────────────────────────

        /// <summary>Write voltage, poles, system classification, load
        /// classification and apparent load onto a family's electrical
        /// connectors, then reload the family. This is a FAMILY edit — it
        /// changes every instance in the model.</summary>
        public static Dictionary<string, object?> SetConnectorElectricalData(Document doc, JsonElement args)
        {
            Document? fdoc = null;
            try
            {
                var spec = ConnectorElectricalData.Build(
                    voltageV: ArgsHelp.GetDouble(args, "voltage_v"),
                    poles: ArgsHelp.GetLong(args, "poles"),
                    systemType: ArgsHelp.GetString(args, "system_type"),
                    loadClassification: ArgsHelp.GetString(args, "load_classification"),
                    apparentLoadVa: ArgsHelp.GetDouble(args, "apparent_load_va"),
                    associateToParameter: ArgsHelp.GetString(args, "associate_to_parameter"),
                    connectorIndex: ArgsHelp.GetLong(args, "connector_index"),
                    dryRun: ArgsHelp.GetBool(args, "dry_run") ?? false);
                if (!spec.IsValid)
                    return MepTx.Failure(string.Join(" | ", spec.Errors), "invalid_args");

                var family = ResolveFamily(doc, args, out var resolveError);
                if (family == null) return MepTx.Failure(resolveError!);
                var guard = FamilyEditGuard(family);
                if (guard != null) return guard;

                // Impact BEFORE anything is opened: the drafter asked about a
                // few sockets, the reload hits every instance in the model.
                var instanceIds = InstanceIdsOf(doc, family);
                var affectedCircuits = CircuitsTouching(doc, instanceIds);

                fdoc = doc.EditFamily(family);
                var allConns = ElectricalConnectorElements(fdoc);
                if (allConns.Count == 0)
                    return MepTx.Blocked("no_electrical_connector",
                        $"family '{family.Name}' has no electrical connector authored in it. There is "
                        + "nothing to set a voltage on — the family needs a power connector added in "
                        + "the Family Editor first, which this tool cannot do.",
                        new Dictionary<string, object?> { ["family_name"] = family.Name });

                var targets = new List<ConnectorElement>();
                if (spec.ConnectorIndex.HasValue)
                {
                    if (spec.ConnectorIndex.Value >= allConns.Count)
                        return MepTx.Failure(
                            $"connector_index {spec.ConnectorIndex.Value} is out of range — family "
                            + $"'{family.Name}' has {allConns.Count} electrical connector(s), indices 0.."
                            + $"{allConns.Count - 1}");
                    targets.Add(allConns[spec.ConnectorIndex.Value]);
                }
                else targets.AddRange(allConns);

                var before = new List<Dictionary<string, object?>>();
                for (var i = 0; i < targets.Count; i++)
                    before.Add(AuthoredConnectorRow(fdoc, targets[i], allConns.IndexOf(targets[i])));

                var impact = new Dictionary<string, object?>
                {
                    ["family_name"] = family.Name,
                    ["instances_in_model"] = instanceIds.Count,
                    ["electrical_connector_count"] = allConns.Count,
                    ["connectors_targeted"] = targets.Count,
                    ["circuits_using_this_family"] = affectedCircuits,
                };

                if (spec.DryRun)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["dry_run"] = true,
                        ["impact"] = impact,
                        ["before"] = before,
                        ["planned"] = PlannedRow(spec),
                        ["note"] = "nothing was written. Re-run without dry_run to apply. Reloading the "
                                 + "family affects every instance listed in impact, not only the elements "
                                 + "you are circuiting.",
                    };

                // ── family document write ──
                var applied = new List<Dictionary<string, object?>>();
                var refused = new List<Dictionary<string, object?>>();
                Transaction? ftx = null;
                try
                {
                    ftx = new Transaction(fdoc, "BINA: set_connector_electrical_data");
                    TxGuard.StartSwallowing(ftx);
                    foreach (var conn in targets)
                        ApplyToConnector(fdoc, conn, spec, allConns.IndexOf(conn), applied, refused);
                    TxGuard.CommitOrThrow(ftx);
                }
                catch (Exception ex)
                {
                    MepTx.SafeRollback(ftx);
                    return MepTx.Failure(
                        $"the family document edit failed and was rolled back — the model is unchanged "
                        + $"and the family was NOT reloaded: {ex.Message}");
                }

                var after = new List<Dictionary<string, object?>>();
                foreach (var conn in targets)
                    after.Add(AuthoredConnectorRow(fdoc, conn, allConns.IndexOf(conn)));

                // ── reload into the host document ──
                //
                // NO TRANSACTION HERE, and do not add one. The API doc for
                // Document.LoadFamily(Document, IFamilyLoadOptions) lists among
                // its throw conditions: "when the target document is MODIFIABLE
                // (e.g. there is an uncommitted transaction)". LoadFamily opens
                // its own. Wrapping this in a host transaction made every call
                // fail with "Revit refused to reload it into the model" after
                // the family edit had already succeeded — found in UAT
                // 2026-08-07, first live run of this tool.
                //
                // doc.Regenerate() is gone with it: it REQUIRES a transaction,
                // so it cannot live here, and LoadFamily regenerates anyway.
                try
                {
                    fdoc.LoadFamily(doc, new OverwriteLoadOptions());
                }
                catch (Exception ex)
                {
                    return MepTx.Failure(
                        "the connector edit succeeded inside the family but Revit refused to reload it "
                        + $"into the model, so nothing changed on the placed instances: {ex.Message}");
                }

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["impact"] = impact,
                    ["applied"] = applied,
                    ["refused"] = refused,
                    ["before"] = before,
                    ["after"] = after,
                    ["circuits_after_reload"] = CircuitStateAfter(doc, affectedCircuits),
                    // Reported on every successful call. A family reload is not
                    // a one-step undo, and an agent that says "undo if you don't
                    // like it" is wrong.
                    ["limitation"] = "this reloaded the family, so every instance in the model changed, "
                                   + "not only the elements being circuited. Revit does not undo a family "
                                   + "reload cleanly in one step. Circuits that existed before may now "
                                   + "report a different voltage or need rebuilding — check "
                                   + "circuits_after_reload and re-run get_circuit_details before "
                                   + "assigning a panel.",
                };
            }
            catch (Exception ex) { return MepTx.Failure(ex.Message); }
            finally { TryClose(fdoc); }
        }

        // ─── the write itself ───────────────────────────────────────────

        private static void ApplyToConnector(
            Document fdoc, ConnectorElement conn, ConnectorElectricalSpec spec, int index,
            List<Dictionary<string, object?>> applied, List<Dictionary<string, object?>> refused)
        {
            // System classification first: Revit validates voltage and poles
            // against it, so setting them in the other order can be rejected
            // for a classification that is about to change anyway.
            if (spec.SystemClassificationName != null)
            {
                if (!Enum.TryParse<MEPSystemClassification>(spec.SystemClassificationName, out var cls))
                    Refuse(refused, index, "system_type",
                        $"this Revit build has no MEPSystemClassification.{spec.SystemClassificationName}");
                else if (!conn.IsSystemClassificationValid(cls))
                    Refuse(refused, index, "system_type",
                        $"Revit rejects {cls} on this connector — the connector's domain does not allow it");
                else
                    Try(refused, index, "system_type", () =>
                    {
                        conn.SystemClassification = cls;
                        Applied(applied, index, "system_type", cls.ToString());
                    });
            }

            if (spec.Poles.HasValue)
                SetIntParam(conn, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES, spec.Poles.Value,
                    index, "poles", applied, refused);

            if (spec.VoltageV.HasValue)
            {
                if (spec.AssociateToParameter != null)
                    AssociateVoltage(fdoc, conn, spec, index, applied, refused);
                else
                    SetDoubleParam(conn, BuiltInParameter.RBS_ELEC_VOLTAGE, spec.VoltageV.Value,
                        UnitTypeId.Volts, index, "voltage_v", applied, refused);
            }

            if (spec.ApparentLoadVa.HasValue)
                SetDoubleParam(conn, BuiltInParameter.RBS_ELEC_APPARENT_LOAD, spec.ApparentLoadVa.Value,
                    UnitTypeId.VoltAmperes, index, "apparent_load_va", applied, refused);

            if (spec.LoadClassification != null)
                SetLoadClassification(fdoc, conn, spec.LoadClassification, index, applied, refused);
        }

        /// <summary>Bind the connector's Voltage to a family parameter and set
        /// that parameter per type. This is the step a drafter following the
        /// "just set the parameter" advice always misses: without the
        /// association the parameter is inert and the connector keeps its old
        /// value.</summary>
        private static void AssociateVoltage(
            Document fdoc, ConnectorElement conn, ConnectorElectricalSpec spec, int index,
            List<Dictionary<string, object?>> applied, List<Dictionary<string, object?>> refused)
        {
            var name = spec.AssociateToParameter!;
            var fm = fdoc.FamilyManager;
            var vParam = conn.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
            if (vParam == null)
            {
                Refuse(refused, index, "associate_to_parameter",
                    "this connector has no Voltage parameter to associate");
                return;
            }
            if (!fm.CanElementParameterBeAssociated(vParam))
            {
                Refuse(refused, index, "associate_to_parameter",
                    "Revit will not allow the connector's Voltage to be associated in this family");
                return;
            }

            var fp = fm.GetParameters()
                .FirstOrDefault(p => string.Equals(p.Definition?.Name, name, StringComparison.Ordinal));
            var created = false;
            if (fp == null)
            {
                try
                {
                    // Type parameter, not instance: every instance of a socket
                    // type runs at the same voltage, and a per-instance voltage
                    // is a modelling error waiting to happen.
                    fp = fm.AddParameter(name, GroupTypeId.Electrical, SpecTypeId.ElectricalPotential, false);
                    created = true;
                }
                catch (Exception ex)
                {
                    Refuse(refused, index, "associate_to_parameter",
                        $"could not create family parameter '{name}': {ex.Message}");
                    return;
                }
            }
            else
            {
                // The JKR case exactly: `Kadaran_jkr_sit` is authored as Text,
                // and Revit will not bind an Electrical Potential connector
                // value to a Text parameter. Say which one it is.
                ForgeTypeId? dataType = null;
                try { dataType = fp.Definition?.GetDataType(); } catch { }
                if (dataType != null && dataType != SpecTypeId.ElectricalPotential)
                {
                    Refuse(refused, index, "associate_to_parameter",
                        $"family parameter '{name}' already exists with data type "
                        + $"'{SafeTypeLabel(dataType)}', not Electrical Potential, so Revit cannot bind "
                        + "a connector voltage to it. Pick a different name, or omit "
                        + "associate_to_parameter for a direct connector write.");
                    return;
                }
            }

            try { fm.AssociateElementParameterToFamilyParameter(vParam, fp); }
            catch (Exception ex)
            {
                Refuse(refused, index, "associate_to_parameter",
                    $"Revit refused the association to '{name}': {ex.Message}");
                return;
            }

            var internalV = UnitUtils.ConvertToInternalUnits(spec.VoltageV!.Value, UnitTypeId.Volts);
            var typesSet = new List<string>();
            try
            {
                // A family with no types cannot hold a value at all; Revit's own
                // editor creates one implicitly, so match that rather than fail.
                if (fm.Types.IsEmpty) fm.NewType("Standard");
                foreach (FamilyType ft in fm.Types)
                {
                    fm.CurrentType = ft;
                    fm.Set(fp, internalV);
                    typesSet.Add(ft.Name);
                }
            }
            catch (Exception ex)
            {
                Refuse(refused, index, "associate_to_parameter",
                    $"associated '{name}' but could not set its value: {ex.Message}");
                return;
            }

            applied.Add(new Dictionary<string, object?>
            {
                ["connector_index"] = index,
                ["field"] = "voltage_v",
                ["value"] = spec.VoltageV.Value,
                ["via"] = "family_parameter_association",
                ["parameter_name"] = name,
                ["parameter_created"] = created,
                ["types_set"] = typesSet,
            });
        }

        private static void SetLoadClassification(
            Document fdoc, ConnectorElement conn, string wanted, int index,
            List<Dictionary<string, object?>> applied, List<Dictionary<string, object?>> refused)
        {
            var p = conn.get_Parameter(BuiltInParameter.RBS_ELEC_LOAD_CLASSIFICATION);
            if (p == null || p.IsReadOnly)
            {
                Refuse(refused, index, "load_classification",
                    "this connector has no writable Load Classification parameter");
                return;
            }

            if (p.StorageType == StorageType.String)
            {
                Try(refused, index, "load_classification", () =>
                {
                    p.Set(wanted);
                    Applied(applied, index, "load_classification", wanted);
                });
                return;
            }

            if (p.StorageType == StorageType.ElementId)
            {
                var match = new FilteredElementCollector(fdoc)
                    .OfClass(typeof(LoadClassification))
                    .FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var available = new FilteredElementCollector(fdoc)
                        .OfClass(typeof(LoadClassification))
                        .Select(e => e.Name)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
                    Refuse(refused, index, "load_classification",
                        $"no load classification named '{wanted}' in this family. Available: "
                        + (available.Count == 0 ? "(none defined)" : string.Join(", ", available)));
                    return;
                }
                Try(refused, index, "load_classification", () =>
                {
                    p.Set(match.Id);
                    Applied(applied, index, "load_classification", match.Name);
                });
                return;
            }

            Refuse(refused, index, "load_classification",
                $"Load Classification stores as {p.StorageType} in this Revit build, which this tool "
                + "does not know how to write");
        }

        // ─── parameter plumbing ─────────────────────────────────────────

        private static void SetDoubleParam(
            ConnectorElement conn, BuiltInParameter bip, double value, ForgeTypeId unit, int index,
            string field, List<Dictionary<string, object?>> applied, List<Dictionary<string, object?>> refused)
        {
            var p = conn.get_Parameter(bip);
            if (p == null) { Refuse(refused, index, field, $"connector has no {field} parameter"); return; }
            if (p.IsReadOnly) { Refuse(refused, index, field, $"{field} is read-only on this connector"); return; }
            Try(refused, index, field, () =>
            {
                p.Set(UnitUtils.ConvertToInternalUnits(value, unit));
                // Read back rather than echo the request — a Revit parameter
                // can accept a Set and store something else.
                Applied(applied, index, field, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit));
            });
        }

        private static void SetIntParam(
            ConnectorElement conn, BuiltInParameter bip, int value, int index, string field,
            List<Dictionary<string, object?>> applied, List<Dictionary<string, object?>> refused)
        {
            var p = conn.get_Parameter(bip);
            if (p == null) { Refuse(refused, index, field, $"connector has no {field} parameter"); return; }
            if (p.IsReadOnly) { Refuse(refused, index, field, $"{field} is read-only on this connector"); return; }
            Try(refused, index, field, () => { p.Set(value); Applied(applied, index, field, p.AsInteger()); });
        }

        private static void Try(List<Dictionary<string, object?>> refused, int index, string field, Action act)
        {
            try { act(); }
            catch (Exception ex) { Refuse(refused, index, field, ex.Message); }
        }

        private static void Applied(List<Dictionary<string, object?>> applied, int index,
            string field, object? value) =>
            applied.Add(new Dictionary<string, object?>
            {
                ["connector_index"] = index, ["field"] = field, ["value"] = value, ["via"] = "connector",
            });

        private static void Refuse(List<Dictionary<string, object?>> refused, int index,
            string field, string reason) =>
            refused.Add(new Dictionary<string, object?>
            {
                ["connector_index"] = index, ["field"] = field, ["reason"] = reason,
            });

        // ─── reading ────────────────────────────────────────────────────

        /// <summary>One placed device's electrical facts as the panel planner
        /// consumes them — the same instance-then-symbol voltage chase as
        /// LiveConnectorRow, element-level. Shared so get_connector_electrical_data
        /// and plan_panel_assignment can never disagree about a device.
        /// No EditFamily — live connectors only, cheap for N-device sweeps.</summary>
        internal static (double? VoltageV, string VoltageSource, int? Poles,
                         string? SystemType, bool HasConnector) ReadLiveFacts(Element el)
        {
            var conns = MepConnectors.TryConnectorsOf(el)
                .Where(c => c.Domain == Domain.DomainElectrical)
                .ToList();
            if (conns.Count == 0)
                return (null, ConnectorElectricalData.SourceAbsent, null, null, false);

            var symbol = (el as FamilyInstance)?.Symbol;
            var source = ConnectorElectricalData.SourceAbsent;
            var voltage = ReadVoltageV(el);
            if (voltage != null) source = ConnectorElectricalData.SourceInstance;
            else
            {
                voltage = ReadVoltageV(symbol);
                if (voltage != null) source = ConnectorElectricalData.SourceSymbol;
            }

            var poles = ParamIntIn(el, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES)
                     ?? ParamIntIn(symbol, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES);
            return (voltage, source, poles, MepDomains.ConnectorSystemTypeName(conns[0]), true);
        }

        /// <summary>A connector on a PLACED instance. Voltage is chased
        /// instance then symbol, mirroring CircuitDriver.AffirmativeZeroVoltage
        /// so the two never disagree about where a value came from.</summary>
        private static Dictionary<string, object?> LiveConnectorRow(
            Connector c, int index, Element instance, FamilySymbol? symbol)
        {
            var source = ConnectorElectricalData.SourceAbsent;
            string? viaParam = null;
            var voltage = ReadVoltageV(instance);
            if (voltage != null)
            {
                source = ConnectorElectricalData.SourceInstance;
                viaParam = VoltageParamName(instance);
            }
            else
            {
                voltage = ReadVoltageV(symbol);
                if (voltage != null)
                {
                    source = ConnectorElectricalData.SourceSymbol;
                    viaParam = VoltageParamName(symbol);
                }
            }

            var row = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["domain"] = MepDomains.ConnectorDomainLabel(c.Domain),
                ["voltage_v"] = voltage,
                ["voltage_source"] = source,
                // Connector exposes no poles property in any of 2023/2025/2027
                // — only the parameter, and only on the owning element, so it
                // gets the same instance-then-symbol chase as voltage.
                ["poles"] = ParamIntIn(instance, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES)
                         ?? ParamIntIn(symbol, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES),
                ["system_type"] = MepDomains.ConnectorSystemTypeName(c),
                ["is_connected"] = c.IsConnected,
                ["apparent_load_va"] = ParamDoubleIn(instance, BuiltInParameter.RBS_ELEC_APPARENT_LOAD,
                                                     UnitTypeId.VoltAmperes)
                                    ?? ParamDoubleIn(symbol, BuiltInParameter.RBS_ELEC_APPARENT_LOAD,
                                                     UnitTypeId.VoltAmperes),
            };
            // Which parameter the number actually came from. Null means the
            // built-in; a name means the family drives its connector from that
            // parameter, and THAT is the one to change.
            if (viaParam != null) row["voltage_parameter"] = viaParam;
            var advice = ConnectorElectricalData.VoltageAdvice(source, voltage);
            if (advice != null) row["advice"] = advice;
            return row;
        }

        /// <summary>A ConnectorElement as authored inside a family document —
        /// the only place the value can actually be changed.</summary>
        private static Dictionary<string, object?> AuthoredConnectorRow(
            Document fdoc, ConnectorElement conn, int index)
        {
            var voltage = ParamDoubleIn(conn, BuiltInParameter.RBS_ELEC_VOLTAGE, UnitTypeId.Volts);
            var row = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["connector_element_id"] = conn.Id.Value,
                ["domain"] = MepDomains.ConnectorDomainLabel(conn.Domain),
                ["voltage_v"] = voltage,
                ["voltage_source"] = voltage == null
                    ? ConnectorElectricalData.SourceAbsent
                    : ConnectorElectricalData.SourceSymbol,
                ["poles"] = ParamIntIn(conn, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES),
                ["apparent_load_va"] = ParamDoubleIn(conn, BuiltInParameter.RBS_ELEC_APPARENT_LOAD,
                                                     UnitTypeId.VoltAmperes),
                ["system_classification"] = TryString(() => conn.SystemClassification.ToString()),
                ["load_classification"] = ReadLoadClassificationName(fdoc, conn),
                ["voltage_bound_to_parameter"] = BoundParameterName(fdoc, conn),
            };
            var advice = ConnectorElectricalData.VoltageAdvice(
                voltage == null ? ConnectorElectricalData.SourceAbsent : ConnectorElectricalData.SourceSymbol,
                voltage);
            if (advice != null) row["advice"] = advice;
            return row;
        }

        /// <summary>Name of the family parameter the connector's Voltage is
        /// associated to, or null for a direct value. This is the field that
        /// answers "will setting Kadaran_Voltan do anything" — null means no.</summary>
        private static string? BoundParameterName(Document fdoc, ConnectorElement conn)
        {
            try
            {
                var p = conn.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (p == null) return null;
                return fdoc.FamilyManager?.GetAssociatedFamilyParameter(p)?.Definition?.Name;
            }
            catch { return null; }
        }

        private static string? ReadLoadClassificationName(Document fdoc, ConnectorElement conn)
        {
            try
            {
                var p = conn.get_Parameter(BuiltInParameter.RBS_ELEC_LOAD_CLASSIFICATION);
                if (p == null || !p.HasValue) return null;
                if (p.StorageType == StorageType.String) return p.AsString();
                if (p.StorageType == StorageType.ElementId) return fdoc.GetElement(p.AsElementId())?.Name;
                return null;
            }
            catch { return null; }
        }

        /// <summary>Voltage in VOLTS off a placed element. Goes through
        /// CircuitDriver.ReadVoltage so this tool and create_circuit's guard
        /// can never disagree about whether a family has a voltage — they did,
        /// and the disagreement is what made a working 120 V panel report
        /// `absent`.</summary>
        private static double? ReadVoltageV(Element? el)
        {
            var internalV = CircuitDriver.ReadVoltage(el);
            if (internalV == null) return null;
            try { return UnitUtils.ConvertFromInternalUnits(internalV.Value, UnitTypeId.Volts); }
            catch { return internalV; }
        }

        /// <summary>Name of the parameter a voltage was actually read from —
        /// "Voltage" for the common associated-parameter case, null when it
        /// came from the built-in. Reported so a drafter can see WHERE the
        /// number lives before being told to change it.</summary>
        private static string? VoltageParamName(Element? el)
        {
            if (el == null) return null;
            try
            {
                var p = el.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (p != null && p.HasValue) return null;
            }
            catch { }
            var first = CircuitDriver.VoltageCandidates(el).FirstOrDefault();
            return first.Name;
        }

        private static double? ParamDoubleIn(Element? el, BuiltInParameter bip, ForgeTypeId unit)
        {
            try
            {
                var p = el?.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit);
            }
            catch { return null; }
        }

        private static int? ParamIntIn(Element? el, BuiltInParameter bip)
        {
            try
            {
                var p = el?.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return p.AsInteger();
            }
            catch { return null; }
        }

        private static int? TryInt(Func<int> f) { try { return f(); } catch { return null; } }
        private static string? TryString(Func<string?> f) { try { return f(); } catch { return null; } }

        private static string SafeTypeLabel(ForgeTypeId id)
        {
            try
            {
                var label = LabelUtils.GetLabelForSpec(id);
                if (!string.IsNullOrWhiteSpace(label)) return label!;
            }
            catch { }
            try { return id.TypeId; } catch { return "unknown"; }
        }

        // ─── family resolution and impact ───────────────────────────────

        private static Family? ResolveFamily(Document doc, JsonElement args, out string? error)
        {
            error = null;
            var familyName = ArgsHelp.GetString(args, "family_name");
            if (familyName != null)
            {
                var fam = FindFamily(doc, familyName);
                if (fam == null) error = $"family '{familyName}' is not loaded in this model";
                return fam;
            }

            var elementId = ArgsHelp.GetLong(args, "element_id");
            if (elementId == null)
            {
                error = "pass family_name, or element_id of a placed instance of the family to edit";
                return null;
            }
            var el = doc.GetElement(ElemIds.From(elementId.Value));
            if (el == null) { error = $"element {elementId.Value} not found"; return null; }
            if (el is not FamilyInstance fi || fi.Symbol?.Family == null)
            {
                error = $"element {elementId.Value} is not a loadable family instance, so it has no "
                      + "family document to edit";
                return null;
            }
            return fi.Symbol.Family;
        }

        private static Family? FindFamily(Document doc, string name) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Refusals that must happen before EditFamily is called,
        /// because EditFamily throws for them with a message that does not say
        /// which of these it was.</summary>
        private static Dictionary<string, object?>? FamilyEditGuard(Family family)
        {
            if (family.IsInPlace)
                return MepTx.Blocked("in_place_family",
                    $"'{family.Name}' is an in-place family. It has no separate family document, so its "
                    + "connectors can only be edited by editing the in-place element in the Revit UI.",
                    new Dictionary<string, object?> { ["family_name"] = family.Name });
            if (!family.IsEditable)
                return MepTx.Blocked("family_not_editable",
                    $"'{family.Name}' is not editable in this session. In a workshared model that "
                    + "usually means another user owns it, or the family is not editable at all.",
                    new Dictionary<string, object?> { ["family_name"] = family.Name });
            return null;
        }

        private static List<ElementId> InstanceIdsOf(Document doc, Family family)
        {
            var symbolIds = new HashSet<long>(
                family.GetFamilySymbolIds().Select(id => (long)id.Value));
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null && symbolIds.Contains((long)fi.Symbol.Id.Value))
                .Select(fi => fi.Id)
                .ToList();
        }

        /// <summary>Circuits with at least one member drawn from this family —
        /// the collateral a reload can invalidate.</summary>
        private static List<Dictionary<string, object?>> CircuitsTouching(
            Document doc, List<ElementId> instanceIds)
        {
            var wanted = new HashSet<long>(instanceIds.Select(id => (long)id.Value));
            var rows = new List<Dictionary<string, object?>>();
            if (wanted.Count == 0) return rows;

            foreach (var sys in new FilteredElementCollector(doc)
                         .OfClass(typeof(ElectricalSystem))
                         .Cast<ElectricalSystem>())
            {
                var hit = 0;
                try
                {
                    foreach (Element m in sys.Elements)
                        if (wanted.Contains((long)m.Id.Value)) hit++;
                }
                catch { continue; }
                if (hit == 0) continue;
                rows.Add(new Dictionary<string, object?>
                {
                    ["circuit_id"] = sys.Id.Value,
                    ["circuit_number"] = TryString(() => sys.CircuitNumber),
                    ["panel"] = TryString(() => sys.PanelName),
                    ["members_from_this_family"] = hit,
                });
            }
            return rows;
        }

        /// <summary>Re-read the circuits counted before the reload. A circuit
        /// that no longer resolves was destroyed by the reload — reporting the
        /// count is the difference between an honest result and a surprise.</summary>
        private static Dictionary<string, object?> CircuitStateAfter(
            Document doc, List<Dictionary<string, object?>> before)
        {
            var surviving = new List<Dictionary<string, object?>>();
            var gone = new List<long>();
            foreach (var row in before)
            {
                if (row["circuit_id"] is not long id) continue;
                if (doc.GetElement(ElemIds.From(id)) is not ElectricalSystem sys) { gone.Add(id); continue; }
                surviving.Add(new Dictionary<string, object?>
                {
                    ["circuit_id"] = id,
                    ["circuit_number"] = TryString(() => sys.CircuitNumber),
                    ["voltage_v"] = ParamDoubleIn(sys, BuiltInParameter.RBS_ELEC_VOLTAGE, UnitTypeId.Volts),
                    ["panel"] = TryString(() => sys.PanelName),
                });
            }
            return new Dictionary<string, object?>
            {
                ["surviving"] = surviving,
                ["destroyed_by_reload"] = gone,
            };
        }

        private static List<ConnectorElement> ElectricalConnectorElements(Document fdoc) =>
            new FilteredElementCollector(fdoc)
                .OfClass(typeof(ConnectorElement))
                .Cast<ConnectorElement>()
                .Where(c =>
                {
                    try { return c.Domain == Domain.DomainElectrical; }
                    catch { return false; }
                })
                // Id order, so connector_index means the same thing on a read
                // and the write that follows it.
                .OrderBy(c => (long)c.Id.Value)
                .ToList();

        private static Dictionary<string, object?> PlannedRow(ConnectorElectricalSpec spec)
        {
            var row = new Dictionary<string, object?>();
            if (spec.VoltageV.HasValue) row["voltage_v"] = spec.VoltageV.Value;
            if (spec.Poles.HasValue) row["poles"] = spec.Poles.Value;
            if (spec.SystemClassificationName != null) row["system_classification"] = spec.SystemClassificationName;
            if (spec.LoadClassification != null) row["load_classification"] = spec.LoadClassification;
            if (spec.ApparentLoadVa.HasValue) row["apparent_load_va"] = spec.ApparentLoadVa.Value;
            if (spec.AssociateToParameter != null) row["associate_to_parameter"] = spec.AssociateToParameter;
            return row;
        }

        private static void TryClose(Document? fdoc)
        {
            // A family document opened by EditFamily is invisible and leaks the
            // family into "already open" if it is not closed; false = discard,
            // the changes we wanted already went back via LoadFamily.
            if (fdoc == null) return;
            try { if (fdoc.IsValidObject) fdoc.Close(false); } catch { }
        }

        private sealed class OverwriteLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            { overwriteParameterValues = true; return true; }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }
    }
}
