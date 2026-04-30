using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Applies JKR compliance fixes to the Revit model.
    /// Each fix runs in its own Transaction for undo support.
    /// </summary>
    public class JkrFixApplicator
    {
        private readonly Document _doc;

        public JkrFixApplicator(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Apply a single fix action. Opens its own Transaction — use for one-off fixes
        /// outside a batch. For Quick Fix All use ApplyFixInExistingTx so all fixes
        /// share one Transaction and collapse to a single undo step.
        /// </summary>
        public FixResult ApplyFix(JkrFixAction fix)
        {
            try
            {
                switch (fix.Action)
                {
                    case "rename_type":
                        return RenameType(fix);
                    case "set_parameter":
                        return SetParameter(fix);
                    case "set_jkr_code":
                        return SetParameter(fix); // same mechanism
                    default:
                        return new FixResult { Success = false, Message = $"Unknown fix action: {fix.Action}" };
                }
            }
            catch (Exception ex)
            {
                return new FixResult { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Apply a fix assuming the caller already has an open Transaction. Used by
        /// JkrRenameHandler.Execute so a Quick Fix All batch produces one undo entry.
        /// </summary>
        public FixResult ApplyFixInExistingTx(JkrFixAction fix)
        {
            try
            {
                switch (fix.Action)
                {
                    case "rename_type":
                        return RenameTypeInTx(fix);
                    case "set_parameter":
                    case "set_jkr_code":
                        return SetParameterInTx(fix);
                    default:
                        return new FixResult { Success = false, Message = $"Unknown fix action: {fix.Action}" };
                }
            }
            catch (Exception ex)
            {
                return new FixResult { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Apply multiple fixes. Returns results for each.
        /// </summary>
        public List<FixResult> ApplyFixes(List<JkrFixAction> fixes)
        {
            var results = new List<FixResult>();
            foreach (var fix in fixes)
            {
                results.Add(ApplyFix(fix));
            }
            return results;
        }

        /// <summary>
        /// Apply all fixable issues from a compliance response.
        /// </summary>
        public List<FixResult> ApplyAllFixable(List<JkrComplianceCheckV2> checks)
        {
            var fixable = checks.Where(c => c.FixAction != null)
                .OrderBy(c => c.FixAction.Priority)
                .ToList();
            var results = new List<FixResult>();

            foreach (var check in fixable)
            {
                var fix = check.FixAction;
                var result = ApplyFix(new JkrFixAction
                {
                    Action = fix.Action,
                    ElementId = fix.ElementId,
                    ParameterName = fix.ParameterName,
                    Value = fix.Value,
                    OldValue = fix.OldValue,
                });
                result.Rule = check.Rule;
                result.CheckElementId = check.ElementId;
                results.Add(result);
            }
            return results;
        }

        // ────────────────────────────────────────────
        // Fix Implementations
        // ────────────────────────────────────────────

        private FixResult RenameType(JkrFixAction fix)
        {
            var preflight = PreflightRename(fix, out var typeElem, out var oldName);
            if (preflight != null) return preflight;

            using (var tx = new Transaction(_doc, $"JKR Fix: Rename '{oldName}' → '{fix.Value}'"))
            {
                tx.Start();
                try
                {
                    typeElem.Name = fix.Value;
                    tx.Commit();
                    return new FixResult
                    {
                        Success = true,
                        Message = $"Renamed '{oldName}' → '{fix.Value}'",
                        Action = "rename_type",
                        ElementId = fix.ElementId,
                    };
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new FixResult { Success = false, Message = $"Rename failed: {ex.Message}" };
                }
            }
        }

        /// <summary>Rename within the caller's open Transaction. No tx open/commit/rollback here.</summary>
        private FixResult RenameTypeInTx(JkrFixAction fix)
        {
            var preflight = PreflightRename(fix, out var typeElem, out var oldName);
            if (preflight != null) return preflight;

            try
            {
                typeElem.Name = fix.Value;
                return new FixResult
                {
                    Success = true,
                    Message = $"Renamed '{oldName}' → '{fix.Value}'",
                    Action = "rename_type",
                    ElementId = fix.ElementId,
                };
            }
            catch (Exception ex)
            {
                return new FixResult { Success = false, Message = $"Rename failed: {ex.Message}" };
            }
        }

        private FixResult PreflightRename(JkrFixAction fix, out Element typeElem, out string oldName)
        {
            typeElem = null;
            oldName = "";
            if (string.IsNullOrEmpty(fix.Value))
                return new FixResult { Success = false, Message = "No new name provided" };

            var elem = _doc.GetElement(new ElementId(fix.ElementId));
            if (elem == null)
                return new FixResult { Success = false, Message = $"Element {fix.ElementId} not found" };

            var typeId = elem.GetTypeId();
            typeElem = _doc.GetElement(typeId);
            if (typeElem == null)
                return new FixResult { Success = false, Message = $"Type not found for element {fix.ElementId}" };

            oldName = typeElem.Name;
            return null;
        }

        private FixResult SetParameter(JkrFixAction fix)
        {
            var preflight = PreflightSetParameter(fix, out var param);
            if (preflight != null) return preflight;

            using (var tx = new Transaction(_doc, $"JKR Fix: Set {fix.ParameterName} = '{fix.Value}'"))
            {
                tx.Start();
                try
                {
                    var result = WriteParameterValue(param, fix);
                    if (result.Success) tx.Commit(); else tx.RollBack();
                    return result;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new FixResult { Success = false, Message = $"Set parameter failed: {ex.Message}" };
                }
            }
        }

        /// <summary>Set parameter within the caller's open Transaction. No tx open/commit/rollback here.</summary>
        private FixResult SetParameterInTx(JkrFixAction fix)
        {
            var preflight = PreflightSetParameter(fix, out var param);
            if (preflight != null) return preflight;

            try
            {
                return WriteParameterValue(param, fix);
            }
            catch (Exception ex)
            {
                return new FixResult { Success = false, Message = $"Set parameter failed: {ex.Message}" };
            }
        }

        private FixResult PreflightSetParameter(JkrFixAction fix, out Parameter param)
        {
            param = null;
            if (string.IsNullOrEmpty(fix.ParameterName))
                return new FixResult { Success = false, Message = "No parameter name provided" };

            var elem = _doc.GetElement(new ElementId(fix.ElementId));
            if (elem == null)
                return new FixResult { Success = false, Message = $"Element {fix.ElementId} not found" };

            // Try instance parameter first, then type parameter
            param = elem.LookupParameter(fix.ParameterName);
            if (param == null)
            {
                var typeElem = _doc.GetElement(elem.GetTypeId());
                if (typeElem != null)
                    param = typeElem.LookupParameter(fix.ParameterName);
            }

            if (param == null)
                return new FixResult
                {
                    Success = false,
                    Message = $"Parameter '{fix.ParameterName}' not found on element {fix.ElementId}. " +
                              "The shared parameter may need to be added to the project first.",
                };

            if (param.IsReadOnly)
                return new FixResult { Success = false, Message = $"Parameter '{fix.ParameterName}' is read-only" };

            return null;
        }

        private FixResult WriteParameterValue(Parameter param, JkrFixAction fix)
        {
            bool set = false;
            switch (param.StorageType)
            {
                case StorageType.String:
                    set = param.Set(fix.Value);
                    break;
                case StorageType.Integer:
                    if (int.TryParse(fix.Value, out int intVal))
                        set = param.Set(intVal);
                    break;
                case StorageType.Double:
                    if (double.TryParse(fix.Value, out double dblVal))
                        set = param.Set(dblVal);
                    break;
                default:
                    return new FixResult { Success = false, Message = $"Unsupported storage type for '{fix.ParameterName}'" };
            }

            if (!set)
                return new FixResult { Success = false, Message = $"Failed to set '{fix.ParameterName}'" };

            return new FixResult
            {
                Success = true,
                Message = $"Set {fix.ParameterName} = '{fix.Value}' (was '{fix.OldValue}')",
                Action = "set_parameter",
                ElementId = fix.ElementId,
                ParameterName = fix.ParameterName,
            };
        }
    }

    // ────────────────────────────────────────────
    // Models
    // ────────────────────────────────────────────

    public class JkrFixAction
    {
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("parameter_name")]
        public string ParameterName { get; set; } = "";

        [JsonProperty("value")]
        public string Value { get; set; } = "";

        [JsonProperty("old_value")]
        public string OldValue { get; set; } = "";

        [JsonProperty("priority")]
        public int Priority { get; set; } = 10;

        [JsonProperty("reference")]
        public string Reference { get; set; } = "";
    }

    public class FixResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Action { get; set; } = "";
        public int ElementId { get; set; }
        public int CheckElementId { get; set; }
        public string ParameterName { get; set; } = "";
        public string Rule { get; set; } = "";
    }
}
