// get_element_mep_info, set_element_parameters — Layer 0, discipline-agnostic.
//
// get_element_mep_info is the one call that answers "what IS this thing, in
// MEP terms": its domain, whether it carries an MEPModel, which systems claim
// it, and every connector with its origin and free/taken state. Without it the
// agent guesses from the category name, and category names are exactly what it
// gets wrong (OST_Pipes does not exist; the real one is OST_PipeCurves).
//
// set_element_parameters is the multi-parameter sibling of set_parameter: one
// element, several parameters, ONE transaction. It exists because circuit and
// panel edits routinely change three or four fields together and four separate
// set_parameter calls means four undo steps and four chances to half-apply.
// Values are in PROJECT DISPLAY UNITS — Mutators.SetParamValue owns that
// conversion and is reused verbatim rather than re-derived here.
//
// UNITS: mm on the wire. Connector origins are converted by MepConnectors.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepElementInfo
    {
        // ─── get_element_mep_info ───────────────────────────────────────

        public static Dictionary<string, object?> GetElementMepInfo(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new ArgumentException("missing element_id");
            var el = doc.GetElement(ElemIds.From(id))
                ?? throw new ArgumentException($"element {id} not found");

            var conns = MepConnectors.TryConnectorsOf(el);
            var summaries = new List<Dictionary<string, object?>>();
            for (int i = 0; i < conns.Count; i++) summaries.Add(MepConnectors.ConnectorSummary(conns[i], i));

            // Domain from the connectors, not the category: a family instance's
            // category says what it is FOR, its connectors say what it can join.
            var domains = conns
                .Select(c => MepDomains.FromConnectorDomain(c.Domain))
                .Where(k => k != MepDomainKind.Unknown)
                .Distinct()
                .ToList();

            var systems = SystemsClaiming(el);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id"] = el.Id.Value,
                ["name"] = el.Name,
                ["category"] = el.Category?.Name,
                ["type_name"] = doc.GetElement(el.GetTypeId()) is ElementType et ? et.Name : null,
                ["level"] = doc.GetElement(el.LevelId) is Level lv ? lv.Name : null,
                ["kind"] = ClassifyKind(el),
                ["domains"] = domains.Select(MepDomains.ToWire).ToList(),
                ["has_mep_model"] = el is FamilyInstance fi && fi.MEPModel != null,
                ["connector_count"] = conns.Count,
                ["free_connector_count"] = conns.Count(c => !c.IsConnected),
                ["connectors"] = summaries,
                ["systems"] = systems,
            };
        }

        /// <summary>Systems that claim this element. Electrical is asked
        /// through MEPModel.GetElectricalSystems (a device can be on several
        /// circuits of different types); everything else comes off the
        /// connectors' MEPSystem.</summary>
        private static List<Dictionary<string, object?>> SystemsClaiming(Element el)
        {
            var rows = new List<Dictionary<string, object?>>();
            var seen = new HashSet<long>();

            void Add(MEPSystem? sys)
            {
                if (sys == null) return;
                if (!seen.Add(sys.Id.Value)) return;
                rows.Add(new Dictionary<string, object?>
                {
                    ["system_id"] = sys.Id.Value,
                    ["name"] = SafeName(sys),
                    ["domain"] = MepDomains.ToWire(MepDomains.KindOf(sys)),
                });
            }

            if (el is FamilyInstance fi && fi.MEPModel != null)
            {
                try
                {
                    var elec = fi.MEPModel.GetElectricalSystems();
                    if (elec != null) foreach (var s in elec) Add(s);
                }
                catch { }
            }

            foreach (var c in MepConnectors.TryConnectorsOf(el))
            {
                try { Add(c.MEPSystem); } catch { }
            }

            return rows;
        }

        internal static string SafeName(Element el)
        {
            try { return el.Name ?? ""; }
            catch { return ""; }
        }

        /// <summary>Coarse node kind, shared with the graph builder. Electrical
        /// devices and fixtures are "device" rather than "equipment": an open
        /// connector on a socket is normal, an open connector on a panel is
        /// worth a different default, and the graph checks exempt both.</summary>
        internal static string ClassifyKind(Element el)
        {
            if (el is MEPCurve) return "curve";
            if (el is FamilyInstance fi)
            {
                var catId = fi.Category?.Id.Value;
                if (catId == (long)BuiltInCategory.OST_DuctFitting
                    || catId == (long)BuiltInCategory.OST_PipeFitting
                    || catId == (long)BuiltInCategory.OST_ConduitFitting
                    || catId == (long)BuiltInCategory.OST_CableTrayFitting)
                    return "fitting";
                if (catId == (long)BuiltInCategory.OST_DuctTerminal
                    || catId == (long)BuiltInCategory.OST_Sprinklers)
                    return "terminal";
                if (catId == (long)BuiltInCategory.OST_ElectricalFixtures
                    || catId == (long)BuiltInCategory.OST_LightingFixtures
                    || catId == (long)BuiltInCategory.OST_LightingDevices
                    || catId == (long)BuiltInCategory.OST_DataDevices
                    || catId == (long)BuiltInCategory.OST_FireAlarmDevices
                    || catId == (long)BuiltInCategory.OST_CommunicationDevices
                    || catId == (long)BuiltInCategory.OST_SecurityDevices
                    || catId == (long)BuiltInCategory.OST_NurseCallDevices
                    || catId == (long)BuiltInCategory.OST_TelephoneDevices
                    || catId == (long)BuiltInCategory.OST_PlumbingFixtures)
                    return "device";
                if (fi.MEPModel != null) return "equipment";
            }
            return "unknown";
        }

        // ─── set_element_parameters ─────────────────────────────────────

        public static Dictionary<string, object?> SetElementParameters(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new ArgumentException("missing element_id");
            var el = doc.GetElement(ElemIds.From(id))
                ?? throw new ArgumentException($"element {id} not found");

            if (!args.TryGetProperty("parameters", out var paramsEl)
                || paramsEl.ValueKind != JsonValueKind.Object)
                return MepTx.Failure("set_element_parameters: `parameters` must be an object of {name: value}");

            var wanted = paramsEl.EnumerateObject().ToList();
            if (wanted.Count == 0)
                return MepTx.Failure("set_element_parameters: `parameters` is empty");

            var updated = new List<Dictionary<string, object?>>();
            var failed = new List<Dictionary<string, object?>>();

            Transaction? tx = null;
            try
            {
                tx = new Transaction(doc, $"BINA: set_element_parameters ({wanted.Count})");
                TxGuard.StartSwallowing(tx);

                foreach (var prop in wanted)
                {
                    var p = el.LookupParameter(prop.Name);
                    if (p == null)
                    {
                        failed.Add(Row(prop.Name, "parameter not found on this element"));
                        continue;
                    }
                    if (p.IsReadOnly)
                    {
                        failed.Add(Row(prop.Name, "parameter is read-only"));
                        continue;
                    }
                    try
                    {
                        Mutators.SetParamValue(p, JsonValue(prop.Value));
                        updated.Add(new Dictionary<string, object?>
                        {
                            ["parameter"] = prop.Name,
                            ["value"] = JsonValue(prop.Value),
                        });
                    }
                    catch (Exception ex) { failed.Add(Row(prop.Name, ex.Message)); }
                }

                // All-or-nothing would be worse here: a caller setting four
                // circuit fields wants the three that Revit accepted, plus an
                // honest list of the one it did not.
                TxGuard.CommitOrThrow(tx);
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id"] = el.Id.Value,
                ["updated"] = updated,
                ["failed"] = failed,
                ["updated_count"] = updated.Count,
            };
        }

        private static Dictionary<string, object?> Row(string name, string reason) => new()
        {
            ["parameter"] = name, ["reason"] = reason,
        };

        /// <summary>JsonElement to the CLR shapes Mutators.SetParamValue
        /// understands.</summary>
        private static object? JsonValue(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (object)v.GetDouble(),
            JsonValueKind.Null => null,
            _ => v.ToString(),
        };
    }
}
