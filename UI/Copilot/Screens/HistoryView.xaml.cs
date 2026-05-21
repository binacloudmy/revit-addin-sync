using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>History tab — scrollable list of past runs; click re-opens the tool.</summary>
    public partial class HistoryView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public HistoryView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.History.CollectionChanged -= OnChanged;
            _hooked = Vm;
            if (_hooked != null) _hooked.History.CollectionChanged += OnChanged;
            Rebuild();
        }

        private void OnChanged(object s, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (Vm == null || RowsHost == null) return;
            Sub.Text = $"{Vm.History.Count} runs in this session";
            RowsHost.Children.Clear();
            foreach (var h in Vm.History)
            {
                var tool = CopilotCatalog.Find(h.ToolId);
                if (tool != null) RowsHost.Children.Add(Row(h, tool));
            }
        }

        private FrameworkElement Row(HistoryEntry h, ToolDef tool)
        {
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14, 10, 14, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = RowTemplate();

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string statusColor = h.Status == "ok" ? "#16a34a" : h.Status == "warn" ? "#d97706" : "#9ca3af";
            var dot = new Ellipse { Width = 6, Height = 6, Fill = CopilotColors.From(statusColor), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 10, 0) };
            Grid.SetColumn(dot, 0);
            var tile = new IconTile { Glyph = tool.Icon, TileBg = tool.TileBg, TileFg = tool.TileFg, TileSize = 22, GlyphSize = 11, Corner = 5, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(tile, 1);
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock { Text = tool.Title, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#0b0d12") });
            col.Children.Add(new TextBlock { Text = $"{h.Time} · {h.Summary}", FontSize = 11, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(col, 2);
            var chev = new Path { Width = 13, Height = 13, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("chevronRight"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 4, 0, 0) };
            Grid.SetColumn(chev, 3);

            g.Children.Add(dot); g.Children.Add(tile); g.Children.Add(col); g.Children.Add(chev);
            btn.Content = g;
            btn.Click += (_, __) => Vm.OpenToolCommand.Execute(tool.Id);
            return btn;
        }

        private static ControlTemplate _rowTemplate;
        private static ControlTemplate RowTemplate()
        {
            if (_rowTemplate != null) return _rowTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderBrushProperty, CopilotColors.From("#f1f3f5"));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            border.SetValue(Border.PaddingProperty, new Thickness(14, 10, 14, 10));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(cp);
            _rowTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            return _rowTemplate;
        }
    }
}
