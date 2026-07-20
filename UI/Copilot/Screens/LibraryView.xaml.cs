using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Library tab — the curated prompt library fetched from the
    /// backend (GET /agents/revit-ai/library) and cached for the session in
    /// <see cref="CopilotPromptLibrary"/>. Tapping a row drops its prompt
    /// (a universal template with [placeholders] the drafter edits) into the
    /// Chat composer (switch + focus, no auto-send) — the same rule the
    /// empty-state suggestions follow. Rows are built in code-behind,
    /// mirroring HistoryView's RowsHost construction.</summary>
    public partial class LibraryView : UserControl
    {
        /// <summary>Raised with the chosen prompt text when a row is tapped. The
        /// panel handles switching to Chat + inserting into the composer (which
        /// owns the "don't overwrite existing user text" rule).</summary>
        public event Action<string> PromptChosen;

        public LibraryView()
        {
            InitializeComponent();
            Loaded += async (_, __) => await BuildAsync();
        }

        private bool _loading;

        private async System.Threading.Tasks.Task BuildAsync()
        {
            if (ListHost == null || _loading) return;

            var sections = CopilotPromptLibrary.Cached;
            if (sections == null)
            {
                _loading = true;
                ListHost.Children.Clear();
                ListHost.Children.Add(Note("Loading prompt library…"));
                try
                {
                    var fetched = await new Services.AIService()
                        .GetPromptLibraryAsync(BinaConfig.Load().AccessToken);
                    if (fetched != null && fetched.Count > 0)
                        CopilotPromptLibrary.Cached = sections = fetched;
                }
                finally { _loading = false; }
            }

            ListHost.Children.Clear();
            if (sections == null)
            {
                // Backend unreachable or not signed in. Re-fetch on next tab
                // visit (Loaded fires again) — no local fallback by design.
                ListHost.Children.Add(Note(
                    "Couldn't load the prompt library. Check your connection and sign-in, then reopen this tab."));
                return;
            }

            // Top hint.
            var hint = new TextBlock
            {
                Text = "Tap a prompt to start a new chat with it.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(6, 6, 6, 10),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            ListHost.Children.Add(hint);

            foreach (var section in sections)
            {
                ListHost.Children.Add(SectionHeader(section.Section));

                var rows = new StackPanel();
                for (int i = 0; i < section.Items.Count; i++)
                {
                    if (i > 0) rows.Children.Add(Hairline());
                    rows.Children.Add(PromptRow(section.Items[i]));
                }
                ListHost.Children.Add(rows);
            }
        }

        // Muted status line (loading / failure states).
        private static FrameworkElement Note(string text)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(6, 10, 6, 10),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            return tb;
        }

        // Uppercase muted section header (letter-spacing isn't available on a
        // plain TextBlock, so bold + caps + Cp.Faint carry the emphasis).
        private static FrameworkElement SectionHeader(string text)
        {
            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(6, 10, 6, 6),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            return tb;
        }

        // Hairline between rows within a section.
        private static FrameworkElement Hairline()
        {
            var b = new Border { Height = 1, Margin = new Thickness(6, 0, 6, 0) };
            b.SetResourceReference(Border.BackgroundProperty, "Cp.LineSoft");
            return b;
        }

        // icon · (bold title + muted desc) · chevron — full-width row with the
        // same hover wash as History/empty-state suggestions.
        private FrameworkElement PromptRow(LibraryPrompt item)
        {
            var btn = new Button
            {
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 11, 6, 11),
                ToolTip = item.Prompt,
            };
            btn.Template = RowTemplate();

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon in a 30×30 transparent tile (stroked, matches the design).
            var tile = new Border { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
            var icon = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform,
                Fill = Brushes.Transparent, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Data = ToolCatalog.Icon(item.IconKey),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(Shape.StrokeProperty, "Cp.Faint");
            tile.Child = icon;
            Grid.SetColumn(tile, 0);
            g.Children.Add(tile);

            var col = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                Text = item.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            col.Children.Add(title);
            var desc = new TextBlock
            {
                Text = item.Desc, FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            col.Children.Add(desc);
            Grid.SetColumn(col, 1);
            g.Children.Add(col);

            var chev = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform,
                Fill = Brushes.Transparent, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M9,6 l6,6 -6,6"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chev.SetResourceReference(Shape.StrokeProperty, "Cp.Faint");
            Grid.SetColumn(chev, 2);
            g.Children.Add(chev);

            btn.Content = g;
            var prompt = item.Prompt;
            btn.Click += (_, __) => PromptChosen?.Invoke(prompt);
            return btn;
        }

        // Borderless full-width row, subtle hover wash. Cached statically so the
        // hover MUST bind Cp.Hover as a live DynamicResource (not a captured
        // brush) — else the hover freezes to first-render's theme.
        private static ControlTemplate _rowTemplate;
        private static ControlTemplate RowTemplate()
        {
            if (_rowTemplate != null) return _rowTemplate;
            var b = new FrameworkElementFactory(typeof(Border));
            b.Name = "bd";
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            b.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = b };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new System.Windows.DynamicResourceExtension("Cp.Hover"), "bd"));
            t.Triggers.Add(hover);
            _rowTemplate = t;
            return t;
        }
    }
}
