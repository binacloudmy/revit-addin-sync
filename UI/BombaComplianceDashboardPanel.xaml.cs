using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Bomba;

namespace RevitWebAppSync.UI
{
    public partial class BombaComplianceDashboardPanel : UserControl
    {
        // Phase-1 default, one place, replaced by a jurisdiction picker later.
        // The requirements ROW is never defaulted — the cascade asks (design
        // 1A / prototype: purpose group is a human decision, asked once).
        private const string DefaultJurisdiction = "peninsular";

        private readonly BombaDashboardViewModel _vm;
        private readonly BombaComplianceService _bombaService = new BombaComplianceService();
        private UIApplication _uiApp;

        // "Asked once": the resolved schedule path per document, for this
        // session. Writing _bomba_purpose_group back into the model needs the
        // ExternalEvent write path (arrives with autofix in phase 2).
        private static readonly Dictionary<string, string> _pathByDoc = new Dictionary<string, string>();
        private bool _cascadeLoading;
        private CascadeLevelVm _bandLevel;

        public BombaComplianceDashboardPanel()
        {
            InitializeComponent();
            _vm = new BombaDashboardViewModel();
            this.DataContext = _vm;
        }

        public BombaDashboardViewModel ViewModel { get { return _vm; } }

        /// Called by the command when the pane opens, so the panel can reach
        /// the live document without depending on Revit at construction.
        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        // Command sets _uiApp when the pane opens; App.UiApp is the fallback
        // when the pane was restored by Revit before the command ever ran
        // (same seam as JkrComplianceDashboardPanel.UiAppLive).
        private UIApplication UiAppLive
        {
            get { return _uiApp ?? App.UiApp; }
        }

        private Autodesk.Revit.DB.Document LiveDoc
        {
            get
            {
                var uiApp = UiAppLive;
                return uiApp != null && uiApp.ActiveUIDocument != null ? uiApp.ActiveUIDocument.Document : null;
            }
        }

        private string DocKey
        {
            get
            {
                var doc = LiveDoc;
                if (doc == null) return null;
                return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
            }
        }

        private string RememberedPath
        {
            get
            {
                var key = DocKey;
                string path;
                return key != null && _pathByDoc.TryGetValue(key, out path) ? path : null;
            }
        }

        // ── cascade ─────────────────────────────────────────────────────────

        // Design 1A / prototype select captions; deeper levels fall back to a
        // generic caption. The band caption is set explicitly on needs_input.
        private static readonly string[] LevelLabels = { "PURPOSE GROUP", "OCCUPANCY", "SUB-ITEM" };

        private async Task EnsureCascadeAsync()
        {
            if (_cascadeLoading || _vm.Cascade.Count > 0) return;
            _cascadeLoading = true;
            try
            {
                RefreshMeasuredFacts();
                await AppendCascadeLevelAsync(null);
            }
            finally
            {
                _cascadeLoading = false;
            }
        }

        private void RefreshMeasuredFacts()
        {
            var doc = LiveDoc;
            if (doc == null) return;
            try { _vm.MeasuredFacts = BombaFactsExtractor.Extract(doc).Label; }
            catch { /* facts line is informational; the scan extracts again */ }
        }

        /// Fetch the options below parentPath and append them as a new level.
        /// Exactly one option auto-selects (single-child chains cost no clicks);
        /// zero options appends nothing (parent was a leaf). A level whose
        /// options are ALL leaves is the band row of the table — it is never
        /// rendered as a select: measured facts resolve it at Run, and only a
        /// needs_input answer brings it back as an explicit BAND choice.
        private async Task AppendCascadeLevelAsync(string parentPath)
        {
            var resp = await _bombaService.OptionsAsync(DefaultJurisdiction, parentPath);
            if (resp == null) return;
            if (resp.Error == BombaComplianceService.LoginRequiredMessage)
            {
                _vm.ScopeDetail = BombaComplianceService.LoginRequiredMessage;
                return;
            }
            if (resp.Error != null)
            {
                _vm.SetupGuidance = resp.Error;
                return;
            }
            if (resp.Options == null || resp.Options.Count == 0) return;
            if (parentPath != null && resp.Options.All(o => o.IsLeaf)) return;

            var level = new CascadeLevelVm();
            level.Label = _vm.Cascade.Count < LevelLabels.Length
                ? LevelLabels[_vm.Cascade.Count]
                : "LEVEL " + (_vm.Cascade.Count + 1);
            foreach (var o in resp.Options) level.Options.Add(o);
            _vm.Cascade.Add(level);
            if (resp.Options.Count == 1)
                level.Selected = level.Options[0];   // triggers SelectionChanged
        }

        private async void CascadeLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as System.Windows.Controls.ComboBox;
            var level = combo != null ? combo.DataContext as CascadeLevelVm : null;
            if (level == null || level.Selected == null) return;

            // A re-selection higher up invalidates everything below it,
            // including a pending BAND ask.
            int index = _vm.Cascade.IndexOf(level);
            while (_vm.Cascade.Count > index + 1)
            {
                if (ReferenceEquals(_vm.Cascade[_vm.Cascade.Count - 1], _bandLevel)) _bandLevel = null;
                _vm.Cascade.RemoveAt(_vm.Cascade.Count - 1);
            }

