using System;
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
    /// <summary>History tab — past chat sessions (whole conversations); click reopens transcript.</summary>
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
            if (_hooked != null) _hooked.Sessions.CollectionChanged -= OnChanged;
            _hooked = Vm;
            if (_hooked != null) _hooked.Sessions.CollectionChanged += OnChanged;
            Rebuild();
        }

        private void OnChanged(object s, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (Vm == null || RowsHost == null) return;
            Sub.Text = $"{Vm.Sessions.Count} past conversation(s)";
            RowsHost.Children.Clear();
            if (Vm.Sessions.Count == 0)
            {
                RowsHost.Children.Add(EmptyState());
                return;
            }
            foreach (var s in Vm.Sessions) RowsHost.Children.Add(Row(s));
        }

        private FrameworkElement Row(ChatSession s)
        {
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(14, 10, 14, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = RowTemplate();

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tile = new IconTile { Glyph = "send", TileBg = "#e0e7ff", TileFg = "#4338ca", TileSize = 26, GlyphSize = 12, Corner = 6, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(tile, 0); g.Children.Add(tile);

            int msgCount = s.Messages?.Count(m => m.Role == "user") ?? 0;
            string when = TimeAgo(s.CreatedAt);
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock { Text = s.Title ?? "(untitled)", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), TextWrapping = TextWrapping.Wrap });
            col.Children.Add(new TextBlock { Text = $"{when} · {msgCount} message{(msgCount == 1 ? "" : "s")}", FontSize = 11, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(col, 1); g.Children.Add(col);

            var del = new Button { Cursor = System.Windows.Input.Cursors.Hand, Width = 22, Height = 22, BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Delete conversation", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 2, 6, 0) };
            del.Content = new TextBlock { Text = "×", FontSize = 16, Foreground = CopilotColors.From("#9ca3af"), HorizontalAlignment = HorizontalAlignment.Center };
            var sid = s.Id;
            del.Click += (sd, ed) => { ed.Handled = true; Vm.DeleteSessionCommand.Execute(sid); };
            Grid.SetColumn(del, 2); g.Children.Add(del);

            var chev = new Path { Width = 13, Height = 13, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("chevronRight"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetColumn(chev, 3); g.Children.Add(chev);

            btn.Content = g;
            btn.Click += (_, __) => Vm.OpenSessionCommand.Execute(s.Id);
            return btn;
        }

        private static string TimeAgo(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "earlier";
            if (!DateTime.TryParse(iso, out var t)) return iso;
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
            return t.ToString("MMM d");
        }

        private FrameworkElement EmptyState()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 32, 0, 0), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Center };
            wrap.Children.Add(new TextBlock { Text = "No conversations yet", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            wrap.Children.Add(new TextBlock { Text = "Past chat conversations show here. Click one to reopen the transcript.", FontSize = 12, Foreground = CopilotColors.From("#6b7280"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
            return wrap;
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
