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
        /// <summary>The legend's words: "4 high" / "4 tinggi".</summary>
        public string Text { get; set; } = "";
    }

    /// <summary>One list row (prototype mkItem).</summary>
    public sealed class ResultRow
    {
        public string Id { get; set; } = "";
        public string Row { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>A ready-made fix: the value in the model and the one it writes (old → new).</summary>
        public bool HasChange { get; set; }
        public string OldValue { get; set; } = "";
        public string NewValue { get; set; } = "";
        /// <summary>No ready-made fix: "No ready-made fix. Needs manual action." (or what the
        /// AI fixer can do); "" with a change or on a semak manual item.</summary>
        public string NoFixText { get; set; } = "";
        public bool HasNoFixText => !string.IsNullOrEmpty(NoFixText);
        /// <summary>"High" | "Med" | "Low": the pip beside the row.</summary>
        public string Sev { get; set; } = "";
        /// <summary>An open AI fixer item: the spark-bolt mark and <see cref="AiMarkLabel"/> ending the meta.</summary>
        public bool IsAiFixer { get; set; }
        /// <summary>"AI fixer proposal" only when the row has a proposed value; else
        /// "AI fixer can propose" (no proposal is claimed before one exists). "" when not AI.</summary>
        public string AiMarkLabel { get; set; } = "";
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
        public bool HasManualTag => !string.IsNullOrEmpty(ManualTag);
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
        /// <summary>"0 of 99 resolved".</summary>
        public string ProgressLabel => string.Format(S("progResolved"), _core.ResolvedN, _core.FixTotal);
        public double ProgFraction => _core.ProgN / 100.0;
        public IReadOnlyList<SevChip> SevChips
        {
            get
            {
                var n = _core.SevCounts();
                return new[] { "High", "Med", "Low" }.Select(k => new SevChip
                {
                    Key = k, Label = PanelStrings.Severity[k].In(_core.Lang), N = n[k], Color = SevColor[k],
                    Text = n[k] + " " + PanelStrings.Severity[k].In(_core.Lang).ToLowerInvariant(),
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
        public string FixAllLabel => string.Format(S("fixAllN"), FixableN);
        public bool CanFixAll => FixableN > 0 && CanAct;

        // ── list toolbar ──
        public string Query
        {
            get => _core.Q;
            set => _core.SetQuery(value ?? "");
        }
        public string SearchPlaceholder => S("searchIssues");
        public string StateTabsAria => S("stateTabs");
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
            // An open AI fixer item. Without a proposed value (today's only case: the fixer proposes
            // during Fix all) the row says the fixer can propose one and claims none.
            var ai = !man && x.AiFix && x.Status == ItemStatus.Open && !x.MarkedManual;
            var aiMark = !ai ? "" : x.Prop != null ? S("aiProposal") : S("aiCanMeta");
            var hasAct = !string.IsNullOrEmpty(a) && a != "—";
            // Clear Sheet's meta line: severity (or state) · element · section, then the cited
            // document. The section is left out when the element already names it ("Project
            // Information"), and "Borang A5" because the key leads the title.
            var meta = new List<string> { sv };
            var el = !string.IsNullOrEmpty(x.El) && x.El != "—" ? x.El : "";
            if (el.Length > 0) meta.Add(el);
            var sec = string.IsNullOrEmpty(x.Sec) ? "" : SectionName(x.Sec);
            if (sec.Length > 0 && !string.Equals(sec, el, StringComparison.OrdinalIgnoreCase)) meta.Add(sec);
            if (!string.IsNullOrEmpty(x.Cite) && x.Cite != "—")
                meta.AddRange(x.Cite.Split(new[] { " · " }, StringSplitOptions.RemoveEmptyEntries).Where(c => c != "Borang " + x.Row));
            if (ai) meta.Add(aiMark);
            var change = !man && x.Prop != null;
            return new ResultRow
            {
                Id = x.Id,
                Row = x.Row,
                Title = x.Title.In(_core.Lang),
                HasChange = change,
                OldValue = change ? (hasAct ? a : "—") : "",
                NewValue = change ? x.Prop : "",
                NoFixText = man || change ? ""
                          : ai ? (hasAct ? string.Format(S("aiCanReads"), a) : S("aiCan"))
                          : hasAct ? string.Format(S("noFixReads"), a) : S("noFix"),
                Sev = x.Sev,
                IsAiFixer = ai,
                AiMarkLabel = aiMark,
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

        /// <summary>Section → Borang item → spec check, for what is open: a section (the section
        /// filter), an aspect, the other JKR rules, or every check; filtered by the state chip.
        /// Inside it the first failing check's card is open unless the user toggled cards.</summary>
        public IReadOnlyList<ChecklistSection> Checklist
        {
            get
            {
                var aspect = _aspectKey != null;
                var list = SpecChecklist.Build(_spec, _core.Issues, _core.Lang,
                    _otherFocus ? SpecChecklist.OtherKey : aspect ? PanelCore.AllSections : _core.Cat,
                    key => aspect ? true : _sectionOpen.TryGetValue(key, out var o) ? o : (bool?)null,
                    id => _showAll.Contains(id),
                    _stateFilter,
                    id => _why.TryGetValue(id, out var w) ? w : (bool?)null,
                    aspect ? AspectMember(_aspectKey) : null);
                var checks = list.SelectMany(s => s.Items).SelectMany(i => i.Checks).ToList();
                var first = checks.FirstOrDefault(c => c.Tone == "fail")?.Id;
                foreach (var c in checks) c.IsOpen = _checkOpen.TryGetValue(c.Id, out var open) ? open : c.Id == first;
                return list;
            }
        }

        /// <summary>Open or close a check's card inside a section.</summary>
        public void ToggleCheck(string checkId)
        {
            var c = Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).FirstOrDefault(x => x.Id == checkId);
            if (c == null) return;
            _checkOpen[checkId] = !c.IsOpen;
            RaiseAll();
        }

        // ── Clear Sheet head ──
        private string L => _core.Lang;
        private string S(string key) => SpecChecklist.Str(key, _core.Lang);

        /// <summary>"Does not comply" / "No failures in the checks covered" / "No spec check verified yet".</summary>
        public string VerdictHead =>
            _spec == null ? "" : _spec.Verdict == SpecVerdict.Fails ? S("vHeadFail") : SpecChecklist.VerdictTitle(_spec, L);
        /// <summary>"22 spec checks fail." — or, with none failing while a person still decides
        /// some, "Not a full pass yet: …"; "" otherwise.</summary>
        public string VerdictSub
        {
            get
            {
                if (_spec == null) return "";
                if (_spec.Verdict == SpecVerdict.Fails) return SpecChecklist.Plural("vSubFail", L, _spec.FailChecks);
                if (_spec.Verdict == SpecVerdict.NoFailures && _spec.NeedsPersonChecks + _spec.PartialChecks > 0) return S("vSubPerson");
                return "";
            }
        }
        public bool HasVerdictSub => VerdictSub.Length > 0;
        /// <summary>The bold start of the score sentence: "18% compliant".</summary>
        public string HeadPctStrong => string.Format(S("hPct"), HeadPctLabel);
        /// <summary>", 19 of 104 results pass. The % counts results, not checks."</summary>
        public string HeadResultsRest
        {
            get
            {
                if (_spec == null) return ", " + AiPass + " / " + AiTotal + " " + T("checksAI");
                var (p, t) = SpecChecklist.Score(_spec);
                return t == 0 ? S("hRestNone") : SpecChecklist.Plural("hRest", L, t, p, t);
            }
        }
        /// <summary>"13 checks wait for semak manual" (opens those checks).</summary>
        public string ManualLinkLabel => SpecChecklist.Plural("gManualAria", L, PendTotal);
        public bool HasManualLink => _spec != null && PendTotal > 0;
        /// <summary>The issue view's one-line verdict: "18% compliant · 22 fail".</summary>
        public string VerdictShortLine =>
            _spec == null ? HeadPctLabel + " " + CompliantLabel : string.Format(S("vShort"), HeadPctLabel, _spec.FailChecks);

        // ── tabs: Checklist n / Issues n ──
        public int ChecklistTabN => _spec?.Checks.Count ?? 0;
        /// <summary>"Issues", or "Issues · Section D" while a section filters them (the count is that section's).</summary>
        public string IssuesTabLabel => HasCat ? string.Format(S("tabIssuesSec"), _core.Cat) : S("tabIssues");
        /// <summary>Open issues, in the filtered section if one is (the Issues view opens on its Open tab).</summary>
        public int IssuesTabN => _core.TabCounts()[PanelTab.Open];
        public bool IssuesTabOn => ShowList;
        /// <summary>The issue view's one-line verdict is shown (issue tabs of a run with a spec report).</summary>
        public bool ShowVerdictBar => ShowList && HasSpec;
        public void ShowIssues() => SelectTab(PanelTab.Open);

        /// <summary>Inside a section: "12 issues in this section" (hidden when none, and in an
        /// aspect or the other rules, which are not a section filter).</summary>
        public bool HasSectionIssuesLink => IsFocused && _aspectKey == null && !_otherFocus && HasCat && IssuesTabN > 0;
        public string SectionIssuesLabel => SpecChecklist.Plural("secIssues", L, IssuesTabN);
        /// <summary>Follow the link: the Issues view, still filtered to the section (items 9 and
        /// 10); its pill clears the filter. Back / Esc from the section clear it instead.</summary>
        public void ShowSectionIssues() => SelectTab(PanelTab.Open);

        // ── the list: a section / aspect row opens its checks ──
        private string _aspectKey;       // an aspect is open (N / P / Q / G)
        private bool _otherFocus;        // the other JKR rules are open
        private readonly Dictionary<string, bool> _checkOpen = new Dictionary<string, bool>();

        private static Func<SpecCheck, bool> AspectMember(string letter) =>
            c => SpecAspects.Letter(SpecAspects.Of(c.Id)) == letter;

        /// <summary>The checks of what is open (a section, an aspect), or null for every check.</summary>
        private Func<SpecCheck, bool> FocusMember =>
            _aspectKey != null ? AspectMember(_aspectKey)
            : _core.Cat != PanelCore.AllSections ? (Func<SpecCheck, bool>)(c => c.Section == _core.Cat)
            : null;

        /// <summary>A section, an aspect, the other rules or a state filter is open: the screen
        /// shows its checks (Clear Sheet's section screen) instead of the list.</summary>
        public bool IsFocused => ShowChecklist && (_core.Cat != PanelCore.AllSections || _aspectKey != null || _otherFocus || IsStateFiltered);
        /// <summary>The Result screen's list (head, tabs, sections / aspects).</summary>
        public bool ShowOverview => ShowChecklist && !IsFocused;
        public bool FocusIsOther => IsFocused && _otherFocus;
        /// <summary>State chips inside what is open (not for the other rules: they are not spec checks).</summary>
        public bool ShowFocusChips => IsFocused && !_otherFocus;
        /// <summary>The open checks come from several sections (an aspect, or every check): their
        /// section names head them.</summary>
        public bool ShowSectionHeads => IsFocused && !_otherFocus && _core.Cat == PanelCore.AllSections;

        private GroupRow FocusRow
        {
            get
            {
                if (_spec == null || _otherFocus) return null;
                if (_aspectKey != null) return SpecAspects.Rows(_spec, L).FirstOrDefault(g => g.Key == _aspectKey);
                if (_core.Cat != PanelCore.AllSections) return SpecChecklist.SectionRows(_spec, L).FirstOrDefault(g => g.Key == _core.Cat);
                return SpecChecklist.Group(_spec, PanelCore.AllSections, "", S("allChecks"), c => true, L);
            }
        }
        public string FocusLetter => _otherFocus ? "+" : FocusRow?.Letter ?? "";
        public bool HasFocusLetter => FocusLetter.Length > 0;
        public string FocusName => _otherFocus ? S("otherName") : FocusRow?.Name ?? "";
        public string FocusPctLabel => _otherFocus ? "" : FocusRow?.PctLabel ?? "";
        public IReadOnlyList<string> FocusTicks => FocusRow?.Ticks ?? new List<string>();
        /// <summary>"Section 4 of 5" / "Aspect 2 of 4"; "" for the other rules or every check.</summary>
        public string FocusPosition
        {
            get
            {
                if (!IsFocused || _otherFocus) return "";
                if (_aspectKey != null)
                {
                    var i = SpecAspects.All.Select(SpecAspects.Letter).ToList().IndexOf(_aspectKey);
                    return i < 0 ? "" : string.Format(S("posAspect"), i + 1, SpecAspects.All.Count);
                }
                var s = Array.IndexOf(PanelCore.Secs, _core.Cat);
                return s < 0 ? "" : string.Format(S("posSection"), s + 1, PanelCore.Secs.Length);
            }
        }
        public string BackLabel => ChecklistTabLabel;
        public string FilterGroupAria => S("filterGroup");

        /// <summary>Open a row of the list: a section letter (also the Issues filter), an aspect
        /// letter (in the Aspects view), or <see cref="SpecChecklist.OtherKey"/>.</summary>
        public void OpenGroup(string key)
        {
            if (_spec == null || string.IsNullOrEmpty(key)) return;
            var isOther = key == SpecChecklist.OtherKey;
            if (!isOther && (_aspects ? !SpecAspects.All.Any(a => SpecAspects.Letter(a) == key) : !PanelCore.Secs.Contains(key))) return;
            _stateFilter = SpecChecklist.AllStates;
            _sectionOpen.Clear();
            _checkOpen.Clear();
            _checklist = true;
            _otherFocus = isOther;
            _aspectKey = !isOther && _aspects ? key : null;
            var cat = !isOther && !_aspects ? key : PanelCore.AllSections;
            if (_core.Cat != cat) { if (cat == PanelCore.AllSections) _core.ClearCat(); else _core.SetCat(cat); }
            RaiseAll();
        }

        /// <summary>Back to the list: no section, aspect or state filter.</summary>
        public void CloseFocus()
        {
            _aspectKey = null;
            _otherFocus = false;
            _stateFilter = SpecChecklist.AllStates;
            _sectionOpen.Clear();
            _checkOpen.Clear();
            if (_core.Cat != PanelCore.AllSections) _core.ClearCat();
            RaiseAll();
        }

        // ── the list's other rows and notes ──
        public bool HasOtherRow => _spec != null && _spec.Others.Count > 0;
        /// <summary>The "Other JKR rules" row sits under the Borang sections (not the aspects).</summary>
        public bool ShowOtherRow => HasOtherRow && ShowSections;
        public string OtherRowName => S("otherName");
        public string OtherRowNote => S("otherNote");
        /// <summary>"4 fail", or "" when none fails.</summary>
        public string OtherRowCount
        {
            get
            {
                var n = _spec?.Others.Count(o => o.State == SpecState.Fail) ?? 0;
                return n > 0 ? string.Format(S("secFail"), n) : "";
            }
        }
        // Clear Sheet's legend order.
        private static readonly string[] LegendOrder = { "fail", "pass", "partial", "person", "listed", "na", "none" };

        /// <summary>The states the run has, in Clear Sheet's legend order, with their names.</summary>
        public IReadOnlyList<StateChip> Legend
        {
            get
            {
                if (_spec == null) return new List<StateChip>();
                var tones = new HashSet<string>(_spec.Checks.Select(c => SpecChecklist.ToneOf(c.State)));
                return LegendOrder.Where(tones.Contains)
                    .Select(k => new StateChip { Key = k, Tone = k, Label = SpecChecklist.StateLabel(k, L) }).ToList();
            }
        }
        public bool ShowAspectsNote => ShowOverview && _aspects;
        public string AspectsNote => string.Format(S("aspectsNote"), ChecklistTabN);

        // ── the split banner: LOD changed since the run, backend unavailable ──
        public bool ShowLodBanner => LodChanged;
        public string LodBannerStrong => string.Format(S("lodStrong"), _core.RunLod);
        public string LodBannerRest => string.Format(S("lodRest"), _core.Lod);
        public string BackendBannerStrong => S("beStrong");
        public string BackendBannerRest => SpecChecklist.BackendNeedsLine(SpecChecklist.BackendNotChecked(_spec), L);
        public bool ShowBanner => ShowLodBanner || ShowBackendBanner;
        public bool ShowBothBanners => ShowLodBanner && ShowBackendBanner;

        // ── "Borang sections | Aspects" (Clear Sheet): the same 61 checks, two groupings ──
        // A view switch only: the section filter, state filter, selection and open
        // sections are left as they are.
        private bool _aspects;

        /// <summary>The list groups checks by aspect (N / P / Q / G) instead of Borang section (A–E).</summary>
        public bool ShowAspects
        {
            get => _aspects;
            set { if (_aspects == value) return; _aspects = value; RaiseAll(); }
        }
        public bool ShowSections => !_aspects;
        public void ShowSectionsView() => ShowAspects = false;
        public void ShowAspectsView() => ShowAspects = true;
        public string SectionsToggleLabel => SpecChecklist.Str("bySections", _core.Lang);
        public string AspectsToggleLabel => SpecChecklist.Str("byAspects", _core.Lang);
        public string GroupToggleAria => SpecChecklist.Str("groupAria", _core.Lang);

        /// <summary>The rows of the current grouping (empty without a spec report): section
        /// rows A–E, or aspect rows N / P / Q / G — both from <see cref="SpecChecklist.Group"/>.</summary>
        public IReadOnlyList<GroupRow> GroupRows =>
            _aspects ? SpecAspects.Rows(_spec, _core.Lang) : SpecChecklist.SectionRows(_spec, _core.Lang);

        /// <summary>The legend: every state with its count of spec checks; click filters the Checklist.</summary>
        public IReadOnlyList<StateChip> StateChips => SpecChecklist.StateChips(_spec, _core.Lang, _stateFilter, FocusMember);
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
            var open = _why.TryGetValue(checkId, out var w) && w;
            _why[checkId] = !open;
            RaiseAll();
        }

        /// <summary>"n need a person": with a spec report, the Checklist filtered to those
        /// checks (the number counts them); without one, the Manual tab.</summary>
        public void GoNeedsPerson()
        {
            if (_spec == null) { GoManual(); return; }
            _stateFilter = "person";
            _aspectKey = null;
            _otherFocus = false;
            _checkOpen.Clear();
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
        public string KeysAria => SpecChecklist.Str("keysAria", _core.Lang);
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
            // Esc inside a section goes back to the list (the detail, confirm or export close first).
            if (key == PanelKey.Escape && !typing && IsFocused && _core.Stage == PanelStage.Results
                && _core.DetailId == null && _core.Confirm == null && !_core.ExportOpen)
            {
                CloseFocus();
                return KeyOutcome.Handled;
            }
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
