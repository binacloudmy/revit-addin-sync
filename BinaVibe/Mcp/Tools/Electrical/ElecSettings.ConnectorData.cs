// set_connector_electrical_data — open the family, write its electrical
// connector data, reload it over the project copy.
//
// EVERY INSTANCE OF THE FAMILY CHANGES. instances_affected reports how many.
//
// EditFamily and LoadFamily are both illegal while the target document is
// modifiable, and LoadFamily opens and commits its OWN transaction (and
// regenerates as part of it). So the project-side work happens with no
// transaction open at all; only the family document gets one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class ElecSettings
    {
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

            var derivedRefusal = RefuseDerivedPanelArgs(args);
            if (derivedRefusal != null) return derivedRefusal;

            var family = ResolveFamily(doc, args, out var resolvedFrom);
            if (family == null)
                return ToolResult.Fail("family not found — pass family_name (exact), or element_id " +
                    "of one placed instance. list_family_types shows what is loaded");

            var preflight = PreflightFamily(doc, family);
            if (preflight != null) return preflight;

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
                return ToolResult.Fail("could not open '" + family.Name + "' for editing: " + ex.Message);
            }

            var connectorRows = new List<object>();
            var skipped = new List<object>();
            int changed;

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
                    return ToolResult.Fail("'" + family.Name + "' has NO electrical connector at all — " +
                        "setting voltage cannot help. The family needs an electrical " +
                        "connector added in the Family Editor before it can be " +
                        "circuited.",
                        new Dictionary<string, object?>
                        {
                            ["family"] = family.Name,
                        });
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
                    return ToolResult.Fail("'" + family.Name + "' is electrical equipment but none of its " +
                        "connectors is a POWER connector (found: " +
                        string.Join(", ", kinds) + "). No voltage, phase or wire value " +
                        "will make this family accept a distribution system — it needs " +
                        "a power connector, which is a Family Editor job. Do not retry " +
                        "with different values.",
                        new Dictionary<string, object?>
                        {
                            ["family"] = family.Name,
                            ["connector_system_types"] = kinds.Cast<object>().ToList(),
                        });
                }

                changed = WriteConnectorData(famDoc, connectors, voltageV, apparentVa, poles,
                                             connectorRows, skipped);

                if (changed == 0)
                {
                    famDoc.Close(false);
                    return ToolResult.Fail("no connector value could be written — every target " +
                        "parameter is read-only or formula-driven. Those are " +
                        "authored in the family and must be changed by hand in " +
                        "the Family Editor.",
                        new Dictionary<string, object?>
                        {
                            ["family"] = family.Name,
                            ["connectors"] = connectorRows,
                        });
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

        /// <summary>Refuse number_of_phases / number_of_wires, which look like
        /// connector data and are not. Null when the args are acceptable.</summary>
        private static Dictionary<string, object?>? RefuseDerivedPanelArgs(JsonElement args)
        {
            // These used to be accepted here and were always refused.
            // RBS_ELEC_PANEL_NUMPHASES_PARAM and RBS_ELEC_PANEL_NUMWIRES_PARAM
            // are PANEL-INSTANCE parameters (same family as PANEL_BUSSING /
            // PANEL_MAINSTYPE), not connector ones — a ConnectorElement carries
            // VOLTAGE, APPARENT_LOAD, NUMBER_OF_POLES, LOAD_CLASSIFICATION,
            // CIRCUIT_TYPE and nothing else electrical. Worse, a panel's
            // phases/wires are DERIVED from the distribution system it is
            // assigned, so they read 0 until one is assigned. Offering them as
            // inputs sent the agent chasing a Family Editor fix for a value it
            // cannot author. The lever that actually decides
            // IsValidDistributionSystem is the connector's voltage and pole
            // count, both of which this tool does set.
            if (!ArgsHelp.GetLong(args, "number_of_phases").HasValue &&
                !ArgsHelp.GetLong(args, "number_of_wires").HasValue)
                return null;

            return ToolResult.Fail("number_of_phases / number_of_wires are not connector data — " +
                "a panel's phase and wire counts are DERIVED from the " +
                "distribution system assigned to it, which is why they read 0 " +
                "before one is assigned. Set number_of_poles (and voltage_v) " +
                "here to make the connector match a system, then assign it with " +
                "set_distribution_system; the phase/wire counts follow.");
        }

        /// <summary>Everything that must be true before EditFamily: a real
        /// loadable family, and NO open transaction on the project. Null when
        /// the way is clear.</summary>
        private static Dictionary<string, object?>? PreflightFamily(Document doc, Family family)
        {
            // EditFamily throws on both of these; saying which is which beats
            // relaying its exception.
            if (family.IsInPlace)
                return ToolResult.Fail("'" + family.Name + "' is an in-place family — it cannot be " +
                    "edited and reloaded this way; the drafter must edit it in " +
                    "the model");
            if (!family.IsEditable)
                return ToolResult.Fail("'" + family.Name + "' is not editable (a system family, or " +
                    "one this project may not modify) — its connector data cannot " +
                    "be changed from here");
            // EditFamily is illegal on a modifiable document, and so is the
            // LoadFamily at the end. Fail with the real reason rather than
            // Revit's wording if a caller ever holds a transaction open.
            // (A TransactionGroup does NOT make a document modifiable, so
            // running inside execute_revit_batch is fine.)
            if (doc.IsModifiable)
                return ToolResult.Fail("the project has an open transaction — a family cannot be " +
                    "edited or reloaded while it does. Retry this call on its own.");
            return null;
        }

        /// <summary>The per-connector write, inside the FAMILY document's own
        /// transaction. Returns how many connectors took at least one value;
        /// <paramref name="rows"/> and <paramref name="skipped"/> are filled for
        /// the result.
        ///
        /// TxGuard, not a bare Start/Commit: a Revit-forced rollback used to
        /// leave Commit() returning RolledBack silently, after which the reload
        /// below pushed the UNCHANGED family back and the tool reported success
        /// with connectors_changed &gt; 0.</summary>
        private static int WriteConnectorData(
            Document famDoc, IReadOnlyList<ConnectorElement> connectors,
            double? voltageV, double? apparentVa, long? poles,
            List<object> rows, List<object> skipped)
        {
            int changed = 0;
            using (var ftx = new Transaction(famDoc, "BinaVibe: set connector electrical data"))
            {
                TxGuard.StartSwallowing(ftx);
                try
                {
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
                        rows.Add(row);
                        if (refused.Count > 0)
                            skipped.Add(new Dictionary<string, object?>
                            {
                                ["connector_id"] = conn.Id.Value,
                                ["refused"] = refused,
                            });
                    }
                    TxGuard.CommitOrThrow(ftx);
                }
                catch { TxGuard.SafeRollBack(ftx); throw; }
            }
            return changed;
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
