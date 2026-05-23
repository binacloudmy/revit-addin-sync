using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
        void RunCode(string code, Action<ExecOutcome> onDone);
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
            SavedCommands = new ObservableCollection<SavedCommand>(st.SavedCommands ?? new List<SavedCommand>());
            Sessions = new ObservableCollection<ChatSession>(st.Sessions ?? new List<ChatSession>());

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
            CancelRouteCommand = new RelayCommand(_ => CancelRoute());
            UngroupApplyCommand = new RelayCommand(_ => UngroupApply());
            ClearChatCommand = new RelayCommand(_ => NewSession());
            ClearHighlightsCommand = new RelayCommand(_ => Highlights.Clear());
            ChatSendCommand = new RelayCommand(p => ChatSend(p as string));
            FollowUpCommand = new RelayCommand(p => ChatSend(p as string));
            ChatRunCommand = new RelayCommand(p => ChatRun(p as ChatMessage));
            ChatRegenerateCommand = new RelayCommand(p => ChatRegenerate(p as ChatMessage));
            ChatOpenEditorCommand = new RelayCommand(p => OpenTool((p as ChatMessage)?.ToolId));
            SaveCurrentRunCommand = new RelayCommand(_ => SaveCurrentRun());
            SaveChatResultCommand = new RelayCommand(p => SaveChatResult(p as ChatMessage));
            DeleteSavedCommand = new RelayCommand(p => DeleteSaved(p as string));
            RunSavedCommand = new RelayCommand(p => RunSaved(p as SavedCommand));
            OpenSessionCommand = new RelayCommand(p => OpenSession(p as string));
            DeleteSessionCommand = new RelayCommand(p => DeleteSession(p as string));
            NewSessionCommand = new RelayCommand(_ => NewSession());
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
        public ObservableCollection<SavedCommand> SavedCommands { get; }
        public ObservableCollection<ChatSession> Sessions { get; }
        public ObservableCollection<HighlightMarker> Highlights { get; } = new ObservableCollection<HighlightMarker>();

        private readonly HashSet<string> _pinned;
        public IReadOnlyCollection<string> Pinned => _pinned;

        // ─── Routing state (spam guard + cancel) ─────────────────────────────
        private CancellationTokenSource _routeCts;
        private bool _isRouting;
        public bool IsRouting { get => _isRouting; private set { if (_isRouting == value) return; _isRouting = value; Raise(); } }

        // Current chat session (lazy — first ChatSend creates it). Persisted to Sessions on
        // NewSession/clear/leave.
        private ChatSession _currentSession;

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
        public int SavedCount => SavedCommands.Count;
        public int SessionCount => Sessions.Count;
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
        public RelayCommand CancelRouteCommand { get; }
        public RelayCommand UngroupApplyCommand { get; }
        public RelayCommand ClearChatCommand { get; }
        public RelayCommand ClearHighlightsCommand { get; }
        public RelayCommand ChatSendCommand { get; }
        public RelayCommand FollowUpCommand { get; }
        public RelayCommand ChatRunCommand { get; }
        public RelayCommand ChatRegenerateCommand { get; }
        public RelayCommand ChatOpenEditorCommand { get; }
        public RelayCommand SaveCurrentRunCommand { get; }
        public RelayCommand SaveChatResultCommand { get; }
        public RelayCommand DeleteSavedCommand { get; }
        public RelayCommand RunSavedCommand { get; }
        public RelayCommand OpenSessionCommand { get; }
        public RelayCommand DeleteSessionCommand { get; }
        public RelayCommand NewSessionCommand { get; }

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

        public void OpenTool(string toolId, IDictionary<string, object> prefill = null)
        {
            var tool = CopilotCatalog.Find(toolId);
            if (tool == null) return;
            Prev = Screen;
            ToolId = tool.Id;
            if (tool.Tier == 1)
            {
                FormValues = tool.Fields.ToDictionary(f => f.Id, f => f.Default);
                // Overlay any prefilled params BEFORE the screen flips, so the form (e.g. the
                // live view dropdown filtered by type) builds with the right values.
                if (prefill != null)
                    foreach (var kv in prefill) FormValues[kv.Key] = kv.Value;
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
            PersistAll();
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

        /// <summary>Opt-in: ungroup the groups holding the target elements, then set the parameter
        /// on everything. Destructive (dissolves those groups) — invoked only from the explicit
        /// "Ungroup & apply" action on the set-parameter result.</summary>
        public void UngroupApply()
        {
            var tool = CurrentTool;
            if (tool == null || tool.BackendName != "set_parameter" || Executor == null) return;
            string code = RevitWebAppSync.Services.VettedToolCode.BuildSetParameterUngroup(FormValues);
            if (string.IsNullOrEmpty(code)) return;
            Prev = Screen;
            Screen = CpScreen.Running;
            _runClock = System.Diagnostics.Stopwatch.StartNew();
            Executor.RunCode(code, FinishRun);
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
            PersistAll();
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
            // Spam guard: drop new sends while a route is in flight. The Cancel chip on the
            // Thinking message is the only way to free the channel.
            if (IsRouting) return;
            Tab = CpTab.Chat;

            // If the ask clearly maps to a vetted (deterministic) tool, open its form directly —
            // no backend /route call, no codegen tokens. The user confirms params and Runs.
            var vetted = QueryInterpreter.DetectVetted(text);
            if (vetted != null)
            {
                OpenTool(vetted.ToolId, vetted.Prefill);
                return;
            }

            Screen = CpScreen.Home;
            ToolId = null;
            EnsureSession();
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text });

            var interp = QueryInterpreter.Interpret(text);
            if (interp.IsClarify)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Clarify, Question = interp.Question, Options = interp.Options });
                PersistSession();
                return;
            }

            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Drafting a command for that…" });
            _ = ResolveProposalAsync(text, interp.ToolId);
        }

        public void CancelRoute()
        {
            try { _routeCts?.Cancel(); } catch { }
        }

        private async System.Threading.Tasks.Task ResolveProposalAsync(string text, string fallbackToolId)
        {
            RouteResult rr = null;
            _routeCts?.Dispose();
            _routeCts = new CancellationTokenSource();
            var ct = _routeCts.Token;
            IsRouting = true;
            try
            {
                if (Router != null)
                {
                    try { rr = await Router.RouteAsync(text, fallbackToolId, ct); }
                    catch (OperationCanceledException) { rr = null; }
                    catch { rr = null; }
                }
                if (ct.IsCancellationRequested)
                {
                    ReplaceLastThinking(new ChatMessage { Role = "ai", Kind = CpMsgKind.Note, Text = "Cancelled." });
                    PersistSession();
                    return;
                }
            }
            finally
            {
                IsRouting = false;
            }

            // Only propose when the backend actually answered with a plan/code. Do NOT fabricate
            // a proposal from the offline keyword pick (that's what mislabeled "count walls" as
            // the doors tool). No usable answer -> say so honestly.
            bool usable = rr != null && !rr.NeedsClarification
                          && (!string.IsNullOrWhiteSpace(rr.Code) || (rr.Plan != null && rr.Plan.Count > 0));

            if (!usable)
            {
                string note;
                if (rr != null && rr.NotAuthenticated)
                    note = "You're not signed in. Click the BINA Login button on the ribbon to sign in, then ask again.";
                else if (rr != null && rr.NeedsClarification && !string.IsNullOrWhiteSpace(rr.ClarifyingQuestion))
                    note = rr.ClarifyingQuestion;
                else
                    note = "I couldn't reach the Copilot backend. Check your connection and try again.";
                ReplaceLastThinking(new ChatMessage { Role = "ai", Kind = CpMsgKind.Note, Text = note });
                PersistSession();
                return;
            }

            string title = !string.IsNullOrWhiteSpace(rr.Intent) ? rr.Intent : CopilotCatalog.Find(rr.ToolId)?.Title;
            string text2 = !string.IsNullOrWhiteSpace(rr.Reply)
                ? rr.Reply
                : "Here's a command that should do it. Review the plan and hit Run when you're ready.";

            ReplaceLastThinking(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.Proposal,
                ToolId = rr.ToolId,           // icon + execution/history shape only
                Title = title,                // shown title — the real intent, not the doors pick
                Text = text2,
                PlanSteps = new List<string>(rr.Plan ?? new List<string>()),
                Code = rr.Code,
                SourcePrompt = text,          // original prompt, so Regenerate re-routes the real ask
            });
            PersistSession();
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
            Thread[idx] = new ChatMessage { Role = "ai", Kind = CpMsgKind.Running, ToolId = tool.Id, Title = msg.Title, Code = msg.Code };

            void Done(ExecOutcome outcome)
            {
                var result = BuildResult(tool, outcome);

                int j = -1;
                for (int i = 0; i < Thread.Count; i++) if (Thread[i].Kind == CpMsgKind.Running && Thread[i].ToolId == tool.Id) j = i;
                if (j >= 0) Thread[j] = new ChatMessage { Role = "ai", Kind = CpMsgKind.Result, ToolId = tool.Id, Title = msg.Title, Result = result };

                History.Insert(0, new HistoryEntry("just now", tool.Id, result.Kind == CpResultKind.Issues ? "warn" : "ok", SummaryOf(result)));
                PersistAll();
                Raise(nameof(RecentEntry));
                PopulateHighlights(tool.Id);
            }

            if (Executor != null) Executor.Run(tool, new Dictionary<string, object>(), msg.Code, Done);
            else Done(new ExecOutcome { Success = true });
            PersistSession();
        }

        public void ChatRegenerate(ChatMessage msg)
        {
            if (msg == null) return;
            int idx = Thread.IndexOf(msg);
            if (idx < 0) return;

            // Re-route the ORIGINAL prompt through the backend (a real regenerate), instead of
            // swapping in a random catalog demo tool.
            string prompt = msg.SourcePrompt;
            if (string.IsNullOrWhiteSpace(prompt)) return;
            if (IsRouting) return; // spam guard
            Thread[idx] = new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Regenerating…" };
            _ = ResolveProposalAsync(prompt, QueryInterpreter.PickResponseTool(prompt)?.Id);
        }

        // ─── Sessions (chat history) ──────────────────────────────────────────
        private void EnsureSession()
        {
            if (_currentSession != null) return;
            _currentSession = new ChatSession
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.Now.ToString("o"),
                Title = "(new chat)",
                Messages = new List<ChatMessage>(),
            };
        }

        private void PersistSession()
        {
            if (_currentSession == null) return;
            // Snapshot the live Thread (in display order). First user turn -> title.
            _currentSession.Messages = Thread.ToList();
            var firstUser = Thread.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Text));
            if (firstUser != null)
            {
                var t = firstUser.Text.Trim();
                _currentSession.Title = t.Length > 60 ? t.Substring(0, 60) + "…" : t;
            }
            // Replace-or-prepend.
            int idx = -1;
            for (int i = 0; i < Sessions.Count; i++) if (Sessions[i].Id == _currentSession.Id) { idx = i; break; }
            if (idx >= 0) Sessions[idx] = _currentSession;
            else Sessions.Insert(0, _currentSession);
            PersistAll();
            Raise(nameof(Sessions));
        }

        public void NewSession()
        {
            // Persist whatever we have, then drop reference & clear the thread.
            if (_currentSession != null && Thread.Count > 0) PersistSession();
            _currentSession = null;
            Thread.Clear();
            PersistAll();
        }

        public void OpenSession(string sessionId)
        {
            var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
            if (s == null) return;
            if (_currentSession != null && _currentSession.Id != s.Id) PersistSession();
            _currentSession = s;
            Thread.Clear();
            foreach (var m in s.Messages ?? new List<ChatMessage>()) Thread.Add(m);
            Tab = CpTab.Chat;
            Screen = CpScreen.Home;
        }

        public void DeleteSession(string sessionId)
        {
            var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
            if (s == null) return;
            Sessions.Remove(s);
            if (_currentSession?.Id == sessionId) { _currentSession = null; Thread.Clear(); }
            PersistAll();
        }

        // ─── Saved commands (re-runnable) ─────────────────────────────────────
        public void SaveCurrentRun()
        {
            // From the form path: snapshot tool + FormValues so re-run pre-fills the form.
            var tool = CurrentTool;
            if (tool == null) return;
            var paramsCopy = new Dictionary<string, object>(FormValues ?? new Dictionary<string, object>());
            string title = tool.Title;
            try { var rl = tool.RunLabel?.Invoke(paramsCopy); if (!string.IsNullOrWhiteSpace(rl)) title = rl; }
            catch { }
            var cmd = new SavedCommand
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                ToolId = tool.Id,
                Params = paramsCopy,
                Source = "form",
                CreatedAt = DateTime.Now.ToString("o"),
            };
            SavedCommands.Insert(0, cmd);
            PersistAll();
            Raise(nameof(SavedCount));
        }

        public void SaveChatResult(ChatMessage msg)
        {
            if (msg == null) return;
            string prompt = msg.SourcePrompt ?? msg.Title ?? msg.Text;
            if (string.IsNullOrWhiteSpace(prompt)) return;
            string title = !string.IsNullOrWhiteSpace(msg.Title) ? msg.Title : (prompt.Length > 60 ? prompt.Substring(0, 60) + "…" : prompt);
            var cmd = new SavedCommand
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Prompt = prompt,
                ToolId = msg.ToolId,
                Code = msg.Code,
                Source = "chat",
                CreatedAt = DateTime.Now.ToString("o"),
            };
            SavedCommands.Insert(0, cmd);
            PersistAll();
            Raise(nameof(SavedCount));
        }

        public void DeleteSaved(string id)
        {
            var cmd = SavedCommands.FirstOrDefault(x => x.Id == id);
            if (cmd == null) return;
            SavedCommands.Remove(cmd);
            PersistAll();
            Raise(nameof(SavedCount));
        }

        public void RunSaved(SavedCommand cmd)
        {
            if (cmd == null) return;
            if (cmd.Source == "chat" && !string.IsNullOrWhiteSpace(cmd.Prompt))
            {
                ChatSend(cmd.Prompt);
                return;
            }
            // Form path: open the tool with the saved params pre-filled.
            if (!string.IsNullOrEmpty(cmd.ToolId))
                OpenTool(cmd.ToolId, cmd.Params);
        }

        private void PersistAll()
        {
            CopilotStateStore.Save(_pinned, History, SavedCommands, Sessions);
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
