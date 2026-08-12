using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Bomba;

namespace RevitWebAppSync.UI
{
    /// Modern Flow pane (design 10A): Home → Checking → Summary → issue-by-
    /// issue Detail → Done, with a Setup screen for the one human decision
    /// (building type). All navigation and HTTP lives here; the VM only holds
    /// what the screens bind to.
    public partial class BombaComplianceDashboardPanel : UserControl
    {
        // Phase-1 default; the requirements ROW is never defaulted — Setup
        // asks (None means ask, never guess).
        private const string DefaultJurisdiction = "peninsular";

        private readonly BombaDashboardViewModel _vm;
        private readonly BombaComplianceService _bombaService = new BombaComplianceService();
        private UIApplication _uiApp;
        private DispatcherTimer _progressTimer;

        // "Asked once": the resolved schedule path (+ its label) per document,
        // for this session. Writing _bomba_purpose_group into the model needs
        // the ExternalEvent write path (phase 2).
        private static readonly Dictionary<string, string> _pathByDoc = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _labelByDoc = new Dictionary<string, string>();
        private bool _cascadeLoading;
        private CascadeLevelVm _bandLevel;

        public BombaComplianceDashboardPanel()
        {
            InitializeComponent();
            _vm = new BombaDashboardViewModel();
            this.DataContext = _vm;
            this.Loaded += (s, e) => RefreshHome();
        }

        public BombaDashboardViewModel ViewModel { get { return _vm; } }

        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
            RefreshHome();
        }

