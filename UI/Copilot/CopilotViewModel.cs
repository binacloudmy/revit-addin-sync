using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
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

        /// <summary>Returns the user's natural-language request for the current run.
        /// Fed to the self-heal retry so a regeneration keeps the right grounding.</summary>
        Func<string> PromptProvider { get; set; }

        /// <summary>Called with the attempt number when the self-heal loop retries
        /// after a compile/runtime failure (drives the "fixing… (attempt N)" line).</summary>
        Action<int> OnRetryProgress { get; set; }
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
            ClearChatCommand = new RelayCommand(_ =>
            {
                Thread.Clear();
                _currentSession = null;
                (Router as RevitChatRouter)?.ResetSession();
                Tab = CpTab.Chat;   // "+" from the History tab must land on the new empty chat
            });
            ClearHighlightsCommand = new RelayCommand(_ => Highlights.Clear());
            ChatSendCommand = new RelayCommand(ChatSendAny);
            FollowUpCommand = new RelayCommand(ChatSendAny);
            CancelSendCommand = new RelayCommand(_ => CancelSend());
            ChatRunCommand = new RelayCommand(p => ChatRun(p as ChatMessage));
            ChatRegenerateCommand = new RelayCommand(p => ChatRegenerate(p as ChatMessage));
            ChatOpenEditorCommand = new RelayCommand(p => OpenTool((p as ChatMessage)?.ToolId));
        }

        // ─── Injected context ────────────────────────────────────────────────
        public ICopilotExecutor Executor { get; set; }
        public IChatRouter Router { get; set; }
        public string UserFirstName { get; set; } = "there";
        public string ModelName { get; set; } = "Main Model";

        /// <summary>Raised when the user taps the inline "rate" nudge under a
        /// reply. The panel listens and slides up the Rate sheet (which owns the
        /// star UI + persistence). Kept as an event so screens don't reach up
        /// into the panel's chrome directly.</summary>
        public event Action RateRequested;
        public void RequestRate() => RateRequested?.Invoke();

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

        /// <summary>The user prompt that produced the response currently on screen.
        /// Set on every send (chat + tool-form run) so the 👍/👎 control can key
        /// feedback to the prompt it refers to (the backend retrieves recipes by
        /// prompt similarity, so feedback without it is unattributable).</summary>
        public string LastPrompt { get; private set; }

        private System.Diagnostics.Stopwatch _runClock;
        private string _lastRunElapsed = "";
        public string LastRunElapsed { get => _lastRunElapsed; private set { _lastRunElapsed = value; Raise(); } }

        public ObservableCollection<ChatMessage> Thread { get; } = new ObservableCollection<ChatMessage>();
        public ObservableCollection<HistoryEntry> History { get; }
        public ObservableCollection<HighlightMarker> Highlights { get; } = new ObservableCollection<HighlightMarker>();

        // ─── Model-mirror indexing (Copilot open → seed → then prompt) ────────
        private bool _isIndexing;
        public bool IsIndexing { get => _isIndexing; private set { if (_isIndexing == value) return; _isIndexing = value; Raise(); } }

        private int _indexProgress;
        public int IndexProgress { get => _indexProgress; private set { if (_indexProgress == value) return; _indexProgress = value; Raise(); Raise(nameof(IndexProgressBarWidth)); } }

        /// <summary>Fill width (px) for the indexing progress bar — 0..220 mapped
        /// from IndexProgress (0..100). Drives a plain Border, no animation.</summary>
        public double IndexProgressBarWidth => 220.0 * _indexProgress / 100.0;

        // Overlay title + subtitle (the full-pane "Preparing model" gate). Default
        // to the mirror-seeding copy; the warm-up phase overrides them.
        private string _indexTitle = "Indexing your model…";
        public string IndexTitle { get => _indexTitle; private set { if (_indexTitle == value) return; _indexTitle = value; Raise(); } }
        private string _indexSubtitle = "One moment — getting your model ready so I can answer instantly.";
        public string IndexSubtitle { get => _indexSubtitle; private set { if (_indexSubtitle == value) return; _indexSubtitle = value; Raise(); } }

        // ─── In-process tool activity (tunnel mutations on Revit's main thread) ──
        // Set when a mutation tool dispatches to Revit, cleared on completion, so
        // the pane shows "applying create_wall…" during a slow cold-model build
        // instead of a frozen, silent wait. The tunnel calls these from its
        // background read thread, so they marshal to the UI thread (AddAi pattern).
        private string _toolActivity = "";
        public string ToolActivity
        {
            get => _toolActivity;
            private set { if (_toolActivity == value) return; _toolActivity = value; Raise(); }
        }

        /// <summary>Show "applying &lt;tool&gt;…" in the pane chrome (UI-thread safe).</summary>
        public void SetToolActivity(string tool)
        {
            var text = string.IsNullOrEmpty(tool) ? "applying…" : $"applying {tool}…";
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => ToolActivity = text);
            else ToolActivity = text;
        }

        /// <summary>Clear the tool-activity indicator (UI-thread safe).</summary>
        public void ClearToolActivity()
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => ToolActivity = "");
            else ToolActivity = "";
        }

        /// <summary>DEPRECATED (2026-06-08) — NO-OP. Seeded the cloud model
        /// mirror on pane open ("Preparing your model… 48%"). The mirror is no
        /// longer read by any live path: inspect tools run natively in Revit
        /// (external_execution) or answer from the live snapshot, and backend
        /// reads are gated behind VIBE_MIRROR_READS (=0). Seeding it just delayed
        /// first-open by up to a minute for zero benefit, so it's disabled. The
        /// body below is retained (parked) for when the zero-round-trip mirror
        /// read path is wired up; flip the early-return to re-enable.</summary>
        public async Task EnsureMirrorSeededAsync()
        {
            // DEPRECATED mirror seed — disabled. See summary above.
            await Task.CompletedTask;
            return;
#pragma warning disable CS0162 // unreachable (parked for future re-enable)
            try
            {
                var cfg = BinaConfig.Load();
                if (cfg == null || cfg.ProjectId <= 0 || string.IsNullOrEmpty(cfg.AccessToken))
                    return;  // not logged in / no project → nothing to seed

                var ai = new AIService();
                var status = await ai.GetSeedStatusAsync(cfg.ProjectId, cfg.AccessToken);
                if (status != null && status.Warm)
                    return;  // mirror already warm — no indicator needed

                IsIndexing = true;
                IndexProgress = status?.Progress ?? 0;
                AddAi(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Thinking,
                    Text = "Indexing your model so I can answer instantly… one moment.",
                });

                // Poll until warm (~30s budget: 20 × 1.5s). StartSeedAsync is
                // retried each round (idempotent) so a tunnel that connects a
                // moment after the pane opens still kicks off the seed — the
                // user no longer has to type first.
                bool warm = false;
                for (int i = 0; i < 20; i++)
                {
                    await ai.StartSeedAsync(cfg.ProjectId, cfg.UserId, cfg.AccessToken);
                    await Task.Delay(1500);
                    status = await ai.GetSeedStatusAsync(cfg.ProjectId, cfg.AccessToken);
                    if (status != null) IndexProgress = status.Progress;
                    if (status != null && status.Warm) { warm = true; break; }
                }

                IsIndexing = false;
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = warm
                        ? "Model indexed — ask me anything."
                        : "Still indexing in the background — you can ask now; the first few answers may be a little slower.",
                });
            }
            catch
            {
                IsIndexing = false;  // never trap the user behind a failed seed
            }
