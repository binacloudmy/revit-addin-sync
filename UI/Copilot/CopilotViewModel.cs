using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using RevitWebAppSync.UI.Copilot.Ports;
using HL = RevitWebAppSync.UI.Copilot.Highlights;

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
    public partial class CopilotViewModel : INotifyPropertyChanged, IPaneNoticeView
    {
        private readonly IPaneServices _pane;
        /// <summary>Revit-side actions (undo/redo, rings, selection); fakeable.</summary>
        public IRevitActions Revit { get; }

        public CopilotViewModel(IPaneServices pane = null, IRevitActions revit = null)
        {
            _pane = pane ?? new PaneServices();
            Revit = revit ?? new RevitActions(() => InModel);
            Revit.UndoChanged += OnUndoChanged;
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

        /// <summary>A finished turn produced a result worth drawing in the model (rings, toast, callouts).</summary>
        public event Action<HL.HighlightSet, ChatMessage> ModelResultReady;
        /// <summary>The in-model result layer (set by the panel); the chat's card actions use it.</summary>
        public HL.InModelResult InModel;
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
            _pane.RunOnUi(() => ToolActivity = text);
        }

        /// <summary>Clear the tool-activity indicator (UI-thread safe).</summary>
        public void ClearToolActivity()
        {
            _pane.RunOnUi(() => ToolActivity = "");
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
            _pane.RunOnUi(() => Thread.Add(m));
        }

        private readonly HashSet<string> _pinned;
        public IReadOnlyCollection<string> Pinned => _pinned;

        private HistoryEntry _currentSession = null;

        private void AppendToCurrentSession(string userText, string botText,
            string status = "ok", List<string> toolIds = null, List<HistoryFile> userFiles = null, string botNote = null,
            ResultCardModel resultCard = null, ReceiptLanguage lang = default)
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
            _currentSession.History.Add(new History("bot", botText, time, toolIds)
            {
                PaneNote = botNote,
                ResultCard = resultCard != null && resultCard.Kind != ResultKind.None ? resultCard : null,
                Lang = lang,
            });
            if (status == "warn") _currentSession.Status = "warn";
            int exchanges = _currentSession.History.Count(m => m.Sender == "user");
            _currentSession.Summary = exchanges == 1 ? userText : $"{exchanges} messages";
            int idx = History.IndexOf(_currentSession);
            if (idx >= 0) History[idx] = _currentSession;
            CopilotStateStore.Save(_pinned, History);
            Raise(nameof(RecentEntry));
        }

        /// <summary>"+" and continuing a chat from History share this (I1): the running
        /// turn stops, and the router switches session
        /// (a fresh handle store). The stopped turn comes back Superseded, so none of it
        /// is drawn in, or recorded to, the next chat.</summary>
        private void LeaveCurrentChat(bool adopt, string sessionId)
        {
            var router = TurnRouter;
            _queued = null;
            if (IsSending || (router?.IsBusy ?? false)) router?.CancelStream();
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
                    // The card is drawn from the saved model alone: empty evidence, so no
                    // Undo is offered for a turn from a past session.
                    ResultCard = m.ResultCard,
                    Evidence = m.ResultCard != null ? new TurnEvidence() : null,
                    Lang = m.Lang,
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
            _pane.BeginOnUi(new Action(() =>
            {
                bool signedOut;
                try { signedOut = !(_pane.LoadConfig()?.IsLoggedIn() ?? false); } catch { signedOut = false; }
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
                case RouteFailure.HostBlocked:
                    _pendingAfterSignIn = retry;
                    var banner = FailureCards.For(rr.FailureKind, rr.PaneNote);
                    return AttentionCard(banner.Title, banner.Body, banner.Origin);
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

        // The prompt typed while a turn ran: sent when that turn ends well.
        private PromptPayload _queued;

        private void SendQueued(RouteResult rr)
        {
            var q = _queued;
            _queued = null;
            if (q == null) return;
            if (rr == null || rr.Superseded || rr.Failed || rr.Ask != null)
            {
                if (rr != null && rr.Superseded) return;   // another chat now
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    PaneNote = "Your queued message was not sent. Send it again when you are ready.",
                    Time = System.DateTime.Now.ToString("h:mm tt"),
                });
                return;
            }
            ChatSend(q.Text, q.ImagesBase64, q.Files);
        }
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
        private int _replyShown;   // chars of the leg's reply already shown above a confirm card

        /// <summary>User clicked Stop — abort the streaming reply. The router's
        /// RouteAsync then returns a "Cancelled." result which resolves the bubble;
        /// IsSending clears in ResolveProposalAsync's finally.</summary>
        public void CancelSend()
        {
            _stopRequested = true;
            _queued = null;
            try { TurnRouter?.CancelStream(); } catch { /* already done */ }
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
                // A plain prompt queues behind the running turn (one slot: the latest
                // wins); a slash command still waits for the drafter to retry.
                var queue = slashChip == null && text.Length > 0;
                if (queue) _queued = new PromptPayload { Text = text, ImagesBase64 = images, Files = files };
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    PaneNote = queue ? "Queued. It runs when this turn finishes. Stop clears it." : CopilotTurnRouter.BusyNote(ReceiptLanguage.En),
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
            try { var prefs = _pane.LoadPrefs(); prefs.PromptsSent++; prefs.Save(); }
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
            var authCfg = _pane.LoadConfig();
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
            _replyShown = 0;
            try { _turnDocKey = BinaVibe.Copilot.Read.SnapshotService.ActiveDocKey ?? ""; } catch (Exception) { _turnDocKey = ""; }
            // Slash command: protocol 3 has no server-side command ids, so the
            // command travels as its own prompt (template + typed inputs).
            var routeTextOverride = slashChip != null ? SlashPrompt(slashChip, text, commandArgs) : null;
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Thinking…" });
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
            var documents = ResolvePdfPages(files);   // AI-297: the pages travel as attachments, not inside the prompt
            RememberSourceFiles(files);
            string routeText = BuildRouteText(text, files);
            await ResolveProposalAsync(routeText, displayText ?? text, fallbackToolId, images, historyFiles, tables, documents);
        }

        // The attached PDFs by name, so a result row's Source panel can draw the page it cites (board 4).
        // Only for this run of the add-in: a chat reopened from History has no file to show.
        private readonly Dictionary<string, string> _sourceFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // One parse per PDF however many rows ask at once: the Task is cached, not its result.
        private readonly Dictionary<string, System.Threading.Tasks.Task<List<BinaVibe.Mcp.Tools.PdfWord>[]>> _sourcePages =
            new Dictionary<string, System.Threading.Tasks.Task<List<BinaVibe.Mcp.Tools.PdfWord>[]>>(StringComparer.OrdinalIgnoreCase);

        private void RememberSourceFiles(List<FileAttachment> files)
        {
            if (files == null) return;
            foreach (var f in files)
                if (f != null && f.Kind == AttachmentKind.Pdf && !string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(f.Path))
                    _sourceFiles[f.Name] = f.Path;
        }

        /// <summary>The words of one page of an attached PDF, read off the UI thread; null when the file is gone or not attached in this run.</summary>
        public async System.Threading.Tasks.Task<IReadOnlyList<BinaVibe.Mcp.Tools.PdfWord>> SourcePageWordsAsync(string attachment, int page)
        {
            if (string.IsNullOrEmpty(attachment) || !_sourceFiles.TryGetValue(attachment, out var path) || !System.IO.File.Exists(path)) return null;
            try
            {
                System.Threading.Tasks.Task<List<BinaVibe.Mcp.Tools.PdfWord>[]> read;
                lock (_sourcePages)
                {
                    if (!_sourcePages.TryGetValue(path, out read) || read.IsFaulted)
                        _sourcePages[path] = read = System.Threading.Tasks.Task.Run(() => BinaVibe.Mcp.Tools.PdfReader.ExtractWords(path));
                }
                var pages = await read;
                return page >= 1 && page <= pages.Length ? pages[page - 1] : null;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] source page words: " + ex.Message); return null; }
        }

        /// <summary>Open an attached PDF in the default viewer.</summary>
        public void OpenSource(string attachment, int? page)
        {
            try
            {
                if (_sourceFiles.TryGetValue(attachment ?? "", out var path) && System.IO.File.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] open source: " + ex.Message); }
        }

        /// <summary>Each attached .pdf with a text layer as attachments[kind=document] (protocol 3): its pages go to
        /// the cloud page by page, and the prompt keeps only a one-line pointer. A PDF with no text layer (a scan) or
        /// one that could not be read keeps its old summary block.</summary>
        private static List<DocumentData> ResolvePdfPages(List<FileAttachment> files)
        {
            var list = new List<DocumentData>();
            if (files == null) return list;
            var left = DocumentAttachment.MaxCharsPerTurn;   // all documents of one turn share this budget
            foreach (var f in files)
            {
                if (left <= 0) break;   // the rest keep their summary block
                if (f.ReadError != null || f.Kind != AttachmentKind.Pdf || string.IsNullOrEmpty(f.Ref)) continue;
                try
                {
                    var pages = BinaVibe.Mcp.Tools.PdfAttachmentCache.Use(f.Ref, doc => doc.PageText);
                    var name = list.Any(d => d.Name == f.Name) ? f.Name + " (" + (list.Count + 1) + ")" : f.Name;
                    var data = DocumentAttachment.FromPages(name, pages, Math.Min(DocumentAttachment.MaxChars, left));
                    if (!data.HasText) continue;
                    left -= data.Pages.Sum(t => t.Length);
                    list.Add(data);
                    f.DocumentName = name;
                    f.DocumentPages = data.Pages.Count;
                    f.DocumentTotalPages = data.TotalPages;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[BINA] PDF kept as a summary block: " + ex.Message);
                }
            }
            return list;
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
                        app => BinaVibe.Mcp.Tools.PaneTools.PdfOpenAttachment(d.Path));
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
                var links = await RunLocalToolAsync(BinaVibe.Mcp.Tools.PaneTools.ListCadLinks);
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
                    var result = await RunLocalToolAsync(app => match != null
                        ? BinaVibe.Mcp.Tools.PaneTools.DwgSummary(app, match)
                        : BinaVibe.Mcp.Tools.PaneTools.DwgOpenAttachment(app, d.Path));
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
            Func<Autodesk.Revit.UI.UIApplication, Dictionary<string, object>> work)
        {
            var result = await BinaVibe.Mcp.Tools.PaneTools.RunAsync(work);
            if (result == null) return null;
            var json = System.Text.Json.JsonSerializer.Serialize(result);
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

        private async System.Threading.Tasks.Task ResolveProposalAsync(string routeText, string displayText, string fallbackToolId,
            List<string> images = null, List<HistoryFile> historyFiles = null, List<TableData> tables = null, List<DocumentData> documents = null)
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
                    router.PendingDocuments = documents;
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
            if (rr != null && rr.Superseded) { _queued = null; return; }

            // A failed turn the drafter can act on becomes a card (Sign-in /
            // Attention) instead of a reply bubble carrying the raw reason.
            if (rr != null && rr.Failed)
            {
                var card = CardForFailure(rr, new PromptPayload { Text = displayText });
                if (card != null) { ReplaceLastThinking(card); SendQueued(rr); return; }
            }

            RenderRouteResult(rr, routeText, displayText, fallbackToolId, historyFiles);
            SendQueued(rr);
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
            StreamingReply = !string.IsNullOrWhiteSpace(Unshown(_lastReplyText)),
            Text = Unshown(_lastReplyText),
            LiveSteps = _lastSteps,
        };

        /// <summary>The reply after what is already shown above a confirm card.</summary>
        private string Unshown(string full) =>
            _replyShown == 0 ? full ?? "" : (full ?? "").Length <= _replyShown ? "" : full.Substring(_replyShown).TrimStart();

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
                    Text = Unshown(rr.Reply),
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
                // Exactly the model's text (less what is shown above a confirm card).
                Text = Unshown(rr.Reply),
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
            // The result card, from the envelopes (never the reply text). Attached BEFORE
            // the reply is published to the thread: ReplaceLastThinking rebuilds
            // synchronously, so a card set afterwards would paint the legacy receipt first.
            HL.HighlightSet highlights = null;
            try
            {
                if (rr.Evidence != null)
                {
                    reply.ResultCard = ResultCardModel.From(rr.Evidence, rr.Language, AttachmentName(routeText)).WithServerResult(rr.ServerResult);
                    reply.ResultCard.DocKey = _turnDocKey;
                    reply.ResultCard.Elapsed = ProgressTrail.TotalElapsedText(ProgressTrail.UserVisible(rr.Steps));
                    if (reply.ResultCard.Kind != ResultKind.None)
                        highlights = HL.HighlightSet.From(reply.ResultCard, rr.Evidence.Calls.Select(c => c.Env).ToList());
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] result card: " + ex.Message); }
            ReplaceLastThinking(reply);
            try { if (highlights != null && !highlights.IsEmpty) ModelResultReady?.Invoke(highlights, reply); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] result card: " + ex.Message); }
            var caution = rr.Evidence != null && rr.Evidence.Caution;
            // M-12: the model's words as they were (empty stays empty); the add-in's
            // own note and receipt on their own labelled line.
            var (historyReply, historyNote) = ReceiptTemplates.HistoryOf(rr.Reply, rr.PaneNote, rr.Evidence, rr.Language);
            AppendToCurrentSession(displayText, historyReply, caution ? "warn" : "ok", rr.ToolsUsed, historyFiles, historyNote, reply.ResultCard, rr.Language);
            if (caution) RunStatus = "Check receipt";
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
            _replyShown = 0;
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Thinking, Text = "Thinking…" });
            HookStreaming(router);
            IsSending = true;
            RouteResult rr = null;
            try { rr = await router.SubmitAskAsync(m.Ask, answer); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[BINA copilot] answer not sent: " + ex);
                rr = new RouteResult { PaneNote = "The answer could not be sent. Try again.", FailureKind = RouteFailure.Other };
            }
            finally
            {
                IsSending = false;
                UnhookStreaming(router);
            }
            _ = RefreshUsageAsync();
            if (rr != null && rr.Superseded) { _queued = null; return; }   // the chat changed under the turn (I1)
            if (rr != null && rr.Failed)
            {
                var card = CardForFailure(rr, null);
                if (card != null) { ReplaceLastThinking(card); SendQueued(rr); return; }
            }
            RenderRouteResult(rr, LastPrompt, m.Ask.AnsweredSummary, "ai-generated", null);
            SendQueued(rr);
        }

        /// <summary>IPaneNoticeView (M-2): Revit is waiting on a dialog during a library
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

        private void RunOnUi(Action a) => _pane.RunOnUi(a);

        // ─── Receipt actions ────────────────────────────────────────────────────
        /// <summary>The undo stack changed (the card's button, or Revit's own Undo/Redo): the cards redraw
        /// from the model's truth, so a button never offers what Revit already did.</summary>
        public event Action UndoStateChanged;

        /// <summary>A card's own state changed (a type left as is): have the thread drawn again.</summary>
        public void RequestRedraw()
        {
            try { _pane.BeginOnUi(() => UndoStateChanged?.Invoke()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] redraw: " + ex.Message); }
        }

        // The model that was open when the prompt was sent: the result's buttons that open a sheet or view check it.
        private string _turnDocKey = "";

        private void OnUndoChanged(BinaVibe.Copilot.Undo.UndoChange change)
        {
            try { _pane.BeginOnUi(() => UndoStateChanged?.Invoke()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] undo state: " + ex.Message); }
        }

        /// <summary>Undo the turn's BINA entries (W3's UndoService; one per batch).</summary>
        public bool CanUndoTurn(ChatMessage m)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return false;
            try { return Revit.CanUndoTurn(m.TurnId); }
            catch { return false; }
        }

        /// <summary>L19: why the turn's Undo is off (W3's UndoBlock kind); None when it is available.</summary>
        public BinaVibe.Copilot.Undo.UndoBlock WhyNotUndoTurn(ChatMessage m)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return BinaVibe.Copilot.Undo.UndoBlock.None;
            try { return Revit.WhyNotUndo(m.TurnId); }
            catch (Exception) { return BinaVibe.Copilot.Undo.UndoBlock.Other; }
        }

        /// <summary>Redraw a reply's result in the model (the card's "Highlight in model").</summary>
        public void ShowResultInModel(ChatMessage m)
        {
            if (m?.ResultCard == null || m.Evidence == null || InModel == null) return;   // no overlay wired
            try
            {
                var set = HL.HighlightSet.From(m.ResultCard, m.Evidence.Calls.Select(c => c.Env).ToList());
                if (!set.IsEmpty) Revit.ShowRings(set, m.Lang, () => UndoTurn(m));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] show in model: " + ex.Message); }
        }

        /// <summary>The attachment named in the routed prompt, or null (see <see cref="RouteText.AttachmentName"/>).</summary>
        internal static string AttachmentName(string routeText) => RouteText.AttachmentName(routeText);

        /// <summary>More than one batch is still live: the card offers "Undo last part".</summary>
        public bool CanUndoLastBatch(ChatMessage m)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return false;
            try { return Revit.LiveBatches(m.TurnId) > 1 && Revit.CanUndoLastBatch(m.TurnId); }
            catch { return false; }
        }

        public bool CanRedoTurn(ChatMessage m)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return false;
            try { return Revit.CanRedoTurn(m.TurnId); }
            catch { return false; }
        }

        /// <summary>The result card's editable After cell: one set_parameters batch under the
        /// reply's own turn id, so the turn's Undo takes it back. Null when written, else why not.</summary>
        public async System.Threading.Tasks.Task<string> EditValueAsync(ChatMessage m, TypeGroup g, ChangeRow row, string value)
        {
            var ms = m?.Lang == ReceiptLanguage.Ms;
            if (Tools == null || string.IsNullOrEmpty(m?.TurnId) || g == null || row == null || row.Ids.Count == 0)
                return ms ? "Tidak dapat menulis nilai ini." : "This value cannot be written from here.";
            try
            {
                var call = new BinaVibe.Copilot.Contracts.ToolCallDto
                {
                    CallId = "card_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    IdempotencyKey = "card:" + Guid.NewGuid().ToString("N"),
                    Tool = BinaVibe.Copilot.Contracts.ToolNames.SetParameters,
                    Args = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
                    {
                        ["target"] = new Dictionary<string, object> { ["ids"] = row.Ids.ToArray() },
                        ["param"] = row.Param,
                        ["value"] = value,
                        ["type_param"] = g.IsType,
                    }),
                };
                var envs = await Tools.RunWritesAsync(m.TurnId, "req-" + Guid.NewGuid().ToString("N"), new[] { call },
                    "Edit " + row.Param, Tools.Snapshot(), System.Threading.CancellationToken.None);
                var env = envs?.FirstOrDefault();
                if (env != null && env.Ok && env.Status != BinaVibe.Copilot.Contracts.EnvelopeStatus.Rejected) return null;
                return env?.Error?.Message ?? env?.Summary ?? (ms ? "Tiada keputusan." : "No result.");
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Run a result check's button. set_parameters: one more batch of the reply's own turn (so its Undo covers it),
        /// with the args as the server sent them; prompt: sent as the next message; dismiss is the card's own. Null when done, else why not.</summary>
        public async System.Threading.Tasks.Task<string> RunCheckActionAsync(ChatMessage m, CheckAction action)
        {
            var ms = m?.Lang == ReceiptLanguage.Ms;
            var why = CheckItems.Vet(action);
            if (why != null) return why;
            if (action.Kind == BinaVibe.Copilot.Contracts.ResultActionKinds.Prompt)
            {
                ChatSend(CheckItems.PromptText(action));
                return null;
            }
            if (action.Kind != BinaVibe.Copilot.Contracts.ResultActionKinds.SetParameters) return null;
            if (Tools == null || string.IsNullOrEmpty(m?.TurnId)) return ms ? "Tidak dapat menulis dari sini." : "This cannot be written from here.";
            try
            {
                var call = CheckItems.SetParametersCall(action);
                var envs = await Tools.RunWritesAsync(m.TurnId, "req-" + Guid.NewGuid().ToString("N"), new[] { call },
                    action.Label, Tools.Snapshot(), System.Threading.CancellationToken.None);
                var env = envs?.FirstOrDefault();
                if (env != null && env.Ok && env.Status != BinaVibe.Copilot.Contracts.EnvelopeStatus.Rejected) return null;
                return env?.Error?.Message ?? env?.Summary ?? (ms ? "Tiada keputusan." : "No result.");
            }
            catch (Exception ex) { return ex.Message; }
        }

        private bool StillAllowed(ChatMessage m, bool lastBatchOnly, bool redo)
        {
            try
            {
                if (redo) return Revit.CanRedoTurn(m.TurnId);
                return lastBatchOnly ? Revit.CanUndoLastBatch(m.TurnId) : Revit.CanUndoTurn(m.TurnId);
            }
            catch (Exception) { return false; }
        }

        public void UndoLastBatch(ChatMessage m) => UndoTurn(m, true, false);

        public void RedoTurn(ChatMessage m) => UndoTurn(m, false, true);

        public void UndoTurn(ChatMessage m) => UndoTurn(m, false, false);

        private async void UndoTurn(ChatMessage m, bool lastBatchOnly, bool redo)
        {
            if (string.IsNullOrEmpty(m?.TurnId)) return;
            var ms = m.Lang == ReceiptLanguage.Ms;
            // The click may come from a card drawn before Revit's own Undo/Redo: check the live state
            // first, so it never undoes (or redoes) what is no longer there. The card then redraws.
            if (!StillAllowed(m, lastBatchOnly, redo)) { UndoStateChanged?.Invoke(); return; }
            string text;
            try { Revit.ClearRings(); } catch { /* the undo still runs */ }
            try
            {
                var summary = redo
                    ? await Revit.RedoTurnAsync(m.TurnId)
                    : await Revit.UndoTurnAsync(m.TurnId, lastBatchOnly);
                text = summary ?? (redo ? (ms ? "Buat semula selesai." : "Redone.") : (ms ? "Buat asal selesai." : "Undone."));
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
            var path = _pane.PickSaveFile("Export to Excel",
                "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                SafeFileName(table.Title, "bina-export") + ".xlsx");
            if (path == null) return;
            string text;
            try
            {
                TableXlsx.Write(table, path);
                text = "Exported " + table.Rows.Count + " rows to " + System.IO.Path.GetFileName(path) + ".";
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

            var cfg = _pane.LoadConfig();
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
            _pane.RunOnUi(Apply);
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