        private UIApplication UiAppLive { get { return _uiApp ?? App.UiApp; } }

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
                var key = DocKey; string p;
                return key != null && _pathByDoc.TryGetValue(key, out p) ? p : null;
            }
        }

        // ── home ────────────────────────────────────────────────────────────

        private void RefreshHome()
        {
            var doc = LiveDoc;
            if (doc == null) return;
            try
            {
                var facts = BombaFactsExtractor.Extract(doc);
                _vm.FloorLabel = facts.FileName;
                _vm.ReadLabel = facts.RoomCount > 0
                    ? facts.RoomCount + " rooms · " + (facts.FloorAreaM2.HasValue ? facts.FloorAreaM2.Value.ToString("0.#") + " m²" : "area unknown")
                    : "no placed rooms";
                var key = DocKey; string label;
                if (key != null && _labelByDoc.TryGetValue(key, out label))
                    _vm.BuildingType = label;
            }
            catch { /* informational; the scan reads again */ }
        }

        private void BuildingType_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _vm.Screen = BombaScreen.Setup;
            _ = EnsureCascadeAsync();
        }

        // ── setup cascade ───────────────────────────────────────────────────

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
            finally { _cascadeLoading = false; }
        }

        private void RefreshMeasuredFacts()
        {
            var doc = LiveDoc;
            if (doc == null) return;
            try { _vm.MeasuredFacts = BombaFactsExtractor.Extract(doc).Label; }
            catch { }
        }

        /// A level whose options are ALL leaves is the band row of the table —
        /// never rendered as a select: measured facts resolve it at Run, and
        /// only a needs_input answer brings it back as an explicit BAND choice.
        private async Task AppendCascadeLevelAsync(string parentPath)
        {
            var resp = await _bombaService.OptionsAsync(DefaultJurisdiction, parentPath);
            if (resp == null) return;
            if (resp.Error == BombaComplianceService.LoginRequiredMessage)
            {
                _vm.Notice = BombaComplianceService.LoginRequiredMessage;
                _vm.Screen = BombaScreen.Home;
                return;
            }
            if (resp.Error != null) { _vm.SetupGuidance = resp.Error; return; }
            if (resp.Options == null || resp.Options.Count == 0) return;
            if (parentPath != null && resp.Options.All(o => o.IsLeaf)) return;

            var level = new CascadeLevelVm();
            level.Label = _vm.Cascade.Count < LevelLabels.Length
                ? LevelLabels[_vm.Cascade.Count]
                : "LEVEL " + (_vm.Cascade.Count + 1);
            foreach (var o in resp.Options) level.Options.Add(o);
            _vm.Cascade.Add(level);
            if (resp.Options.Count == 1)
                level.Selected = level.Options[0];
        }

        private async void CascadeLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var combo = sender as System.Windows.Controls.ComboBox;
            var level = combo != null ? combo.DataContext as CascadeLevelVm : null;
            if (level == null || level.Selected == null) return;

            int index = _vm.Cascade.IndexOf(level);
            while (_vm.Cascade.Count > index + 1)
            {
                if (ReferenceEquals(_vm.Cascade[_vm.Cascade.Count - 1], _bandLevel)) _bandLevel = null;
                _vm.Cascade.RemoveAt(_vm.Cascade.Count - 1);
            }
            _vm.CanRun = true;

            if (!level.Selected.IsLeaf)
            {
                try { await AppendCascadeLevelAsync(level.Selected.Path); }
                catch { }
            }
        }

        private string DeepestSelectedPath()
        {
            var last = _vm.Cascade.LastOrDefault(l => l.Selected != null);
            return last != null ? last.Selected.Path : null;
        }

        private string DeepestSelectedLabel()
        {
            var first = _vm.Cascade.FirstOrDefault(l => l.Selected != null);
            return first != null ? first.Selected.Label : null;
        }

        private void RunSetupScan_Click(object sender, RoutedEventArgs e)
        {
            var path = DeepestSelectedPath();
            if (path == null) return;
            _ = RunScanAsync(path);
        }

        // ── navigation ──────────────────────────────────────────────────────

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            switch (_vm.Screen)
            {
                case BombaScreen.Detail: _vm.Screen = BombaScreen.Summary; break;
                case BombaScreen.Summary: _vm.Screen = BombaScreen.Home; break;
                case BombaScreen.Setup: _vm.Screen = BombaScreen.Home; break;
            }
        }

        private void SummaryRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var border = sender as FrameworkElement;
            var issue = border != null ? border.DataContext as IssueVm : null;
            if (issue == null) return;
            _vm.CurrentIssue = issue;
            _vm.Screen = BombaScreen.Detail;
        }

        private void StartFixing_Click(object sender, RoutedEventArgs e)
        {
            var first = _vm.Issues.FirstOrDefault(i => !i.Done);
            if (first == null) return;
            _vm.CurrentIssue = first;
            _vm.Screen = BombaScreen.Detail;
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            var open = _vm.Issues.Where(i => !i.Done).ToList();
            if (open.Count == 0) return;
            int at = _vm.CurrentIssue != null ? open.IndexOf(_vm.CurrentIssue) : -1;
            _vm.CurrentIssue = open[(at + 1) % open.Count];
        }

        // ── scan ────────────────────────────────────────────────────────────

        private void Rescan_Click(object sender, RoutedEventArgs e)
        {
            var path = RememberedPath ?? DeepestSelectedPath();
            if (path == null)
            {
                // The one human decision is missing — route into the ask.
                _vm.Screen = BombaScreen.Setup;
                _ = EnsureCascadeAsync();
                return;
            }
            _ = RunScanAsync(path);
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
            _vm.Notice = null;
            StartProgress();
            try
            {
                await RunScanInner(doc, schedulePath);
            }
            catch (Exception ex)
            {
                _vm.Screen = BombaScreen.Home;
                _vm.Notice = "Scan error: " + ex.Message;
            }
            finally
            {
                StopProgress();
                _vm.Scanning = false;
            }
        }

        /// The ring is honest about being one request: it advances while the
        /// call is in flight and holds at 95% until the answer lands.
        private void StartProgress()
        {
            _vm.Screen = BombaScreen.Checking;
            _vm.ProgressPct = 0;
            foreach (var r in _vm.CheckRows) { r.Glyph = "·"; r.GlyphInk = M.Faint; r.LabelInk = M.Sub; }
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
            _progressTimer.Tick += (s, e) =>
            {
                var p = Math.Min(95, _vm.ProgressPct + 3);
                _vm.ProgressPct = p;
                _vm.ProgressTitle = p < 35 ? "Reading the model" : p < 75 ? "Resolving requirements" : "Checking fire systems";
                _vm.ProgressSub = p < 35 ? "rooms, areas, heights" : p < 75 ? "row from measured facts" : "querying families…";
                Row(0, p >= 35 ? "done" : "run");
                Row(1, p >= 75 ? "done" : p >= 35 ? "run" : "queue");
                Row(2, p >= 95 ? "run" : p >= 75 ? "run" : "queue");
            };
            _progressTimer.Start();
        }

        private void Row(int i, string state)
        {
            var r = _vm.CheckRows[i];
            r.Glyph = state == "done" ? "✓" : state == "run" ? "◐" : "·";
            r.GlyphInk = state == "done" ? M.Green : state == "run" ? M.Accent : M.Faint;
            r.LabelInk = state == "run" ? M.Ink : M.Sub;
        }

        private void StopProgress()
        {
            if (_progressTimer != null) { _progressTimer.Stop(); _progressTimer = null; }
            _vm.ProgressPct = 100;
        }

        private async Task RunScanInner(Autodesk.Revit.DB.Document doc, string schedulePath)
        {
            var facts = BombaFactsExtractor.Extract(doc);
            var response = await _bombaService.CheckAsync(BuildRequest(facts, schedulePath));
            // A single needs_input option is no choice at all — advance through it.
            for (int hop = 0; hop < 4; hop++)
            {
                if (response == null || response.Error != null || !response.NeedsInput) break;
                if (response.Options == null || response.Options.Count != 1) break;
                schedulePath = response.Options[0].Path;
                response = await _bombaService.CheckAsync(BuildRequest(facts, schedulePath));
            }
            if (response == null) { _vm.Screen = BombaScreen.Home; return; }

            if (response.Error == BombaComplianceService.LoginRequiredMessage)
            {
                _vm.Screen = BombaScreen.Home;
                _vm.Notice = BombaComplianceService.LoginRequiredMessage;
                return;
            }
            if (response.Error != null)
            {
                _vm.Screen = BombaScreen.Home;
                _vm.Notice = "Scan failed: " + response.Error;
                return;
            }
            if (response.NeedsInput)
            {
                // Genuine band boundary — extend the Setup cascade and ask.
                _vm.Screen = BombaScreen.Setup;
                _vm.SetupGuidance = response.Guidance
                    ?? "The measured facts sit on a boundary — choose the band that applies.";
                RefreshMeasuredFacts();
                if (_bandLevel != null) { _vm.Cascade.Remove(_bandLevel); _bandLevel = null; }
                if (response.Options != null && response.Options.Count > 0)
                {
                    var level = new CascadeLevelVm();
                    level.Label = "BAND — the facts sit on a boundary, choose one";
                    foreach (var o in response.Options) level.Options.Add(o);
                    _vm.Cascade.Add(level);
                    _bandLevel = level;
                }
                _vm.CanRun = false;
                return;
            }

            // Success: remember the choice, build the issue list.
            var key = DocKey;
            if (key != null)
            {
                _pathByDoc[key] = schedulePath;
                var label = DeepestSelectedLabel();
                if (label != null) _labelByDoc[key] = label;
            }
            RefreshHome();

            var mapped = BombaMapper.Map(response);
            _vm.Issues.Clear();
            foreach (var i in mapped.Issues) _vm.Issues.Add(i);
            _vm.RaiseCounts();

            if (_vm.Issues.Count == 0)
            {
                _vm.DoneTitle = "All clear";
                _vm.DoneSub = mapped.PassCount + " required systems present.\nChecked against " + mapped.RulesLine + ".";
                _vm.Screen = BombaScreen.Done;
            }
            else
            {
                _vm.Screen = BombaScreen.Summary;
            }
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
            // Phase 1: host model only, no fire-system counting. The backend
            // answers NOT CHECKED for M&E-resident systems — honest, never a
            // false "missing".
            request.SearchedModels = facts.SearchedModels;
            return request;
        }
    }
}
