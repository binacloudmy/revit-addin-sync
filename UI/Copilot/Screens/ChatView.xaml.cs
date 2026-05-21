using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                av.Child = new TextBlock { Text = "M", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#374151"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var bubble = new Border { Background = CopilotColors.From("#f1f3f5"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8) };
                bubble.Child = new TextBlock { Text = m.Text, FontSize = 13, Foreground = CopilotColors.From("#0b0d12"), TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
                row.Children.Add(av); row.Children.Add(bubble);
                return row;
            }

            // Minimal AI text line for now.
            var aiRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            aiRow.Children.Add(BotAvatar());
            aiRow.Children.Add(new TextBlock { Text = m.Text ?? "…", FontSize = 13, Foreground = CopilotColors.From("#374151"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return aiRow;
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
    }
}
