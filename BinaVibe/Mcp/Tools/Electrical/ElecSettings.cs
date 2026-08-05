// Electrical settings + the two fixes that unblock circuiting:
//   list_electrical_settings        (read)
//   set_distribution_system         (mutate, project)
//   set_connector_electrical_data   (mutate, FAMILY — reloads the family)
//
// WHY THESE EXIST. A circuit's voltage comes from the DEVICE connectors, and
// a panel only accepts a circuit whose voltage falls inside a voltage
// definition used by the panel's distribution system. A JKR-library socket
// whose connector has Voltage 0 therefore produces a 0 V circuit that NO
// panel can take, and Revit rejects it with "The panel and circuit do not
// match" — wording that reads like a panel problem and sent the agent into a
// place/delete/replace loop over DB boxes during UAT (2026-08-03).
//
// Until now the only fix was drafter work in the Family Editor and the
// Properties palette, so the tools said so. These three close that gap:
// inspect what the project defines, set a panel's distribution system
// (an ElementId parameter the generic set_parameter cannot write), and
// repair a family's connector data through an EditFamily round-trip.
//
// SCOPE WARNING, deliberately surfaced in the result: editing a family and
// reloading it changes EVERY instance of that family in the project. The
// tool reports the instance count so the drafter's Ya/Tidak card is an
// informed one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElecSettings
    {
        // ─── list_electrical_settings ───────────────────────────────────
        public static Dictionary<string, object?> List(Document doc, JsonElement args)
        {
            var voltageTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(VoltageType)).Cast<VoltageType>()
                .OrderBy(v => v.Id.Value)
                .ToList();

            // VoltageType exposes its values as properties ALREADY IN VOLTS
            // ("the unit is volt" — API docs), so no unit conversion here.
            var voltageRows = voltageTypes.Select(v => (object)new Dictionary<string, object?>
            {
                ["id"] = v.Id.Value,
                ["name"] = v.Name,
                ["voltage_v"] = Round1(SafeVolts(() => v.ActualValue)),
                ["min_v"] = Round1(SafeVolts(() => v.MinValue)),
                ["max_v"] = Round1(SafeVolts(() => v.MaxValue)),
            }).ToList();

            var distRows = new FilteredElementCollector(doc)
                .OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>()
                .OrderBy(d => d.Id.Value)
                .Select(d => (object)new Dictionary<string, object?>
                {
                    ["id"] = d.Id.Value,
                    ["name"] = d.Name,
                    ["phases"] = SafePhase(d) == ElectricalPhase.ThreePhase ? 3 : 1,
                    ["phase_config"] = SafePhaseConfig(d),
                    ["wires"] = SafeWires(d),
                    ["voltage_line_to_ground_v"] = Round1(
                        SafeVolts(() => d.VoltageLineToGround?.ActualValue)),
                    ["voltage_line_to_line_v"] = Round1(
                        SafeVolts(() => d.VoltageLineToLine?.ActualValue)),
                }).ToList();

            // The panel's OWN connector is reported alongside the system it was
            // given, because the two failure modes look identical from outside:
            // an unassigned system and a connector Revit cannot match both read
            // as "the panel and circuit do not match".
            //
            // connector_voltage_v and connector_poles are the pair
            // IsValidDistributionSystem actually compares, and both are
            // settable via set_connector_electrical_data. panel_phases and
            // panel_wires are DERIVED from the assigned distribution system —
            // they read 0 until one is assigned, and no tool can author them.
            // They are named apart deliberately: reported as connector data
            // they read as a fixable defect, and an agent chased that into a
            // Family Editor dead end (2026-08-04).
            var panelRows = CircuitCandidates.FindPanels(doc)
                .Select(p =>
                {
                    var fi = doc.GetElement(ElemIds.From(p.Info.Id)) as FamilyInstance;
                    return (object)new Dictionary<string, object?>
                    {
                        ["id"] = p.Info.Id,
                        ["name"] = p.Info.Name,
                        ["distribution_system"] = string.IsNullOrEmpty(p.DistSystem) ? null : p.DistSystem,
                        ["usable"] = p.Usable,
                        ["reason"] = p.Usable ? null : p.SkipReason,
                        ["connector_voltage_v"] = Round1(ReadVolts(fi, BuiltInParameter.RBS_ELEC_VOLTAGE)),
                        ["connector_poles"] = ReadInt(fi, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES),
                        ["panel_phases_derived"] = ReadInt(fi, BuiltInParameter.RBS_ELEC_PANEL_NUMPHASES_PARAM),
                        ["panel_wires_derived"] = ReadInt(fi, BuiltInParameter.RBS_ELEC_PANEL_NUMWIRES_PARAM),
                    };
                }).ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["voltage_types"] = voltageRows,
                ["distribution_systems"] = distRows,
                ["panels"] = panelRows,
                ["voltage_type_count"] = voltageRows.Count,
                ["distribution_system_count"] = distRows.Count,
            };
        }

        // ─── set_distribution_system ────────────────────────────────────
        // The panel's distribution system is an ElementId-valued parameter,
        // which set_parameter (string/double/int only) cannot write — hence a
        // typed tool rather than a generic call.
        public static Dictionary<string, object?> SetDistributionSystem(Document doc, JsonElement args)
        {
            var panelId = ArgsHelp.GetLong(args, "panel_id")
                ?? throw new ArgumentException("missing panel_id");
            var systemName = ArgsHelp.GetString(args, "distribution_system");
            var systemId = ArgsHelp.GetLong(args, "distribution_system_id");
            if (systemName == null && !systemId.HasValue)
                throw new ArgumentException(
                    "pass distribution_system (name) or distribution_system_id — " +
                    "call list_electrical_settings for what this project defines");

            var panel = doc.GetElement(ElemIds.From(panelId)) as FamilyInstance
                ?? throw new ArgumentException("panel " + panelId + " not found");
            if (panel.MEPModel is not ElectricalEquipment equipment)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "element " + panelId + " is not electrical equipment " +
                                "(category " + (panel.Category?.Name ?? "?") + ") — " +
                                "only a panel/switchboard carries a distribution system",
                };

            var systems = new FilteredElementCollector(doc)
                .OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>()
                .ToList();
            var target = systemId.HasValue
                ? systems.FirstOrDefault(s => s.Id.Value == systemId.Value)
                : systems.FirstOrDefault(s =>
                    string.Equals(s.Name, systemName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "distribution system '" +
                                (systemName ?? systemId!.Value.ToString()) +
                                "' not found in this project",
                    ["available"] = systems.OrderBy(s => s.Id.Value)
                        .Select(s => (object)s.Name).ToList(),
                };

            // Pre-check: Revit compares the system's voltages against the
            // panel family's own connector, so an unmatched pair is refused.
            // Saying WHY beats relaying a bare ArgumentException.
            bool valid;
            try { valid = equipment.IsValidDistributionSystem(target); }
            catch { valid = true; }   // unavailable check must not block the attempt
            if (!valid)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "'" + target.Name + "' cannot be assigned to panel " + panelId +
                                " — its voltage/phase does not match the panel family's " +
                                "electrical connector. Either pick a distribution system whose " +
                                "voltage matches, or fix the panel family's connector first " +
                                "with set_connector_electrical_data (a 0 V connector matches " +
                                "nothing).",
                    ["distribution_system"] = target.Name,
                    ["voltage_line_to_ground_v"] = Round1(
                        SafeVolts(() => target.VoltageLineToGround?.ActualValue)),
                    ["voltage_line_to_line_v"] = Round1(
                        SafeVolts(() => target.VoltageLineToLine?.ActualValue)),
                };

            using (var tx = new Transaction(doc, "BinaVibe: set distribution system"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    equipment.DistributionSystem = target;
                    TxGuard.CommitOrThrow(tx);
                }
                catch { TxGuard.SafeRollBack(tx); throw; }
            }

            // Read back rather than assume.
            var after = (doc.GetElement(ElemIds.From(panelId)) as FamilyInstance)
                ?.MEPModel as ElectricalEquipment;
            var applied = after?.DistributionSystem;
            bool ok = applied != null && applied.Id.Value == target.Id.Value;

            // A cached circuit plan carries the panel's phase count and its
            // assignment, both derived from the distribution system that was in
            // place when the plan was made. Changing it makes that plan stale
            // while its plan_id still resolves, so the natural chain — propose,
            // notice the system is wrong, fix it, commit — committed against
            // stale phases. Same contract as set_connector_electrical_data.
            if (ok) ElecPlanCaches.DropAll();

            return new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["panel_id"] = panelId,
                ["distribution_system"] = applied?.Name,
                ["distribution_system_id"] = applied?.Id.Value,
                ["phases"] = applied != null && SafePhase(applied) == ElectricalPhase.ThreePhase ? 3 : 1,
                ["voltage_line_to_ground_v"] = Round1(
                    SafeVolts(() => applied?.VoltageLineToGround?.ActualValue)),
                ["plans_invalidated"] = ok,
                ["error"] = ok ? null
                    : "Revit did not keep the assignment — the panel family's connector " +
                      "voltage most likely does not match this distribution system. Fix the " +
                      "connector with set_connector_electrical_data first.",
            };
        }

        // ─── set_connector_electrical_data ──────────────────────────────
        // Opens the family, writes the electrical connector's data, reloads
        // it over the project copy. Every instance of the family changes.
        public static Dictionary<string, object?> SetConnectorElectricalData(
            Document doc, JsonElement args)
        {
            double? voltageV = ArgsHelp.GetDouble(args, "voltage_v");
            double? apparentVa = ArgsHelp.GetDouble(args, "apparent_load_va");
            long? poles = ArgsHelp.GetLong(args, "number_of_poles");
            if (!voltageV.HasValue && !apparentVa.HasValue && !poles.HasValue)
                throw new ArgumentException(
                    "nothing to set — pass voltage_v and/or apparent_load_va and/or " +
                    "number_of_poles");

            // number_of_phases / number_of_wires used to be accepted here and
            // were always refused. RBS_ELEC_PANEL_NUMPHASES_PARAM and
            // RBS_ELEC_PANEL_NUMWIRES_PARAM are PANEL-INSTANCE parameters (same
            // family as PANEL_BUSSING / PANEL_MAINSTYPE), not connector ones —
            // a ConnectorElement carries VOLTAGE, APPARENT_LOAD,
            // NUMBER_OF_POLES, LOAD_CLASSIFICATION, CIRCUIT_TYPE and nothing
            // else electrical. Worse, a panel's phases/wires are DERIVED from
            // the distribution system it is assigned, so they read 0 until one
            // is assigned. Offering them as inputs sent the agent chasing a
            // Family Editor fix for a value it cannot author. The lever that
            // actually decides IsValidDistributionSystem is the connector's
            // voltage and pole count, both already here.
            if (ArgsHelp.GetLong(args, "number_of_phases").HasValue ||
                ArgsHelp.GetLong(args, "number_of_wires").HasValue)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "number_of_phases / number_of_wires are not connector data — " +
                                "a panel's phase and wire counts are DERIVED from the " +
                                "distribution system assigned to it, which is why they read 0 " +
                                "before one is assigned. Set number_of_poles (and voltage_v) " +
                                "here to make the connector match a system, then assign it with " +
                                "set_distribution_system; the phase/wire counts follow.",
                };

            var family = ResolveFamily(doc, args, out var resolvedFrom);
            if (family == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "family not found — pass family_name (exact), or element_id " +
                                "of one placed instance. list_family_types shows what is loaded",
                };
            // EditFamily throws on both of these; saying which is which beats
            // relaying its exception.
            if (family.IsInPlace)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "'" + family.Name + "' is an in-place family — it cannot be " +
                                "edited and reloaded this way; the drafter must edit it in " +
                                "the model",
                };
            if (!family.IsEditable)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "'" + family.Name + "' is not editable (a system family, or " +
                                "one this project may not modify) — its connector data cannot " +
                                "be changed from here",
                };
            // EditFamily is illegal on a modifiable document, and so is the
            // LoadFamily at the end. Fail with the real reason rather than
            // Revit's wording if a caller ever holds a transaction open.
            // (A TransactionGroup does NOT make a document modifiable, so
            // running inside execute_revit_batch is fine.)
            if (doc.IsModifiable)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "the project has an open transaction — a family cannot be " +
                                "edited or reloaded while it does. Retry this call on its own.",
                };

            int instanceCount = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Count(fi => fi.Symbol?.Family?.Id.Value == family.Id.Value);

            // EditFamily is illegal while ANY document is modifiable, so this
            // happens before a single transaction opens (same constraint
            // Mutators.LoadFamily documents).
            Document famDoc;
            try
            {
                famDoc = doc.EditFamily(family);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "could not open '" + family.Name + "' for editing: " + ex.Message,
                };
            }

            var connectorRows = new List<object>();
            var skipped = new List<object>();
            int changed = 0;

            try
            {
                var connectors = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>()
                    .Where(c => SafeDomain(c) == Domain.DomainElectrical)
                    .OrderBy(c => c.Id.Value)
                    .ToList();

                if (connectors.Count == 0)
                {
                    famDoc.Close(false);
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = "'" + family.Name + "' has NO electrical connector at all — " +
                                    "setting voltage cannot help. The family needs an electrical " +
                                    "connector added in the Family Editor before it can be " +
                                    "circuited.",
                        ["family"] = family.Name,
                    };
                }

                // A panel whose only connector is a non-power one (Data,
                // FireAlarm, …) can never take a distribution system, and every
                // voltage/phase/wire permutation the agent tries fails the same
                // way. Say so once instead of letting it loop. Re-classifying a
                // connector is deliberately NOT done here: it changes what the
                // connector IS, not what it is rated at, and that is a family
                // authoring decision.
                if (family.FamilyCategory?.Id.Value ==
                        (long)BuiltInCategory.OST_ElectricalEquipment &&
                    !connectors.Any(IsPowerConnector))
                {
                    var kinds = connectors.Select(SafeSystemType).Distinct().ToList();
                    famDoc.Close(false);
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = "'" + family.Name + "' is electrical equipment but none of its " +
                                    "connectors is a POWER connector (found: " +
                                    string.Join(", ", kinds) + "). No voltage, phase or wire value " +
                                    "will make this family accept a distribution system — it needs " +
                                    "a power connector, which is a Family Editor job. Do not retry " +
                                    "with different values.",
                        ["family"] = family.Name,
                        ["connector_system_types"] = kinds.Cast<object>().ToList(),
                    };
                }

                using (var ftx = new Transaction(famDoc, "BinaVibe: set connector electrical data"))
                {
                    ftx.Start();
                    foreach (var conn in connectors)
                    {
                        var row = new Dictionary<string, object?>
                        {
                            ["connector_id"] = conn.Id.Value,
                            ["system_type"] = SafeSystemType(conn),
                        };
                        var applied = new List<object>();
                        var refused = new List<object>();

                        if (voltageV.HasValue)
                            Apply(famDoc, conn, BuiltInParameter.RBS_ELEC_VOLTAGE, "voltage_v",
                                  UnitUtils.ConvertToInternalUnits(voltageV.Value, UnitTypeId.Volts),
                                  applied, refused);
                        if (apparentVa.HasValue)
                            Apply(famDoc, conn, BuiltInParameter.RBS_ELEC_APPARENT_LOAD,
                                  "apparent_load_va",
                                  UnitUtils.ConvertToInternalUnits(apparentVa.Value, UnitTypeId.VoltAmperes),
                                  applied, refused);
                        // Poles is the panel-side lever: IsValidDistributionSystem
                        // compares the connector's voltage AND pole count against
                        // the system, so a 3-phase board needs 3 poles here.
                        if (poles.HasValue)
                            ApplyInt(famDoc, conn, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES,
                                     "number_of_poles", (int)poles.Value, applied, refused);

                        row["applied"] = applied;
                        row["refused"] = refused;
                        if (applied.Count > 0) changed++;
                        connectorRows.Add(row);
                        if (refused.Count > 0)
                            skipped.Add(new Dictionary<string, object?>
                            {
                                ["connector_id"] = conn.Id.Value,
                                ["refused"] = refused,
                            });
                    }
                    ftx.Commit();
                }

                if (changed == 0)
                {
                    famDoc.Close(false);
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = "no connector value could be written — every target " +
                                    "parameter is read-only or formula-driven. Those are " +
                                    "authored in the family and must be changed by hand in " +
                                    "the Family Editor.",
                        ["family"] = family.Name,
                        ["connectors"] = connectorRows,
                    };
                }

                // Reload over the project copy. NO TRANSACTION HERE, and this
                // is not a style choice: LoadFamily throws "The document must
                // not be modifiable before calling LoadFamily" when the TARGET
                // has an open transaction. It opens and commits its own, and
                // regenerates as part of that — so an explicit Regenerate()
                // (which would itself need a transaction) is wrong too.
                //
                // The undo stack still gets one entry, from LoadFamily's own
                // transaction. Mutators.LoadFamily's rvt-container path wraps
                // this same call in a transaction and has the same defect;
                // do not copy from there.
                if (doc.IsModifiable)
                    throw new InvalidOperationException(
                        "internal: project document is still modifiable at reload time — " +
                        "LoadFamily requires no open transaction on the target");
                famDoc.LoadFamily(doc, new OverwriteLoadOptions());
            }
            finally
            {
                try { famDoc.Close(false); } catch { }
            }

            // An overwrite reload REGENERATES every instance of the family, so
            // any cached plan is now holding element ids that no longer exist.
            // Its plan_id still resolves, and CircuitCommit only re-checks the
            // PANEL, so a commit would proceed against dead devices. UAT
            // 2026-08-04 lost a 124-socket plan to exactly this. Drop all three
            // caches and say so, rather than let the agent find out at commit.
            ElecPlanCaches.DropAll();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["family"] = family.Name,
                ["resolved_from"] = resolvedFrom,
                ["connectors_changed"] = changed,
                ["connectors"] = connectorRows,
                ["partially_refused"] = skipped,
                ["instances_affected"] = instanceCount,
                ["voltage_v"] = voltageV,
                ["apparent_load_va"] = apparentVa,
                ["number_of_poles"] = poles,
                ["plans_invalidated"] = true,
            };
        }

        // ─── helpers ────────────────────────────────────────────────────

        /// <summary>Set a double parameter, following an association to a
        /// family parameter when the connector value is driven by one (the
        /// common JKR authoring pattern). Records what happened either way —
        /// a silently unwritten voltage is exactly the failure this tool
        /// exists to end.</summary>
        private static void Apply(Document famDoc, ConnectorElement conn,
                                  BuiltInParameter bip, string label, double internalValue,
                                  List<object> applied, List<object> refused)
            => ApplyCore(famDoc, conn, bip, label, internalValue, null, applied, refused);

        /// <summary>Integer twin of <see cref="Apply"/>. Shares the association
        /// walk deliberately: poles/phases/wires are authored through family
        /// parameters in the JKR library exactly as voltage is, and writing the
        /// connector parameter direct in that case reports success and then
        /// reverts on reload.</summary>
        private static void ApplyInt(Document famDoc, ConnectorElement conn,
                                     BuiltInParameter bip, string label, int value,
                                     List<object> applied, List<object> refused)
            => ApplyCore(famDoc, conn, bip, label, null, value, applied, refused);

        private static void ApplyCore(Document famDoc, ConnectorElement conn,
                                      BuiltInParameter bip, string label,
                                      double? doubleValue, int? intValue,
                                      List<object> applied, List<object> refused)
        {
            var p = conn.get_Parameter(bip);
            if (p == null)
            {
                refused.Add(label + ": parameter not present on this connector");
                return;
            }

            var fm = famDoc.FamilyManager;
            var assoc = fm?.GetAssociatedFamilyParameter(p);
            if (assoc != null)
            {
                // Driven by a family parameter — write THAT, for every type,
                // or the connector snaps back on reload.
                try
                {
                    if (assoc.IsDeterminedByFormula)
                    {
                        refused.Add(label + ": driven by formula '" + (assoc.Formula ?? "?") +
                                    "' — edit the formula in the Family Editor");
                        return;
                    }
                    if (fm!.Types.Size == 0)
                    {
                        SetAssociated(fm, assoc, doubleValue, intValue);
                    }
                    else
                    {
                        foreach (FamilyType t in fm.Types)
                        {
                            fm.CurrentType = t;
                            SetAssociated(fm, assoc, doubleValue, intValue);
                        }
                    }
                    applied.Add(label + " (via family parameter '" + assoc.Definition.Name + "')");
                }
                catch (Exception ex)
                {
                    refused.Add(label + ": family parameter '" + assoc.Definition.Name +
                                "' rejected the value — " + ex.Message);
                }
                return;
            }

            if (p.IsReadOnly)
            {
                refused.Add(label + ": parameter is read-only in this family");
                return;
            }
            try
            {
                if (doubleValue.HasValue) p.Set(doubleValue.Value);
                else p.Set(intValue!.Value);
                applied.Add(label);
            }
            catch (Exception ex)
            {
                refused.Add(label + ": " + ex.Message);
            }
        }

        private static void SetAssociated(FamilyManager fm, FamilyParameter fp,
                                          double? doubleValue, int? intValue)
        {
            if (doubleValue.HasValue) fm.Set(fp, doubleValue.Value);
            else fm.Set(fp, intValue!.Value);
        }

        private static Family? ResolveFamily(Document doc, JsonElement args, out string resolvedFrom)
        {
            resolvedFrom = "";
            var name = ArgsHelp.GetString(args, "family_name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var byName = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family)).Cast<Family>()
                    .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (byName != null) { resolvedFrom = "family_name"; return byName; }
            }

            var elementId = ArgsHelp.GetLong(args, "element_id");
            if (elementId.HasValue &&
                doc.GetElement(ElemIds.From(elementId.Value)) is FamilyInstance fi)
            {
                var fam = fi.Symbol?.Family;
                if (fam != null) { resolvedFrom = "element_id"; return fam; }
            }

            // A type id/name is what list_family_types hands out, so accept it.
            var typeId = ArgsHelp.GetLong(args, "type_id");
            if (typeId.HasValue &&
                doc.GetElement(ElemIds.From(typeId.Value)) is FamilySymbol fs)
            {
                resolvedFrom = "type_id";
                return fs.Family;
            }
            return null;
        }

        /// <summary>A voltage read that survives a distribution system with an
        /// undefined line-to-line voltage (single-phase), where the property
        /// throws rather than returning null.</summary>
        private static double? SafeVolts(Func<double?> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static object? Round1(double? v) => v.HasValue ? Math.Round(v.Value, 1) : (object?)null;

        /// <summary>Read a voltage parameter off an instance, falling back to its
        /// type — the same instance-then-symbol walk CircuitCandidates uses,
        /// since JKR families set this on either. Unlike VoltageType.ActualValue
        /// a PARAMETER is in internal units, so this one does convert.</summary>
        private static double? ReadVolts(FamilyInstance? fi, BuiltInParameter bip)
        {
            var p = ReadParam(fi, bip);
            if (p == null) return null;
            try { return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Volts); }
            catch { return null; }
        }

        private static int? ReadInt(FamilyInstance? fi, BuiltInParameter bip)
        {
            var p = ReadParam(fi, bip);
            if (p == null) return null;
            try { return p.AsInteger(); }
            catch { return null; }
        }

        private static Parameter? ReadParam(FamilyInstance? fi, BuiltInParameter bip)
        {
            if (fi == null) return null;
            var p = fi.get_Parameter(bip);
            if (p == null || !p.HasValue) p = fi.Symbol?.get_Parameter(bip);
            return p != null && p.HasValue ? p : null;
        }

        private static ElectricalPhase SafePhase(DistributionSysType d)
        {
            try { return d.ElectricalPhase; }
            catch { return ElectricalPhase.SinglePhase; }
        }

        private static ElectricalPhase SafePhase(ElectricalEquipment eq)
        {
            try { return eq.DistributionSystem?.ElectricalPhase ?? ElectricalPhase.SinglePhase; }
            catch { return ElectricalPhase.SinglePhase; }
        }

        private static object? SafePhaseConfig(DistributionSysType d)
        {
            try { return d.ElectricalPhaseConfiguration.ToString(); }
            catch { return null; }
        }

        private static object? SafeWires(DistributionSysType d)
        {
            try { return d.NumWires; }
            catch { return null; }
        }

        private static Domain SafeDomain(ConnectorElement c)
        {
            try { return c.Domain; }
            catch { return Domain.DomainUndefined; }
        }

        /// <summary>A ConnectorElement carries SystemClassification; the
        /// ElectricalSystemType property belongs to the runtime Connector, which
        /// a family document does not hand out.</summary>
        private static string SafeSystemType(ConnectorElement c)
        {
            try { return c.SystemClassification.ToString(); }
            catch { return "unknown"; }
        }

        /// <summary>True for the power flavours (PowerCircuit, PowerBalanced,
        /// PowerUnBalanced). Matched on the name prefix rather than enumerated:
        /// the enum gained members across Revit versions and this builds against
        /// three of them.</summary>
        private static bool IsPowerConnector(ConnectorElement c) =>
            SafeSystemType(c).StartsWith("Power", StringComparison.OrdinalIgnoreCase);

        /// <summary>Overwrite the project copy AND its parameter values —
        /// the point of the call is that the old (0 V) values are wrong.
        /// Mutators has an identical private class; duplicated rather than
        /// widening that one's visibility for a single caller.</summary>
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
