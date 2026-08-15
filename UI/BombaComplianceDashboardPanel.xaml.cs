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
        // "auto" (room-name read) vs "your pick" (asserted) — provenance for
        // the Home row tag; the distinction must never be silently lost.
        private static readonly Dictionary<string, string> _tagByDoc = new Dictionary<string, string>();
        // Open subjects from the previous scan, per document — feeds the
        // "N fixed since last check" banner. Only ever states what two real
        // scans proved.
        private static readonly Dictionary<string, HashSet<string>> _prevOpenByDoc = new Dictionary<string, HashSet<string>>();
        private bool _pgOptionsLoaded;
        private bool _cascadeLoading;
        private CascadeLevelVm _bandLevel;
        private BombaScreen _needsFrom = BombaScreen.Home;
        // Latest scan's findings by subject — joins presence onto the
        // requirements rows. Empty until a scan has run.
        private Dictionary<string, BombaFindingDto> _lastFindings = new Dictionary<string, BombaFindingDto>();

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

                var key = DocKey; string label; string tag;
                // Extensible Storage first: a pick stored in the model outlives
                // Revit sessions and syncs with the .rvt. Seed the session
                // dictionaries from it so every later lookup agrees.
                if (key != null && !_labelByDoc.ContainsKey(key))
                {
                    var stored = BombaPickStore.Read(doc);
                    if (stored != null)
                    {
                        _pathByDoc[key] = stored.Path;
                        _labelByDoc[key] = stored.Label ?? stored.Path;
                        _tagByDoc[key] = string.IsNullOrEmpty(stored.Tag) ? "your pick" : stored.Tag;
                    }
                }
                if (key != null && _labelByDoc.TryGetValue(key, out label))
                {
                    _vm.BuildingType = label;
                    _vm.PgTag = key != null && _tagByDoc.TryGetValue(key, out tag) ? tag : "your pick";
                    _vm.PgEvidence = "";
                }
                else
                {
                    // No stored answer — show what the room names point to.
                    // A read, not an assertion: the tag says "auto" and the
                    // evidence line shows why.
                    var guess = BombaFactsExtractor.DetectPurposeGroup(doc);
                    if (guess.Bucket != null)
                    {
                        _vm.BuildingType = BucketDisplay(guess.Bucket);
                        _vm.PgTag = "auto";
                        _vm.PgEvidence = "auto-detected from room names — " + guess.Evidence
                            + " · tap the row to change it";
                    }
                    else
                    {
                        _vm.BuildingType = "Not set";
                        _vm.PgTag = "tap to choose";
                        _vm.PgEvidence = guess.Total == 0
                            ? "no placed rooms to read — choose the building type"
                            : "room names point nowhere clearly — choose the building type";
                    }
                }

                RefreshMneRow(doc);
            }
            catch { /* informational; the scan reads again */ }
        }

        /// M&E scope row. Phase 1 never searches links — the row says so, and
        /// an unloaded link gets the amber strip so a NOT CHECKED verdict is
        /// never a surprise.
        private void RefreshMneRow(Autodesk.Revit.DB.Document doc)
        {
            var links = BombaFactsExtractor.ListMneLinks(doc);
            if (links.Count == 0)
            {
                _vm.MeValue = "no M&E link found";
                _vm.MeInk = M.Dim;
                _vm.MeWarn = "Fire systems are modelled in M&E. Without a linked M&E model the check answers NOT CHECKED — never a false “missing”.";
                return;
            }
            var unloaded = links.Where(l => !l.Loaded).ToList();
            _vm.MeValue = string.Join(", ", links.Select(l => l.Name + (l.Loaded ? "" : " — unloaded")).ToArray());
            _vm.MeInk = unloaded.Count > 0 ? M.Amber : M.Sub;
            _vm.MeWarn = unloaded.Count > 0
                ? "An M&E link is unloaded. Checks answer NOT CHECKED, never “missing”, until it’s loaded — load it in Revit and re-check."
                : "Linked, not yet searched — fire-system counting arrives with the next update; until then presence is NOT CHECKED.";
        }

        private static string BucketDisplay(string bucket)
        {
            switch (bucket)
            {
                case "office": return "Office";
                case "assembly": return "Place of assembly";
                case "shop": return "Shop";
                case "hospital": return "Institutional";
                case "school": return "Institutional";
                case "hotel": return "Other residential";
                case "residential": return "Residential";
                default: return bucket;
            }
        }

        /// Keywords that join a detected bucket to a jurisdiction's root
        /// option LABEL. Labels come from the rules service — never hardcode
        /// the option list itself.
        private static readonly Dictionary<string, string[]> BucketKeywords = new Dictionary<string, string[]>
        {
            { "office",      new[] { "office", "pejabat" } },
            { "assembly",    new[] { "assembly", "perhimpunan" } },
            { "shop",        new[] { "shop", "kedai" } },
            { "hospital",    new[] { "institutional", "institusi" } },
            { "school",      new[] { "institutional", "institusi" } },
            { "hotel",       new[] { "other residential", "hotel" } },
            { "residential", new[] { "residential", "kediaman" } },
        };

        private void BuildingType_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _vm.PgOpen = !_vm.PgOpen;
            if (_vm.PgOpen) _ = EnsurePgOptionsAsync();
        }

        /// Root options for the inline select, loaded once per pane from the
        /// rules service. The "detected" badge marks where the room-name read
        /// points; the current pick gets the ✓.
        private async Task EnsurePgOptionsAsync()
        {
            if (_pgOptionsLoaded && _vm.PgOptions.Count > 0) return;
            _vm.PgLoading = true;
            try
            {
                var resp = await _bombaService.OptionsAsync(DefaultJurisdiction, null);
                if (resp == null) return;
                if (resp.Error == BombaComplianceService.LoginRequiredMessage)
                {
                    _vm.Notice = BombaComplianceService.LoginRequiredMessage;
                    _vm.PgOpen = false;
                    return;
                }
                if (resp.Error != null) { _vm.Notice = resp.Error; _vm.PgOpen = false; return; }
                if (resp.Options == null || resp.Options.Count == 0) return;

                string detectedPath = null;
                var doc = LiveDoc;
                var guess = doc != null ? BombaFactsExtractor.DetectPurposeGroup(doc) : null;
                if (guess != null && guess.Bucket != null)
                    detectedPath = MatchOption(resp.Options, guess.Bucket);

                _vm.PgOptions.Clear();
                foreach (var o in resp.Options)
                {
                    _vm.PgOptions.Add(new PgOptionVm
                    {
                        Path = o.Path,
                        Label = o.Label,
                        Detected = o.Path == detectedPath,
                        Current = o.Path == RememberedPath || o.Label == _vm.BuildingType,
                    });
                }
                _pgOptionsLoaded = true;
            }
            catch { /* the row stays; a scan will surface real errors */ }
            finally { _vm.PgLoading = false; }
        }

        private static string MatchOption(IList<BombaOptionDto> options, string bucket)
        {
            string[] keywords;
            if (!BucketKeywords.TryGetValue(bucket, out keywords)) return null;
            var hits = options.Where(o =>
            {
                var low = (o.Label ?? "").ToLowerInvariant();
                return keywords.Any(k => low.Contains(k));
            }).ToList();
            // Exactly one — an ambiguous match is no suggestion at all.
            return hits.Count == 1 ? hits[0].Path : null;
        }

        private void PgOption_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var opt = el != null ? el.DataContext as PgOptionVm : null;
            if (opt == null) return;
            var key = DocKey;
            if (key != null)
            {
                _pathByDoc[key] = opt.Path;
                _labelByDoc[key] = opt.Label;
                _tagByDoc[key] = "your pick";
            }
            _vm.BuildingType = opt.Label;
            _vm.PgTag = "your pick";
            _vm.PgEvidence = "";
            _vm.PgOpen = false;
            PersistPick(opt.Path, opt.Label, "your pick");
            _pgOptionsLoaded = false; // rebuild ✓ marks next open
            e.Handled = true;
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
                case BombaScreen.Needs: _vm.Screen = _needsFrom; break;
            }
        }

        // ── required fire systems ───────────────────────────────────────────

        private void NeedsBtn_Click(object sender, RoutedEventArgs e) { _ = OpenNeedsAsync(); }

        private void Bylaw_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _vm.BylawOpen = !_vm.BylawOpen;
        }
        private void Needs_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { _ = OpenNeedsAsync(); }

        private void ReqRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var row = el != null ? el.DataContext as ReqRowVm : null;
            if (row == null || row.Issue == null) return;
            _vm.CurrentIssue = row.Issue;
            _vm.Screen = BombaScreen.Detail;
        }

        private async Task OpenNeedsAsync()
        {
            var path = RememberedPath ?? DeepestSelectedPath();
            if (path == null)
            {
                // The screen needs a schedule row — open the Home inline
                // select; the requirements load on the next tap.
                _vm.Screen = BombaScreen.Home;
                _vm.PgOpen = true;
                _ = EnsurePgOptionsAsync();
                return;
            }
            _needsFrom = _vm.Screen == BombaScreen.Needs ? _needsFrom : _vm.Screen;
            _vm.Screen = BombaScreen.Needs;
            _vm.NeedsSub = "Loading…";
            _vm.NeedsNote = null;
            _vm.Extinguishing.Clear();
            _vm.Alarm.Clear();

            var doc = LiveDoc;
            var facts = doc != null ? BombaFactsExtractor.Extract(doc) : null;
            var resp = await _bombaService.RequirementsAsync(
                DefaultJurisdiction, path,
                facts != null ? facts.FloorAreaM2 : null,
                facts != null ? facts.HeightMm : null);
            if (resp == null) return;
            if (resp.Error != null)
            {
                _vm.NeedsSub = "";
                _vm.NeedsNote = resp.Error;
                return;
            }
            if (resp.NeedsInput)
            {
                _vm.Screen = BombaScreen.Setup;
                _vm.SetupGuidance = resp.Guidance ?? "Choose the band that applies.";
                return;
            }

            // "Tenth Schedule · Office — general office…" — the schedule word
            // is citation prose from THIS jurisdiction's rules, never hardcoded.
            _vm.NeedsSub = (resp.ScheduleNumber != null ? resp.ScheduleNumber + " Schedule · " : "")
                + (resp.Row != null ? resp.Row.Label : "");
            _vm.NeedsCite = "requirements: UBBL 1984, " + (resp.Row != null ? resp.Row.ClauseRef : "")
                + " · rules " + (resp.RulesVersion ?? "?")
                + (resp.RulesStatus != null && resp.RulesStatus != "VERIFIED" ? " · " + resp.RulesStatus : "")
                + "\npresence: counted in this model";

            foreach (var s in resp.Extinguishing) _vm.Extinguishing.Add(BuildReqRow(s.Name));
            foreach (var s in resp.Alarm) _vm.Alarm.Add(BuildReqRow(s.Name));

            bool anyIssue = _vm.Extinguishing.Concat(_vm.Alarm).Any(r => r.Issue != null);
            _vm.NeedsNote = _lastFindings.Count == 0
                ? "No scan yet — these are the schedule's requirements for this building. Run a check to see what the model actually has."
                : anyIssue ? "Tap a red or amber row to deal with it." : null;
        }

        private ReqRowVm BuildReqRow(string name)
        {
            var row = new ReqRowVm { Name = name };
            BombaFindingDto f;
            if (_lastFindings.Count == 0 || !_lastFindings.TryGetValue(name, out f))
            {
                row.ChipText = "not checked yet";
                row.ChipInk = M.Dim; row.ChipBg = M.Line;
                return row;
            }
            var issue = _vm.Issues.FirstOrDefault(i => !i.Done && i.Subject == name);
            if (f.Passed == true)
            {
                double present;
                var n = f.Metrics != null && f.Metrics.TryGetValue("present", out present) ? (int)present : 0;
                row.ChipText = "✓ " + n + " in model";
                row.ChipInk = M.Green; row.ChipBg = M.GreenTint;
            }
            else if (f.Passed == false)
            {
                row.ChipText = "✗ none found";
                row.ChipInk = M.Red; row.ChipBg = M.RedTint;
                row.Issue = issue;
            }
            else
            {
                row.ChipText = "— can't check yet";
                row.ChipInk = M.Amber; row.ChipBg = M.AmberTint;
                row.Issue = issue;
            }
            return row;
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

        private void Chip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var chip = el != null ? el.DataContext as ChipVm : null;
            if (chip == null) return;
            _vm.FilterCls = chip.Cls;
        }

        /// JKR precedent: pane events run on Revit's UI thread, so ShowElements
        /// + SetElementIds are safe to call directly.
        private void ShowInModel_Click(object sender, RoutedEventArgs e)
        {
            var issue = _vm.CurrentIssue;
            var uiApp = UiAppLive;
            var uiDoc = uiApp != null ? uiApp.ActiveUIDocument : null;
            if (issue == null || uiDoc == null || issue.ElementIds.Count == 0) return;
            try
            {
                var ids = new List<Autodesk.Revit.DB.ElementId>();
                foreach (var raw in issue.ElementIds)
                {
                    var id = Autodesk.Revit.DB.ElemIds.From(raw);
                    if (uiDoc.Document.GetElement(id) != null) ids.Add(id);
                }
                if (ids.Count == 0) return;
                uiDoc.ShowElements(ids);
                uiDoc.Selection.SetElementIds(ids);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] bomba locate failed: " + ex.Message);
            }
        }

        // ── scan ────────────────────────────────────────────────────────────

        private void Rescan_Click(object sender, RoutedEventArgs e)
        {
            var path = RememberedPath ?? DeepestSelectedPath();
            if (path == null)
            {
                _ = AutoResolveAndRunAsync();
                return;
            }
            _ = RunScanAsync(path);
        }

        /// Run pressed with no stored pick: try the room-name read against
        /// the root options. An unambiguous match runs tagged "auto"; anything
        /// less opens the inline select — the pane never guesses.
        private async Task AutoResolveAndRunAsync()
        {
            var doc = LiveDoc;
            if (doc == null)
            {
                TaskDialog.Show("BINA Bomba Compliance", "Buka dokumen Revit dahulu.");
                return;
            }
            var guess = BombaFactsExtractor.DetectPurposeGroup(doc);
            if (guess.Bucket != null)
            {
                var resp = await _bombaService.OptionsAsync(DefaultJurisdiction, null);
                if (resp != null && resp.Error == BombaComplianceService.LoginRequiredMessage)
                {
                    _vm.Notice = BombaComplianceService.LoginRequiredMessage;
                    return;
                }
                var path = resp != null && resp.Error == null && resp.Options != null
                    ? MatchOption(resp.Options, guess.Bucket) : null;
                if (path != null)
                {
                    var key = DocKey;
                    var label = resp.Options.First(o => o.Path == path).Label;
                    if (key != null)
                    {
                        _pathByDoc[key] = path;
                        _labelByDoc[key] = label;
                        _tagByDoc[key] = "auto";
                    }
                    _vm.BuildingType = label;
                    _vm.PgTag = "auto";
                    PersistPick(path, label, "auto");
                    await RunScanAsync(path);
                    return;
                }
            }
            // Nothing detected cleanly — expand the select and say why.
            _vm.Screen = BombaScreen.Home;
            _vm.PgOpen = true;
            _ = EnsurePgOptionsAsync();
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

            // Success: remember the choice, build the issue list. A band-ask
            // label must not overwrite the building-type label the drafter saw.
            var key = DocKey;
            if (key != null)
            {
                _pathByDoc[key] = schedulePath;
                if (!_labelByDoc.ContainsKey(key))
                {
                    var label = DeepestSelectedLabel();
                    if (label != null) _labelByDoc[key] = label;
                }
            }
            RefreshHome();
            _lastFindings = new Dictionary<string, BombaFindingDto>();
            if (response.Findings != null)
                foreach (var f in response.Findings)
                    if (!string.IsNullOrEmpty(f.Subject) && !_lastFindings.ContainsKey(f.Subject))
                        _lastFindings[f.Subject] = f;

            var mapped = BombaMapper.Map(response);
            _vm.Issues.Clear();
            foreach (var i in mapped.Issues) _vm.Issues.Add(i);
            _vm.PassCount = mapped.PassCount;
            _vm.FilterCls = "open";

            // Receipt: what the previous scan flagged that this scan no longer
            // does. Computed from two real scans — never simulated.
            var openNow = new HashSet<string>(mapped.Issues.Select(i => i.Subject).Where(s => s != null));
            HashSet<string> prev;
            if (key != null && _prevOpenByDoc.TryGetValue(key, out prev))
            {
                var fixedN = prev.Count(s => !openNow.Contains(s));
                _vm.FixedSinceLast = fixedN > 0
                    ? fixedN + (fixedN == 1 ? " item" : " items") + " fixed since last check ✓"
                    : "";
            }
            else _vm.FixedSinceLast = "";
            if (key != null) _prevOpenByDoc[key] = openNow;

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

        /// Queue the pick into the model via the ExternalEvent (writes need
        /// API context). Auto must never overwrite a human's assertion: if the
        /// stored tag is "your pick" and this write is "auto", skip it.
        private void PersistPick(string path, string label, string tag)
        {
            try
            {
                if (tag == "auto")
                {
                    var stored = BombaPickStore.Read(LiveDoc);
                    if (stored != null && stored.Tag == "your pick") return;
                }
                if (App.BombaPickHandler == null || App.BombaPickEvent == null) return;
                App.BombaPickHandler.Pending = new BombaPickStore.Pick { Path = path, Label = label, Tag = tag };
                App.BombaPickEvent.Raise();
            }
            catch { /* persistence is best-effort; the session dictionaries still hold the pick */ }
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
            request.Facts.Storeys = facts.Storeys;
            // Rooms (hotel bands: bilik per block) deliberately unsent — the
            // guest-room count is not generically measurable; null means ASK.
            // Phase 1: host model only, no fire-system counting. The backend
            // answers NOT CHECKED for M&E-resident systems — honest, never a
            // false "missing".
            request.SearchedModels = facts.SearchedModels;
            return request;
        }
    }
}
