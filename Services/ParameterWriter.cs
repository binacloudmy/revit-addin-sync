using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Writes Bina element parameters into the open model (ClickUp 86d3y5jxx).
    ///
    /// Parameters added in the BINA viewer live only in Bina's database, so a
    /// model downloaded from BINA opens in Revit without them. This puts them
    /// back: elements are matched on UniqueId — which is exactly what BINA
    /// stores as `elementExternalId` — and each value is written to the
    /// element's own parameter where one exists, or to a BINA shared parameter
    /// created and bound to that category where one does not.
    ///
    /// Must be called inside an open Transaction. Every item is attempted
    /// independently: a model where half the elements have been deleted since
    /// the parameters were entered still gets the other half, and says so.
    ///
    /// The binding half is modelled on JkrFixApplicator, including its hard-won
    /// rule that ParameterBindings changes are document-level and are NOT rolled
    /// back by a SubTransaction, so each item undoes its own binding when its
    /// write fails (UndoBindingIfWePlacedIt). ParameterPullService.Write also runs
    /// the whole batch in one transaction that is rolled back on failure; that
    /// the rollback reverts the bindings too has not been confirmed on a real
    /// model, which is why the per-item undo stays.
    /// </summary>
    public sealed class ParameterWriter
    {
        private const string SharedParameterGroup = "BINA";
        private const string SharedParameterFileName = "bina_shared_params.txt";

        private readonly Document _doc;
        private bool _sharedParamFileMissing;
        private bool _sharedFilenameChanged;
        private string _originalSharedFilename;
        private string _sharedParamFileMissingMessage;

        public ParameterWriter(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        /// <summary>What happened to the batch, in the words the summary uses.</summary>
        public sealed class Report
        {
            public int Applied { get; set; }
            public int ElementsTouched { get; set; }
            /// <summary>Parameters whose element is not in this model — usually version drift.</summary>
            public List<string> ElementNotFound { get; } = new List<string>();
            /// <summary>Parameters Revit will not let us write (built-ins like Area, Volume).</summary>
            public List<string> ReadOnly { get; } = new List<string>();
            /// <summary>Existing measurable parameters (Length, Area...): BINA stores no unit, so the number is not written.</summary>
            public List<string> Measurable { get; } = new List<string>();
            /// <summary>Everything else, each with the reason Revit gave.</summary>
            public List<string> Failed { get; } = new List<string>();

            public int SkippedCount => ElementNotFound.Count + ReadOnly.Count + Measurable.Count + Failed.Count;
        }

        /// <param name="onProgress">Called as (done, total) so a caller can show progress.</param>
        /// <param name="cancel">Checked between elements; cancelling throws so the caller's transaction rolls back.</param>
        public Report Apply(
            IEnumerable<BinaElementParameter> parameters,
            Action<int, int> onProgress = null,
            System.Threading.CancellationToken cancel = default)
        {
            var report = new Report();
            var plan = ParameterApplyPlan.Build(parameters);
            int total = plan.Usable;
            int done = 0;

            // Grouped by element so each UniqueId is resolved once, and so a
            // deleted element is reported once per parameter but looked up once.
            foreach (var group in plan.Groups)
            {
                cancel.ThrowIfCancellationRequested();

                Element elem = null;
                try
                {
                    elem = _doc.GetElement(group.Key);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Params] lookup failed for {group.Key}: {ex.Message}");
                }

                if (elem == null)
                {
                    foreach (var p in group)
                    {
                        report.ElementNotFound.Add(p.ParameterName);
                        onProgress?.Invoke(++done, total);
                    }
                    continue;
                }

                bool touched = false;
                foreach (var p in group)
                {
                    if (ApplyOne(elem, p, report)) touched = true;
                    onProgress?.Invoke(++done, total);
                }

                if (touched) report.ElementsTouched++;
            }

            return report;
        }

        /// <summary>
        /// Puts back the session state this writer changed: the shared parameter
        /// file path, when it had to point Revit at the add-in's own file. The
        /// bindings themselves belong to the document transaction.
        /// </summary>
        public void RestoreApplicationState()
        {
            if (!_sharedFilenameChanged) return;
            try
            {
                _doc.Application.SharedParametersFilename = _originalSharedFilename ?? "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Params] could not restore the shared parameter file path: {ex.Message}");
            }
            finally
            {
                _sharedFilenameChanged = false;
            }
        }

        /// <returns>True when the value reached the model.</returns>
        private bool ApplyOne(Element elem, BinaElementParameter p, Report report)
        {
            BindingMutation mutation = null;
            try
            {
                var param = ResolveParameter(elem, p.ParameterName);

                // Nothing of that name on the element. An Add parameter is
                // expected to be missing; an Override whose target is gone is
                // treated the same way rather than dropped, because the value
                // the user entered is real either way.
                if (param == null)
                {
                    string bindError = TryBindSharedParameter(elem, p, out mutation);
                    if (bindError != null)
                    {
                        report.Failed.Add($"{p.ParameterName}: {bindError}");
                        return false;
                    }

                    param = ResolveParameter(elem, p.ParameterName);
                    if (param == null)
                    {
                        UndoBindingIfWePlacedIt(mutation);
                        report.Failed.Add(
                            $"{p.ParameterName}: bound to the category but still not on the element");
                        return false;
                    }
                }

                // Revit blocks instance writes on model-group members outside
                // group edit mode, and does it with a modal error that would
                // stall the whole batch. Refuse it up front instead.
                if (param.Element != null && param.Element.Id == elem.Id
                    && elem.GroupId != null && elem.GroupId != ElementId.InvalidElementId)
                {
                    UndoBindingIfWePlacedIt(mutation);
                    report.Failed.Add(
                        $"{p.ParameterName}: element is inside a model group — edit the group and re-run");
                    return false;
                }

                if (param.IsReadOnly)
                {
                    UndoBindingIfWePlacedIt(mutation);
                    report.ReadOnly.Add(p.ParameterName);
                    return false;
                }

                // BINA stores a bare number with no unit; Parameter.Set(double) reads
                // it as feet. Skip an existing measurable parameter rather than
                // store a wrong size.
                if (param.StorageType == StorageType.Double
                    && ValueCoercion.SkipsMeasurable(CoerceKind.Double, IsMeasurable(param)))
                {
                    UndoBindingIfWePlacedIt(mutation);
                    report.Measurable.Add(p.ParameterName);
                    return false;
                }

                string writeError = WriteValue(param, p);
                if (writeError != null)
                {
                    UndoBindingIfWePlacedIt(mutation);
                    report.Failed.Add($"{p.ParameterName}: {writeError}");
                    return false;
                }

                report.Applied++;
                return true;
            }
            catch (Exception ex)
            {
                UndoBindingIfWePlacedIt(mutation);
                report.Failed.Add($"{p.ParameterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The element's parameter of that name, preferring a writable handle
        /// and preferring the instance over the type — a Bina parameter belongs
        /// to the element the user clicked, not to every element of its type.
        /// A read-only handle is returned rather than null so the caller can say
        /// "read-only" instead of "not found".
        /// </summary>
        private Parameter ResolveParameter(Element elem, string name)
        {
            var instanceParam = elem.LookupParameter(name);
            if (instanceParam != null && !instanceParam.IsReadOnly) return instanceParam;

            var typeElem = _doc.GetElement(elem.GetTypeId());
            var typeParam = typeElem?.LookupParameter(name);
            if (typeParam != null && !typeParam.IsReadOnly) return typeParam;

            return instanceParam ?? typeParam;
        }

        /// <summary>
        /// Revit's own type for a Bina parameter type.
        ///
        /// Number maps to the unitless spec deliberately: Parameter.Set(double)
        /// takes internal (feet) units, so a Length would silently reinterpret
        /// "1200" as 1200 feet. Unitless means the number written is the number
        /// the user typed. Date has no Revit equivalent and travels as text.
        /// </summary>
        private static ForgeTypeId SpecFor(string parameterType)
        {
            switch ((parameterType ?? "").Trim())
            {
                case "Number": return SpecTypeId.Number;
                case "YesNo": return SpecTypeId.Boolean.YesNo;
                default: return SpecTypeId.String.Text;
            }
        }

        /// <summary>
        /// True when the parameter has a unit (Length, Area, Percentage...), not counting the
        /// unitless Number spec BINA creates. Unknown counts as measurable: skipping is the safe side.
        /// </summary>
        private static bool IsMeasurable(Parameter param)
        {
            try
            {
                var spec = param.Definition.GetDataType();
                bool isNumber = spec == SpecTypeId.Number || SpecPolicy.IsNumberTypeId(spec?.TypeId);
                return SpecPolicy.IsMeasurable(UnitUtils.IsMeasurableSpec(spec), isNumber);
            }
            catch { return true; }
        }

        private string WriteValue(Parameter param, BinaElementParameter p)
        {
            CoerceKind kind;
            switch (param.StorageType)
            {
                case StorageType.String: kind = CoerceKind.String; break;
                case StorageType.Integer: kind = CoerceKind.Integer; break;
                case StorageType.Double: kind = CoerceKind.Double; break;
                default: return $"unsupported storage type {param.StorageType}";
            }

            var coerced = ValueCoercion.Coerce(kind, p.Value);
            if (!coerced.Ok) return coerced.Error;

            bool set;
            switch (kind)
            {
                case CoerceKind.String: set = param.Set(coerced.Text); break;
                case CoerceKind.Integer: set = param.Set(coerced.Int); break;
                default: set = param.Set(coerced.Double); break;
            }

            return set ? null : "Revit rejected the value";
        }

        /// <summary>
        /// What we changed about the document's parameter bindings, so it can be
        /// reversed. A SubTransaction will not do it for us.
        /// </summary>
        private sealed class BindingMutation
        {
            public Definition Definition;
            public Category Category;
            /// <summary>True when we created the binding; false when we widened an existing one.</summary>
            public bool WasFreshBind;
            public bool WasInstanceBinding;
        }

        /// <summary>
        /// Make the parameter exist on this element's category, creating the
        /// shared definition if the file does not carry it yet. Returns null on
        /// success, otherwise the reason to report. Must be called inside a
        /// Transaction.
        /// </summary>
        private string TryBindSharedParameter(Element elem, BinaElementParameter p, out BindingMutation mutation)
        {
            mutation = null;
            if (_sharedParamFileMissing) return _sharedParamFileMissingMessage;

            var app = _doc.Application;
            DefinitionFile sharedFile = OpenOrProvisionSharedParameterFile(app);
            if (sharedFile == null) return _sharedParamFileMissingMessage;

            ExternalDefinition def = FindDefinition(sharedFile, p.ParameterName);
            if (def == null)
            {
                try
                {
                    DefinitionGroup group = sharedFile.Groups
                        .Cast<DefinitionGroup>()
                        .FirstOrDefault(g => string.Equals(g.Name, SharedParameterGroup,
                            StringComparison.OrdinalIgnoreCase))
                        ?? sharedFile.Groups.Create(SharedParameterGroup);

                    var options = new ExternalDefinitionCreationOptions(
                        p.ParameterName, SpecFor(p.ParameterType))
                    {
                        Visible = true
                    };
                    def = group.Definitions.Create(options) as ExternalDefinition;
                    if (def == null)
                        return "could not create the shared parameter definition";
                }
                catch (Exception ex)
                {
                    return $"could not create the shared parameter: {ex.Message}";
                }
            }

            var category = elem.Category;
            if (category == null || !category.AllowsBoundParameters)
                return "this element's category does not accept added parameters";

            var bindings = _doc.ParameterBindings;
            var catSet = app.Create.NewCategorySet();

            if (bindings.get_Item(def) is ElementBinding existing)
            {
                foreach (Category c in existing.Categories) catSet.Insert(c);
                if (catSet.Contains(category))
                    return "already bound to this category but not visible on the element";
                catSet.Insert(category);

                // Keep the binding kind the project already chose; flipping it
                // would change how every other category sees the parameter.
                bool wasInstance = existing is InstanceBinding;
                Binding widened = wasInstance
                    ? (Binding)app.Create.NewInstanceBinding(catSet)
                    : app.Create.NewTypeBinding(catSet);

                if (!bindings.ReInsert(def, widened, GroupTypeId.Data))
                    return "could not extend the existing binding to this category";

                mutation = new BindingMutation
                {
                    Definition = def,
                    Category = category,
                    WasFreshBind = false,
                    WasInstanceBinding = wasInstance
                };
                return null;
            }

            // Fresh binding, always per-instance: the value was entered against
            // one element in the viewer, so binding by type would spread it to
            // every element sharing that type.
            catSet.Insert(category);
            if (!bindings.Insert(def, app.Create.NewInstanceBinding(catSet), GroupTypeId.Data))
                return $"could not bind '{p.ParameterName}' to category '{category.Name}'";

            mutation = new BindingMutation
            {
                Definition = def,
                Category = category,
                WasFreshBind = true,
                WasInstanceBinding = true
            };
            return null;
        }

        private DefinitionFile OpenOrProvisionSharedParameterFile(Autodesk.Revit.ApplicationServices.Application app)
        {
            DefinitionFile sharedFile = null;
            try
            {
                sharedFile = app?.OpenSharedParameterFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Params] could not open shared parameter file: {ex.Message}");
            }

            if (sharedFile != null) return sharedFile;

            // No shared parameter file configured in this Revit session. Give it
            // one under the add-in's own folder rather than telling the user to
            // go and set one up in Manage > Shared Parameters.
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync");
                System.IO.Directory.CreateDirectory(dir);

                string path = System.IO.Path.Combine(dir, SharedParameterFileName);
                if (!System.IO.File.Exists(path))
                {
                    System.IO.File.WriteAllText(path,
                        "# This is a Revit shared parameter file.\n" +
                        "# Provisioned by BINA to hold parameters synced from BINA Cloud.\n" +
                        "*META\tVERSION\tMINVERSION\n" +
                        "META\t2\t1\n");
                }

                if (!_sharedFilenameChanged)
                {
                    _originalSharedFilename = app.SharedParametersFilename;
                    _sharedFilenameChanged = true;
                }
                app.SharedParametersFilename = path;
                sharedFile = app.OpenSharedParameterFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Params] shared parameter file provisioning failed: {ex.Message}");
            }

            if (sharedFile == null)
            {
                _sharedParamFileMissing = true;
                _sharedParamFileMissingMessage =
                    "no shared parameter file is loaded and one could not be created";
            }
            return sharedFile;
        }

        private static ExternalDefinition FindDefinition(DefinitionFile file, string name)
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition d in group.Definitions)
                {
                    if (d is ExternalDefinition ed && string.Equals(ed.Name, name, StringComparison.Ordinal))
                        return ed;
                }
            }
            return null;
        }

        /// <summary>
        /// Reverse a binding we placed when the write that followed it failed,
        /// so a failed parameter leaves no empty field behind in the model.
        /// </summary>
        private void UndoBindingIfWePlacedIt(BindingMutation mutation)
        {
            if (mutation == null) return;

            try
            {
                var bindings = _doc.ParameterBindings;
                if (mutation.WasFreshBind)
                {
                    bindings.Remove(mutation.Definition);
                    return;
                }

                if (!(bindings.get_Item(mutation.Definition) is ElementBinding existing)) return;

                var reduced = _doc.Application.Create.NewCategorySet();
                foreach (Category c in existing.Categories)
                {
                    if (c.Id != mutation.Category.Id) reduced.Insert(c);
                }

                Binding restored = mutation.WasInstanceBinding
                    ? (Binding)_doc.Application.Create.NewInstanceBinding(reduced)
                    : _doc.Application.Create.NewTypeBinding(reduced);

                bindings.ReInsert(mutation.Definition, restored, GroupTypeId.Data);
            }
            catch (Exception ex)
            {
                // Best effort: the residue is an unused binding on one category.
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA Params] could not undo binding: {ex.Message}");
            }
        }
    }
}
