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
            ["High"] = "#C23B3B", ["Med"] = "#EA580C", ["Low"] = "#6B7079",
        };

        private readonly PanelCore _core;
        private readonly StartScreenModel _start;
        private readonly HashSet<string> _reviewed = new HashSet<string>();
        private string _busy;

        public ResultsScreenModel(PanelCore core, StartScreenModel start)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _start = start ?? throw new ArgumentNullException(nameof(start));
            _core.Changed += RaiseAll;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> OpenRequested;
        public event Action<List<string>> FixRequested;
        public event Action<List<string>> IgnoreRequested;
        /// <summary>Fix all in a section (null = all sections): ready-made fixes plus
        /// whatever the backend AI fixer proposes; the host confirms and writes.</summary>
        public event Action<string> FixAllRequested;
        public event Action<int, string> RescanRequested;
        public event Action ResetRequested;
        public event Action ExportRequested;

        public PanelCore Core => _core;
        private string T(string key) => _core.T(key);

        // ── data ──
        /// <summary>New results or a status change: reload the items, keep the view.</summary>
        public void Load(IEnumerable<IssueVm> issues, Bim010Summary summary)
        {
            var items = (issues ?? Enumerable.Empty<IssueVm>()).Where(i => i != null).Select(ResultsAdapter.FromIssue).ToList();
            foreach (var it in items.Where(x => x.IsHuman && _reviewed.Contains(x.Id))) it.Status = ItemStatus.Reviewed;
            _core.Replace(items, ResultsAdapter.BasePass(summary, items));
        }

        /// <summary>prototype onReview (kept across reloads; persisted in step 12).</summary>
        public void ToggleReviewed(string id)
        {
            if (!_reviewed.Remove(id)) _reviewed.Add(id);
            _core.ToggleReviewed(id);
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
        public string HeadPctLabel => _core.HeadPct.HasValue ? _core.HeadPct.Value + "%" : "—";
        public string CompliantLabel => T("compliant");
        public string HeadCellsLabel => _core.AiPass + " / " + _core.AiTotal + " " + T("checksAI");
        public int PendTotal => _core.PendTotal;
        public string NeedPersonLabel => T("needPerson");

        public IReadOnlyList<SectionTile> Sections
        {
            get
            {
                var st = _core.Stats();
                return PanelCore.Secs.Select(s => new SectionTile
                {
                    Key = s,
                    PctLabel = st[s].Pct.HasValue ? st[s].Pct.Value + "%" : "—",
                    Fraction = (st[s].Pct ?? 0) / 100.0,
                    PendLabel = st[s].Pend > 0 ? "+" + st[s].Pend : "",
                    IsOn = _core.Cat == s,
                    Aria = s + " · " + SectionName(s) + " · " + (st[s].Pct.HasValue ? st[s].Pct + "%" : "—")
                           + (st[s].Pend > 0 ? " · " + st[s].Pend + " " + T("manualShort") : ""),
                }).ToList();
            }
        }

        // ── progress ──
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
            _core.Issues.Where(x => !x.Passed && !x.IsHuman && x.AiFix && x.Status == ItemStatus.Open
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
                }.Select(t => new TabChip { Tab = t.Item1, Label = T(t.Item2), N = n[t.Item1], IsOn = _core.Tab == t.Item1 }).ToList();
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
            else if (x.Status == ItemStatus.Resolved) { sv = T("fixedRow"); kind = "ok"; }
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
                ShowFix = !man && x.Fix != FixKind.None && x.Status == ItemStatus.Open,
                FixError = x.FixError ?? "",
                Index = i,
            };
        }

        public bool IsManualTab => _core.Tab == PanelTab.Manual;
        public string ManualIntro => T("manualIntro");
        public bool ListEmpty => _core.Filtered().Count == 0;
        public string EmptyTitle => T("empty");
        public string EmptyHint => T(_core.EmptyHintKey);
        public bool HasQ => (_core.Q ?? "").Trim().Length > 0;
        public string ClearSearchLabel => T("clearSearch");

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
        public void SelectTab(PanelTab tab) => _core.SetTab(tab);
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
            var i = _core.Filtered().FindIndex(x => x.Id == id);
            if (i >= 0) _core.SetSel(i);
            FixRequested?.Invoke(new List<string> { id });
        }

        /// <summary>Ignore one open AI item (the detail's Ignore / A).</summary>
        public void IgnoreRow(string id)
        {
            if (!CanAct) return;
            var x = _core.Issues.FirstOrDefault(i => i.Id == id);
            if (x == null || x.IsHuman || x.Status != ItemStatus.Open) return;
            IgnoreRequested?.Invoke(new List<string> { id });
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
            var ids = _core.Issues.Where(x => !x.Passed && !x.IsHuman && x.Status == ItemStatus.Open
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
                    if (CanAct && cur.Fix != FixKind.None && cur.Status == ItemStatus.Open && !cur.IsHuman)
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
