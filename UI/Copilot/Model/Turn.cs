#nullable enable
// One copilot turn as the pane sees it (AI-305, redesign plan item #21).
//
// WPF-free and Revit-free: System.ObjectModel only, so it compiles in
// Tests.csproj. A Turn is mutated ONLY by ChatSession (the reducer); the pane
// reads it and listens to PropertyChanged / Steps.CollectionChanged. Nothing
// here posts to a dispatcher: the caller marshals to the UI thread, the same
// rule ProgressStep already follows.
//
// One turn owns exactly one TurnEvidence. The old ChatMessage shape let an
// ask card and the reply that continued the turn share one instance, and the
// view had to work out who draws it (ChatView.OwnsEvidence). TurnProjection
// now puts it on one message by construction.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Where a turn is. Not the wire status (TurnEndStatus): that is
    /// mapped onto this by ChatSession.</summary>
    public enum TurnPhase
    {
        /// <summary>Started; thinking, running tools, or streaming the reply.</summary>
        Running,
        /// <summary>Parked on the assistant's question (the Ask card).</summary>
        AwaitingAnswer,
        Done,
        /// <summary>turn_end failed, or the turn could not run.</summary>
        Failed,
        /// <summary>turn_end limit: the turn stopped at its budget.</summary>
        Limit,
        /// <summary>The drafter pressed Stop, or the cloud cancelled or interrupted it.</summary>
        Interrupted,
    }

    /// <summary>A question the model asked, answered or not, with the text it
    /// wrote before asking (kept above the card).</summary>
    public sealed class TurnQuestion
    {
        public AskCardModel Card { get; }
        public string LeadText { get; }
        /// <summary>Position among the turn's questions (shared counter).</summary>
        public int Order { get; }
        public TurnQuestion(AskCardModel card, string leadText, int order) { Card = card; LeadText = leadText; Order = order; }
    }

    /// <summary>Where a drafter's edit of a result-card value is.</summary>
    public enum CellEditState
    {
        /// <summary>Typed, not sent to Revit. The text lives here, not in the view.</summary>
        Draft,
        /// <summary>The pane is writing it to Revit.</summary>
        Applying,
        /// <summary>Revit took it; the row's After shows the new value.</summary>
        Applied,
        /// <summary>The write failed; the text stays so the drafter can retry or discard it.</summary>
        Failed,
    }

    /// <summary>The drafter's edit of one value in the result card's Changes table:
    /// Result.Groups[Group].Rows[Row].After. Carries what the pane needs to write it
    /// (the group's element or type ids and the parameter) without working them out again.</summary>
    public sealed class CellEdit
    {
        public int Group { get; }
        public int Row { get; }
        public string TypeName { get; }
        public string Param { get; }
        /// <summary>True when Ids are type ids (a type parameter).</summary>
        public bool IsType { get; }
        public IReadOnlyList<long> Ids { get; }
        /// <summary>The row's After when this edit started.</summary>
        public string? Was { get; }
        public string Text { get; internal set; }
        public CellEditState State { get; internal set; }
        /// <summary>Why the write failed (State Failed); null otherwise.</summary>
        public string? Error { get; internal set; }

        internal CellEdit(int group, int row, TypeGroup g, ChangeRow r, string text)
        {
            Group = group; Row = row;
            TypeName = g.TypeName; Param = r.Param; IsType = g.IsType;
            Ids = g.Ids.ToList();
            Was = r.After;
            Text = text;
            State = CellEditState.Draft;
        }
    }

    public sealed class Turn : INotifyPropertyChanged
    {
        private readonly object _lock = new object();
        private readonly ObservableCollection<ProgressStep> _steps = new ObservableCollection<ProgressStep>();
        private readonly StringBuilder _reply = new StringBuilder();
        private readonly List<string> _notes = new List<string>();
        private readonly List<string> _toolsUsed = new List<string>();
        private readonly List<TurnQuestion> _questions = new List<TurnQuestion>();
        private readonly HashSet<string> _previewed = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<CellEdit> _edits = new List<CellEdit>();

        private int _legStart;
        private int _legShown;      // characters of the current leg already placed in an earlier message
        private bool _stepBoundary;
        private string? _lastStepId;
        private string? _lastProgressId;
        private string? _lastProgressKey;
        private int _dialogs;
        private int _order;

        private TurnPhase _state = TurnPhase.Running;
        private AskCardModel? _ask;
        private ResultCardModel? _result;
        private bool _undone;
        private bool _failed;
        private RouteFailure _failureKind;
        private string? _sessionId;

        public Turn(string id, string prompt, ReceiptLanguage language)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Prompt = prompt ?? "";
            SourcePrompt = Prompt;
            Language = language;
            Started = DateTime.Now;
        }

        // ---- identity (fixed at Begin) --------------------------------------------------

        public string Id { get; }
        /// <summary>What the drafter typed (the bubble text).</summary>
        public string Prompt { get; }
        /// <summary>What went to the cloud: the prompt plus attachment blocks or a slash command's template.</summary>
        public string SourcePrompt { get; internal set; }
        public ReceiptLanguage Language { get; }
        public DateTime Started { get; }
        /// <summary>The chat (router session epoch) the turn started under.</summary>
        public long SessionEpoch { get; internal set; }
        /// <summary>The snapshot the turn started with; never refreshed mid-turn.</summary>
        public SnapshotModel Snapshot { get; internal set; } = new SnapshotModel { Stale = true };
        /// <summary>Name of the attached file the result card credits ("source"), or null.</summary>
        public string? AttachmentName { get; internal set; }
        public IReadOnlyList<string> Images { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<FileAttachment> Files { get; internal set; } = Array.Empty<FileAttachment>();
        public SlashTool? Slash { get; internal set; }

        /// <summary>One TurnEvidence per turn. It is not replaced, only added to.</summary>
        public TurnEvidence Evidence { get; } = new TurnEvidence();

        // ---- state ----------------------------------------------------------------------

        public TurnPhase State => Read(() => _state);
        public bool IsFinished => Read(() => IsTerminal(_state));
        public static bool IsTerminal(TurnPhase p) =>
            p == TurnPhase.Done || p == TurnPhase.Failed || p == TurnPhase.Limit || p == TurnPhase.Interrupted;

        /// <summary>Every step row, the developer budget row included. The pane draws StepsSnapshot().</summary>
        public ObservableCollection<ProgressStep> Steps => _steps;

        /// <summary>The visible step rows now (no budget row), as a fresh list.</summary>
        public IReadOnlyList<ProgressStep> StepsSnapshot() => Read(() => (IReadOnlyList<ProgressStep>)ProgressTrail.UserVisible(_steps));

        /// <summary>All the model's text this turn (a blank line at each tool round, nothing else added).</summary>
        public string Reply => Read(() => _reply.ToString());

        /// <summary>The model's text since the last ask (all of it before one).</summary>
        public string LegReply => Read(() => _reply.ToString(_legStart, _reply.Length - _legStart));

        /// <summary>The current leg's text not yet placed in an earlier message (above a question card).</summary>
        public string UnshownReply => Read(() => Unshown(_reply.ToString(_legStart, _reply.Length - _legStart), _legShown));

        /// <summary>The assistant's question while the turn is parked on it; null otherwise.</summary>
        public AskCardModel? Ask => Read(() => _ask);

        /// <summary>The result card built from the envelopes when the turn ends; null when nothing resulted.</summary>
        public ResultCardModel? Result => Read(() => _result);

        /// <summary>Every question the turn asked, in order (answered ones stay as records).</summary>
        public IReadOnlyList<TurnQuestion> Questions => Read(() => (IReadOnlyList<TurnQuestion>)_questions.ToList());

        /// <summary>Pane-owned lines for this turn (cancelled, limit, failure), in order. Never the model's words.</summary>
        public IReadOnlyList<string> Notes => Read(() => (IReadOnlyList<string>)_notes.ToList());

        /// <summary>The note the pane shows under the reply: the last one.</summary>
        public string? PaneNote => Read(() => _notes.Count > 0 ? _notes[_notes.Count - 1] : null);

        /// <summary>The turn has nothing of the model's to show and ended badly: the pane shows a failure card.</summary>
        public bool Failed => Read(() => _failed);
        public RouteFailure FailureKind => Read(() => _failureKind);

        /// <summary>Distinct add-in tools the turn ran, in first-use order.</summary>
        public IReadOnlyList<string> ToolsUsed => Read(() => (IReadOnlyList<string>)_toolsUsed.Distinct().ToList());

        /// <summary>From turn_end; null until then.</summary>
        public string? SessionId => Read(() => _sessionId);

        /// <summary>The receipt's Undo ran for this turn.</summary>
        public bool Undone => Read(() => _undone);

        /// <summary>Undo is offered: the turn ended, a write committed, and it was not undone yet.</summary>
        public bool CanUndo => Read(() => IsTerminal(_state) && !_undone) && Evidence.AnyCommitted;

        /// <summary>The drafter's edits of result-card values, in the order they were started.</summary>
        public IReadOnlyList<CellEdit> CellEdits => Read(() => (IReadOnlyList<CellEdit>)_edits.ToList());

        /// <summary>The edit of Result.Groups[group].Rows[row], or null.</summary>
        public CellEdit? FindCellEdit(int group, int row) => Read(() => _edits.FirstOrDefault(e => e.Group == group && e.Row == row));

        public event PropertyChangedEventHandler? PropertyChanged;

        // ---- mutation (ChatSession only) ------------------------------------------------

        private T Read<T>(Func<T> f) { lock (_lock) return f(); }

        private void Change(Action mutate, params string[] props)
        {
            lock (_lock) mutate();
            var h = PropertyChanged;
            if (h == null) return;
            foreach (var p in props) h(this, new PropertyChangedEventArgs(p));
        }

        internal void SetState(TurnPhase s) => Change(() => _state = s, nameof(State), nameof(IsFinished), nameof(CanUndo));

        internal void SetSessionId(string? id) => Change(() => { if (!string.IsNullOrEmpty(id)) _sessionId = id; }, nameof(SessionId));

        internal void SetResult(ResultCardModel? r) => Change(() => _result = r, nameof(Result));

        internal void AddNote(string? note)
        {
            if (string.IsNullOrWhiteSpace(note)) return;
            Change(() => _notes.Add(note!), nameof(Notes), nameof(PaneNote));
        }

        internal void MarkFailed(RouteFailure kind) => Change(() => { _failed = true; _failureKind = kind; }, nameof(Failed), nameof(FailureKind));

        internal void MarkUndone() => Change(() => _undone = true, nameof(Undone), nameof(CanUndo));

        internal void AddTool(string tool) => Change(() => _toolsUsed.Add(tool), nameof(ToolsUsed));

        /// <summary>Appends; returns the cumulative text of the current leg. The first
        /// text after a tool request gets a blank line before it when the two steps
        /// would touch ("first.Saya"); never doubled. An empty delta keeps the boundary
        /// for the next text.</summary>
        internal string AppendReply(string delta)
        {
            string leg;
            lock (_lock)
            {
                if (_stepBoundary && delta.Length > 0)
                {
                    _stepBoundary = false;
                    if (_reply.Length > _legStart && !char.IsWhiteSpace(_reply[_reply.Length - 1]) && !char.IsWhiteSpace(delta[0]))
                        _reply.Append("\n\n");
                }
                _reply.Append(delta);
                leg = _reply.ToString(_legStart, _reply.Length - _legStart);
            }
            Raise(nameof(Reply)); Raise(nameof(LegReply)); Raise(nameof(UnshownReply));
            return leg;
        }

        internal void MarkStepBoundary() { lock (_lock) _stepBoundary = true; }

        /// <summary>Close the current leg (an ask): later text is a new leg.</summary>
        internal void StartNewLeg()
        {
            Change(() => { _legStart = _reply.Length; _legShown = 0; }, nameof(LegReply), nameof(UnshownReply));
        }

        /// <summary>The text above a question: the leg's text not yet shown. Does not move the leg.</summary>
        internal string TakeLead()
        {
            lock (_lock)
            {
                var lead = Unshown(_reply.ToString(_legStart, _reply.Length - _legStart), _legShown);
                return lead;
            }
        }

        internal void AddQuestion(AskCardModel card, string lead) =>
            Change(() => { _questions.Add(new TurnQuestion(card, lead, _order++)); _ask = card; }, nameof(Ask), nameof(Questions));

        internal void ClearAsk() => Change(() => _ask = null, nameof(Ask));

        private static string Unshown(string full, int shown) =>
            shown == 0 ? full : full.Length <= shown ? "" : full.Substring(shown).TrimStart();

        /// <summary>Runs <paramref name="mutate"/> on the edit list under the turn's lock;
        /// raises CellEdits when it returns true.</summary>
        internal bool ChangeEdits(Func<List<CellEdit>, bool> mutate)
        {
            bool changed;
            lock (_lock) changed = mutate(_edits);
            if (!changed) return false;
            Raise(nameof(CellEdits));
            return true;
        }

        /// <summary>A value in the result card changed in place (an applied cell edit).</summary>
        internal void ResultValuesChanged() => Raise(nameof(Result));

        internal int NextDialog() { lock (_lock) return ++_dialogs; }

        /// <summary>True from a batch's second preview on.</summary>
        internal bool PreviewAgain(string batchId) { lock (_lock) return !_previewed.Add(batchId); }

        internal void Step(string id, string phase, string label, StepState state, string? detail = null)
        {
            lock (_lock)
            {
                ProgressReducer.Apply(_steps, id, phase, label, detail ?? "", state);
                _lastStepId = id;
            }
        }

        /// <summary>A progress frame. One that repeats the previous step's kind and
        /// text (the cloud sends progress{thinking} every ~20 s) keeps its row.</summary>
        internal void Progress(long seq, string kind, string label, StepState state)
        {
            lock (_lock)
            {
                var key = kind + "\u0001" + label;
                if (_lastProgressId != null && _lastProgressKey == key && _lastStepId == _lastProgressId)
                {
                    ProgressReducer.Apply(_steps, _lastProgressId, kind, label, "", state);
                    return;
                }
                CloseProgressLocked();
                var id = "p:" + seq;
                ProgressReducer.Apply(_steps, id, kind, label, "", state);
                _lastStepId = _lastProgressId = id;
                _lastProgressKey = key;
            }
        }

        /// <summary>A new step supersedes the open progress rows.</summary>
        internal void CloseProgress() { lock (_lock) CloseProgressLocked(); }

        private void CloseProgressLocked()
        {
            foreach (var s in _steps)
                if (s.State == StepState.Running && (s.StepId.StartsWith("p:", StringComparison.Ordinal) || s.StepId == "thinking"))
                {
                    s.EndedUtc ??= DateTime.UtcNow;
                    s.State = StepState.Done;
                }
        }

        internal void Budget(string text)
        {
            lock (_lock)
            {
                ProgressReducer.Apply(_steps, "budget", "budget", text, "", StepState.Done);
                ProgressReducer.MoveStepToEnd(_steps, "budget");
            }
        }

        internal void CompleteSteps() { lock (_lock) ProgressReducer.CompleteRunning(_steps); }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
