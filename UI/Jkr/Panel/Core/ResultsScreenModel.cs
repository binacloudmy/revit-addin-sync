using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>A Borang section tile (prototype .sec): key, %, "+n" manual, bar.</summary>
    public sealed class SectionTile
    {
        public string Key { get; set; } = "";
        public string PctLabel { get; set; } = "—";
        /// <summary>Bar fill 0..1.</summary>
        public double Fraction { get; set; }
        /// <summary>"+n" items that need a person, or "" when none.</summary>
        public string PendLabel { get; set; } = "";
        public bool IsOn { get; set; }
        public string Aria { get; set; } = "";
    }

    public sealed class TabChip
    {
        public PanelTab Tab { get; set; }
        public string Label { get; set; } = "";
        public int N { get; set; }
        public bool IsOn { get; set; }
    }

    public sealed class SevChip
    {
        /// <summary>"High" | "Med" | "Low".</summary>
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int N { get; set; }
        /// <summary>Dot colour (prototype SEV).</summary>
        public string Color { get; set; } = "";
    }

    /// <summary>One list row (prototype mkItem).</summary>
    public sealed class ResultRow
    {
        public string Id { get; set; } = "";
        public string Row { get; set; } = "";
        public string Title { get; set; } = "";
        public string Diff { get; set; } = "";
        public bool HasDiff { get; set; }
        public bool IsManual { get; set; }
        public string Reason { get; set; } = "";
        public string SvLabel { get; set; } = "";
        /// <summary>"High" | "Med" | "Low" | "man" | "ok" | "ign" (prototype sv-*).</summary>
        public string SvKind { get; set; } = "";
        public string Meta { get; set; } = "";
        public bool IsSel { get; set; }
        /// <summary>Handled (ignored / fixed): title in the secondary colour.</summary>
        public bool IsDone { get; set; }
        public bool ShowFix { get; set; }
        /// <summary>"Manual · you'll fix it in Revit" on a marked item, else empty.</summary>
        public string ManualTag { get; set; } = "";
        public string FixError { get; set; } = "";
        public bool HasFixError => !string.IsNullOrEmpty(FixError);
        public int Index { get; set; }
    }

    /// <summary>
    /// The Results screen (prototype isResults; frames t-results, t-section,
    /// t-search, t-manual, t-empty, t-stale). Reads the step 6 core; actions that
    /// touch the model or the audit file (open, fix, ignore, re-scan, reset,
    /// export) are raised as events for the host, which owns those writes, and the
    /// host then reloads the items with <see cref="Load"/>. WPF-free.
    /// </summary>
    public sealed class ResultsScreenModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, string> SevColor = new Dictionary<string, string>
        {
            // Cp.Jkr.Dark.FailDot / WarnDot / ListedDot (UI/Copilot/CopilotTokens.xaml):
            // the pane is always dark (Direction C).
            ["High"] = "#ff6b5e", ["Med"] = "#f5b942", ["Low"] = "#8a94a6",
        };

        private readonly PanelCore _core;
        private readonly StartScreenModel _start;
        private Bim010Summary _summary;
        private string _busy;

        // The run measured against the JKR Model Audit spec (61 checks), shown on
        // the Checklist tab. Null in runs without one (tests that load items only).
        private SpecReport _spec;
        // Checklist tab (the spec view) vs the issue tabs. The Checklist is shown first:
        // it answers "does the model comply?"; the issue tabs are the fix queue.
        private bool _checklist = true;
        private readonly Dictionary<string, bool> _sectionOpen = new Dictionary<string, bool>();
        private readonly HashSet<string> _showAll = new HashSet<string>();
        // Checks whose spec criterion ("Why?") the user opened or closed; the default is open when it fails.
        private readonly Dictionary<string, bool> _why = new Dictionary<string, bool>();
        // The Checklist's state filter (a tone, or SpecChecklist.AllStates). Kept across reloads.
        private string _stateFilter = SpecChecklist.AllStates;
        // The loaded issues by id, and the language their load-time text is in: a BM / EN
        // switch puts that text (element words, fix-failure reasons) in the new language.
        private Dictionary<string, IssueVm> _issues = new Dictionary<string, IssueVm>(StringComparer.Ordinal);
        private string _itemsLang;

        public ResultsScreenModel(PanelCore core, StartScreenModel start)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _start = start ?? throw new ArgumentNullException(nameof(start));
            _core.Changed += OnCoreChanged;
        }

        private void OnCoreChanged()
        {
            if (_itemsLang != null && _itemsLang != _core.Lang)
            {
                _itemsLang = _core.Lang;
                ResultsAdapter.Relabel(_core.Issues, _issues, _core.Lang);
            }
            RaiseAll();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> OpenRequested;
        public event Action<List<string>> FixRequested;
        public event Action<List<string>> IgnoreRequested;
        /// <summary>Mark one open AI item as handled by hand (stays open, still a fail).</summary>
        public event Action<string> MarkManualRequested;
        /// <summary>Put an ignored or marked item back to Open.</summary>
        public event Action<string> ReopenRequested;
        /// <summary>Fix the issues behind one spec check: ready-made fixes plus whatever
        /// the backend AI fixer proposes; the host confirms and writes.</summary>
        public event Action<List<string>> FixCheckRequested;
        /// <summary>Fix all in a section (null = all sections): ready-made fixes plus
        /// whatever the backend AI fixer proposes; the host confirms and writes.</summary>
        public event Action<string> FixAllRequested;
        public event Action<int, string> RescanRequested;
        public event Action ResetRequested;
        public event Action ExportRequested;
        /// <summary>Mark as reviewed (true) or back (false): the host saves it.</summary>
        public event Action<string, bool> ReviewRequested;

        public PanelCore Core => _core;
        private string T(string key) => _core.T(key);

        // ── data ──
        /// <summary>New results or a status change: reload the items, keep the view.</summary>
        public void Load(IEnumerable<IssueVm> issues, Bim010Summary summary, SpecReport spec = null)
        {
            _summary = summary;
            if (!ReferenceEquals(_spec, spec)) { _sectionOpen.Clear(); _showAll.Clear(); _why.Clear(); }
            _spec = spec;
            // The run's BM / EN remarks, so the spec evaluators' issues follow the language switch.
            var notes = spec != null ? ResultsAdapter.NoteTexts(spec) : null;
            var list = (issues ?? Enumerable.Empty<IssueVm>()).Where(i => i != null).ToList();
            var byId = new Dictionary<string, IssueVm>(StringComparer.Ordinal);
            foreach (var i in list) if (!string.IsNullOrEmpty(i.Id) && !byId.ContainsKey(i.Id)) byId[i.Id] = i;
            _issues = byId;
            _itemsLang = _core.Lang;
            var items = list.Select(i => ResultsAdapter.FromIssue(i, _core.Lang, notes)).ToList();
            _core.Replace(items, ResultsAdapter.BasePass(summary, items));
        }

        /// <summary>prototype onReview: Mark as reviewed ↔ back. Shown at once; the host
        /// saves it to the audit file and reloads (IssueVm.Reviewed).</summary>
        public void ToggleReviewed(string id)
        {
            var x = _core.Issues.FirstOrDefault(i => i.Id == id);
            if (x == null || !x.IsHuman) return;
            var reviewed = x.Status != ItemStatus.Reviewed;
            _core.ToggleReviewed(id);                 // shows at once; the host saves and reloads
            ReviewRequested?.Invoke(id, reviewed);
        }

        /// <summary>A fix batch is running ("Applying n changes…"): the write actions
        /// are disabled until it ends. Null when idle.</summary>
        public string BusyText
        {
            get => _busy;
            set { if (_busy == value) return; _busy = value; RaiseAll(); }
        }
        public bool IsBusy => !string.IsNullOrEmpty(_busy);
        public bool CanAct => !IsBusy;

        // ── tools bar ──
        // Same option objects as the Start screen (relabelled on BM / EN), created once:
        // a new list on every refresh made a dropdown lose its selection.
        private IReadOnlyList<StartOption> _lodOptions;
        public IReadOnlyList<StartOption> LodOptions => _lodOptions ?? (_lodOptions = _start.LodOptions.Skip(1).ToList());
        public IReadOnlyList<StartOption> DiscOptions => _start.DiscOptions;
        public string LodValue { get => _start.LodValue; set { if (!string.IsNullOrEmpty(value)) _start.LodValue = value; } }
        public string DiscValue { get => _start.DiscValue; set => _start.DiscValue = value; }
        public string DiscShortLabel => T("discShort");
        public string RescanLabel => T("rescan");
        public bool LodChanged => _core.LodChanged;
        public string LodChangedText => T("lodChanged");

        // ── headline ──
        // Two numbers (DESIGN-BRIEF R2). With a spec report they are the spec's, the
        // same numbers the verdict and the Checklist show: % = AI-verifiable spec
        // results passed / (passed + failed), per result ("Kiraan %": human-decides
        // cells never in it), and the checks a person decides. Without one (tests,
        // runs from before the spec), the run's BIM010 cells, the same as the Running
        // screen and the Borang; without a summary either, the items stand in for
        // cells, as in the prototype.
        private int? HeadPct => _spec != null ? SpecChecklist.Pct(_spec) : _summary != null ? _summary.Pct : _core.HeadPct;
        private int AiPass => _summary != null ? _summary.Pass : _core.AiPass;
        private int AiTotal => _summary != null ? _summary.AiTotal : _core.AiTotal;
        private int? SectionPct(string s, Dictionary<string, SectionStats> st) =>
            _spec != null ? SpecChecklist.Pct(_spec, s) : _summary != null ? _summary.Section(s)?.Pct : st[s].Pct;
        private int SectionPend(string s, Dictionary<string, SectionStats> st) =>
            _spec != null ? SpecChecklist.NeedsPerson(_spec, s) : st[s].Pend;

        public string HeadPctLabel => HeadPct.HasValue ? HeadPct.Value + "%" : "—";
        public string CompliantLabel => T("compliant");
        public string HeadCellsLabel => _spec != null ? SpecChecklist.ResultsLabel(_spec, _core.Lang) : AiPass + " / " + AiTotal + " " + T("checksAI");
        /// <summary>Spec checks a person decides (with a spec report), else BIM010 cells pending a person.</summary>
        public int PendTotal => _spec != null ? SpecChecklist.NeedsPerson(_spec) : _core.PendTotal;
        public string NeedPersonLabel => _spec != null ? SpecChecklist.NeedPersonLabel(_spec, _core.Lang) : T("needPerson");

        public IReadOnlyList<SectionTile> Sections
        {
            get
            {
                var st = _core.Stats();
                return PanelCore.Secs.Select(s =>
                {
                    var pct = SectionPct(s, st);
                    var pend = SectionPend(s, st);
                    return new SectionTile
                    {
                        Key = s,
                        PctLabel = pct.HasValue ? pct.Value + "%" : "—",
                        Fraction = (pct ?? 0) / 100.0,
                        PendLabel = pend > 0 ? "+" + pend : "",
                        IsOn = _core.Cat == s,
                        Aria = s + " · " + SectionName(s) + " · " + (pct.HasValue ? pct + "%" : "—")
                               + (pend > 0 ? " · " + pend + " " + T("manualShort") : ""),
                    };
                }).ToList();
            }
        }

        // ── progress (shown with the issue tabs: it counts issues, not spec checks) ──
        public bool ShowTriage => ShowList;
        public int ResolvedN => _core.ResolvedN;
        public string ProgressRest => " " + T("of") + " " + _core.FixTotal + " " + T("resolved") + " · " + _core.ToGo + " " + T("toGo");
        public double ProgFraction => _core.ProgN / 100.0;
        public IReadOnlyList<SevChip> SevChips
        {
            get
            {
                var n = _core.SevCounts();
                return new[] { "High", "Med", "Low" }.Select(k => new SevChip
                {
                    Key = k, Label = PanelStrings.Severity[k].In(_core.Lang), N = n[k], Color = SevColor[k],
                }).ToList();
            }
        }

        // ── actions ──
        public string IgnoreAllLabel => T("ignoreAll");
        /// <summary>Fix all (n): open items in the section with a ready-made fix, plus
        /// those the AI fixer will be asked about (it may not find a fix for each).</summary>
        public int FixableN => _core.FixableOpen().Count + AiCandidates().Count();

        private IEnumerable<PanelItem> AiCandidates() =>
            _core.Issues.Where(x => !x.Passed && !x.IsHuman && x.AiFix && x.Status == ItemStatus.Open && !x.MarkedManual
                                    && (_core.Cat == PanelCore.AllSections || x.Sec == _core.Cat));
        public string FixAllLabel => T("fixAll") + " (" + FixableN + ")";
        public bool CanFixAll => FixableN > 0 && CanAct;

        // ── list toolbar ──
        public string Query
        {
            get => _core.Q;
            set => _core.SetQuery(value ?? "");
        }
        public string SearchPlaceholder => T("search");
        public bool HasCat => _core.Cat != PanelCore.AllSections;
        public string CatLabel => HasCat ? T("secShort") + " " + _core.Cat + " · " + SectionName(_core.Cat) : "";
        public string ClearCatLabel => T("clearF");

        public IReadOnlyList<TabChip> Tabs
        {
            get
            {
                var n = _core.TabCounts();
                return new[]
                {
                    (PanelTab.Open, "tOpen"), (PanelTab.Ignore, "tIgnore"), (PanelTab.Resolved, "tResolved"), (PanelTab.Manual, "tManual"),
                }.Select(t => new TabChip { Tab = t.Item1, Label = T(t.Item2), N = n[t.Item1], IsOn = !ShowChecklist && _core.Tab == t.Item1 }).ToList();
            }
        }

        public string ShownLabel => _core.Filtered().Count + " " + T("shown");

        // ── list ──
        public IReadOnlyList<ResultRow> Rows
        {
            get
            {
                var list = _core.Filtered();
                var sel = Math.Min(_core.Sel, Math.Max(0, list.Count - 1));
                return list.Select((x, i) => MakeRow(x, i, i == sel)).ToList();
            }
        }

        private ResultRow MakeRow(PanelItem x, int i, bool isSel)
        {
            var man = x.IsHuman;
            var a = x.Act.In(_core.Lang);
            string sv, kind;
            if (man) { sv = T("manualRow"); kind = "man"; }
            else if (x.Status == ItemStatus.Resolved) { sv = T("resolvedRow"); kind = "ok"; }
            else if (x.Status == ItemStatus.Ignored) { sv = T("ignoredRow"); kind = "ign"; }
            else { sv = PanelStrings.Severity[x.Sev].In(_core.Lang); kind = x.Sev; }
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(x.Sec)) meta.Add(SectionName(x.Sec));
            if (!string.IsNullOrEmpty(x.El) && x.El != "—") meta.Add(x.El);
            if (!string.IsNullOrEmpty(x.Cite) && x.Cite != "—") meta.Add(x.Cite);
            return new ResultRow
            {
                Id = x.Id,
                Row = x.Row,
                Title = x.Title.In(_core.Lang),
                Diff = x.Prop != null ? a + " → " + x.Prop : (man ? a : a + " (" + T("needsAction") + ")"),
                HasDiff = !(man && (string.IsNullOrEmpty(a) || a == "—")),
                IsManual = man,
                Reason = man ? x.Reason?.In(_core.Lang) ?? "" : "",
                SvLabel = sv,
                SvKind = kind,
                Meta = (man && x.Status == ItemStatus.Reviewed ? T("reviewed") + " · " : "") + string.Join(" · ", meta),
                IsSel = isSel,
                IsDone = x.Status != ItemStatus.Open && !man,
                ShowFix = !man && x.Fix != FixKind.None && x.Status == ItemStatus.Open && !x.MarkedManual,
                ManualTag = x.MarkedManual ? T("manualTag") : "",
                FixError = x.FixError ?? "",
                Index = i,
            };
        }

        public bool IsManualTab => ShowList && _core.Tab == PanelTab.Manual;
        public string ManualIntro => T("manualIntro");
        public bool ListEmpty => ShowList && _core.Filtered().Count == 0;
        public string EmptyTitle => T("empty");
        public string EmptyHint => T(_core.EmptyHintKey);
        public bool HasQ => (_core.Q ?? "").Trim().Length > 0;
        public string ClearSearchLabel => T("clearSearch");

        // ── spec verdict + Checklist ──
        public bool HasSpec => _spec != null;
        /// <summary>The Checklist tab is shown (only when the run has a spec report).</summary>
        public bool ShowChecklist => _checklist && _spec != null;
        public bool ShowList => !ShowChecklist;
        public string ChecklistTabLabel => SpecChecklist.Str("tab", _core.Lang);
        public string ChecklistIntro => SpecChecklist.Str("intro", _core.Lang);
        /// <summary>"Does not comply — 5 spec checks fail" / "No failures in the checks covered".</summary>
        public string VerdictTitle => SpecChecklist.VerdictTitle(_spec, _core.Lang);
        /// <summary>fail | pass | none.</summary>
        public string VerdictTone => SpecChecklist.VerdictTone(_spec);
        public string VerdictText => SpecChecklist.VerdictText(_spec, _core.Lang);
        public SpecReport Spec => _spec;

        /// <summary>Section → Borang item → spec check, filtered by the section tile and the state chip.</summary>
        public IReadOnlyList<ChecklistSection> Checklist =>
            SpecChecklist.Build(_spec, _core.Issues, _core.Lang, _core.Cat,
                key => _sectionOpen.TryGetValue(key, out var o) ? o : (bool?)null,
                id => _showAll.Contains(id),
                _stateFilter,
                id => _why.TryGetValue(id, out var w) ? w : (bool?)null);

        /// <summary>The legend: every state with its count of spec checks; click filters the Checklist.</summary>
        public IReadOnlyList<StateChip> StateChips => SpecChecklist.StateChips(_spec, _core.Lang, _stateFilter);
        public string StateFilter => _stateFilter;
        public bool IsStateFiltered => _stateFilter != SpecChecklist.AllStates;

        /// <summary>Show only the checks in one state (a tone). "all", or the state already
        /// on, shows every check. Sections re-open by the default for the new filter.</summary>
        public void SetStateFilter(string key)
        {
            if (_spec == null) return;
            var next = string.IsNullOrEmpty(key) || key == _stateFilter ? SpecChecklist.AllStates : key;
            if (next != SpecChecklist.AllStates && !SpecChecklist.StateKeys.Contains(next)) return;
            _stateFilter = next;
            _sectionOpen.Clear();
            _checklist = true;
            RaiseAll();
        }

        /// <summary>The filter leaves no check (in the section shown).</summary>
        public bool ChecklistEmpty => ShowChecklist && IsStateFiltered && Checklist.Count == 0;
        public string ChecklistEmptyText => SpecChecklist.Str("filterEmpty", _core.Lang);
        /// <summary>"4 other JKR rules (not in the spec) also fail — see below."</summary>
        public string OthersNote => SpecChecklist.OthersNote(_spec, _core.Lang);
        public bool HasOthersNote => ShowChecklist && !IsStateFiltered && !string.IsNullOrEmpty(OthersNote);

        /// <summary>The backend check failed: checks that depend on it are not checked (banner).</summary>
        public bool ShowBackendBanner => _spec != null && _spec.BackendUnavailable;
        public string BackendBannerText => SpecChecklist.BackendBanner(_core.Lang);
        public string BackendErrorText => _spec?.BackendError ?? "";

        /// <summary>Show or hide a check's spec criterion and what was tested.</summary>
        public void ToggleWhy(string checkId)
        {
            var r = _spec?.For(checkId);
            if (r == null) return;
            var open = _why.TryGetValue(checkId, out var w) ? w : r.State == SpecState.Fail;
            _why[checkId] = !open;
            RaiseAll();
        }

        /// <summary>"n need a person": with a spec report, the Checklist filtered to those
        /// checks (the number counts them); without one, the Manual tab.</summary>
        public void GoNeedsPerson()
        {
            if (_spec == null) { GoManual(); return; }
            _stateFilter = "person";
            _sectionOpen.Clear();
            _checklist = true;
            RaiseAll();
        }

        public void ShowChecklistTab()
        {
            if (_spec == null) return;
            _checklist = true;
            RaiseAll();
        }

        /// <summary>Open or close a Checklist section.</summary>
        public void ToggleSection(string key)
        {
            var current = Checklist.FirstOrDefault(c => c.Key == key)?.IsOpen ?? false;
            _sectionOpen[key] = !current;
            RaiseAll();
        }

        /// <summary>Show every element under a check, or the first few again.</summary>
        public void ToggleShowAll(string checkId)
        {
            if (!_showAll.Remove(checkId)) _showAll.Add(checkId);
            RaiseAll();
        }

        /// <summary>Open an element's issue from the Checklist. The issue tab behind
        /// the detail follows the issue's state, so its paging includes it.</summary>
        public void OpenElement(string id)
        {
            var x = _core.Issues.FirstOrDefault(i => i.Id == id);
            if (x == null) return;
            var tab = x.IsHuman ? PanelTab.Manual
                    : x.Status == ItemStatus.Ignored ? PanelTab.Ignore
                    : x.Status == ItemStatus.Resolved ? PanelTab.Resolved
                    : PanelTab.Open;
            if (HasQ) _core.ClearQuery();
            if (_core.Tab != tab) _core.SetTab(tab);
            OpenRequested?.Invoke(id);
        }

        /// <summary>Fix the open issues behind a spec check (its "Fix n").</summary>
        public void FixCheck(string checkId)
        {
            if (!CanAct) return;
            // Built for the check itself, so a closed section or a state filter does not hide it.
            var spec = _spec?.For(checkId);
            var row = spec != null
                ? SpecChecklist.CheckRow(spec, _spec, ItemsById(), _core.Lang, id => true)
                : Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).FirstOrDefault(c => c.Id == checkId);
            if (row == null || !row.CanFix) return;
            FixCheckRequested?.Invoke(row.FixIds.ToList());
        }

        private Dictionary<string, PanelItem> ItemsById()
        {
            var d = new Dictionary<string, PanelItem>();
            foreach (var i in _core.Issues) if (!string.IsNullOrEmpty(i?.Id) && !d.ContainsKey(i.Id)) d[i.Id] = i;
            return d;
        }

        // ── footer ──
        public string KeyOpen => T("kOpen");
        public string KeyNav => T("kNav");
        public string KeyFix => T("kFix");
        public string KeyIgnore => T("kIgnore");
        public string ResetLabel => T("reset");
        public string ExportLabel => T("export");

        private string SectionName(string s) =>
            Bim010Form.SectionNames.TryGetValue(s, out var n) ? (_core.Lang == PanelStrings.En ? n.En : n.Ms) : s;

        // ── commands ──
        public void SelectSection(string key) => _core.SetCat(key);
        public void ClearCat() => _core.ClearCat();
        public void SelectTab(PanelTab tab)
        {
            _checklist = false;
            _core.SetTab(tab);
            RaiseAll();
        }
        public void ClearQuery() => _core.ClearQuery();
        public void GoManual() => _core.GoManual();

        public void OpenRow(string id)
        {
            var i = _core.Filtered().FindIndex(x => x.Id == id);
            if (i >= 0) _core.SetSel(i);
            OpenRequested?.Invoke(id);
        }

        public void FixRow(string id)
        {
            if (!CanAct) return;
            if (_core.Issues.Any(x => x.Id == id && x.MarkedManual)) return;
            var i = _core.Filtered().FindIndex(x => x.Id == id);
            if (i >= 0) _core.SetSel(i);
            FixRequested?.Invoke(new List<string> { id });
        }

        /// <summary>Ignore one open AI item (the detail's Ignore / A).</summary>
        public void IgnoreRow(string id)
        {
            if (!CanAct) return;
            var x = _core.Issues.FirstOrDefault(i => i.Id == id);
            if (x == null || x.IsHuman || x.Status != ItemStatus.Open || x.MarkedManual) return;
            IgnoreRequested?.Invoke(new List<string> { id });
        }

        /// <summary>Mark one open AI item as handled manually (the detail's Mark as Manual).</summary>
        public void MarkManual(string id)
        {
            if (!CanAct) return;
            var x = _core.Issues.FirstOrDefault(i => i.Id == id);
            if (x == null || x.IsHuman || x.Passed || x.Status != ItemStatus.Open || x.MarkedManual || !x.CanMarkManual) return;
            MarkManualRequested?.Invoke(id);
        }

        /// <summary>Reopen an ignored or manually marked item.</summary>
        public void Reopen(string id)
        {
            if (!CanAct) return;
            var x = _core.Issues.FirstOrDefault(i => i.Id == id);
            if (x == null || x.IsHuman || !(x.MarkedManual || x.Status == ItemStatus.Ignored)) return;
            ReopenRequested?.Invoke(id);
        }

        /// <summary>Fix all (n) in the current section (see <see cref="FixableN"/>).</summary>
        public void FixAll()
        {
            if (!CanAct || FixableN == 0) return;
            FixAllRequested?.Invoke(_core.Cat == PanelCore.AllSections ? null : _core.Cat);
        }

        /// <summary>Ignore all: open AI items in the current section.</summary>
        public void IgnoreAll()
        {
            if (!CanAct) return;
            var ids = _core.Issues.Where(x => !x.Passed && !x.IsHuman && x.Status == ItemStatus.Open && !x.MarkedManual
                                              && (_core.Cat == PanelCore.AllSections || x.Sec == _core.Cat))
                                  .Select(x => x.Id).ToList();
            if (ids.Count > 0) IgnoreRequested?.Invoke(ids);
        }

        public void Rescan()
        {
            if (!CanAct || !int.TryParse(_core.Lod, out var lod)) return;
            RescanRequested?.Invoke(lod, _core.Disc);
        }

        public void Reset() { if (CanAct) ResetRequested?.Invoke(); }
        public void Export() { if (CanAct) ExportRequested?.Invoke(); }

        /// <summary>prototype handleKey on the results list. Navigation goes to the
        /// core; Enter / F / A become open / fix / ignore requests for the host.</summary>
        public KeyOutcome HandleKey(PanelKey key, bool typing)
        {
            if (key == PanelKey.Escape || typing || _core.Stage != PanelStage.Results)
                return _core.HandleKey(key, typing);
            if (_core.DetailId != null) return KeyOutcome.None;   // the detail window handles its keys
            // The Checklist has no selection: the list keys act on the issue tabs only.
            if (ShowChecklist && key != PanelKey.Slash) return KeyOutcome.None;
            var list = _core.Filtered();
            if (key == PanelKey.Slash) return KeyOutcome.FocusSearch;
            if (list.Count == 0) return KeyOutcome.None;
            var cur = list[Math.Min(_core.Sel, list.Count - 1)];
            switch (key)
            {
                case PanelKey.Enter:
                    OpenRequested?.Invoke(cur.Id);
                    return KeyOutcome.Handled;
                case PanelKey.F:
                    if (CanAct && cur.Fix != FixKind.None && cur.Status == ItemStatus.Open && !cur.IsHuman && !cur.MarkedManual)
                        FixRequested?.Invoke(new List<string> { cur.Id });
                    return KeyOutcome.Handled;
                case PanelKey.A:
                    if (CanAct && !cur.IsHuman && cur.Status == ItemStatus.Open)
                        IgnoreRequested?.Invoke(new List<string> { cur.Id });
                    return KeyOutcome.Handled;
                default:
                    return _core.HandleKey(key, typing);
            }
        }

        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
