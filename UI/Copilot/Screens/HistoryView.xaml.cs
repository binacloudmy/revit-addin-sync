using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
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

        private void BackBtn_Click(object s, RoutedEventArgs e)
        {
            ListPanel.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;
            BackBtn.Visibility = Visibility.Collapsed;
            HeaderTitle.Text = "History";
            Rebuild();
        }

        private void Rebuild()
        {
            if (Vm == null || RowsHost == null) return;
            Sub.Text = $"{Vm.History.Count} session{(Vm.History.Count == 1 ? "" : "s")}";
            RowsHost.Children.Clear();
            foreach (var h in Vm.History)
                RowsHost.Children.Add(Row(h));
        }

        private void ShowDetail(HistoryEntry h)
        {
            if (MessagesHost == null) return;
            MessagesHost.Children.Clear();
            HeaderTitle.Text = RowTitle(h);
            ListPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            BackBtn.Visibility = Visibility.Visible;

            foreach (var msg in h.History)
            {
                MessagesHost.Children.Add(MessageBubble(msg));
                // Disable tools view
                // if (msg.Sender == "bot" && msg.Tools != null)
                // {
                //     foreach (var tid in msg.Tools)
                //     {
                //         var card = ToolReviewCard(tid);
                //         if (card != null) MessagesHost.Children.Add(card);
                //     }
                // }
            }
        }

        /// <summary>The text shown as a history row's title: the user-set label if
        /// any, otherwise the first user message of the session (not the auto
        /// "N messages" summary). Newlines from Shift+Enter are collapsed so the
        /// title stays on one line.</summary>
        private static string RowTitle(HistoryEntry h)
        {
            string raw = !string.IsNullOrWhiteSpace(h.Label)
                ? h.Label
                : FirstUserMessage(h) ?? h.Summary;
            string clean = CleanTitle(raw);
            return string.IsNullOrEmpty(clean) ? "Run" : clean;
        }

        private static string FirstUserMessage(HistoryEntry h)
        {
            var first = h.History?.FirstOrDefault(m => m.Sender == "user");
            return first?.Text;
        }

        /// <summary>Collapse any run of whitespace (including the \r\n that
        /// Shift+Enter inserts) into a single space, then trim.</summary>
        private static string CleanTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }

        /// <summary>Tooltip showing the full (untruncated) title, wrapped to a
        /// sane width so long first-messages stay readable.</summary>
        private static ToolTip MakeTitleTooltip(string text) => new ToolTip
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
        };

        private FrameworkElement Row(HistoryEntry h)
        {
            var btn = new Button
            {
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            btn.Template = RowTemplate();

            var editBox = new TextBox
            {
                Text = RowTitle(h),
                FontSize = 12.5,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                BorderBrush = CopilotColors.From("#6d28d9"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
            };

            string rowTitle = RowTitle(h);
            var titleBlock = new TextBlock
            {
                // History session label — the first user message (or a user-set
                // label), forced to a single line with an ellipsis so it trims to
                // the row width; full text is shown on hover via the tooltip.
                Text = rowTitle,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.From("#0b0d12"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                ToolTip = MakeTitleTooltip(rowTitle),
            };

            Action BeginRename = () =>
            {
                editBox.Text = RowTitle(h);
                titleBlock.Visibility = Visibility.Collapsed;
                editBox.Visibility = Visibility.Visible;
                editBox.Focus();
                editBox.SelectAll();
            };
            Action CommitRename = null;
            CommitRename = () =>
            {
                Vm.RenameHistoryEntry(h, editBox.Text);
                string t = RowTitle(h);
                titleBlock.Text = t;
                titleBlock.ToolTip = MakeTitleTooltip(t);
                editBox.Visibility = Visibility.Collapsed;
                titleBlock.Visibility = Visibility.Visible;
            };
            Action CancelRename = () =>
            {
                editBox.Visibility = Visibility.Collapsed;
                titleBlock.Visibility = Visibility.Visible;
            };

            editBox.KeyDown += (_, e2) =>
            {
                if (e2.Key == Key.Return) CommitRename();
                else if (e2.Key == Key.Escape) CancelRename();
            };
            editBox.LostFocus += (_, __) => CommitRename();

            var g = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string statusColor = h.Status == "ok" ? "#16a34a" : h.Status == "warn" ? "#d97706" : "#9ca3af";
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                Fill = CopilotColors.From(statusColor),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 10, 0),
            };
            Grid.SetColumn(dot, 0);

            var tile = new IconTile
            {
                Glyph = "sparkles",
                TileBg = "#ede9fe", TileFg = "#6d28d9",
                TileSize = 22, GlyphSize = 11, Corner = 5,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(tile, 1);

            int msgCount = h.History?.Count ?? 0;
            var col = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(titleBlock);
            col.Children.Add(editBox);
            // Add history session block
            col.Children.Add(new TextBlock
            {
                Text = h.Time + (msgCount > 0 ? $" · {msgCount / 2} message{(msgCount / 2 == 1 ? "" : "s")}" : ""),
                FontSize = 11,
                Foreground = CopilotColors.From("#6b7280"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(col, 2);

            var dotsPath = new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform,
                Fill = CopilotColors.From("#9ca3af"),
                Data = Geometry.Parse(
                    "M12,5 m-1.5,0 a1.5,1.5,0,1,0,3,0 a1.5,1.5,0,1,0,-3,0 " +
                    "M12,12 m-1.5,0 a1.5,1.5,0,1,0,3,0 a1.5,1.5,0,1,0,-3,0 " +
                    "M12,19 m-1.5,0 a1.5,1.5,0,1,0,3,0 a1.5,1.5,0,1,0,-3,0"),
            };
            var dotsBtn = new Button
            {
                Content = dotsPath,
                Style = (Style)TryFindResource("Cp.IconButton"),
                VerticalAlignment = VerticalAlignment.Top,
            };

            var menu = new ContextMenu();
            var renameItem = new MenuItem { Header = "Rename" };
            var deleteItem = new MenuItem { Header = "Delete" };
            renameItem.Click += (_, __) => BeginRename();
            deleteItem.Click += (_, __) => Vm.DeleteHistoryEntry(h);
            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            dotsBtn.Click += (_, e2) =>
            {
                e2.Handled = true;
                menu.PlacementTarget = dotsBtn;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            Grid.SetColumn(dotsBtn, 3);

            var chev = new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform,
                Stroke = CopilotColors.From("#9ca3af"),
                StrokeThickness = 1.6,
                Data = CopilotIcons.Get("chevronRight"),
            };
            var chevBtn = new Button
            {
                Content = chev,
                Style = (Style)TryFindResource("Cp.IconButton"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            chevBtn.Click += (_, e2) => { e2.Handled = true; ShowDetail(h); };
            Grid.SetColumn(chevBtn, 4);

            g.Children.Add(dot);
            g.Children.Add(tile);
            g.Children.Add(col);
            g.Children.Add(dotsBtn);
            g.Children.Add(chevBtn);

            btn.Content = g;
            btn.Click += (_, e2) =>
            {
                if (editBox.Visibility == Visibility.Visible) return;
                ShowDetail(h);
            };
            return btn;
        }

        // Render history messages with the same bubbles as the live chat: user
        // messages as a plain bubble (with file chips), bot replies as markdown.
        private FrameworkElement MessageBubble(Model.History msg)
        {
            var wrap = new StackPanel { Margin = new Thickness(14, 0, 14, 0) };
            if (msg.Sender == "user")
                wrap.Children.Add(CopilotMessageBubble.User(
                    msg.Text, Vm?.UserFirstName, null,
                    msg.Files?.Select(f => (f.Name, f.Lines)), DetailMaxWidth()));
            else
                wrap.Children.Add(CopilotMessageBubble.Ai(msg.Text, DetailMaxWidth()));
            return wrap;
        }

        /// <summary>Message column width for the detail view — mirrors
        /// ChatView.BubbleMaxWidth so bubbles track the panel. Narrow default
        /// pre-layout.</summary>
        private double DetailMaxWidth()
        {
            double w = MessagesHost != null ? MessagesHost.ActualWidth : 0;
            if (w <= 0) return 360;
            return System.Math.Max(320, w * 0.85 - 44);
        }

        private FrameworkElement ToolReviewCard(string toolId)
        {
            var tool = CopilotCatalog.Find(toolId);
            if (tool == null) return null;

            var outer = new Border
            {
                Margin = new Thickness(12, 2, 12, 8),
                BorderBrush = CopilotColors.From("#ddd6fe"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Background = CopilotColors.From("#faf5ff"),
                Padding = new Thickness(10, 8, 10, 8),
            };

            var sp = new StackPanel();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform,
                Fill = CopilotColors.From("#7c3aed"),
                Data = CopilotIcons.Get("sparkleSolid"),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(new TextBlock
            {
                Text = tool.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#5b21b6"),
            });
            sp.Children.Add(header);

            int i = 1;
            foreach (var step in tool.Plan)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = $"{i++}. {step}",
                    FontSize = 11,
                    Foreground = CopilotColors.From("#374151"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }

            if (!string.IsNullOrWhiteSpace(tool.Code))
            {
                var toggle = new ToggleButton
                {
                    Content = "Show code",
                    FontSize = 11,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = CopilotColors.From("#7c3aed"),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                toggle.Template = CodeToggleTemplate();
                var codeBorder = new Border
                {
                    Visibility = Visibility.Collapsed,
                    Background = CopilotColors.From("#1e1b4b"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 4, 0, 0),
                };
                var codeBox = new TextBox
                {
                    Text = tool.Code,
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    FontSize = 10.5,
                    Foreground = CopilotColors.From("#c4b5fd"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                };
                codeBorder.Child = codeBox;
                toggle.Checked += (_, __) => { codeBorder.Visibility = Visibility.Visible; toggle.Content = "Hide code"; };
                toggle.Unchecked += (_, __) => { codeBorder.Visibility = Visibility.Collapsed; toggle.Content = "Show code"; };
                sp.Children.Add(toggle);
                sp.Children.Add(codeBorder);
            }

            outer.Child = sp;
            return outer;
        }

        private static ControlTemplate _rowTemplate;
        private static ControlTemplate RowTemplate()
        {
            if (_rowTemplate != null) return _rowTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.BorderBrushProperty, CopilotColors.From("#f1f3f5"));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(cp);
            _rowTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            // Hover darken — same surface-hover value as Cp.Card (#fafafa). The
            // row buttons (Cp.IconButton, #f3f4f6) darken further so they stand out.
            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, CopilotColors.From("#fafafa"), "bd"));
            _rowTemplate.Triggers.Add(hover);
            return _rowTemplate;
        }

        private static ControlTemplate _codeToggleTemplate;
        private static ControlTemplate CodeToggleTemplate()
        {
            if (_codeToggleTemplate != null) return _codeToggleTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(cp);
            _codeToggleTemplate = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
            return _codeToggleTemplate;
        }
    }
}
