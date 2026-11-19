using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>One issue card in the detail view (prototype mkCard).</summary>
    public sealed class DetailCard
    {
        public string Id { get; set; } = "";
        public string SvLabel { get; set; } = "";
        /// <summary>"High" | "Med" | "Low" | "man" | "ok" | "ign" (prototype sv-*).</summary>
        public string SvKind { get; set; } = "";
        /// <summary>"Borang A1 · Model integrity &amp; quality".</summary>
        public string Where { get; set; } = "";
        public bool HasWhere => !string.IsNullOrEmpty(Where);
        public string Hash { get; set; } = "";
        public string Title { get; set; } = "";
        public string Desc { get; set; } = "";
        public bool HasDesc => !string.IsNullOrEmpty(Desc);
        public string El { get; set; } = "";
        public string Req { get; set; } = "";
        public string Act { get; set; } = "";
        /// <summary>"bad" (red, AI verdict) or "man" (indigo, a person decides).</summary>
        public string ActKind { get; set; } = "bad";

        public bool IsManual { get; set; }
        public string Reason { get; set; } = "";
        public bool IsReviewed { get; set; }
        public string ReviewLabel { get; set; } = "";

        /// <summary>Suggested fix block: open AI item with a ready-made fix.</summary>
        public bool CanFix { get; set; }
        public string Prop { get; set; } = "";

        /// <summary>The last auto-fix failed: it stays open with the reason.</summary>
        public string FixError { get; set; } = "";
        public bool HasFixError => !string.IsNullOrEmpty(FixError);

        /// <summary>How to fix it by hand: open AI items without a ready-made fix.</summary>
        public IReadOnlyList<string> Steps { get; set; } = Array.Empty<string>();
        public bool HasSteps => Steps.Count > 0;

        public bool IsDone { get; set; }
        public string DoneLabel { get; set; } = "";

        public bool CanLocate { get; set; }
        public bool CanIgnore { get; set; }
        public string Cite { get; set; } = "";
        public bool HasCite => !string.IsNullOrEmpty(Cite) && Cite != "—";
        /// <summary>The cited spec has a PDF the source line can open.</summary>
        public bool CanOpenSource { get; set; }
    }

    /// <summary>A step dot under the card (prototype .dot).</summary>
    public sealed class DetailDot
    {
        public int Index { get; set; }
        public bool IsOn { get; set; }
        public string Label { get; set; } = "";
    }

    /// <summary>
    /// The issue detail (prototype detail dialog; frames f-detail, f-preview,
    /// f-fixed, f-ignore, m-detail). Opens over Revit's window from a Results row
    /// with the list as it was at that moment (prototype detailIds); ← → / J K and
    /// sideways scroll move through it. Fix and Ignore go through the Results
    /// screen's requests (the host confirms and writes), Locate and the source PDF
    /// through events. WPF-free.
    /// </summary>
    public sealed class DetailScreenModel : INotifyPropertyChanged
    {
        /// <summary>Most dots shown; a longer list shows a window around the current issue.</summary>
        public const int MaxDots = 15;

        private static readonly Dictionary<string, (string Ms, string En)> Extra = new Dictionary<string, (string, string)>
        {
            ["fixFailed"] = ("Auto-fix gagal", "Auto-fix failed"),
            ["howToFix"] = ("Cara membaiki secara manual", "How to fix manually"),
        };

        private readonly PanelCore _core;
        private readonly ResultsScreenModel _results;

        public DetailScreenModel(PanelCore core, ResultsScreenModel results)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _results = results ?? throw new ArgumentNullException(nameof(results));
            _core.Changed += RaiseAll;
            _results.PropertyChanged += (_, __) => RaiseAll();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>The detail opened (the host shows its window).</summary>
        public event Action Opened;
        /// <summary>Locate in 3D for this issue id.</summary>
        public event Action<string> LocateRequested;
        /// <summary>Open the cited spec PDF for this issue id.</summary>
        public event Action<string> SourceRequested;

        /// <summary>Items that can open their source PDF (set by the host).</summary>
        public Func<string, bool> HasSourcePdf { get => _hasPdf; set { _hasPdf = value ?? (_ => false); _card = null; } }
        private Func<string, bool> _hasPdf = _ => false;
        /// <summary>Items with an element to locate (set by the host).</summary>
        public Func<string, bool> IsLocatable { get => _locatable; set { _locatable = value ?? (_ => true); _card = null; } }
        private Func<string, bool> _locatable = _ => true;
        /// <summary>How-to-fix steps per item (set by the host).</summary>
        public Func<string, IReadOnlyList<string>> StepsOf { get => _stepsOf; set { _stepsOf = value ?? (_ => Array.Empty<string>()); _card = null; } }
        private Func<string, IReadOnlyList<string>> _stepsOf = _ => Array.Empty<string>();

        private string T(string key) =>
            Extra.TryGetValue(key, out var p) ? (_core.Lang == PanelStrings.En ? p.En : p.Ms) : _core.T(key);

        // ── open / close ──
        public bool IsOpen => _core.DetailId != null && Current != null;

        /// <summary>Open at this item, with the current filtered list to move through.</summary>
        public void Open(string id)
        {
            if (string.IsNullOrEmpty(id) || _core.Issues.All(x => x.Id != id)) return;
            _core.Open(id);
            Opened?.Invoke();
        }

        /// <summary>Close; the list selection moves to the issue last shown, so ⏎ reopens it.</summary>
        public void Close()
        {
            var id = _core.DetailId;
            if (id == null) return;
            var i = _core.Filtered().FindIndex(x => x.Id == id);
            if (i >= 0) _core.SetSel(i);
            _core.CloseDetail();
        }

        // ── header ──
        private List<string> Ids =>
            _core.DetailIds.Count > 0 ? _core.DetailIds : (_core.DetailId != null ? new List<string> { _core.DetailId } : new List<string>());

        public int Index => Math.Max(0, Ids.IndexOf(_core.DetailId ?? ""));
        public int Count => Ids.Count;
        public string IdxLabel => T("idx") + " " + (Index + 1) + " " + T("of") + " " + Count;
        public double ProgFraction => Count == 0 ? 0 : (Index + 1) / (double)Count;
        public bool AtStart => Index == 0;
        public bool AtEnd => Index >= Count - 1;
        public bool CanPrev => !AtStart;
        public bool CanNext => !AtEnd;
        public string PrevLabel => T("prevIssue");
        public string NextLabel => T("nextIssue");
        public string CloseLabel => T("close");
        public string SwipeHint => T("swipeHint");

        public IReadOnlyList<DetailDot> Dots
        {
            get
            {
                var n = Count;
                var idx = Index;
                var start = n <= MaxDots ? 0 : Math.Max(0, Math.Min(n - MaxDots, idx - MaxDots / 2));
                var end = Math.Min(n, start + MaxDots);
                var dots = new List<DetailDot>();
                for (var j = start; j < end; j++)
                    dots.Add(new DetailDot { Index = j, IsOn = j == idx, Label = T("dotLabel") + " " + (j + 1) });
                return dots;
            }
        }

        public void Prev() => _core.GoTo(Index - 1);
        public void Next() => _core.GoTo(Index + 1);
        public void GoTo(int j) => _core.GoTo(j);

        // ── card ──
        public PanelItem Current => _core.Current;

        private DetailCard _card;

        /// <summary>The current card, built once per change (every field binds to it).</summary>
        public DetailCard Card
        {
            get
            {
                var c = Current;
                if (c == null) return _card = null;
                return _card != null && _card.Id == c.Id ? _card : (_card = MakeCard(c));
            }
        }

        public string ElementLabel => T("elementL");
        public string RequiredLabel => T("requiredL");
        public string ActualLabel => T("actualL");
        public string WhyManualLabel => T("whyManual");
        public string ManualNote => T("manualNote");
        public string SuggestedLabel => T("suggested");
        public string AiFixSub => T("aiFixSub");
        public string ApplyFixLabel => T("applyFix");
        public string LocateLabel => T("locate");
        public string IgnoreLabel => T("ignore");
        public string SourceLabel => T("sourceL");
        public string FixFailedLabel => T("fixFailed");
        public string HowToFixLabel => T("howToFix");
        /// <summary>Writes are off while a fix batch runs.</summary>
        public bool CanAct => _results.CanAct;

        private DetailCard MakeCard(PanelItem c)
        {
            var man = c.IsHuman;
            var lang = _core.Lang;
            string sv, kind;
            if (man) { sv = T("manualRow"); kind = "man"; }
            else if (c.Status == ItemStatus.Resolved) { sv = T("fixedRow"); kind = "ok"; }
            else if (c.Status == ItemStatus.Ignored) { sv = T("ignoredRow"); kind = "ign"; }
            else { sv = PanelStrings.Severity[c.Sev].In(lang); kind = c.Sev; }
            var open = !man && c.Status == ItemStatus.Open;
            var where = new List<string>();
            if (!string.IsNullOrEmpty(c.Row) && c.Row != "—") where.Add("Borang " + c.Row);
            if (!string.IsNullOrEmpty(c.Sec)) where.Add(SectionName(c.Sec));
            var steps = open && c.Fix == FixKind.None ? (StepsOf(c.Id) ?? Array.Empty<string>()) : Array.Empty<string>();
            return new DetailCard
            {
                Id = c.Id,
                SvLabel = sv,
                SvKind = kind,
                Where = string.Join(" · ", where),
                Hash = c.Hash ?? "",
                Title = c.Title.In(lang),
                // A person-decided item shows its reason in the manual block, not twice.
                Desc = man && c.Reason != null && c.Reason.In(lang) == c.Desc.In(lang) ? "" : c.Desc.In(lang),
                El = string.IsNullOrEmpty(c.El) ? "—" : c.El,
                Req = string.IsNullOrEmpty(c.Req) ? "—" : c.Req,
                Act = string.IsNullOrEmpty(c.Act.In(lang)) ? "—" : c.Act.In(lang),
                ActKind = man ? "man" : "bad",
                IsManual = man,
                Reason = man ? c.Reason?.In(lang) ?? "" : "",
                IsReviewed = man && c.Status == ItemStatus.Reviewed,
                ReviewLabel = c.Status == ItemStatus.Reviewed ? T("reviewed") : T("markReviewed"),
                CanFix = open && c.Fix == FixKind.Fix,
                Prop = c.Prop ?? "",
                FixError = open ? c.FixError ?? "" : "",
                Steps = steps,
                IsDone = !man && c.Status != ItemStatus.Open,
                DoneLabel = c.Status == ItemStatus.Resolved ? T("doneFix") : T("doneIgn"),
                CanLocate = IsLocatable(c.Id),
                CanIgnore = open,
                Cite = c.Cite ?? "",
                CanOpenSource = HasSourcePdf(c.Id),
            };
        }

        private string SectionName(string s) =>
            Bim010Form.SectionNames.TryGetValue(s, out var n) ? (_core.Lang == PanelStrings.En ? n.En : n.Ms) : s;

        // ── actions ──
        /// <summary>Apply auto-fix (F): the host confirms, writes, and the card turns "Fixed".</summary>
        public void Fix()
        {
            var c = Current;
            if (c == null || c.IsHuman || c.Status != ItemStatus.Open || c.Fix == FixKind.None) return;
            _results.FixRow(c.Id);
        }

        /// <summary>Ignore (A): plain toast; the card turns "Ignored".</summary>
        public void Ignore()
        {
            var c = Current;
            if (c == null || c.IsHuman || c.Status != ItemStatus.Open) return;
            _results.IgnoreRow(c.Id);
        }

        /// <summary>Mark as reviewed ↔ back to open (items that need a person).</summary>
        public void ToggleReviewed()
        {
            var c = Current;
            if (c == null || !c.IsHuman) return;
            _results.ToggleReviewed(c.Id);
        }

        /// <summary>Locate in 3D. The detail closes first: it covers Revit's window,
        /// and the point is to see the element there. ⏎ on the list reopens it.</summary>
        public void Locate()
        {
            var c = Current;
            if (c == null || !IsLocatable(c.Id)) return;
            Close();
            LocateRequested?.Invoke(c.Id);
        }

        public void OpenSource()
        {
            var c = Current;
            if (c != null && HasSourcePdf(c.Id)) SourceRequested?.Invoke(c.Id);
        }

        /// <summary>prototype handleKey while the detail is open: Esc closes, ← → / J K
        /// move, F fixes, A ignores. Returns Handled when the key was used.</summary>
        public KeyOutcome HandleKey(PanelKey key)
        {
            if (!IsOpen) return KeyOutcome.None;
            switch (key)
            {
                case PanelKey.Escape: Close(); return KeyOutcome.Handled;
                case PanelKey.Right:
                case PanelKey.J: Next(); return KeyOutcome.Handled;
                case PanelKey.Left:
                case PanelKey.K: Prev(); return KeyOutcome.Handled;
                case PanelKey.F: Fix(); return KeyOutcome.Handled;
                case PanelKey.A: Ignore(); return KeyOutcome.Handled;
                default: return KeyOutcome.None;
            }
        }

        private void RaiseAll()
        {
            _card = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
