using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.CostDashboard
{
    /// <summary>
    /// Overview tab: estimated-cost hero, stats strip, breakdown by discipline,
    /// disclaimer and action bar. All display text comes from
    /// <see cref="MockDashboardData"/>; discipline rows are generated in code
    /// so brushes resolve from the token dictionary by key. No Revit dependency.
    /// </summary>
    public partial class OverviewTabView : UserControl
    {
        public MockDashboardModel Model { get; }

        public OverviewTabView()
        {
            InitializeComponent();
            Model = MockDashboardData.Create();
            Apply();
        }

        private void Apply()
        {
            // Card 1 — estimated cost
            ConfidenceText.Text = Model.ConfidencePill;
            CurrencyText.Text = Model.Currency;
            CostText.Text = Model.EstimatedCost;
            ProjectionText.Text = Model.Projection;
            Gauge.Value = Model.GaugePercent;
            Gauge.Label = Model.GaugeLabel;

            // Card 2 — stats strip
            QuantifiedItemsText.Text = Model.QuantifiedItems;
            QuantifiedItemsCaption.Text = Model.QuantifiedItemsLabel;
            LevelsText.Text = Model.Levels;
            LevelsCaption.Text = Model.LevelsLabel;
            AwaitingRateText.Text = Model.AwaitingRate;
            AwaitingRateCaption.Text = Model.AwaitingRateLabel;

            // Card 3 — breakdown by discipline
            BreakdownHeaderText.Text = Model.BreakdownHeader;
            BreakdownHintText.Text = Model.BreakdownHint;
            DisciplineRows.Children.Clear();
            for (int i = 0; i < Model.Disciplines.Count; i++)
            {
                if (i > 0)
                {
                    DisciplineRows.Children.Add(new Rectangle
                    {
                        Height = 1,
                        Fill = Brush("Cd.Divider"),
                        Margin = new Thickness(20, 0, 20, 0),
                        SnapsToDevicePixels = true,
                    });
                }
                DisciplineRows.Children.Add(BuildDisciplineRow(Model.Disciplines[i]));
            }

            // Footer
            DisclaimerText.Text = Model.Disclaimer;
            SyncLabelText.Text = Model.SyncLabel;
            SyncSubtextText.Text = Model.SyncSubtext;
            DataLabelText.Text = Model.DataLabel;
            StatusText.Text = Model.Status;
            SyncTimeText.Text = Model.LastSyncTime;
        }

        /// <summary>
        /// One row:  [tile] [name / items line] [cost / cost % ]
        ///                  [────────── progress bar ──────────]
        /// </summary>
        private FrameworkElement BuildDisciplineRow(DisciplineBreakdown d)
        {
            var accent = Brush(d.BrushKey);
            var soft = Brush(d.SoftBrushKey);

            var grid = new Grid { Margin = new Thickness(20, 14, 20, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Icon tile
            var tile = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = (CornerRadius)FindResource("Cd.Radius.Tile"),
                Background = soft,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 0),
                Child = new Path
                {
                    Data = Geometry.Parse(GlyphFor(d.Name)),
                    Fill = (Brush)FindResource("Cd.TextOnAccent"),
                    Stretch = Stretch.Uniform,
                    Width = 18,
                    Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(tile, 0);
            Grid.SetRowSpan(tile, 2);
            grid.Children.Add(tile);

            // Name + items line
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Text(d.Name, "Cd.Subhead"));
            var items = Text(d.ItemsLine, "Cd.Body");
            items.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(items);
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            // Cost + cost-percent (right aligned)
            var right = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0),
            };
            var cost = Text(d.Cost, "Cd.Subhead");
            cost.TextAlignment = TextAlignment.Right;
            right.Children.Add(cost);
            var pct = Text(d.CostPercentLine, "Cd.Body");
            pct.TextAlignment = TextAlignment.Right;
            pct.Margin = new Thickness(0, 2, 0, 0);
            right.Children.Add(pct);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            // Progress bar spanning the two left columns
            var bar = new ProgressBar
            {
                Style = (Style)FindResource("Cd.DisciplineBar"),
                Value = d.PricedPercent,
                Foreground = accent,
                Margin = new Thickness(0, 10, 0, 0),
            };
            Grid.SetRow(bar, 1);
            Grid.SetColumn(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            grid.Children.Add(bar);

            return grid;
        }

        private TextBlock Text(string text, string styleKey) => new TextBlock
        {
            Text = text,
            Style = (Style)FindResource(styleKey),
        };

        private Brush Brush(string key) =>
            TryFindResource(key) as Brush ?? (Brush)FindResource("Cd.TextMuted");

        /// <summary>Simple 24x24 filled glyphs; one per discipline, keyed on Name (EvenOdd fill cuts windows out of the facade).</summary>
        private static string GlyphFor(string name)
        {
            switch (name)
            {
                case "Structure":
                    // I-beam / frame
                    return "M3,3 H21 V6 H14 V18 H21 V21 H3 V18 H10 V6 H3 Z";
                case "Mechanical":
                    // Fan: hub + 4 blades
                    return "M12,10 A2,2 0 1 0 12,14 A2,2 0 1 0 12,10 Z " +
                           "M12,2 C15,2 16,5 14,7 L12,9 L10,7 C8,5 9,2 12,2 Z " +
                           "M22,12 C22,15 19,16 17,14 L15,12 L17,10 C19,8 22,9 22,12 Z " +
                           "M12,22 C9,22 8,19 10,17 L12,15 L14,17 C16,19 15,22 12,22 Z " +
                           "M2,12 C2,9 5,8 7,10 L9,12 L7,14 C5,16 2,15 2,12 Z";
                case "Plumbing & sanitary":
                    // Droplet
                    return "M12,2 C12,2 5,10 5,15 A7,7 0 0 0 19,15 C19,10 12,2 12,2 Z";
                case "Electrical":
                    // Lightning bolt
                    return "M13,2 L4,14 H11 L10,22 L20,9 H13 Z";
                default:
                    // Architecture: building facade with windows
                    return "M4,22 V4 H20 V22 Z M7,7 H10 V10 H7 Z M14,7 H17 V10 H14 Z " +
                           "M7,12 H10 V15 H7 Z M14,12 H17 V15 H14 Z M10,17 H14 V22 H10 Z";
            }
        }
    }
}
