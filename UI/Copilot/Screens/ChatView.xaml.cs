using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
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

        /// <summary>Raised by the blocked state's "Upgrade plan" CTA (the panel opens the sheet).</summary>
        public event System.Action UpgradeRequested;

        private void Hook()
        {
            if (_hooked != null)
            {
                _hooked.Thread.CollectionChanged -= OnThread;
                _hooked.UsageChanged -= UpdateBlocked;
                _hooked.PropertyChanged -= OnVmProp;
            }
            _hooked = Vm;
            if (_hooked != null)
            {
                _hooked.Thread.CollectionChanged += OnThread;
                _hooked.UsageChanged += UpdateBlocked;
                _hooked.PropertyChanged += OnVmProp;
                Prompt.BindUsage(_hooked);
                _ = _hooked.RefreshUsageAsync();
            }
            Rebuild();
            UpdateBlocked();
        }

        private void OnVmProp(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.IsSending)) UpdateBlocked();
        }

        /// <summary>At 100% usage the composer is replaced by the blocked state —
        /// centered over the empty body, or a bottom section under a thread.</summary>
        private void UpdateBlocked()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((System.Action)UpdateBlocked);
                return;
            }
            var vm = Vm;
            bool blocked = vm != null && vm.Usage != null && vm.Usage.AtLimit && !vm.IsSending;
            if (!blocked)
            {
                BlockedHost.Visibility = Visibility.Collapsed;
                BlockedHost.Content = null;
                Prompt.Visibility = Visibility.Visible;
                Grid.SetRow(BlockedHost, 2);
                return;
            }
            bool centered = vm.Thread.Count == 0;
            Grid.SetRow(BlockedHost, centered ? 1 : 2);
            BlockedHost.VerticalAlignment = centered ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            BlockedHost.Content = Controls.BlockedView.Build(
                vm.Usage,
                () => UpgradeRequested?.Invoke(),
                () => vm.UsageService != null ? vm.UsageService.NotifyAdminAsync() : System.Threading.Tasks.Task.CompletedTask,
                centered);
            BlockedHost.Visibility = Visibility.Visible;
            Prompt.Visibility = Visibility.Collapsed;
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

        private void OnScroll(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0)
            {
                // A user/layout scroll with no content-size change: update intent.
                // Within 4px of the bottom counts as "following".
                _stick = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4;
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
            if (empty || (Vm.Thread.Count > 0 && Vm.Thread[Vm.Thread.Count - 1].Role == "user"))
                _thinkingView = null;

            if (empty) { BodyHost.Children.Add(EmptyState()); return; }

            ConvCount.Text = $"Conversation · {Vm.Thread.Count(m => m.Role == "user")} messages";
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
                    m.Files?.Select(f => (f.Name, LineCount(f.Content))), BubbleMaxWidth(), m.Time,
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
            if (m.Kind == CpMsgKind.Thinking && !m.StreamingReply)
            {
                col.Children.Add(ThinkingTrail(m.Text));
                aiRow.Children.Add(col);
                return aiRow;
            }
            if (m.Kind == CpMsgKind.Thinking && m.StreamingReply)
            {
                if (!string.IsNullOrEmpty(m.Text))
                    col.Children.Add(CopilotMessageBubble.MarkdownText(m.Text, col.MaxWidth));
                // Pin the pulsing-dots liveness indicator below the partial prose.
                // Still Kind=Thinking, so this only shows while the turn runs; the
                // final reply (Kind flips off Thinking) never renders it.
                col.Children.Add(StreamingDots());
                aiRow.Children.Add(col);
                return aiRow;
            }

            // Design: a completed reply shows ONLY the answer — the live single-line
            // thinking indicator fades out and no step trail is persisted.
            // (ProgressTracePanel/ToolTracePanel remain for old serialized history.)

            if (!string.IsNullOrEmpty(m.Text))
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

            switch (m.Kind)
            {
                // NOTE: Thinking is handled above (ThinkingTrail) and returns early —
                // there is deliberately no Thinking case here, so the panel can
                // never render a second loading indicator for one message.
                case CpMsgKind.Clarify: col.Children.Add(ClarifyCard(m)); break;
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
                col.Children.Add(BuildFeedback(m, SourcePromptFor(m)));
                var nudge = BuildRatingNudge(m);
                if (nudge != null) col.Children.Add(nudge);
            }

            aiRow.Children.Add(col);
            return aiRow;
        }

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
            if (CopilotPrefs.Load().RatingSubmitted) return null;
            // Only under the newest message, and not before the user has had a
            // couple of exchanges (don't ask after the very first answer).
            if (Vm.Thread.Count == 0 || !ReferenceEquals(Vm.Thread[Vm.Thread.Count - 1], reply)) return null;
            if (Vm.Thread.Count(x => x.Role == "user") < 2) return null;

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

            var promptLabel = new TextBlock
            {
                Text = "Was this helpful?", FontSize = 10.5,
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

        private FrameworkElement ClarifyCard(ChatMessage m)
        {
            var outer = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#140F1B2D"), BorderThickness = new Thickness(1), Background = CopilotColors.From("#ffffff") };
            var sp = new StackPanel();

            var head = new Border { Padding = new Thickness(12, 10, 12, 10), BorderBrush = CopilotColors.From("#140F1B2D"), BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(12, 12, 0, 0) };
            var hg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            hg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#f5f3ff"), 0));
            hg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#eff6ff"), 1));
            head.Background = hg;
            var hs = new StackPanel { Orientation = Orientation.Horizontal };
            var star = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 0) };
            star.Child = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Fill = CopilotMessageBubble.StarGradient(), Data = CopilotIcons.Get("sparkleSolid"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hs.Children.Add(star);
            hs.Children.Add(new TextBlock { Text = "I need a bit more detail", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#1e3a8a"), VerticalAlignment = VerticalAlignment.Center });
            head.Child = hs;
            sp.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
            body.Children.Add(new TextBlock { Text = m.Question, FontSize = 12.5, Foreground = CopilotColors.From("#131c2b"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 10) });
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
            var foot = new Border { Background = CopilotColors.From("#f3f6f9"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 5, 0, 0) };
            foot.Child = new TextBlock { Text = "Or just rephrase your question with more detail.", FontSize = 11, Foreground = CopilotColors.From("#586273"), TextWrapping = TextWrapping.Wrap };
            body.Children.Add(foot);
            sp.Children.Add(body);
            outer.Child = sp;
            return outer;
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

        /// <summary>Line count matching AttachmentChip's "N ln" display, for the
        /// shared user-bubble file chips.</summary>
        private static int LineCount(string content) =>
            string.IsNullOrEmpty(content) ? 0 : content.Count(c => c == '\n') + 1;

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

        // ─── Empty state (Slate: centered hero star + suggestion rows) ───────
        private FrameworkElement EmptyState()
        {
            var root = new StackPanel { Margin = new Thickness(26, 56, 26, 24) };

            // Hero: sparkle cluster — one big gradient star with a soft blue
            // glow plus two small satellite stars at its top/bottom right.
            var starGeom = Geometry.Parse("M12,1.1 C12.45,7.05 16.95,11.55 22.9,12 C16.95,12.45 12.45,16.95 12,22.9 C11.55,16.95 7.05,12.45 1.1,12 C7.05,11.55 11.55,7.05 12,1.1 Z");
            Path Star(double size, double glow)
            {
                var p = new Path
                {
                    Width = size, Height = size, Stretch = Stretch.Uniform,
                    Fill = CopilotMessageBubble.StarGradient(), Data = starGeom,
                };
                p.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#3b8ef7"),
                    BlurRadius = glow, ShadowDepth = 0, Opacity = 0.55,
                };
                return p;
            }
            var hero = new Grid { Width = 72, Height = 60, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 22) };
            var big = Star(50, 16);
            big.HorizontalAlignment = HorizontalAlignment.Left;
            big.VerticalAlignment = VerticalAlignment.Center;
            big.Margin = new Thickness(2, 0, 0, 0);
            var small1 = Star(17, 8);
            small1.HorizontalAlignment = HorizontalAlignment.Right;
            small1.VerticalAlignment = VerticalAlignment.Top;
            small1.Margin = new Thickness(0, 0, 4, 0);
            var small2 = Star(13, 6);
            small2.HorizontalAlignment = HorizontalAlignment.Right;
            small2.VerticalAlignment = VerticalAlignment.Bottom;
            small2.Margin = new Thickness(0, 0, 0, 8);
            hero.Children.Add(big); hero.Children.Add(small1); hero.Children.Add(small2);
            root.Children.Add(hero);

            root.Children.Add(new TextBlock
            {
                Text = "How can I help with your model?",
                FontSize = 19, FontWeight = FontWeights.Bold,
                Foreground = CopilotColors.From("#131c2b"),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(new TextBlock
            {
                Text = "Describe what you need in plain words — I'll turn it into a Revit command you can review and apply.",
                FontSize = 12.5, Foreground = CopilotColors.From("#586273"),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                LineHeight = 19, MaxWidth = 268, Margin = new Thickness(0, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            // Suggestions: hairline-separated rows (icon · label · chevron).
            var sug = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
            sug.Children.Add(new Border { Height = 1, Background = CopilotColors.From("#140F1B2D") });
            (string icon, string label, string prompt)[] suggestions =
            {
                ("M3,4.5 h18 v15 h-18 Z M3,9.5 h18 M3,14.5 h18 M8,4.5 v5 M14,9.5 v5 M10,14.5 v5", "Create walls", "Create exterior walls on Level 2 along grid A–F"),
                ("M3.5,3.5 h17 v17 h-17 Z M3.5,9 h17 M9,9 v11.5 M13,13 h4 M13,16.5 h4", "Generate schedule", "Generate a door schedule for Block A"),
                ("M3.5,11.3 V4.5 a1,1 0 0 1 1,-1 h6.8 a1,1 0 0 1 0.7,0.3 l8,8 a1,1 0 0 1 0,1.4 l-6.8,6.8 a1,1 0 0 1 -1.4,0 l-8,-8 a1,1 0 0 1 -0.3,-0.7 Z M8,8 m-1.4,0 a1.4,1.4 0 1 0 2.8,0 a1.4,1.4 0 1 0 -2.8,0", "Tag rooms", "Tag all rooms on Level 1 with name and number"),
            };
            foreach (var (icon, label, prompt) in suggestions)
            {
                var btn = new Button
                {
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 13, 4, 13),
                };
                btn.Template = SuggestionRowTemplate();
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var ic = new Path
                {
                    Width = 18, Height = 18, Stretch = Stretch.Uniform,
                    Stroke = CopilotColors.From("#131c2b"), StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse(icon), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(ic, 0); g.Children.Add(ic);
                var tb = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#131c2b"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(tb, 1); g.Children.Add(tb);
                var chev = new Path { Width = 15, Height = 15, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#99a3b3"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = Geometry.Parse("M9,6 l6,6 -6,6"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chev, 2); g.Children.Add(chev);
                btn.Content = g;
                var p = prompt;
                // Behavior change: don't send — drop the starter prompt into the
                // composer for the user to edit, then they press send themselves.
                btn.Click += (_, __) => Prompt.InsertStarterPrompt(p);
                sug.Children.Add(btn);
                sug.Children.Add(new Border { Height = 1, Background = CopilotColors.From("#140F1B2D") });
            }
            sug.Children.RemoveAt(sug.Children.Count - 1);  // no hairline after the last row
            root.Children.Add(sug);

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
            b.SetValue(Border.BorderBrushProperty, CopilotColors.From("#140F1B2D"));
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
            b.SetValue(Border.BorderBrushProperty, CopilotColors.From("#140F1B2D"));
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
            b.SetValue(Border.BorderBrushProperty, CopilotColors.From("#140F1B2D"));
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
