using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// AI-436: the Revit and backend side of the Project Checklist. A button does its
    /// work (Save As, Load Shared Parameters, the BPEP write), then the item's own
    /// re-check runs; the item changes only on that answer (ProjectChecklistModel).
    /// Rule 26 has no button: it is fed from every element-rule run.
    /// </summary>
    public sealed class ProjectChecklistController
    {
        /// <summary>The backend's name for rule 26's check in a scan answer.</summary>
        internal const string NamingAdoptionRule = "Element naming adoption";

        private readonly ProjectChecklistModel _model;
        private readonly JkrComplianceService _service;
        private readonly Func<int> _lod;
        private readonly Func<string> _discipline;
        private readonly Action<string> _toast;
        private readonly Dispatcher _ui;
        private bool _subscribed;

        public ProjectChecklistController(ProjectChecklistModel model, JkrComplianceService service,
            Func<int> lod, Func<string> discipline, Action<string> toast)
        {
            _model = model;
            _service = service;
            _lod = lod;
            _discipline = discipline;
            _toast = toast;
            _ui = Dispatcher.CurrentDispatcher;
            model.ActionRequested += rule => _ = ActAsync(rule);
            model.RecheckRequested += rule => _ = RecheckAsync(rule);
        }

        private static Handlers.RevitDispatcher Revit => App.RevitDispatcher;

        private static string L(string ms, string en) => PaneText.IsEn(null) ? en : ms;

        // ── rule 26: every element-rule run ──

        /// <summary>The element rules just ran (full scan or targeted re-check). Null response = the backend failed.</summary>
        public void OnElementRulesRan(ModelCheckResponse response)
        {
            var adoption = response?.BuildingRequirements?.Concat(response.ElementIssues ?? Enumerable.Empty<ComplianceIssueDto>())
                .FirstOrDefault(i => i != null && string.Equals(i.Rule, NamingAdoptionRule, StringComparison.OrdinalIgnoreCase));
            _model.ApplyNamingAdoption(adoption?.Actual);
        }

        // ── re-checks ──

        /// <summary>After a full scan: every project rule re-runs on the model as it is now.</summary>
        public async Task RecheckAllAsync()
        {
            if (JkrDevMode.Current.UsesFixture) return;   // test fixture: no backend
            foreach (var rule in new[] { 1, 21, 22, 23, 24 })
                await RecheckAsync(rule);
        }

        public async Task RecheckAsync(int rule)
        {
            if (ProjectChecklistModel.EndpointKey(rule) == null) return;
            _model.Begin(rule);
            JkrProjectRecheckResponse answer;
            try
            {
                int lod = _lod(); var disc = _discipline();
                var project = await Revit.InvokeAsync(app =>
                {
                    var doc = app.ActiveUIDocument?.Document;
                    return doc == null ? null : JkrBuildingInfoExtractor.ExtractProject(doc, lod, disc);
                });
                answer = project == null
                    ? new JkrProjectRecheckResponse { RuleNumber = rule, Error = L("Tiada model dibuka.", "No model is open.") }
                    : await _service.ProjectRecheckAsync(rule, project);
            }
            catch (Exception ex)
            {
                answer = new JkrProjectRecheckResponse { RuleNumber = rule, Error = ex.Message };
            }
            _model.ApplyRecheck(rule, answer);
        }

        // ── buttons ──

        private async Task ActAsync(int rule)
        {
            try
            {
                switch (ProjectChecklistModel.ActionOf(rule))
                {
                    case ProjectCheckAction.SaveAs:
                        await PostAsync(PostableCommand.SaveAsProject);
                        // The dialog is the user's: rules 1 and 23 re-check when the file is saved as.
                        await WatchSaveAsAsync();
                        break;
                    case ProjectCheckAction.LoadSharedParams:
                        // Revit gives no event when the file is picked: "Check again" reads it.
                        await PostAsync(PostableCommand.SharedParameters);
                        break;
                    case ProjectCheckAction.ConfirmBpep:
                        var error = await Revit.InvokeAsync(app => WriteBpepConfirmed(app));
                        if (error != null) { _toast?.Invoke(error); return; }
                        await RecheckAsync(rule);   // never trust the write: the item flips on this
                        break;
                }
            }
            catch (Exception ex)
            {
                _toast?.Invoke(ex.Message);
            }
        }

        private async Task PostAsync(PostableCommand cmd)
        {
            var posted = await Revit.InvokeAsync(app =>
            {
                var id = RevitCommandId.LookupPostableCommandId(cmd);
                if (id == null || !app.CanPostCommand(id)) return false;
                app.PostCommand(id);
                return true;
            });
            if (!posted) _toast?.Invoke(L("Perintah ini tidak tersedia sekarang di Revit.", "Revit cannot run this command right now."));
        }

        private async Task WatchSaveAsAsync()
        {
            if (_subscribed) return;
            _subscribed = true;
            await Revit.InvokeAsync(app =>
            {
                app.Application.DocumentSavedAs += (s, e) =>
                    _ui.BeginInvoke(new Action(async () => { await RecheckAsync(1); await RecheckAsync(23); }));
            });
        }

        /// <summary>
        /// Writes Project Information "BPEP_Confirmed" = "Yes" in one transaction (one undo step),
        /// creating the text parameter first when the model has none. Returns an error text, or null.
        /// </summary>
        private static string WriteBpepConfirmed(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return L("Tiada model dibuka.", "No model is open.");
            var info = doc.ProjectInformation;
            if (info == null) return L("Model tiada Project Information.", "The model has no Project Information.");

            using (var tx = new Transaction(doc, "BINA: confirm BPEP"))
            {
                try
                {
                    tx.Start();
                    var p = info.LookupParameter(JkrBuildingInfoExtractor.BpepConfirmedParam);
                    if (p == null)
                    {
                        CreateBpepParameter(app.Application, doc);
                        p = info.LookupParameter(JkrBuildingInfoExtractor.BpepConfirmedParam);
                    }
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                    {
                        tx.RollBack();
                        return L("Parameter BPEP_Confirmed tidak boleh ditulis (mesti teks, boleh diubah).",
                                 "BPEP_Confirmed cannot be written (it must be an editable text parameter).");
                    }
                    p.Set("Yes");
                    tx.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return ex.Message;
                }
            }
        }

        /// <summary>
        /// A text instance parameter on Project Information, defined through a temporary shared
        /// parameter file. The user's own shared parameter file is put back afterwards (rule 24
        /// reads it).
        /// </summary>
        private static void CreateBpepParameter(Autodesk.Revit.ApplicationServices.Application application, Document doc)
        {
            string previous = application.SharedParametersFilename;
            try
            {
                var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BINA_BpepParam.txt");
                if (!System.IO.File.Exists(temp)) System.IO.File.WriteAllText(temp, "");
                application.SharedParametersFilename = temp;
                var file = application.OpenSharedParameterFile();
                var group = file.Groups.get_Item("BINA") ?? file.Groups.Create("BINA");
                var def = group.Definitions.get_Item(JkrBuildingInfoExtractor.BpepConfirmedParam) as ExternalDefinition
                    ?? group.Definitions.Create(new ExternalDefinitionCreationOptions(
                        JkrBuildingInfoExtractor.BpepConfirmedParam, SpecTypeId.String.Text)) as ExternalDefinition;

                var cats = application.Create.NewCategorySet();
                cats.Insert(Category.GetCategory(doc, BuiltInCategory.OST_ProjectInformation));
                var binding = application.Create.NewInstanceBinding(cats);
                if (!doc.ParameterBindings.Insert(def, binding, GroupTypeId.Data))
                    doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Data);
            }
            finally
            {
                application.SharedParametersFilename = previous ?? "";
            }
        }
    }
}
