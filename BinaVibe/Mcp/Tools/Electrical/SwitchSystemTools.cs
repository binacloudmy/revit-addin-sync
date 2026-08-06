// create_switch_system — Layer 1, and the one tool in this suite that is NOT
// backed by a real Revit API.
//
// READ THIS BEFORE CHANGING ANYTHING HERE. Searching RevitAPI.xml across the
// 2023, 2025 and 2027 reference assemblies:
//   * there is NO SwitchSystem class,
//   * there is NO ElectricalSystemType.Switch member (the enum is
//     UndefinedSystemType, PowerCircuit, PowerBalanced, PowerUnBalanced, Data,
//     Telephone, Security, FireAlarm, NurseCall, Controls, Communication),
//   * the only switch-shaped hook anywhere in the API is the BuiltInParameter
//     RBS_ELEC_SWITCH_ID_PARAM.
// Revit's Switch System feature is UI-only. So this tool writes the switch id
// parameter on each fixture and SAYS SO in its own result — it does not
// produce a Revit-native switch system object, and any downstream claim that
// it did is wrong.
//
// The honesty is the feature. The alternative was a stub, and a stub means the
// agent falls back to codegen and invents `ElectricalSystemType.Switch`, which
// does not compile.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Mcp.Tools.Mep;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class SwitchSystemTools
    {
        public static Dictionary<string, object?> CreateSwitchSystem(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var switchId = ArgsHelp.GetLong(args, "switch_id")
                    ?? throw new ArgumentException("missing switch_id (the lighting/switch device)");
                var fixtureIds = ArgsHelp.GetLongList(args, "fixture_ids");
                if (fixtureIds.Count == 0)
                    return MepTx.Failure("create_switch_system: fixture_ids is required and must not be empty");

                var switchEl = doc.GetElement(ElemIds.From(switchId))
                    ?? throw new ArgumentException($"switch {switchId} not found");

                var fixtures = new List<Element>();
                var missing = new List<long>();
                foreach (var id in fixtureIds)
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el == null) missing.Add(id); else fixtures.Add(el);
                }
                if (missing.Count > 0)
                    return MepTx.Failure($"fixture(s) not found: {string.Join(", ", missing)}");

                // Fail before the transaction opens if the parameter is not on
                // the switch itself — that is the clearest sign the chosen
                // element is not a switch device at all.
                var switchParam = switchEl.get_Parameter(BuiltInParameter.RBS_ELEC_SWITCH_ID_PARAM);
                if (switchParam == null)
                    return MepTx.Blocked("not_a_switch_device",
                        $"element {switchId} has no Switch ID parameter, so it is not a switch device. "
                        + "Pick a lighting device (a switch family), not a fixture.");

                var switchIdText = switchParam.AsString();
                if (string.IsNullOrWhiteSpace(switchIdText))
                    switchIdText = ArgsHelp.GetString(args, "switch_id_text") ?? switchId.ToString();

                tx = new Transaction(doc, "BINA: create_switch_system");
                TxGuard.StartSwallowing(tx);

                if (!switchParam.IsReadOnly)
                {
                    try { switchParam.Set(switchIdText); } catch { /* keep whatever it already had */ }
                }

                var linked = new List<Dictionary<string, object?>>();
                var refused = new List<Dictionary<string, object?>>();

                foreach (var f in fixtures)
                {
                    var p = f.get_Parameter(BuiltInParameter.RBS_ELEC_SWITCH_ID_PARAM);
                    if (p == null)
                    {
                        refused.Add(Row(f.Id.Value, "no Switch ID parameter on this element"));
                        continue;
                    }
                    if (p.IsReadOnly)
                    {
                        refused.Add(Row(f.Id.Value, "Switch ID is read-only on this element"));
                        continue;
                    }
                    try
                    {
                        p.Set(switchIdText);
                        linked.Add(new Dictionary<string, object?>
                        {
                            ["element_id"] = f.Id.Value,
                            ["name"] = MepElementInfo.SafeName(f),
                            ["switch_id"] = switchIdText,
                        });
                    }
                    catch (Exception ex) { refused.Add(Row(f.Id.Value, ex.Message)); }
                }

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["switch_element_id"] = switchId,
                    ["switch_id"] = switchIdText,
                    ["linked"] = linked,
                    ["refused"] = refused,
                    ["linked_count"] = linked.Count,
                    // Reported on EVERY call, not just failures. The agent must
                    // never tell a drafter it made a Revit switch system.
                    ["limitation"] = "Revit exposes no switch-system API — no SwitchSystem class and no "
                                   + "ElectricalSystemType.Switch. This wrote the Switch ID parameter on "
                                   + "each fixture, which is how Revit's own Switch System stores the "
                                   + "association, but it did NOT create a switch system object. Confirm "
                                   + "in the Revit UI before treating the fixtures as switched.",
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

        private static Dictionary<string, object?> Row(long id, string reason) => new()
        {
            ["element_id"] = id, ["reason"] = reason,
        };
    }
}
