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

        // Cache for "no shared parameter file is loaded" — once detected, every
        // subsequent param fix that would need to bind a shared param can fail
        // fast with the same message instead of repeating the OpenSharedParameterFile()
        // call and producing N copies of the same error in a Quick-Fix-All run.
        private bool _sharedParamFileMissing;
        private string _sharedParamFileMissingMessage;

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

            param = ResolveParameter(elem, fix.ParameterName);

            // If the param isn't on the element at all, try to bind it from the
            // currently-loaded shared parameter file. Turns "manual: add the param
            // first" into a one-click auto-fix when the JKR shared params file is
            // loaded in the project.
            if (param == null)
            {
                var bindMsg = TryBindSharedParameter(elem, fix.ParameterName);
                if (bindMsg != null)
                    return new FixResult
                    {
                        Success = false,
                        Message = $"Parameter '{fix.ParameterName}' not found on element {fix.ElementId}. {bindMsg}",
                    };

                // Re-resolve after binding.
                param = ResolveParameter(elem, fix.ParameterName);
                if (param == null)
                    return new FixResult
                    {
                        Success = false,
                        Message = $"Parameter '{fix.ParameterName}' was bound but still not visible on element {fix.ElementId}.",
                    };
            }

            if (param.IsReadOnly)
                return new FixResult { Success = false, Message = $"Parameter '{fix.ParameterName}' is read-only" };

            return null;
        }

        /// <summary>
        /// Look up a parameter on an element, preferring the writable handle.
        /// Tries instance-side first; if missing or read-only, falls back to type-side.
        /// </summary>
        private Parameter ResolveParameter(Element elem, string name)
        {
            var p = elem.LookupParameter(name);
            if (p != null && !p.IsReadOnly) return p;

            var typeElem = _doc.GetElement(elem.GetTypeId());
            var tp = typeElem?.LookupParameter(name);
            if (tp != null && !tp.IsReadOnly) return tp;

            // Neither writable — return whatever we found so caller surfaces a clean
            // "read-only" error instead of "not found".
            return p ?? tp;
        }

        /// <summary>
        /// Bind a JKR shared parameter to the element's category if it's defined in
        /// the currently-loaded shared parameter file but not yet bound to the project.
        /// Returns null on success; otherwise an error message explaining why the bind
        /// couldn't happen (so the caller can include it in the user-facing failure).
        /// Must be called inside an open Transaction.
        /// </summary>
        private string TryBindSharedParameter(Element elem, string paramName)
        {
            // Short-circuit: if we already established the shared param file is
            // missing for this applicator's lifetime, return the cached message
            // instead of probing the API again on every queued fix.
            if (_sharedParamFileMissing)
                return _sharedParamFileMissingMessage;

            var app = _doc.Application;
            DefinitionFile sharedFile;
            try
            {
                sharedFile = app?.OpenSharedParameterFile();
            }
            catch (Exception ex)
            {
                _sharedParamFileMissing = true;
                _sharedParamFileMissingMessage = $"Could not open shared parameter file: {ex.Message}.";
                return _sharedParamFileMissingMessage;
            }

            if (sharedFile == null)
            {
                _sharedParamFileMissing = true;
                _sharedParamFileMissingMessage = "No shared parameter file is loaded — set Manage > Shared Parameters first.";
                return _sharedParamFileMissingMessage;
            }

            ExternalDefinition def = null;
            foreach (var group in sharedFile.Groups)
            {
                foreach (var d in group.Definitions)
                {
                    if (d is ExternalDefinition ed && string.Equals(ed.Name, paramName, StringComparison.Ordinal))
                    {
                        def = ed;
                        break;
                    }
                }
                if (def != null) break;
            }

            if (def == null)
                return $"'{paramName}' is not defined in the loaded shared parameter file.";

            var category = elem.Category;
            if (category == null || !category.AllowsBoundParameters)
                return $"Element category does not accept bound parameters.";

            var bindings = _doc.ParameterBindings;
            var existing = bindings.get_Item(def);
            var catSet = app.Create.NewCategorySet();

            if (existing is ElementBinding eb)
            {
                // Already bound somewhere — expand the CategorySet to include our category.
                foreach (Category c in eb.Categories)
                    catSet.Insert(c);
                if (catSet.Contains(category))
                    return $"'{paramName}' is bound but not visible on element — check binding scope.";
                catSet.Insert(category);

                // Preserve the existing binding kind (instance vs type) so we don't
                // accidentally flip how every other category sees this param.
                Binding newBinding = eb is InstanceBinding
                    ? (Binding)app.Create.NewInstanceBinding(catSet)
                    : app.Create.NewTypeBinding(catSet);

                if (!bindings.ReInsert(def, newBinding, GroupTypeId.Data))
                    return $"Failed to extend binding for '{paramName}' to this category.";
                return null;
            }

            // Fresh binding. JKR classification params (Kod_Jenis, Sistem, etc.) are
            // type-level by spec — bind to the element's type so the value applies to
            // every instance of that type and avoids the read-only-on-instance trap.
            catSet.Insert(category);
            var binding = app.Create.NewTypeBinding(catSet);
            if (!bindings.Insert(def, binding, GroupTypeId.Data))
                return $"Failed to bind '{paramName}' to category '{category.Name}'.";
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
