using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The prototype's panel logic ("Model Audit Prototype.dc.html", class
    /// Component), ported method for method with no WPF and no Revit so every
    /// rule is unit-tested: stats(), filtered(), tab counts, Fix all / Ignore all
    /// scope, ignore with a 6 s undo, re-scan (fixed → passed), reset (back to
    /// Start), reviewed state, the run animation and handleKey. Timers go
    /// through <see cref="IPanelClock"/>. The XAML views (steps 7–13) bind to it.
    ///
    /// One addition for the real model: with <see cref="DeferWrites"/> set,
    /// confirming a fix does not mark it resolved — the host writes to Revit and
    /// calls <see cref="MarkFixed"/> for the fixes that succeeded.
    /// </summary>
    public sealed class PanelCore
    {
        public static readonly string[] Secs = { "A", "B", "C", "D", "E" };
        public const string AllSections = "all";

        public static readonly TimeSpan ToastTime = TimeSpan.FromMilliseconds(2600);
        public static readonly TimeSpan UndoToastTime = TimeSpan.FromMilliseconds(6000);
        public static readonly TimeSpan SectionTime = TimeSpan.FromMilliseconds(750);
        public static readonly TimeSpan ResultsDelay = TimeSpan.FromMilliseconds(800);

        private readonly IPanelClock _clock;
        private List<PanelItem> _fresh;
        private Dictionary<string, int> _basePass;
        private IDisposable _toastTimer;
        private IDisposable _runTimer;

        public PanelCore(IEnumerable<PanelItem> items, IReadOnlyDictionary<string, int> basePass, IPanelClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Load(items, basePass);
        }

        /// <summary>Raised after every state change.</summary>
        public event Action Changed;

        /// <summary>Raised when a confirmed fix should be written (DeferWrites).</summary>
        public event Action<ConfirmRequest> Confirmed;

        /// <summary>When true, confirming leaves statuses alone and raises
        /// <see cref="Confirmed"/>; the host calls <see cref="MarkFixed"/> after the write.</summary>
        public bool DeferWrites { get; set; }

        // ── state (prototype this.state) ──
        public PanelStage Stage { get; private set; } = PanelStage.Start;
        public string Lod { get; private set; } = "";
        public string RunLod { get; private set; } = "";
        public string Disc { get; private set; } = "AR";
        public string Lang { get; private set; } = PanelStrings.Ms;
        public HashSet<string> Run { get; } = new HashSet<string>();
        public int PendLive { get; private set; }
        public List<PanelItem> Issues { get; private set; } = new List<PanelItem>();
        public string Cat { get; private set; } = AllSections;
        public PanelTab Tab { get; private set; } = PanelTab.Open;
        public string Q { get; private set; } = "";
        public int Sel { get; private set; }
        public string DetailId { get; private set; }
        public List<string> DetailIds { get; private set; } = new List<string>();
        public ConfirmRequest Confirm { get; private set; }
        public bool Consent { get; private set; }
        public bool ExportOpen { get; private set; }
        public string Toast { get; private set; }
        public List<string> UndoIds { get; private set; }
        public string Located { get; private set; }

        /// <summary>prototype t()</summary>
        public string T(string key) => PanelStrings.Get(Lang, key);

        /// <summary>Replace the issues (a new scan). Reset returns to these.</summary>
        public void Load(IEnumerable<PanelItem> items, IReadOnlyDictionary<string, int> basePass)
        {
            _fresh = (items ?? Enumerable.Empty<PanelItem>()).Select(Fresh).ToList();
            _basePass = Secs.ToDictionary(s => s, s => basePass != null && basePass.TryGetValue(s, out var n) ? n : 0);
            Issues = _fresh.Select(i => i.Clone()).ToList();
            Notify();
        }

        /// <summary>New data for the same results (a status changed, a fix, a
        /// re-check): items keep the status they arrive with, and the tab, section,
        /// search, selection and open detail stay. Reset returns to these items.</summary>
        public void Replace(IEnumerable<PanelItem> items, IReadOnlyDictionary<string, int> basePass)
        {
            Issues = (items ?? Enumerable.Empty<PanelItem>()).Select(i => i.Clone()).ToList();
            _fresh = Issues.Select(Fresh).ToList();
            _basePass = Secs.ToDictionary(s => s, s => basePass != null && basePass.TryGetValue(s, out var n) ? n : 0);
            var count = Filtered().Count;
            if (Sel >= count) Sel = Math.Max(0, count - 1);
            if (DetailId != null && Issues.All(x => x.Id != DetailId)) DetailId = null;
            Notify();
        }

        private static PanelItem Fresh(PanelItem i)
        {
            var c = i.Clone();
            c.Status = ItemStatus.Open;
            c.Passed = false;
            return c;
        }

        private void Notify() => Changed?.Invoke();

        // ── settings ──
        public void SetLod(string lod) { Lod = lod ?? ""; Notify(); }
        public void SetDisc(string disc) { Disc = disc ?? "AR"; Notify(); }
        public void SetLang(string lang) { Lang = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? PanelStrings.En : PanelStrings.Ms; Notify(); }
        public bool RunDisabled => string.IsNullOrEmpty(Lod);
        public bool LodMissing => string.IsNullOrEmpty(Lod);
        public bool Lod100 => Lod == "100";
        public bool LodChanged => Stage == PanelStage.Results && Lod != RunLod;

        // ── run ──
        public int RunDone => Secs.Count(s => Run.Contains(s));

        /// <summary>prototype startRun: one section completes every 750 ms, results
        /// 800 ms after the last. Nothing happens without an LOD.</summary>
        public void StartRun()
        {
            if (string.IsNullOrEmpty(Lod)) return;
            _runTimer?.Dispose();
            Stage = PanelStage.Running;
            Run.Clear();
            PendLive = 0;
            RunLod = Lod;
            DetailId = null;
            Notify();
            ScheduleSection(0);
        }

        private void ScheduleSection(int i)
        {
            _runTimer = _clock.Schedule(SectionTime, () =>
            {
                var s = Secs[i];
                Run.Add(s);
                PendLive += Issues.Count(x => x.Sec == s && x.IsHuman);
                Notify();
                if (i + 1 < Secs.Length) ScheduleSection(i + 1);
                else _runTimer = _clock.Schedule(ResultsDelay, () =>
                {
                    Stage = PanelStage.Results;
                    Tab = PanelTab.Open;
                    Sel = 0;
                    Notify();
                });
            });
        }

        /// <summary>A run driven by the real scan (no prototype timers): Running now,
        /// <see cref="FinishExternalRun"/> when the Running screen has shown the results.
        /// Nothing happens without an LOD.</summary>
        public void BeginExternalRun()
        {
            if (string.IsNullOrEmpty(Lod)) return;
            _runTimer?.Dispose();
            _runTimer = null;
            Stage = PanelStage.Running;
            Run.Clear();
            PendLive = 0;
            RunLod = Lod;
            DetailId = null;
            Notify();
        }

        /// <summary>The real run is over: Results, Open tab, first row.</summary>
        public void FinishExternalRun()
        {
            if (Stage != PanelStage.Running) return;
            foreach (var s in Secs) Run.Add(s);
            Stage = PanelStage.Results;
            Tab = PanelTab.Open;
            Sel = 0;
            Notify();
        }

        /// <summary>prototype rescan: fixed items now pass, then a new run.</summary>
        public void Rescan()
        {
            foreach (var x in Issues.Where(x => x.Status == ItemStatus.Resolved)) x.Passed = true;
            StartRun();
        }

        /// <summary>prototype reset: back to Start, LOD cleared, every issue open
        /// again, filters cleared. Writes nothing to the model.</summary>
        public void Reset()
        {
            _runTimer?.Dispose();
            _runTimer = null;
            Stage = PanelStage.Start;
            Lod = "";
            Issues = _fresh.Select(i => i.Clone()).ToList();
            Cat = AllSections;
            Tab = PanelTab.Open;
            Q = "";
            Sel = 0;
            DetailId = null;
            ExportOpen = false;
            Located = null;
            Notify();
        }

        // ── stats / filtered (prototype stats(), filtered()) ──
        public Dictionary<string, SectionStats> Stats()
        {
            var output = new Dictionary<string, SectionStats>();
            foreach (var s in Secs)
            {
                var its = Issues.Where(x => x.Sec == s).ToList();
                output[s] = new SectionStats
                {
                    Pass = _basePass[s] + its.Count(x => !x.IsHuman && x.Passed),
                    Fail = its.Count(x => !x.IsHuman && !x.Passed),
                    Pend = its.Count(x => x.IsHuman),
                };
            }
            return output;
        }

        public List<PanelItem> Filtered()
        {
            var qq = (Q ?? "").Trim().ToLowerInvariant();
            return Issues
                .Where(x => !x.Passed)
                .Where(x => Cat == AllSections || x.Sec == Cat)
                .Where(x =>
                {
                    if (Tab == PanelTab.Manual) return x.IsHuman;
                    if (x.IsHuman) return false;
                    if (Tab == PanelTab.Open) return x.Status == ItemStatus.Open;
                    if (Tab == PanelTab.Ignore) return x.Status == ItemStatus.Ignored;
                    return x.Status == ItemStatus.Resolved;
                })
                .Where(x => qq.Length == 0 || string.Join(" ", x.Title.In(Lang), x.Row, x.El, x.Act.In(Lang), x.Cite)
                    .ToLowerInvariant().Contains(qq))
                .ToList();
        }

        // ── derived (prototype renderVals) ──
        private List<PanelItem> Live => Issues.Where(x => !x.Passed).ToList();
        private List<PanelItem> InCat => Live.Where(x => Cat == AllSections || x.Sec == Cat).ToList();

        public int AiPass => Stats().Values.Sum(s => s.Pass);
        public int AiTotal => Stats().Values.Sum(s => s.Pass + s.Fail);
        /// <summary>Headline: AI-verified passing cells / AI-verified cells; items
        /// that need a person are never in it.</summary>
        public int? HeadPct => AiTotal == 0 ? (int?)null : (int)Math.Round(AiPass * 100.0 / AiTotal, MidpointRounding.AwayFromZero);
        /// <summary>Items that need a person (the "Manual review" number).</summary>
        public int PendTotal => Live.Count(x => x.IsHuman);

        public int ResolvedN => Live.Count(x => !x.IsHuman && x.Status == ItemStatus.Resolved);
        public int FixTotal => Live.Count(x => !x.IsHuman);
        public int ToGo => FixTotal - ResolvedN - Live.Count(x => !x.IsHuman && x.Status == ItemStatus.Ignored);
        public int ProgN => FixTotal == 0 ? 100 : (int)Math.Round(ResolvedN * 100.0 / FixTotal, MidpointRounding.AwayFromZero);

        /// <summary>Open AI items per severity ("High", "Med", "Low").</summary>
        public Dictionary<string, int> SevCounts()
        {
            var open = Live.Where(x => !x.IsHuman && x.Status == ItemStatus.Open).ToList();
            return new[] { "High", "Med", "Low" }.ToDictionary(s => s, s => open.Count(x => x.Sev == s));
        }

        /// <summary>Fix all scope: open AI items with a plain fix in the current
        /// section (Generate is never part of Fix all).</summary>
        public List<PanelItem> FixableOpen() =>
            InCat.Where(x => !x.IsHuman && x.Status == ItemStatus.Open && x.Fix == FixKind.Fix).ToList();

        /// <summary>Tab counts over the current section.</summary>
        public Dictionary<PanelTab, int> TabCounts()
        {
            var inCat = InCat;
            return new Dictionary<PanelTab, int>
            {
                [PanelTab.Open] = inCat.Count(x => !x.IsHuman && x.Status == ItemStatus.Open),
                [PanelTab.Ignore] = inCat.Count(x => x.Status == ItemStatus.Ignored),
                [PanelTab.Resolved] = inCat.Count(x => x.Status == ItemStatus.Resolved),
                [PanelTab.Manual] = inCat.Count(x => x.IsHuman),
            };
        }

        /// <summary>Key of the empty-list hint (emptyQ / emptyOpen / emptyOther).</summary>
        public string EmptyHintKey => (Q ?? "").Trim().Length > 0 ? "emptyQ" : Tab == PanelTab.Open ? "emptyOpen" : "emptyOther";

        // ── filters ──
        public void SetCat(string s) { Cat = Cat == s ? AllSections : s; Sel = 0; Notify(); }
        public void ClearCat() { Cat = AllSections; Sel = 0; Notify(); }
        public void SetTab(PanelTab t) { Tab = t; Sel = 0; Notify(); }
        public void SetQuery(string q) { Q = q ?? ""; Sel = 0; Notify(); }
        public void ClearQuery() => SetQuery("");
        public void GoManual() => SetTab(PanelTab.Manual);
        public void SetSel(int i) { Sel = i; Notify(); }

        // ── actions ──
        public void SetStatus(IEnumerable<string> ids, ItemStatus status)
        {
            var set = new HashSet<string>(ids ?? Enumerable.Empty<string>());
            foreach (var x in Issues.Where(x => set.Contains(x.Id))) x.Status = status;
            Notify();
        }

        /// <summary>prototype askFix: confirm dialog for the items that have a fix;
        /// kind from the first ("generate" needs consent). Consent starts unticked.</summary>
        public void AskFix(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids ?? Enumerable.Empty<string>());
            var its = Issues.Where(x => set.Contains(x.Id) && x.Fix != FixKind.None).ToList();
            if (its.Count == 0) return;
            Confirm = new ConfirmRequest
            {
                Kind = its[0].Fix == FixKind.Generate ? FixKind.Generate : FixKind.Fix,
                Ids = its.Select(x => x.Id).ToList(),
            };
            Consent = false;
            Notify();
        }

        public void ToggleConsent() { Consent = !Consent; Notify(); }
        public void CancelConfirm() { Confirm = null; Notify(); }
        public bool ConfirmDisabled => Confirm != null && Confirm.Kind == FixKind.Generate && !Consent;

        /// <summary>prototype doConfirm. Generate without consent does nothing.</summary>
        public void DoConfirm()
        {
            var c = Confirm;
            if (c == null) return;
            if (c.Kind == FixKind.Generate && !Consent) return;
            Confirm = null;
            if (DeferWrites)
            {
                Notify();
                Confirmed?.Invoke(c);
                return;
            }
            SetStatus(c.Ids, ItemStatus.Resolved);
            ShowToast(c.Kind == FixKind.Generate ? T("toastGen") : c.Ids.Count + " " + T("toastFix"));
        }

        /// <summary>Host side of DeferWrites: the fixes that were written.</summary>
        public void MarkFixed(IEnumerable<string> ids)
        {
            var list = (ids ?? Enumerable.Empty<string>()).ToList();
            if (list.Count == 0) return;
            SetStatus(list, ItemStatus.Resolved);
            ShowToast(list.Count + " " + T("toastFix"));
        }

        /// <summary>prototype ignore: toast with Undo for 6 s.</summary>
        public void Ignore(IEnumerable<string> ids)
        {
            var list = (ids ?? Enumerable.Empty<string>()).ToList();
            SetStatus(list, ItemStatus.Ignored);
            ShowToast(list.Count + " " + T("toastIgn"), list);
        }

        /// <summary>Ignore all: open AI items in the current section.</summary>
        public void IgnoreAll()
        {
            var ids = InCat.Where(x => !x.IsHuman && x.Status == ItemStatus.Open).Select(x => x.Id).ToList();
            if (ids.Count > 0) Ignore(ids);
        }

        public void FixAll() => AskFix(FixableOpen().Select(x => x.Id));

        /// <summary>prototype undo: the ignored items go back to open.</summary>
        public void Undo()
        {
            var ids = UndoIds ?? new List<string>();
            SetStatus(ids, ItemStatus.Open);
            _toastTimer?.Dispose();
            Toast = null;
            UndoIds = null;
            Notify();
        }

        /// <summary>prototype onReview: open ↔ reviewed.</summary>
        public void ToggleReviewed(string id)
        {
            var x = Issues.FirstOrDefault(i => i.Id == id);
            if (x == null) return;
            SetStatus(new[] { id }, x.Status == ItemStatus.Reviewed ? ItemStatus.Open : ItemStatus.Reviewed);
        }

        public void Locate(PanelItem it)
        {
            if (it == null) return;
            Located = it.El;
            ShowToast(T("toastLoc") + ": " + it.El);
        }

        public void ShowToast(string msg, List<string> undoIds = null)
        {
            _toastTimer?.Dispose();
            Toast = msg;
            UndoIds = undoIds;
            _toastTimer = _clock.Schedule(undoIds != null ? UndoToastTime : ToastTime, () =>
            {
                Toast = null;
                UndoIds = null;
                Notify();
            });
            Notify();
        }

        // ── export ──
        public void OpenExport() { ExportOpen = true; DetailId = null; Notify(); }
        public void CloseExport() { ExportOpen = false; Notify(); }

        // ── detail ──
        public void OpenDetail(string id, int i)
        {
            Sel = i;
            DetailId = id;
            DetailIds = Filtered().Select(x => x.Id).ToList();
            Notify();
        }

        /// <summary>prototype open(id): detail at the item's place in the list.</summary>
        public void Open(string id)
        {
            var l = Filtered();
            OpenDetail(id, Math.Max(0, l.FindIndex(x => x.Id == id)));
        }

        public void CloseDetail() { DetailId = null; Notify(); }

        public PanelItem Current => DetailId == null ? null : Issues.FirstOrDefault(x => x.Id == DetailId);

        /// <summary>Position of the open detail in its fixed list (detailIds).</summary>
        public int DetailIndex => DetailId == null ? -1 : Math.Max(0, (DetailIds.Count > 0 ? DetailIds : new List<string> { DetailId }).IndexOf(DetailId));

        public void GoTo(int j)
        {
            var ids = DetailIds.Count > 0 ? DetailIds : (DetailId != null ? new List<string> { DetailId } : new List<string>());
            if (j < 0 || j >= ids.Count) return;
            DetailId = ids[j];
            Notify();
        }

        // ── keyboard (prototype handleKey) ──
        /// <param name="typing">Focus is in a text box / select.</param>
        public KeyOutcome HandleKey(PanelKey key, bool typing)
        {
            if (key == PanelKey.Escape)
            {
                if (Confirm != null) { Confirm = null; Notify(); }
                else if (DetailId != null) { DetailId = null; Notify(); }
                else if (ExportOpen) { ExportOpen = false; Notify(); }
                else if (typing) return KeyOutcome.Blur;
                return KeyOutcome.None;
            }
            if (Stage != PanelStage.Results || Confirm != null || ExportOpen || typing) return KeyOutcome.None;

            var list = Filtered();
            if (DetailId != null)
            {
                var ids = DetailIds;
                var idx = ids.IndexOf(DetailId);
                var cur = Current;
                if (key == PanelKey.Right || key == PanelKey.J)
                {
                    if (ids.Count > 0) { DetailId = ids[Math.Min(ids.Count - 1, idx + 1)]; Notify(); }
                    return KeyOutcome.Handled;
                }
                if (key == PanelKey.Left || key == PanelKey.K)
                {
                    if (ids.Count > 0) { DetailId = ids[Math.Max(0, idx - 1)]; Notify(); }
                    return KeyOutcome.Handled;
                }
                if (key == PanelKey.F && cur != null && cur.Fix != FixKind.None && cur.Status == ItemStatus.Open) AskFix(new[] { cur.Id });
                else if (key == PanelKey.A && cur != null && !cur.IsHuman && cur.Status == ItemStatus.Open) Ignore(new[] { cur.Id });
                return KeyOutcome.None;
            }

            if (key == PanelKey.Slash) return KeyOutcome.FocusSearch;
            if (list.Count == 0) return KeyOutcome.None;
            var sel = Math.Min(Sel, list.Count - 1);
            switch (key)
            {
                case PanelKey.J:
                case PanelKey.Down:
                    Sel = Math.Min(list.Count - 1, sel + 1); Notify();
                    return KeyOutcome.Handled;
                case PanelKey.K:
                case PanelKey.Up:
                    Sel = Math.Max(0, sel - 1); Notify();
                    return KeyOutcome.Handled;
                case PanelKey.Enter:
                    Open(list[sel].Id);
                    return KeyOutcome.None;
                case PanelKey.F:
                    if (list[sel].Fix != FixKind.None && list[sel].Status == ItemStatus.Open) AskFix(new[] { list[sel].Id });
                    return KeyOutcome.None;
                case PanelKey.A:
                    if (!list[sel].IsHuman && list[sel].Status == ItemStatus.Open) Ignore(new[] { list[sel].Id });
                    return KeyOutcome.None;
                default:
                    return KeyOutcome.None;
            }
        }
    }
}
