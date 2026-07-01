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
            BindingMutation bindingMutation = null;

            if (string.IsNullOrEmpty(fix.ParameterName))
                return new FixResult { Success = false, Message = "No parameter name provided" };

            var elem = _doc.GetElement(new ElementId(fix.ElementId));
            if (elem == null)
                return new FixResult { Success = false, Message = $"Element {fix.ElementId} not found" };

            param = ResolveParameter(elem, fix.ParameterName, fix.Target);

            // If the param isn't on the element at all, try to bind it from the
            // currently-loaded shared parameter file. Turns "manual: add the param
            // first" into a one-click auto-fix when the JKR shared params file is
            // loaded in the project.
            if (param == null)
            {
                var bindMsg = TryBindSharedParameter(elem, fix.ParameterName, out bindingMutation);
                if (bindMsg != null)
                    return new FixResult
                    {
                        Success = false,
                        Message = $"Parameter '{fix.ParameterName}' not found on element {fix.ElementId}. {bindMsg}",
                    };

                // Re-resolve after binding.
                param = ResolveParameter(elem, fix.ParameterName, fix.Target);
                if (param == null)
                {
                    UndoBindingIfWePlacedIt(bindingMutation);
                    return new FixResult
                    {
                        Success = false,
                        Message = $"Parameter '{fix.ParameterName}' was bound but still not visible on element {fix.ElementId}.",
                    };
                }
            }

            if (param.IsReadOnly)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Fix] read-only failure: param='{fix.ParameterName}' elem={fix.ElementId} target={fix.Target} " +
                    $"placedBinding={(bindingMutation == null ? "no" : "yes")}");
                // Critical: undo the binding we just placed. ParameterBindings.Insert
                // is NOT rolled back by Revit's SubTransaction (document-level op that
                // commits immediately), so without explicit cleanup the binding leaks
                // and changes which rule fires on the next scan, dropping affected
                // elements out of FixableCount post-Reset.
                UndoBindingIfWePlacedIt(bindingMutation);
                return new FixResult { Success = false, Message = $"Parameter '{fix.ParameterName}' is read-only" };
            }

            return null;
        }

        /// <summary>
        /// Look up a parameter on an element, preferring the writable handle.
        ///
        /// `target` controls which side is tried first:
        ///   "type"     — prefer ElementType.LookupParameter (JKR classification
        ///                params are type-bound by spec, instance copies are
        ///                read-only on most categories).
        ///   "instance" — prefer the placed element's LookupParameter (default).
        ///
        /// In both modes we still fall back to the other side so that projects
        /// with non-spec bindings keep working. As a last resort we return a
        /// read-only handle so the caller can surface a clean "read-only" error
        /// instead of "not found".
        /// </summary>
        private Parameter ResolveParameter(Element elem, string name, string target = "instance")
        {
            var instParam = elem.LookupParameter(name);
            var typeElem = _doc.GetElement(elem.GetTypeId());
            var typeParam = typeElem?.LookupParameter(name);

            bool preferType = string.Equals(target, "type", StringComparison.OrdinalIgnoreCase);

            if (preferType)
            {
                if (typeParam != null && !typeParam.IsReadOnly) return typeParam;
                if (instParam != null && !instParam.IsReadOnly) return instParam;
            }
            else
            {
                if (instParam != null && !instParam.IsReadOnly) return instParam;
                if (typeParam != null && !typeParam.IsReadOnly) return typeParam;
            }

            return preferType ? (typeParam ?? instParam) : (instParam ?? typeParam);
        }

        /// <summary>Snapshot of a binding mutation we made, used to roll it back if
        /// the subsequent value-set fails. Revit's SubTransaction can't undo
        /// ParameterBindings changes (they're treated as document-level operations
        /// that commit immediately), so we have to track and reverse them ourselves.</summary>
        private class BindingMutation
        {
            public Definition Definition;
            public Category Category;
            public bool WasFreshBind;        // true: we Inserted; false: we ReInserted with extended set
            public bool WasInstanceBinding;  // for the ReInsert case, preserves original binding kind
        }

        /// <summary>
        /// Bind a JKR shared parameter to the element's category if it's defined in
        /// the currently-loaded shared parameter file but not yet bound to the project.
        /// Returns null on success; otherwise an error message explaining why the bind
        /// couldn't happen. On success, `mutation` describes what we changed so the
        /// caller can roll it back via UndoBindingIfWePlacedIt() if the fix attempt
        /// then fails (e.g. the parameter turned out read-only on this element type).
        /// Must be called inside an open Transaction.
        /// </summary>
        private string TryBindSharedParameter(Element elem, string paramName, out BindingMutation mutation)
        {
            mutation = null;
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
                bool wasInstance = eb is InstanceBinding;
                Binding newBinding = wasInstance
                    ? (Binding)app.Create.NewInstanceBinding(catSet)
                    : app.Create.NewTypeBinding(catSet);

                if (!bindings.ReInsert(def, newBinding, GroupTypeId.Data))
                    return $"Failed to extend binding for '{paramName}' to this category.";
                mutation = new BindingMutation
                {
                    Definition = def,
                    Category = category,
                    WasFreshBind = false,
                    WasInstanceBinding = wasInstance,
                };
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Bind] extended '{paramName}' to '{category.Name}' (was-instance={wasInstance})");
                return null;
            }

            // Fresh binding. JKR classification params (Kod_Jenis, Sistem, etc.) are
            // type-level by spec — bind to the element's type so the value applies to
            // every instance of that type and avoids the read-only-on-instance trap.
            catSet.Insert(category);
            var binding = app.Create.NewTypeBinding(catSet);
            if (!bindings.Insert(def, binding, GroupTypeId.Data))
                return $"Failed to bind '{paramName}' to category '{category.Name}'.";
            mutation = new BindingMutation
            {
                Definition = def,
                Category = category,
                WasFreshBind = true,
                WasInstanceBinding = false,
            };
            System.Diagnostics.Debug.WriteLine(
                $"[BINA Bind] fresh-bound '{paramName}' to '{category.Name}'");
            return null;
        }

        /// <summary>Roll back a binding mutation we previously made via TryBindSharedParameter.
        /// Called when the subsequent value-set fails (e.g. param turned out read-only),
        /// so a failed fix attempt leaves zero footprint on the model and the next scan
        /// sees the original rule fire instead of a derivative one.</summary>
        private void UndoBindingIfWePlacedIt(BindingMutation mutation)
        {
            if (mutation == null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[BINA Unbind] called with null mutation — no bind side-effect to undo");
                return;
            }

            var paramLabel = mutation.Definition?.Name ?? "<unknown>";
            var catLabel = mutation.Category?.Name ?? "<unknown>";

            try
            {
                var bindings = _doc.ParameterBindings;
                if (mutation.WasFreshBind)
                {
                    // We added the binding from scratch — remove it entirely.
                    var removed = bindings.Remove(mutation.Definition);
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Unbind] fresh — Remove('{paramLabel}') returned {removed}");
                    return;
                }

                // We extended an existing binding — rebuild it without our category.
                if (!(bindings.get_Item(mutation.Definition) is ElementBinding eb))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Unbind] extended — binding for '{paramLabel}' no longer exists; nothing to undo");
                    return;
                }

                var newSet = _doc.Application.Create.NewCategorySet();
                foreach (Category c in eb.Categories)
                {
                    if (c.Id != mutation.Category.Id)
                        newSet.Insert(c);
                }

                Binding newBinding = mutation.WasInstanceBinding
                    ? (Binding)_doc.Application.Create.NewInstanceBinding(newSet)
                    : _doc.Application.Create.NewTypeBinding(newSet);

                var reinserted = bindings.ReInsert(mutation.Definition, newBinding, GroupTypeId.Data);
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Unbind] extended — stripped '{catLabel}' from '{paramLabel}' (ReInsert returned {reinserted})");
            }
            catch (Exception ex)
            {
                // Best-effort cleanup. If undo fails, the model carries the residual
                // binding; the user will see the same "82 → 83 fixable" drift on
                // affected categories, but no other harm done.
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Unbind] FAILED for '{paramLabel}'/'{catLabel}': {ex.Message}");
            }
        }

        private FixResult WriteParameterValue(Parameter param, JkrFixAction fix)
        {
            bool set = false;
            string before = "";
            string after = "";
            switch (param.StorageType)
            {
                case StorageType.String:
                    before = param.AsString() ?? "";
                    set = param.Set(fix.Value);
                    after = param.AsString() ?? "";
                    break;
                case StorageType.Integer:
                    before = param.AsInteger().ToString();
                    if (int.TryParse(fix.Value, out int intVal))
                        set = param.Set(intVal);
                    after = param.AsInteger().ToString();
                    break;
                case StorageType.Double:
                    before = param.AsDouble().ToString();
                    if (double.TryParse(fix.Value, out double dblVal))
                        set = param.Set(dblVal);
                    after = param.AsDouble().ToString();
                    break;
                default:
                    return new FixResult { Success = false, Message = $"Unsupported storage type for '{fix.ParameterName}'" };
            }

            if (!set)
                return new FixResult { Success = false, Message = $"Failed to set '{fix.ParameterName}'" };

            // Detect silent no-ops: Set returned true but the underlying value didn't
            // actually change. This is exactly the kind of drift that leaves a
            // reverse-fix unable to restore state — the value Revit reports as "current"
            // doesn't match what we asked for.
            if (before == after && fix.Value != before)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Set] NO-OP detected: param='{fix.ParameterName}' elem={fix.ElementId} " +
                    $"asked='{fix.Value}' before='{before}' after='{after}' (Set returned true but value didn't change)");
            }

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
        public long ElementId { get; set; }

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

        // "instance" or "type". Tells PreflightSetParameter which side of the
        // element to write to. JKR classification params (Kod_Jenis, Sistem,
        // Bahan, etc.) are type-bound by spec; the backend marks them "type"
        // so the applicator skips the read-only instance handle.
        [JsonProperty("target")]
        public string Target { get; set; } = "instance";
    }

    public class FixResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Action { get; set; } = "";
        public long ElementId { get; set; }
        public long CheckElementId { get; set; }
        public string ParameterName { get; set; } = "";
        public string Rule { get; set; } = "";
    }
}
