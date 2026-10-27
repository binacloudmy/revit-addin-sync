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
        public string TurnId; // host run id: the result's Undo is per run (W3's UndoService)
    }

    /// <summary>Runs a library tool form — the panel sets HostCopilotExecutor,
    /// which runs it through CopilotToolHost (preview + confirm card for writes).</summary>
    public interface ICopilotExecutor
    {
        void Run(ToolDef tool, IDictionary<string, object> values, string code, Action<ExecOutcome> onDone);
    }

    /// <summary>
    /// Central Copilot state machine — a C# port of the prototype useReducer (app.jsx).
    /// Drives the panel chrome + all screen bodies via data binding.
    /// </summary>
    public class CopilotViewModel : INotifyPropertyChanged, IConfirmCardView
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
                LeaveCurrentChat(adopt: false, sessionId: null);
                Thread.Clear();
                _currentSession = null;
                Tab = CpTab.Chat;   // "+" from the History tab must land on the new empty chat
                RunStatus = "Ready";
            });
            ClearHighlightsCommand = new RelayCommand(_ => Highlights.Clear());
            ChatSendCommand = new RelayCommand(ChatSendAny);
            FollowUpCommand = new RelayCommand(ChatSendAny);

            // Sign-in state (2026-08-27 sign-in states). The composer is NEVER
            // locked on load: locking only happens together with a Sign-in card
            // (SignInCard()), so there is always a button to reach. v0.0.62
            // locked on load while signed out and the card only appeared on
            // send - the hint said "use the card above" and there was none.
            // A BINA AI sign-in (App.SessionChanged) unlocks and re-sends the
            // kept prompt; "Not now" unlocks and drops it.
            App.SessionChanged += OnSessionChanged;
            // Auto approval moved to the confirm card ("Approve this build",
            // spec §7.5). Drafters who had the old toggle on get one note.
            ShowAutoApprovalNoteOnce();
            CancelSendCommand = new RelayCommand(_ => CancelSend());
        }

        // ─── Injected context ────────────────────────────────────────────────
        public ICopilotExecutor Executor { get; set; }
        public IChatRouter Router { get; set; }
        /// <summary>The protocol-3 router behind <see cref="Router"/> (null in design hosts).</summary>
        private CopilotTurnRouter TurnRouter => Router as CopilotTurnRouter;
        /// <summary>The pane's door to CopilotToolHost (reads, previewed writes).</summary>
        public PaneToolRunner Tools { get; set; }
        public string UserFirstName { get; set; } = "there";
        public string ModelName { get; set; } = "Main Model";
        // 2026-08-02 defect #9 fix: the header subline is "«connection label»
        // · «active document title»" — ModelName above IS the connection
        // label (and doubles as feedback-log context elsewhere, unrelated to
        // the header), but it does NOT hold the document title. It used to:
        // the header bound BOTH halves to ModelName, and ModelName's own
        // fallback (before SetRevitContext populates it, or if doc.Title is
        // ever empty) is literally the string "Main Model" too — so the
        // subline could read "Main Model · Main Model". DocumentTitle is a
        // separate property, set only from the live Document.Title (see
        // CopilotPanel.SetRevitContext), so the two halves can never collide.
        public string DocumentTitle { get; set; } = "";

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


        /// <summary>On pane open: hold the send gate until
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
            string status = "ok", List<string> toolIds = null, List<HistoryFile> userFiles = null, string botNote = null)
        {
            string time = DateTime.Now.ToString("h:mm tt");
            if (_currentSession == null)
            {
                _currentSession = new HistoryEntry(
                    DateTime.Now.ToString("MMM d, h:mm tt"), status, userText,
                    new List<History>())
                { SessionId = TurnRouter?.SessionId };
                History.Insert(0, _currentSession);
            }
            // The server issues the session id at the first turn_end, which can
            // come after this entry was opened (a turn that asked first).
            if (string.IsNullOrEmpty(_currentSession.SessionId)) _currentSession.SessionId = TurnRouter?.SessionId;
            _currentSession.History.Add(new History("user", userText, time) { Files = userFiles });
            _currentSession.History.Add(new History("bot", botText, time, toolIds) { PaneNote = botNote });
            if (status == "warn") _currentSession.Status = "warn";
            int exchanges = _currentSession.History.Count(m => m.Sender == "user");
            _currentSession.Summary = exchanges == 1 ? userText : $"{exchanges} messages";
            int idx = History.IndexOf(_currentSession);
            if (idx >= 0) History[idx] = _currentSession;
            CopilotStateStore.Save(_pinned, History);
            Raise(nameof(RecentEntry));
        }

        /// <summary>"+" and continuing a chat from History share this (I1): the running
        /// turn stops, every open confirm card declines, and the router switches session
        /// (a fresh handle store). The stopped turn comes back Superseded, so none of it
        /// is drawn in, or recorded to, the next chat.</summary>
        private void LeaveCurrentChat(bool adopt, string sessionId)
        {
            var router = TurnRouter;
            if (IsSending || (router?.IsBusy ?? false)) router?.CancelStream();
            DeclineOpenConfirmations();
            if (adopt) router?.AdoptSession(sessionId);
            else router?.ResetSession();
        }

        private void StartNewChat()
        {
            Thread.Clear();
            _currentSession = null; // next exchange begins a fresh HistoryEntry
        }

        /// <summary>Continue a past conversation from the History tab: the router
        /// adopts its backend session (fresh session when the entry predates
        /// SessionId tracking), the chat thread is rebuilt from the stored
        /// exchanges, and new turns append to the same entry.</summary>
        public void ContinueSession(HistoryEntry entry)
        {
            if (entry == null) return;
            // A null id (an entry that never reached turn_end) starts a new
            // server session; AppendToCurrentSession records it on the next turn.
            // A turn still running in the chat being left stops first, as on "+".
            LeaveCurrentChat(adopt: true, sessionId: entry.SessionId);
            _currentSession = entry;
            Thread.Clear();
            foreach (var m in entry.History)
            {
                if (m.Sender == "user")
                {
                    Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = m.Text, Time = m.Time });
                    continue;
                }
                // M-12: the add-in's own words come back as its note, never as the
                // model's reply (older history stored its "Done" as the reply).
                var legacy = m.PaneNote == null && ReceiptTemplates.IsLegacyAddinDone(m.Text);
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = legacy ? "" : m.Text,
                    PaneNote = legacy ? m.Text : m.PaneNote,
                    Time = m.Time, ToolCallTrace = m.Tools,
                });
            }
            Tab = CpTab.Chat;
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
        /// <summary>Raised by the 402 Attention card's "See plans"; ChatView
        /// forwards it to the pane's existing upgrade flow.</summary>
        public event Action UpgradeRequested;

        private bool _isSignedOut;
        /// <summary>No BINA AI session. Bound to PromptBar.Locked.</summary>
        public bool IsSignedOut
        {
            get => _isSignedOut;
            set { if (_isSignedOut == value) return; _isSignedOut = value; Raise(nameof(IsSignedOut)); }
        }

        // The prompt kept while signed out — sent automatically after sign-in.
        private PromptPayload _pendingAfterSignIn;
        private SlashTool _pendingSlashAfterSignIn;

        private void OnSessionChanged()
        {
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                bool signedOut;
                try { signedOut = !(BinaConfig.Load()?.IsLoggedIn() ?? false); } catch { signedOut = false; }
                if (signedOut)
                {
                    // Signed out: composer stays usable. The next send produces
                    // the Sign-in card (and locks, with that prompt kept).
                    IsSignedOut = false;
                    _pendingAfterSignIn = null; _pendingSlashAfterSignIn = null;
                    return;
                }
                IsSignedOut = false;
                // Saved Commands J1: the fresh token can now see the Mine tier.
                _ = RefreshCommandCatalogAsync(force: true);
                // Drop the Sign-in card(s) now that they are answered.
                for (int i = Thread.Count - 1; i >= 0; i--)
                    if (Thread[i].Kind == CpMsgKind.SignIn) Thread.RemoveAt(i);
                var pending = _pendingAfterSignIn; _pendingAfterSignIn = null;
                var slash = _pendingSlashAfterSignIn; _pendingSlashAfterSignIn = null;
                if (pending != null) ChatSend(pending.Text, pending.ImagesBase64, pending.Files, slash);
            }));
        }

        /// <summary>The Sign-in card: replaces the old chat bubble that named the
        /// wrong ribbon button ("BINA Cloud → Login" is the CDE sign-in and never
        /// mints the engine token). Names the ACCOUNT, not a button.</summary>
        private ChatMessage SignInCard(bool tokenExpired = false)
        {
            IsSignedOut = true;   // lock ONLY here - the card is the way out
            return new ChatMessage
        {
            Role = "ai", Kind = CpMsgKind.SignIn,
            Title = tokenExpired ? "Your BINA AI sign-in has expired" : "Sign in to use the Copilot",
            Body = "Your prompt is kept. Sign in once and it runs — the Copilot needs your BINA AI account for credits and the model.",
            PrimaryLabel = "Sign in to BINA AI",
            PrimaryAction = () =>
            {
                if (!App.PostLoginToAI())
                    Thread.Add(AttentionCard("Couldn't open the sign-in", "Use the ribbon: Bina tab → Login to AI.", "pane · PostLoginToAI · command id not resolved"));
            },
            SecondaryLabel = "Not now",
            SecondaryAction = () =>
            {
                _pendingAfterSignIn = null; _pendingSlashAfterSignIn = null;
                IsSignedOut = false;   // unlock; the drafter can keep typing
                for (int i = Thread.Count - 1; i >= 0; i--)
                    if (Thread[i].Kind == CpMsgKind.SignIn) Thread.RemoveAt(i);
            },
        };
        }

        /// <summary>One Attention card for every "do one thing" failure.</summary>
        private ChatMessage AttentionCard(string title, string body, string origin,
            string primaryLabel = "Try again", Action primary = null, string secondaryLabel = null, Action secondary = null)
        {
            var card = new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.Attention,
                Title = title, Body = body, Origin = origin,
                PrimaryLabel = primaryLabel, SecondaryLabel = secondaryLabel, SecondaryAction = secondary,
            };
            card.PrimaryAction = primary ?? (() =>
            {
                var pending = _pendingAfterSignIn; _pendingAfterSignIn = null;
                if (pending != null) ChatSend(pending.Text, pending.ImagesBase64, pending.Files, _pendingSlashAfterSignIn);
            });
            return card;
        }

        /// <summary>Map a turn that could not run (RouteResult.FailureKind) to the
        /// card the drafter can act on. Null when there is nothing to act on; the
        /// failure then renders as a plain reply.</summary>
        private ChatMessage CardForFailure(RouteResult rr, PromptPayload retry)
        {
            switch (rr?.FailureKind ?? RouteFailure.None)
            {
                case RouteFailure.SignIn:
                    _pendingAfterSignIn = retry;
                    return SignInCard(tokenExpired: true);
                case RouteFailure.UpdateRequired:
                    return AttentionCard("Update BINA Sync to keep using the Copilot",
                        "BINA's copilot now needs a newer add-in. Nothing was changed in the model.",
                        "copilot · HTTP 426 update_required", primaryLabel: "Check for update",
                        primary: () => { try { RevitWebAppSync.Services.UpdateService.EnsureUpToDate(); } catch { } });
                case RouteFailure.TurnActive:
                    return AttentionCard("Another request is still running",
                        "Wait for it to finish or press Stop, then send again. Nothing was changed in the model.",
                        "copilot · HTTP 409 turn active", primaryLabel: "Send again",
                        primary: () => { if (retry != null) ChatSend(retry.Text, retry.ImagesBase64, retry.Files); });
                case RouteFailure.Unreachable:
                    _pendingAfterSignIn = retry;
                    return AttentionCard("The Copilot couldn't reach BINA",
                        "Check the network and try again. Nothing was changed in the model.",
                        "copilot · " + (rr.PaneNote ?? "unreachable").Trim());
                default:
                    var e = rr?.PaneNote ?? "";
                    if (e.IndexOf("credit", StringComparison.OrdinalIgnoreCase) >= 0
                        && (e.IndexOf("used up", StringComparison.OrdinalIgnoreCase) >= 0 || e.IndexOf("limit", StringComparison.OrdinalIgnoreCase) >= 0))
                        return AttentionCard("AI credit limit reached for this period", "Your plan's credits are used up. Upgrade, or wait for the next period.",
                            "copilot · plan_limit", primaryLabel: "See plans", primary: () => UpgradeRequested?.Invoke(), secondaryLabel: "Try again",
                            secondary: () => { if (retry != null) ChatSend(retry.Text, retry.ImagesBase64, retry.Files); });
                    return null;
            }
        }

        public bool IsSending
        {
            get => _isSending;
            set
            {
                _isSending = value;
                Raise();
                // Header status pill (PRD A7) rides the same transitions: a
                // send opens Running; its end lands on Stopped (user clicked
                // Stop this turn) or Done. No extra state machine.
                if (value) RunStatus = "Running";
                else if (RunStatus == "Running") RunStatus = _stopRequested ? "Stopped" : "Done";
                _stopRequested = false;
            }
        }

        // ─── Header status pill (PRD A7): Ready / Running / Done / Stopped ──
        private bool _stopRequested;
        private string _runStatus = "Ready";
        public string RunStatus
        {
            get => _runStatus;
            private set { if (_runStatus == value) return; _runStatus = value; Raise(); }
        }

        // Live state for the CURRENT turn leg: the step list from the router
        // and the cumulative streamed reply. Reset at the start of every leg;
        // every live handler rebuilds the Thinking message from both, so one
        // feed never stomps the other.
        private IReadOnlyList<ProgressStep> _lastSteps;
        private string _lastReplyText;

        /// <summary>User clicked Stop — abort the streaming reply. The router's
        /// RouteAsync then returns a "Cancelled." result which resolves the bubble;
        /// IsSending clears in ResolveProposalAsync's finally.</summary>
        public void CancelSend()
        {
            _stopRequested = true;
            try { TurnRouter?.CancelStream(); } catch { /* already done */ }
        }

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

            // AI (tier 2) commands are requests for the copilot, not code to run
            // here: they go to the chat, where the model works with the 7 tools.
            if (tool.Tier != 1)
            {
                Screen = CpScreen.Home;
                ChatSend(tool.Title + (string.IsNullOrWhiteSpace(tool.Desc) ? "" : " — " + tool.Desc));
                return;
            }
            string code = null;

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

        private string _lastToolRunTurnId;

        /// <summary>The result screen's Undo: the last tool run's BINA entries.</summary>
        public void UndoLastToolRun()
        {
            if (string.IsNullOrEmpty(_lastToolRunTurnId)) return;
            UndoTurn(new ChatMessage { TurnId = _lastToolRunTurnId });
            GoTab(CpTab.Chat);
        }

        public void CancelRun()
        {
            Screen = Prev == CpScreen.Running ? CpScreen.Home : Prev;
        }

        public void FinishRun(ExecOutcome outcome)
        {
            var tool = CurrentTool;
            if (tool == null) return;

            _lastToolRunTurnId = outcome?.TurnId;
            RunResult = BuildResult(tool, outcome);
            var result = RunResult;
            Screen = CpScreen.Result;

            string status = result.Kind == CpResultKind.Issues ? "warn" : (outcome != null && !outcome.Success ? "warn" : "ok");
            string summary = SummaryOf(result);
            AppendToCurrentSession(tool.Title, summary, status, new List<string> { tool.Id });

            Highlights.Clear();   // no prototype markers: a run shows only what Revit selected
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

        // ─── Chat ──────────────────────────────────────────────────────────────

        /// <summary>Command entry: the PromptBar sends a PromptPayload when
        /// screenshots are attached; chips/follow-ups still send a plain string.</summary>
        private void ChatSendAny(object p)
        {
            if (p is PromptPayload pp) ChatSend(pp.Text, pp.ImagesBase64, pp.Files);
            else ChatSend(p as string);
        }

        public void ChatSend(string text, List<string> images = null, List<FileAttachment> files = null,
            SlashTool slashChip = null, Dictionary<string, object> commandArgs = null)
        {
            text = (text ?? "").Trim();
            // A slash command may carry no typed args (a bare "/level-builder"),
            // so an empty text is valid when a command chip is attached.
            if (text.Length == 0 && slashChip == null) return;
            // Single flight (review I3): one copilot turn at a time. A parked
            // question is not busy: a typed message answers it.
            if (IsSending || (TurnRouter?.IsBusy ?? false))
            {
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    PaneNote = CopilotTurnRouter.BusyNote(ReceiptTemplates.Detect(text)),
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
                return;
            }
            // N1: a parked question that asks for a password or token is never
            // answered by typing. Refuse BEFORE the card is marked answered, so its
            // "Don't answer" stays live, and keep the typed text out of the thread,
            // LastPrompt, the prompt counter and local history.
            var typedRefusal = TurnRouter?.PendingAsk?.RefuseTypedAnswer();
            if (typedRefusal != null)
            {
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    PaneNote = typedRefusal,
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
                return;
            }
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
            // Lifetime prompt counter feeds the rating nudge threshold.
            try { var prefs = CopilotPrefs.Load(); prefs.PromptsSent++; prefs.Save(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] PromptsSent not persisted: " + ex.Message); }
            Tab = CpTab.Chat;
            Screen = CpScreen.Home;
            ToolId = null;

            // A typed message while the turn is parked on the assistant's
            // question answers it (the router posts it as the "text" answer);
            // the question card collapses into the record.
            var parkedAsk = TurnRouter?.PendingAsk;
            for (int k = 0; k < Thread.Count; k++)
            {
                var msg = Thread[k];
                if (parkedAsk != null && ReferenceEquals(msg.Ask, parkedAsk) && msg.Ask.AnsweredSummary == null)
                {
                    msg.Ask.AnsweredSummary = text;
                    Thread[k] = Thread[k];   // item-replace redraw trick
                }
            }

            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text, ImagesBase64 = images, Files = files, SlashCommand = slashChip, Time = System.DateTime.Now.ToString("h:mm tt") });


            // Intent detection runs on the user's text only, so file contents can't
            // falsely match tool keywords in PickResponseTool. NO early-return here:
            // the local keyword clarify-intercept was disabled 2026-06-12 (see the
            // commented block below) but a merge resurrected a live copy ABOVE the
            // auth gate — it hijacked any prompt lacking a known verb ("audit …
            // walls") with canned tool chips and never reached the backend
            // (BINAXONE CatA finding, 2026-07-12). The interpreter still runs
            // silently to supply the fallback ToolId the router expects.
            var interp = QueryInterpreter.Interpret(text);

            // Auth gate: BINA Copilot needs a signed-in BINA Cloud session. Show a friendly
            // prompt instead of letting the request 401 at the backend.
            var authCfg = BinaConfig.Load();
            if (authCfg == null || !authCfg.IsLoggedIn())
            {
                // Sign-in card in the thread, composer locks, prompt kept and
                // re-sent after sign-in (OnSessionChanged). The old bubble here
                // named "BINA Cloud → Login" - the CDE sign-in, which never mints
                // the engine token - and had nothing to click.
                _pendingAfterSignIn = new PromptPayload { Text = text, ImagesBase64 = images, Files = files };
                _pendingSlashAfterSignIn = slashChip;
                Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = text, SlashCommand = slashChip });
                Thread.Add(SignInCard());   // locks the composer; "Not now" or sign-in unlocks
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
            _lastReplyText = null;
            // Slash command: protocol 3 has no server-side command ids, so the
            // command travels as its own prompt (template + typed inputs).
            var routeTextOverride = slashChip != null ? SlashPrompt(slashChip, text, commandArgs) : null;
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Menganalisis permintaan…" });
            _ = SendWithAttachmentsAsync(routeTextOverride ?? text, files, interp.ToolId, images, text);
        }

        /// <summary>Read any binary attachments locally (the addin does it — Revit
        /// for a DWG, PdfPig for a PDF), then route the turn. Split out of ChatSend
        /// because reading them needs the Revit job pump and therefore an await —
        /// the thinking bubble is already on screen, so the wait is visible rather
        /// than a frozen pane.</summary>
        private async System.Threading.Tasks.Task SendWithAttachmentsAsync(
            string text, List<FileAttachment> files, string fallbackToolId,
            List<string> images, string displayText = null)
        {
            await ResolveDrawingAttachmentsAsync(files);
            await ResolveDocumentAttachmentsAsync(files);

            // Lightweight projection persisted with the run history so the chip can
            // be redrawn in the History tab (name + line/page count; contents are
            // never stored). Built AFTER resolution so a PDF's page count is known.
            List<HistoryFile> historyFiles = files == null || files.Count == 0
                ? null
                : files.Select(HistoryFile.From).ToList();

            // The chat bubble + history use `text` (what the user typed). The
            // backend route text re-embeds any attached file contents — there's no
            // separate file channel, so files ride along inside the prompt string.
            var tables = ResolveTableAttachments(files);
            string routeText = BuildRouteText(text, files);
            await ResolveProposalAsync(routeText, displayText ?? text, fallbackToolId, images, historyFiles, tables);
        }

        /// <summary>Each attached .csv/.xlsx as attachments[kind=table] (protocol 3),
        /// so set_parameters(from_table) gets real rows. A table that cannot be
        /// parsed stays a text attachment (its CSV text is already in Content).</summary>
        private static List<TableData> ResolveTableAttachments(List<FileAttachment> files)
        {
            var list = new List<TableData>();
            if (files == null) return list;
            foreach (var f in files)
            {
                if (f.ReadError != null || f.Kind != AttachmentKind.Text || !TableAttachment.IsTable(f.Path)) continue;
                try
                {
                    var t = TableXlsx.Read(f.Path);
                    if (t.Columns.Count == 0) continue;
                    var name = list.Any(a => a.Name == t.Name) ? t.Name + " (" + (list.Count + 1) + ")" : t.Name;
                    t.Name = name;
                    list.Add(t);
                    f.TableName = name;
                    f.TableRows = t.Rows.Count;
                    f.TableTotalRows = t.TotalRows;
                    f.TableColumns = t.Columns.Count;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[BINA] table attachment kept as text: " + ex.Message);
                }
            }
            return list;
        }

        /// <summary>Turn each attached .pdf into a pdf_ref + compact summary by
        /// running the addin's own PDF tools. Same contract as the drawing path:
        /// failures are recorded on the attachment, never thrown, so an unreadable
        /// document degrades to a one-line note instead of killing the turn.</summary>
        private async System.Threading.Tasks.Task ResolveDocumentAttachmentsAsync(List<FileAttachment> files)
        {
            var docs = files?.Where(f => f.Kind == AttachmentKind.Pdf).ToList();
            if (docs == null || docs.Count == 0) return;

            foreach (var d in docs)
            {
                try
                {
                    var result = await RunLocalToolAsync(
                        "pdf_open_attachment", new Dictionary<string, object> { ["path"] = d.Path });
                    if (result == null) { d.ReadError = "the PDF reader returned no result"; continue; }
                    d.Ref = result.Value.TryGetProperty("pdf_ref", out var r) ? r.GetString() : null;
                    d.SummaryJson = result.Value.GetRawText();
                }
                catch (Exception ex)
                {
                    d.ReadError = ex.Message;
                }
            }
        }

        /// <summary>Turn each attached .dwg/.dxf into a dwg_ref + compact summary
        /// by running the addin's own DWG tools on the Revit thread. A drawing
        /// that is ALREADY linked in the open model reuses that model ref instead
        /// of being opened a second time — one drawing, one context block.
        /// Failures are recorded on the attachment, never thrown: an unreadable
        /// drawing must degrade to a one-line note, not kill the turn.</summary>
        private async System.Threading.Tasks.Task ResolveDrawingAttachmentsAsync(List<FileAttachment> files)
        {
            var drawings = files?.Where(f => f.Kind == AttachmentKind.Dwg).ToList();
            if (drawings == null || drawings.Count == 0) return;

            // One list_cad_links read serves every attachment's dedup check.
            List<System.Text.Json.JsonElement> inModel = null;
            try
            {
                var links = await RunLocalToolAsync("list_cad_links", new Dictionary<string, object>());
                if (links != null && links.Value.TryGetProperty("cad_links", out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    inModel = arr.EnumerateArray().ToList();
            }
            catch { /* no model / older addin — dedup simply doesn't apply */ }

            foreach (var d in drawings)
            {
                try
                {
                    var match = FindLinkedCad(inModel, d.Path, d.Name);
                    var args = new Dictionary<string, object>();
                    string tool;
                    if (match != null) { tool = "get_dwg_summary"; args["dwg_ref"] = match; }
                    else { tool = "dwg_open_attachment"; args["path"] = d.Path; }

                    var result = await RunLocalToolAsync(tool, args);
                    if (result == null) { d.ReadError = "Revit did not return a result"; continue; }
                    d.Ref = result.Value.TryGetProperty("dwg_ref", out var r) ? r.GetString() : match;
                    d.SummaryJson = result.Value.GetRawText();
                }
                catch (Exception ex)
                {
                    d.ReadError = ex.Message;
                }
            }
        }

        /// <summary>dwg_ref of an in-model CAD link whose path (or, failing that,
        /// file name) matches the attachment. Null when the drawing isn't linked.</summary>
        private static string FindLinkedCad(List<System.Text.Json.JsonElement> inModel, string path, string name)
        {
            if (inModel == null) return null;
            foreach (var item in inModel)
            {
                string LinkStr(string key) =>
                    item.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() : null;
                var linkPath = LinkStr("path");
                var linkName = LinkStr("name");
                bool samePath = !string.IsNullOrEmpty(linkPath) && !string.IsNullOrEmpty(path)
                                && string.Equals(linkPath, path, StringComparison.OrdinalIgnoreCase);
                bool sameName = !string.IsNullOrEmpty(linkName) && !string.IsNullOrEmpty(name)
                                && string.Equals(linkName, name, StringComparison.OrdinalIgnoreCase);
                if (samePath || sameName) return LinkStr("dwg_ref");
            }
            return null;
        }

        /// <summary>Run one addin tool in-process on the Revit thread (no backend
        /// round-trip) and return its result as JSON. Throws with the tool's own
        /// message on failure so the caller can show it to the drafter.</summary>
        private static async System.Threading.Tasks.Task<System.Text.Json.JsonElement?> RunLocalToolAsync(
            string tool, Dictionary<string, object> args)
        {
            var job = new BinaVibe.Mcp.McpJob
            {
                Tool = tool,
                Args = System.Text.Json.JsonSerializer.SerializeToElement(args ?? new Dictionary<string, object>()),
            };
            BinaVibe.Mcp.McpJobPump.Enqueue(job);
            await job.Done.Task;
            if (job.Error != null) throw new InvalidOperationException(job.Error);
            if (job.Result == null) return null;
            var json = System.Text.Json.JsonSerializer.Serialize(job.Result);
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        }

        /// <summary>Slash command sent from the composer (P2). Routes the picked
        /// command to the backend via `command_id` — the definition's instructions
        /// and tool allowlist are injected server-side — and renders the user turn
        /// as a command chip plus any typed args. Local tools (open-view) never
        /// leave the addin: they reuse the Tier-1 vetted executor snippet.</summary>
        public void ChatSendSlashCommand(SlashTool tool, string args,
            Dictionary<string, object> inputs = null)
        {
            if (tool == null) return;
            if (tool.Local) { RunLocalSlash(tool, (args ?? "").Trim()); return; }
            // ChatSend does the routing; the chip rides the user bubble and the
            // backend command id (+ typed input values, Saved Commands J1) is
            // handed to the router just before RouteAsync.
            ChatSend((args ?? "").Trim(), slashChip: tool, commandArgs: inputs);
        }

        private void RunLocalSlash(SlashTool tool, string args)
        {
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = args, SlashCommand = tool, Time = System.DateTime.Now.ToString("h:mm tt") });

            if (tool.Id != "open-view") return;   // only local tool today

            if (string.IsNullOrEmpty(args))
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = "Name a view to open — e.g. `/open-view Aras 01 WIP`. Type `@` to pick one." });
                return;
            }

            var def = CopilotCatalog.Vetted.FirstOrDefault(t => t.Id == "open-view");
            if (def == null || Executor == null)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = "No Revit context — open a document first." });
                return;
            }

            var values = new Dictionary<string, object> { ["view"] = args };
            Executor.Run(def, values, null, outcome =>
            {
                // Same thread contract as the chat codegen Done callback — the
                // executor completes on the UI thread via its ExternalEvent.
                string text = LocalSlashReply(outcome);
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, ToolId = tool.Id, Text = text });
                AppendToCurrentSession("/open-view " + args, text, outcome != null && outcome.Success ? "ok" : "warn", new List<string> { tool.Id });
            });
        }

        // The snippet's SetResult lands in outcome.Data as {"kind":"plain","headline":…,"sub":…};
        // outcome.Message is usually empty on success, so the headline is the reply.
        private static string LocalSlashReply(ExecOutcome outcome)
        {
            if (outcome == null || !outcome.Success)
                return "Couldn't open that view" + (string.IsNullOrEmpty(outcome?.Error) ? "." : ": " + outcome.Error);
            if (!string.IsNullOrEmpty(outcome.Data))
            {
                try
                {
                    var d = Newtonsoft.Json.Linq.JObject.Parse(outcome.Data);
                    var headline = d["headline"]?.ToString();
                    if (!string.IsNullOrEmpty(headline)) return headline;
                }
                catch { /* fall through to Message */ }
            }
            return string.IsNullOrEmpty(outcome.Message) ? "Opened." : outcome.Message;
        }

        /// <summary>A slash command as a plain prompt: its template with the typed
        /// inputs filled into the {name} holes, then any free text. Protocol 3
        /// has no server-side command ids.</summary>
        private static string SlashPrompt(SlashTool tool, string typed, Dictionary<string, object> inputs)
        {
            var template = tool?.PromptTemplate;
            if (string.IsNullOrWhiteSpace(template))
                return string.IsNullOrWhiteSpace(typed) ? (tool?.Name ?? "") : (tool?.Name ?? "") + ": " + typed;
            var text = template;
            if (inputs != null)
                foreach (var kv in inputs)
                    text = text.Replace("{" + kv.Key + "}", kv.Value?.ToString() ?? "");
            return string.IsNullOrWhiteSpace(typed) ? text : text + "\n" + typed;
        }

        /// <summary>Prompt string sent to the backend — see RouteText.Build for
        /// the block format (it lives in Model/ so the wire format is testable
        /// without WPF).</summary>
        private static string BuildRouteText(string text, List<FileAttachment> files) =>
            RouteText.Build(text, files);

        // Persistent header badge: "27 / 30 credits" or "Unlimited". Empty string hides the pill.
        private string _creditBadge = "";
        public string CreditBadge
        {
            get => _creditBadge;
            private set { if (_creditBadge == value) return; _creditBadge = value; Raise(); }
        }

        /// <summary>
        /// Fetch the signed-in user's monthly AI credit balance and seed the usage
        /// meter (footer) + header badge. Called right after login. No chat message —
        /// the quota lives in the meter, not the thread (ClickUp 86eybn4vz).
        /// Best-effort: silently no-ops when not signed in or the backend is unavailable.
        /// </summary>
        public async System.Threading.Tasks.Task ShowCreditsAsync()
        {
            try
            {
                var credits = await FetchCreditsAsync();
                if (credits == null) return;
                SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit, credits.Plan, credits.ResetsAt));
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

        // NOTE: BadgeText describes the QUOTA, not the plan — "Unlimited"
        // here means an uncapped wallet (an internal/admin override; pricing v2 has no
        // unlimited tier), which is what UsageState reports as "Unlimited (internal)".
        private static string BadgeText(AIService.CreditInfo c)
        {
            if (c.Unlimited) return "Unlimited";
            if (c.Limit <= 0) return "";
            int remaining = c.Remaining ?? System.Math.Max(0, c.Limit - c.Used);
            return $"{remaining:N0} / {c.Limit:N0} credits";
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
            long seq = NextUsageSeq();
            try
            {
                if (UsageService != null) { SetUsage(await UsageService.GetAsync(), seq); return; }
                var credits = await FetchCreditsAsync();
                if (credits != null)
                    SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit, credits.Plan, credits.ResetsAt), seq);
            }
            catch { /* best-effort */ }
        }

        /// <summary>ONE balance read feeding both the usage snapshot and the credit
        /// badge. The poll previously called RefreshUsageAsync + RefreshCreditBadgeAsync
        /// back to back, and each fetched independently — two byte-identical
        /// GET /credits/balance per tick (~24/min in the fast tier). Use this wherever
        /// both are wanted.</summary>
        public async System.Threading.Tasks.Task RefreshUsageAndBadgeAsync()
        {
            long seq = NextUsageSeq();
            try
            {
                if (UsageService != null) { SetUsage(await UsageService.GetAsync(), seq); return; }
                var credits = await FetchCreditsAsync();
                if (credits == null) return;
                SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit, credits.Plan, credits.ResetsAt), seq);
                SetCreditBadge(BadgeText(credits));
            }
            catch { /* best-effort */ }
        }

        // Monotonic stamp per usage read. Polling can leave two reads in flight at a
        // 5s tick and they may land out of order — a slow PRE-upgrade response
        // arriving after a fast POST-upgrade one would re-raise the blocked wall over
        // a working composer. Only ever publish a read newer than the last applied.
        private long _usageSeq;
        private long _usageApplied;

        /// <summary>Take a ticket for an in-flight usage read; hand it to SetUsage.</summary>
        private long NextUsageSeq() => System.Threading.Interlocked.Increment(ref _usageSeq);

        private void SetUsage(UsageState u, long seq = 0)
        {
            if (u == null) return;

            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) { disp.Invoke(() => SetUsage(u, seq)); return; }

            // Ordering and de-duping are both decided on the UI thread, so two
            // concurrent refreshes can never both pass these checks.
            if (seq != 0)
            {
                if (seq <= _usageApplied) return;      // stale read, already overtaken
                _usageApplied = seq;
            }

            // While the quota is exhausted the pane re-polls the SAME snapshot every
            // few seconds waiting for an upgrade to land. Publishing an identical
            // value would still raise UsageChanged, and consumers rebuild on that —
            // the blocked wall would be torn down and re-created on every tick
            // (visible flicker, and it would reset a button mid-click).
            //
            // SameAs covers EVERY rendered field. Comparing only a subset was a real
            // bug: a snapshot differing solely in the reset date could never reach the
            // screen, so a fresh 0%-usage account kept the seeded default forever and
            // was shown an "Upgrade plan" CTA instead of "Resets 1 Aug".
            if (_usage != null && _usage.SameAs(u)) return;

            Usage = u;
        }

        private async System.Threading.Tasks.Task ResolveProposalAsync(string routeText, string displayText, string fallbackToolId,
            List<string> images = null, List<HistoryFile> historyFiles = null, List<TableData> tables = null)
        {
            RouteResult rr = null;
            if (Router != null)
            {
                var router = TurnRouter;
                HookStreaming(router);
                // Pasted screenshots and attached tables ride along with this
                // turn only — the router consumes and clears them.
                if (router != null)
                {
                    router.PendingImages = images;
                    router.PendingTables = tables;
                }
                IsSending = true;   // send button shows Stop until the reply resolves
                try { rr = await Router.RouteAsync(routeText, fallbackToolId); }
                catch { rr = null; }
                finally
                {
                    IsSending = false;
                    UnhookStreaming(router);
                }
            }

            // The credit was consumed by the backend during RouteAsync — refresh the
            // footer meter so usage ticks up live (best-effort).
            _ = RefreshUsageAsync();

            // The drafter left this chat while the turn ran ("+" or History, I1):
            // its result belongs to a thread that is gone.
            if (rr != null && rr.Superseded) return;

            // A failed turn the drafter can act on becomes a card (Sign-in /
            // Attention) instead of a reply bubble carrying the raw reason.
            if (rr != null && rr.Failed)
            {
                var card = CardForFailure(rr, new PromptPayload { Text = displayText });
                if (card != null) { ReplaceLastThinking(card); return; }
            }

            RenderRouteResult(rr, routeText, displayText, fallbackToolId, historyFiles);
        }

        /// <summary>Wire the live callbacks (step list, growing reply) onto the
        /// router for ONE turn leg. Shared by a fresh send and an ask answer.</summary>
        private void HookStreaming(CopilotTurnRouter router)
        {
            if (router == null) return;
            router.OnSteps = steps =>
            {
                _lastSteps = steps;
                ReplaceLastThinking(LiveThinking());
            };
            router.OnCodeStream = cumulative =>
            {
                if (string.IsNullOrWhiteSpace(cumulative)) return;
                _lastReplyText = cumulative;
                ReplaceLastThinking(LiveThinking());
            };
        }

        /// <summary>The ONE live Thinking message, built from the per-turn
        /// snapshot (_lastSteps/_lastReplyText) so no feed stomps another.</summary>
        private ChatMessage LiveThinking() => new ChatMessage
        {
            Role = "ai", Kind = CpMsgKind.Thinking,
            StreamingReply = !string.IsNullOrWhiteSpace(_lastReplyText),
            Text = _lastReplyText ?? "",
            LiveSteps = _lastSteps,
        };

        private void UnhookStreaming(CopilotTurnRouter router)
        {
            if (router == null) return;
            router.OnSteps = null;
            router.OnCodeStream = null;
            router.OnProgressText = null;
        }

        /// <summary>Render a resolved RouteResult: the assistant's question card,
        /// or the reply with the turn's evidence, receipt and caution (all from
        /// envelopes — RouteResult.Evidence — never from the reply text).</summary>
        private void RenderRouteResult(RouteResult rr, string routeText, string displayText, string fallbackToolId, List<HistoryFile> historyFiles)
        {
            if (rr == null)
            {
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    PaneNote = "The copilot did not answer. Nothing was changed in the model.",
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
                return;
            }

            if (rr.Ask != null)
            {
                ReplaceLastThinking(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Clarify,
                    // Whatever the model said before asking stays its own text.
                    Text = rr.Reply ?? "",
                    Question = rr.Ask.Question,
                    Ask = rr.Ask,
                    TurnId = rr.TurnId,
                    Evidence = rr.Evidence,
                    Lang = rr.Language,
                    Steps = rr.Steps,
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
                AppendToCurrentSession(displayText, rr.Ask.Question, "ok", null, historyFiles);
                RunStatus = "Needs input";
                return;
            }

            var reply = new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply, ToolId = fallbackToolId,
                // Exactly the model's text. Pane notes travel separately.
                Text = rr.Reply ?? "",
                PaneNote = rr.PaneNote,
                ToolsUsed = rr.ToolsUsed,
                SourcePrompt = routeText,
                Steps = rr.Steps,
                Interrupted = rr.Interrupted,
                TurnId = rr.TurnId,
                Evidence = rr.Evidence,
                Lang = rr.Language,
                Time = System.DateTime.Now.ToString("h:mm tt"),
            };
            ReplaceLastThinking(reply);
            var caution = rr.Evidence != null && rr.Evidence.Caution;
            // M-12: the model's words as they were (empty stays empty); the add-in's
            // own note and receipt on their own labelled line.
            var (historyReply, historyNote) = ReceiptTemplates.HistoryOf(rr.Reply, rr.PaneNote, rr.Evidence, rr.Language);
            AppendToCurrentSession(displayText, historyReply, caution ? "warn" : "ok", rr.ToolsUsed, historyFiles, historyNote);
            if (caution) RunStatus = "Check receipt";
        }

        // ─── Tindakan (one-tap next-step offer) ────────────────────────────────
        // Old-backend fallback only (a turn with no Followups list — see
        // ChatView's hasTindakan gate). The agent's reply can end with a "next
        // step" offer ("Nak saya lanjutkan?"); the pane renders it as a
        // follow-up CHIP under the LAST AiReply while unresolved (ChatView
        // gates it — see IsLastAiReply) — 2026-08-02: no longer the old blue
        // "✓ Ya, teruskan"/"Tidak" buttons (defect #4, unified into the
        // reasoning-UI's chip row). Task 12 (2026-08-02): sends the FULL
        // m.Tindakan text verbatim, matching every other follow-up chip's
        // "what's shown [before truncation] is what gets sent" contract — a
        // bare "Continue" placeholder lost the offer's content on send, and the
        // multi-bullet Followups path never used one, so this fallback
        // shouldn't either.
        public void AcceptTindakan(ChatMessage m)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Tindakan) || m.TindakanResolved) return;
            m.TindakanResolved = true;
            ChatSend(m.Tindakan);
        }

        public void DeclineTindakan(ChatMessage m)
        {
            if (m == null) return;
            m.TindakanResolved = true;
            // Item-replace trick: setting the SAME item back into the indexer
            // still raises ObservableCollection's CollectionChanged (Replace),
            // which ChatView's Thread.CollectionChanged hook uses to Rebuild() —
            // there's no separate "refresh" method, this is how every other
            // in-place mutation (m.Dismissed = true, etc.) forces a redraw.
            int i = Thread.IndexOf(m);
            if (i >= 0) Thread[i] = Thread[i];
        }

        // ─── The assistant's question (ask card) ───────────────────────────────
        /// <summary>Answer the parked ask from its card (option, form or project
        /// note). The card collapses into a record and the turn continues.</summary>
        public async void SubmitAsk(ChatMessage m, BinaVibe.Copilot.Contracts.AnswerDto answer, string summary)
        {
            var router = TurnRouter;
            if (m?.Ask == null || router == null || m.Ask.AnsweredSummary != null || answer == null) return;
            m.Ask.AnsweredSummary = string.IsNullOrWhiteSpace(summary) ? "✓" : summary;
            int i = Thread.IndexOf(m);
            if (i >= 0) Thread[i] = Thread[i];

            _lastSteps = null;
            _lastReplyText = null;
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Thinking…" });
            HookStreaming(router);
            IsSending = true;
            RouteResult rr = null;
            try { rr = await router.SubmitAskAsync(m.Ask, answer); }
            catch (Exception ex) { rr = new RouteResult { PaneNote = "The answer could not be sent (" + ex.Message + ").", FailureKind = RouteFailure.Other }; }
            finally
            {
                IsSending = false;
                UnhookStreaming(router);
            }
            _ = RefreshUsageAsync();
            if (rr != null && rr.Superseded) return;   // the chat changed under the turn (I1)
            if (rr != null && rr.Failed)
            {
                var card = CardForFailure(rr, null);
                if (card != null) { ReplaceLastThinking(card); return; }
            }
            RenderRouteResult(rr, LastPrompt, m.Ask.AnsweredSummary, "ai-generated", null);
        }

        /// <summary>Old structured-question cards (history from before protocol 3):
        /// a tap sends the chosen labels as the next message.</summary>
        public void SubmitChoiceSelections(ChatMessage m, Dictionary<string, List<string>> selections)
        {
            if (m == null || m.ActionsResolved || selections == null || selections.Count == 0) return;
            m.ActionsResolved = true;
            m.ChoiceSummary = string.Join(" · ", selections.Select(kv => string.Join(", ", kv.Value)));
            int i = Thread.IndexOf(m);
            if (i >= 0) Thread[i] = Thread[i];
            ChatSend(m.ChoiceSummary);
        }

        // ─── The add-in's confirm card (spec §7.5) ─────────────────────────────
        // W3's WriteBatchRunner previews the batch, then asks THIS pane through
        // PaneToolRunner's presenter. The card is built from the preview
        // envelopes only; the turn waits here until the drafter decides (or
        // presses Stop, which declines).

        /// <summary>IConfirmCardView: show the card at the bottom of the chat and
        /// wait for Ya / Tidak / Approve this build.</summary>
        public Task<BinaVibe.Copilot.Contracts.ConfirmDecision> ShowConfirmAsync(ConfirmCardModel card, System.Threading.CancellationToken ct)
        {
            card.BindCancellation(ct);
            var msg = new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.ConfirmActions, Confirm = card, Lang = card.Language,
                Time = System.DateTime.Now.ToString("h:mm tt"),
            };
            var shown = false;
            RunOnUi(() =>
            {
                // M-4: checked on the UI thread, where "+" and History-continue run:
                // a card from a chat that was left, or whose turn was stopped, never
                // appears in the next chat; it is declined where it stands.
                if (card.DeclineIfStale(Tools?.ChatEpoch ?? card.ChatEpoch, ct)) return;
                shown = true;
                // A tool form or saved command can ask too: the card lives in the chat.
                if (Tab != CpTab.Chat) Tab = CpTab.Chat;
                if (Screen != CpScreen.Home) Screen = CpScreen.Home;
                Thread.Add(msg);
                RunStatus = "Awaiting confirmation";
            });
            if (!shown) return card.Decision;
            card.Decision.ContinueWith(_ => RunOnUi(() =>
            {
                int i = Thread.IndexOf(msg);
                if (i >= 0) Thread[i] = Thread[i];   // redraw: buttons -> resolved record
                // Keep the live thinking bubble BELOW the decided card, so the
                // rest of the turn (commit, reply) reads in order.
                for (int k = 0; k < i; k++)
                    if (Thread[k].Kind == CpMsgKind.Thinking) { Thread.Move(k, Thread.Count - 1); break; }
                if (RunStatus == "Awaiting confirmation") RunStatus = IsSending ? "Running" : "Done";
            }), System.Threading.Tasks.TaskScheduler.Default);
            return card.Decision;
        }

        /// <summary>IConfirmCardView (M-2): Revit is waiting on a dialog during a library
        /// tool or saved command (a copilot turn shows it as a step instead).</summary>
        public void RevitWaiting(string text)
        {
            var line = "Revit is waiting on a dialog" + (string.IsNullOrWhiteSpace(text) ? "." : ": " + text.Trim());
            RunOnUi(() =>
            {
                var last = Thread.Count > 0 ? Thread[Thread.Count - 1] : null;
                if (last != null && last.PaneNote == line) return;
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, PaneNote = line, Time = System.DateTime.Now.ToString("h:mm tt") });
            });
        }

        public void AcceptActions(ChatMessage m) =>
            m?.Confirm?.TryDecide(new BinaVibe.Copilot.Contracts.ConfirmDecision { Approved = true });

        public void DeclineActions(ChatMessage m) =>
            m?.Confirm?.TryDecide(new BinaVibe.Copilot.Contracts.ConfirmDecision { Approved = false, Reason = "declined" });

        public void ApproveBuild(ChatMessage m)
        {
            if (m?.Confirm == null || !m.Confirm.OfferApproveThisBuild) return;
            m.Confirm.TryDecide(new BinaVibe.Copilot.Contracts.ConfirmDecision { Approved = true, ApproveThisBuild = true });
        }

        /// <summary>Decline every open card. Called when the drafter leaves the chat
        /// ("+" or continuing another chat, LeaveCurrentChat). A document closed
        /// under an open card is not handled here: its commit fails doc_changed.</summary>
        public void DeclineOpenConfirmations()
        {
            foreach (var m in Thread.Where(t => t.Confirm != null && !t.Confirm.Resolved).ToList())
                DeclineActions(m);
        }

        private static void RunOnUi(Action a)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(a); else a();
        }

        // ─── Receipt actions ────────────────────────────────────────────────────
        /// <summary>Undo the turn's BINA entries (W3's UndoService; one per batch).</summary>
        public bool CanUndoTurn(ChatMessage m)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return false;
            try { return BinaVibe.Copilot.Undo.UndoService.CanUndoTurn(m.TurnId); }
            catch { return false; }
        }

        public async void UndoTurn(ChatMessage m)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return;
            var ms = m.Lang == ReceiptLanguage.Ms;
            string text;
            try
            {
                var env = await BinaVibe.Copilot.Undo.UndoService.UndoTurnAsync(m.TurnId, System.Threading.CancellationToken.None);
                text = env?.Summary ?? (ms ? "Buat asal selesai." : "Undone.");
            }
            catch (Exception ex)
            {
                text = (ms ? "Tidak dapat membuat asal: " : "Could not undo: ") + ex.Message
                     + (ms ? " Guna senarai Undo Revit (entri \"BINA:\")." : " Use Revit's Undo list (\"BINA:\" entries).");
            }
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = text, Time = System.DateTime.Now.ToString("h:mm tt") });
        }

        /// <summary>Highlight on a check rule (spec §21): find_elements over the rule's
        /// failing-elements handle with highlight on, through the same host as the
        /// model's reads (PaneToolRunner). Handles live per chat session, so one from
        /// a chat that was left answers not_found, and the pane says so.</summary>
        public async void HighlightHandle(string handle, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var vc = VettedCalls.Highlight(handle, out var error);
            if (vc == null || Tools == null) return;
            string note = null;
            try
            {
                var envs = await Tools.RunReadsAsync("pane-" + Guid.NewGuid().ToString("N"), new[] { vc.Call },
                    Tools.Snapshot(), System.Threading.CancellationToken.None);
                var env = envs?.FirstOrDefault();
                if (env == null || !env.Ok)
                    note = (ms ? "Tidak dapat menyerlahkan: " : "Could not highlight: ")
                         + (env?.Error?.Message ?? env?.Summary ?? (ms ? "tiada keputusan" : "no result"));
            }
            catch (Exception ex)
            {
                note = (ms ? "Tidak dapat menyerlahkan: " : "Could not highlight: ") + ex.Message;
            }
            if (note != null)
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, PaneNote = note, Lang = lang, Time = System.DateTime.Now.ToString("h:mm tt") });
        }

        /// <summary>Export a result table the turn returned (file picker owned by the pane).</summary>
        public void ExportTable(ResultTable table)
        {
            if (table == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export to Excel",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                FileName = SafeFileName(table.Title, "bina-export") + ".xlsx",
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return;
            string text;
            try
            {
                TableXlsx.Write(table, dlg.FileName);
                text = "Exported " + table.Rows.Count + " rows to " + System.IO.Path.GetFileName(dlg.FileName) + ".";
            }
            catch (Exception ex) { text = "Export failed: " + ex.Message; }
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = text, Time = System.DateTime.Now.ToString("h:mm tt") });
        }

        private static string SafeFileName(string title, string fallback)
        {
            var name = string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        // ─── Save as command (spec §11: re-runs without the model) ──────────────
        private List<SavedToolCommand> _toolCommands;
        public IReadOnlyList<SavedToolCommand> ToolCommands => _toolCommands ??= ToolCommandStore.Load();

        /// <summary>Keep a committed write as {tool, args, intent} in the Library.</summary>
        public void SaveWriteAsCommand(ChatMessage m, CommittedWrite write)
        {
            var ms = m?.Lang == ReceiptLanguage.Ms;
            string text;
            if (!ToolCommandStore.CanSave(write, out var why))
                text = (ms ? "Tidak dapat disimpan sebagai arahan: " : "Can't save this as a command: ") + why + ".";
            else
            {
                var cmd = ToolCommandStore.Create(write, CurrentProjectKey());
                var list = ToolCommands.ToList();
                list.Insert(0, cmd);
                try
                {
                    ToolCommandStore.Save(list);
                    _toolCommands = list;
                    Raise(nameof(ToolCommands));
                    text = (ms ? "Disimpan dalam Pustaka: " : "Saved to the Library: ") + cmd.Name
                         + (ms ? ". Jalankan semula tanpa AI; ia tetap dipratonton dan disahkan." : ". It re-runs without the AI, still previewed and confirmed.");
                }
                catch (Exception ex) { text = (ms ? "Gagal menyimpan: " : "Could not save: ") + ex.Message; }
            }
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = text, Lang = m?.Lang ?? ReceiptLanguage.En, Time = System.DateTime.Now.ToString("h:mm tt") });
        }

        private string CurrentProjectKey()
        {
            try { return Tools?.Snapshot()?.Doc?.ProjectKey ?? ""; }
            catch { return ""; }
        }

        public void DeleteToolCommand(SavedToolCommand cmd)
        {
            if (cmd == null) return;
            var list = ToolCommands.Where(c => c.Id != cmd.Id).ToList();
            try { ToolCommandStore.Save(list); } catch { return; }
            _toolCommands = list;
            Raise(nameof(ToolCommands));
        }

        /// <summary>Re-run a saved command through CopilotToolHost: preview, the
        /// confirm card, commit, receipt and Undo, with no model involved.</summary>
        public async void RunToolCommand(SavedToolCommand cmd)
        {
            if (cmd == null || Tools == null) return;
            GoTab(CpTab.Chat);
            var lang = ReceiptTemplates.Detect(cmd.Name);
            // One snapshot for the run: the project check and the write's doc check use it.
            var snapshot = Tools.Snapshot();
            // Review M4: element ids only mean something in the project they came from.
            var check = ToolCommandStore.CheckRerun(cmd, snapshot?.Doc?.ProjectKey ?? "");
            if (!check.Allowed)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, PaneNote = check.Message, Time = System.DateTime.Now.ToString("h:mm tt") });
                return;
            }
            if (IsSending || (TurnRouter?.IsBusy ?? false))
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, PaneNote = CopilotTurnRouter.BusyNote(lang), Time = System.DateTime.Now.ToString("h:mm tt") });
                return;
            }
            var turnId = "cmd-" + Guid.NewGuid().ToString("N");
            var call = ToolCommandStore.ToCall(cmd);
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = cmd.Name, Time = System.DateTime.Now.ToString("h:mm tt") });
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = lang == ReceiptLanguage.Ms ? "Pratonton dalam Revit…" : "Previewing in Revit…" });
            IsSending = true;
            var evidence = new TurnEvidence();
            string text;
            try
            {
                Tools.Language = lang;
                var request = new BinaVibe.Copilot.Contracts.ToolRequestDto
                {
                    RequestId = "req-" + turnId,
                    Kind = BinaVibe.Copilot.Contracts.ToolRequestKinds.Write,
                    Calls = new List<BinaVibe.Copilot.Contracts.ToolCallDto> { call },
                    Description = cmd.Intent,
                };
                var envs = await Tools.RunWritesAsync(turnId, request.RequestId, request.Calls, cmd.Intent, snapshot, System.Threading.CancellationToken.None);
                evidence.Add(request, envs);
                text = envs.Length > 0 ? envs[0].Summary : "";
                cmd.RunCount++;
                try { ToolCommandStore.Save(ToolCommands); } catch { }
            }
            catch (Exception ex) { text = "The command could not run (" + ex.Message + ")."; }
            finally { IsSending = false; }
            ReplaceLastThinking(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply, Text = text, TurnId = turnId, Evidence = evidence, Lang = lang,
                PaneNote = check.Message,
                Time = System.DateTime.Now.ToString("h:mm tt"),
            });
            AppendToCurrentSession(cmd.Name, text, evidence.Caution ? "warn" : "ok", new List<string> { cmd.Tool });
        }

        // ─── Auto approval note (spec §7.5) ─────────────────────────────────────
        private void ShowAutoApprovalNoteOnce()
        {
            try
            {
                var prefs = CopilotPrefs.Load();
                if (!prefs.AutoApproveWrites) return;
                prefs.AutoApproveWrites = false;   // the old toggle is gone; never show twice
                prefs.Save();
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = "Auto approval is now \"Approve this build\", offered on build jobs. Edits and deletes always ask.",
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
            }
            catch { /* a note must never break the pane */ }
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

        // ─── Saved Commands J1 ───────────────────────────────────────────────

        /// <summary>ChatView subscribes and shows the Save sheet.</summary>
        public event System.Action<SavedCommandDraft> SaveCommandRequested;

        /// <summary>Palette rebuilds its list on next open after a catalog change.</summary>
        public event System.Action PaletteInvalidated;

        public void OpenSaveCommandSheet(ChatMessage m)
        {
            if (m == null) return;
            if (IsSignedOut || string.IsNullOrEmpty(BinaConfig.Load()?.AccessToken))
            {
                Thread.Add(new ChatMessage
                { Role = "ai", Kind = CpMsgKind.AiReply, Text = "Sign in to save commands." });
                return;
            }
            SaveCommandRequested?.Invoke(SavedCommandDraft.FromReply(m.SourcePrompt, m.ToolsUsed, m.RunId));
        }

        public void OnEditCommand(SlashTool t)
        {
            if (t == null || !t.Editable) return;
            SaveCommandRequested?.Invoke(SavedCommandDraft.FromTool(t));
        }

        public async System.Threading.Tasks.Task OnDeleteCommandAsync(SlashTool t)
        {
            if (t == null || !t.Editable) return;
            var token = BinaConfig.Load()?.AccessToken;
            if (string.IsNullOrEmpty(token)) return;
            bool ok;
            try { ok = await new AIService().DeleteCommandAsync(t.Id, token, System.Threading.CancellationToken.None); }
            catch { ok = false; }
            await RefreshCommandCatalogAsync(force: true);
            Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply,
                Text = ok ? $"Deleted `/{t.Id}`." : $"`/{t.Id}` was already gone.",
            });
        }

        /// <summary>Save (or update, when the draft carries EditingId) — returns
        /// null on success or an error string the sheet displays.</summary>
        public async System.Threading.Tasks.Task<string> SaveDraftAsync(SavedCommandDraft d)
        {
            var token = BinaConfig.Load()?.AccessToken;
            if (string.IsNullOrEmpty(token)) return "Sign in to save commands.";
            try
            {
                var body = d.ToRequest();
                var res = d.EditingId == null
                    ? await new AIService().SaveCommandAsync(body, token, System.Threading.CancellationToken.None)
                    : await new AIService().UpdateCommandAsync(d.EditingId, body, token, System.Threading.CancellationToken.None);
                if (res == null) return "That command no longer exists.";
                await RefreshCommandCatalogAsync(force: true);
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = d.EditingId == null
                        ? $"Saved — run it any time with `/{res.Command.Id}`."
                        : $"Updated `/{res.Command.Id}`.",
                });
                return null;
            }
            catch (System.InvalidOperationException ex) { return ex.Message; }
            catch (System.Exception ex) { return "Could not save: " + ex.Message; }
        }

        /// <summary>GET the catalog (ETag-cached) and merge the Mine tier into
        /// ToolCatalog; persists the rows so an offline start still shows them.</summary>
        public async System.Threading.Tasks.Task RefreshCommandCatalogAsync(bool force = false)
        {
            var prefs = CopilotPrefs.Load();
            var token = BinaConfig.Load()?.AccessToken;
            if (string.IsNullOrEmpty(token)) { RestoreCachedMine(prefs); return; }
            var svc = new AIService();
            Models.CatalogResponseDto res = null;
            try
            {
                res = await svc.GetCommandsAsync(token, force ? null : prefs.SavedCommandsEtag,
                                                 System.Threading.CancellationToken.None);
            }
            catch { /* transport — fall through to cache */ }
            if (res == null) { RestoreCachedMine(prefs); return; }   // 304 or failure → keep what we have
            var mine = res.Commands.Where(c => c.Group == "mine").ToList();
            ToolCatalog.MergeRemote(mine.Select(ToolCatalog.FromCatalogEntry));
            try
            {
                prefs.SavedCommandsJson = Newtonsoft.Json.JsonConvert.SerializeObject(mine);
                prefs.SavedCommandsEtag = svc.LastCommandsEtag ?? "";
                prefs.Save();
            }
            catch { /* cache is a convenience */ }
            PaletteInvalidated?.Invoke();
        }

        private void RestoreCachedMine(CopilotPrefs prefs)
        {
            if (string.IsNullOrEmpty(prefs?.SavedCommandsJson)) return;
            try
            {
                var mine = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    System.Collections.Generic.List<Models.CatalogCommandDto>>(prefs.SavedCommandsJson);
                ToolCatalog.MergeRemote(mine.Select(ToolCatalog.FromCatalogEntry));
                PaletteInvalidated?.Invoke();
            }
            catch { /* stale cache — ignore */ }
        }

        /// <summary>Thumbs feedback with the downvote panel's optional reason/note
        /// and the auto-attached version context. Best-effort; never throws.</summary>
        public void SubmitFeedback(string rating, string prompt, string reason, string note)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            var cfg = BinaConfig.Load();
            int? userId = (cfg?.UserId ?? 0) > 0 ? (int?)cfg.UserId : null;
            string sessionId = TurnRouter?.SessionId;
            string token = cfg?.AccessToken ?? "";

            // Detached — never await on the UI thread; failures are swallowed inside.
            _ = _feedback.SubmitFeedbackAsync(prompt, rating, prompt, sessionId, userId, token,
                reason: reason, note: note, context: CopilotContext.ShortLabel);
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
