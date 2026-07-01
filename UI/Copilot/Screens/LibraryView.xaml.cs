using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Library tab — search, category chips, Recent, Vetted + AI sections, Ask-Copilot fallback.</summary>
    public partial class LibraryView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public LibraryView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => RebuildAll();
            System.Action onTheme = () => RebuildAll();   // recolor on light/dark swap
            Loaded += (_, __) => CopilotTheme.ThemeChanged += onTheme;
            Unloaded += (_, __) => CopilotTheme.ThemeChanged -= onTheme;
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            RebuildAll();
        }

        private void OnVm(object s, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(CopilotViewModel.Category):
                case nameof(CopilotViewModel.Query):
                case nameof(CopilotViewModel.VettedFiltered):
                    RebuildAll();
                    break;
            }
        }

        private void RebuildAll()
        {
            if (Vm == null || ChipsHost == null) return;
            BuildChips();
            BuildList();
        }

        private void BuildChips()
        {
            ChipsHost.Children.Clear();
            foreach (var c in Vm.Categories)
            {
                var chip = new ToggleButton
                {
                    Style = (Style)TryFindResource("Cp.Chip"),
                    Margin = new Thickness(0, 0, 6, 0),
                    IsChecked = Vm.Category == c.Id,
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = c.Label });
                sp.Children.Add(new TextBlock { Text = c.Count.ToString(), Opacity = 0.55, FontSize = 10.5, Margin = new Thickness(4, 0, 0, 0) });
                chip.Content = sp;
                var id = c.Id;
                chip.Click += (_, __) => Vm.SetCategoryCommand.Execute(id);
                ChipsHost.Children.Add(chip);
            }
        }

        private void BuildList()
        {
            ListHost.Children.Clear();
            var vm = Vm;
            bool noQuery = string.IsNullOrEmpty(vm.Query);

            // Recent
            if (noQuery && vm.Category == "all" && vm.RecentEntry != null)
            {
                ListHost.Children.Add(SectionHeader("Recent", null, null));
                // RecentEntry is now a session (no single ToolId); show a summary row if present
                if (vm.RecentEntry != null)
                {
                    var firstToolId = vm.RecentEntry.History?.SelectMany(m => m.Tools ?? new System.Collections.Generic.List<string>()).FirstOrDefault();
                    var recentTool = firstToolId != null ? CopilotCatalog.Find(firstToolId) : null;
                    if (recentTool != null) ListHost.Children.Add(RecentRow(vm.RecentEntry, recentTool));
                }
            }

            var vetted = vm.VettedFiltered.ToList();
            var ai = vm.AiFiltered.ToList();

            if (vetted.Count > 0)
            {
                ListHost.Children.Add(SectionHeader("Vetted tools", "· one click, no review", 1));
                foreach (var t in vetted) ListHost.Children.Add(MakeCard(t));
            }
            if (ai.Count > 0)
            {
                ListHost.Children.Add(SectionHeader("AI commands", "· generates code, you Review & Run", 2));
                foreach (var t in ai) ListHost.Children.Add(MakeCard(t));
            }

            if (!noQuery)
                ListHost.Children.Add(AskCopilotFallback(vm.Query));
            else if (vetted.Count == 0 && ai.Count == 0)
                ListHost.Children.Add(new TextBlock { Text = "No tools in this category yet.", Foreground = CopilotColors.From("#6b7280"), FontSize = 13, Margin = new Thickness(0, 32, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        }

        private ToolCard MakeCard(ToolDef t)
        {
            return new ToolCard { Tool = t, IsPinned = Vm.IsPinned(t.Id), Command = Vm.OpenToolCommand };
        }

        private FrameworkElement SectionHeader(string text, string sub, int? tier)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 14, 0, 8) };
            if (tier.HasValue)
                sp.Children.Add(new TierBadge { Tier = tier.Value, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new System.Windows.Documents.Run(text));
            if (!string.IsNullOrEmpty(sub))
                tb.Inlines.Add(new System.Windows.Documents.Run(" " + sub) { Foreground = CopilotColors.From("#9ca3af"), FontWeight = FontWeights.Medium });
            sp.Children.Add(tb);
            return sp;
        }

        private FrameworkElement RecentRow(HistoryEntry entry, ToolDef tool)
        {
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 10, 12, 10), BorderBrush = CopilotColors.From("#bbf7d0"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = CardTemplate();
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#f0fdf4"), 0));
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#eff6ff"), 1));
            btn.Background = bg;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = new IconTile { Glyph = tool.Icon, TileBg = "#ffffff", TileFg = tool.TileFg, TileSize = 28, GlyphSize = 14, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tile, 0);
            var col = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock { Text = "Re-run · " + tool.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#065f46") });
            col.Children.Add(new TextBlock { Text = entry.Time + " · " + entry.Summary, FontSize = 11, Foreground = CopilotColors.From("#047857") });
            Grid.SetColumn(col, 1);
            var play = new Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, Fill = CopilotColors.From("#047857"), Data = CopilotIcons.Get("play"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(play, 2);
            g.Children.Add(tile); g.Children.Add(col); g.Children.Add(play);
            btn.Content = g;
            btn.Click += (_, __) => Vm.OpenToolCommand.Execute(tool.Id);
            return btn;
        }

        private FrameworkElement AskCopilotFallback(string query)
        {
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12), BorderBrush = CopilotColors.From("#ddd6fe"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = CardTemplate();
            var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#f5f3ff"), 0));
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#eff6ff"), 1));
            btn.Background = bg;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = CopilotColors.From("#ffffff"), VerticalAlignment = VerticalAlignment.Center };
            tile.Child = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Fill = CopilotColors.From("#7c3aed"), Data = CopilotIcons.Get("sparkleSolid"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tile, 0);
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var line = new TextBlock { FontSize = 12.5 };
            line.Inlines.Add(new System.Windows.Documents.Run("Ask Copilot: ") { FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12") });
            line.Inlines.Add(new System.Windows.Documents.Run($"\"{query}\"") { FontStyle = FontStyles.Italic, Foreground = CopilotColors.From("#374151") });
            col.Children.Add(line);
            col.Children.Add(new TextBlock { Text = "Start a chat and generate a new command", FontSize = 11, Foreground = CopilotColors.From("#6b7280") });
            Grid.SetColumn(col, 1);
            g.Children.Add(tile); g.Children.Add(col);
            btn.Content = g;
            btn.Click += (_, __) => Vm.ChatSendCommand.Execute(query);
            return btn;
        }

        private static ControlTemplate _cardTemplate;
        private static ControlTemplate CardTemplate()
        {
            if (_cardTemplate != null) return _cardTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 10, 12, 10));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(cp);
            _cardTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            return _cardTemplate;
        }
    }
}
