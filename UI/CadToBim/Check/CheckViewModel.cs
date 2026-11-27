// AI-638: the Check pane's view model. Holds the one CheckState; every bound property is computed
// from it, and every change refreshes them all, so counts are always live. Revit/WPF-free: the
// Revit side comes in through IRevitView, the read through a delegate (run off the UI thread).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cad2Bim.ViewModels;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    /// <summary>What the pane asks of the Revit view. The real one raises an ExternalEvent; tests and the harness pass a fake or null.</summary>
    public interface IRevitView
    {
        void ZoomTo(BoxMm box);
        /// <summary>Link the DWG, place it so the main plan sits in the view, zoom to that plan.</summary>
        void ShowUnderlay(string dwgPath, double mmPerUnit, BoxMm mainPlan, BoxMm? drawingExtents);
        void RemoveUnderlay();
        /// <summary>A preview call failed (already logged); the text is shown as one line on screen 2.</summary>
        event Action<string> Failed;
        /// <summary>The DWG preview link was made.</summary>
        event Action Linked;
        /// <summary>A one-line note for screen 2 (e.g. a leftover preview was removed).</summary>
        event Action<string> Notice;
    }

    /// <summary>One question card as shown: its number, focus and the plan its crop comes from.</summary>
    public sealed class QuestionCard
    {
        public int Number { get; set; }
        public Question Q { get; set; }
        public bool IsFocused { get; set; }
        public PlanSnap Plan { get; set; }
        public bool IsAnswered => Q.Answer != null;
        public bool ShowSource => IsAnswered && !string.IsNullOrEmpty(Q.Source);
        public string SourceName => "Source: " + Q.Source;
        public string AnsweredText => "✓ " + Q.Answer;
        public string NumberText => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class CheckViewModel : ViewModelBase
    {
        private readonly IRevitView _view;
        private readonly Func<string, SheetSnapshot> _read;
        private readonly EngineShortcuts _shortcuts;
        private string _path;
        private int _openSeq;   // only the latest open may write State
        private int _focused = -1;
        private readonly double _wallHeightMm;

        public CheckState State { get; private set; } = new CheckState();

        /// <summary>Raised by Build; the Build phase (AI-642 / AI-628) picks it up.</summary>
        public event Action<CheckState> BuildRequested;

        /// <param name="read">Reads a DWG into a snapshot. Called on the thread pool, never on the UI thread.</param>
        /// <param name="shortcuts">Fixture engine data (harness/tests/dev flag only); null = everything pending.</param>
        public CheckViewModel(Func<string, SheetSnapshot> read, IRevitView view = null, EngineShortcuts shortcuts = null, double wallHeightMm = 3000)
        {
            _wallHeightMm = wallHeightMm;
            _read = read;
            _view = view;
            if (_view != null)
            {
                _view.Failed += message => { State.PreviewError = message; State.Linked = false; Changed(); };
                _view.Linked += () => { State.Linked = true; State.PreviewError = null; Changed(); };
                _view.Notice += message => { State.PreviewNotice = message; Changed(); };
            }
            _shortcuts = shortcuts;
        }

        // ── bound surface (all derived from State) ──
        public CheckScreen Screen => State.Screen;
        public string Error => State.Error;
        public string FileName => State.Sheet?.File ?? "";
        public string Engine => State.Sheet?.Engine ?? "";
        /// <summary>File row: "linked underlay" only after a successful link.</summary>
        public string UnderlayText => State.Target.Workshared ? "not linked (workshared)"
            : State.PreviewError != null ? "preview failed"
            : State.Linked ? "linked underlay" : "not linked yet";
        public string ConsultantChip => State.Engine.Consultant?.Name ?? "Consultant pending";
        public bool ConsultantPending => State.Engine.Consultant == null;
        public ConsultantInfo Consultant => State.Engine.Consultant;
        public List<CheckRow> PreflightRows => State.Sheet == null ? new List<CheckRow>() : CheckRows.Preflight(State);
        public List<CheckRow> Gate1Rows => State.Sheet == null ? new List<CheckRow>() : CheckRows.Gate1(State);
        public List<CheckRow> Gate2Rows => State.Sheet == null ? new List<CheckRow>() : CheckRows.Gate2(State);
        public string Gate2Verdict => CheckRows.Gate2Verdict(State);
        public string Gate2Tone => CheckRows.Gate2Tone(State);
        public bool Gate2Blocked => State.Gate2 == GateState.Blocked;
        public List<Question> Questions => State.Questions.Take(3).ToList();
        public List<QuestionCard> Cards => Questions.Select((q, i) => new QuestionCard
        {
            Number = i + 1, Q = q, IsFocused = i == _focused && q.Answer == null,
            Plan = State.Sheet?.Plans.FirstOrDefault(p => p.N == q.PlanN),
        }).ToList();
        public string Gate2NextLabel => State.Questions.Count == 0 ? "Next: everything else" : "Next: questions";
        public int CandidateCount => Math.Max(State.Engine.CandidateCount, State.Questions.Count);
        public int Overflow => CandidateCount - Questions.Count;
        public int FocusedIndex => _focused;
        public bool CanContinue => State.Answered == State.Questions.Count;
        public string ContinueLabel => CheckRows.ContinueLabel(State);
        public string AnsweredBanner => CheckRows.AnsweredBanner(State);
        public string AnswerSummary => CheckRows.AnswerSummary(State);
        public List<CheckRow> Rows => _rows ??= State.Sheet == null ? new List<CheckRow>() : CheckRows.Everything(State);
        private List<CheckRow> _rows;
        public string BuildLabel => CheckRows.BuildLabel(State);
        public bool CanBuild => State.CanBuild;
        public string StatusLine => CheckRows.StatusLine(State);
        public List<string> Levels => State.Target.Levels.OrderBy(l => l.ElevationMm).Select(l => l.Name).ToList();
        public bool EditingMapping { get; private set; }

        private void Changed(bool rows = false)
        {
            if (rows) _rows = null;
            OnPropertyChanged(string.Empty);   // refresh every derived property: one state, live counts
        }

        // ── commands ──

        /// <summary>Read a DWG off the UI thread. The screen is Loading until the read returns.</summary>
        public async Task OpenAsync(string path, TargetInfo target)
        {
            _view?.RemoveUnderlay();    // another file: the old preview link goes
            _path = path;
            int seq = ++_openSeq;
            State = new CheckState { Screen = CheckScreen.Loading, Target = target ?? new TargetInfo() };
            Changed(rows: true);
            try
            {
                SheetSnapshot sheet = await Task.Run(() => _read(path)).ConfigureAwait(true);
                if (seq != _openSeq) return;      // a newer open won (also the same file opened twice)
                State = CheckState.For(sheet, target, _shortcuts);
                State.WallHeightMm = _wallHeightMm;
                if (!State.Target.Workshared)   // no preview link in a workshared model (owner, 2026-11-27)
                {
                    PlanMap main = State.Plans.FirstOrDefault(p => p.IsMain);
                    if (main != null) _view?.ShowUnderlay(path, sheet.UsedMmPerUnit, main.Plan.Box, sheet.Extents);
                }
            }
            catch (Exception ex)
            {
                if (seq != _openSeq) return;
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                State = new CheckState { Screen = CheckScreen.Error, Error = root.Message, Target = target ?? new TargetInfo() };
            }
            Changed(rows: true);
        }

        /// <summary>For the harness and tests: show a snapshot without reading.</summary>
        public void Show(SheetSnapshot sheet, TargetInfo target)
        {
            State = CheckState.For(sheet, target, _shortcuts);
            State.WallHeightMm = _wallHeightMm;
            Changed(rows: true);
        }

        public void Retry() { if (_path != null) _ = OpenAsync(_path, State.Target); }

        public void Next()
        {
            switch (State.Screen)
            {
                case CheckScreen.Preflight: State.Screen = CheckScreen.Consultant; break;
                case CheckScreen.Consultant:
                    State.ConsultantConfirmed = State.Engine.Consultant != null;
                    State.Screen = CheckScreen.Gate1; break;
                case CheckScreen.Gate1:
                    State.Gate1 = GateState.Passed; EditingMapping = false;
                    State.Screen = CheckScreen.Gate2; break;
                case CheckScreen.Gate2:
                    if (State.Gate2 == GateState.Blocked) return;     // can't skip
                    State.Gate2 = GateState.Passed;
                    State.Screen = State.Questions.Count == 0 ? CheckScreen.Rows : CheckScreen.Questions; break;
                case CheckScreen.Questions:
                    if (!CanContinue) return;
                    State.Screen = CheckScreen.Rows; break;
                default: return;
            }
            Changed(rows: true);
        }

        public void Back()
        {
            CheckScreen prev = State.Screen switch
            {
                CheckScreen.Consultant => CheckScreen.Preflight,
                CheckScreen.Gate1 => CheckScreen.Consultant,
                CheckScreen.Gate2 => CheckScreen.Gate1,
                CheckScreen.Questions => CheckScreen.Gate2,
                CheckScreen.Rows => State.Questions.Count == 0 ? CheckScreen.Gate2 : CheckScreen.Questions,
                _ => State.Screen,
            };
            if (prev == State.Screen) return;
            State.Screen = prev;
            Changed(rows: true);
        }

        public void ToggleEditMapping() { EditingMapping = !EditingMapping; Changed(); }

        /// <summary>Only an existing level can be chosen; the pane never creates one.</summary>
        public void Map(PlanMap map, string level)
        {
            if (map == null || !State.Target.Levels.Any(l => l.Name == level)) return;
            map.TargetLevel = level;
            map.Mapped = true;          // mapping a plan is what makes a non-main plan build
            Changed(rows: true);
        }

        public void ZoomTo(PlanMap map) { if (map != null) _view?.ZoomTo(map.Plan.Box); }

        public void Focus(Question q)
        {
            _focused = State.Questions.IndexOf(q);
            var plan = State.Sheet?.Plans.FirstOrDefault(p => p.N == q?.PlanN);
            if (plan != null) _view?.ZoomTo(plan.Box);
            Changed();
        }

        /// <summary>Keys 1-3 on the questions screen focus that card (and zoom the view to it).</summary>
        public bool FocusNumber(int n)
        {
            if (State.Screen != CheckScreen.Questions || n < 1 || n > Questions.Count) return false;
            Focus(Questions[n - 1]);
            return true;
        }

        public void Answer(Question q, string option)
        {
            if (q == null || !q.Options.Contains(option)) return;
            q.Answer = option;
            int next = State.Questions.FindIndex(x => x.Answer == null);
            _focused = next;
            Changed();
        }

        public void ToggleRow(CheckRow row)
        {
            if (row == null || !row.HasDetail) return;
            row.Expanded = !row.Expanded;
            _rows = _rows?.ToList();   // a new list instance, so the ItemsControl re-templates the row
            Changed();
        }

        public void Build() { if (State.CanBuild) BuildRequested?.Invoke(State); }

        /// <summary>Cancel / pane closed: drop the preview link.</summary>
        public void Close() => _view?.RemoveUnderlay();
    }
}