            _vm.CascadeCrumb = level.Selected.Path;
            // Any selection is runnable: a leaf runs directly; a band parent
            // lets the engine resolve the band from measured facts, and an
            // ambiguity comes back as needs_input options appended below.
            _vm.CanRun = true;

            if (!level.Selected.IsLeaf)
            {
                try { await AppendCascadeLevelAsync(level.Selected.Path); }
                catch { /* next Run surfaces the error */ }
            }
        }

        // ── scan ────────────────────────────────────────────────────────────

        private void Rescan_Click(object sender, RoutedEventArgs e)
        {
            var remembered = RememberedPath ?? DeepestSelectedPath();
            if (remembered == null)
            {
                // Nothing chosen yet — Re-check routes into the ask, it never guesses.
                _vm.State = PaneState.NeedsSetup;
                _ = EnsureCascadeAsync();
                return;
            }
            _ = RunScanAsync(remembered);
        }

        private void RunSetupScan_Click(object sender, RoutedEventArgs e)
        {
            var path = DeepestSelectedPath();
            if (path == null) return;
            _ = RunScanAsync(path);
        }

        private string DeepestSelectedPath()
        {
            var last = _vm.Cascade.LastOrDefault(l => l.Selected != null);
            return last != null ? last.Selected.Path : null;
        }

        private async Task RunScanAsync(string schedulePath)
        {
            if (_vm.Scanning) return;
            var doc = LiveDoc;
            if (doc == null)
            {
                TaskDialog.Show("BINA Bomba Compliance", "Buka dokumen Revit dahulu.");
                return;
            }

            _vm.Scanning = true;
            try
            {
                await RunScanInner(doc, schedulePath);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Bomba Compliance", $"Scan error:\n\n{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _vm.Scanning = false;
            }
        }

        private async Task RunScanInner(Autodesk.Revit.DB.Document doc, string schedulePath)
        {
            var facts = BombaFactsExtractor.Extract(doc);
            var response = await _bombaService.CheckAsync(BuildRequest(facts, schedulePath));
            // A single needs_input option is no choice at all — advance through
            // it server-side instead of rendering a one-item select.
            for (int hop = 0; hop < 4; hop++)
            {
                if (response == null || response.Error != null || !response.NeedsInput) break;
                if (response.Options == null || response.Options.Count != 1) break;
                schedulePath = response.Options[0].Path;
                response = await _bombaService.CheckAsync(BuildRequest(facts, schedulePath));
            }
            if (response == null) return;

            if (response.Error == BombaComplianceService.LoginRequiredMessage)
            {
                // Persistent state, not a dismissable dialog — a TaskDialog
                // would leave stale results looking valid behind it (the JKR
                // pane learned this; same rule here).
                _vm.ReplaceChecks(new List<CheckVm>(), null);
                _vm.ScopeDetail = BombaComplianceService.LoginRequiredMessage;
                return;
            }
            if (response.Error != null)
            {
                TaskDialog.Show("BINA Bomba Compliance", $"Scan failed:\n\n{response.Error}");
                return;
            }
            if (response.NeedsInput)
            {
                // The facts do not pick a single band — extend the cascade
                // with the backend's options and ask (None means ASK, design
                // invariant). No TaskDialog: the choice lives in the pane.
                _vm.State = PaneState.NeedsSetup;
                _vm.SetupGuidance = response.Guidance
                    ?? "The model facts do not select a single row — choose the applicable band.";
                RefreshMeasuredFacts();
                // Re-runs must not stack band selects — one BAND level, replaced.
                if (_bandLevel != null) { _vm.Cascade.Remove(_bandLevel); _bandLevel = null; }
                if (response.Options != null && response.Options.Count > 0)
                {
                    var level = new CascadeLevelVm();
                    level.Label = "BAND — the facts sit on a boundary, choose one";
                    foreach (var o in response.Options) level.Options.Add(o);
                    _vm.Cascade.Add(level);
                    _bandLevel = level;
                }
                _vm.CanRun = false;   // until a band is picked
                return;
            }

            var key = DocKey;
            if (key != null) _pathByDoc[key] = schedulePath;   // asked once per model

            _vm.ReplaceChecks(BombaMapper.Map(response), null);
            _vm.ScopeLabel = string.IsNullOrEmpty(facts.FileName) ? "Bomba Compliance" : facts.FileName;
            _vm.ScopeDetail = response.Jurisdiction
                + " · " + (response.RulesVersion ?? "?")
                + (response.RulesStatus != null && response.RulesStatus != "VERIFIED"
                    ? " · " + response.RulesStatus : "");
        }

        private static BombaCheckRequestDto BuildRequest(BombaModelFacts facts, string schedulePath)
        {
            var request = new BombaCheckRequestDto();
            request.Project.ProjectName = facts.ProjectName;
            request.Project.FileName = facts.FileName;
            request.Jurisdiction = DefaultJurisdiction;
            request.SchedulePath = schedulePath;
            request.Facts.FloorAreaM2 = facts.FloorAreaM2;
            request.Facts.HeightMm = facts.HeightMm;
            // Phase 1: no fire-system counting yet, host model only. The
            // backend answers NOT CHECKED for M&E-resident systems — honest,
            // never a false "missing".
            request.SearchedModels = facts.SearchedModels;
            return request;
        }
    }
}
