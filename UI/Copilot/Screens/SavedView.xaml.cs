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
    /// <summary>Saved tab — re-runnable saved commands (chat prompts and form params).</summary>
    public partial class SavedView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public SavedView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.SavedCommands.CollectionChanged -= OnChanged;
            _hooked = Vm;
            if (_hooked != null) _hooked.SavedCommands.CollectionChanged += OnChanged;
            Rebuild();
        }

        private void OnChanged(object s, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (Vm == null || Host == null) return;
            var saved = Vm.SavedCommands.ToList();
            Sub.Text = $"{saved.Count} saved commands";
            Host.Children.Clear();

            if (saved.Count == 0) { Host.Children.Add(EmptyState()); return; }
            foreach (var cmd in saved) Host.Children.Add(Card(cmd));
        }

        private FrameworkElement Card(SavedCommand cmd)
        {
            var tool = CopilotCatalog.Find(cmd.ToolId);
            var btn = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 6), BorderBrush = CopilotColors.From("#e5e7eb"), Background = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            btn.Template = OutlineCardTemplate();

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (tool != null)
            {
                var tile = new IconTile { Glyph = tool.Icon, TileBg = tool.TileBg, TileFg = tool.TileFg, TileSize = 28, GlyphSize = 14, Corner = 6, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(tile, 0); g.Children.Add(tile);
            }
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock { Text = cmd.Title ?? "(saved)", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), TextWrapping = TextWrapping.Wrap });
            string sub = (cmd.Source == "chat" ? "Chat · " : "Form · ") + (tool?.Title ?? cmd.ToolId ?? "command");
            col.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(col, 1); g.Children.Add(col);

            var del = new Button { Cursor = System.Windows.Input.Cursors.Hand, Width = 22, Height = 22, BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Remove saved command", VerticalAlignment = VerticalAlignment.Center };
            del.Content = new TextBlock { Text = "×", FontSize = 16, Foreground = CopilotColors.From("#9ca3af"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var id = cmd.Id;
            del.Click += (s, e) => { e.Handled = true; Vm.DeleteSavedCommand.Execute(id); };
            Grid.SetColumn(del, 2); g.Children.Add(del);

            btn.Content = g;
            btn.Click += (_, __) => Vm.RunSavedCommand.Execute(cmd);
            return btn;
        }

        private FrameworkElement EmptyState()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 32, 0, 0), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Center };
            var icon = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(14), Background = CopilotColors.From("#f1f3f5"), Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
            icon.Child = new Path { Width = 22, Height = 22, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("bookmark"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wrap.Children.Add(icon);
            wrap.Children.Add(new TextBlock { Text = "Nothing saved yet", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            wrap.Children.Add(new TextBlock { Text = "After running a command, hit Save to add it here. Click to re-run, × to remove.", FontSize = 12, Foreground = CopilotColors.From("#6b7280"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
            return wrap;
        }

        private static ControlTemplate _outline;
        private static ControlTemplate OutlineCardTemplate()
        {
            if (_outline != null) return _outline;
            var b = new System.Windows.FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 9));
            var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            _outline = new ControlTemplate(typeof(Button)) { VisualTree = b };
            return _outline;
        }
    }
}
