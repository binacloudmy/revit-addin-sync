using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BinaVibe.Mcp;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>
    /// Chat tab. Empty state (greeting, suggested prompts, topic chips, library CTA, how-runs)
    /// or the conversation thread. Proposal/clarify/result message rendering is added in
    /// Tasks 12-13; for now user messages render as bubbles.
    /// </summary>
    public partial class ChatView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        private static readonly (string glyph, string fg, string bg, string text)[] Prompts =
        {
            ("filter",  "#a16207", "#fef3c7", "Rename levels: Level → L"),
            ("door",    "#a16207", "#fef3c7", "Count doors by level"),
            ("warning", "#b91c1c", "#fee2e2", "Find walls missing fire rating"),
            ("layers",  "#4338ca", "#e0e7ff", "Tag all walls in this view"),
            ("table",   "#0369a1", "#e0f2fe", "Export door schedule to Excel"),
            ("warning", "#a16207", "#fef3c7", "Check UBBL room minimums"),
        };
        private static readonly string[] Topics = { "doors", "walls", "fire rating", "rooms", "sheets", "levels" };

        public ChatView()
        {
            InitializeComponent();
            // Element-id clicks (MarkdownRenderer, table cells + bina://select/<id>
            // links) → local select+zoom. Static event, guarded subscribe — see
            // WireElementIdClick.
            WireElementIdClick();
            // Slash command sent from the composer → add the turn (chip bubble +
            // placeholder reply). UI-only until tools run from chat.
            Prompt.SlashToolSubmitted += (tool, args) => Vm?.ChatSendSlashCommand(tool, args);
            // Host the "/" palette as an IN-PANEL overlay (SlashLayer) so it stays
            // inside the pane and tracks resize — the editor shows/hides the layer.
            Prompt.AttachSlashPalette(SlashPalette, v =>
            {
                if (v) UpdateSlashPaletteBounds();
                SlashLayer.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            });
            SlashScrim.MouseLeftButtonDown += (_, __) => Prompt.CloseSlashPalette();
            SizeChanged += (_, __) => { if (SlashLayer.Visibility == Visibility.Visible) UpdateSlashPaletteBounds(); };
            DataContextChanged += (_, __) => Hook();
            // Re-render the (code-behind-drawn) thread when the palette flips —
            // its bubbles snapshot colours via CopilotColors, so unlike the XAML
            // chrome they don't auto-repaint. Subscribe while visible only, so a
            // backgrounded ChatView doesn't leak onto the static event.
            Loaded += (_, __) => { CopilotTheme.ThemeChanged += OnThemeChanged; Rebuild(); };
            Unloaded += (_, __) => { CopilotTheme.ThemeChanged -= OnThemeChanged; };
            // Re-flow bubbles when the PANE is resized (docked narrow ↔ pulled
            // wide) so message width tracks the panel instead of staying at the
            // narrow default. Delta-guarded: rebuilding on every pixel would
            // churn during a drag.
            SizeChanged += (_, e) =>
            {
                if (System.Math.Abs(e.NewSize.Width - _lastLayoutWidth) < 24) return;
                _lastLayoutWidth = e.NewSize.Width;
                Rebuild();
            };
            // Approval card keyboard shortcuts (2026-08-02 spec): Ctrl+Enter
            // allows, Esc rejects — bubbles up from wherever focus is (composer
            // included), so the drafter never has to click into the card.
            PreviewKeyDown += OnApprovalKeyDown;
            // Ctrl+K → command palette (PRD A8), from anywhere in the pane.
            // (Also reachable from the header's search button via OpenPalette.)
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.K
                    && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                {
                    Prompt.OpenCommandPalette();
                    e.Handled = true;
                }
            };
        }

        /// <summary>Open the command palette (header search button / Ctrl+K).</summary>
        public void OpenPalette() => Prompt.OpenCommandPalette();

        // ─── Element-id click → local select+zoom (Task 7) ──────────────────
        // MarkdownRenderer.ElementIdClicked is STATIC and ChatView is cached
        // per CopilotPanel (View<T> in CopilotPanel.xaml.cs) but a new panel
        // instance (pane re-dock, multi-doc host) constructs a new ChatView —
        // guard so a re-construction never stacks a second handler on the
        // static event (which would select+zoom the same click twice).
        private static bool _elementClickWired;

        private static void WireElementIdClick()
        {
            if (_elementClickWired) return;
            _elementClickWired = true;
            RevitWebAppSync.Helpers.MarkdownRenderer.ElementIdClicked += OnElementIdClicked;
        }

        /// <summary>Runs the addin's OWN `select_elements` tool against the live
        /// Revit doc — the SAME McpJob/McpJobPump path ToolLoopRunner.ExecuteOneAsync
        /// uses for every backend-driven tool call (see Services/ToolLoopRunner.cs),
        /// just fired locally with no /tool/generate round-trip and no resume loop
        /// (select_elements is a single fire-and-observe call). `async void` is
        /// deliberate here — this IS the event handler, and a click must never
        /// throw into WPF, so every failure path is swallowed into a status log.</summary>
        private static void OnElementIdClicked(long elementId) => SelectElements(new[] { elementId });

        /// <summary>Also the engine behind the answer's "Highlight in model"
        /// action row (PRD A9), which fires the WHOLE id set in one call.</summary>
        private static async void SelectElements(long[] elementIds)
        {
            if (elementIds == null || elementIds.Length == 0) return;
            try
            {
                var args = System.Text.Json.JsonSerializer.SerializeToElement(
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["element_ids"] = elementIds,
                    });
                var job = new McpJob { Tool = "select_elements", Args = args };
                McpJobPump.Enqueue(job);
                await job.Done.Task.ConfigureAwait(false);
                if (job.Error != null)
                    System.Diagnostics.Debug.WriteLine($"[BinaVibe][chat] select_elements(×{elementIds.Length}) failed: {job.Error}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinaVibe][chat] select_elements(×{elementIds.Length}) threw: {ex.Message}");
            }
        }

        private double _lastLayoutWidth;

        /// <summary>Message column width: ~85% of the visible chat area (minus
        /// the avatar gutter), so wide panes get wide bubbles/tables instead of
        /// a fixed 360px strip. Falls back to the narrow default pre-layout.</summary>
        private double BubbleMaxWidth()
        {
            double w = BodyHost != null && BodyHost.ActualWidth > 0 ? BodyHost.ActualWidth
                     : (Scroller != null ? Scroller.ActualWidth : 0);
            if (w <= 0) return 360;
            return System.Math.Max(320, w * 0.85 - 44);
        }

        // Keep the "/" palette bounded by the pane: cap the whole card to ~64% of
        // the panel height and the inner scrolling list to what's left after the
        // header + footer, so it never covers the tabs or spills past the edges.
        private void UpdateSlashPaletteBounds()
        {
            double h = ActualHeight;
            if (h <= 0) return;
            double cap = System.Math.Max(200, h * 0.64);
            SlashPalette.MaxHeight = cap;
            SlashPalette.SetListMaxHeight(cap - 96);   // ≈ header + footer + margins
        }

        private void OnThemeChanged() => Rebuild();

        // ─── Saved Commands J1: Save/Edit sheet hosting ─────────────────────
        private void OnSaveCommandRequested(SavedCommandDraft d)
        {
            if (Vm == null) return;
            SaveLayer.Visibility = Visibility.Visible;
            SaveSheet.Closed -= OnSaveSheetClosed;
            SaveSheet.Closed += OnSaveSheetClosed;
            SaveSheet.Show(d, Vm.SaveDraftAsync);
        }

        private void OnSaveSheetClosed() => SaveLayer.Visibility = Visibility.Collapsed;

        private void OnSaveScrimClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SaveSheet.Hide();
            SaveLayer.Visibility = Visibility.Collapsed;
        }

        /// <summary>Raised by the blocked state's "Upgrade plan" CTA (the panel opens the sheet).</summary>
        public event System.Action UpgradeRequested;

        private void OnVmUpgrade() => UpgradeRequested?.Invoke();

        private void Hook()
        {
            if (_hooked != null)
            {
                _hooked.Thread.CollectionChanged -= OnThread;
                _hooked.UsageChanged -= UpdateUsage;
                _hooked.PropertyChanged -= OnVmProp;
                _hooked.UpgradeRequested -= OnVmUpgrade;
                _hooked.SaveCommandRequested -= OnSaveCommandRequested;
            }
            _hooked = Vm;
            if (_hooked != null)
            {
                _hooked.Thread.CollectionChanged += OnThread;
                _hooked.UsageChanged += UpdateUsage;
                _hooked.PropertyChanged += OnVmProp;
                _hooked.UpgradeRequested += OnVmUpgrade;
                _hooked.SaveCommandRequested += OnSaveCommandRequested;
                Prompt.BindUsage(_hooked);
                _ = _hooked.RefreshUsageAsync();
                // Saved Commands J1: merge the Mine tier into the palette
                // (ETag-cached; falls back to the persisted rows offline).
                _ = _hooked.RefreshCommandCatalogAsync();
            }
            Rebuild();
            UpdateUsage();
        }

        private void OnVmProp(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.IsSending))
            {
                UpdateUsage();
                UpdateElapsedClock();
            }
        }

        // ─── Subheader elapsed clock (design elapsedLabel) ───────────────────
        // A view-side Stopwatch + DispatcherTimer (never a Storyboard — those
        // crash Revit's dockable pane): starts with the turn, ticks tenths, and
        // freezes on the final value when the turn ends.
        private System.Diagnostics.Stopwatch _elapsedSw;
        private System.Windows.Threading.DispatcherTimer _elapsedTimer;

        private void UpdateElapsedClock()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((System.Action)UpdateElapsedClock);
                return;
            }
            bool sending = Vm != null && Vm.IsSending;
            if (sending)
            {
                if (_elapsedSw != null && _elapsedSw.IsRunning) return;  // resumed turn keeps its clock
                _elapsedSw = System.Diagnostics.Stopwatch.StartNew();
                if (_elapsedTimer == null)
                {
                    _elapsedTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = System.TimeSpan.FromMilliseconds(100) };
                    _elapsedTimer.Tick += (_, __) =>
                    {
                        if (_elapsedSw != null && ElapsedText != null)
                            ElapsedText.Text = _elapsedSw.Elapsed.TotalSeconds.ToString("0.0") + "s";
                    };
                }
                _elapsedTimer.Start();
            }
            else
            {
                _elapsedTimer?.Stop();
                if (_elapsedSw != null)
                {
                    _elapsedSw.Stop();
                    if (ElapsedText != null)
                        ElapsedText.Text = _elapsedSw.Elapsed.TotalSeconds.ToString("0.0") + "s";
                }
            }
        }

        /// <summary>Drives BOTH usage surfaces in the bottom band from one snapshot:
        /// at 100% the composer is replaced by the blocked state (centered over an
        /// empty body, else a bottom section); below that, the near-limit notice may
        /// sit above the composer. They are mutually exclusive by construction.</summary>
        private void UpdateUsage()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((System.Action)UpdateUsage);
                return;
            }
            var vm = Vm;
            bool blocked = vm != null && vm.Usage != null && vm.Usage.AtLimit && !vm.IsSending;
            if (!blocked)
            {
                BlockedHost.Visibility = Visibility.Collapsed;
                BlockedHost.Content = null;
                Prompt.Visibility = Visibility.Visible;
                Scroller.Visibility = Visibility.Visible;
                Grid.SetRow(BlockedHost, 2);
                UpdateNotice(vm, false);
                return;
            }
            bool centered = vm.Thread.Count == 0;
            // Centered means the wall moves INTO the body row — which still holds the
            // greeting and the suggested-prompt rows, so without collapsing it the
            // padlock and CTA render straight on top of "Generate schedule" / "Tag
            // rooms". With a thread present the wall sits in the composer row instead
            // and the conversation behind it should stay readable.
            Scroller.Visibility = centered ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetRow(BlockedHost, centered ? 1 : 2);
            BlockedHost.VerticalAlignment = centered ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            BlockedHost.Content = Controls.BlockedView.Build(
                vm.Usage,
                () => UpgradeRequested?.Invoke(),
                () => vm.UsageService != null ? vm.UsageService.NotifyAdminAsync() : System.Threading.Tasks.Task.CompletedTask,
                centered,
                () => { _ = vm.RefreshUsageAndBadgeAsync(); });
            BlockedHost.Visibility = Visibility.Visible;
            Prompt.Visibility = Visibility.Collapsed;
            UpdateNotice(vm, true);
        }

        /// <summary>Near-limit notice above the composer. Suppressed while blocked
        /// (that wall already states the case) and, in the 80–94 band only, once the
        /// user has dismissed this exact warning — the ≥95 band is deliberately
        /// undismissable so nobody is surprised mid-command.</summary>
        // Band dismissed this session when there's no quota period to key a persisted
        // dismissal to. 0 = none.
        private int _noticeDismissedThisSession;

        private void UpdateNotice(CopilotViewModel vm, bool blocked)
        {
            var u = vm != null ? vm.Usage : null;
            bool show = !blocked && u != null && u.ShouldWarn;

            // The key carries the quota period so a dismissal expires with it. Without
            // resets_at there IS no period to key on, and a bare ":80" would silence
            // the notice forever, across every future month — so in that case fall
            // back to a session-only dismissal: never persisted, never consulted.
            string key = show && !string.IsNullOrEmpty(u.ResetsAt)
                ? u.ResetsAt + ":" + u.WarnBand
                : null;
            if (show && u.WarnBand == Model.UsageState.WarnPct &&
                (_noticeDismissedThisSession == u.WarnBand ||
                 (key != null && Model.CopilotPrefs.Load().IsUsageNoticeDismissed(key))))
                show = false;

            if (!show)
            {
                NoticeHost.Visibility = Visibility.Collapsed;
                NoticeHost.Content = null;
                return;
            }

            int band = u.WarnBand;
            NoticeHost.Content = Controls.UsageWarningBanner.Build(
                u,
                () => UpgradeRequested?.Invoke(),
                () =>
                {
                    if (key != null) Model.CopilotPrefs.Load().DismissUsageNotice(key);
                    else _noticeDismissedThisSession = band;
                    UpdateUsage();
                });
            NoticeHost.Visibility = Visibility.Visible;
        }

        private void OnThread(object s, NotifyCollectionChangedEventArgs e)
        {
            // Rebuild only — the scroll is driven by OnScroll's stick logic (new/taller
            // content raises ExtentHeightChange). Unconditionally scrolling here would
            // yank a user who has scrolled up to read.
            Rebuild();
        }

        // True while the view is pinned to the newest message. Cleared when the user
        // scrolls up; restored when they scroll back to the bottom.
        private bool _stick = true;

        // 40px threshold per the copilot-reasoning-ui spec (README: "scrolling up
        // to read frees the view and scrolling back re-pins" — was 4px, tightened
        // for the reasoning timeline's taller collapse/expand height jumps).
        private const double StickThresholdPx = 40;

        private void OnScroll(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0)
            {
                // A user/layout scroll with no content-size change: update intent.
                _stick = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - StickThresholdPx;
            }
            else if (_stick)
            {
                // Content grew (new message / streaming answer getting taller): follow
                // the bottom, deferred to Loaded priority so it runs after layout settles
                // (the WPF equivalent of the mockup's double requestAnimationFrame).
                Dispatcher.BeginInvoke(
                    new System.Action(() => Scroller?.ScrollToEnd()),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>Re-pin to the bottom after a layout change that ISN'T a new
        /// token/message (the reasoning timeline expanding/collapsing) — the
        /// README calls this out by name as the bug to avoid: a one-shot
        /// distance threshold that never re-pins after such a height jump.
        /// Only acts while the drafter is already stuck to the bottom.</summary>
        internal void RepinIfSticky()
        {
            if (!_stick) return;
            Dispatcher.BeginInvoke(
                new System.Action(() => Scroller?.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private int _lastMsgCount;

        private void Rebuild()
        {
            if (Vm == null || BodyHost == null) return;
            bool empty = Vm.Thread.Count == 0;
            SubHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            BodyHost.Children.Clear();

            // A message was appended (vs. resize/re-hook rebuilds): the new row
            // gets the design's msgRise entrance (fade in + 7px rise).
            bool appended = Vm.Thread.Count > _lastMsgCount;
            _lastMsgCount = Vm.Thread.Count;

            // A new turn just started (the user's message is last, thinking hasn't
            // begun): drop the persistent thinking view so the NEXT run's steps
            // reveal + animate fresh instead of reusing the prior run's rows.
            // 2026-08-02 defect fix: _reasoningView was missing from this reset
            // (only _thinkingView/_progressTrailView were dropped) — the LIVE
            // reasoning card instance, including its rebuild fingerprint
            // (_renderedKey) and open/closed state, survived across turns. A
            // stale _renderedKey left over from the PREVIOUS turn's last render
            // could coincidentally match one of the NEW turn's early keys
            // (both are cheap "streaming|count|..." style strings), silently
            // skipping the very first rebuild(s) of the new card and leaving it
            // showing stale/blank rows from the moment the new turn starts.
            if (empty || (Vm.Thread.Count > 0 && Vm.Thread[Vm.Thread.Count - 1].Role == "user"))
            {
                _thinkingView = null;
                _progressTrailView = null;
                _reasoningView = null;
                _activityView = null;
            }
            if (empty)
            {
                // Per-thread render caches (tool cards / narrative blocks) only
                // clear with the thread itself — mid-thread they are exactly
                // what keeps re-renders from resetting card expand state.
                _threadToolCards.Clear();
                _threadNarratives.Clear();
            }

            if (empty)
            {
                // New chat: the elapsed clock belongs to the cleared thread.
                _elapsedTimer?.Stop();
                _elapsedSw = null;
                if (ElapsedText != null) ElapsedText.Text = "";
                BodyHost.Children.Add(EmptyState());
                return;
            }

            var thread = new StackPanel { Margin = new Thickness(16, 16, 16, 16) };
            foreach (var m in Vm.Thread)
                thread.Children.Add(Message(m));
            if (appended && thread.Children.Count > 0)
                MsgRise(thread.Children[thread.Children.Count - 1] as FrameworkElement);
            BodyHost.Children.Add(thread);
        }

        // Italic faint "Interrupted." row (design lines 175-180).
        private FrameworkElement InterruptedLine(string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 15) };
            var icon = new System.Windows.Shapes.Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                // circle + filled square (stop-in-circle)
                Data = Geometry.Parse("M12,3 A9,9 0 1 1 11.99,3 Z M9,9 H15 V15 H9 Z"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Faint");
            row.Children.Add(icon);
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(text) ? "Interrupted." : text,
                FontSize = 13, FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            row.Children.Add(tb);
            MsgRise(row);
            return row;
        }

        // Design "msgRise": opacity 0→1 + translateY 7→0, .34s ease-out. Direct
        // BeginAnimation on the element (same pattern as the spinner below) —
        // no XAML Storyboard, which crashes inside a Revit dockable pane.
        private static void MsgRise(FrameworkElement el)
        {
            if (el == null) return;
            var tt = new TranslateTransform(0, 7);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = new Duration(TimeSpan.FromMilliseconds(340));
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, dur) { EasingFunction = ease });
        }

        // Liveness affordance pinned under a streaming reply: three pulsing dots
        // (typing-indicator style) so a mid-word pause — backend running tools
        // between tool-loop rounds, 10s+ — reads as "still working", not frozen.
        // Direct BeginAnimation (not a XAML Storyboard, which crashes in a Revit
        // dockable pane — same constraint as MsgRise/the spinner). The dots live
        // only on the live Thinking+StreamingReply bubble; when the turn completes
        // the message flips to a plain reply and this element is never built.
        private static FrameworkElement StreamingDots()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var fill = CopilotColors.From("#99a3b3");
            var dur = new Duration(TimeSpan.FromMilliseconds(560));
            for (int i = 0; i < 3; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 5, Height = 5, Fill = fill, Opacity = 0.3,
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1.0, dur)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 180),
                });
                row.Children.Add(dot);
            }
            return row;
        }

        private FrameworkElement Message(ChatMessage m)
        {
            // User bubble — shared with the History detail view.
            if (m.Role == "user")
                return CopilotMessageBubble.User(
                    m.Text, Vm?.UserFirstName, m.ImagesBase64,
                    m.Files?.Select(Model.HistoryFile.From), BubbleMaxWidth(), m.Time,
                    m.SlashCommand);

            // Cancelled generation — the design's italic faint "Interrupted."
            // line: stop-in-circle icon + italic text, no bubble, no feedback.
            if (m.Interrupted)
                return InterruptedLine(m.Text);

            // AI row — Slate design: full-width plain text column, no avatar.
            var aiRow = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            col.MaxWidth = BubbleMaxWidth();

            // Live "thinking" frame: ONE indicator only (the Slate mockup's
            // thinking/steps design). The streaming trail lives in m.Text as
            // glyph-prefixed lines ("✓ …/▶ …"); ThinkingTrail renders it as a
            // single control — star + step rows + a shimmering active step — so
            // it must NOT also fall through to MarkdownText / a second spinner.
            // Once the reply prose starts streaming (StreamingReply), the trail
            // collapses and the accumulating answer renders in its place.
            // Streaming reasoning timeline (2026-08-02 spec) — present only once
            // the backend actually emits `reasoning` frames this turn; older
            // backends / reasoning-less turns fall through to the pre-existing
            // ProgressTrailPanel/ThinkingTrail indicator untouched below.
            bool hasLiveReasoning = m.Kind == CpMsgKind.Thinking
                && m.LiveReasoningSteps != null && m.LiveReasoningSteps.Count > 0;
            // Stream v2 (T1): the segmented turn body. Non-empty only when this
            // turn's backend tagged reply legs with segment ids (feature-detect)
            // AND the kill switch is on — every other turn renders the legacy
            // paths below byte-identically.
            bool hasBlocks = m.Blocks != null && m.Blocks.Count > 0;

            if (m.Kind == CpMsgKind.Thinking && !m.StreamingReply)
            {
                System.Collections.Generic.ISet<string> claimed = null;
                bool hasLiveEvidence = hasLiveReasoning || (m.LiveSteps != null && m.LiveSteps.Count > 0);
                if (hasLiveEvidence)
                {
                    // v6: ONE agent-activity card — thinking prose + step
                    // checklist + nested tool cards. Design behavior
                    // (2026-08-30): the card stays OPEN with its working bar
                    // for the whole live turn — narrative streaming below is
                    // NOT "the answer starting" anymore, because narration now
                    // streams from round one; auto-collapsing on it hid the
                    // steps and the progress bar for essentially the entire
                    // run. The card collapses when the turn completes (the
                    // persisted AiReply's ActivityBlock starts closed).
                    var activity = ActivityPanel(m, streaming: true, answerStarting: false);
                    claimed = activity.ClaimedToolIds;
                    col.Children.Add(activity);
                }
                else if (!hasBlocks)
                    // No reasoning AND no steps yet — the old single-line
                    // ThinkingTrail placeholder until the first frame lands.
                    col.Children.Add(ThinkingTrail(m.Text));
                if (hasBlocks)
                {
                    // v2 live body: narrative legs (+ any tool card the activity
                    // card did NOT claim) in arrival order, dots underneath.
                    col.Children.Add(BlocksPanel(m, col.MaxWidth, claimed));
                    col.Children.Add(StreamingDots());
                }
                aiRow.Children.Add(col);
                return aiRow;
            }
            if (m.Kind == CpMsgKind.Thinking && m.StreamingReply)
            {
                System.Collections.Generic.ISet<string> claimed = null;
                if (hasLiveReasoning || (m.LiveSteps != null && m.LiveSteps.Count > 0))
                {
                    // Answer streaming, turn still live: the card stays open
                    // with its working bar above the growing answer (design
                    // behavior — see the !StreamingReply branch). streaming
                    // stays true so the busy bar rides the active step until
                    // the persisted AiReply replaces all of this, collapsed.
                    var activity = ActivityPanel(m, streaming: true, answerStarting: false);
                    claimed = activity.ClaimedToolIds;
                    col.Children.Add(activity);
                }
                if (hasBlocks)
                    col.Children.Add(BlocksPanel(m, col.MaxWidth, claimed));
                else if (!string.IsNullOrEmpty(m.Text))
                    col.Children.Add(CopilotMessageBubble.MarkdownText(m.Text, col.MaxWidth));
                // Pin the pulsing-dots liveness indicator below the partial prose.
                // Still Kind=Thinking, so this only shows while the turn runs; the
                // final reply (Kind flips off Thinking) never renders it.
                col.Children.Add(StreamingDots());
                aiRow.Children.Add(col);
                return aiRow;
            }

            // Design: a completed reply shows the answer. When m.Steps is populated
            // (final AI replies from the backend), a collapsed chip above the text
            // shows "✓ N · Xs" plus the key step's name and a rotating chevron;
            // clicking expands a ProgressTrailView timeline below it.
            // The live single-line thinking indicator fades out and no step trail
            // persists otherwise. (ProgressTracePanel/ToolTracePanel remain for old
            // serialized history.)

            // Persisted reasoning timeline (2026-08-02 spec) — a completed turn's
            // working narrative, re-expandable from history. Sits above BOTH the
            // approval card and the answer (ConfirmActions and AiReply both carry
            // ReasoningSteps). Omitted entirely when the turn had no reasoning
            // frames — no empty shell.
            //
            // ONE card per TURN (2026-08-02 defect #3 fix): a MUTATE/codegen
            // approval pause and the AiReply that follows it once resolved BOTH
            // carry a ReasoningSteps snapshot from the SAME underlying
            // reasoningTrail — Confirm at pause time, AiReply at completion —
            // so rendering both produced two near-identical "Thinking Ns · N
            // steps" cards for one turn (the second appearing right after the
            // approval card resolved). A ConfirmActions card only shows its OWN
            // reasoning block while it's still the newest thing in the thread;
            // the instant the turn continues past it (resume leg -> a later
            // Thinking/AiReply message gets added), it defers to that later
            // message's card instead of duplicating. AiReply doesn't need the
            // same guard — a turn produces at most one AiReply, ever.
            bool showReasoningBlock = m.ReasoningSteps != null && m.ReasoningSteps.Count > 0
                && (m.Kind == CpMsgKind.AiReply
                    || (m.Kind == CpMsgKind.ConfirmActions && IsThreadTail(m)));
            System.Collections.Generic.ISet<string> claimedTools = null;
            if (showReasoningBlock)
            {
                // v6: the persisted card carries the WHOLE run's evidence —
                // prose + steps + nested tool cards (ActivityBlock replaces the
                // old reasoning-only ReasoningBlock).
                var activity = ActivityBlock(m);
                claimedTools = activity.ClaimedToolIds;
                col.Children.Add(activity);
            }

            // Progress trail pill: collapsed expandable summary on final AI replies
            // only (not Clarify/Proposal/Running/Result — those carry Steps too but
            // render their own cards, so the pill would be a stray duplicate).
            // v6 dedupe (2026-08-20 parity pass): when the turn ALSO carries a
            // reasoning trail, the Agent-activity card above already presents
            // the run's evidence — a second "N · Xs · label" chip under it read
            // as clutter in the JKR-audit screenshot. Steps stay reachable for
            // reasoning-less turns (old backends) exactly as before.
            if (m.Kind == CpMsgKind.AiReply && m.Steps != null && m.Steps.Count > 0
                && (m.ReasoningSteps == null || m.ReasoningSteps.Count == 0))
            {
                // ── Collapsed chip ────────────────────────────────────────────
                // Redesigned 2026-07-27. The old pill stretched the full column
                // width, so a one-line summary rendered as a wide empty bar
                // heavier than the answer beneath it; it concatenated the ▸ into
                // the text string; and it said only "N langkah · Xs" — nothing
                // about WHAT ran, so checking whether the copilot had read the
                // right things meant expanding it on every single turn. It also
                // put a Malay noun over English answers once replies started
                // mirroring the user's language.
                //
                // Now: auto-width so it takes only the room it needs, a wordless
                // count·duration, the key step's name beside it, and a chevron
                // that ROTATES instead of a character that gets swapped.
                string trailSummary = ProgressTrail.Summary(m.Steps);
                string trailPreview = ProgressTrail.Preview(m.Steps);
                var trailView = new ProgressTrailView();
                trailView.Update(m.Steps);
                trailView.Visibility = Visibility.Collapsed;

                var chipRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var summaryText = new TextBlock
                {
                    Text = trailSummary,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                summaryText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                chipRow.Children.Add(summaryText);

                if (!string.IsNullOrEmpty(trailPreview))
                {
                    var previewText = new TextBlock
                    {
                        Text = trailPreview,
                        FontSize = 11,
                        Margin = new Thickness(8, 0, 0, 0),
                        MaxWidth = 240,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    // Fainter than the summary: a hint, not a headline.
                    previewText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                    chipRow.Children.Add(previewText);
                }

                // Chevron as a Path, not a glyph character: the rotation is
                // smooth and the shape is identical in both states.
                var chevron = new System.Windows.Shapes.Path
                {
                    Width = 8,
                    Height = 8,
                    Stretch = Stretch.Uniform,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse("M 3,1 L 7,5 L 3,9"),
                    Margin = new Thickness(9, 0, 1, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Faint");
                var chevronSpin = new RotateTransform(0);
                chevron.RenderTransform = chevronSpin;
                chipRow.Children.Add(chevron);

                // A Border, not a Button: FlatButton's template carries an
                // IsMouseOver trigger that tints the background Cp.Hover, and
                // that highlight was rejected (2026-07-27) — on a chip this
                // small it flashes a grey slab under the text. A Border has no
                // control chrome to suppress, so there is simply nothing to
                // hover. Transparent background for the same reason: the chip
                // now reads as plain text with a chevron, and the answer keeps
                // every bit of the visual weight.
                var chipButton = new Border
                {
                    Child = chipRow,
                    Padding = new Thickness(0, 2, 6, 2),
                    // Hug the content instead of spanning the bubble column.
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 7),
                    Background = Brushes.Transparent,   // still hit-testable
                    Cursor = System.Windows.Input.Cursors.Hand,
                };

                chipButton.MouseLeftButtonUp += (_, __) =>
                {
                    bool expanding = trailView.Visibility == Visibility.Collapsed;
                    trailView.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                    chevronSpin.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(expanding ? 0 : 90, expanding ? 90 : 0,
                            new Duration(TimeSpan.FromMilliseconds(140)))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        });
                    // Fade the rows in so the panel does not pop into place.
                    if (expanding)
                        trailView.BeginAnimation(UIElement.OpacityProperty,
                            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
                };

                col.Children.Add(chipButton);
                col.Children.Add(trailView);
            }

            // Stream v2: a completed v2 turn keeps its segmented body — ordered
            // narrative blocks + tool cards — instead of collapsing back into
            // one blob. Same one-card-per-turn guard as the reasoning block: a
            // ConfirmActions message only shows the thread while it's still the
            // tail; once the turn continues past it, the later message carries
            // the full (longer) block list and this one defers. Copy still
            // hands over m.Text — the full accumulated reply.
            bool renderBlocks = hasBlocks
                && (m.Kind == CpMsgKind.AiReply
                    || (m.Kind == CpMsgKind.ConfirmActions && IsThreadTail(m)));
            if (renderBlocks)
            {
                col.Children.Add(BlocksPanel(m, col.MaxWidth, claimedTools));
                if (!string.IsNullOrEmpty(m.Text))
                {
                    CopilotMessageBubble.AttachCopyMenu(col, m.Text);
                    col.Children.Add(CopilotMessageBubble.HoverReveal(aiRow, CopilotMessageBubble.CopyButton(m.Text)));
                }
            }
            else if (!string.IsNullOrEmpty(m.Text))
            {
                // AI replies are markdown (headers, **bold**, tables, lists) —
                // render formatted, not as raw text. User bubbles stay plain.
                col.Children.Add(CopilotMessageBubble.MarkdownText(m.Text, col.MaxWidth));
                // Copyable: right-click → Copy message, plus a hover ⧉ button.
                // Thinking bubbles are excluded (their text is the live trail).
                if (m.Kind != CpMsgKind.Thinking)
                {
                    CopilotMessageBubble.AttachCopyMenu(col, m.Text);
                    col.Children.Add(CopilotMessageBubble.HoverReveal(aiRow, CopilotMessageBubble.CopyButton(m.Text)));
                }
            }

            // Result card (2026-08-02 spec): proportion-bar breakdown (when the
            // done frame carried a structured result_summary) + follow-up chips
            // (independent — a turn can offer follow-ups with no bars) + an Undo
            // chip whenever a structured write result is shown + the tindakan
            // one-tap next-step offer, ALSO as a chip (2026-08-02 defect #4 fix:
            // unified into this one chip row — no more separate blue "✓ Ya,
            // teruskan"/"Tidak" buttons rendering alongside the new cards). Sits
            // between the answer and the feedback row. Omitted entirely when the
            // turn has none of the above — no empty shell.
            bool hasResultBars = m.ResultSummary != null && m.ResultSummary.Rows.Count > 0;
            bool hasFollowups = m.Followups != null && m.Followups.Count > 0;
            // Legacy single-string fallback only — once the backend sends the
            // multi-bullet Followups list, Tindakan is just its mirrored first
            // item (see ChatMessage.Tindakan) and would render as a duplicate
            // chip if shown alongside the real list, so it's gated on
            // !hasFollowups (2026-08-02 old-backend-compat pass).
            bool hasTindakan = m.Kind == CpMsgKind.AiReply && !hasFollowups && !string.IsNullOrWhiteSpace(m.Tindakan)
                && !m.TindakanResolved && IsLastAiReply(m);
            // Turn receipt (2026-08-18): deterministic change evidence — counts
            // from the transaction, [Tunjuk semula]/[Undo], optional
            // before/after thumbnails. Above the summary bars: proof first.
            if (m.Kind == CpMsgKind.AiReply && m.Receipt != null)
            {
                col.Children.Add(ReceiptCard(m.Receipt));
            }
            // "Highlight in model" action row (PRD A9): when the answer lists
            // 2+ clickable element ids, offer the whole set as one selection —
            // the per-id click stays for single elements. Same local
            // select_elements path, no backend round-trip.
            if (m.Kind == CpMsgKind.AiReply && !string.IsNullOrEmpty(m.Text))
            {
                var elementIds = RevitWebAppSync.Helpers.MarkdownRenderer.ExtractElementIds(m.Text);
                if (elementIds.Count >= 2)
                    col.Children.Add(HighlightRow(elementIds));
            }
            if (m.Kind == CpMsgKind.AiReply && (hasResultBars || hasFollowups || hasTindakan))
            {
                col.Children.Add(ResultSummaryCard(m, hasResultBars, hasFollowups, hasTindakan, col.MaxWidth));
            }

            switch (m.Kind)
            {
                // NOTE: Thinking is handled above (ThinkingTrail) and returns early —
                // there is deliberately no Thinking case here, so the panel can
                // never render a second loading indicator for one message.
                case CpMsgKind.Clarify: col.Children.Add(ClarifyCardSafe(m)); break;
                case CpMsgKind.SignIn:
                case CpMsgKind.Attention: col.Children.Add(NoticeCard(m)); break;
                // Auto mode means auto: an approval card for something nobody was
                // asked to approve is noise, and on a build that is dozens of
                // writes it buries the actual reply (UAT 2026-08-06 — a tower
                // build filled the pane with "Needs permission → Allowed" cards
                // the drafter never interacted with). The step list still shows
                // in the thinking trail, so nothing is hidden.
                case CpMsgKind.ConfirmActions:
                    if (!m.AutoApproved) col.Children.Add(ConfirmActionsCard(m));
                    break;
                case CpMsgKind.Proposal: col.Children.Add(ProposalCard(m)); break;
                case CpMsgKind.Running: col.Children.Add(RunningBar(m)); break;
                case CpMsgKind.Result:
                    col.Children.Add(CompactResult(m));
                    // Design: the rating nudge hangs under the latest APPLIED card.
                    var resultNudge = BuildRatingNudge(m);
                    if (resultNudge != null) col.Children.Add(resultNudge);
                    break;
            }

            // Reviewer verdict badge (PRD §6.2 stage 7). Tiny line
            // under the trace: green when verified, amber with issue
            // count when not. Drafter can ignore it most of the time
            // — it's audit signal, not a blocker.
            if (m.Verdict != null)
            {
                string badge;
                string color;
                if (m.Verdict.Verified)
                {
                    badge = "verified ✓" + (m.Verdict.Remediated ? " (remediated)" : "");
                    color = "#10b981";  // green
                }
                else
                {
                    int n = m.Verdict.Issues != null ? m.Verdict.Issues.Count : 0;
                    badge = $"review: {n} issue" + (n == 1 ? "" : "s") + " ⚠"
                        + (m.Verdict.Remediated ? " (remediation didn't fully clear)" : "");
                    color = "#d97706";  // amber
                }
                col.Children.Add(new TextBlock
                {
                    Text = badge,
                    FontSize = 10,
                    Foreground = CopilotColors.From(color),
                    FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            // Feedback (👍/👎) — only on final AI replies (not thinking/clarify/
            // proposal/running/result frames). Mirrors ResultView.BuildFeedback:
            // right-aligned row, tint-on-click, disable-after-one-send. The chat
            // is the higher-traffic path, so this is where most signal comes from.
            if (m.Kind == CpMsgKind.AiReply)
            {
                // Saved Commands J1 (A1): "Save as command" on a completed reply
                // that actually ran tools from a natural-language prompt. Pure
                // Q&A (0 tools), slash-command turns and interrupted replies
                // don't qualify; signed-out shows the sign-in card instead
                // (handled in the VM).
                bool canSave = m.ToolsUsed != null && m.ToolsUsed.Count > 0
                            && !string.IsNullOrWhiteSpace(m.SourcePrompt)
                            && !m.SourcePrompt.TrimStart().StartsWith("/")
                            && !m.Interrupted;
                if (canSave)
                {
                    var saveRow = new StackPanel
                    { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                    saveRow.Children.Add(CopilotMessageBubble.SaveCommandButton(
                        () => Vm?.OpenSaveCommandSheet(m)));
                    col.Children.Add(saveRow);
                }
                col.Children.Add(BuildFeedback(m, SourcePromptFor(m)));
                var nudge = BuildRatingNudge(m);
                if (nudge != null) col.Children.Add(nudge);
            }

            aiRow.Children.Add(col);
            return aiRow;
        }

        // ─── Tindakan (one-tap next-step offer) ─────────────────────────────
        // m is the last message in Vm.Thread with Kind == AiReply. Gates the
        // button row so a stale offer on an older bubble (superseded by a
        // newer answer) never renders live buttons.
        private bool IsLastAiReply(ChatMessage m)
        {
            if (Vm == null) return false;
            for (int i = Vm.Thread.Count - 1; i >= 0; i--)
            {
                if (Vm.Thread[i].Kind == CpMsgKind.AiReply)
                    return ReferenceEquals(Vm.Thread[i], m);
            }
            return false;
        }

        // True when m is the very last message in the whole thread — used to
        // stop a superseded ConfirmActions card from re-rendering its own
        // reasoning block once a later message in the same turn (a resume
        // leg's continuation) has taken over that role (defect #3, one
        // reasoning card per turn).
        private bool IsThreadTail(ChatMessage m) =>
            Vm != null && Vm.Thread.Count > 0 && ReferenceEquals(Vm.Thread[Vm.Thread.Count - 1], m);

        // ─── Inline rating nudge ─────────────────────────────────────────────
        // A gentle one-time prompt under the LATEST reply, inviting a star rating
        // (which the Rate sheet collects + persists). Shown only until the user
        // rates (CopilotPrefs.RatingSubmitted) and suppressed for the rest of the
        // session once dismissed, so it never nags. Distinct from the 👍/👎 row:
        // that scores a single answer; this scores the whole Copilot experience.
        private bool _nudgeDismissed;

        private FrameworkElement BuildRatingNudge(ChatMessage reply)
        {
            if (Vm == null || _nudgeDismissed) return null;
            var prefs = CopilotPrefs.Load();
            if (prefs.RatingSubmitted) return null;
            // Only under the newest message, and only once the drafter has sent
            // RatingNudgeMinPrompts prompts across all sessions — two exchanges
            // was far too early to ask (2026-08-30).
            if (Vm.Thread.Count == 0 || !ReferenceEquals(Vm.Thread[Vm.Thread.Count - 1], reply)) return null;
            if (prefs.PromptsSent < CopilotPrefs.RatingNudgeMinPrompts) return null;

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = CopilotColors.From("#f7f9fb"),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 8, 8),
                Margin = new Thickness(0, 12, 0, 0),
            };

            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Child = inner;

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform, Fill = CopilotColors.From("#f59e0b"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
                Data = Geometry.Parse("M12,2 l3.1,6.3 6.9,1 -5,4.9 1.2,6.8 L12,17.8 5.8,21 7,14.2 2,9.3 l6.9,-1 Z")
            });
            left.Children.Add(new TextBlock
            {
                Text = "How's Copilot doing?", FontSize = 11.5, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.From("#586273"), VerticalAlignment = VerticalAlignment.Center
            });
            inner.Children.Add(left);

            var rate = new Button
            {
                Content = "Rate",
                Foreground = CopilotColors.From("#1d4ed8"), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            Controls.FlatButton.Apply(rate, 6);
            rate.Click += (_, __) => { try { Vm.RequestRate(); } catch { /* best-effort */ } };
            Grid.SetColumn(rate, 1);
            inner.Children.Add(rate);

            var dismiss = new Button
            {
                Padding = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new Path
                {
                    Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                    Stroke = CopilotColors.From("#99a3b3"),
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M5,5 L15,15 M15,5 L5,15")
                }
            };
            Controls.FlatButton.Apply(dismiss, 6);
            dismiss.Click += (_, __) => { _nudgeDismissed = true; Rebuild(); };
            Grid.SetColumn(dismiss, 2);
            inner.Children.Add(dismiss);

            return card;
        }

        // ─── Feedback (👍/👎) ────────────────────────────────────────────────
        // Same control as ResultView's result screen, ported to chat bubbles.
        // Clicking a thumb fires the VM's fire-and-forget POST, tints the chosen
        // thumb (green up / red down) and disables both so one rating is sent.
        //
        // sourcePrompt is the user message this reply answered — captured here so
        // the rating is attributed to the right prompt. See the LastPrompt note
        // in SendFeedback below.
        // Design micro-feedback block (lines 221-262): time · "Was this helpful?"
        // · 👍 👎 ⧉ — a silent up-vote toggle, a down-vote reason panel with chips
        // + note + auto-attached context, a copied-check copy button, and a
        // "Thanks" line once submitted.
        private FrameworkElement BuildFeedback(ChatMessage m, string sourcePrompt)
        {
            var host = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.Children.Add(row);

            if (!string.IsNullOrWhiteSpace(m.Time))
            {
                var time = new TextBlock
                {
                    Text = m.Time, FontSize = 10, Foreground = CopilotColors.From("#99a3b3"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(time);
            }

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            // "{t}s · {n} actions" (2026-08-02 spec) — reuses the SAME m.Steps
            // trail the collapsed progress chip already carries, so it's exactly
            // what actually ran this turn, not a re-derived count.
            if (m.Steps != null && m.Steps.Count > 0)
            {
                var metric = new TextBlock
                {
                    Text = ProgressTrail.TotalElapsedText(m.Steps) + " · " + m.Steps.Count + (m.Steps.Count == 1 ? " action" : " actions"),
                    FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                };
                metric.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
                metric.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                right.Children.Add(metric);
            }

            var promptLabel = new TextBlock
            {
                Text = "Useful?", FontSize = 10.5,
                Foreground = CopilotColors.From("#99a3b3"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            };
            right.Children.Add(promptLabel);

            string vote = null;
            var panelHost = new ContentControl();
            var thanksHost = new ContentControl();
            host.Children.Add(panelHost);
            host.Children.Add(thanksHost);

            Button up = null, down = null;
            Path upIcon = null, downIcon = null;

            void PaintVotes()
            {
                var accent = CopilotColors.From("#1d4ed8");
                var idle = CopilotColors.From("#99a3b3");
                if (upIcon != null) upIcon.Stroke = vote == "up" ? accent : idle;
                if (downIcon != null) downIcon.Stroke = vote == "down" ? accent : idle;
                promptLabel.Visibility = vote == null && thanksHost.Content == null
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            up = FeedbackIconButton("thumbUp", out upIcon, () =>
            {
                // Silent toggle (design voteUp): highlight only, close any panel.
                vote = vote == "up" ? null : "up";
                panelHost.Content = null;
                if (vote == "up") Vm?.SubmitFeedback("up", sourcePrompt);
                PaintVotes();
            });
            down = FeedbackIconButton("thumbDown", out downIcon, () =>
            {
                if (vote == "down") { vote = null; panelHost.Content = null; PaintVotes(); return; }
                vote = "down";
                panelHost.Content = BuildDownvotePanel(m, sourcePrompt,
                    close: () => { panelHost.Content = null; },
                    submitted: () =>
                    {
                        panelHost.Content = null;
                        thanksHost.Content = ThanksLine();
                        PaintVotes();
                    });
                PaintVotes();
            });
            right.Children.Add(up);
            right.Children.Add(down);

            Path copyIcon = null;
            Button copy = null;
            copy = FeedbackIconButton("copy", out copyIcon, () =>
            {
                try { Clipboard.SetText(m.Text ?? ""); } catch { /* clipboard can be locked */ }
                copyIcon.Data = Geometry.Parse("M20,6 L9,17 L4,12");
                copyIcon.Stroke = CopilotColors.From("#10b981");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(1600) };
                t.Tick += (_, __) =>
                {
                    t.Stop();
                    copyIcon.Data = CopilotIcons.Get("copy");
                    copyIcon.Stroke = CopilotColors.From("#99a3b3");
                };
                t.Start();
            });
            right.Children.Add(copy);

            PaintVotes();
            return host;
        }

        private Button FeedbackIconButton(string glyph, out Path icon, System.Action onClick)
        {
            icon = new Path
            {
                Width = 14, Height = 14, Stretch = Stretch.Uniform,
                Stroke = CopilotColors.From("#99a3b3"), StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Data = CopilotIcons.Get(glyph),
            };
            var btn = new Button
            {
                Content = icon, Width = 27, Height = 27,
                Margin = new Thickness(3, 0, 0, 0),
            };
            // Flat chrome: no default WPF button box/focus rect (that light-blue
            // highlight), just a theme-aware hover tint. Selected state is the icon
            // colour (PaintVotes), never a background box — matches the design.
            Controls.FlatButton.Apply(btn, 7);
            btn.Click += (_, __) => { try { onClick(); } catch { /* best-effort */ } };
            return btn;
        }

        // "What was off?" — reason chips + optional note + Send/Cancel + the
        // auto-attached context row (design lines 240-258).
        private FrameworkElement BuildDownvotePanel(ChatMessage m, string sourcePrompt,
            System.Action close, System.Action submitted)
        {
            var card = new Border
            {
                Background = CopilotColors.From("#f7f9fb"),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
                Padding = new Thickness(11, 11, 11, 10), Margin = new Thickness(0, 9, 0, 0),
            };
            MsgRise(card);
            var sp = new StackPanel();
            card.Child = sp;

            sp.Children.Add(new TextBlock
            {
                Text = "What was off?", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#586273"), Margin = new Thickness(0, 0, 0, 8),
            });

            string reason = null;
            var chipsWrap = new WrapPanel();
            var chips = new System.Collections.Generic.List<(Border chip, TextBlock label, string value)>();
            foreach (var r in new[] { "Not accurate", "Wrong elements", "Too slow", "Other" })
            {
                var label = new TextBlock { Text = r, FontSize = 11, FontWeight = FontWeights.Medium };
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                    Cursor = System.Windows.Input.Cursors.Hand, Child = label,
                };
                chips.Add((chip, label, r));
                chipsWrap.Children.Add(chip);
            }
            void PaintChips()
            {
                foreach (var (chip, label, value) in chips)
                {
                    bool on = value == reason;
                    chip.Background = on ? CopilotColors.From("#1A1D4ED8") : Brushes.Transparent;
                    chip.BorderBrush = on ? Brushes.Transparent : CopilotColors.From("#140F1B2D");
                    label.Foreground = CopilotColors.From(on ? "#1d4ed8" : "#586273");
                }
            }
            foreach (var (chip, _, value) in chips)
                chip.MouseLeftButtonDown += (_, __) => { reason = reason == value ? null : value; PaintChips(); };
            PaintChips();
            sp.Children.Add(chipsWrap);

            var note = new TextBox
            {
                FontSize = 11.5, MinHeight = 40, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 3, 0, 0),
                Background = CopilotColors.From("#ffffff"), Foreground = CopilotColors.From("#131c2b"),
                BorderBrush = CopilotColors.From("#140F1B2D"), CaretBrush = CopilotColors.From("#131c2b"),
            };
            // Placeholder: faint hint that clears on focus (WPF TextBox has none).
            var hint = "Add details (optional)";
            note.Text = hint; note.Foreground = CopilotColors.From("#99a3b3");
            note.GotFocus += (_, __) => { if (note.Text == hint) { note.Text = ""; note.Foreground = CopilotColors.From("#131c2b"); } };
            note.LostFocus += (_, __) => { if (note.Text.Length == 0) { note.Text = hint; note.Foreground = CopilotColors.From("#99a3b3"); } };
            sp.Children.Add(note);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            var send = new Button
            {
                Content = new TextBlock { Text = "Send feedback", FontSize = 11, FontWeight = FontWeights.SemiBold },
                Padding = new Thickness(13, 7, 13, 7), BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand, Foreground = CopilotColors.From("#ffffff"),
            };
            send.SetResourceReference(BackgroundProperty, "Cp.AccentGrad");
            send.Resources.Add(typeof(Border), RoundedButtonBorderStyle(8));
            send.Click += (_, __) =>
            {
                var noteText = note.Text == hint ? null : (string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim());
                Vm?.SubmitFeedback("down", sourcePrompt, reason, noteText);
                submitted();
            };
            actions.Children.Add(send);
            var cancel = new Button
            {
                Content = "Cancel", FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.From("#99a3b3"), Padding = new Thickness(8, 7, 8, 7),
            };
            Controls.FlatButton.Apply(cancel, 6);
            cancel.Click += (_, __) => close();
            actions.Children.Add(cancel);
            sp.Children.Add(actions);

            // Auto-attached context row under a hairline.
            var ctx = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                Padding = new Thickness(0, 9, 0, 0), Margin = new Thickness(0, 10, 0, 0),
            };
            var ctxRow = new StackPanel { Orientation = Orientation.Horizontal };
            var clipIcon = new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stroke = CopilotColors.From("#99a3b3"), Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse("M21.44,11.05 L12.25,20.24 A6,6 0 0 1 3.76,11.75 L12.33,3.18 A4,4 0 0 1 18,8.84 L9.41,17.41 A2,2 0 0 1 6.58,14.58 L15.07,6.1"),
            };
            ctxRow.Children.Add(clipIcon);
            string cmd = m.ToolId != null && m.ToolId != "ai-generated" ? m.ToolId : null;
            ctxRow.Children.Add(new TextBlock
            {
                Text = Model.CopilotContext.ContextLabel(cmd),
                FontSize = 9.5, Foreground = CopilotColors.From("#99a3b3"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ctx.Child = ctxRow;
            sp.Children.Add(ctx);

            return card;
        }

        private FrameworkElement ThanksLine()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            var check = new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M20,6 L9,17 L4,12"), Stroke = CopilotColors.From("#1d4ed8"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            row.Children.Add(check);
            row.Children.Add(new TextBlock
            {
                Text = "Thanks — your feedback helps improve BINA.",
                FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.From("#1d4ed8"), VerticalAlignment = VerticalAlignment.Center,
            });
            MsgRise(row);
            return row;
        }

        // Rounded-corner style for coded buttons (WPF's default template squares them).
        private static Style RoundedButtonBorderStyle(double radius)
        {
            var s = new Style(typeof(Border));
            s.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(radius)));
            return s;
        }

        // The user message that this AI reply answered: the nearest preceding
        // "user" message in the thread. Captured at build time for correct
        // per-bubble attribution (see SendFeedback). Null when none precedes it.
        private string SourcePromptFor(ChatMessage reply)
        {
            if (Vm == null) return null;
            int idx = Vm.Thread.IndexOf(reply);
            if (idx < 0) idx = Vm.Thread.Count;
            for (int i = idx - 1; i >= 0; i--)
                if (Vm.Thread[i].Role == "user" && !string.IsNullOrEmpty(Vm.Thread[i].Text))
                    return Vm.Thread[i].Text;
            return null;
        }

        // ─── Thinking trail (THE single loading indicator) ──────────────────
        // Reuses ONE persistent ThinkingTrailView per thinking session so steps
        // reveal one-by-one (each new step animates in with `stepIn`) instead of
        // the whole list re-rendering every progress tick. The instance is reset
        // (see Rebuild) when a new turn starts. On reply, the VM flags the message
        // StreamingReply and this block is replaced by the answer (think-out).
        private ThinkingTrailView _thinkingView;

        private FrameworkElement ThinkingTrail(string text)
        {
            if (_thinkingView == null) _thinkingView = new ThinkingTrailView();
            // The parent thread StackPanel is rebuilt each tick; detach the
            // persistent view from its previous parent before re-parenting so
            // WPF doesn't throw "element already has a logical parent".
            else if (_thinkingView.Parent is Panel oldParent)
                oldParent.Children.Remove(_thinkingView);
            _thinkingView.Update(text);
            return _thinkingView;
        }

        // Same one-instance-per-turn caching as ThinkingTrail/_thinkingView above
        // (see Rebuild's reset when a new turn starts) — Rebuild fires on EVERY
        // thread mutation, so a fresh ProgressTrailView per call would recreate
        // (and re-animate) every row on every tick instead of updating in place.
        private ProgressTrailView _progressTrailView;

        private FrameworkElement ProgressTrailPanel(System.Collections.Generic.IReadOnlyList<ProgressStep> steps)
        {
            // Live=true: while the turn runs, show ONE line for the current step
            // instead of a growing stack (and one spinner, not one per row —
            // several rows could hold State=Running at once). Nothing is lost:
            // the completed reply's chip expands into the full timeline from its
            // own ProgressTrailView with Live=false. See ProgressTrail.Current.
            if (_progressTrailView == null) _progressTrailView = new ProgressTrailView { Live = true };
            else if (_progressTrailView.Parent is Panel oldParent)
                oldParent.Children.Remove(_progressTrailView);
            _progressTrailView.Update(steps);
            return _progressTrailView;
        }

        // ─── Streaming reasoning timeline (2026-08-02 spec) ──────────────────
        // Same one-instance-per-turn caching as _thinkingView/_progressTrailView
        // above (reset when a new turn starts — see Rebuild) so the expand/
        // collapse state and its animations survive re-parenting across ticks.
        private ReasoningTimelineView _reasoningView;

        private FrameworkElement ReasoningTimelinePanel(System.Collections.Generic.IReadOnlyList<ReasoningStep> steps, bool streaming, bool answerStarting)
        {
            if (_reasoningView == null)
            {
                _reasoningView = new ReasoningTimelineView();
                _reasoningView.OnLayoutChanged = RepinIfSticky;
            }
            else if (_reasoningView.Parent is Panel oldParent)
                oldParent.Children.Remove(_reasoningView);
            _reasoningView.Update(steps, streaming, answerStarting);
            return _reasoningView;
        }

        // ─── v6 Agent-activity card (2026-08-20 parity pass) ─────────────────
        // ONE card per turn holding thinking prose + the step checklist with
        // nested tool cards — replaces the separate reasoning card + progress
        // chip + orphan cards. The LIVE instance is cached per turn (same
        // lifecycle as _reasoningView); completed messages build a fresh,
        // collapsed one per Rebuild, but their tool cards come from the shared
        // per-thread cache so expand state survives re-renders.
        private AgentActivityView _activityView;
        private readonly System.Collections.Generic.Dictionary<string, ToolResultCard> _threadToolCards =
            new System.Collections.Generic.Dictionary<string, ToolResultCard>(System.StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, (int len, double width, FrameworkElement el)> _threadNarratives =
            new System.Collections.Generic.Dictionary<string, (int, double, FrameworkElement)>(System.StringComparer.Ordinal);

        // Cache key for one execution's card. tool_call_ids are per-run nonces
        // (globally unique), so a card keyed on one survives the live-Thinking →
        // persisted-AiReply hand-off with its expand state. Id-less events fall
        // back to tool name SCOPED to the message — leg/tool names repeat
        // across turns, and an unscoped name key would re-parent an old turn's
        // card into the new one.
        private static string CardKey(ChatMessage m, RevitWebAppSync.Services.ToolResultEvent ev) =>
            !string.IsNullOrEmpty(ev.ToolCallId) ? ev.ToolCallId
                : m.GetHashCode().ToString() + ":" + (ev.Tool ?? "");

        private ToolResultCard GetToolCard(ChatMessage m, RevitWebAppSync.Services.ToolResultEvent ev)
        {
            var key = CardKey(m, ev);
            if (!_threadToolCards.TryGetValue(key, out var card))
                _threadToolCards[key] = card = new ToolResultCard(ev);
            return card;
        }

        /// <summary>Step-id → nested tool card resolver for one message: matches
        /// a step to its execution by tool_call_id (locally-run batches key their
        /// trail rows off exactly that id) or, failing that, the raw tool name
        /// (the parser's step-id fallback). Cards are created once per thread.</summary>
        private System.Func<string, ToolResultCard> ToolCardResolver(ChatMessage m)
        {
            var events = new System.Collections.Generic.Dictionary<string, RevitWebAppSync.Services.ToolResultEvent>(System.StringComparer.Ordinal);
            if (m.Blocks != null)
                foreach (var b in m.Blocks)
                    if (b != null && b.Kind == TurnBlockKind.ToolCard && b.ToolResult != null)
                    {
                        if (!string.IsNullOrEmpty(b.ToolResult.ToolCallId)) events[b.ToolResult.ToolCallId] = b.ToolResult;
                        if (!string.IsNullOrEmpty(b.ToolResult.Tool) && !events.ContainsKey(b.ToolResult.Tool)) events[b.ToolResult.Tool] = b.ToolResult;
                    }
            return id =>
                !string.IsNullOrEmpty(id) && events.TryGetValue(id, out var ev)
                    ? GetToolCard(m, ev) : null;
        }

        private AgentActivityView ActivityPanel(ChatMessage m, bool streaming, bool answerStarting)
        {
            if (_activityView == null)
            {
                _activityView = new AgentActivityView { Margin = new Thickness(0, 0, 0, 12) };
                _activityView.OnLayoutChanged = RepinIfSticky;
            }
            else if (_activityView.Parent is Panel oldParent)
                oldParent.Children.Remove(_activityView);
            _activityView.ResolveToolCard = ToolCardResolver(m);
            _activityView.Update(m.LiveReasoningSteps, m.LiveSteps, streaming, answerStarting,
                                 replyChars: m.Text?.Length ?? 0);
            return _activityView;
        }

        /// <summary>Fresh, collapsed activity card for a COMPLETED message —
        /// same lifecycle as the old ReasoningBlock (once per Rebuild).</summary>
        private AgentActivityView ActivityBlock(ChatMessage m)
        {
            var view = new AgentActivityView { Margin = new Thickness(0, 0, 0, 12) };
            view.OnLayoutChanged = RepinIfSticky;
            view.ResolveToolCard = ToolCardResolver(m);
            view.Update(m.ReasoningSteps, m.Steps, streaming: false, answerStarting: false, seedOpen: false);
            return view;
        }

        /// <summary>Fresh (non-cached) reasoning timeline for a COMPLETED message —
        /// AiReply/ConfirmActions render this once per Rebuild (same lifecycle as
        /// ClarifyCard/ConfirmActionsCard below), always starting collapsed. The
        /// "stay open if the drafter had it open while streaming" nuance lives
        /// entirely inside the LIVE instance above (auto-collapse-unless-toggled
        /// during the turn); once the message is persisted as a new ChatMessage
        /// object, re-opening it is one click away (README: "re-expandable at
        /// any time, including on completed historical turns").</summary>
        private FrameworkElement ReasoningBlock(ChatMessage m)
        {
            var view = new ReasoningTimelineView();
            view.OnLayoutChanged = RepinIfSticky;
            view.Update(m.ReasoningSteps, streaming: false, answerStarting: false, seedOpen: false);
            view.Margin = new Thickness(0, 0, 0, 12);
            return view;
        }

        // ─── Stream v2 segmented turn body (T1/T3) ───────────────────────────
        // Ordered Narrative | ToolCard | ConfirmCard blocks — the Hermes-parity
        // rendering. Smoothness pass (2026-08-20): narrative markdown and tool
        // cards are cached per thread and RE-PARENTED instead of rebuilt on
        // every SSE tick — a settled narrative leg is never re-parsed, and a
        // tool card keeps its expand state across re-renders (the old
        // rebuild-per-tick reset both). `suppressToolIds` skips cards the
        // agent-activity card already nested under their steps.
        private FrameworkElement BlocksPanel(ChatMessage m, double maxWidth,
            System.Collections.Generic.ISet<string> suppressToolIds = null)
        {
            var panel = new StackPanel();
            foreach (var b in m.Blocks)
            {
                if (b == null) continue;
                switch (b.Kind)
                {
                    case TurnBlockKind.Narrative:
                        if (!string.IsNullOrWhiteSpace(b.Text))
                        {
                            // Message-scoped: leg ids restart per stream, so an
                            // unscoped key would steal an older turn's element.
                            var key = "n:" + m.GetHashCode() + ":" + (b.SegmentId ?? "");
                            FrameworkElement md;
                            if (_threadNarratives.TryGetValue(key, out var cached)
                                && cached.len == b.Text.Length && cached.width == maxWidth)
                            {
                                md = cached.el;
                                if (md.Parent is Panel oldP) oldP.Children.Remove(md);
                            }
                            else
                            {
                                md = CopilotMessageBubble.MarkdownText(b.Text, maxWidth);
                                md.Margin = new Thickness(0, 0, 0, 8);
                                _threadNarratives[key] = (b.Text.Length, maxWidth, md);
                            }
                            panel.Children.Add(md);
                        }
                        break;
                    case TurnBlockKind.ToolCard:
                        if (b.ToolResult != null)
                        {
                            if (suppressToolIds != null
                                && (suppressToolIds.Contains(b.ToolResult.ToolCallId ?? "")
                                    || suppressToolIds.Contains(b.ToolResult.Tool ?? "")))
                                break;   // nested under its step in the activity card
                            var card = GetToolCard(m, b.ToolResult);
                            if (card.Parent is Panel oldP) oldP.Children.Remove(card);
                            panel.Children.Add(card);
                        }
                        break;
                    case TurnBlockKind.ConfirmCard:
                        panel.Children.Add(ConfirmRecordLine(b));
                        break;
                }
            }
            return panel;
        }

        // Compact in-thread decision record (T5): after Ya/Tidak the confirm
        // reads as one line inside the continuing thread — the interactive
        // card itself still renders via ConfirmActionsCard while pending.
        private FrameworkElement ConfirmRecordLine(TurnBlock b)
        {
            bool ok = b.Approved == true;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            var mark = new TextBlock
            {
                Text = ok ? "✓" : "✗", FontSize = 11, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Cp.Green" : "Cp.IssueFg");
            row.Children.Add(mark);
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(b.Text) ? (ok ? "Diluluskan" : "Ditolak") : b.Text,
                FontSize = 11, FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            row.Children.Add(label);
            return row;
        }

        /// <summary>SignIn / Attention notice card (design: copilot-signin-states,
        /// 2026-08-27). Lives in the thread where the answer would have gone, so
        /// the drafter sees exactly what their prompt is waiting on. Amber for
        /// Attention (nothing broke, one thing to do), plain for SignIn. Every
        /// colour is a pane token; the origin line is mono for support.</summary>
        private FrameworkElement NoticeCard(ChatMessage m)
        {
            bool attention = m.Kind == CpMsgKind.Attention;
            var amber = CopilotColors.From(CopilotTheme.IsDark ? "#f2a33a" : "#d97706");
            var amberBg = CopilotColors.From(CopilotTheme.IsDark ? "#1Ff2a33a" : "#1Ad97706");

            var outer = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14) };
            if (attention) { outer.BorderBrush = amber; outer.Background = amberBg; }
            else { outer.SetResourceReference(Border.BorderBrushProperty, "Cp.Line"); outer.SetResourceReference(Border.BackgroundProperty, "Cp.Bg"); }
            var sp = new StackPanel();

            var eyebrow = new TextBlock { Text = attention ? "\u26A0  COPILOT NEEDS ATTENTION" : "SIGN IN REQUIRED", FontSize = 10, FontWeight = FontWeights.SemiBold };
            eyebrow.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
            if (attention) eyebrow.Foreground = amber; else eyebrow.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            sp.Children.Add(eyebrow);

            var title = new TextBlock { Text = m.Title ?? "", FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            sp.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(m.Body))
            {
                var body = new TextBlock { Text = m.Body, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
                body.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                sp.Children.Add(body);
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            if (!string.IsNullOrEmpty(m.PrimaryLabel)) actions.Children.Add(NoticeButton(m.PrimaryLabel, m.PrimaryAction, primary: true));
            if (!string.IsNullOrEmpty(m.SecondaryLabel)) actions.Children.Add(NoticeButton(m.SecondaryLabel, m.SecondaryAction, primary: false));
            if (actions.Children.Count > 0) sp.Children.Add(actions);

            if (!string.IsNullOrWhiteSpace(m.Origin))
            {
                var origin = new TextBlock { Text = m.Origin, FontSize = 10.5, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
                origin.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
                origin.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                sp.Children.Add(origin);
            }
            outer.Child = sp;
            return outer;
        }

        private FrameworkElement NoticeButton(string label, Action onClick, bool primary)
        {
            var bd = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            var tb = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.Medium };
            if (primary)
            {
                bd.SetResourceReference(Border.BackgroundProperty, "Cp.AccentGrad");
                bd.BorderBrush = Brushes.Transparent;
                tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            }
            else
            {
                bd.Background = Brushes.Transparent;
                bd.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            }
            bd.Child = tb;
            bd.MouseLeftButtonUp += (_, __) => { try { onClick?.Invoke(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] notice action failed: " + ex.Message); } };
            return bd;
        }

        /// <summary>A card-builder exception must never blank the transcript
        /// (2026-08-29: a Style handed to Border.Background did exactly that).
        /// Fall back to the plain question text and log.</summary>
        private FrameworkElement ClarifyCardSafe(ChatMessage m)
        {
            try { return ClarifyCard(m); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] ClarifyCard render failed: " + ex);
                var fb = new TextBlock { Text = m.Question ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 6) };
                fb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                return fb;
            }
        }

        private FrameworkElement ClarifyCard(ChatMessage m)
        {
            // Theme resources, not hex: the old #ffffff card + #f5f3ff header
            // were light-only and glared on the dark pane.
            var outer = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            outer.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            outer.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            var sp = new StackPanel();

            var head = new Border { Padding = new Thickness(14, 11, 14, 11), BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(12, 12, 0, 0) };
            head.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            head.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
            // Chrome language follows the QUESTION, not a fixed locale: BM
            // chrome around an English question read as two products stitched
            // together (UAT 2026-08-29), and the reverse was the 2026-08-05
            // finding. One heuristic, applied to title / footer / submit.
            var clarifyText = (m.Question ?? "") + " " + string.Join(" ", (m.Questions ?? new System.Collections.Generic.List<ClarifyQuestionModel>()).Select(q => q.Question ?? ""));
            bool bm = ClarifyIsMalay(clarifyText);
            var hs = new Grid { VerticalAlignment = VerticalAlignment.Center };
            hs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // A drawn badge, not the sparkle Path: the star sat off its
            // baseline at every DPI (screenshot 2026-08-29 22:33). An Ellipse
            // + centred glyph in a Grid cannot drift.
            var badge = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            var badgeBg = new System.Windows.Shapes.Ellipse();
            badgeBg.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cp.AccentGrad");
            badge.Children.Add(badgeBg);
            var badgeGlyph = new TextBlock { Text = "?", FontSize = 13.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
            badgeGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            badge.Children.Add(badgeGlyph);
            Grid.SetColumn(badge, 0); hs.Children.Add(badge);
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var clarifyTitle = new TextBlock { Text = bm ? "Semak sebentar sebelum saya teruskan" : "Quick check before I continue", FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            clarifyTitle.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            titleStack.Children.Add(clarifyTitle);
            var clarifySub = new TextBlock { Text = bm ? "Pilih satu, atau taip jawapan anda" : "Pick one, or type your own answer", FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0) };
            clarifySub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            titleStack.Children.Add(clarifySub);
            Grid.SetColumn(titleStack, 1); hs.Children.Add(titleStack);
            head.Child = hs;
            sp.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            // MarkdownText, not a bare TextBlock: the model writes **bold** in
            // clarify questions exactly as it does in answers, and a plain
            // TextBlock rendered the asterisks literally (UAT 2026-07-27). Same
            // renderer the answer bubble uses, so the two read consistently.
            //
            // PILIHAN protocol (task 11, 2026-08-13): a clarify question MAY
            // end with a machine-readable `PILIHAN: opt | opt | opt` line
            // (recipe: model_house_massing.md "Bila bertanya drafter" #5).
            // Strip that line out of the rendered markdown and turn it into
            // tappable chips below instead, so the drafter taps rather than
            // re-typing the option verbatim. No PILIHAN line -> questionText
            // is untouched and no chip row renders.
            var questionText = m.Question ?? "";
            var pilihanOptions = new System.Collections.Generic.List<string>();
            var pilihanMatch = System.Text.RegularExpressions.Regex.Match(
                questionText, @"^PILIHAN:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (pilihanMatch.Success)
            {
                questionText = questionText.Remove(pilihanMatch.Index, pilihanMatch.Length).TrimEnd();
                pilihanOptions = pilihanMatch.Groups[1].Value
                    .Split('|')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Take(4)
                    .ToList();
            }
            var qText = CopilotMessageBubble.MarkdownText(questionText, 460);
            qText.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(qText);
            if (pilihanOptions.Count > 0)
            {
                // Same pill factory/style as the offer-actions ("Tindakan")
                // chips (FollowupChip, used by ResultSummaryCard below) and
                // the same send path as the input box and every other chip
                // in this file -- Vm.ChatSendCommand.Execute -- so a tapped
                // option is indistinguishable from the drafter typing it.
                var pilihanChips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
                foreach (var opt in pilihanOptions)
                {
                    var full = opt;
                    pilihanChips.Children.Add(FollowupChip(TruncateChipLabel(full), () => Vm?.ChatSendCommand.Execute(full)));
                }
                body.Children.Add(pilihanChips);
            }
            // ─── ask_user structured questions (2026-08-18) ────────────────
            // Claude-Code-style option rows: full-width stacked rows (narrow
            // pane — never a wrapping chip strip), label + consequence
            // description, ○/● radio for single-select, ☐/☑ + Hantar for
            // multi_select. Single question + single-select submits on tap.
            // Free text stays available: typing in the prompt bar answers the
            // question (router BuildAnswers treats it as the Lain-lain escape).
            if (m.Questions != null && m.Questions.Count > 0)
            {
                if (m.ActionsResolved)
                {
                    var done = new TextBlock
                    {
                        Text = "✓ " + (m.ChoiceSummary ?? (bm ? "dijawab" : "answered")),
                        FontSize = 12, FontWeight = FontWeights.Medium,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
                    };
                    done.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                    body.Children.Add(done);
                }
                else
                {
                    var selected = new System.Collections.Generic.Dictionary<ClarifyQuestionModel, System.Collections.Generic.HashSet<string>>();
                    var rowMarks = new System.Collections.Generic.Dictionary<ClarifyQuestionModel, System.Collections.Generic.List<System.Tuple<Border, Grid, string>>>();
                    foreach (var q0 in m.Questions)
                    {
                        selected[q0] = new System.Collections.Generic.HashSet<string>();
                        rowMarks[q0] = new System.Collections.Generic.List<System.Tuple<Border, Grid, string>>();
                    }
                    bool instant = m.Questions.Count == 1 && !m.Questions[0].MultiSelect;
                    Button hantarBtn = null;
                    System.Action refreshHantar = () =>
                    {
                        if (hantarBtn != null)
                            hantarBtn.IsEnabled = m.Questions.All(qq => selected[qq].Count > 0);
                    };
                    System.Action submit = () =>
                    {
                        if (m.Questions.Any(qq => selected[qq].Count == 0)) return;
                        var sel = m.Questions.ToDictionary(qq => qq.Question, qq => selected[qq].ToList());
                        Vm?.SubmitChoiceSelections(m, sel);
                    };
                    foreach (var q in m.Questions)
                    {
                        var qLocal = q;
                        var qRow = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
                        if (!string.IsNullOrWhiteSpace(q.Header))
                        {
                            var chip = new Border
                            {
                                CornerRadius = new CornerRadius(5),
                                Padding = new Thickness(7, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left,
                                Margin = new Thickness(0, 0, 0, 6),
                            };
                            chip.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
                            var chipText = new TextBlock { Text = q.Header.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.Bold };
                            chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                            chip.Child = chipText;
                            qRow.Children.Add(chip);
                        }
                        var qt = CopilotMessageBubble.MarkdownText(q.Question, 460);
                        qt.Margin = new Thickness(0, 0, 0, 6);
                        qRow.Children.Add(qt);
                        foreach (var opt in q.Options ?? new System.Collections.Generic.List<ClarifyOptionModel>())
                        {
                            var optLocal = opt;
                            var rowBorder = new Border
                            {
                                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                                Margin = new Thickness(0, 0, 0, 6), Cursor = System.Windows.Input.Cursors.Hand,
                                MinHeight = 44,
                            };
                            rowBorder.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                            rowBorder.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
                            var g2 = new Grid();
                            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            // Drawn control, not a unicode ○/☐: the glyph sat
                            // off-baseline and changed size per font fallback.
                            var mark = ClarifyMark(q.MultiSelect, false);
                            mark.Margin = new Thickness(12, 0, 10, 0); mark.VerticalAlignment = VerticalAlignment.Center;
                            Grid.SetColumn(mark, 0); g2.Children.Add(mark);
                            var oc2 = new StackPanel { Margin = new Thickness(0, 9, 12, 9), VerticalAlignment = VerticalAlignment.Center };
                            var optLabel = new TextBlock { Text = opt.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                            optLabel.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                            oc2.Children.Add(optLabel);
                            if (!string.IsNullOrWhiteSpace(opt.Description))
                            {
                                var optDesc = new TextBlock { Text = opt.Description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 15 };
                                optDesc.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                                oc2.Children.Add(optDesc);
                            }
                            Grid.SetColumn(oc2, 1); g2.Children.Add(oc2);
                            rowBorder.Child = g2;
                            rowBorder.MouseEnter += (_, __) => { if (!selected[qLocal].Contains(optLocal.Label)) rowBorder.SetResourceReference(Border.BackgroundProperty, "Cp.Hover"); };
                            rowBorder.MouseLeave += (_, __) => { if (!selected[qLocal].Contains(optLocal.Label)) rowBorder.SetResourceReference(Border.BackgroundProperty, "Cp.Bg"); };
                            rowMarks[qLocal].Add(System.Tuple.Create(rowBorder, mark, optLocal.Label));
                            rowBorder.MouseLeftButtonUp += (_, __) =>
                            {
                                if (m.ActionsResolved) return;
                                var set = selected[qLocal];
                                if (qLocal.MultiSelect)
                                {
                                    if (!set.Add(optLocal.Label)) set.Remove(optLocal.Label);
                                }
                                else
                                {
                                    set.Clear(); set.Add(optLocal.Label);
                                }
                                foreach (var t in rowMarks[qLocal])
                                {
                                    bool on = set.Contains(t.Item3);
                                    ClarifySetMark(t.Item2, qLocal.MultiSelect, on);
                                    t.Item1.SetResourceReference(Border.BorderBrushProperty, on ? "Cp.Blue" : "Cp.Line");
                                    t.Item1.SetResourceReference(Border.BackgroundProperty, on ? "Cp.BlueSoft" : "Cp.Bg");
                                    t.Item1.BorderThickness = new Thickness(on ? 1.5 : 1);
                                }
                                refreshHantar();
                                if (instant) submit();
                            };
                            qRow.Children.Add(rowBorder);
                        }
                        body.Children.Add(qRow);
                    }
                    if (!instant)
                    {
                        hantarBtn = new Button
                        {
                            Content = bm ? "Hantar" : "Submit", FontSize = 12, FontWeight = FontWeights.SemiBold,
                            Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 4, 0, 8),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Cursor = System.Windows.Input.Cursors.Hand, IsEnabled = false,
                        };
                        hantarBtn.SetResourceReference(FrameworkElement.StyleProperty, "Cp.RunButton");
                        hantarBtn.Click += (_, __) => submit();
                        body.Children.Add(hantarBtn);
                    }
                    var footer = new TextBlock
                    {
                        Text = bm ? "Atau taip jawapan anda sendiri di ruang mesej di bawah." : "Or type your own answer in the message box below.",
                        FontSize = 10.5, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap,
                    };
                    footer.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                    body.Children.Add(footer);
                }
            }
            foreach (var o in m.Options)
            {
                var tool = CopilotCatalog.Find(o.ToolId);
                var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 5), BorderBrush = CopilotColors.From("#140F1B2D"), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                btn.Template = OutlineCardTemplate();
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (tool != null)
                {
                    var tile = new IconTile { Glyph = tool.Icon, TileBg = tool.TileBg, TileFg = tool.TileFg, TileSize = 24, GlyphSize = 12, Corner = 6, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(tile, 0); g.Children.Add(tile);
                }
                var oc = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                oc.Children.Add(new TextBlock { Text = o.Label, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#131c2b"), TextWrapping = TextWrapping.Wrap });
                oc.Children.Add(new TextBlock { Text = o.Hint, FontSize = 10.5, Foreground = CopilotColors.From("#586273"), Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(oc, 1); g.Children.Add(oc);
                var chev = new Path { Width = 13, Height = 13, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#99a3b3"), StrokeThickness = 1.6, Data = CopilotIcons.Get("chevronRight"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chev, 2); g.Children.Add(chev);
                btn.Content = g;
                var prompt = o.Prompt;
                btn.Click += (_, __) => Vm.ChatSendCommand.Execute(prompt);
                body.Children.Add(btn);
            }
            sp.Children.Add(body);
            outer.Child = sp;
            return outer;
        }

        // ─── Mutate-confirmation (Ya/Tidak) card ─────────────────────────────
        // The tool loop parked on a pending MUTATE batch; this card lists the
        // proposed actions (friendly ToolLabels lines) and gates them behind
        // [✓ Ya, teruskan] / [Tidak]. Chrome cloned from ClarifyCard, amber
        // warning header instead of the clarify violet. Buttons render only
        // while unresolved AND on the newest card (no dead buttons on a
        // superseded turn); a resolved card keeps the action list as the
        // audit trail of what was proposed.
        // Ctrl+Enter -> Allow, Esc -> Reject on whichever ConfirmActions card is
        // currently live (last in thread, unresolved). No-op when none pending —
        // Esc in particular must not swallow an unrelated Escape elsewhere.
        private void OnApprovalKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (Vm == null) return;
            ChatMessage pending = null;
            for (int i = Vm.Thread.Count - 1; i >= 0; i--)
            {
                if (Vm.Thread[i].Kind == CpMsgKind.ConfirmActions) { pending = Vm.Thread[i]; break; }
            }
            if (pending == null || pending.ActionsResolved) return;
            bool isCodeApproval = !string.IsNullOrEmpty(pending.PendingCode);

            if (e.Key == System.Windows.Input.Key.Enter
                && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                if (isCodeApproval) Vm.AcceptCodeApproval(pending); else Vm.AcceptActions(pending);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (isCodeApproval) Vm.DeclineCodeApproval(pending); else Vm.DeclineActions(pending);
                e.Handled = true;
            }
        }

        private bool IsLastActionable(ChatMessage m)
        {
            if (Vm == null) return false;
            for (int i = Vm.Thread.Count - 1; i >= 0; i--)
            {
                if (Vm.Thread[i].Kind == CpMsgKind.ConfirmActions)
                    return ReferenceEquals(Vm.Thread[i], m);
            }
            return false;
        }

        // A label built by ToolLabels sometimes carries a trailing "(1,053)" /
        // "(3 systems)" count — split it into (mainLabel, metric) so the
        // numbered row can right-dock the metric in mono, matching the v2
        // prototype's "Baca parameter elemen … 1,053" layout. No parenthesized
        // suffix -> metric is "".
        private static (string label, string metric) SplitLabelMetric(string label)
        {
            if (string.IsNullOrEmpty(label)) return ("", "");
            int open = label.LastIndexOf('(');
            if (open <= 0 || !label.EndsWith(")")) return (label, "");
            var metric = label.Substring(open + 1, label.Length - open - 2).Trim();
            var main = label.Substring(0, open).TrimEnd();
            return metric.Length > 0 ? (main, metric) : (label, "");
        }

        /// <summary>The consolidated approval card ("Needs permission") — ONE card
        /// per turn listing every pending write-step as a numbered mono row, Allow
        /// (Ctrl+Enter) / Reject (Esc), disabled + resolved-state after a decision,
        /// "Undoable" note. Re-skin of the old "Sahkan tindakan" card per the
        /// 2026-08-02 spec — same ActionLabels data, same Accept/DeclineActions
        /// plumbing, no behaviour change to the approve/reject flow itself.</summary>
        private FrameworkElement ConfirmActionsCard(ChatMessage m)
        {
            // Action Mode addendum (2026-08-02): two things distinguish this
            // card's meaning from the original "Sahkan tindakan" re-skin —
            // isCodeApproval routes Allow/Reject to the codegen gate instead of
            // the MUTATE-batch resolve path, and autoApproved renders a compact
            // "already decided by Auto mode" state instead of a warning.
            bool isCodeApproval = !string.IsNullOrEmpty(m.PendingCode);
            bool autoApproved = m.AutoApproved;

            var outer = new Border
            {
                CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true,
            };
            outer.SetResourceReference(Border.BorderBrushProperty, "Cp.Reasoning.Border3");
            outer.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.Surface");
            var sp = new StackPanel();

            var head = new Border
            {
                Padding = new Thickness(13, 10, 13, 10), BorderThickness = new Thickness(0, 0, 0, 1),
            };
            head.SetResourceReference(Border.BorderBrushProperty, "Cp.Reasoning.BorderSubtle2");
            // Auto-approved: no permission was ever actually asked for, so the
            // amber "warning" header would misrepresent what happened — swap to
            // the same green success token the resolved-state text below uses.
            if (!autoApproved) head.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.WarnHeadGrad");
            var headRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var warnBadge = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0) };
            warnBadge.SetResourceReference(Border.BackgroundProperty, autoApproved ? "Cp.Tile.GreenBg" : "Cp.Reasoning.WarnBg");
            var warnMark = new TextBlock { Text = autoApproved ? "✓" : "!", FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            warnMark.SetResourceReference(TextBlock.ForegroundProperty, autoApproved ? "Cp.Tile.GreenFg" : "Cp.Reasoning.WarnFg");
            warnBadge.Child = warnMark;
            headRow.Children.Add(warnBadge);
            var title = new TextBlock { Text = autoApproved ? "Auto-approved" : "Needs permission", FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.Ink");
            headRow.Children.Add(title);
            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(headRow, 0);
            headGrid.Children.Add(headRow);
            var labels = m.ActionLabels ?? new System.Collections.Generic.List<string>();
            var writesTag = new TextBlock
            {
                Text = (labels.Count == 1 ? "1 WRITE" : $"{labels.Count} WRITES"),
                FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            };
            writesTag.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            writesTag.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
            Grid.SetColumn(writesTag, 2);
            headGrid.Children.Add(writesTag);
            head.Child = headGrid;
            sp.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };
            var rows = new StackPanel();
            for (int idx = 0; idx < labels.Count; idx++)
            {
                var (mainLabel, metric) = SplitLabelMetric(labels[idx]);
                var rowBorder = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 8, 9, 8) };
                rowBorder.MouseEnter += (_, __) => rowBorder.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.Hover2");
                rowBorder.MouseLeave += (_, __) => rowBorder.Background = Brushes.Transparent;
                rowBorder.Background = Brushes.Transparent;
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var index = new TextBlock { Text = (idx + 1).ToString("00"), FontSize = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                index.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint2");
                index.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                Grid.SetColumn(index, 0);
                rowGrid.Children.Add(index);

                var lbl = new TextBlock { Text = mainLabel, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextPrimary");
                Grid.SetColumn(lbl, 1);
                rowGrid.Children.Add(lbl);

                if (!string.IsNullOrEmpty(metric))
                {
                    var metricText = new TextBlock { Text = metric, FontSize = 10.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    metricText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextMuted");
                    metricText.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                    Grid.SetColumn(metricText, 2);
                    rowGrid.Children.Add(metricText);
                }

                rowBorder.Child = rowGrid;
                rows.Children.Add(rowBorder);
            }
            body.Children.Add(rows);

            bool isLive = !m.ActionsResolved && IsLastActionable(m);
            if (isLive)
            {
                var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var allow = new Button { Padding = new Thickness(14, 8, 14, 8), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
                allow.SetResourceReference(BackgroundProperty, "Cp.Reasoning.Ink");
                var allowBorder = new FrameworkElementFactory(typeof(Border));
                allowBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
                allowBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
                allowBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
                var allowCp = new FrameworkElementFactory(typeof(ContentPresenter));
                allowBorder.AppendChild(allowCp);
                allow.Template = new ControlTemplate(typeof(Button)) { VisualTree = allowBorder };
                var allowContent = new StackPanel { Orientation = Orientation.Horizontal };
                allowContent.Children.Add(new TextBlock { Text = "Allow", FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                var allowHint = new TextBlock { Text = "  Ctrl+Enter", FontSize = 10, Opacity = 0.55, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                allowHint.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                allowContent.Children.Add(allowHint);
                allow.Content = allowContent;
                allow.Click += (_, __) => { if (isCodeApproval) Vm?.AcceptCodeApproval(m); else Vm?.AcceptActions(m); };
                Grid.SetColumn(allow, 0);
                actions.Children.Add(allow);

                var reject = new Button
                {
                    Content = "Reject", FontSize = 12.5, Padding = new Thickness(14, 8, 14, 8),
                    BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                reject.SetResourceReference(Control.ForegroundProperty, "Cp.Reasoning.TextSecondary");
                reject.SetResourceReference(Button.BorderBrushProperty, "Cp.Reasoning.Border2");
                FlatButton.Apply(reject, 9, withBorder: true);
                reject.Click += (_, __) => { if (isCodeApproval) Vm?.DeclineCodeApproval(m); else Vm?.DeclineActions(m); };
                Grid.SetColumn(reject, 1);
                actions.Children.Add(reject);

                var undoable = new TextBlock { Text = "Undoable", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                undoable.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
                Grid.SetColumn(undoable, 3);
                actions.Children.Add(undoable);

                body.Children.Add(actions);
            }
            else if (m.ActionsResolved)
            {
                var resolvedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                string resolvedLabel;
                if (autoApproved)
                    // Codegen is always exactly one script — "N writes" would read
                    // oddly for a single generated C# run, so it gets its own word
                    // (operator override, 2026-08-02).
                    resolvedLabel = isCodeApproval
                        ? "Auto-approved · script"
                        : "Auto-approved · " + labels.Count + (labels.Count == 1 ? " write" : " writes");
                else
                    resolvedLabel = m.ActionsApproved == true ? "Allowed" : "Rejected";
                var resolvedText = new TextBlock
                {
                    Text = resolvedLabel,
                    FontSize = 11.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
                };
                resolvedText.SetResourceReference(TextBlock.ForegroundProperty,
                    (autoApproved || m.ActionsApproved == true) ? "Cp.Reasoning.Success" : "Cp.Reasoning.TextFaint");
                resolvedRow.Children.Add(resolvedText);
                body.Children.Add(resolvedRow);
            }

            sp.Children.Add(body);
            outer.Child = sp;
            return outer;
        }

        // color_hint -> Cp.System.* token, falling back to the "no value" swatch
        // for anything unmapped (never guesses a colour client-side).
        private static string SystemColorToken(string colorHint) => (colorHint ?? "").ToLowerInvariant() switch
        {
            "supply" => "Cp.System.Supply",
            "return" => "Cp.System.Return",
            "exhaust" => "Cp.System.Exhaust",
            // Generic (non-MEP-system) semantic hints — 2026-08-02 addendum for
            // route-tool results. Reuse EXISTING tokens whose hex already
            // matches rather than adding new literals: info/#2563eb is the
            // same blue as Cp.System.Supply, ok/#10b981 is the same green as
            // Cp.Reasoning.Success, warn/#eab308 is the same amber as
            // Cp.System.Exhaust.
            "info" => "Cp.System.Supply",
            "ok" => "Cp.Reasoning.Success",
            "warn" => "Cp.System.Exhaust",
            _ => "Cp.System.None",
        };

        /// <summary>Result card (2026-08-02 offer_actions spec) — proportion-bar
        /// rows from ChatMessage.ResultSummary (when present) plus follow-up
        /// chips from ChatMessage.Followups (independent of the bars — a turn
        /// can offer follow-ups with no bars), an "Undo" chip, and (old-backend
        /// compat only) the legacy tindakan one-tap offer as a single chip.
        /// Every chip DISPLAYS a (possibly truncated) label but SENDS its own
        /// full prompt verbatim through the SAME ChatSendCommand the ClarifyCard
        /// option buttons already use — offer_actions items carry {label,
        /// prompt} that may legitimately differ (short pill, rich standalone
        /// command); a plain-string item from an older backend decodes as
        /// Label == Prompt == that string, so nothing sent is ever a shorter/
        /// different sentence than what the model actually authored, and none
        /// send a placeholder like the old bare "Continue" (task 12/13 fix).
        /// The legacy tindakan-only chip follows the same truncate-display /
        /// send-full-string rule for the same reason (pre-Followups backends
        /// can send a full long AI sentence there). "Undo" stays client-side:
        /// it asks the agent in natural language rather than assuming a
        /// dedicated undo tool exists server-side.</summary>
        /// <summary>Turn-receipt card (spec 2026-08-18): counts assembled by
        /// TurnReceiptService from DocumentChanged transaction ground truth —
        /// the model never authors this. Buttons run addin-internal jobs on
        /// the Revit thread via the same McpJobPump the tools use.</summary>
        private FrameworkElement ReceiptCard(ReceiptModel r)
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                BorderBrush = CopilotColors.From("#1F16A34A"), Background = CopilotColors.From("#F0FDF4"),
                Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 9, 12, 9),
            };
            var sp = new StackPanel();

            var headline = new TextBlock
            {
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#166534"), TextWrapping = TextWrapping.Wrap,
            };
            var parts = new System.Collections.Generic.List<string>();
            if (r.Added > 0) parts.Add($"+{r.Added} ditambah");
            if (r.Modified > 0) parts.Add($"{r.Modified} diubah");
            if (r.Deleted > 0) parts.Add($"{r.Deleted} dipadam");
            headline.Text = "✓ " + string.Join(" · ", parts);
            sp.Children.Add(headline);

            if (r.ByCategory.Count > 0)
            {
                var cats = string.Join(" · ", r.ByCategory.Take(4).Select(kv => $"{kv.Value} {kv.Key}"));
                if (r.ByCategory.Count > 4) cats += " · …";
                sp.Children.Add(new TextBlock
                {
                    Text = cats, FontSize = 10.5, Foreground = CopilotColors.From("#4d7c5f"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                });
            }

            // Before/after thumbnails (confirm-gated captures only).
            if (!string.IsNullOrEmpty(r.BeforeImage) || !string.IsNullOrEmpty(r.AfterImage))
            {
                var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                foreach (var t in new[] { System.Tuple.Create("Sebelum", r.BeforeImage), System.Tuple.Create("Selepas", r.AfterImage) })
                {
                    if (string.IsNullOrEmpty(t.Item2) || !System.IO.File.Exists(t.Item2)) continue;
                    try
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(t.Item2);
                        bi.DecodePixelWidth = 200;
                        bi.EndInit();
                        var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                        cell.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                            BorderBrush = CopilotColors.From("#140F1B2D"),
                            Child = new Image { Source = bi, Width = 200, Stretch = Stretch.UniformToFill, MaxHeight = 130 },
                        });
                        cell.Children.Add(new TextBlock { Text = t.Item1, FontSize = 10, Foreground = CopilotColors.From("#586273"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
                        pair.Children.Add(cell);
                    }
                    catch { /* a broken thumbnail never breaks the receipt */ }
                }
                if (pair.Children.Count > 0) sp.Children.Add(pair);
            }

            var buttons = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            buttons.Children.Add(FollowupChip("Tunjuk semula", () => RunReceiptJob("__receipt_show", "Perubahan diserlah dan dizum dalam pandangan.", "Tiada rekod perubahan untuk ditunjuk — resit ini dari sesi sebelum Revit dimulakan semula. Jalankan semula permintaan untuk resit baharu.")));
            buttons.Children.Add(FollowupChip("Undo", () => RunReceiptJob("__receipt_undo", "Undo diposkan ke Revit.", "Undo tidak dapat diposkan.")));
            sp.Children.Add(buttons);

            outer.Child = sp;
            return outer;
        }

        /// <summary>Run a receipt job and ALWAYS surface the outcome in the
        /// pane. The fire-and-forget version reproduced the exact bug it was
        /// built to prevent: a failed [Tunjuk semula] (e.g. static receipt
        /// state reset by a Revit restart) rendered literally NOTHING (UAT
        /// 2026-08-18) — a silent no-op from the feature whose whole job is
        /// "never leave the drafter guessing".</summary>
        private void RunReceiptJob(string tool, string okText, string failText)
        {
            var job = new BinaVibe.Mcp.McpJob { Tool = tool };
            try { BinaVibe.Mcp.McpJobPump.Enqueue(job); }
            catch { ReceiptFeedback(failText); return; }
            System.Threading.Tasks.Task.Run(async () =>
            {
                string text;
                try
                {
                    var done = await System.Threading.Tasks.Task.WhenAny(
                        job.Done.Task, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(12)));
                    if (done != job.Done.Task) { job.Abandoned = true; text = "Revit sibuk — cuba sekali lagi."; }
                    else if (job.Error != null) text = failText + " (" + job.Error + ")";
                    else
                    {
                        var ok = job.Result != null
                            && (!job.Result.TryGetValue("ok", out var okVal) || !(okVal is bool b) || b);
                        text = ok ? okText : failText;
                    }
                }
                catch { text = failText; }
                try { Dispatcher.Invoke(() => ReceiptFeedback(text)); } catch { }
            });
        }

        private void ReceiptFeedback(string text)
        {
            try
            {
                Vm?.Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply, Text = text,
                    Time = DateTime.Now.ToString("h:mm tt"),
                });
            }
            catch { }
        }

        private FrameworkElement ResultSummaryCard(ChatMessage m, bool hasBars, bool hasFollowups, bool hasTindakan, double maxCardWidth)
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

            if (hasBars)
            {
                var rs = m.ResultSummary;
                var card = new Border { CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 10) };
                card.SetResourceReference(Border.BorderBrushProperty, "Cp.Reasoning.Border3");
                var cardSp = new StackPanel();

                var head = new Grid { Margin = new Thickness(0) };
                var headBorder = new Border { Padding = new Thickness(13, 9, 13, 9), BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
                headBorder.SetResourceReference(Border.BorderBrushProperty, "Cp.Reasoning.BorderSubtle2");
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var titleTb = new TextBlock
                {
                    Text = LetterSpace(FormatResultCardTitle(rs.Title)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                };
                titleTb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
                titleTb.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                Grid.SetColumn(titleTb, 0);
                head.Children.Add(titleTb);
                var totalTb = new TextBlock { Text = rs.Total.ToString("N0"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
                totalTb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextMuted");
                totalTb.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                Grid.SetColumn(totalTb, 2);
                head.Children.Add(totalTb);
                cardSp.Children.Add(headBorder);

                var rows = new StackPanel { Margin = new Thickness(13, 11, 13, 12) };
                // All rows' label columns share one auto-fit width (widest
                // label wins, e.g. "L3 — Connected") capped at ~45% of the
                // card so a runaway label still ellipses instead of crushing
                // the bar — SharedSizeGroup needs an IsSharedSizeScope
                // ancestor, which `rows` provides for every Grid below.
                Grid.SetIsSharedSizeScope(rows, true);
                double labelCap = System.Math.Max(70, (maxCardWidth - 26 /* card padding */) * 0.45);
                int total = rs.Total > 0 ? rs.Total : rs.Rows.Sum(r => r.Count);
                foreach (var row in rs.Rows)
                {
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ResultSummaryLabel", MaxWidth = labelCap });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var swatch = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
                    swatch.SetResourceReference(Border.BackgroundProperty, SystemColorToken(row.ColorHint));
                    Grid.SetColumn(swatch, 0);
                    g.Children.Add(swatch);

                    var lbl = new TextBlock
                    {
                        Text = row.Label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0),
                    };
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextPrimary");
                    Grid.SetColumn(lbl, 1);
                    g.Children.Add(lbl);

                    // Proportion bar: two overlaid Rectangles (not nested
                    // Borders) with RadiusX/Y=2.5 == half the 5px height, so
                    // both ends are always fully rounded regardless of DPI —
                    // a thin pill, never the fat/Ellipse-looking blob the
                    // previous Border+CornerRadius(99) combo rendered as.
                    var barHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                    var track = new Rectangle { Height = 5, RadiusX = 2.5, RadiusY = 2.5, VerticalAlignment = VerticalAlignment.Center };
                    track.SetResourceReference(Shape.FillProperty, "Cp.Reasoning.BarTrack");
                    barHost.Children.Add(track);

                    double pct = total > 0 ? System.Math.Max(0, System.Math.Min(1.0, row.Count / (double)total)) : 0;
                    var fill = new Rectangle
                    {
                        Height = 5, RadiusX = 2.5, RadiusY = 2.5,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                        Width = 0,
                    };
                    fill.SetResourceReference(Shape.FillProperty, SystemColorToken(row.ColorHint));
                    barHost.Children.Add(fill);
                    // Width is resolved against the host's ActualWidth once
                    // laid out — a percentage Rectangle needs a host; bind via
                    // Loaded so the host has a real ActualWidth to multiply.
                    barHost.Loaded += (_, __) => fill.Width = barHost.ActualWidth * pct;
                    barHost.SizeChanged += (_, __) => fill.Width = barHost.ActualWidth * pct;
                    Grid.SetColumn(barHost, 2);
                    g.Children.Add(barHost);

                    var countTb = new TextBlock { Text = row.Count.ToString("N0"), FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    countTb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextRowCount");
                    countTb.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                    Grid.SetColumn(countTb, 3);
                    g.Children.Add(countTb);

                    rows.Children.Add(g);
                }
                cardSp.Children.Add(rows);
                card.Child = cardSp;
                outer.Children.Add(card);
            }

            if (hasFollowups || hasBars || hasTindakan)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
                if (hasFollowups)
                    // offer_actions contract (2026-08-02): each item is
                    // {label, prompt} — Take(3) is a defensive cap (the
                    // server already caps at 3). The pill DISPLAYS the
                    // (truncated) label but SENDS the full prompt verbatim —
                    // label and prompt may differ (short pill, rich command).
                    // A plain-string item from an older backend deserializes
                    // as Label == Prompt == that string, so it behaves exactly
                    // like the old contract.
                    foreach (var action in m.Followups.Take(3))
                    {
                        var prompt = !string.IsNullOrWhiteSpace(action?.Prompt) ? action.Prompt : action?.Label;
                        if (string.IsNullOrWhiteSpace(prompt)) continue;
                        var label = TruncateChipLabel(!string.IsNullOrWhiteSpace(action.Label) ? action.Label : prompt);
                        chips.Children.Add(FollowupChip(label, () => Vm?.ChatSendCommand.Execute(prompt)));
                    }
                if (hasTindakan)
                    // Old-backend compat only (no Followups list on this turn).
                    // Display is truncated so a long AI-authored offer sentence
                    // never renders as one oversized chip; the SEND is always
                    // the full m.Tindakan string, verbatim — never a "Continue"
                    // placeholder (task 12 fix, 2026-08-02).
                    chips.Children.Add(FollowupChip(TruncateChipLabel(m.Tindakan), () => Vm?.AcceptTindakan(m)));
                if (hasBars)
                    // Client-side, always offered after a write result — no
                    // dedicated undo tool assumed server-side; phrased as a
                    // normal request so the agent's existing tools handle it.
                    chips.Children.Add(FollowupChip("Undo", () => Vm?.ChatSendCommand.Execute("Undo the last change")));
                outer.Children.Add(chips);
            }

            if (!CopilotTheme.ReducedMotion) MsgRise(outer);
            return outer;
        }

        // Result-card title cosmetics (task 16, 2026-08-02): the backend
        // composes compound group_by titles as "BY LEVEL,CONNECTIVITY" (comma,
        // no space) — uppercase the whole string, then insert a space after
        // every comma so it reads as "BY LEVEL, CONNECTIVITY". Display-only;
        // the wire Title string is untouched.
        private static string FormatResultCardTitle(string title)
        {
            var upper = (title ?? "").ToUpperInvariant();
            return System.Text.RegularExpressions.Regex.Replace(upper, @",\s*", ", ");
        }

        // WPF's TextBlock has no CSS-style letter-spacing API (verified: no
        // CharacterSpacing member on TextBlock/TextElement in this SDK) — the
        // handoff's .06em uppercase mono tracking (also unimplemented so far
        // for "ACTION MODE" / step labels elsewhere in this file) is
        // approximated here by threading a hair space (U+200A, ~0.06em wide
        // in most fonts) between characters. Existing inline spaces still
        // read as spaces, just slightly wider — the standard WPF workaround.
        private static string LetterSpace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return string.Join(" ", text.ToCharArray());
        }

        // Chip-sized display for any follow-up chip (task 12/13, 2026-08-02):
        // offer_actions labels are already server-truncated to <=32 chars, but
        // the legacy tindakan fallback and a stray oversized label from an
        // older backend can send a full sentence here — this must never blow
        // up into "ONE wide chip". Display-only: the caller always sends the
        // untruncated prompt/tindakan string, so nothing is lost on tap.
        private static string TruncateChipLabel(string text, int max = 48)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max).TrimEnd() + "…";
        }

        // Pill styling per the operator's follow-up-chip mockup (task 13,
        // 2026-08-02): fully rounded (radius >= half the pill's height so it
        // always renders as a true pill, not just rounded corners), white
        // background, 1px hairline border, 13px label, ~16x8 padding, hover =
        // light gray. Shared by every chip in this row (offer_actions,
        // tindakan fallback, and the client-side "Undo" chip) so they stay
        // visually identical.
        // ── ClarifyCard helpers (2026-08-29 redesign) ─────────────────────
        private static readonly string[] _bmMarkers = { "sila", "anda", "saya", "tidak", "yang", "untuk", "dengan", "adakah", "mahu", "boleh", "teruskan", "pilih", "atau", "dan", "ini", "itu", "ke", "dari", "di", "pada", "semua", "dalam" };
        private static readonly string[] _enMarkers = { "the", "to", "of", "and", "with", "from", "you", "your", "need", "proceed", "apply", "which", "should", "want", "one", "more", "all", "this", "that", "or", "in", "on" };

        /// <summary>Chrome language follows the question: count BM vs English
        /// function words; ties go to BM (JKR fleet default).</summary>
        internal static bool ClarifyIsMalay(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var words = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z]+").Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value).ToList();
            int bmScore = words.Count(w => _bmMarkers.Contains(w));
            int enScore = words.Count(w => _enMarkers.Contains(w));
            return bmScore >= enScore;
        }

        private static Grid ClarifyMark(bool multi, bool on)
        {
            var g = new Grid { Width = 18, Height = 18 };
            var shape = multi
                ? (System.Windows.Shapes.Shape)new System.Windows.Shapes.Rectangle { RadiusX = 4, RadiusY = 4 }
                : new System.Windows.Shapes.Ellipse();
            shape.StrokeThickness = 1.5;
            g.Children.Add(shape);
            var inner = multi
                ? (FrameworkElement)new Path { Data = Geometry.Parse("M4,9.5 L7.5,13 L14,5.5"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None }
                : new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(inner);
            ClarifySetMark(g, multi, on);
            return g;
        }

        private static void ClarifySetMark(Grid g, bool multi, bool on)
        {
            if (g.Children.Count < 2) return;
            var shape = (System.Windows.Shapes.Shape)g.Children[0];
            var inner = g.Children[1];
            shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Cp.Blue" : "Cp.Line");
            if (on) shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cp.Blue");
            else shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cp.Bg");
            inner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (inner is Path p) p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.AccentContrast");
            else if (inner is System.Windows.Shapes.Ellipse e) e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cp.AccentContrast");
        }

        private FrameworkElement FollowupChip(string text, System.Action onClick)
        {
            var b = new Border
            {
                // Radius 9 + tight padding per the artifact chip spec (operator
                // mockup 2026-08-02 22:07) — full-round 999 rendered oval/egg
                // shapes on WPF Borders taller than 2x the radius.
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "Cp.Reasoning.Border2");
            b.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            b.MouseEnter += (_, __) => b.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.Hover");
            b.MouseLeave += (_, __) => b.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            var tb = new TextBlock { Text = text, FontSize = 12.5 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextPrimary");
            b.Child = tb;
            if (onClick != null) b.MouseLeftButtonUp += (_, __) => onClick();
            return b;
        }

        // Answer action row (PRD A9): count tag + "Highlight in model" chip.
        // Same chip idiom as FollowupChip; the crosshair glyph is drawn inline
        // (CopilotIcons has no crosshair and one icon doesn't warrant a map row).
        private FrameworkElement HighlightRow(System.Collections.Generic.List<long> elementIds)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
            };

            var countTag = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            countTag.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
            var countText = new TextBlock { Text = elementIds.Count + " elements", FontSize = 11.5 };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            countTag.Child = countText;
            row.Children.Add(countTag);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 5, 11, 5), Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "Cp.Reasoning.Border2");
            chip.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            chip.MouseEnter += (_, __) => chip.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.Hover");
            chip.MouseLeave += (_, __) => chip.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var crosshair = new Path
            {
                Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M12,3 v4 M12,17 v4 M3,12 h4 M17,12 h4 M7,12 a5,5 0 1 0 10,0 a5,5 0 1 0 -10,0"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            crosshair.SetResourceReference(Shape.StrokeProperty, "Cp.BlueText");
            content.Children.Add(crosshair);
            var label = new TextBlock { Text = "Highlight in model", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextPrimary");
            content.Children.Add(label);
            chip.Child = content;

            var ids = elementIds.ToArray();
            chip.MouseLeftButtonUp += (_, __) => SelectElements(ids);
            row.Children.Add(chip);
            return row;
        }

        // Design command card (lines 186-218): a hairline-topped SECTION inside the
        // AI column — command name + "· Proposed/Dismissed" header, the plan rows,
        // then "✓ Apply to model" (gradient) + "Dismiss" (ghost). No boxed card,
        // no tile, no tier badge. View-code / Regenerate / Open editor stay as
        // ghost affordances (functional, styled to the design's Dismiss idiom).
        private FrameworkElement ProposalCard(ChatMessage m)
        {
            var tool = CopilotCatalog.Find(m.ToolId);
            var outer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 11, 0, 0),
            };
            var sp = new StackPanel();
            outer.Child = sp;

            // Header: name + status word.
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            head.Children.Add(new TextBlock
            {
                Text = tool?.Title ?? "Command", FontSize = 12.5, FontWeight = FontWeights.Bold,
                Foreground = CopilotColors.From("#131c2b"), VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = m.Dismissed ? "· Dismissed" : "· Proposed",
                FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0),
                Foreground = CopilotColors.From(m.Dismissed ? "#99a3b3" : "#1d4ed8"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(head);

            // Plan rows (the card's parameter section — index faint, step right).
            int i = 1;
            foreach (var step in m.PlanSteps)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var idx = new TextBlock { Text = (i++).ToString(), FontSize = 11.5, Foreground = CopilotColors.From("#99a3b3") };
                g.Children.Add(idx);
                var txt = new TextBlock
                {
                    Text = step, FontSize = 11.5, FontWeight = FontWeights.Medium,
                    Foreground = CopilotColors.From("#586273"), TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(txt, 1);
                g.Children.Add(txt);
                g.Margin = new Thickness(0, 2.5, 0, 2.5);
                sp.Children.Add(g);
            }

            // View code toggle (kept; ghost idiom).
            int lines = string.IsNullOrEmpty(m.Code) ? 0 : m.Code.Split('\n').Length;
            var toggle = new ToggleButton { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = CopilotColors.From("#99a3b3") };
            toggle.Template = LinkToggleTemplate();
            toggle.Content = $"View code ({lines} lines)";
            var codeBox = new TextBox { Text = m.Code ?? "", Style = (Style)TryFindResource("Cp.CodeBlock"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 180 };
            toggle.Checked += (_, __) => { codeBox.Visibility = Visibility.Visible; toggle.Content = "Hide code"; };
            toggle.Unchecked += (_, __) => { codeBox.Visibility = Visibility.Collapsed; toggle.Content = $"View code ({lines} lines)"; };
            sp.Children.Add(toggle);
            sp.Children.Add(codeBox);

            if (m.Dismissed) return outer;

            // Actions: ✓ Apply to model (gradient) · Dismiss · spacer · Regenerate · Open editor.
            var ag = new Grid { Margin = new Thickness(0, 13, 0, 0) };
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var apply = new Button
            {
                Padding = new Thickness(14, 8, 14, 8), BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            apply.SetResourceReference(BackgroundProperty, "Cp.AccentGrad");
            var aBorder = new FrameworkElementFactory(typeof(Border));
            aBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            aBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            aBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var aCp = new FrameworkElementFactory(typeof(ContentPresenter));
            aBorder.AppendChild(aCp);
            apply.Template = new ControlTemplate(typeof(Button)) { VisualTree = aBorder };
            var asp = new StackPanel { Orientation = Orientation.Horizontal };
            var applyCheck = new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M20,6 L9,17 L4,12"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            applyCheck.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.AccentContrast");
            asp.Children.Add(applyCheck);
            var applyLabel = new TextBlock { Text = "Apply to model", FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            applyLabel.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            asp.Children.Add(applyLabel);
            apply.Content = asp;
            apply.Click += (_, __) => Vm.ChatRunCommand.Execute(m);
            ag.Children.Add(apply);

            var dismiss = new Button
            {
                Content = "Dismiss", FontSize = 11.5, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.From("#99a3b3"), Padding = new Thickness(8, 8, 6, 8),
                Margin = new Thickness(3, 0, 0, 0),
            };
            Controls.FlatButton.Apply(dismiss, 6);
            dismiss.Click += (_, __) => { m.Dismissed = true; Rebuild(); };
            Grid.SetColumn(dismiss, 1);
            ag.Children.Add(dismiss);

            var regen = SmallGhost("Regenerate");
            regen.Click += (_, __) => Vm.ChatRegenerateCommand.Execute(m);
            Grid.SetColumn(regen, 3);
            ag.Children.Add(regen);
            var edit = SmallGhost("Open editor");
            edit.Click += (_, __) => Vm.ChatOpenEditorCommand.Execute(m);
            edit.Margin = new Thickness(5, 0, 0, 0);
            Grid.SetColumn(edit, 4);
            ag.Children.Add(edit);

            sp.Children.Add(ag);
            return outer;
        }

        private FrameworkElement RunningBar(ChatMessage m)
        {
            var tool = CopilotCatalog.Find(m.ToolId);
            var bar = new Border { CornerRadius = new CornerRadius(10), BorderBrush = CopilotColors.From("#140F1B2D"), BorderThickness = new Thickness(1), Background = CopilotColors.From("#eff6ff"), Padding = new Thickness(12, 10, 12, 10) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 14, Height = 14, Stroke = CopilotColors.From("#1d4ed8"), StrokeThickness = 2,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                StrokeDashArray = new DoubleCollection { 3, 2 }, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var spin = new RotateTransform();
            ring.RenderTransform = spin;
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(800)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
            sp.Children.Add(ring);
            sp.Children.Add(new TextBlock { Text = $"Running {tool?.Title?.ToLowerInvariant()}…", FontSize = 12.5, Foreground = CopilotColors.From("#1e40af"), VerticalAlignment = VerticalAlignment.Center });
            bar.Child = sp;
            return bar;
        }

        // Design applied card: hairline-topped section — name + "· Applied",
        // result body, "✓ Applied to the model" green line, then ghost chips.
        private FrameworkElement CompactResult(ChatMessage m)
        {
            var tool = CopilotCatalog.Find(m.ToolId);
            var r = m.Result;
            var outer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 11, 0, 0),
            };
            var sp = new StackPanel();
            outer.Child = sp;

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
            head.Children.Add(new TextBlock
            {
                Text = tool?.Title ?? "Command", FontSize = 12.5, FontWeight = FontWeights.Bold,
                Foreground = CopilotColors.From("#131c2b"), VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = "· Applied", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#10b981"), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(head);

            sp.Children.Add(CompactBody(r));

            var appliedLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 0) };
            appliedLine.Children.Add(new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M20,6 L9,17 L4,12"), Stroke = CopilotColors.From("#10b981"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            });
            appliedLine.Children.Add(new TextBlock
            {
                Text = "Applied to the model", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#10b981"), VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(appliedLine);

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            chips.Children.Add(ResultChip("bookmark", "Save", () => Vm.PinCommand.Execute(m.ToolId)));
            chips.Children.Add(ResultChip("copy", "Copy", null));
            chips.Children.Add(ResultChip("undo", "Undo", null));
            sp.Children.Add(chips);

            return outer;
        }

        private FrameworkElement CompactBody(ResultModel r)
        {
            if (r == null) return new TextBlock();
            if (r.Kind == CpResultKind.Count)
            {
                var sp = new StackPanel();
                var num = new TextBlock();
                num.Inlines.Add(new System.Windows.Documents.Run(r.Headline) { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#131c2b") });
                num.Inlines.Add(new System.Windows.Documents.Run(" " + r.Unit) { FontSize = 12.5, Foreground = CopilotColors.From("#586273") });
                sp.Children.Add(num);
                sp.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#586273"), Margin = new Thickness(0, 0, 0, 8) });
                int total = r.Bars.Sum(b => b.Value);
                foreach (var b in r.Bars)
                {
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var dot = new Ellipse { Width = 6, Height = 6, Fill = CopilotColors.From(b.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    Grid.SetColumn(dot, 0);
                    var lbl = new TextBlock { Text = b.Label, FontSize = 11.5, Foreground = CopilotColors.From("#586273"), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(lbl, 1);
                    var val = new TextBlock { Text = b.Value.ToString(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#131c2b"), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(val, 2);
                    g.Children.Add(dot); g.Children.Add(lbl); g.Children.Add(val);
                    sp.Children.Add(g);
                }
                return sp;
            }
            if (r.Kind == CpResultKind.Issues)
            {
                var sp = new StackPanel();
                var hd = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                hd.Children.Add(new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#dc2626"), StrokeThickness = 1.6, Data = CopilotIcons.Get("warning"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                hd.Children.Add(new TextBlock { Text = $"{r.Headline} {r.Unit}", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#7f1d1d"), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(hd);
                foreach (var it in r.Items.Take(3))
                {
                    var b = new Border { Background = CopilotColors.From("#fef2f2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 0, 3) };
                    var g = new StackPanel { Orientation = Orientation.Horizontal };
                    g.Children.Add(new TextBlock { Text = it.Id, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#7f1d1d"), Margin = new Thickness(0, 0, 8, 0) });
                    g.Children.Add(new TextBlock { Text = it.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#991b1b"), TextTrimming = TextTrimming.CharacterEllipsis });
                    b.Child = g;
                    sp.Children.Add(b);
                }
                return sp;
            }
            if (r.Kind == CpResultKind.File)
            {
                var g = new StackPanel { Orientation = Orientation.Horizontal };
                var ext = new Border { Width = 32, Height = 40, CornerRadius = new CornerRadius(5), Background = CopilotColors.From("#f3f6f9"), BorderBrush = CopilotColors.From("#bbf7d0"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
                ext.Child = new TextBlock { Text = "xlsx", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#10b981"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(ext);
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                col.Children.Add(new TextBlock { Text = r.Headline, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#131c2b") });
                col.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11, Foreground = CopilotColors.From("#586273") });
                g.Children.Add(col);
                return g;
            }
            // plain / list (compact)
            var plain = new StackPanel();
            plain.Children.Add(new TextBlock { Text = r.Headline, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#131c2b"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(r.Sub))
                plain.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#586273"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            return plain;
        }

        private Button SmallGhost(string text)
        {
            var b = new Button { Content = text, Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11, Foreground = CopilotColors.From("#586273"), Padding = new Thickness(9, 5, 9, 5) };
            b.Template = SmallGhostTemplate();
            return b;
        }

        private Button ResultChip(string glyph, string text, System.Action onClick)
        {
            var b = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 4, 8, 4) };
            b.Template = SmallGhostTemplate();
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Path { Width = 11, Height = 11, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#586273"), StrokeThickness = 1.6, Data = CopilotIcons.Get(glyph), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = CopilotColors.From("#586273") });
            b.Content = sp;
            if (onClick != null) b.Click += (_, __) => onClick();
            return b;
        }

        // Compact audit panel of the tools the agent ran this turn. Each
        // tool is a row with a green check + monospace name, inside a light
        // rounded container — readable but quiet, doesn't fight the answer.
        private FrameworkElement ToolTracePanel(System.Collections.Generic.IList<string> tools)
        {
            var outer = new Border
            {
                Background = CopilotColors.From("#f7f9fb"),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 8, 0, 0),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = tools.Count == 1 ? "1 STEP" : $"{tools.Count} STEPS",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#99a3b3"),
                Margin = new Thickness(0, 0, 0, 5),
            });
            foreach (var t in tools)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1.5, 0, 1.5) };
                var dot = new Border
                {
                    Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                    Background = CopilotColors.From("#dcfce7"),
                    Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                dot.Child = new TextBlock
                {
                    Text = "✓", FontSize = 8.5, FontWeight = FontWeights.Bold,
                    Foreground = CopilotColors.From("#10b981"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = Humanize(t),
                    FontSize = 11.5,
                    Foreground = CopilotColors.From("#3d4a5f"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                sp.Children.Add(row);
            }
            outer.Child = sp;
            return outer;
        }

        // Persisted phased trail (phases + per-tool rows) shown in the FINAL
        // bubble — the same rows the live thinking-bubble trail showed, so a
        // completed run keeps its rich trail instead of collapsing to "1 STEP".
        // Always expanded. State-aware glyph/colour (✓ done / ▶ running / ✗ error).
        private FrameworkElement ProgressTracePanel(
            System.Collections.Generic.IReadOnlyList<RevitWebAppSync.UI.Copilot.Model.ProgressStep> steps)
        {
            var outer = new Border
            {
                Background = CopilotColors.From("#f7f9fb"),
                BorderBrush = CopilotColors.From("#140F1B2D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 8, 0, 0),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = steps.Count == 1 ? "1 STEP" : $"{steps.Count} STEPS",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#99a3b3"),
                Margin = new Thickness(0, 0, 0, 5),
            });
            foreach (var s in steps)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1.5, 0, 1.5) };

                // Glyph swatch colours follow state: green check (done),
                // grey arrow (running/incomplete), red cross (error).
                string dotBg = s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Done ? "#dcfce7"
                             : s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Error ? "#fef2f2" : "#140F1B2D";
                string glyphFg = s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Done ? "#10b981"
                               : s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Error ? "#dc2626" : "#99a3b3";

                var dot = new Border
                {
                    Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                    Background = CopilotColors.From(dotBg),
                    Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                dot.Child = new TextBlock
                {
                    Text = RevitWebAppSync.UI.Copilot.Model.ProgressTrail.Glyph(s.State),
                    FontSize = 8.5, FontWeight = FontWeights.Bold,
                    Foreground = CopilotColors.From(glyphFg),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = RevitWebAppSync.UI.Copilot.Model.ProgressTrail.RowText(s),
                    FontSize = 11.5,
                    Foreground = CopilotColors.From("#3d4a5f"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                sp.Children.Add(row);
            }
            outer.Child = sp;
            return outer;
        }

        // Copy-to-clipboard affordances, the bot avatar, and base64 image decoding
        // live in CopilotMessageBubble (shared with the History detail view).

        // Raw tool name → friendly step label. Polished labels for the common
        // tools; everything else falls back to a clean snake_case → sentence
        // transform (e.g. "find_elements_by_filter" → "Find elements by filter").
        private static readonly System.Collections.Generic.Dictionary<string, string> _toolLabels =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                // --- Inspect (read-only) ---
                ["list_levels"] = "Reading levels",
                ["list_wall_types"] = "Reading wall types",
                ["list_family_types"] = "Reading family types",
                ["list_view_templates"] = "Reading view templates",
                ["list_worksets"] = "Reading worksets",
                ["get_element_parameters"] = "Reading element parameters",
                ["find_elements_by_filter"] = "Finding elements",
                ["find_elements_by_parameter"] = "Finding elements",
                ["get_current_selection"] = "Checking your selection",
                ["get_active_view"] = "Checking the active view",
                ["get_current_view_elements"] = "Reading the current view",
                ["get_project_info"] = "Reading project info",
                ["list_views"] = "Reading views",
                ["list_sheets"] = "Reading sheets",
                ["list_schedules"] = "Reading schedules",
                ["list_grids"] = "Reading grids",
                ["find_elements_between_grids"] = "Finding elements between grids",
                ["find_mep_elements"] = "Finding MEP elements",
                ["analyze_model_statistics"] = "Analyzing the model",
                ["get_material_quantities"] = "Reading material quantities",
                ["get_model_warnings"] = "Checking model warnings",
                ["list_view_filters"] = "Reading view filters",

                // --- Mutate (write) ---
                ["set_parameter"] = "Setting a parameter",
                ["set_parameter_bulk"] = "Setting parameters",
                ["change_type"] = "Changing the type",
                ["delete_elements"] = "Deleting elements",
                ["duplicate_view"] = "Duplicating the view",
                ["apply_view_template"] = "Applying the view template",
                ["place_door"] = "Placing a door",
                ["place_window"] = "Placing a window",
                ["create_wall"] = "Creating a wall",
                ["create_level"] = "Creating a level",
                ["create_grid"] = "Creating a grid",
                ["create_room"] = "Creating a room",
                ["color_elements"] = "Coloring elements",
                ["hide_isolate_elements"] = "Hiding/isolating elements",
                ["place_family_instance"] = "Placing a family",
                ["load_family"] = "Loading a family from the library",
                ["move_elements"] = "Moving elements",
                ["create_sheet"] = "Creating a sheet",
                ["place_view_on_sheet"] = "Placing the view on a sheet",
                ["tag_elements"] = "Tagging elements",
                ["swap_element_type"] = "Swapping the type",
                ["place_text_note"] = "Adding a text note",
                ["rotate_elements"] = "Rotating elements",
                ["copy_elements"] = "Copying elements",
                ["mirror_elements"] = "Mirroring elements",
                ["export_views"] = "Exporting views",
                ["group_elements"] = "Grouping elements",
                ["pin_elements"] = "Pinning elements",
                ["join_geometry"] = "Joining geometry",
                ["renumber_elements"] = "Renumbering elements",
                ["create_view_filter"] = "Creating a view filter",
                ["apply_view_filter"] = "Applying the view filter",
                ["create_floor"] = "Creating a floor",
                ["create_ceiling"] = "Creating a ceiling",
                ["execute_revit_batch"] = "Running the batch",

                ["think"] = "Thinking it through",
            };

        private static string Humanize(string tool)
        {
            if (string.IsNullOrWhiteSpace(tool)) return "Step";
            if (_toolLabels.TryGetValue(tool, out var label)) return label;
            var s = tool.Replace('_', ' ').Trim();
            if (s.Length == 0) return tool;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // Welcome-card icon geometry (24-box, stroke-drawn — table / shield-check /
        // ruler / tag, from the design's Phosphor set redrawn as paths).
        private static string WelcomeIcon(string key)
        {
            switch (key)
            {
                case "shield": return "M12,3 l7,2.5 v5 c0,4.5 -3,8 -7,10.5 c-4,-2.5 -7,-6 -7,-10.5 v-5 Z M9,11.5 l2,2 4,-4";
                case "ruler": return "M20.5,14.7 a1.8,1.8 0 0 1 0,2.6 l-3.2,3.2 a1.8,1.8 0 0 1 -2.6,0 L3.5,9.3 a1.8,1.8 0 0 1 0,-2.6 l3.2,-3.2 a1.8,1.8 0 0 1 2.6,0 Z M13.8,11.8 l1.6,-1.6 M11,9 l1.6,-1.6 M16.6,14.6 l1.6,-1.6";
                case "tagIcon": return "M9,5 H5 a2,2 0 0 0 -2,2 v4 l9,9 a1.5,1.5 0 0 0 2.1,0 l4,-4 a1.5,1.5 0 0 0 0,-2.1 L9,5 z M7.5,8.5 h0.01";
                default: return "M4,4.5 h16 a1,1 0 0 1 1,1 v13 a1,1 0 0 1 -1,1 h-16 a1,1 0 0 1 -1,-1 v-13 a1,1 0 0 1 1,-1 z M3,9.5 h18 M3,14.5 h18 M9.5,9.5 v9";
            }
        }

        // ─── Empty state (design: gradient wash · centred time-of-day greeting ·
        //     "Suggested for this model" list card) ─────────────────────────────
        private FrameworkElement EmptyState()
        {
            // Fill the viewport so the centred column truly centres (the design's
            // min-height:100% + margin:auto): the root grid tracks the scroller's
            // viewport height.
            var root = new Grid();
            root.SetBinding(FrameworkElement.MinHeightProperty,
                new System.Windows.Data.Binding("ViewportHeight") { Source = Scroller });

            // Top wash: 190px accent-6% → transparent, full width, non-interactive.
            var wash = new Border
            {
                Height = 190, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0F2A69C6"),
                    Colors.Transparent, 90),
            };
            root.Children.Add(wash);

            var col = new StackPanel
            {
                MaxWidth = 430,
                Margin = new Thickness(18, 8, 18, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Logo tile: 34×34 surface square, hairline + soft shadow, holding the
            // 15×15 rotated brand diamond.
            var tile = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(11),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, Opacity = 0.07, BlurRadius = 6, ShadowDepth = 1, Direction = 270 },
            };
            tile.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            tile.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            tile.BorderThickness = new Thickness(1);
            tile.Child = new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(4.5),
                Background = CopilotTheme.LogoGrad(),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(45),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            col.Children.Add(tile);

            // Greeting: time of day + first name (design greeting), centred.
            var name = Vm?.UserFirstName;
            var hour = System.DateTime.Now.Hour;
            var part = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
            bool hasName = !string.IsNullOrWhiteSpace(name) && name != "there";
            var greeting = new TextBlock
            {
                Text = hasName ? part + ", " + name + "." : part + ".",
                FontSize = 21, FontWeight = FontWeights.Medium,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };
            greeting.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            col.Children.Add(greeting);

            var sub = new TextBlock
            {
                FontSize = 13.5, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            var doc = Vm?.DocumentTitle;
            if (string.IsNullOrWhiteSpace(doc)) doc = "Main Model";
            sub.Inlines.Add(new System.Windows.Documents.Run("Copilot is connected to "));
            var docRun = new System.Windows.Documents.Run(doc) { FontWeight = FontWeights.Medium };
            docRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Cp.Text");
            sub.Inlines.Add(docRun);
            sub.Inlines.Add(new System.Windows.Documents.Run(" and ready to work."));
            col.Children.Add(sub);

            // "Suggested for this model" card: one surface card, kicker header +
            // hairline-separated task rows. Click INSERTS the prompt (house
            // behaviour — the drafter reviews before sending; the design auto-runs).
            var cardBody = new StackPanel();

            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(14, 10, 14, 8),
            };
            // Same "✦" sparkle glyph the agent-activity header uses.
            var sparkle = new TextBlock
            {
                Text = "✦", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            };
            sparkle.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
            head.Children.Add(sparkle);
            var kicker = new TextBlock
            {
                Text = "SUGGESTED FOR THIS MODEL", FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            kicker.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            head.Children.Add(kicker);
            cardBody.Children.Add(head);

            (string icon, string title, string desc, string prompt)[] welcome =
            {
                ("table",  "Door schedule", "Generate a door schedule grouped by type and level.",
                 "Create a door schedule grouped by type and level"),
                ("shield", "Clash check", "Structural vs MEP hard clashes with an element list.",
                 "Check structural vs MEP clashes"),
                ("ruler",  "Room areas", "Floor area per room with totals per level.",
                 "Calculate floor area per room"),
                ("tagIcon", "Auto-tag", "Tag untagged doors, rooms and windows in the active view.",
                 "Tag all untagged elements in the active view"),
            };
            foreach (var (icon, title, desc, prompt) in welcome)
            {
                var g = new Grid { Margin = new Thickness(14, 9, 14, 9) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 30×30 icon tile on a 9% accent wash.
                var iconTile = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
                    Background = CopilotColors.From("#172A69C6"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var iconPath = new Path
                {
                    Width = 15, Height = 15, Stretch = Stretch.Uniform,
                    StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse(WelcomeIcon(icon)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                iconPath.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
                iconTile.Child = iconPath;
                Grid.SetColumn(iconTile, 0);
                g.Children.Add(iconTile);

                var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var t1 = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.Medium };
                t1.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                texts.Children.Add(t1);
                var t2 = new TextBlock
                {
                    Text = desc, FontSize = 11, Margin = new Thickness(0, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                t2.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                texts.Children.Add(t2);
                Grid.SetColumn(texts, 1);
                g.Children.Add(texts);

                // "↵" mono chip.
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
                };
                chip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var chipText = new TextBlock { Text = "↵", FontSize = 10 };
                chipText.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                chip.Child = chipText;
                Grid.SetColumn(chip, 2);
                g.Children.Add(chip);

                var row = new Border
                {
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    BorderBrush = CopilotColors.From("#0F22242A"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Child = g,
                };
                row.MouseEnter += (_, __) => row.Background = CopilotColors.From("#0D2A69C6");
                row.MouseLeave += (_, __) => row.Background = Brushes.Transparent;
                var p = prompt;
                row.MouseLeftButtonUp += (_, __) => Prompt.InsertStarterPrompt(p);
                cardBody.Children.Add(row);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 16, 0, 0),
                Child = cardBody,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, Opacity = 0.08, BlurRadius = 18, ShadowDepth = 4, Direction = 270 },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            col.Children.Add(card);

            root.Children.Add(col);

            // // Suggested prompts
            // root.Children.Add(Label("TRY ONE OF THESE"));
            // foreach (var p in Prompts)
            //     root.Children.Add(PromptCard(p));

            // // Topic chips
            // root.Children.Add(Label("NOT SURE? TYPE A TOPIC — I'LL ASK"));
            // var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
            // foreach (var t in Topics)
            // {
            //     var chip = new Button { Content = t, Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11.5, Foreground = CopilotColors.From("#374151"), Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(10, 4, 10, 4) };
            //     chip.Template = PillTemplate();
            //     var topic = t;
            //     chip.Click += (_, __) => Vm.ChatSendCommand.Execute(topic);
            //     chips.Children.Add(chip);
            // }
            // root.Children.Add(chips);

            // // Library CTA
            // root.Children.Add(LibraryCta());

            // // How runs work
            // root.Children.Add(HowRuns());
            return root;
        }

        private FrameworkElement Label(string text) =>
            new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#9ca3af"), Margin = new Thickness(2, 4, 0, 8) };

        private FrameworkElement PromptCard((string glyph, string fg, string bg, string text) p)
        {
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 6), BorderBrush = CopilotColors.From("#e5e7eb"), Background = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = OutlineCardTemplate();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = new IconTile { Glyph = p.glyph, TileBg = p.bg, TileFg = p.fg, TileSize = 24, GlyphSize = 12, Corner = 6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tile, 0);
            var tb = new TextBlock { Text = p.text, FontSize = 12.5, Foreground = CopilotColors.From("#374151"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(tb, 1);
            var send = new Path { Width = 11, Height = 11, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("send"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(send, 2);
            g.Children.Add(tile); g.Children.Add(tb); g.Children.Add(send);
            btn.Content = g;
            var text = p.text;
            btn.Click += (_, __) => Vm.ChatSendCommand.Execute(text);
            return btn;
        }

        private FrameworkElement LibraryCta()
        {
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 18), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = DashedCardTemplate();
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#f9fafb"), 0));
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#f3f4f6"), 1));
            btn.Background = bg;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = Brushes.White, BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            tile.Child = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#374151"), StrokeThickness = 1.8, Data = CopilotIcons.Get("layers"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tile, 0);
            var col = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock { Text = "Browse the Library →", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12") });
            col.Children.Add(new TextBlock { Text = $"{CopilotCatalog.All.Count()} ready-made tools across {CopilotCatalog.Categories.Count - 1} categories", FontSize = 11, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(col, 1);
            g.Children.Add(tile); g.Children.Add(col);
            btn.Content = g;
            btn.Click += (_, __) => Vm.GoTab(CpTab.Library);
            return btn;
        }

        private FrameworkElement HowRuns()
        {
            var border = new Border { Background = CopilotColors.From("#fafafa"), BorderBrush = CopilotColors.From("#f1f3f5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10) };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            head.Children.Add(new Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#374151"), StrokeThickness = 1.6, Data = CopilotIcons.Get("warning"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            head.Children.Add(new TextBlock { Text = "How runs work", FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#374151") });
            sp.Children.Add(head);
            var body = new TextBlock { FontSize = 11.5, Foreground = CopilotColors.From("#6b7280"), TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            body.Inlines.Add(new System.Windows.Documents.Run("Vetted") { FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#15803d") });
            body.Inlines.Add(new System.Windows.Documents.Run(" tools (5) run one-click. "));
            body.Inlines.Add(new System.Windows.Documents.Run("AI") { FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#7c3aed") });
            body.Inlines.Add(new System.Windows.Documents.Run(" commands show you the plan first — you Review & Run."));
            sp.Children.Add(body);
            border.Child = sp;
            return border;
        }

        // ─── Templates ───────────────────────────────────────────────────────
        private static ControlTemplate _outline, _pill, _dashed, _sugRow;

        // Borderless full-width row with a subtle hover wash (empty-state suggestions).
        private static ControlTemplate SuggestionRowTemplate()
        {
            if (_sugRow != null) return _sugRow;
            var b = new FrameworkElementFactory(typeof(Border));
            b.Name = "bd";
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            b.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = b };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            // Live DynamicResource, NOT CopilotColors.From: this template is cached
            // statically, so a captured brush freezes the hover to whatever theme was
            // active at first render (→ black hover in light after a dark toggle).
            // Cp.Hover swaps per theme (light #f3f6f9 / dark white-5%).
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new System.Windows.DynamicResourceExtension("Cp.Hover"), "bd"));
            t.Triggers.Add(hover);
            _sugRow = t;
            return t;
        }

        private static ControlTemplate OutlineCardTemplate()
        {
            if (_outline != null) return _outline;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 9));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            _outline = new ControlTemplate(typeof(Button)) { VisualTree = b };
            return _outline;
        }

        private static ControlTemplate PillTemplate()
        {
            if (_pill != null) return _pill;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            // DynamicResource (not a baked From() brush): this template is static-
            // cached and built once, so a concrete brush would freeze the first
            // theme. Cp.Line re-resolves on every light/dark swap.
            b.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetValue(Border.PaddingProperty, new Thickness(10, 4, 10, 4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            _pill = new ControlTemplate(typeof(Button)) { VisualTree = b };
            return _pill;
        }

        private static ControlTemplate DashedCardTemplate()
        {
            if (_dashed != null) return _dashed;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            // Live Cp.Line, not a baked From() brush — see PillTemplate.
            b.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetValue(Border.PaddingProperty, new Thickness(14, 12, 14, 12));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            _dashed = new ControlTemplate(typeof(Button)) { VisualTree = b };
            return _dashed;
        }

        private static ControlTemplate _linkToggle, _smallGhost;

        // Text-only link style for the "View code (N lines)" toggle.
        private static ControlTemplate LinkToggleTemplate()
        {
            if (_linkToggle != null) return _linkToggle;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(cp);
            _linkToggle = new ControlTemplate(typeof(ToggleButton)) { VisualTree = b };
            return _linkToggle;
        }

        // Bordered rounded button for proposal/result small actions.
        private static ControlTemplate SmallGhostTemplate()
        {
            if (_smallGhost != null) return _smallGhost;
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            // Live Cp.Line, not a baked From() brush — see PillTemplate.
            b.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetValue(Border.PaddingProperty, new Thickness(9, 5, 9, 5));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(cp);
            _smallGhost = new ControlTemplate(typeof(Button)) { VisualTree = b };
            return _smallGhost;
        }
    }
}
