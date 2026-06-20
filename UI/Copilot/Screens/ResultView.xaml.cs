using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Result screen — Done header, a result-body variant, next steps, follow-up bar.</summary>
    public partial class ResultView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        private static readonly FontFamily Mono = new FontFamily("Geist Mono, Cascadia Mono, Consolas, monospace");

        public ResultView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Rebuild();
        }

        private void OnVm(object s, PropertyChangedEventArgs e)
        {
            if ((e.PropertyName == nameof(CopilotViewModel.Screen) && Vm?.Screen == CpScreen.Result)
                || e.PropertyName == nameof(CopilotViewModel.RunResult))
                Rebuild();
        }

        private void Rebuild()
        {
            var vm = Vm; var tool = vm?.CurrentTool; var result = vm?.RunResult;
            if (tool == null || result == null || ResultHost == null) return;

            Tile.Glyph = tool.Icon; Tile.TileBg = tool.TileBg; Tile.TileFg = tool.TileFg;
            ToolTitle.Text = tool.Title;
            Badge.Tier = tool.Tier;

            ResultHost.Content = BuildBody(result);
            BuildFeedback();
            BuildNextSteps();
        }

        // ─── Feedback (👍/👎) ────────────────────────────────────────────────
        // The only signal that catches "compiled but wrong". One row under the
        // result; clicking a thumb fires the VM's fire-and-forget POST, highlights
        // the chosen thumb, and disables both so a rating is sent once.
        private void BuildFeedback()
        {
            if (FeedbackHost == null) return;
            FeedbackHost.Children.Clear();

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
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
            up = ThumbButton("thumbUp", () => SendFeedback("up", up, down));
            down = ThumbButton("thumbDown", () => SendFeedback("down", up, down));
            row.Children.Add(up);
            row.Children.Add(down);

            FeedbackHost.Children.Add(row);
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

        private void SendFeedback(string rating, Button up, Button down)
        {
            Vm?.SubmitFeedback(rating);

            // Light visual confirmation: tint the chosen thumb, disable both so the
            // rating is sent once. Green for 👍, red for 👎.
            var chosen = rating == "up" ? up : down;
            var chosenColor = CopilotColors.From(rating == "up" ? "#16a34a" : "#b91c1c");
            if (chosen?.Content is Path p) p.Stroke = chosenColor;
            if (up != null) up.IsEnabled = false;
            if (down != null) down.IsEnabled = false;
        }

        // ─── Next steps ──────────────────────────────────────────────────────
        private void BuildNextSteps()
        {
            NextHost.Children.Clear();
            NextHost.Children.Add(NextRow("bookmark", "#fef3c7", "#a16207", "Save as a re-runnable command",
                () => Vm?.PinCommand.Execute(Vm.ToolId)));
            NextHost.Children.Add(NextRow("history", "#f1f3f5", "#6b7280", "View history",
                () => Vm?.GoTab(CpTab.History)));
            NextHost.Children.Add(NextRow("undo", "#fee2e2", "#b91c1c", "Undo this action", Undo));
        }

        private void Undo()
        {
            // Reuse the shared handler's undo path (posts ID_REVIT_UNDO on the API thread).
            try
            {
                App.AIHandler.Action = "undo";
                App.AIHandler.OnCompleted = _ => { };
                App.AIExternalEvent.Raise();
            }
            catch { /* no-op if Revit context unavailable */ }
        }

        private FrameworkElement NextRow(string glyph, string bg, string fg, string title, System.Action onClick)
        {
            var btn = new Button { Style = (Style)TryFindResource("Cp.Card"), Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(10, 8, 10, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tile = new IconTile { Glyph = glyph, TileBg = bg, TileFg = fg, TileSize = 24, GlyphSize = 13, Corner = 6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tile, 0);
            var tb = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#0b0d12"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(tb, 1);
            var chev = new Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("chevronRight"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chev, 2);

            grid.Children.Add(tile); grid.Children.Add(tb); grid.Children.Add(chev);
            btn.Content = grid;
            btn.Click += (_, __) => onClick?.Invoke();
            return btn;
        }

        // ─── Result body variants ────────────────────────────────────────────
        private FrameworkElement BuildBody(ResultModel r)
        {
            switch (r.Kind)
            {
                case CpResultKind.Count: return BuildCount(r);
                case CpResultKind.Issues: return BuildIssues(r);
                case CpResultKind.List: return BuildList(r);
                case CpResultKind.File: return BuildFile(r);
                default: return BuildPlain(r);
            }
        }

        private FrameworkElement BuildCount(ResultModel r)
        {
            var root = new StackPanel();
            var card = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#bbf7d0"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12) };
            card.Background = Gradient("#f0fdf4", "#ecfeff");
            var cs = new StackPanel();
            cs.Children.Add(new TextBlock { Text = "TOTAL", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#15803d") });
            var num = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
            num.Inlines.Add(new System.Windows.Documents.Run(r.Headline) { FontSize = 32, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#0f172a") });
            num.Inlines.Add(new System.Windows.Documents.Run(" " + r.Unit) { FontSize = 14, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#6b7280") });
            cs.Children.Add(num);
            cs.Children.Add(new TextBlock { Text = r.Sub, FontSize = 12, Foreground = CopilotColors.From("#6b7280"), Margin = new Thickness(0, 2, 0, 0) });
            card.Child = cs;
            root.Children.Add(card);

            int total = r.Bars.Sum(b => b.Value);
            var group = new Border { CornerRadius = new CornerRadius(10), BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1) };
            var rows = new StackPanel();
            bool first = true;
            foreach (var b in r.Bars)
            {
                rows.Children.Add(BreakdownRow(b, total, first));
                first = false;
            }
            group.Child = rows;
            root.Children.Add(group);
            return root;
        }

        private FrameworkElement BreakdownRow(BarItem b, int total, bool first)
        {
            var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
            if (!first)
                grid.Margin = new Thickness(12, 10, 12, 10);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse { Width = 8, Height = 8, Fill = CopilotColors.From(b.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(dot, 0);
            var label = new TextBlock { Text = b.Label, FontSize = 12.5, Foreground = CopilotColors.From("#374151"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);

            var track = new Border { Width = 70, Height = 4, CornerRadius = new CornerRadius(999), Background = CopilotColors.From("#f1f3f5"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            double pct = total > 0 ? (double)b.Value / total : 0;
            var fill = new Border { Width = 70 * pct, Height = 4, CornerRadius = new CornerRadius(999), Background = CopilotColors.From(b.Color), HorizontalAlignment = HorizontalAlignment.Left };
            track.Child = fill;
            Grid.SetColumn(track, 2);

            var val = new TextBlock { Text = b.Value.ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), MinWidth = 22, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(val, 3);

            grid.Children.Add(dot); grid.Children.Add(label); grid.Children.Add(track); grid.Children.Add(val);

            if (!first)
            {
                var wrap = new Border { BorderBrush = CopilotColors.From("#f1f3f5"), BorderThickness = new Thickness(0, 1, 0, 0) };
                wrap.Child = grid;
                return wrap;
            }
            return grid;
        }

        private FrameworkElement BuildIssues(ResultModel r)
        {
            var outer = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#fecaca"), BorderThickness = new Thickness(1) };
            var sp = new StackPanel();

            var head = new Border { Background = CopilotColors.From("#fef2f2"), Padding = new Thickness(14, 12, 14, 12) };
            var hs = new StackPanel { Orientation = Orientation.Horizontal };
            hs.Children.Add(new Path { Width = 18, Height = 18, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#b91c1c"), StrokeThickness = 1.6, Data = CopilotIcons.Get("warning"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            var ht = new StackPanel();
            ht.Children.Add(new TextBlock { Text = r.Headline, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#7f1d1d") });
            ht.Children.Add(new TextBlock { Text = r.Unit, FontSize = 11.5, Foreground = CopilotColors.From("#991b1b") });
            hs.Children.Add(ht);
            head.Child = hs;
            sp.Children.Add(head);

            foreach (var it in r.Items)
            {
                var row = new Border { Background = Brushes.White, BorderBrush = CopilotColors.From("#fecaca"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 9, 14, 9) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var col = new StackPanel();
                col.Children.Add(new TextBlock { Text = it.Id, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12") });
                col.Children.Add(new TextBlock { Text = it.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#6b7280") });
                Grid.SetColumn(col, 0);
                var zoom = new Button { Content = "Zoom to", Style = (Style)TryFindResource("Cp.Ghost"), Padding = new Thickness(10, 4, 10, 4) };
                Grid.SetColumn(zoom, 1);
                g.Children.Add(col); g.Children.Add(zoom);
                row.Child = g;
                sp.Children.Add(row);
            }
            outer.Child = sp;
            return outer;
        }

        private FrameworkElement BuildList(ResultModel r)
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = r.Headline, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#0b0d12"), Margin = new Thickness(0, 0, 0, 10) });
            var group = new Border { CornerRadius = new CornerRadius(10), BorderBrush = CopilotColors.From("#e5e7eb"), BorderThickness = new Thickness(1) };
            var rows = new StackPanel();
            bool first = true;
            foreach (var d in r.Diffs)
            {
                var row = new Grid { Margin = new Thickness(12, 8, 12, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var from = new TextBlock { Text = d.From, FontSize = 12, FontFamily = Mono, Foreground = CopilotColors.From("#374151"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(from, 0);
                var arrow = new TextBlock { Text = "  →  ", FontSize = 12, Foreground = CopilotColors.From("#9ca3af"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(arrow, 1);
                var to = new TextBlock { Text = d.To, FontSize = 12, FontFamily = Mono, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(to, 2);
                row.Children.Add(from); row.Children.Add(arrow); row.Children.Add(to);

                FrameworkElement add = row;
                if (!first)
                {
                    var wrap = new Border { BorderBrush = CopilotColors.From("#f1f3f5"), BorderThickness = new Thickness(0, 1, 0, 0) };
                    wrap.Child = row; add = wrap;
                }
                rows.Children.Add(add);
                first = false;
            }
            group.Child = rows;
            root.Children.Add(group);
            return root;
        }

        private FrameworkElement BuildFile(ResultModel r)
        {
            string ext = r.Headline != null && r.Headline.EndsWith(".csv") ? "csv" : "xlsx";
            var card = new Border { CornerRadius = new CornerRadius(12), Background = CopilotColors.From("#f0fdf4"), BorderBrush = CopilotColors.From("#bbf7d0"), BorderThickness = new Thickness(1), Padding = new Thickness(14) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ft = new Border { Width = 42, Height = 52, CornerRadius = new CornerRadius(6), Background = Brushes.White, BorderBrush = CopilotColors.From("#bbf7d0"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            ft.Child = new TextBlock { Text = ext, FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#15803d"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(ft, 0);
            var col = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new TextBlock { Text = r.Headline, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), TextTrimming = TextTrimming.CharacterEllipsis });
            col.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11.5, Foreground = CopilotColors.From("#6b7280") });
            col.Children.Add(new TextBlock { Text = r.Path, FontSize = 10.5, FontFamily = Mono, Foreground = CopilotColors.From("#9ca3af"), Margin = new Thickness(0, 2, 0, 0) });

            // One-click access — the file is local (written on this machine), so
            // "download" = open it / reveal it. r.Path is the folder, r.Headline
            // the file name (the backend SetResult(kind="file") splits them).
            string fullPath = (!string.IsNullOrWhiteSpace(r.Path) && !string.IsNullOrWhiteSpace(r.Headline))
                ? System.IO.Path.Combine(r.Path, r.Headline)
                : (r.Path ?? r.Headline);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            // Labeled "Download" to match the Claude mental model — the file is
            // already local (Revit wrote it to disk), so this opens it directly.
            btnRow.Children.Add(MakeFileButton("Download", () => OpenLocalPath(fullPath)));
            btnRow.Children.Add(MakeFileButton("Show in folder", () => RevealInFolder(fullPath)));
            col.Children.Add(btnRow);

            Grid.SetColumn(col, 1);
            g.Children.Add(ft); g.Children.Add(col);
            card.Child = g;
            return card;
        }

        private static Button MakeFileButton(string text, System.Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(11, 5, 11, 5),
                FontSize = 11.5,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.White,
                BorderBrush = CopilotColors.From("#86efac"),
                BorderThickness = new Thickness(1),
                Foreground = CopilotColors.From("#15803d"),
            };
            b.Click += (s, e) => { try { onClick(); } catch { /* best-effort */ } };
            return b;
        }

        private static void OpenLocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }

        private static void RevealInFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
            {
                var dir = System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
            }
        }

        private FrameworkElement BuildPlain(ResultModel r)
        {
            var card = new Border { CornerRadius = new CornerRadius(12), BorderBrush = CopilotColors.From("#bbf7d0"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14) };
            card.Background = Gradient("#f0fdf4", "#ecfeff");
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = r.Headline, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#0f172a"), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(r.Sub))
                sp.Children.Add(new TextBlock { Text = r.Sub, FontSize = 12.5, Foreground = CopilotColors.From("#374151"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            card.Child = sp;
            return card;
        }

        private static LinearGradientBrush Gradient(string from, string to)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(from), 0));
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(to), 1));
            b.Freeze();
            return b;
        }
    }
}
