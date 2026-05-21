using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Outcome of running a tool (real execution or offline simulation).</summary>
    public class ExecOutcome
    {
        public bool Success;
        public string Message;
        public string Error;
    }

    /// <summary>Pluggable executor — set by the panel (Task 7 wires it to the Revit ExternalEvent).</summary>
    public interface ICopilotExecutor
    {
        void Run(ToolDef tool, IDictionary<string, object> values, string code, Action<ExecOutcome> onDone);
    }

    /// <summary>
    /// Central Copilot state machine — a C# port of the prototype useReducer (app.jsx).
    /// Drives the panel chrome + all screen bodies via data binding.
    /// </summary>
    public class CopilotViewModel : INotifyPropertyChanged
    {
        public CopilotViewModel()
        {
            var st = CopilotStateStore.Load();
            _pinned = new HashSet<string>(st.Pinned);
            History = new ObservableCollection<HistoryEntry>(st.History);

            GoTabCommand = new RelayCommand(p => GoTab(ParseTab(p)));
            OpenToolCommand = new RelayCommand(p => OpenTool(p as string));
            BackCommand = new RelayCommand(_ => Back());
            BackHomeCommand = new RelayCommand(_ => BackHome());
            SetCategoryCommand = new RelayCommand(p => Category = p as string ?? "all");
            PinCommand = new RelayCommand(_ => Pin(ToolId));
            PinToolCommand = new RelayCommand(p => Pin(p as string));
            UnpinCommand = new RelayCommand(p => Unpin(p as string));
            RunCommand = new RelayCommand(_ => Run());
            CancelRunCommand = new RelayCommand(_ => CancelRun());
            ClearChatCommand = new RelayCommand(_ => Thread.Clear());
            ClearHighlightsCommand = new RelayCommand(_ => Highlights.Clear());
            ChatSendCommand = new RelayCommand(p => ChatSend(p as string));
            FollowUpCommand = new RelayCommand(p => ChatSend(p as string));
        }

        // ─── Injected context ────────────────────────────────────────────────
        public ICopilotExecutor Executor { get; set; }
        public string UserFirstName { get; set; } = "there";
        public string ModelName { get; set; } = "Main Model";

        // ─── State ───────────────────────────────────────────────────────────
        private CpScreen _screen = CpScreen.Home;
        public CpScreen Screen
        {
            get => _screen;
            set { if (_screen == value) return; _screen = value; Raise(); Raise(nameof(IsSubScreen)); Raise(nameof(ShowBreadcrumb)); }
        }

        private CpTab _tab = CpTab.Chat;
        public CpTab Tab { get => _tab; set { if (_tab == value) return; _tab = value; Raise(); } }

        private string _toolId;
        public string ToolId { get => _toolId; set { _toolId = value; Raise(); Raise(nameof(CurrentTool)); } }

        public ToolDef CurrentTool => CopilotCatalog.Find(_toolId);

        public Dictionary<string, object> FormValues { get; private set; } = new Dictionary<string, object>();

        private ResultModel _runResult;
        public ResultModel RunResult { get => _runResult; set { _runResult = value; Raise(); } }

        public ObservableCollection<ChatMessage> Thread { get; } = new ObservableCollection<ChatMessage>();
        public ObservableCollection<HistoryEntry> History { get; }
        public ObservableCollection<HighlightMarker> Highlights { get; } = new ObservableCollection<HighlightMarker>();

        private readonly HashSet<string> _pinned;
        public IReadOnlyCollection<string> Pinned => _pinned;

        private CpScreen _prev = CpScreen.Home;
        public CpScreen Prev { get => _prev; set { _prev = value; Raise(); } }

        private string _query = "";
        public string Query { get => _query; set { _query = value ?? ""; _category = "all"; Raise(); Raise(nameof(Category)); RaiseLibrary(); } }

        private string _category = "all";
        public string Category { get => _category; set { _category = value ?? "all"; Raise(); RaiseLibrary(); } }

        // ─── Derived ─────────────────────────────────────────────────────────
        public bool IsSubScreen =>
            Screen == CpScreen.ToolForm || Screen == CpScreen.ToolReview ||
            Screen == CpScreen.Running || Screen == CpScreen.Result;

        public bool ShowBreadcrumb => Screen == CpScreen.ToolForm || Screen == CpScreen.ToolReview;

        public string BreadcrumbRoot => Prev == CpScreen.Home && Tab == CpTab.Chat ? "Chat" : "Library";

        public int LibraryCount => CopilotCatalog.All.Count();
        public int SavedCount => _pinned.Count;
        public IEnumerable<CategoryDef> Categories => CopilotCatalog.Categories;
        public bool IsPinned(string toolId) => toolId != null && _pinned.Contains(toolId);

        public IEnumerable<ToolDef> VettedFiltered => CopilotCatalog.Vetted.Where(MatchesFilter);
        public IEnumerable<ToolDef> AiFiltered => CopilotCatalog.Ai.Where(MatchesFilter);
        public IEnumerable<ToolDef> SavedTools => CopilotCatalog.All.Where(t => _pinned.Contains(t.Id));
        public HistoryEntry RecentEntry => History.FirstOrDefault();

        private bool MatchesFilter(ToolDef t)
        {
            bool cat = _category == "all" || t.Category == _category;
            bool q = string.IsNullOrEmpty(_query)
                || (t.Title?.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0)
                || (t.Desc?.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);
            return cat && q;
        }

        // ─── Commands ────────────────────────────────────────────────────────
        public RelayCommand GoTabCommand { get; }
        public RelayCommand OpenToolCommand { get; }
        public RelayCommand BackCommand { get; }
        public RelayCommand BackHomeCommand { get; }
        public RelayCommand SetCategoryCommand { get; }
        public RelayCommand PinCommand { get; }
        public RelayCommand PinToolCommand { get; }
        public RelayCommand UnpinCommand { get; }
        public RelayCommand RunCommand { get; }
        public RelayCommand CancelRunCommand { get; }
        public RelayCommand ClearChatCommand { get; }
        public RelayCommand ClearHighlightsCommand { get; }
        public RelayCommand ChatSendCommand { get; }
        public RelayCommand FollowUpCommand { get; }

        private static CpTab ParseTab(object p)
        {
            if (p is CpTab t) return t;
            return Enum.TryParse(p as string ?? "Chat", true, out CpTab parsed) ? parsed : CpTab.Chat;
        }

        public void GoTab(CpTab tab)
        {
            Tab = tab;
            Screen = CpScreen.Home;
            ToolId = null;
            RunResult = null;
        }

        public void OpenTool(string toolId)
        {
            var tool = CopilotCatalog.Find(toolId);
            if (tool == null) return;
            Prev = Screen;
            ToolId = tool.Id;
            if (tool.Tier == 1)
            {
                FormValues = tool.Fields.ToDictionary(f => f.Id, f => f.Default);
                Raise(nameof(FormValues));
                Screen = CpScreen.ToolForm;
            }
            else
            {
                FormValues = new Dictionary<string, object>();
                Raise(nameof(FormValues));
                Screen = CpScreen.ToolReview;
            }
        }

        public void SetForm(string fieldId, object value)
        {
            FormValues[fieldId] = value;
            Raise(nameof(FormValues));
            // Re-evaluate the live preview/run-label bindings.
            Raise(nameof(CurrentTool));
        }

        public void Back()
        {
            Screen = Prev == CpScreen.Home ? CpScreen.Home : Prev;
            if (Screen == CpScreen.Home) { ToolId = null; RunResult = null; }
        }

        public void BackHome()
        {
            Screen = CpScreen.Home;
            ToolId = null;
            RunResult = null;
            Prev = CpScreen.Home;
        }

        public void Pin(string toolId)
        {
            if (string.IsNullOrEmpty(toolId)) return;
            if (_pinned.Add(toolId)) PersistAndRaisePinned();
        }

        public void Unpin(string toolId)
        {
            if (string.IsNullOrEmpty(toolId)) return;
            if (_pinned.Remove(toolId)) PersistAndRaisePinned();
        }

        private void PersistAndRaisePinned()
        {
            CopilotStateStore.Save(_pinned, History);
            Raise(nameof(SavedCount));
            Raise(nameof(SavedTools));
            Raise(nameof(Pinned));
        }

        // ─── Run / finish ────────────────────────────────────────────────────
        public void Run()
        {
            var tool = CurrentTool;
            if (tool == null) return;
            Prev = Screen;
            Screen = CpScreen.Running;

            string code = tool.Tier == 2 ? tool.Code : null; // vetted code synthesized in the executor

            if (Executor != null)
            {
                Executor.Run(tool, FormValues, code, FinishRun);
            }
            else
            {
                // Offline fallback (no Revit context) — simulate success with the catalog result shape.
                FinishRun(new ExecOutcome { Success = true, Message = null });
            }
        }

        public void CancelRun()
        {
            Screen = Prev == CpScreen.Running ? CpScreen.Home : Prev;
        }

        public void FinishRun(ExecOutcome outcome)
        {
            var tool = CurrentTool;
            if (tool == null) return;

            ResultModel result;
            if (outcome != null && !outcome.Success)
            {
                result = new ResultModel
                {
                    Kind = CpResultKind.Plain,
                    Headline = "Run failed",
                    Sub = outcome.Error ?? "The operation did not complete.",
                };
            }
            else
            {
                // Use the catalog result shape (faithful to design); real message refines the subtitle.
                result = tool.Result != null ? tool.Result(FormValues) : new ResultModel { Kind = CpResultKind.Plain, Headline = tool.Title, Sub = "Done." };
                if (outcome != null && !string.IsNullOrWhiteSpace(outcome.Message) && result.Kind == CpResultKind.Plain)
                    result.Sub = outcome.Message;
            }

            RunResult = result;
            Screen = CpScreen.Result;

            string status = result.Kind == CpResultKind.Issues ? "warn" : (outcome != null && !outcome.Success ? "warn" : "ok");
            string summary = SummaryOf(result);
            History.Insert(0, new HistoryEntry("just now", tool.Id, status, summary));
            CopilotStateStore.Save(_pinned, History);
            Raise(nameof(RecentEntry));

            PopulateHighlights(tool.Id);
        }

        private static string SummaryOf(ResultModel r)
        {
            if (r.Kind == CpResultKind.Count || r.Kind == CpResultKind.Issues)
                return $"{r.Headline} {r.Unit}";
            return r.Headline;
        }

        private void PopulateHighlights(string toolId)
        {
            // Real marker projection lands in Task 15; for now mirror the prototype's seed set.
            Highlights.Clear();
            foreach (var m in CopilotHighlights.For(toolId))
                Highlights.Add(m);
        }

        // ─── Chat (skeleton — routing/clarify wired in Tasks 12/13) ────────────
        public void ChatSend(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            Tab = CpTab.Chat;
            Screen = CpScreen.Home;
            ToolId = null;
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text });
            // Tier-2 routing + clarify cards are added in Tasks 12 & 13.
        }

        // ─── INotifyPropertyChanged ────────────────────────────────────────────
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseLibrary()
        {
            Raise(nameof(VettedFiltered));
            Raise(nameof(AiFiltered));
            Raise(nameof(RecentEntry));
        }
    }
}
