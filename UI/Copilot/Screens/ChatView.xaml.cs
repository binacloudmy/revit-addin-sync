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
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
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

        private void Hook()
        {
            if (_hooked != null) _hooked.Thread.CollectionChanged -= OnThread;
            _hooked = Vm;
            if (_hooked != null) _hooked.Thread.CollectionChanged += OnThread;
            Rebuild();
        }

        private void OnThread(object s, NotifyCollectionChangedEventArgs e)
        {
            Rebuild();
            Dispatcher.BeginInvoke(new System.Action(() => Scroller.ScrollToEnd()));
        }

        private void Rebuild()
        {
            if (Vm == null || BodyHost == null) return;
            bool empty = Vm.Thread.Count == 0;
            SubHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            BodyHost.Children.Clear();

            if (empty) { BodyHost.Children.Add(EmptyState()); return; }

            ConvCount.Text = $"Conversation · {Vm.Thread.Count(m => m.Role == "user")} messages";
            var thread = new StackPanel { Margin = new Thickness(14, 16, 14, 16) };
            foreach (var m in Vm.Thread)
                thread.Children.Add(Message(m));
            BodyHost.Children.Add(thread);
        }

        private FrameworkElement Message(ChatMessage m)
        {
            // User bubble (AI proposal/clarify/result templates land in Tasks 12-13).
            if (m.Role == "user")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
                var av = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = CopilotColors.From("#e5e7eb"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) };
                string initial = !string.IsNullOrEmpty(Vm?.UserFirstName) ? Vm.UserFirstName.Substring(0, 1).ToUpperInvariant() : "?";
                av.Child = new TextBlock { Text = initial, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#374151"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var bubble = new Border { Background = CopilotColors.From("#f1f3f5"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), MaxWidth = BubbleMaxWidth() };
                var bubbleStack = new StackPanel();
                // Screenshots pasted with this prompt render above the text.
                if (m.ImagesBase64 != null)
                    foreach (var b64 in m.ImagesBase64)
                    {
                        var src = ImageFromBase64(b64);
                        if (src == null) continue;
                        bubbleStack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(6), ClipToBounds = true,
                            MaxWidth = 220, Margin = new Thickness(0, 0, 0, 6),
                            Child = new Image { Source = src, Stretch = Stretch.Uniform, MaxHeight = 140 },
                        });
                    }
                // Selectable read-only TextBox (a WPF TextBlock cannot be selected/
                // copied) — styled to look identical to the old TextBlock.
                bubbleStack.Children.Add(new TextBox
                {
                    Text = m.Text, FontSize = 13, Foreground = CopilotColors.From("#0b0d12"),
                    TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                    BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                    Padding = new Thickness(0), IsTabStop = false,
                });
                bubble.Child = bubbleStack;
                AttachCopyMenu(bubble, m.Text);
                row.Children.Add(av); row.Children.Add(bubble);
                if (!string.IsNullOrEmpty(m.Text))
                    row.Children.Add(HoverReveal(row, CopyButton(m.Text)));
                return row;
            }

            // AI row: bot avatar + content column (header text + kind-specific body).
            var aiRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            aiRow.Children.Add(BotAvatar());
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            col.MaxWidth = BubbleMaxWidth();

            // Claude-style: show the tools the agent ran FIRST (a compact steps
            // panel with check glyphs), then the final answer below it.
            // Prefer the full persisted phased trail (phases + tools). Legacy
            // messages (no Steps) fall back to the tool-name-only summary card.
            if (m.Steps != null && m.Steps.Count > 0)
                col.Children.Add(ProgressTracePanel(m.Steps));
            else if (m.ToolCallTrace != null && m.ToolCallTrace.Count > 0)
                col.Children.Add(ToolTracePanel(m.ToolCallTrace));

            if (!string.IsNullOrEmpty(m.Text))
            {
                // AI replies are markdown (headers, **bold**, tables, lists) —
                // render formatted, not as raw text. User bubbles stay plain.
                var md = RevitWebAppSync.Helpers.MarkdownRenderer.Render(m.Text, col.MaxWidth);
                md.Margin = new Thickness(0, 0, 0, 8);
                col.Children.Add(md);
                // Copyable: right-click → Copy message, plus a hover ⧉ button.
                // Thinking bubbles are excluded (their text is the live trail).
                if (m.Kind != CpMsgKind.Thinking)
                {
                    AttachCopyMenu(col, m.Text);
                    col.Children.Add(HoverReveal(aiRow, CopyButton(m.Text)));
                }
            }

            switch (m.Kind)
            {
                case CpMsgKind.Thinking: col.Children.Add(ThinkingDots()); break;
                case CpMsgKind.Clarify: col.Children.Add(ClarifyCard(m)); break;
                case CpMsgKind.Proposal: col.Children.Add(ProposalCard(m)); break;
                case CpMsgKind.Running: col.Children.Add(RunningBar(m)); break;
                case CpMsgKind.Result: col.Children.Add(CompactResult(m)); break;
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
                    color = "#16a34a";  // green
                }
                else
                {
                    int n = m.Verdict.Issues != null ? m.Verdict.Issues.Count : 0;
                    badge = $"review: {n} issue" + (n == 1 ? "" : "s") + " ⚠"
                        + (m.Verdict.Remediated ? " (remediation didn't fully clear)" : "");
                    color = "#b45309";  // amber
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
                col.Children.Add(BuildFeedback(SourcePromptFor(m)));

            aiRow.Children.Add(col);
            return aiRow;
        }

        // ─── Feedback (👍/👎) ────────────────────────────────────────────────
        // Same control as ResultView's result screen, ported to chat bubbles.
        // Clicking a thumb fires the VM's fire-and-forget POST, tints the chosen
        // thumb (green up / red down) and disables both so one rating is sent.
        //
        // sourcePrompt is the user message this reply answered — captured here so
        // the rating is attributed to the right prompt. See the LastPrompt note
        // in SendFeedback below.
        private FrameworkElement BuildFeedback(string sourcePrompt)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
            };
            row.Children.Add(new TextBlock
            {
                Text = "Was this helpful?",
                FontSize = 11.5,
                Foreground = CopilotColors.From("#9ca3af"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });

            Button up = null, down = null;
            up = ThumbButton("thumbUp", () => SendFeedback("up", up, down, sourcePrompt));
            down = ThumbButton("thumbDown", () => SendFeedback("down", up, down, sourcePrompt));
            row.Children.Add(up);
            row.Children.Add(down);
            return row;
        }

        private Button ThumbButton(string glyph, System.Action onClick)
        {
            var path = new Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = CopilotColors.From("#9ca3af"),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Data = CopilotIcons.Get(glyph),
            };
            var btn = new Button
            {
                Content = path,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, __) => { try { onClick(); } catch { /* best-effort */ } };
            return btn;
        }

        private void SendFeedback(string rating, Button up, Button down, string sourcePrompt)
        {
            // Attribute the rating to THIS bubble's source prompt (not LastPrompt),
            // so rating an older reply after newer turns isn't mis-attributed.
            Vm?.SubmitFeedback(rating, sourcePrompt);

            var chosen = rating == "up" ? up : down;
            var chosenColor = CopilotColors.From(rating == "up" ? "#16a34a" : "#b91c1c");
            if (chosen?.Content is Path p) p.Stroke = chosenColor;
            if (up != null) up.IsEnabled = false;
            if (down != null) down.IsEnabled = false;
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

        private FrameworkElement ThinkingDots()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            for (int i = 0; i < 3; i++)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = CopilotColors.From("#9ca3af"), Margin = new Thickness(0, 0, 4, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
                var tt = new TranslateTransform();
                dot.RenderTransform = tt;
                var anim = new DoubleAnimation(0, -3, new Duration(TimeSpan.FromMilliseconds(400)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 200),
                    EasingFunction = new SineEase(),
                };
                tt.BeginAnimation(TranslateTransform.YProperty, anim);
                sp.Children.Add(dot);
            }
            return sp;
        }

        private FrameworkElement ClarifyCard(ChatMessage m)
        {
            var outer = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1), Background = Brushes.White };
            var sp = new StackPanel();

            var head = new Border { Padding = new Thickness(12, 10, 12, 10), BorderBrush = CopilotColors.From("#ddd6fe"), BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(12, 12, 0, 0) };
            var hg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            hg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#f5f3ff"), 0));
            hg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#eff6ff"), 1));
            head.Background = hg;
            var hs = new StackPanel { Orientation = Orientation.Horizontal };
            var star = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = Brushes.White, Margin = new Thickness(0, 0, 8, 0) };
            star.Child = new Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, Fill = CopilotColors.From("#7c3aed"), Data = CopilotIcons.Get("sparkleSolid"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hs.Children.Add(star);
            hs.Children.Add(new TextBlock { Text = "I need a bit more detail", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#3b1d75"), VerticalAlignment = VerticalAlignment.Center });
            head.Child = hs;
            sp.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
            body.Children.Add(new TextBlock { Text = m.Question, FontSize = 12.5, Foreground = CopilotColors.From("#374151"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 10) });
            foreach (var o in m.Options)
            {
                var tool = CopilotCatalog.Find(o.ToolId);
                var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 5), BorderBrush = CopilotColors.From("#e5e7eb"), Background = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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
                oc.Children.Add(new TextBlock { Text = o.Label, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#0b0d12"), TextWrapping = TextWrapping.Wrap });
                oc.Children.Add(new TextBlock { Text = o.Hint, FontSize = 10.5, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(oc, 1); g.Children.Add(oc);
                var chev = new Path { Width = 13, Height = 13, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("chevronRight"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chev, 2); g.Children.Add(chev);
                btn.Content = g;
                var prompt = o.Prompt;
                btn.Click += (_, __) => Vm.ChatSendCommand.Execute(prompt);
                body.Children.Add(btn);
            }
            var foot = new Border { Background = CopilotColors.From("#f1f3f5"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 5, 0, 0) };
            foot.Child = new TextBlock { Text = "Or just rephrase your question with more detail.", FontSize = 11, Foreground = CopilotColors.From("#6b7280"), TextWrapping = TextWrapping.Wrap };
            body.Children.Add(foot);
            sp.Children.Add(body);
            outer.Child = sp;
            return outer;
        }

        private FrameworkElement ProposalCard(ChatMessage m)
        {
            var tool = CopilotCatalog.Find(m.ToolId);
            var outer = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1), Background = Brushes.White };
            var sp = new StackPanel();

            // header
            var head = new Border { Background = CopilotColors.From("#fafafa"), BorderBrush = CopilotColors.From("#f1f3f5"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(12, 12, 0, 0) };
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (tool != null)
            {
                var tile = new IconTile { Glyph = tool.Icon, TileBg = tool.TileBg, TileFg = tool.TileFg, TileSize = 26, GlyphSize = 13, Corner = 6, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(tile, 0); hg.Children.Add(tile);
            }
            var hc = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            hc.Children.Add(new TextBlock { Text = tool?.Title ?? "Command", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12") });
            hc.Children.Add(new TextBlock { Text = "Proposed command", FontSize = 11, Foreground = CopilotColors.From("#6b7280") });
            Grid.SetColumn(hc, 1); hg.Children.Add(hc);
            var badge = new TierBadge { Tier = 2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(badge, 2); hg.Children.Add(badge);
            head.Child = hg;
            sp.Children.Add(head);

            // plan + code
            var planBox = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            planBox.Children.Add(new TextBlock { Text = "PLAN", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#7c3aed"), Margin = new Thickness(0, 0, 0, 6) });
            int i = 1;
            foreach (var step in m.PlanSteps)
                planBox.Children.Add(new TextBlock { Text = $"{i++}.  {step}", FontSize = 12, Foreground = CopilotColors.From("#374151"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 2) });

            int lines = string.IsNullOrEmpty(m.Code) ? 0 : m.Code.Split('\n').Length;
            var toggle = new ToggleButton { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = CopilotColors.From("#6b7280") };
            toggle.Template = LinkToggleTemplate();
            toggle.Content = $"View code ({lines} lines)";
            var codeBox = new TextBox { Text = m.Code ?? "", Style = (Style)TryFindResource("Cp.CodeBlock"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 180 };
            toggle.Checked += (_, __) => { codeBox.Visibility = Visibility.Visible; toggle.Content = "Hide code"; };
            toggle.Unchecked += (_, __) => { codeBox.Visibility = Visibility.Collapsed; toggle.Content = $"View code ({lines} lines)"; };
            planBox.Children.Add(toggle);
            planBox.Children.Add(codeBox);
            sp.Children.Add(planBox);

            // actions
            var actions = new Border { Background = CopilotColors.From("#fafafa"), BorderBrush = CopilotColors.From("#f1f3f5"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(0, 0, 12, 12) };
            var ag = new Grid();
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ag.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var regen = SmallGhost("Regenerate"); Grid.SetColumn(regen, 0);
            regen.Click += (_, __) => Vm.ChatRegenerateCommand.Execute(m);
            var edit = SmallGhost("Open editor"); Grid.SetColumn(edit, 1); edit.Margin = new Thickness(5, 0, 0, 0);
            edit.Click += (_, __) => Vm.ChatOpenEditorCommand.Execute(m);
            var run = new Button { Style = (Style)TryFindResource("Cp.RunDark"), Padding = new Thickness(14, 6, 14, 6) };
            var rsp = new StackPanel { Orientation = Orientation.Horizontal };
            rsp.Children.Add(new Path { Width = 10, Height = 10, Stretch = Stretch.Uniform, Fill = Brushes.White, Data = CopilotIcons.Get("play"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            rsp.Children.Add(new TextBlock { Text = "Run", Foreground = Brushes.White });
            run.Content = rsp;
            Grid.SetColumn(run, 3);
            run.Click += (_, __) => Vm.ChatRunCommand.Execute(m);
            ag.Children.Add(regen); ag.Children.Add(edit); ag.Children.Add(run);
            actions.Child = ag;
            sp.Children.Add(actions);

            outer.Child = sp;
            return outer;
        }

        private FrameworkElement RunningBar(ChatMessage m)
        {
            var tool = CopilotCatalog.Find(m.ToolId);
            var bar = new Border { CornerRadius = new CornerRadius(10), BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1), Background = CopilotColors.From("#eff6ff"), Padding = new Thickness(12, 10, 12, 10) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 14, Height = 14, Stroke = CopilotColors.From("#2563eb"), StrokeThickness = 2,
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

        private FrameworkElement CompactResult(ChatMessage m)
        {
            var tool = CopilotCatalog.Find(m.ToolId);
            var r = m.Result;
            var outer = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1), Background = Brushes.White };
            var sp = new StackPanel();

            var head = new Border { Background = CopilotColors.From("#fafafa"), BorderBrush = CopilotColors.From("#f1f3f5"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(12, 12, 0, 0) };
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (tool != null)
            {
                var tile = new IconTile { Glyph = tool.Icon, TileBg = tool.TileBg, TileFg = tool.TileFg, TileSize = 22, GlyphSize = 11, Corner = 5, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(tile, 0); hg.Children.Add(tile);
            }
            var title = new TextBlock { Text = tool?.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); hg.Children.Add(title);
            var done = new Border { CornerRadius = new CornerRadius(999), Background = CopilotColors.From("#dcfce7"), Padding = new Thickness(8, 2, 8, 2) };
            var dsp = new StackPanel { Orientation = Orientation.Horizontal };
            dsp.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = CopilotColors.From("#16a34a"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            dsp.Children.Add(new TextBlock { Text = "Done", FontSize = 11, Foreground = CopilotColors.From("#16a34a") });
            done.Child = dsp;
            Grid.SetColumn(done, 2); hg.Children.Add(done);
            head.Child = hg;
            sp.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(12) };
            body.Children.Add(CompactBody(r));
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            chips.Children.Add(ResultChip("bookmark", "Save", () => Vm.PinCommand.Execute(m.ToolId)));
            chips.Children.Add(ResultChip("copy", "Copy", null));
            chips.Children.Add(ResultChip("undo", "Undo", null));
            body.Children.Add(chips);
            sp.Children.Add(body);

            outer.Child = sp;
            return outer;
        }

        private FrameworkElement CompactBody(ResultModel r)
        {
            if (r == null) return new TextBlock();
            if (r.Kind == CpResultKind.Count)
            {
                var sp = new StackPanel();
                var num = new TextBlock();
                num.Inlines.Add(new System.Windows.Documents.Run(r.Headline) { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#0b0d12") });
                num.Inlines.Add(new System.Windows.Documents.Run(" " + r.Unit) { FontSize = 12.5, Foreground = CopilotColors.From("#6b7280") });
                sp.Children.Add(num);
                sp.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 0, 0, 8) });
                int total = r.Bars.Sum(b => b.Value);
                foreach (var b in r.Bars)
                {
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var dot = new Ellipse { Width = 6, Height = 6, Fill = CopilotColors.From(b.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    Grid.SetColumn(dot, 0);
                    var lbl = new TextBlock { Text = b.Label, FontSize = 11.5, Foreground = CopilotColors.From("#374151"), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(lbl, 1);
                    var val = new TextBlock { Text = b.Value.ToString(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), VerticalAlignment = VerticalAlignment.Center };
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
                hd.Children.Add(new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#b91c1c"), StrokeThickness = 1.6, Data = CopilotIcons.Get("warning"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
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
                var ext = new Border { Width = 32, Height = 40, CornerRadius = new CornerRadius(5), Background = Brushes.White, BorderBrush = CopilotColors.From("#bbf7d0"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
                ext.Child = new TextBlock { Text = "xlsx", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#15803d"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(ext);
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                col.Children.Add(new TextBlock { Text = r.Headline, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12") });
                col.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11, Foreground = CopilotColors.From("#6b7280") });
                g.Children.Add(col);
                return g;
            }
            // plain / list (compact)
            var plain = new StackPanel();
            plain.Children.Add(new TextBlock { Text = r.Headline, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#0b0d12"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(r.Sub))
                plain.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            return plain;
        }

        private Button SmallGhost(string text)
        {
            var b = new Button { Content = text, Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11, Foreground = CopilotColors.From("#6b7280"), Padding = new Thickness(9, 5, 9, 5) };
            b.Template = SmallGhostTemplate();
            return b;
        }

        private Button ResultChip(string glyph, string text, System.Action onClick)
        {
            var b = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 4, 8, 4) };
            b.Template = SmallGhostTemplate();
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Path { Width = 11, Height = 11, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#6b7280"), StrokeThickness = 1.6, Data = CopilotIcons.Get(glyph), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = CopilotColors.From("#6b7280") });
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
                Background = CopilotColors.From("#fafafa"),
                BorderBrush = CopilotColors.From("#eef0f3"),
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
                Foreground = CopilotColors.From("#9ca3af"),
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
                    Foreground = CopilotColors.From("#16a34a"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = Humanize(t),
                    FontSize = 11.5,
                    Foreground = CopilotColors.From("#374151"),
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
                Background = CopilotColors.From("#fafafa"),
                BorderBrush = CopilotColors.From("#eef0f3"),
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
                Foreground = CopilotColors.From("#9ca3af"),
                Margin = new Thickness(0, 0, 0, 5),
            });
            foreach (var s in steps)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1.5, 0, 1.5) };

                // Glyph swatch colours follow state: green check (done),
                // grey arrow (running/incomplete), red cross (error).
                string dotBg = s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Done ? "#dcfce7"
                             : s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Error ? "#fee2e2" : "#eef0f3";
                string glyphFg = s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Done ? "#16a34a"
                               : s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Error ? "#dc2626" : "#9ca3af";

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
                    Foreground = CopilotColors.From("#374151"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                sp.Children.Add(row);
            }
            outer.Child = sp;
            return outer;
        }

        // ─── Copy-to-clipboard affordances ─────────────────────────────────
        // WPF TextBlocks are not selectable, so chat text could be pasted INTO
        // the input but never copied OUT. Every bubble now gets a right-click
        // "Copy message" menu + a hover ⧉ button; user bubbles are additionally
        // text-selectable (read-only TextBox).

        private static void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Revit add-ins share the clipboard with the host; SetDataObject can
            // throw CLIPBRD_E_CANT_OPEN when another app holds it — retry once.
            try { Clipboard.SetDataObject(text, true); }
            catch { try { Clipboard.SetDataObject(text, false); } catch { /* clipboard busy */ } }
        }

        private static void AttachCopyMenu(FrameworkElement el, string text)
        {
            if (el == null || string.IsNullOrEmpty(text)) return;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "Copy message" };
            item.Click += (_, __) => CopyText(text);
            menu.Items.Add(item);
            el.ContextMenu = menu;
        }

        // Small ⧉ button that copies the message and flashes ✓ as feedback.
        private static Button CopyButton(string text)
        {
            var label = new TextBlock { Text = "⧉", FontSize = 12, Foreground = CopilotColors.From("#9ca3af") };
            var btn = new Button
            {
                Content = label, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Copy",
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 2, 0, 0), IsTabStop = false,
            };
            btn.Click += (_, __) =>
            {
                CopyText(text);
                label.Text = "✓"; label.Foreground = CopilotColors.From("#16a34a");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.2) };
                t.Tick += (s, e2) =>
                {
                    label.Text = "⧉"; label.Foreground = CopilotColors.From("#9ca3af");
                    ((System.Windows.Threading.DispatcherTimer)s).Stop();
                };
                t.Start();
            };
            return btn;
        }

        /// <summary>Decode a base64 PNG (a pasted screenshot) into a frozen
        /// BitmapImage for the chat thumbnail. Null on bad input.</summary>
        private static System.Windows.Media.Imaging.BitmapImage ImageFromBase64(string b64)
        {
            try
            {
                var bytes = System.Convert.FromBase64String(b64);
                var img = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    img.BeginInit();
                    img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                }
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        // Keeps the chat quiet: the affordance only appears while the pointer is
        // over its message row.
        private static FrameworkElement HoverReveal(FrameworkElement row, FrameworkElement affordance)
        {
            affordance.Visibility = Visibility.Hidden;
            row.MouseEnter += (_, __) => affordance.Visibility = Visibility.Visible;
            row.MouseLeave += (_, __) => affordance.Visibility = Visibility.Hidden;
            return affordance;
        }

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

        private static FrameworkElement BotAvatar(double size = 22)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top };
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2563eb"), 0));
            g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#7c3aed"), 1));
            b.Background = g;
            b.Child = new Path { Width = size * 0.55, Height = size * 0.55, Stretch = Stretch.Uniform, Fill = Brushes.White, Data = CopilotIcons.Get("sparkleSolid"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        // ─── Empty state ─────────────────────────────────────────────────────
        private FrameworkElement EmptyState()
        {
            var root = new StackPanel { Margin = new Thickness(16, 20, 16, 16) };

            // Greeting
            var greet = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
            greet.Children.Add(BotAvatar(32));
            var gcol = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            gcol.Children.Add(new TextBlock { Text = $"Hi {Vm.UserFirstName} 👋", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12") });
            gcol.Children.Add(new TextBlock { Text = "I can run Revit commands for you. Describe what you need, or pick from the suggestions below.", FontSize = 13.5, Foreground = CopilotColors.From("#374151"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 340 });
            greet.Children.Add(gcol);
            root.Children.Add(greet);

            // Suggested prompts
            root.Children.Add(Label("TRY ONE OF THESE"));
            foreach (var p in Prompts)
                root.Children.Add(PromptCard(p));

            // Topic chips
            root.Children.Add(Label("NOT SURE? TYPE A TOPIC — I'LL ASK"));
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
            foreach (var t in Topics)
            {
                var chip = new Button { Content = t, Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11.5, Foreground = CopilotColors.From("#374151"), Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(10, 4, 10, 4) };
                chip.Template = PillTemplate();
                var topic = t;
                chip.Click += (_, __) => Vm.ChatSendCommand.Execute(topic);
                chips.Children.Add(chip);
            }
            root.Children.Add(chips);

            // Library CTA
            root.Children.Add(LibraryCta());

            // How runs work
            root.Children.Add(HowRuns());
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
        private static ControlTemplate _outline, _pill, _dashed;

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
            b.SetValue(Border.BackgroundProperty, Brushes.White);
            b.SetValue(Border.BorderBrushProperty, CopilotColors.From("#e5e7eb"));
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
            b.SetValue(Border.BorderBrushProperty, CopilotColors.From("#e5e7eb"));
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
            b.SetValue(Border.BorderBrushProperty, CopilotColors.From("#e5e7eb"));
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