#pragma warning restore CS0162
        }

        /// <summary>On pane open (after mirror seeding): hold the send gate until
        /// the Revit model is warm — i.e. the add-in's warm-up edit has paid the
        /// one-time ~70s first regen — so the user's first BUILD runs warm (~60ms)
        /// instead of freezing on the cold regen. Reuses IsIndexing (which gates
        /// ChatSend). Best-effort: on timeout the gate releases and the first
        /// build just pays the cost itself. Runs on the UI thread (same thread as
        /// the warm-up ExternalEvent) so the await yields let the warm-up run.</summary>
        public async Task EnsureModelWarmAsync()
        {
            // DISABLED (2026-06-08) — no-op. This pre-paid Revit's ~58s first
            // regeneration on pane open ("Preparing your model for editing… 48%")
            // so the FIRST model edit wouldn't freeze. But it blocked pane-open
            // for every session, including read-only Q&A that never edits. Off for
            // now: instant pane open. TRADE-OFF: the first mutate/codegen edit in a
            // session pays the ~58s cold regen itself (frozen, no progress). Parked
            // body below — flip the early-return (or make it lazy: warm right
            // before the first edit) to re-enable.
            await Task.CompletedTask;
            return;
#pragma warning disable CS0162 // unreachable (parked for re-enable / lazy warmup)
            try
            {
                if (RevitWebAppSync.App.VibeModelWarm) return;  // already warm

                // Take over the full-pane overlay: input is disabled (the overlay
                // is hit-test opaque) and we show an honest, phased "Preparing
                // model" progress. Note: during the actual heavy regen the UI
                // thread freezes, so the bar stops mid-progress — that frozen
                // "Preparing… 90%" reads as "working", which is the intent.
                IndexTitle = "Preparing your model for editing…";
                IndexSubtitle = "Loading the model — the first open can take up to a minute.";
                IndexProgress = 0;
                IsIndexing = true;

                // Budget covers a cold open (~77s) + first regen (~71s) + margin.
                bool warm = false;
                for (int i = 0; i < 160; i++)
                {
                    if (RevitWebAppSync.App.VibeModelWarm) { warm = true; break; }

                    if (RevitWebAppSync.App.VibeWarmupStarted)
                    {
                        // Heavy phase: warming geometry (50→95%).
                        IndexSubtitle = "Warming up geometry so your first edit is instant…";
                        if (IndexProgress < 50) IndexProgress = 50;
                        else if (IndexProgress < 95) IndexProgress += 2;
                    }
                    else
                    {
                        // Loading phase: model still opening (0→48%).
                        if (IndexProgress < 48) IndexProgress += 2;
                    }
                    await Task.Delay(1000);
                }

                IndexProgress = 100;
                IsIndexing = false;
                if (warm)
                    AddAi(new ChatMessage
                    {
                        Role = "ai", Kind = CpMsgKind.AiReply,
                        Text = "Model ready — your edits will be instant now.",
                    });
            }
            catch
            {
                IsIndexing = false;  // never trap the user behind warm-up
            }
#pragma warning restore CS0162
        }

        /// <summary>Add an AI message to the thread, marshalling to the UI thread.</summary>
        private void AddAi(ChatMessage m)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => Thread.Add(m));
            else Thread.Add(m);
        }

        private readonly HashSet<string> _pinned;
        public IReadOnlyCollection<string> Pinned => _pinned;

        private HistoryEntry _currentSession = null;

        private void AppendToCurrentSession(string userText, string botText,
            string status = "ok", List<string> toolIds = null, List<HistoryFile> userFiles = null)
        {
            string time = DateTime.Now.ToString("h:mm tt");
            if (_currentSession == null)
            {
                _currentSession = new HistoryEntry(
                    DateTime.Now.ToString("MMM d, h:mm tt"), status, userText,
                    new List<History>());
                History.Insert(0, _currentSession);
            }
            _currentSession.History.Add(new History("user", userText, time) { Files = userFiles });
            _currentSession.History.Add(new History("bot", botText, time, toolIds));
            if (status == "warn") _currentSession.Status = "warn";
            int exchanges = _currentSession.History.Count(m => m.Sender == "user");
            _currentSession.Summary = exchanges == 1 ? userText : $"{exchanges} messages";
            int idx = History.IndexOf(_currentSession);
            if (idx >= 0) History[idx] = _currentSession;
            CopilotStateStore.Save(_pinned, History);
            Raise(nameof(RecentEntry));
        }

        private void StartNewChat()
        {
            Thread.Clear();
            _currentSession = null; // next exchange begins a fresh HistoryEntry
        }

        public void RenameHistoryEntry(HistoryEntry entry, string newLabel)
        {
            if (entry == null) return;
            entry.Label = string.IsNullOrWhiteSpace(newLabel) ? null : newLabel.Trim();
            CopilotStateStore.Save(_pinned, History);
        }

        public void DeleteHistoryEntry(HistoryEntry entry)
        {
            if (entry == null) return;
            if (History.Remove(entry))
            {
                CopilotStateStore.Save(_pinned, History);
                Raise(nameof(RecentEntry));
            }
        }

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
        public RelayCommand CancelSendCommand { get; }

        // True while a reply is in flight (the RouteAsync window). Bound to the
        // PromptBar so the send button becomes a Stop button the user can click
        // to cancel the prompt mid-reply.
        private bool _isSending;
        public bool IsSending { get => _isSending; set { _isSending = value; Raise(); } }

        // Live step trail state for the CURRENT turn. _lastSteps is the latest
        // typed-steps snapshot from RevitChatRouter.OnSteps (null until the
        // backend emits at least one steps frame — older backends never set
        // this, which is how the legacy OnProgress fallback below detects it
        // should keep driving the trail). _streamText mirrors whatever reply
        // prose OnCodeStream has accumulated so far, so OnSteps can keep the
        // growing answer visible under the trail instead of blanking it.
        // Both reset at the start of every turn (ChatSend) — never mid-turn,
        // so a late OnProgress tick from an old-style backend can't clobber
        // a newer OnSteps-driven trail once one has arrived this turn.
        private IReadOnlyList<ProgressStep> _lastSteps;
        private string _streamText;

        /// <summary>User clicked Stop — abort the streaming reply. The router's
        /// RouteAsync then returns a "Cancelled." result which resolves the bubble;
        /// IsSending clears in ResolveProposalAsync's finally.</summary>
        public void CancelSend()
        {
            try { (Router as RevitChatRouter)?.CancelStream(); } catch { /* already done */ }
        }
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
            // Tool-form path has no free-text prompt — the tool title is what the
            // response answers, so feedback keys to that.
            LastPrompt = tool.Title;
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
            AppendToCurrentSession(tool.Title, summary, status, new List<string> { tool.Id });

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

        /// <summary>Command entry: the PromptBar sends a PromptPayload when
        /// screenshots are attached; chips/follow-ups still send a plain string.</summary>
        private void ChatSendAny(object p)
        {
            if (p is PromptPayload pp) ChatSend(pp.Text, pp.ImagesBase64, pp.Files);
            else ChatSend(p as string);
        }

        public void ChatSend(string text, List<string> images = null, List<FileAttachment> files = null)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            LastPrompt = text;   // key for 👍/👎 feedback on the resulting response
            if (IsIndexing)
            {
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = "Indexing your model — give me a couple seconds, then ask again.",
                });
                return;
            }
            Tab = CpTab.Chat;
            Screen = CpScreen.Home;
            ToolId = null;
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text, ImagesBase64 = images, Files = files, Time = System.DateTime.Now.ToString("h:mm tt") });

            // The chat bubble + history use `text` (what the user typed). The
            // backend route text re-embeds any attached file contents — there's no
            // separate file channel, so files ride along inside the prompt string.
            string routeText = BuildRouteText(text, files);

            // Lightweight projection persisted with the run history so the chip can
            // be redrawn in the History tab (name + line count; contents not stored).
            List<HistoryFile> historyFiles = files == null || files.Count == 0
                ? null
                : files.Select(f => new HistoryFile(f.Name, LineCount(f.Content))).ToList();

            // Intent detection runs on the user's text only, so file contents can't
            // falsely match tool keywords in PickResponseTool.
            var interp = QueryInterpreter.Interpret(text);
            if (interp.IsClarify)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Clarify, Question = interp.Question, Options = interp.Options });
                AppendToCurrentSession(text, interp.Question ?? "Needs clarification", userFiles: historyFiles);
                return;
            }

            // Auth gate: BINA Copilot needs a signed-in BINA Cloud session. Show a friendly
            // prompt instead of letting the request 401 at the backend.
            var authCfg = BinaConfig.Load();
            if (authCfg == null || !authCfg.IsLoggedIn())
            {
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = "Please sign in to use BINA Copilot — click BINA Cloud → Login in the ribbon, then try again.",
                });
                return;
            }

            // Local vetted-tool clarify intercept — DISABLED (vetted tools removed).
            // It answered vague prompts with canned tool chips BEFORE the backend was
            // ever reached. All messages now route to bina-ai; when the agent needs
            // detail it pauses with its own clarifying question (HITL card below).
            // var interp = QueryInterpreter.Interpret(text);
            // if (interp.IsClarify)
            // {
            //     Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Clarify, Question = interp.Question, Options = interp.Options });
            //     AppendToCurrentSession(text, interp.Question ?? "Needs clarification");
            //     return;
            // }

            _lastSteps = null;
            _streamText = null;
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Menganalisis permintaan…" });
            _ = ResolveProposalAsync(routeText, text, interp.ToolId, images, historyFiles);
        }

        /// <summary>Slash command sent from the composer. UI-only for now: adds the
        /// user's turn (a command chip, plus any typed args) and a placeholder reply
        /// — running tools from chat is wired later.</summary>
        public void ChatSendSlashCommand(SlashTool tool, string args)
        {
            if (tool == null) return;
            Tab = CpTab.Chat;
            Screen = CpScreen.Home;
            ToolId = null;
            string time = System.DateTime.Now.ToString("h:mm tt");
            Thread.Add(new ChatMessage
            {
                Role = "user", Kind = CpMsgKind.User, SlashCommand = tool,
                Text = (args ?? "").Trim(), Time = time,
            });
            Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply, Time = time,
                Text = $"**/{tool.Name}** is ready. Running tools directly from chat is coming soon — "
                     + "for now this is a placeholder so you can preview the flow.",
            });
        }

        /// <summary>Line count matching AttachmentChip's "N ln" display.</summary>
        private static int LineCount(string content) =>
            string.IsNullOrEmpty(content) ? 0 : content.Count(c => c == '\n') + 1;

        /// <summary>Compose the prompt string sent to the backend: each attached
        /// file's contents prepended as a labelled block, then the user's text.
        /// Returns `text` unchanged when nothing is attached. Format is kept
        /// byte-for-byte identical to the legacy PromptBar concatenation so the
        /// backend sees exactly the same input.</summary>
        private static string BuildRouteText(string text, List<FileAttachment> files)
        {
            if (files == null || files.Count == 0) return text;
            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                sb.Append("[Attached: ").Append(f.Name).AppendLine("]");
                sb.AppendLine(f.Content);
                sb.AppendLine("---");
            }
            sb.Append(text);
            return sb.ToString();
        }

        // Persistent header badge: "27 / 30 credits" or "Unlimited". Empty string hides the pill.
        private string _creditBadge = "";
        public string CreditBadge
        {
            get => _creditBadge;
            private set { if (_creditBadge == value) return; _creditBadge = value; Raise(); }
        }

        /// <summary>
        /// Fetch the signed-in user's monthly AI credit balance, post it as a chat message
        /// (the login greeting) AND set the persistent header badge. Called right after login.
        /// Best-effort: silently no-ops when not signed in or the backend is unavailable.
        /// </summary>
        public async System.Threading.Tasks.Task ShowCreditsAsync()
        {
            try
            {
                var credits = await FetchCreditsAsync();
                if (credits == null) return;
                SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit));
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = MessageText(credits) });
            }
            catch { /* best-effort — never block login on the credits read */ }
        }

        /// <summary>Re-fetch the balance and update ONLY the header badge (no chat message).
        /// Called after each prompt so the badge ticks down live.</summary>
        public async System.Threading.Tasks.Task RefreshCreditBadgeAsync()
        {
            try
            {
                var cfg = BinaConfig.Load();
                if (cfg == null || !cfg.IsLoggedIn()) { SetCreditBadge(""); return; }   // signed out → hide
                var credits = await new AIService().GetCreditsAsync(cfg.AccessToken);
                if (credits != null) SetCreditBadge(BadgeText(credits));                // keep last value on transient failure
            }
            catch { /* best-effort */ }
        }

        private static async System.Threading.Tasks.Task<AIService.CreditInfo> FetchCreditsAsync()
        {
            var cfg = BinaConfig.Load();
            if (cfg == null || !cfg.IsLoggedIn()) return null;
            return await new AIService().GetCreditsAsync(cfg.AccessToken);
        }

        private static string BadgeText(AIService.CreditInfo c)
        {
            if (c.Unlimited) return "Unlimited";
            if (c.Limit <= 0) return "";
            int remaining = c.Remaining ?? System.Math.Max(0, c.Limit - c.Used);
            return $"{remaining} / {c.Limit} credits";
        }

        private static string MessageText(AIService.CreditInfo c)
        {
            if (c.Unlimited) return "You have unlimited AI requests.";
            int remaining = c.Remaining ?? System.Math.Max(0, c.Limit - c.Used);
            string resets = string.IsNullOrWhiteSpace(c.ResetsAt) ? "" : $" Resets {c.ResetsAt}.";
            return $"You have {remaining} of {c.Limit} AI requests left this month.{resets}";
        }

        // UI-thread-safe badge setter (RefreshCreditBadgeAsync may resume off the UI thread).
        private void SetCreditBadge(string text)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => CreditBadge = text);
            else CreditBadge = text;
        }

        // ── Usage / plan (footer meter, popover, upgrade, blocked state) ─────
        /// <summary>Override for the credits-backed default — harness/tests inject a
        /// StubUsageService; a real billing adapter can replace it later.</summary>
        public Services.IUsageService UsageService { get; set; }

        private UsageState _usage = new UsageState();
        public UsageState Usage
        {
            get => _usage;
            private set { _usage = value ?? new UsageState(); Raise(); UsageChanged?.Invoke(); }
        }
        public event System.Action UsageChanged;

        /// <summary>Refresh the usage snapshot — from UsageService when injected,
        /// else from the credits balance. Best-effort; the meter never blocks chat.</summary>
        public async System.Threading.Tasks.Task RefreshUsageAsync()
        {
            try
            {
                if (UsageService != null) { SetUsage(await UsageService.GetAsync()); return; }
                var credits = await FetchCreditsAsync();
                if (credits != null)
                    SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit));
            }
            catch { /* best-effort */ }
        }

        private void SetUsage(UsageState u)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => Usage = u);
            else Usage = u;
        }

        private async System.Threading.Tasks.Task ResolveProposalAsync(string routeText, string displayText, string fallbackToolId, List<string> images = null, List<HistoryFile> historyFiles = null)
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
                    // Live step trail in the thinking bubble (▶ running, ✓ done,
                    // ✗ error) — backend-authored, BIMLogiq-style. Replaces the
                    // old "Drafting…" code preview: the step trail is the progress
                    // display now; the final reply lands in the answer bubble on
                    // completion.
                    // Once answer TEXT starts streaming (OnCodeStream), the reply
                    // takes over the bubble — late trail re-renders (review ticks,
                    // resume rounds) must not stomp the text the user is reading.
                    bool replyStreaming = false;
                    // ONE growing bubble (Claude-style). The backend streams a fresh
                    // reply per tool-loop round, but ToolLoopRunner ACCUMULATES them
                    // (round1 \n\n round2 \n\n …) so `cumulative` here is always the
                    // full running answer — we just render it into the single bubble.
                    // Legacy plain-text trail — only drives the bubble when this
                    // turn never got a typed steps frame (older backend). Once
                    // OnSteps has fired once, _lastSteps is non-null and this
                    // fallback goes quiet for the rest of the turn.
                    revitRouter.OnProgress = (trail) =>
                    {
                        if (replyStreaming || _lastSteps != null) return;
                        ReplaceLastThinking(new ChatMessage
                        {
                            Role = "ai", Kind = CpMsgKind.Thinking,
                            Text = string.IsNullOrEmpty(trail) ? "Thinking…" : trail,
                        });
                    };
                    // Typed live step trail (▶ running, ✓ done, ✗ error) — the
                    // multi-row trail rendered by ProgressTrailView. Marshaled the
                    // same way as ReplaceLastThinking itself (see its dispatcher
                    // check): ProgressStep objects are shared/mutable, but we only
                    // ever pass the reference along here — ProgressTrailView reads
                    // their current values synchronously on the UI thread inside
                    // Update, never binds to their INPC.
                    revitRouter.OnSteps = steps =>
                    {
                        _lastSteps = steps;
                        ReplaceLastThinking(new ChatMessage
                        {
                            Role = "ai", Kind = CpMsgKind.Thinking,
                            Text = _streamText ?? "",
                            StreamingReply = !string.IsNullOrEmpty(_streamText),
                            LiveSteps = steps,
                        });
                    };
                    revitRouter.OnCodeStream = (cumulative) =>
                    {
                        if (string.IsNullOrWhiteSpace(cumulative)) return;
                        replyStreaming = true;
                        _streamText = cumulative;
                        // StreamingReply: still Kind=Thinking so ReplaceLastThinking
                        // keeps growing THIS bubble, but flagged so ChatView renders
                        // the prose as the reply (markdown) — not the steps trail.
                        // LiveSteps carries along whatever trail has arrived so far
                        // so the ticking rows stay visible ABOVE the growing answer
                        // instead of disappearing the moment prose starts.
                        ReplaceLastThinking(new ChatMessage
                        {
                            Role = "ai", Kind = CpMsgKind.Thinking,
                            Text = cumulative,
                            StreamingReply = true,
                            LiveSteps = _lastSteps,
                        });
                    };
                }
                // Pasted screenshots ride along with this route only — the router
                // consumes and clears them when it builds the request (same
                // per-call pattern as OnProgress).
                if (revitRouter != null) revitRouter.PendingImages = images;
                IsSending = true;   // send button shows Stop until the reply resolves
                try { rr = await Router.RouteAsync(routeText, fallbackToolId); }
                catch { rr = null; }
                finally
                {
                    IsSending = false;
                    if (revitRouter != null)
                    {
                        revitRouter.OnProgress = null;
                        revitRouter.OnCodeStream = null;
                        revitRouter.OnSteps = null;
                    }
                }
            }

            // The credit was consumed by the backend during RouteAsync — refresh the
            // footer meter so usage ticks up live (best-effort).
            _ = RefreshUsageAsync();

            // HITL clarify pause: the agent needs an answer before acting. Render
            // the question as a Clarify card; the user's NEXT message is routed
            // back into the paused run by the router (resume-input), so no
            // option chips are required — they type the answer in the prompt bar.
            if (rr != null && rr.NeedsClarification && !string.IsNullOrWhiteSpace(rr.ClarifyingQuestion))
            {
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Clarify,
                    Question = rr.ClarifyingQuestion,
                    Options = new List<ClarifyOption>(),
                    Steps = rr.Steps,
                });
                return;
            }

            string toolId = !string.IsNullOrEmpty(rr?.ToolId) ? rr.ToolId : fallbackToolId;
            var tool = CopilotCatalog.Find(toolId) ?? CopilotCatalog.Find(fallbackToolId);
            if (tool == null) return;

            var plan = (rr?.PlanSteps != null && rr.PlanSteps.Count > 0) ? rr.PlanSteps : tool.Plan;
            string code = !string.IsNullOrWhiteSpace(rr?.Code) ? rr.Code : tool.Code;

            // Backend answered with NO code to run — a clarification ("which
            // view did you mean?"), an answer-from-context, or a refusal to
            // guess. Render rr.Reply as a chat bubble. Check rr.Code DIRECTLY,
            // not the `code` var above (which falls back to the catalog tool's
            // sample code and would mask this case → the empty response then
            // gets routed to the executor as "Couldn't build runnable code").
            if (rr != null && string.IsNullOrWhiteSpace(rr.Code)
                && !string.IsNullOrWhiteSpace(rr.Reply))
            {
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply, ToolId = tool.Id,
                    Text = !string.IsNullOrWhiteSpace(rr.Reply) ? rr.Reply : "Done.",
                    ToolCallTrace = rr.ToolCallTrace,
                    Steps = rr.Steps,
                    Verdict = rr.Verdict,
                    Interrupted = rr.Interrupted,
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
                var replyToolIds = (rr.ToolCallTrace != null && rr.ToolCallTrace.Count > 0) ? rr.ToolCallTrace : new List<string> { tool.Id };
                AppendToCurrentSession(displayText, rr.Reply ?? "Done", "ok", replyToolIds, historyFiles);
                return;
            }

            // AI-generated code (query OR action): auto-run and show the
            // result. Deletion is gated server-side (delete_elements →
            // approval card), so any C# the agent emits here is non-delete
            // and runs automatically — no Run button.
            if (rr != null && !string.IsNullOrWhiteSpace(rr.Code))
            {
                ExecuteAsChatReply(tool, rr.Code, routeText, displayText, historyFiles, rr.Reply);
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
            AppendToCurrentSession(displayText, text2, "ok", new List<string> { tool.Id }, historyFiles);
        }

        private void ExecuteAsChatReply(ToolDef tool, string code, string routePrompt = null, string displayPrompt = null, List<HistoryFile> historyFiles = null, string streamedReply = null)
        {
            ToolId = tool.Id;
            _runClock = System.Diagnostics.Stopwatch.StartNew();

            // Feed the user's actual request to the self-heal loop so a retry
            // regenerates WITH the right recipe/grounding (the backend retrieves
            // recipes from this prompt). Without it, ResolvePrompt falls back to
            // the catalog tool title and the fix loses context.
            if (Executor != null)
            {
                Executor.PromptProvider = () => routePrompt ?? tool?.Title ?? "";
                Executor.OnRetryProgress = attempt => ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Thinking,
                    Text = $"That didn't compile — fixing it… (attempt {attempt})",
                });
            }

            void Done(ExecOutcome outcome)
            {
                var result = BuildResult(tool, outcome);
                var resultText = FormatResultAsText(result, outcome);
                // ONE growing bubble: keep the model's streamed narration ("Tingkap
                // diasingkan… Sekarang warnakan…") and APPEND the code's result under
                // it, instead of REPLACING the narration with the result.
                var reply = (!string.IsNullOrWhiteSpace(streamedReply)
                             && streamedReply.Trim() != resultText.Trim())
                    ? streamedReply.Trim() + "\n\n" + resultText
                    : resultText;
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply, ToolId = tool.Id,
                    Text = reply,
                });
                AppendToCurrentSession(displayPrompt ?? tool.Title, reply, "ok", new List<string> { tool.Id }, historyFiles);
                PopulateHighlights(tool.Id);
            }

            if (Executor != null) Executor.Run(tool, new Dictionary<string, object>(), code, Done);
            else Done(new ExecOutcome { Success = true });
        }

        // ─── Feedback (👍/👎) ───────────────────────────────────────────────────
        // The only signal that catches "compiled but wrong". Fire-and-forget to the
        // backend, keyed to the prompt the rated response answered. Best-effort: a
        // failed POST must never break the pane (FeedbackService swallows).
        private readonly FeedbackService _feedback = new FeedbackService();

        /// <summary>Submit thumbs feedback for the response currently on screen.
        /// <paramref name="rating"/> is "up" or "down". Best-effort; never throws.</summary>
        public void SubmitFeedback(string rating) => SubmitFeedback(rating, LastPrompt);

        /// <summary>Submit thumbs feedback attributed to a SPECIFIC prompt.
        /// Chat reply bubbles pass their own source prompt so rating an older
        /// bubble isn't mis-attributed to whatever prompt is newest (LastPrompt).
        /// Best-effort; never throws.</summary>
        public void SubmitFeedback(string rating, string prompt) =>
            SubmitFeedback(rating, prompt, null, null);

        /// <summary>Thumbs feedback with the downvote panel's optional reason/note
        /// and the auto-attached version context. Best-effort; never throws.</summary>
        public void SubmitFeedback(string rating, string prompt, string reason, string note)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            var cfg = BinaConfig.Load();
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;
            string sessionId = (Router as RevitChatRouter)?.SessionId;
            string token = cfg?.AccessToken ?? "";

            // Detached — never await on the UI thread; failures are swallowed inside.
            _ = _feedback.SubmitFeedbackAsync(prompt, rating, prompt, sessionId, userId, token,
                reason: reason, note: note, context: CopilotContext.ShortLabel);
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

                string chatRunSummary = SummaryOf(result);
                AppendToCurrentSession(tool.Title, chatRunSummary, result.Kind == CpResultKind.Issues ? "warn" : "ok", new List<string> { tool.Id });
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
