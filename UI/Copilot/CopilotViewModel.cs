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
        public string Data;   // JSON of the snippet's real structured return (drives the card)
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
            ChatRunCommand = new RelayCommand(p => ChatRun(p as ChatMessage));
            ChatRegenerateCommand = new RelayCommand(p => ChatRegenerate(p as ChatMessage));
            ChatOpenEditorCommand = new RelayCommand(p => OpenTool((p as ChatMessage)?.ToolId));
        }

        // ─── Injected context ────────────────────────────────────────────────
        public ICopilotExecutor Executor { get; set; }
        public IChatRouter Router { get; set; }
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

        private System.Diagnostics.Stopwatch _runClock;
        private string _lastRunElapsed = "";
        public string LastRunElapsed { get => _lastRunElapsed; private set { _lastRunElapsed = value; Raise(); } }

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
        public RelayCommand ChatRunCommand { get; }
        public RelayCommand ChatRegenerateCommand { get; }
        public RelayCommand ChatOpenEditorCommand { get; }

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
            _runClock = System.Diagnostics.Stopwatch.StartNew();

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

            RunResult = BuildResult(tool, outcome);
            var result = RunResult;
            Screen = CpScreen.Result;

            string status = result.Kind == CpResultKind.Issues ? "warn" : (outcome != null && !outcome.Success ? "warn" : "ok");
            string summary = SummaryOf(result);
            History.Insert(0, new HistoryEntry("just now", tool.Id, status, summary));
            CopilotStateStore.Save(_pinned, History);
            Raise(nameof(RecentEntry));

            PopulateHighlights(tool.Id);
        }

        /// <summary>
        /// Build the result card. On a real run we render the snippet's actual model data
        /// (mapped from outcome.Data) — never the catalog mock. The catalog mock is used only
        /// in offline preview (no Revit executor wired).
        /// </summary>
        private ResultModel BuildResult(ToolDef tool, ExecOutcome outcome)
        {
            if (_runClock != null)
            {
                _runClock.Stop();
                LastRunElapsed = _runClock.Elapsed.TotalSeconds.ToString("0.0") + "s";
                _runClock = null;
            }

            if (outcome != null && !outcome.Success)
                return new ResultModel { Kind = CpResultKind.Plain, Headline = "Run failed", Sub = outcome.Error ?? "The operation did not complete." };

            if (Executor == null)
            {
                // Offline design preview only — no live model to query.
                return tool.Result != null ? tool.Result(FormValues) : new ResultModel { Kind = CpResultKind.Plain, Headline = tool.Title, Sub = "Done." };
            }

            // Real run — map the actual structured return; fall back to the status message.
            return CopilotResultMapper.Map(outcome?.Data, tool, outcome?.Message);
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

        // ─── Chat ──────────────────────────────────────────────────────────────
        public void ChatSend(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            Tab = CpTab.Chat;
            Screen = CpScreen.Home;
            ToolId = null;
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text });

            var interp = QueryInterpreter.Interpret(text);
            if (interp.IsClarify)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Clarify, Question = interp.Question, Options = interp.Options });
                return;
            }

            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Drafting a command for that…" });
            _ = ResolveProposalAsync(text, interp.ToolId);
        }

        private async System.Threading.Tasks.Task ResolveProposalAsync(string text, string fallbackToolId)
        {
            RouteResult rr = null;
            if (Router != null)
            {
                // Hook streaming if the concrete router supports it. The
                // backend's /generate/stream endpoint emits code chunks
                // every ~80 chars so the user sees code fill in token by
                // token instead of waiting for the full response.
                var revitRouter = Router as RevitChatRouter;
                if (revitRouter != null)
                {
                    revitRouter.OnCodeStream = (partial) =>
                    {
                        var snippet = partial.Length > 200
                            ? "Drafting…\n\n" + partial.Substring(0, 200) + "…"
                            : "Drafting…\n\n" + partial;
                        ReplaceLastThinking(new ChatMessage
                        {
                            Role = "ai", Kind = CpMsgKind.Thinking, Text = snippet,
                        });
                    };
                }
                try { rr = await Router.RouteAsync(text, fallbackToolId); }
                catch { rr = null; }
                if (revitRouter != null) revitRouter.OnCodeStream = null;
            }


            string toolId = !string.IsNullOrEmpty(rr?.ToolId) ? rr.ToolId : fallbackToolId;
            var tool = CopilotCatalog.Find(toolId) ?? CopilotCatalog.Find(fallbackToolId);
            if (tool == null) return;

            var plan = (rr?.PlanSteps != null && rr.PlanSteps.Count > 0) ? rr.PlanSteps : tool.Plan;
            string code = !string.IsNullOrWhiteSpace(rr?.Code) ? rr.Code : tool.Code;

            // Tool-calling agent answered directly — no C# to run.
            // Render the reply as a plain chat bubble with the tool
            // trace shown faintly underneath.
            if (rr != null && rr.IsQuery && string.IsNullOrWhiteSpace(code))
            {
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply, ToolId = tool.Id,
                    Text = !string.IsNullOrWhiteSpace(rr.Reply) ? rr.Reply : "Done.",
                    ToolCallTrace = rr.ToolCallTrace,
                    Verdict = rr.Verdict,
                });
                if (rr.ToolCallTrace != null && rr.ToolCallTrace.Count > 0)
                    History.Insert(0, new HistoryEntry("just now", tool.Id, "ok",
                        $"{rr.Reply} (used: {string.Join(" → ", rr.ToolCallTrace)})"));
                else
                    History.Insert(0, new HistoryEntry("just now", tool.Id, "ok", rr.Reply ?? "Done"));
                CopilotStateStore.Save(_pinned, History);
                Raise(nameof(RecentEntry));
                return;
            }

            // AI-generated code (query OR action): auto-run and show the
            // result. Deletion is gated server-side (delete_elements →
            // approval card), so any C# the agent emits here is non-delete
            // and runs automatically — no Run button.
            if (rr != null && !string.IsNullOrWhiteSpace(rr.Code))
            {
                ExecuteAsChatReply(tool, rr.Code);
                return;
            }

            // Only reached for catalog tier-2 sample code (tool.Code) — the
            // vetted library still shows a reviewable Proposal card.
            string text2 = !string.IsNullOrWhiteSpace(rr?.Reply)
                ? rr.Reply
                : "Here's a command that should do it. Review the plan and hit Run when you're ready.";

            ReplaceLastThinking(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.Proposal, ToolId = tool.Id,
                Text = text2, PlanSteps = new List<string>(plan ?? new List<string>()), Code = code,
            });
        }

        private void ExecuteAsChatReply(ToolDef tool, string code)
        {
            ToolId = tool.Id;
            _runClock = System.Diagnostics.Stopwatch.StartNew();

            void Done(ExecOutcome outcome)
            {
                var result = BuildResult(tool, outcome);
                var reply = FormatResultAsText(result, outcome);
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply, ToolId = tool.Id,
                    Text = reply,
                });
                History.Insert(0, new HistoryEntry("just now", tool.Id, "ok", reply));
                CopilotStateStore.Save(_pinned, History);
                Raise(nameof(RecentEntry));
                PopulateHighlights(tool.Id);
            }

            if (Executor != null) Executor.Run(tool, new Dictionary<string, object>(), code, Done);
            else Done(new ExecOutcome { Success = true });
        }

        /// <summary>Reformulate the structured result as one conversational line for the
        /// AiReply chat bubble. Falls back to the headline when no richer shape applies.</summary>
        private static string FormatResultAsText(ResultModel r, ExecOutcome outcome)
        {
            if (outcome != null && !outcome.Success)
                return "Sorry — that didn't run. " + (outcome.Error ?? "");
            if (r == null) return "Done.";
            switch (r.Kind)
            {
                case CpResultKind.Count:
                    return $"{r.Headline} {r.Unit}".Trim() + (string.IsNullOrEmpty(r.Sub) ? "" : $". {r.Sub}");
                case CpResultKind.Issues:
                    return $"{r.Headline} {r.Unit}".Trim();
                case CpResultKind.List:
                    return r.Headline; // diffs render in History; chat keeps the line tight
                case CpResultKind.File:
                    return $"Saved {r.Headline}." + (string.IsNullOrEmpty(r.Sub) ? "" : $" {r.Sub}");
                default:
                    return string.IsNullOrEmpty(r.Sub) ? r.Headline : $"{r.Headline} — {r.Sub}";
            }
        }

        private void ReplaceLastThinking(ChatMessage replacement)
        {
            void Apply()
            {
                for (int i = Thread.Count - 1; i >= 0; i--)
                {
                    if (Thread[i].Kind == CpMsgKind.Thinking) { Thread[i] = replacement; return; }
                }
                Thread.Add(replacement);
            }
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(Apply); else Apply();
        }

        public void ChatRun(ChatMessage msg)
        {
            if (msg == null) return;
            int idx = Thread.IndexOf(msg);
            if (idx < 0) return;
            var tool = CopilotCatalog.Find(msg.ToolId);
            if (tool == null) return;

            ToolId = tool.Id; // so highlights/history resolve against this tool
            _runClock = System.Diagnostics.Stopwatch.StartNew();
            Thread[idx] = new ChatMessage { Role = "ai", Kind = CpMsgKind.Running, ToolId = tool.Id, Code = msg.Code };

            void Done(ExecOutcome outcome)
            {
                var result = BuildResult(tool, outcome);

                int j = -1;
                for (int i = 0; i < Thread.Count; i++) if (Thread[i].Kind == CpMsgKind.Running && Thread[i].ToolId == tool.Id) j = i;
                if (j >= 0) Thread[j] = new ChatMessage { Role = "ai", Kind = CpMsgKind.Result, ToolId = tool.Id, Result = result };

                History.Insert(0, new HistoryEntry("just now", tool.Id, result.Kind == CpResultKind.Issues ? "warn" : "ok", SummaryOf(result)));
                CopilotStateStore.Save(_pinned, History);
                Raise(nameof(RecentEntry));
                PopulateHighlights(tool.Id);
            }

            if (Executor != null) Executor.Run(tool, new Dictionary<string, object>(), msg.Code, Done);
            else Done(new ExecOutcome { Success = true });
        }


        public void ChatRegenerate(ChatMessage msg)
        {
            if (msg == null) return;
            int idx = Thread.IndexOf(msg);
            if (idx < 0) return;

            var alts = CopilotCatalog.Ai.Where(a => a.Id != msg.ToolId).ToList();
            var next = alts.Count > 0 ? alts[new System.Random().Next(alts.Count)] : CopilotCatalog.Find(msg.ToolId);
            if (next == null) return;

            Thread[idx] = new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.Proposal, ToolId = next.Id,
                Text = "How about this instead?",
                PlanSteps = new List<string>(next.Plan ?? new List<string>()), Code = next.Code,
            };
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
