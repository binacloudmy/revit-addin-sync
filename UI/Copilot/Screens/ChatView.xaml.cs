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
    /// Chat tab. Empty state = Home (saved commands, example prompts; ChatView.Home.cs)
    /// or the conversation thread. Proposal/clarify/result message rendering is added in
    /// Tasks 12-13; for now user messages render as bubbles.
    /// </summary>
    public partial class ChatView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public ChatView()
        {
            InitializeComponent();
            // Element-id clicks (MarkdownRenderer, table cells + bina://select/<id>
            // links) → local select+zoom. Static event, guarded subscribe — see
            // WireElementIdClick.
            WireElementIdClick();
            // Slash command sent from the composer → add the turn (chip bubble +
            // placeholder reply). UI-only until tools run from chat.
            Prompt.SlashToolSubmitted += (tool, args, inputs) => Vm?.ChatSendSlashCommand(tool, args, inputs);
            // Host the "/" palette as an IN-PANEL overlay (SlashLayer) so it stays
            // inside the pane and tracks resize — the editor shows/hides the layer.
            Prompt.AttachSlashPalette(SlashPalette, v =>
            {
                if (v) UpdateSlashPaletteBounds();
                SlashLayer.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            });
            SlashScrim.MouseLeftButtonDown += (_, __) => Prompt.CloseSlashPalette();
            // Saved Commands J1: Mine-row ⋯ actions → VM handlers.
            SlashPalette.EditRequested += t => { Prompt.CloseSlashPalette(); Vm?.OnEditCommand(t); };
            SlashPalette.DeleteRequested += t =>
            {
                Prompt.CloseSlashPalette();
                if (Vm == null) return;
                var ok = System.Windows.MessageBox.Show(
                    $"Delete /{t.Id}? This cannot be undone.", "Delete command",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (ok) _ = Vm.OnDeleteCommandAsync(t);
            };
            SizeChanged += (_, __) => { if (SlashLayer.Visibility == Visibility.Visible) UpdateSlashPaletteBounds(); };
            DataContextChanged += (_, __) => Hook();
            // Re-render the (code-behind-drawn) thread when the palette flips —
            // its bubbles snapshot colours via CopilotColors, so unlike the XAML
            // chrome they don't auto-repaint. Subscribe while visible only, so a
            // backgrounded ChatView doesn't leak onto the static event.
            Loaded += (_, __) => { CopilotTheme.ThemeChanged += OnThemeChanged; HookScope(); Rebuild(); };
            Unloaded += (_, __) => { CopilotTheme.ThemeChanged -= OnThemeChanged; UnhookScope(); };
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
        /// Revit doc through the McpJob/McpJobPump path, fired locally with no
        /// cloud round-trip (select_elements is a single fire-and-observe call). `async void` is
        /// deliberate here — this IS the event handler, and a click must never
        /// throw into WPF, so every failure path is swallowed into a status log.</summary>
        private static void OnElementIdClicked(long elementId) => SelectElements(new[] { elementId });

        /// <summary>Also the engine behind the answer's "Highlight in model"
        /// action row (PRD A9), which fires the WHOLE id set in one call.</summary>
        private static async void SelectElements(long[] elementIds)
        {
            if (elementIds == null || elementIds.Length == 0) return;
            var op = Model.ClickTrace.Begin("select-elements", elementIds.Length);
            try
            {
                var run = BinaVibe.Mcp.Tools.PaneTools.RunAsync(app => { op.Started(); return BinaVibe.Mcp.Tools.PaneTools.SelectElements(app, elementIds); });
                op.HandedOff();
                await run;
                Model.ClickTrace.End(op);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinaVibe][chat] select_elements(×{elementIds.Length}) failed: {ex.Message}");
                Model.ClickTrace.End(op, "failed: " + ex.Message);
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

        // Cached live elements snapshot colours at build time: drop them on a theme flip.
        private void OnThemeChanged() { ResetLiveCaches(); Rebuild(); }

        private void ResetLiveCaches()
        {
            _liveMd = null;
            _liveMdText = null;
            _liveLookEls.Clear();
        }

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
                _hooked.UndoStateChanged -= OnUndoStateChanged;
            }
            _hooked = Vm;
            if (_hooked != null)
            {
                _hooked.Thread.CollectionChanged += OnThread;
                _hooked.UsageChanged += UpdateUsage;
                _hooked.PropertyChanged += OnVmProp;
                _hooked.UpgradeRequested += OnVmUpgrade;
                _hooked.SaveCommandRequested += OnSaveCommandRequested;
                _hooked.UndoStateChanged += OnUndoStateChanged;
                Prompt.BindUsage(_hooked);
                _ = _hooked.RefreshUsageAsync();
                // Saved Commands J1: merge the Mine tier into the palette
                // (ETag-cached; falls back to the persisted rows offline).
                _ = _hooked.RefreshCommandCatalogAsync();
            }
            Rebuild();
            UpdateUsage();
        }

        // Revit's own Undo/Redo, or our button: redraw so the result cards offer only what is still possible.
        // One redraw per burst (an Undo of several batches raises several changes).
        private bool _undoRedrawQueued;
        private void OnUndoStateChanged()
        {
            if (_undoRedrawQueued) return;
            _undoRedrawQueued = true;
            Dispatcher.BeginInvoke(new System.Action(() => { _undoRedrawQueued = false; Rebuild(); }));
        }

        private void OnVmProp(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.IsSending))
            {
                UpdateUsage();
                UpdateElapsedClock();
                UpdateSubHeader(Vm == null || Vm.Thread.Count == 0);
            }
        }

        /// <summary>The slim status strip ("Ready", "Done", elapsed) shows with a thread, but not while a turn runs:
        /// the running card says it all (design Running.dc.html has no strip).</summary>
        private void UpdateSubHeader(bool emptyThread)
        {
            if (SubHeader == null) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new System.Action(() => UpdateSubHeader(emptyThread))); return; }
            bool running = Vm != null && Vm.IsSending;
            SubHeader.Visibility = emptyThread || running ? Visibility.Collapsed : Visibility.Visible;
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
            if (e.Action == NotifyCollectionChangedAction.Replace && TryRenderLiveTail(e)) return;
            Rebuild();
        }

        /// <summary>A stream tick (the VM swaps the live Thinking message for a
        /// newer Thinking snapshot, every ~80 reply characters and on every step)
        /// re-renders only that last row. Everything above it is untouched, so
        /// earlier replies, their tables and their cards never redraw mid-turn.
        /// Any other change (a new message, the final reply replacing the live
        /// one, a card resolving) still rebuilds the whole thread.</summary>
        private bool TryRenderLiveTail(NotifyCollectionChangedEventArgs e)
        {
            var vm = Vm;
            if (vm == null || _threadPanel == null || !BodyHost.Children.Contains(_threadPanel)) return false;
            if (e.NewItems == null || e.NewItems.Count != 1 || e.OldItems == null || e.OldItems.Count != 1) return false;
            int i = e.NewStartingIndex;
            if (i != vm.Thread.Count - 1 || _threadPanel.Children.Count != vm.Thread.Count) return false;
            if (!(e.OldItems[0] is ChatMessage oldM) || !(e.NewItems[0] is ChatMessage newM)) return false;
            if (oldM.Kind != CpMsgKind.Thinking || newM.Kind != CpMsgKind.Thinking) return false;
            try
            {
                // Not through the panel's indexer: it only fills an EMPTY slot and throws "Specified index is
                // already in use" for a drawn row, so every live tick failed and the pane kept its first "Thinking…"
                // until the turn ended (live test of 0.0.99). Take the old row out, put the new one in.
                var row = Message(newM);
                _threadPanel.Children.RemoveAt(i);
                _threadPanel.Children.Insert(i, row);
                return true;
            }
            catch (System.Exception ex)
            {
                // Whatever went wrong, the whole thread draws again rather than leaving a stale row.
                System.Diagnostics.Debug.WriteLine("[BINA] live row: " + ex.Message);
                return false;
            }
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

        // The thread's StackPanel from the last full Rebuild (null on the empty
        // state): TryRenderLiveTail swaps its last child in place.
        private StackPanel _threadPanel;

        private void Rebuild()
        {
            if (Vm == null || BodyHost == null) return;
            bool empty = Vm.Thread.Count == 0;
            UpdateSubHeader(empty);
            BodyHost.Children.Clear();
            _threadPanel = null;

            // A message was appended (vs. resize/re-hook rebuilds): the new row
            // gets the design's msgRise entrance (fade in + 7px rise).
            bool appended = Vm.Thread.Count > _lastMsgCount;
            _lastMsgCount = Vm.Thread.Count;

            // A new turn just started (the user's message is last, thinking hasn't
            // begun): drop the per-turn live views/caches so the NEXT run's steps
            // reveal fresh instead of reusing the prior run's rows.
            if (empty || (Vm.Thread.Count > 0 && Vm.Thread[Vm.Thread.Count - 1].Role == "user"))
            {
                _thinkingView = null;
                _activityView = null;
                _runningView = null;
                ResetLiveCaches();
                // look's pictures belong to the turn that took them.
                LookImageStash.Clear();
            }
            if (empty)
            {
                // New chat: the elapsed clock belongs to the cleared thread.
                _elapsedTimer?.Stop();
                _elapsedSw = null;
                if (ElapsedText != null) ElapsedText.Text = "";
                BodyHost.Children.Add(HomeState());
                return;
            }

            var thread = new StackPanel { Margin = new Thickness(16, 16, 16, 16) };
            foreach (var m in Vm.Thread)
                thread.Children.Add(Message(m));
            if (appended && thread.Children.Count > 0)
                MsgRise(thread.Children[thread.Children.Count - 1] as FrameworkElement);
            BodyHost.Children.Add(thread);
            _threadPanel = thread;
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
            var fill = CopilotColors.Token("Cp.Faint");
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
            {
                // Ruling R26: a cancelled turn still shows what happened (its
                // receipt and evidence, from the envelopes) above the pane's own
                // "Cancelled" line; any text the model wrote first stays its own.
                if (string.IsNullOrWhiteSpace(m.PaneNote)) return InterruptedLine(m.Text);   // pre-protocol-3 history
                var cancelled = InterruptedLine(m.PaneNote);
                bool ownsEvidence = m.Evidence != null && OwnsEvidence(m);
                var evidence = ownsEvidence ? Controls.TurnEvidenceView.Build(m.Evidence, m.Lang, EvidenceActions(m), m.ResultCard, RetainedCard(m)) : null;
                if (string.IsNullOrWhiteSpace(m.Text) && evidence == null) return cancelled;
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = BubbleMaxWidth() };
                if (!string.IsNullOrWhiteSpace(m.Text))
                    stack.Children.Add(CopilotMessageBubble.MarkdownText(m.Text, BubbleMaxWidth(),
                        tableDownloads: !(ownsEvidence && m.Evidence.Tables.Count > 0)));
                if (evidence != null) stack.Children.Add(evidence);
                stack.Children.Add(cancelled);
                return stack;
            }

            // AI row — Slate design: full-width plain text column, no avatar.
            var aiRow = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            col.MaxWidth = BubbleMaxWidth();

            // Live turn: ONE activity indicator (the step card once steps
            // arrive, else the single-line thinking trail), then the reply as
            // it streams, the pictures `look` took so far, and the pulsing dots.
            // Only this message re-renders per stream tick (TryRenderLiveTail).
            if (m.Kind == CpMsgKind.Thinking)
            {
                // The running card is full width (design Running.dc.html), not the 85% reply column.
                if (BodyHost != null && BodyHost.ActualWidth > 0) col.MaxWidth = System.Math.Max(320, BodyHost.ActualWidth - 32);
                col.Width = col.MaxWidth;
                aiRow.Children.Add(Controls.RunningCardView.CopilotLabel());
                col.Children.Add(RunningPanel(m));
                if (m.StreamingReply && !string.IsNullOrEmpty(m.Text))
                    col.Children.Add(LiveReplyText(m.Text, col.MaxWidth));
                var looks = LiveLookImages();
                if (looks != null) col.Children.Add(looks);
                if (m.StreamingReply) col.Children.Add(StreamingDots());
                aiRow.Children.Add(col);
                return aiRow;
            }

            // A finished reply: the turn's steps as the collapsed activity card
            // (same card the live turn showed, so step text reads the same).
            // The turn's evidence (receipt, evidence strip, look images, check
            // rules, Export). Drawn once per turn: when several messages carry
            // the same TurnEvidence (an ask card, then the reply that continued
            // the turn) only the last one draws it.
            bool showEvidence = m.Evidence != null
                && (m.Kind == CpMsgKind.AiReply || m.Kind == CpMsgKind.Clarify) && OwnsEvidence(m);
            // A result card draws the headline, the chips, the "Steps" toggle and the action row itself:
            // then the steps card and the save row do not draw on their own.
            bool cardShown = showEvidence && m.ResultCard != null && m.ResultCard.Kind != ResultKind.None;
            // No server reply for this card: the model's first sentence is the headline, not a count beside the model's text.
            if (cardShown && m.Kind == CpMsgKind.AiReply) m.ResultCard.AdoptProseHeadline(m.Text);
            if (m.Kind == CpMsgKind.AiReply) aiRow.Children.Add(Controls.RunningCardView.CopilotLabel());
            // A result card is full width, like the running card (the boards), not the 85% reply column.
            // A long prose answer is laid out like a card (headline + detail), so it gets the same width.
            bool serverProse = m.Kind == CpMsgKind.AiReply && m.ResultCard != null && m.ResultCard.ProseResult;
            // A turn whose writes all changed nothing, with no server reply: the model's first sentence is the headline.
            bool noopProse = m.Kind == CpMsgKind.AiReply && m.ResultCard != null && m.ResultCard.NoChange && Model.ProseSplit.IsPlainProse(m.Text, 6);
            bool proseLayout = !cardShown && m.Kind == CpMsgKind.AiReply && (serverProse || noopProse || Model.ProseSplit.Applies(m.Text));
            // Without a card the evidence view hosts the quiet "Steps" toggle (what ran, one line for failures, what was read).
            bool stepsInEvidence = showEvidence && !cardShown;
            if ((cardShown || proseLayout) && BodyHost != null && BodyHost.ActualWidth > 0)
            {
                col.MaxWidth = System.Math.Max(320, BodyHost.ActualWidth - 32);
                col.Width = col.MaxWidth;
            }
            if (m.Kind == CpMsgKind.AiReply && m.Steps != null && m.Steps.Count > 0 && !cardShown && !stepsInEvidence)
                col.Children.Add(ActivityBlock(m));
            // The reply's tables already get "Export to Excel" from the evidence:
            // no second "Download CSV" button under the same table.
            // Also when an earlier message of the same turn holds the same evidence: the
            // message that draws it carries the export, this one must not add its own.
            bool evidenceExports = m.Evidence != null && m.Evidence.Tables.Count > 0
                && (m.Kind == CpMsgKind.AiReply || m.Kind == CpMsgKind.Clarify);

            // The server's one-sentence reply is the card's headline: the model's prose above it would say it twice.
            if (!string.IsNullOrEmpty(m.Text) && !(cardShown && m.ResultCard.ReplacesProse))
            {
                // AI replies are markdown (headers, **bold**, tables, lists); a long prose answer gets a headline and "More detail".
                col.Children.Add(proseLayout
                    ? Controls.ProseAnswer.Build(m.Text, m.Lang, col.MaxWidth, tableDownloads: !evidenceExports, serverHeadline: serverProse ? m.ResultCard.ServerReply : null)
                    : CopilotMessageBubble.MarkdownText(m.Text, col.MaxWidth, tableDownloads: !evidenceExports));
                // Copyable: right-click → Copy message, plus a hover ⧉ button.
                CopilotMessageBubble.AttachCopyMenu(col, m.Text);
                col.Children.Add(CopilotMessageBubble.HoverReveal(aiRow, CopilotMessageBubble.CopyButton(m.Text)));
            }

            if (showEvidence)
            {
                var evActions = EvidenceActions(m);
                if (cardShown)
                {
                    if (m.Steps != null && m.Steps.Count > 0) evActions.StepsPanel = ActivityBlock(m, open: true);
                    if (CanSaveAsCommand(m)) evActions.SaveAsCommand = () => Vm?.OpenSaveCommandSheet(m);
                }
                else
                {
                    if (m.Steps != null && m.Steps.Count > 0) evActions.Steps = m.Steps;
                    if (serverProse) evActions.Notes = m.ResultCard.Notes;
                }
                var evidence = Controls.TurnEvidenceView.Build(m.Evidence, m.Lang, evActions, m.ResultCard, RetainedCard(m));
                if (evidence != null) col.Children.Add(evidence);
                // A prose answer that also has rows: the rows are the card; the full text sits behind "More detail" under it.
                if (cardShown && serverProse)
                {
                    var more = Controls.ProseAnswer.MoreDetail(m.Text, m.ResultCard.ServerReply, m.Lang, col.MaxWidth, !evidenceExports);
                    if (more != null) col.Children.Add(more);
                }
                else if (cardShown && m.ResultCard.AdoptedRest.Length > 0)
                {
                    var more = Controls.ProseAnswer.MoreDetail(m.ResultCard.AdoptedRest, m.ResultCard.ServerReply, m.Lang, col.MaxWidth, !evidenceExports);
                    if (more != null) col.Children.Add(more);
                }
            }
            if (m.Kind == CpMsgKind.AiReply && !string.IsNullOrWhiteSpace(m.PaneNote))
            {
                var note = new TextBlock { Text = m.PaneNote, FontSize = 11, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
                note.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                col.Children.Add(note);
            }
            // "Highlight in model" action row (PRD A9): when the answer lists
            // 2+ clickable element ids, offer the whole set as one selection —
            // the per-id click stays for single elements. Same local
            // select_elements path, no backend round-trip.
            if (m.Kind == CpMsgKind.AiReply && !string.IsNullOrEmpty(m.Text))
            {
                var elementIds = RevitWebAppSync.Helpers.MarkdownRenderer.ExtractElementIds(m.Text);
                if (elementIds.Count >= 2 && !(m.ResultCard != null && m.ResultCard.NoChange))
                    col.Children.Add(HighlightRow(elementIds));
            }

            switch (m.Kind)
            {
                // NOTE: Thinking is handled above and returns early — there is
                // deliberately no Thinking case here, so the panel can never
                // render a second loading indicator for one message.
                case CpMsgKind.Clarify:
                    if (m.Ask != null)
                        col.Children.Add(Controls.AskCard.Build(m.Ask, live: m.Ask.AnsweredSummary == null && IsThreadTail(m),
                            (answer, summary) => Vm?.SubmitAsk(m, answer, summary)));
                    else if (!string.IsNullOrWhiteSpace(m.Question))
                        col.Children.Add(CopilotMessageBubble.MarkdownText(m.Question, col.MaxWidth));
                    break;
                case CpMsgKind.SignIn:
                case CpMsgKind.Attention: col.Children.Add(NoticeCard(m)); break;
            }

            // Feedback (👍/👎) — only on final AI replies (not thinking/clarify/
            // confirm frames). Right-aligned row, tint-on-click, one rating sent.
            if (m.Kind == CpMsgKind.AiReply)
            {
                // Saved Commands J1 (A1): "Save as command" on a completed reply
                // that actually ran tools from a natural-language prompt. Pure
                // Q&A (0 tools), slash-command turns and interrupted replies
                // don't qualify; signed-out shows the sign-in card instead
                // (handled in the VM).
                bool canSave = CanSaveAsCommand(m);
                if (canSave && !cardShown)
                {
                    var saveRow = new StackPanel
                    { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                    saveRow.Children.Add(CopilotMessageBubble.SaveCommandButton(
                        () => Vm?.OpenSaveCommandSheet(m)));
                    col.Children.Add(saveRow);
                }
                col.Children.Add(BuildFeedback(m, SourcePromptFor(m)));
                // The one-time rating banner is gone (result pattern): one feedback control per reply.
            }

            aiRow.Children.Add(col);
            return aiRow;
        }

        // One view per result card: Rebuild() redraws the whole thread, but the card's
        // visual state (an After cell being edited) lives in its tree, so the built
        // card is kept per model and re-parented. Rebuilt only when what it draws from
        // the live actions (undo/redo chips) changes. Colours are resource references,
        // so a retained card follows a theme switch without a rebuild.
        private sealed class RetainedCardEntry { public string Key; public FrameworkElement View; }
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ResultCardModel, RetainedCardEntry> _cards
            = new System.Runtime.CompilerServices.ConditionalWeakTable<ResultCardModel, RetainedCardEntry>();

        private Func<ResultCardModel, Controls.TurnEvidenceActions, bool, string, FrameworkElement> RetainedCard(ChatMessage m) =>
            (card, actions, canUndo, undoOff) =>
            {
                bool Ok(Func<bool> f) { try { return f?.Invoke() ?? false; } catch { return false; } }
                string key = $"{canUndo}|{undoOff}|{Ok(actions.CanUndoLast)}|{Ok(actions.CanRedo)}|{m.Lang}|{card.Groups.Count(g => g.LeftAsIs)}";
                if (_cards.TryGetValue(card, out var e) && e.Key == key && e.View != null)
                {
                    if (e.View.Parent is Panel p) p.Children.Remove(e.View);
                    if (e.View.Parent == null) return e.View;
                }
                var view = Controls.ResultCardView.Build(card, m.Lang, actions, canUndo, undoOff);
                _cards.Remove(card);
                if (view != null) _cards.Add(card, new RetainedCardEntry { Key = key, View = view });
                return view;
            };

        /// <summary>True when no later message carries this message's evidence.</summary>
        private bool OwnsEvidence(ChatMessage m)
        {
            var vm = Vm;
            if (vm == null || m?.Evidence == null) return m?.Evidence != null;
            int i = vm.Thread.IndexOf(m);
            if (i < 0) return true;
            return EvidenceDedupe.IsLastHolder(vm.Thread.Select(t => (object)t.Evidence).ToList(), i);
        }

        // ─── Live reply text (streaming) ─────────────────────────────────────
        // The reply grows by ~80 characters a tick. Drawing it whole each tick
        // flashed a half-written table as raw "| a | b |" pipes, so the tail
        // block that is not finished yet (a table with no line after it, an
        // unclosed ``` fence) is held back and named in one faint line; it
        // appears whole once it ends, or when the turn's final reply replaces
        // this message. The markdown element is reused while the drawable part
        // has not changed (a tick inside a held table re-parses nothing).
        private FrameworkElement _liveMd;
        private string _liveMdText;
        private double _liveMdWidth;

        private FrameworkElement LiveReplyText(string text, double maxWidth)
        {
            var stable = RevitWebAppSync.Helpers.MarkdownBlocks.StableForStreaming(text, out var held);
            var host = new StackPanel();
            if (stable.Length > 0)
            {
                if (_liveMd == null || _liveMdText != stable || _liveMdWidth != maxWidth)
                {
                    _liveMd = CopilotMessageBubble.MarkdownText(stable, maxWidth);
                    _liveMdText = stable;
                    _liveMdWidth = maxWidth;
                }
                else if (_liveMd.Parent is Panel oldParent)
                    oldParent.Children.Remove(_liveMd);
                host.Children.Add(_liveMd);
            }
            if (held != RevitWebAppSync.Helpers.HeldBlock.None)
            {
                var tb = new TextBlock { Text = held == RevitWebAppSync.Helpers.HeldBlock.Table ? "Preparing a table…" : "Writing code…", FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 6) };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                host.Children.Add(tb);
            }
            return host;
        }

        // True when m is the very last message in the whole thread — used to
        // stop a superseded message from re-rendering its own
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
                Background = CopilotColors.Token("Cp.Raised"),
                BorderBrush = CopilotColors.Token("Cp.Line"),
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
                Width = 15, Height = 15, Stretch = Stretch.Uniform, Fill = CopilotColors.Token("Cp.Rating.Fill"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
                Data = Geometry.Parse("M12,2 l3.1,6.3 6.9,1 -5,4.9 1.2,6.8 L12,17.8 5.8,21 7,14.2 2,9.3 l6.9,-1 Z")
            });
            left.Children.Add(new TextBlock
            {
                Text = "How's Copilot doing?", FontSize = 11.5, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.Token("Cp.Muted"), VerticalAlignment = VerticalAlignment.Center
            });
            inner.Children.Add(left);

            var rate = new Button
            {
                Content = "Rate",
                Foreground = CopilotColors.Token("Cp.Accent"), FontSize = 12, FontWeight = FontWeights.SemiBold,
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
                    Stroke = CopilotColors.Token("Cp.Faint"),
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
                    Text = m.Time, FontSize = 10, Foreground = CopilotColors.Token("Cp.Faint"),
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
                    Text = ProgressTrail.TotalElapsedText(m.Steps),
                    FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                };
                metric.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
                metric.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
                right.Children.Add(metric);
            }

            var promptLabel = new TextBlock
            {
                Text = "", FontSize = 10.5,
                Foreground = CopilotColors.Token("Cp.Faint"),
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
                var accent = CopilotColors.Token("Cp.Accent");
                var idle = CopilotColors.Token("Cp.Faint");
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
                copyIcon.Stroke = CopilotColors.Token("Cp.Green");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(1600) };
                t.Tick += (_, __) =>
                {
                    t.Stop();
                    copyIcon.Data = CopilotIcons.Get("copy");
                    copyIcon.Stroke = CopilotColors.Token("Cp.Faint");
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
                Stroke = CopilotColors.Token("Cp.Faint"), StrokeThickness = 1.9,
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
                Background = CopilotColors.Token("Cp.Raised"),
                BorderBrush = CopilotColors.Token("Cp.Line"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
                Padding = new Thickness(11, 11, 11, 10), Margin = new Thickness(0, 9, 0, 0),
            };
            MsgRise(card);
            var sp = new StackPanel();
            card.Child = sp;

            sp.Children.Add(new TextBlock
            {
                Text = "What was off?", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.Token("Cp.Muted"), Margin = new Thickness(0, 0, 0, 8),
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
                    chip.Background = on ? CopilotColors.Token("Cp.ChipOnBg") : Brushes.Transparent;
                    chip.BorderBrush = on ? Brushes.Transparent : CopilotColors.Token("Cp.Line");
                    label.Foreground = CopilotColors.Token(on ? "Cp.Accent" : "Cp.Muted");
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
                Background = CopilotColors.Token("Cp.Menu"), Foreground = CopilotColors.Token("Cp.Ink"),
                BorderBrush = CopilotColors.Token("Cp.Line"), CaretBrush = CopilotColors.Token("Cp.Ink"),
            };
            // Placeholder: faint hint that clears on focus (WPF TextBox has none).
            var hint = "Add details (optional)";
            note.Text = hint; note.Foreground = CopilotColors.Token("Cp.Faint");
            note.GotFocus += (_, __) => { if (note.Text == hint) { note.Text = ""; note.Foreground = CopilotColors.Token("Cp.Ink"); } };
            note.LostFocus += (_, __) => { if (note.Text.Length == 0) { note.Text = hint; note.Foreground = CopilotColors.Token("Cp.Faint"); } };
            sp.Children.Add(note);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            var send = new Button
            {
                Content = new TextBlock { Text = "Send feedback", FontSize = 11, FontWeight = FontWeights.SemiBold },
                Padding = new Thickness(13, 7, 13, 7), BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand, Foreground = CopilotColors.Token("Cp.AccentContrast"),
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
                Foreground = CopilotColors.Token("Cp.Faint"), Padding = new Thickness(8, 7, 8, 7),
            };
            Controls.FlatButton.Apply(cancel, 6);
            cancel.Click += (_, __) => close();
            actions.Children.Add(cancel);
            sp.Children.Add(actions);

            // Auto-attached context row under a hairline.
            var ctx = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = CopilotColors.Token("Cp.Line"),
                Padding = new Thickness(0, 9, 0, 0), Margin = new Thickness(0, 10, 0, 0),
            };
            var ctxRow = new StackPanel { Orientation = Orientation.Horizontal };
            var clipIcon = new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stroke = CopilotColors.Token("Cp.Faint"), Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse("M21.44,11.05 L12.25,20.24 A6,6 0 0 1 3.76,11.75 L12.33,3.18 A4,4 0 0 1 18,8.84 L9.41,17.41 A2,2 0 0 1 6.58,14.58 L15.07,6.1"),
            };
            ctxRow.Children.Add(clipIcon);
            string cmd = m.ToolId != null && m.ToolId != "ai-generated" ? m.ToolId : null;
            ctxRow.Children.Add(new TextBlock
            {
                Text = Model.CopilotContext.ContextLabel(cmd),
                FontSize = 9.5, Foreground = CopilotColors.Token("Cp.Faint"),
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
                Data = Geometry.Parse("M20,6 L9,17 L4,12"), Stroke = CopilotColors.Token("Cp.Accent"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            row.Children.Add(check);
            row.Children.Add(new TextBlock
            {
                Text = "Thanks — your feedback helps improve BINA.",
                FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.Token("Cp.Accent"), VerticalAlignment = VerticalAlignment.Center,
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

        // ─── Agent-activity card ─────────────────────────────────────────────
        // ONE card per turn: the step checklist (plus thinking prose when a
        // backend sends it). The LIVE instance is cached per turn so its rows
        // update in place; completed messages build a fresh, collapsed one.
        private AgentActivityView _activityView;

        // Design behavior (2026-08-30): the card stays OPEN with its working
        // bar for the whole live turn; it collapses when the turn completes
        // (the finished reply's ActivityBlock starts closed).
        // The live card (design Running.dc.html). ONE instance per turn so its animations keep running between ticks.
        private Controls.RunningCardView _runningView;

        private Controls.RunningCardView RunningPanel(ChatMessage m)
        {
            if (_runningView == null)
            {
                _runningView = new Controls.RunningCardView { OnLayoutChanged = RepinIfSticky };
                _runningView.OnStop = () => { var c = Vm?.CancelSendCommand; if (c != null && c.CanExecute(null)) c.Execute(null); };
            }
            else if (_runningView.Parent is Panel oldParent)
                oldParent.Children.Remove(_runningView);
            _runningView.Language = LiveLanguage();
            _runningView.Update(m.LiveSteps, m.StreamingReply ? m.Text?.Length ?? 0 : 0);
            return _runningView;
        }

        private AgentActivityView ActivityPanel(ChatMessage m)
        {
            if (_activityView == null)
            {
                _activityView = new AgentActivityView { Margin = new Thickness(0, 0, 0, 12) };
                _activityView.OnLayoutChanged = RepinIfSticky;
            }
            else if (_activityView.Parent is Panel oldParent)
                oldParent.Children.Remove(_activityView);
            _activityView.Language = LiveLanguage();
            _activityView.Update(m.LiveReasoningSteps, m.LiveSteps, streaming: true, answerStarting: false,
                                 replyChars: m.Text?.Length ?? 0);
            return _activityView;
        }

        /// <summary>The language of the prompt the running turn answers (what the receipts follow), English when unknown.</summary>
        private ReceiptLanguage LiveLanguage()
        {
            try
            {
                var last = Vm?.Thread.LastOrDefault(t => t.Role == "user");
                return last == null || string.IsNullOrWhiteSpace(last.Text) ? ReceiptLanguage.En : PromptLanguage.Detect(last.Text);
            }
            catch (System.Exception) { return ReceiptLanguage.En; }
        }

        /// <summary>Fresh, collapsed activity card for a COMPLETED message —
        /// same lifecycle as the old ReasoningBlock (once per Rebuild).</summary>
        private static bool CanSaveAsCommand(ChatMessage m) =>
            m.Kind == CpMsgKind.AiReply && m.ToolsUsed != null && m.ToolsUsed.Count > 0
            && !string.IsNullOrWhiteSpace(m.SourcePrompt)
            && !m.SourcePrompt.TrimStart().StartsWith("/")
            && !m.Interrupted;

        private AgentActivityView ActivityBlock(ChatMessage m, bool open = false)
        {
            var view = new AgentActivityView { Margin = new Thickness(0, 0, 0, 12) };
            view.OnLayoutChanged = RepinIfSticky;
            view.Update(m.ReasoningSteps, m.Steps, streaming: false, answerStarting: false, seedOpen: open);
            return view;
        }

        /// <summary>SignIn / Attention notice card (design: copilot-signin-states,
        /// 2026-08-27). Lives in the thread where the answer would have gone, so
        /// the drafter sees exactly what their prompt is waiting on. Amber for
        /// Attention (nothing broke, one thing to do), plain for SignIn. Every
        /// colour is a pane token; the origin line is mono for support.</summary>
        private FrameworkElement NoticeCard(ChatMessage m)
        {
            bool attention = m.Kind == CpMsgKind.Attention;
            var amber = CopilotColors.Token("Cp.Notice");
            var amberBg = CopilotColors.Token("Cp.NoticeBg");

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

        /// <summary>Receipt actions for a reply (Undo is W3's, per turn).</summary>
        private Controls.TurnEvidenceActions EvidenceActions(ChatMessage m) => new Controls.TurnEvidenceActions
        {
            CanUndo = () => Vm != null && Vm.CanUndoTurn(m),
            WhyNotUndo = () => Vm?.WhyNotUndoTurn(m) ?? BinaVibe.Copilot.Undo.UndoBlock.None,
            Undo = () => Vm?.UndoTurn(m),
            CanUndoLast = () => Vm != null && Vm.CanUndoLastBatch(m),
            UndoLast = () => Vm?.UndoLastBatch(m),
            CanRedo = () => Vm != null && Vm.CanRedoTurn(m),
            Redo = () => Vm?.RedoTurn(m),
            Export = t => Vm?.ExportTable(t),
            Highlight = h => Vm?.HighlightHandle(h, m.Lang),
            // Result card (result pattern): the in-model layer and Revit's own selection.
            ShowInModel = () => Vm?.ShowResultInModel(m),
            SelectGroup = g => Vm?.Revit?.SelectGroup(g),
            ZoomGroup = g => Vm?.Revit?.ZoomGroup(g),
            OpenTypeProperties = g => Vm?.Revit?.OpenTypeProperties(g),
            ShowElements = ids => SelectElements(ids),
            SourcePageWords = (att, page) => Vm != null ? Vm.SourcePageWordsAsync(att, page) : System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<BinaVibe.Mcp.Tools.PdfWord>>(null),
            OpenSource = (att, page) => Vm?.OpenSource(att, page),
            OpenView = (id, sheet) => Vm?.Revit != null ? Vm.Revit.OpenViewAsync(id, m.ResultCard?.DocKey, sheet) : System.Threading.Tasks.Task.FromResult<string>("Revit is not ready."),
            ClassifyIds = ids => Vm?.Revit != null ? Vm.Revit.ClassifyAsync(ids, m.ResultCard?.DocKey) : System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyDictionary<long, ItemClass>>(null),
            Redraw = () => Vm?.RequestRedraw(),
            RunCheckAction = a => Vm != null ? Vm.RunCheckActionAsync(m, a) : System.Threading.Tasks.Task.FromResult<string>("Copilot is not ready."),
            SendPrompt = t => Vm?.ChatSend(t),
            ResetIsolate = () => Vm?.Revit != null ? Vm.Revit.ResetIsolateAsync() : System.Threading.Tasks.Task.FromResult<string>("Revit is not ready."),
            EditValue = (g, r, v) => Vm != null ? Vm.EditValueAsync(m, g, r, v) : System.Threading.Tasks.Task.FromResult<string>("Copilot is not ready."),
        };

        // Answer action row (PRD A9): count tag + "Highlight in model" chip.
        // Same chip idiom as FollowupChip; the crosshair glyph is drawn inline
        // (CopilotIcons has no crosshair and one icon doesn't warrant a map row).
        private FrameworkElement HighlightRow(System.Collections.Generic.List<long> elementIds)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            // The count: a quiet label (the boards have no tinted tag); the action is a Secondary button with the target icon.
            var count = new TextBlock { Text = elementIds.Count + " elements", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            row.Children.Add(count);

            var ids = elementIds.ToArray();
            row.Children.Add(Controls.CardKit.Secondary("Highlight in model", () => SelectElements(ids), Controls.CardKit.Icons.Target));
            return row;
        }

        // Copy-to-clipboard affordances, the bot avatar, and base64 image decoding
        // live in CopilotMessageBubble (shared with the History detail view).
    }
}
